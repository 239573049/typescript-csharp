// Ported from tsgo internal/ls/signaturehelp.go (call/new-expression foundation subset).
//
// Scope note (breadth-first wave): the argument-list location machinery
// (getContainingArgumentInfo → getImmediatelyContainingArgumentInfo →
// getArgumentOrParameterListInfo → getArgumentIndex/Count → applicable span) is ported
// faithfully for call and new expressions. The candidate/signature layer in Go runs
// through checker.GetResolvedSignatureForSignatureHelp, GetSymbolAtLocation, and the
// nodebuilder display-parts printers — all internal to Ts.Compiler (or throwing stubs),
// so candidates come from the invoked expression's declarations on the public
// emit-resolver surface, and labels render from declaration source text.
// Deferred (each marked at its branch below):
//   * contextual invocations (tryGetParameterInfo / getContextualSignatureLocationInfo),
//   * tagged-template and JSX argument lists,
//   * type-argument lists (getPossibleTypeArgumentsInfo) and generic display,
//   * spread argument expansion (getSpreadElementCount tuple introspection),
//   * resolved-signature selection and checker-printed signature labels.

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Ast; // instance-syntax resolution of the Ts.Ast.NodeAccessors extension methods

namespace Ts.Ls;

// lsproto.SignatureHelp, reduced to the raw (non-LSP) foundation payload.
public sealed record SignatureHelp(
    SignatureInformation[] Signatures,
    int ActiveSignature,
    int? ActiveParameter);

// signaturehelp.go: signatureInformation (label + parameters + variadic flag subset).
public sealed record SignatureInformation(
    string Label,
    ParameterInformation[] Parameters,
    bool IsVariadic);

// signaturehelp.go: signatureHelpParameter (label + isRest subset).
public sealed record ParameterInformation(string Label, bool IsRest);

public sealed partial class LanguageService
{
    // signaturehelp.go:57 GetSignatureHelpItems (foundation subset).
    public SignatureHelp? GetSignatureHelpItems(
        string fileName,
        int position,
        CancellationToken cancellationToken = default)
    {
        var file = GetProgramAndFile(fileName);
        if ((uint)position > (uint)file.End)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        var (checker, done) = host.GetTypeCheckerForFile(cancellationToken, file);
        try
        {
            // Decide whether to show signature help
            var startingToken = global::Ts.AstNav.Tokens.FindPrecedingToken(file, position);
            if (startingToken is null)
            {
                // We are at the beginning of the file
                return null;
            }

            // Go maps the LSP SignatureHelpContext onto a trigger-reason kind; the raw
            // foundation API carries no LSP context, so every request takes Go's default
            // branch: "invoked" (isManuallyInvoked=true, onlyUseSyntacticOwners=false).
            // The in-string/in-comment bail-out is gated on onlyUseSyntacticOwners and is
            // therefore inactive here, exactly as in Go's invoked path.
            var argumentInfo = GetContainingArgumentInfo(
                startingToken, file, checker, true /*isManuallyInvoked*/, position);
            if (argumentInfo is null)
            {
                return null;
            }

            // Go: getCandidateOrTypeInfo → checker.GetResolvedSignatureForSignatureHelp /
            // GetSymbolAtLocation → []*checker.Signature. GetResolvedSignature and
            // GetSignaturesOfType are internal to Ts.Compiler and GetSymbolAtLocation is a
            // throwing stub, so each function-like declaration of the invoked expression
            // stands in for one candidate signature (createJSSignatureHelpItems-style
            // named-declaration fallback, restricted to the actual referenced symbol).
            var (callTargetName, candidateDeclarations) =
                GetDeclarationSignatureCandidates(checker, argumentInfo);
            if (candidateDeclarations.Count == 0)
            {
                return null;
            }

            return CreateSignatureHelpItems(callTargetName, candidateDeclarations, argumentInfo, file);
        }
        finally
        {
            done();
        }
    }

    // ── invocation / argument-list model ─────────────────────────────────────────────

    // signaturehelp.go: invocation is a 3-arm union (callInvocation | typeArgsInvocation |
    // contextualInvocation). Only the callInvocation arm is constructible in this
    // foundation subset; the other arms belong to the deferred type-argument and
    // contextual paths.
    private sealed record Invocation(global::Ts.Ast.Node CallInvocationNode);

    // signaturehelp.go:895 argumentListInfo
    // ArgumentCount is the *apparent* number of arguments.
    private sealed record ArgumentListInfo(
        bool IsTypeParameterList,
        Invocation Invocation,
        global::Ts.Core.TextRange ArgumentsSpan,
        int ArgumentIndex,
        int ArgumentCount);

    // signaturehelp.go:1131 argumentOrParameterListInfo
    private sealed record ArgumentOrParameterListInfo(
        global::Ts.Ast.NodeList? List,
        int ArgumentIndex,
        int ArgumentCount,
        global::Ts.Core.TextRange ArgumentsSpan);

    // signaturehelp.go:1189 argumentOrParameterListAndIndex
    private sealed record ArgumentOrParameterListAndIndex(
        global::Ts.Ast.NodeList? List,
        int ArgumentIndex);

    // signaturehelp.go:843 getContainingArgumentInfo
    private static ArgumentListInfo? GetContainingArgumentInfo(
        global::Ts.Ast.Node node,
        global::Ts.Ast.SourceFile sourceFile,
        global::Ts.Checker.Checker checker,
        bool isManuallyInvoked,
        int position)
    {
        ArgumentListInfo? firstArgumentInfo = null;
        for (var n = node;
            !global::Ts.Ast.NodeTests.IsSourceFile(n) &&
                (isManuallyInvoked || !global::Ts.Ast.NodeTests.IsBlock(n));
            n = n.Parent!)
        {
            // If the node is not a subspan of its parent, this is a big problem.
            // There have been crashes that might be caused by this violation.
            global::Ts.Core.Debug.Assert(
                RangeContainsRange(n.Parent!.Loc, n.Loc),
                "Not a subspan. Child: ", n.Kind, ", parent: ", n.Parent.Kind);
            var argumentInfo =
                GetImmediatelyContainingArgumentOrContextualParameterInfo(n, position, sourceFile, checker);
            if (argumentInfo is not null)
            {
                // For contextual invocations (e.g., arrow functions with contextual types),
                // Go returns immediately without checking the position. That arm is not
                // constructible in this subset (tryGetParameterInfo is deferred), so the
                // check is omitted here.

                // Remember the first (innermost) argument info we find
                firstArgumentInfo ??= argumentInfo;

                // If the position is at the end boundary of an argument list, keep the
                // innermost call. This covers cases like foo(bar("x"|)) where the cursor is
                // still inside the inner invocation, just before its closing paren.
                if (argumentInfo.ArgumentsSpan.End == position)
                {
                    return argumentInfo;
                }

                // If any call's span contains the position, return it.
                // We walk from inner to outer, so this naturally prefers the innermost call
                // when multiple calls contain the position.
                if (argumentInfo.ArgumentsSpan.Contains(position))
                {
                    return argumentInfo;
                }
            }
        }

        // No call's span contains the position. Fall back to the innermost call we found.
        // This covers boundary positions that are still syntactically associated with that
        // invocation, such as being at the end of the argument list or on the close paren.
        return firstArgumentInfo;
    }

    // signaturehelp.go:886 getImmediatelyContainingArgumentOrContextualParameterInfo
    private static ArgumentListInfo? GetImmediatelyContainingArgumentOrContextualParameterInfo(
        global::Ts.Ast.Node node,
        int position,
        global::Ts.Ast.SourceFile sourceFile,
        global::Ts.Checker.Checker checker)
    {
        var result = TryGetParameterInfo(node, sourceFile, checker);
        if (result is null)
        {
            return GetImmediatelyContainingArgumentInfo(node, position, sourceFile, checker);
        }
        return result;
    }

    // signaturehelp.go:1243 tryGetParameterInfo + :1295 getContextualSignatureLocationInfo.
    // The contextual-invocation path (signature help inside a callback's parameter list)
    // requires checker.GetContextualTypeForObjectLiteralElement / GetContextualType /
    // GetSignaturesOfType — all internal to Ts.Compiler. Deferred; Go's nil result falls
    // through to getImmediatelyContainingArgumentInfo, which this port preserves.
    private static ArgumentListInfo? TryGetParameterInfo(
        global::Ts.Ast.Node startingToken,
        global::Ts.Ast.SourceFile sourceFile,
        global::Ts.Checker.Checker checker)
    {
        _ = startingToken;
        _ = sourceFile;
        _ = checker;
        return null;
    }

    // signaturehelp.go:905 getImmediatelyContainingArgumentInfo
    // Returns relevant information for the argument list and the current argument if we are
    // in the argument of an invocation; returns null otherwise.
    private static ArgumentListInfo? GetImmediatelyContainingArgumentInfo(
        global::Ts.Ast.Node node,
        int position,
        global::Ts.Ast.SourceFile sourceFile,
        global::Ts.Checker.Checker checker)
    {
        var parent = node.Parent!;
        if (global::Ts.Ast.AstUtilities.IsCallOrNewExpression(parent))
        {
            // There are 3 cases to handle:
            //   1. The token introduces a list, and should begin a signature help session
            //   2. The token is either not associated with a list, or ends a list, so the session should end
            //   3. The token is buried inside a list, and should give signature help
            //
            // The following are examples of each:
            //
            //    Case 1:
            //          foo<#T, U>(#a, b)    -> The token introduces a list, and should begin a signature help session
            //    Case 2:
            //          fo#o<T, U>#(a, b)#   -> The token is either not associated with a list, or ends a list, so the session should end
            //    Case 3:
            //          foo<T#, U#>(a#, #b#) -> The token is buried inside a list, and should give signature help
            // Find out if 'node' is an argument, a type argument, or neither
            var info = GetArgumentOrParameterListInfo(node, sourceFile, checker);
            if (info is null)
            {
                return null;
            }
            var isTypeParameterList = false;
            var parentTypeArgumentList = parent.TypeArgumentList();
            if (parentTypeArgumentList is not null && info.List is not null &&
                parentTypeArgumentList.Pos == info.List.Pos)
            {
                isTypeParameterList = true;
            }
            return new ArgumentListInfo(
                isTypeParameterList,
                new Invocation(parent),
                info.ArgumentsSpan,
                info.ArgumentIndex,
                info.ArgumentCount);
        }
        if (node.Kind == global::Ts.Ast.SyntaxKind.NoSubstitutionTemplateLiteral &&
            parent.Kind == global::Ts.Ast.SyntaxKind.TaggedTemplateExpression)
        {
            // Go: getArgumentListInfoForTemplate when inside the template. Tagged-template
            // signature help is deferred with the template-literal argument work.
            return null;
        }
        if (node.Kind == global::Ts.Ast.SyntaxKind.TemplateHead &&
            parent.Parent?.Kind == global::Ts.Ast.SyntaxKind.TaggedTemplateExpression)
        {
            // Deferred (tagged templates), as above.
            return null;
        }
        if (parent.Kind == global::Ts.Ast.SyntaxKind.TemplateSpan &&
            parent.Parent?.Parent?.Kind == global::Ts.Ast.SyntaxKind.TaggedTemplateExpression)
        {
            // Deferred (tagged templates), as above.
            return null;
        }
        if (parent.Kind == global::Ts.Ast.SyntaxKind.JsxOpeningElement ||
            parent.Kind == global::Ts.Ast.SyntaxKind.JsxSelfClosingElement)
        {
            // Go builds an attributes-span argumentListInfo for JSX opening-like elements.
            // Deferred with the JSX language-service work.
            return null;
        }
        // Go: getPossibleTypeArgumentsInfo — signature help while typing an unclosed type
        // argument list (`f<T|`). Deferred with the generic display work.
        return null;
    }

    // signaturehelp.go:1137 getArgumentOrParameterListInfo
    private static ArgumentOrParameterListInfo? GetArgumentOrParameterListInfo(
        global::Ts.Ast.Node node,
        global::Ts.Ast.SourceFile sourceFile,
        global::Ts.Checker.Checker checker)
    {
        var info = GetArgumentOrParameterListAndIndex(node, sourceFile, checker);
        if (info is null)
        {
            return null;
        }
        var argumentCount = GetArgumentCount(node, info.List, sourceFile, checker);
        var argumentsSpan = GetApplicableSpanForArguments(info.List, node, sourceFile);
        return new ArgumentOrParameterListInfo(info.List, info.ArgumentIndex, argumentCount, argumentsSpan);
    }

    // signaturehelp.go:1198 getArgumentOrParameterListAndIndex
    private static ArgumentOrParameterListAndIndex? GetArgumentOrParameterListAndIndex(
        global::Ts.Ast.Node node,
        global::Ts.Ast.SourceFile sourceFile,
        global::Ts.Checker.Checker checker)
    {
        if (node.Kind == global::Ts.Ast.SyntaxKind.LessThanToken ||
            node.Kind == global::Ts.Ast.SyntaxKind.OpenParenToken)
        {
            // Find the list that starts right *after* the < or ( token.
            // If the user has just opened a list, consider this item 0.
            var list = GetChildListThatStartsWithOpenerToken(node.Parent!, node);
            return new ArgumentOrParameterListAndIndex(list, 0);
        }
        // findContainingList can return null if we are not in parent's argument list
        // or type argument list. This includes cases where the cursor is:
        //   - To the right of the closing parenthesis, non-substitution template, or template tail.
        //   - Between the type arguments and the arguments (greater than token)
        //   - On the target of the call (parent.func)
        //   - On the 'new' keyword in a 'new' expression
        var containingList = FindContainingList(node, sourceFile);
        if (containingList is null)
        {
            return null;
        }
        return new ArgumentOrParameterListAndIndex(
            containingList,
            // Find the index of the argument that contains the node.
            GetArgumentIndex(node, containingList, sourceFile, checker));
    }

    // signaturehelp.go:1226 getChildListThatStartsWithOpenerToken
    private static global::Ts.Ast.NodeList? GetChildListThatStartsWithOpenerToken(
        global::Ts.Ast.Node parent,
        global::Ts.Ast.Node openerToken)
    {
        if (global::Ts.Ast.NodeTests.IsCallExpression(parent))
        {
            var parentCallExpression = parent.AsCallExpression();
            if (openerToken.Kind == global::Ts.Ast.SyntaxKind.LessThanToken)
            {
                return parent.TypeArgumentList();
            }
            return parentCallExpression.Arguments;
        }
        if (global::Ts.Ast.NodeTests.IsNewExpression(parent))
        {
            var parentNewExpression = parent.AsNewExpression();
            if (openerToken.Kind == global::Ts.Ast.SyntaxKind.LessThanToken)
            {
                return parent.TypeArgumentList();
            }
            return parentNewExpression.Arguments;
        }
        return null;
    }

    // signaturehelp.go:1080 getArgumentIndex
    private static int GetArgumentIndex(
        global::Ts.Ast.Node node,
        global::Ts.Ast.NodeList? arguments,
        global::Ts.Ast.SourceFile sourceFile,
        global::Ts.Checker.Checker checker)
    {
        return GetArgumentIndexOrCount(GetTokenFromNodeList(arguments, node.Parent, sourceFile), node, checker);
    }

    // signaturehelp.go:1084 getArgumentCount
    private static int GetArgumentCount(
        global::Ts.Ast.Node node,
        global::Ts.Ast.NodeList? arguments,
        global::Ts.Ast.SourceFile sourceFile,
        global::Ts.Checker.Checker checker)
    {
        return GetArgumentIndexOrCount(GetTokenFromNodeList(arguments, node.Parent, sourceFile), null, checker);
    }

    // signaturehelp.go:1088 getArgumentIndexOrCount
    private static int GetArgumentIndexOrCount(
        List<global::Ts.Ast.Node> arguments,
        global::Ts.Ast.Node? node,
        global::Ts.Checker.Checker checker)
    {
        var argumentIndex = 0;
        var skipComma = false;
        foreach (var arg in arguments)
        {
            if (node is not null && arg == node)
            {
                if (!skipComma && arg.Kind == global::Ts.Ast.SyntaxKind.CommaToken)
                {
                    argumentIndex++;
                }
                return argumentIndex;
            }
            if (global::Ts.Ast.NodeTests.IsSpreadElement(arg))
            {
                argumentIndex += GetSpreadElementCount(arg, checker);
                skipComma = true;
                continue;
            }
            if (arg.Kind != global::Ts.Ast.SyntaxKind.CommaToken)
            {
                argumentIndex++;
                skipComma = true;
                continue;
            }
            if (skipComma)
            {
                skipComma = false;
                continue;
            }
            argumentIndex++;
        }
        if (node is not null)
        {
            return argumentIndex;
        }
        // The argument count for a list is normally the number of non-comma children it has.
        // For example, if you have "Foo(a,b)" then there will be three children of the arg
        // list 'a' '<comma>' 'b'. So, in this case the arg count will be 2. However, there
        // is a small subtlety. If you have "Foo(a,)", then the child list will just have
        // 'a' '<comma>'. So, in the case where the last child is a comma, we increase the
        // arg count by one to compensate.
        var argumentCount = argumentIndex;
        if (arguments.Count > 0 && arguments[^1].Kind == global::Ts.Ast.SyntaxKind.CommaToken)
        {
            argumentCount = argumentIndex + 1;
        }
        return argumentCount;
    }

    // signaturehelp.go:1056 getSpreadElementCount. The faithful body needs
    // checker.GetTypeAtLocation plus tuple-type introspection (IsTupleType /
    // ElementFlags / FixedLength) — internal to Ts.Compiler. Go returns 0 for every
    // non-tuple spread; this stub returns Go's non-tuple result unconditionally.
    private static int GetSpreadElementCount(global::Ts.Ast.Node node, global::Ts.Checker.Checker checker)
    {
        _ = node;
        _ = checker;
        return 0;
    }

    // signaturehelp.go:1357 getTokenFromNodeList
    private static List<global::Ts.Ast.Node> GetTokenFromNodeList(
        global::Ts.Ast.NodeList? nodeList,
        global::Ts.Ast.Node? nodeListParent,
        global::Ts.Ast.SourceFile sourceFile)
    {
        var tokens = new List<global::Ts.Ast.Node>();
        if (nodeList is null || nodeListParent is null)
        {
            return tokens;
        }
        var left = nodeList.Pos;
        var nodeListIndex = 0;
        while (left < nodeList.End)
        {
            if (nodeList.Nodes.Length > nodeListIndex && left == nodeList.Nodes[nodeListIndex].Pos)
            {
                tokens.Add(nodeList.Nodes[nodeListIndex]);
                left = nodeList.Nodes[nodeListIndex].End;
                nodeListIndex++;
            }
            else
            {
                var scanner = global::Ts.Scanner.Scanner.GetScannerForSourceFile(sourceFile, left);
                var token = scanner.Token();
                var tokenFullStart = scanner.TokenFullStart();
                var tokenEnd = scanner.TokenEnd();
                tokens.Add(sourceFile.GetOrCreateToken(
                    token, tokenFullStart, tokenEnd, nodeListParent, scanner.TokenFlags()));
                left = tokenEnd;
            }
        }
        return tokens;
    }

    // signaturehelp.go:1154 getApplicableSpanForArguments
    private static global::Ts.Core.TextRange GetApplicableSpanForArguments(
        global::Ts.Ast.NodeList? argumentList,
        global::Ts.Ast.Node? node,
        global::Ts.Ast.SourceFile sourceFile)
    {
        // We use full start and skip trivia on the end because we want to include trivia on
        // both sides. For example,
        //
        //    foo(   /*comment */     a, b, c      /*comment*/     )
        //        |                                               |
        //
        // The applicable span is from the first bar to the second bar (inclusive,
        // but not including parentheses).
        if (argumentList is null && node is not null)
        {
            // If the user has just opened a list, and there are no arguments.
            // For example, foo(    )
            //                  |  |
            // The span should include positions inside the parentheses.
            var spanStart = node.End;
            var spanEnd = global::Ts.Scanner.Scanner.SkipTrivia(sourceFile.Text(), node.End);
            spanEnd = EnsureMinimumSpanSize(spanStart, spanEnd);
            return new global::Ts.Core.TextRange(spanStart, spanEnd);
        }
        var applicableSpanStart = argumentList!.Pos;
        var applicableSpanEnd = global::Ts.Scanner.Scanner.SkipTrivia(sourceFile.Text(), argumentList.End);

        // If the argument list is empty (Pos == End), extend the span to include at least
        // one position. This handles foo(|) where the cursor is right after the opening paren.
        applicableSpanEnd = EnsureMinimumSpanSize(applicableSpanStart, applicableSpanEnd);

        return new global::Ts.Core.TextRange(applicableSpanStart, applicableSpanEnd);
    }

    // signaturehelp.go:1186 ensureMinimumSpanSize
    // Ensures that a span includes at least one position. TextRange.Contains uses a
    // half-open interval, so an empty span would not contain the cursor immediately
    // after typing an opening paren in a call like foo(bar(|)).
    private static int EnsureMinimumSpanSize(int start, int end)
    {
        if (end <= start)
        {
            return start + 1;
        }
        return end;
    }

    // ── ls/utilities.go helpers (not yet hosted by a shared Ls utilities file) ──────

    // utilities.go:1098 RangeContainsRange
    private static bool RangeContainsRange(global::Ts.Core.TextRange r1, global::Ts.Core.TextRange r2)
    {
        return StartEndContainsRange(r1.Pos, r1.End, r2);
    }

    // utilities.go:1102 startEndContainsRange
    private static bool StartEndContainsRange(int start, int end, global::Ts.Core.TextRange textRange)
    {
        return start <= textRange.Pos && end >= textRange.End;
    }

    // utilities.go:1188 findContainingList
    private static global::Ts.Ast.NodeList? FindContainingList(
        global::Ts.Ast.Node node,
        global::Ts.Ast.SourceFile file)
    {
        // The node might be a list element (nonsynthetic) or a comma (synthetic). Either way, it will
        // be parented by the container of the SyntaxList, not the SyntaxList itself.
        global::Ts.Ast.NodeList? list = null;
        global::Ts.AstNav.Tokens.VisitEachChildAndJSDoc(
            node.Parent!,
            file,
            (n, visitor) => n,
            (nodes, visitor) =>
            {
                if (nodes is not null && RangeContainsRange(nodes.Loc, node.Loc))
                {
                    list = nodes;
                }
                return nodes;
            });
        return list;
    }

    // ── candidate signatures from the public declaration surface ────────────────────

    // Stand-in for signaturehelp.go:749 getCandidateOrTypeInfo (see the PORT-NOTE at the
    // call site). Resolves the invoked expression to its declarations through the public
    // emit resolver and keeps every function-like declaration as one candidate.
    private static (string CallTargetName, List<global::Ts.Ast.Node> Candidates) GetDeclarationSignatureCandidates(
        global::Ts.Checker.Checker checker,
        ArgumentListInfo argumentInfo)
    {
        var candidates = new List<global::Ts.Ast.Node>();
        if (argumentInfo.IsTypeParameterList)
        {
            // Generic (type-argument) signature help display is deferred; see scope note.
            return ("", candidates);
        }

        // signaturehelp.go:732 getExpressionFromInvocation → ast.GetInvokedExpression.
        var expression = global::Ts.Ast.AstUtilities.GetInvokedExpression(argumentInfo.Invocation.CallInvocationNode);
        if (expression is null || expression.Kind != global::Ts.Ast.SyntaxKind.Identifier)
        {
            // Property-access and element-access call targets need the member symbol
            // channel (checker.GetSymbolAtLocation); deferred with that surface.
            return ("", candidates);
        }

        var declarations = checker.GetEmitResolver().GetReferencedValueDeclarations(expression);
        foreach (var declaration in declarations)
        {
            var signatureDeclaration = GetSignatureDeclaration(declaration);
            if (signatureDeclaration is not null)
            {
                // Overload note: when a function has overload signatures, the checker's
                // candidate list excludes the implementation declaration. That filtering
                // needs the internal signature surface and is deferred; every
                // function-like declaration is kept here.
                candidates.Add(signatureDeclaration);
            }
        }
        return (expression.Text(), candidates);
    }

    private static global::Ts.Ast.Node? GetSignatureDeclaration(global::Ts.Ast.Node declaration)
    {
        switch (declaration.Kind)
        {
            case global::Ts.Ast.SyntaxKind.FunctionDeclaration:
            case global::Ts.Ast.SyntaxKind.FunctionExpression:
            case global::Ts.Ast.SyntaxKind.ArrowFunction:
            case global::Ts.Ast.SyntaxKind.MethodDeclaration:
                return declaration;
            case global::Ts.Ast.SyntaxKind.VariableDeclaration:
            {
                // const f = (…) => …  /  const f = function (…) {…}
                var initializer = declaration.Initializer();
                if (initializer is not null &&
                    (initializer.Kind == global::Ts.Ast.SyntaxKind.FunctionExpression ||
                        initializer.Kind == global::Ts.Ast.SyntaxKind.ArrowFunction))
                {
                    return initializer;
                }
                return null;
            }
            case global::Ts.Ast.SyntaxKind.ClassDeclaration:
            case global::Ts.Ast.SyntaxKind.ClassExpression:
                // Construct-signature help (new C(…)) needs the checker's construct
                // signatures (base-class constructor inheritance, overloads); deferred.
                return null;
            default:
                return null;
        }
    }

    // ── item creation ────────────────────────────────────────────────────────────────

    // signaturehelp.go:277 createSignatureHelpItems (foundation subset). Go prints
    // labels via checker.SymbolToString + nodebuilder display parts (internal); this
    // renders from declaration source text. Client-capability branches collapse to the
    // no-per-signature-activeParameter path: one top-level ActiveParameter.
    private static SignatureHelp? CreateSignatureHelpItems(
        string callTargetName,
        List<global::Ts.Ast.Node> candidateDeclarations,
        ArgumentListInfo argumentInfo,
        global::Ts.Ast.SourceFile sourceFile)
    {
        var signatures = new SignatureInformation[candidateDeclarations.Count];
        for (var i = 0; i < candidateDeclarations.Count; i++)
        {
            signatures[i] = CreateSignatureInformation(candidateDeclarations[i], callTargetName, sourceFile);
        }
        if (signatures.Length == 0)
        {
            return null;
        }

        // Go seeds selectedItemIndex from candidates[i] == resolvedSignature and then,
        // within that item's variants, prefers the first one that is variadic or has
        // enough parameters for the apparent argument count. The resolved signature is
        // unavailable (checker.GetResolvedSignature is internal), so the same preference
        // runs over the whole list from index 0.
        var selectedItemIndex = 0;
        for (var i = 0; i < signatures.Length; i++)
        {
            var signature = signatures[i];
            if (signature.IsVariadic || signature.Parameters.Length >= argumentInfo.ArgumentCount)
            {
                selectedItemIndex = i;
                break;
            }
        }

        var activeParameter = ComputeActiveParameter(signatures[selectedItemIndex], argumentInfo.ArgumentIndex);
        return new SignatureHelp(signatures, selectedItemIndex, activeParameter);
    }

    private static SignatureInformation CreateSignatureInformation(
        global::Ts.Ast.Node signatureDeclaration,
        string callTargetName,
        global::Ts.Ast.SourceFile sourceFile)
    {
        var parameterNodes = signatureDeclaration.Parameters();
        var parameters = new ParameterInformation[parameterNodes.Length];
        var isVariadic = false;
        var labelBuilder = new System.Text.StringBuilder();
        labelBuilder.Append(callTargetName);
        labelBuilder.Append('(');
        for (var i = 0; i < parameterNodes.Length; i++)
        {
            var parameterNode = parameterNodes[i];
            var parameterLabel = NodeSourceText(parameterNode, sourceFile);
            var isRest = parameterNode.AsParameterDeclaration().DotDotDotToken is not null;
            isVariadic = isVariadic || isRest;
            parameters[i] = new ParameterInformation(parameterLabel, isRest);
            if (i > 0)
            {
                labelBuilder.Append(", ");
            }
            labelBuilder.Append(parameterLabel);
        }
        labelBuilder.Append(')');
        var returnTypeNode = signatureDeclaration.Type();
        if (returnTypeNode is not null)
        {
            labelBuilder.Append(": ");
            labelBuilder.Append(NodeSourceText(returnTypeNode, sourceFile));
        }
        return new SignatureInformation(labelBuilder.ToString(), parameters, isVariadic);
    }

    // Source-text rendering of a node (token start, trivia excluded, through End).
    private static string NodeSourceText(global::Ts.Ast.Node node, global::Ts.Ast.SourceFile sourceFile)
    {
        var start = global::Ts.Scanner.Scanner.GetTokenPosOfNode(node, sourceFile, false /*includeJSDoc*/);
        return sourceFile.Text()[start..node.End];
    }

    // signaturehelp.go:401 computeActiveParameter. Go returns *lsproto.UintegerOrNull —
    // nil for "no parameters" and JSON null for "no active parameter" when the client
    // supports it; the raw foundation API folds both onto C# null.
    private static int? ComputeActiveParameter(SignatureInformation signature, int argumentIndex)
    {
        var paramCount = signature.Parameters.Length;
        if (paramCount == 0)
        {
            // No parameters, no active parameter.
            return null;
        }

        var activeParam = argumentIndex;
        if (signature.IsVariadic)
        {
            var firstRest = -1;
            for (var i = 0; i < signature.Parameters.Length; i++)
            {
                if (signature.Parameters[i].IsRest)
                {
                    firstRest = i;
                    break;
                }
            }
            if (firstRest > -1 && firstRest < paramCount - 1)
            {
                // Middle rest parameter - we can't accurately highlight, so indicate
                // "no active parameter".
                return null;
            }
            // Clamp to last parameter for trailing rest parameters
            if (activeParam > paramCount - 1)
            {
                activeParam = paramCount - 1;
            }
        }

        return activeParam;
    }
}
