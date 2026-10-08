// Ported from tsgo internal/ls/codeactions_fixmissingtypeannotation.go (whole file): the
// IsolatedDeclarationsFixProvider — type-annotation / inline-assertion / extract-to-variable
// code actions for isolatedDeclarations errors, and the isolatedDeclarationsFixer engine.
//
// PORT-NOTE: `change.NewTracker(ctx, ...)` → Change.Tracker.NewTracker(...) (ctx drops, see
// Tracker.cs header); locale.FromContext(ctx) → Locale.Default (LS port locale convention).

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Checker; // CheckerLsExports extension methods (TypeToTypeNodeEx, GetUnionTypeEx, ...)
using Ts.Core;
using Ts.Diagnostics; // Message.Localize extension

namespace Ts.Ls;

// codeactions_fixmissingtypeannotation.go:21-43 — isolatedDeclarationsFixErrorCodes.
public static partial class CodeActions
{
    internal static readonly int[] isolatedDeclarationsFixErrorCodes =
    [
        global::Ts.Diagnostics.Diagnostics.Function_must_have_an_explicit_return_type_annotation_with_isolatedDeclarations.Code,
        global::Ts.Diagnostics.Diagnostics.Method_must_have_an_explicit_return_type_annotation_with_isolatedDeclarations.Code,
        global::Ts.Diagnostics.Diagnostics.At_least_one_accessor_must_have_an_explicit_type_annotation_with_isolatedDeclarations.Code,
        global::Ts.Diagnostics.Diagnostics.Variable_must_have_an_explicit_type_annotation_with_isolatedDeclarations.Code,
        global::Ts.Diagnostics.Diagnostics.Parameter_must_have_an_explicit_type_annotation_with_isolatedDeclarations.Code,
        global::Ts.Diagnostics.Diagnostics.Property_must_have_an_explicit_type_annotation_with_isolatedDeclarations.Code,
        global::Ts.Diagnostics.Diagnostics.Expression_type_can_t_be_inferred_with_isolatedDeclarations.Code,
        global::Ts.Diagnostics.Diagnostics.Binding_elements_can_t_be_exported_directly_with_isolatedDeclarations.Code,
        global::Ts.Diagnostics.Diagnostics.Computed_property_names_on_class_or_object_literals_cannot_be_inferred_with_isolatedDeclarations.Code,
        global::Ts.Diagnostics.Diagnostics.Computed_properties_must_be_number_or_string_literals_variables_or_dotted_expressions_with_isolatedDeclarations.Code,
        global::Ts.Diagnostics.Diagnostics.Enum_member_initializers_must_be_computable_without_references_to_external_symbols_with_isolatedDeclarations.Code,
        global::Ts.Diagnostics.Diagnostics.Extends_clause_can_t_contain_an_expression_with_isolatedDeclarations.Code,
        global::Ts.Diagnostics.Diagnostics.Objects_that_contain_shorthand_properties_can_t_be_inferred_with_isolatedDeclarations.Code,
        global::Ts.Diagnostics.Diagnostics.Objects_that_contain_spread_assignments_can_t_be_inferred_with_isolatedDeclarations.Code,
        global::Ts.Diagnostics.Diagnostics.Arrays_with_spread_elements_can_t_inferred_with_isolatedDeclarations.Code,
        global::Ts.Diagnostics.Diagnostics.Default_exports_can_t_be_inferred_with_isolatedDeclarations.Code,
        global::Ts.Diagnostics.Diagnostics.Only_const_arrays_can_be_inferred_with_isolatedDeclarations.Code,
        global::Ts.Diagnostics.Diagnostics.Assigning_properties_to_functions_without_declaring_them_is_not_supported_with_isolatedDeclarations_Add_an_explicit_declaration_for_the_properties_assigned_to_this_function.Code,
        global::Ts.Diagnostics.Diagnostics.Declaration_emit_for_this_parameter_requires_implicitly_adding_undefined_to_its_type_This_is_not_supported_with_isolatedDeclarations.Code,
        global::Ts.Diagnostics.Diagnostics.Type_containing_private_name_0_can_t_be_used_with_isolatedDeclarations.Code,
        global::Ts.Diagnostics.Diagnostics.Add_satisfies_and_a_type_assertion_to_this_expression_satisfies_T_as_T_to_make_the_type_explicit.Code,
    ];

    // codeactions_fixmissingtypeannotation.go:45 — fixMissingTypeAnnotationOnExportsFixID.
    internal const string fixMissingTypeAnnotationOnExportsFixID = "fixMissingTypeAnnotationOnExports";

    // codeactions_fixmissingtypeannotation.go:47-53 — IsolatedDeclarationsFixProvider is the
    // CodeFixProvider for isolatedDeclarations-related type annotation fixes.
    public static readonly CodeFixProvider IsolatedDeclarationsFixProvider = new()
    {
        ErrorCodes = isolatedDeclarationsFixErrorCodes,
        GetCodeActions = LanguageService.GetIsolatedDeclarationsCodeActionsPort,
        FixIds = [fixMissingTypeAnnotationOnExportsFixID],
        GetAllCodeActions = LanguageService.GetAllIsolatedDeclarationsCodeActionsPort,
    };

    // codeactions_fixmissingtypeannotation.go:55-69 — canHaveTypeAnnotationKinds are the node
    // kinds that can have type annotations added. (Go map[ast.Kind]bool → membership set.)
    internal static readonly HashSet<SyntaxKind> canHaveTypeAnnotationKinds =
    [
        SyntaxKind.GetAccessor,
        SyntaxKind.MethodDeclaration,
        SyntaxKind.PropertyDeclaration,
        SyntaxKind.FunctionDeclaration,
        SyntaxKind.FunctionExpression,
        SyntaxKind.ArrowFunction,
        SyntaxKind.VariableDeclaration,
        SyntaxKind.Parameter,
        SyntaxKind.ExportAssignment,
        SyntaxKind.ClassDeclaration,
        SyntaxKind.ObjectBindingPattern,
        SyntaxKind.ArrayBindingPattern,
    ];

    // codeactions_fixmissingtypeannotation.go:71-78 — declarationEmitNodeBuilderFlags are the
    // node builder flags used for declaration emit.
    internal const global::Ts.NodeBuilder.Flags declarationEmitNodeBuilderFlags =
        global::Ts.NodeBuilder.Flags.MultilineObjectLiterals |
        global::Ts.NodeBuilder.Flags.WriteClassExpressionAsTypeLiteral |
        global::Ts.NodeBuilder.Flags.UseTypeOfFunction |
        global::Ts.NodeBuilder.Flags.UseStructuralFallback |
        global::Ts.NodeBuilder.Flags.AllowEmptyTuple |
        global::Ts.NodeBuilder.Flags.GenerateNamesForShadowedTypeParams |
        global::Ts.NodeBuilder.Flags.NoTruncation;

    // codeactions_fixmissingtypeannotation.go:307-310 — needsParenthesizedExpressionForAssertion
    // checks if an expression needs parentheses for an assertion.
    internal static bool NeedsParenthesizedExpressionForAssertion(Node node)
    {
        return !AstUtilities.IsEntityNameExpression(node) && !NodeTests.IsCallExpression(node) && !NodeTests.IsObjectLiteralExpression(node) && !NodeTests.IsArrayLiteralExpression(node);
    }

    // codeactions_fixmissingtypeannotation.go:312-318 — createAsExpression creates an
    // `expr as Type` expression, parenthesizing if needed.
    internal static Node CreateAsExpression(NodeFactory factory, Node node, Node typeNode)
    {
        if (NeedsParenthesizedExpressionForAssertion(node))
        {
            node = factory.NewParenthesizedExpression(node);
        }
        return factory.NewAsExpression(node, typeNode);
    }

    // codeactions_fixmissingtypeannotation.go:475-481 — isExpandoPropertyDeclarationForFix
    // matches TS's isExpandoPropertyDeclaration which includes PropertyAccessExpression,
    // ElementAccessExpression, and BinaryExpression. The shared ast.IsExpandoPropertyDeclaration
    // was narrowed to BinaryExpression only for checker purposes.
    internal static bool IsExpandoPropertyDeclarationForFix(Node? node)
    {
        return node != null && (NodeTests.IsPropertyAccessExpression(node) || NodeTests.IsElementAccessExpression(node) || NodeTests.IsBinaryExpression(node));
    }

    // codeactions_fixmissingtypeannotation.go:483-547 — findExpandoFunction finds the function
    // declaration that has expando properties assigned to it.
    internal static Node? FindExpandoFunction(global::Ts.Checker.Checker ch, Node? node)
    {
        var expandoDeclaration = AstUtilities.FindAncestorOrQuit(node, n =>
        {
            if (AstUtilities.IsStatement(n))
            {
                return FindAncestorResult.Quit;
            }
            if (IsExpandoPropertyDeclarationForFix(n))
            {
                return FindAncestorResult.True;
            }
            return FindAncestorResult.False;
        });

        if (expandoDeclaration == null || !IsExpandoPropertyDeclarationForFix(expandoDeclaration))
        {
            return null;
        }

        var assignmentTarget = expandoDeclaration;
        // Some late bound expando members use the whole expression as the declaration.
        if (NodeTests.IsBinaryExpression(assignmentTarget))
        {
            assignmentTarget = assignmentTarget.AsBinaryExpression().Left!;
            if (!IsExpandoPropertyDeclarationForFix(assignmentTarget))
            {
                return null;
            }
        }

        Node? expression;
        if (NodeTests.IsPropertyAccessExpression(assignmentTarget))
        {
            expression = assignmentTarget.AsPropertyAccessExpression().Expression;
        }
        else if (NodeTests.IsElementAccessExpression(assignmentTarget))
        {
            expression = assignmentTarget.AsElementAccessExpression().Expression;
        }
        else
        {
            return null;
        }

        var targetType = ch.GetTypeAtLocation(expression!);
        if (targetType == null)
        {
            return null;
        }

        var properties = ch.GetPropertiesOfType(targetType);
        var found = false;
        foreach (var p in properties)
        {
            if (p.ValueDeclaration == expandoDeclaration || p.ValueDeclaration == expandoDeclaration.Parent)
            {
                found = true;
                break;
            }
        }
        if (!found)
        {
            return null;
        }

        var symbol = targetType.Symbol;
        if (symbol == null || symbol.ValueDeclaration == null)
        {
            return null;
        }

        var fn = symbol.ValueDeclaration;
        if (NodeTests.IsFunctionExpression(fn) && NodeTests.IsVariableDeclaration(fn.Parent!))
        {
            return fn.Parent;
        }
        if (NodeTests.IsFunctionDeclaration(fn))
        {
            return fn;
        }

        return null;
    }

    // codeactions_fixmissingtypeannotation.go:1106-1113 — isConstAssertion checks if a node is
    // an `as const` or `<const>` assertion.
    internal static bool IsConstAssertion(Node node)
    {
        if (AstUtilities.IsAssertionExpression(node))
        {
            var typeNode = node.Type();
            return AstUtilities.IsConstTypeReference(typeNode!);
        }
        return false;
    }

    // codeactions_fixmissingtypeannotation.go:1205-1242 — endOfRequiredTypeParameters finds the
    // number of type arguments that are actually required (i.e., differ from their defaults).
    // Ported from TS's services/codefixes/helpers.ts endOfRequiredTypeParameters.
    internal static int EndOfRequiredTypeParameters(global::Ts.Checker.Checker ch, global::Ts.Checker.Type t)
    {
        var typeArgs = ch.GetTypeArguments(t);
        if (typeArgs.Length == 0)
        {
            return 0;
        }
        var target = t.Target();
        if (target == null || target.AsInterfaceType() == null)
        {
            return typeArgs.Length;
        }
        var typeParams = target.AsInterfaceType()!.TypeParameters()!;
        var localTypeParams = target.AsInterfaceType()!.LocalTypeParameters()!;
        var outerCount = typeParams.Length - localTypeParams.Length;
        for (var cutoff = 0; cutoff < typeArgs.Length; cutoff++)
        {
            // Skip cutoff positions where the local type parameter has no default.
            // This matches TS's check for constraint === undefined on localTypeParameters,
            // which in practice skips type parameters without defaults (e.g. Set<T>
            // where T has no default should not have <unknown> elided).
            var localIdx = cutoff - outerCount;
            if (localIdx < 0 || localIdx >= localTypeParams.Length || !TypeParamHasDefault(localTypeParams[localIdx]))
            {
                continue;
            }
            var filledIn = ch.FillMissingTypeArguments(typeArgs[..cutoff], typeParams, cutoff, false);
            var allMatch = true;
            for (var i = 0; i < filledIn!.Length; i++)
            {
                if (filledIn[i] != typeArgs[i])
                {
                    allMatch = false;
                    break;
                }
            }
            if (allMatch)
            {
                return cutoff;
            }
        }
        return typeArgs.Length;
    }

    // codeactions_fixmissingtypeannotation.go:1244-1256 — typeParamHasDefault checks if a type
    // parameter has a default type declaration.
    internal static bool TypeParamHasDefault(global::Ts.Checker.Type tp)
    {
        var sym = tp.Symbol;
        if (sym == null)
        {
            return false;
        }
        foreach (var decl in sym.Declarations ?? [])
        {
            if (NodeTests.IsTypeParameterDeclaration(decl) && decl.AsTypeParameterDeclaration().DefaultType != null)
            {
                return true;
            }
        }
        return false;
    }

    // codeactions_fixmissingtypeannotation.go:1277-1300 — typeToStringForDiag converts a type
    // node to a string for use in diagnostic descriptions. It reuses the change tracker's
    // EmitContext so that generated identifier names are resolved consistently with the actual
    // code edits, and passes the source file so that the printer's name generator can check for
    // conflicts with existing file-level identifiers.
    internal static string TypeToStringForDiag(Node typeNode, SourceFile sourceFile, global::Ts.Ls.Change.Tracker ct)
    {
        var savedFlags = ct.EmitContext.EmitFlags(typeNode);
        ct.EmitContext.SetEmitFlags(typeNode, savedFlags | global::Ts.Printer.EmitFlags.SingleLine);
        var p = global::Ts.Printer.Printer.NewPrinter(
            new global::Ts.Printer.PrinterOptions
            {
                NewLine = NewLineKind.LF,
            },
            new global::Ts.Printer.PrintHandlers(),
            ct.EmitContext);
        var (writer, release) = global::Ts.Checker.CheckerLsExports.GetSingleLineStringWriter();
        try
        {
            p.Write(typeNode, sourceFile, writer, null);
            ct.EmitContext.SetEmitFlags(typeNode, savedFlags);
            var result = writer.String();
            if (result.Length > 160)
            {
                return result[..157] + "...";
            }
            return result;
        }
        finally
        {
            release();
        }
    }

    // codeactions_fixmissingtypeannotation.go:1302-1314 — findAncestorWithMissingType walks up
    // the ancestor chain to find a node that can have a type annotation and is missing one.
    internal static Node? FindAncestorWithMissingType(Node? node)
    {
        return AstUtilities.FindAncestor(node, n =>
        {
            if (!canHaveTypeAnnotationKinds.Contains(n.Kind))
            {
                return false;
            }
            if (NodeTests.IsObjectBindingPattern(n) || NodeTests.IsArrayBindingPattern(n))
            {
                return NodeTests.IsVariableDeclaration(n.Parent!);
            }
            return true;
        });
    }

    // codeactions_fixmissingtypeannotation.go:1316-1334 — findBestFittingNode walks up from the
    // token to find the node that best fits the diagnostic span.
    internal static Node? FindBestFittingNode(Node? node, TextRange span)
    {
        if (node == null)
        {
            return null;
        }
        while (node != null && node.End < span.Pos + span.Len)
        {
            node = node.Parent;
        }
        while (node!.Parent != null && node.Parent.Pos == node.Pos && node.Parent.End == node.End)
        {
            node = node.Parent;
        }
        if (NodeTests.IsIdentifier(node) && AstUtilities.HasInitializer(node.Parent!) && node.Parent!.Initializer() != null)
        {
            return node.Parent!.Initializer();
        }
        if (NodeTests.IsIdentifier(node) && NodeTests.IsShorthandPropertyAssignment(node.Parent!))
        {
            return node.Parent;
        }
        return node;
    }

    // codeactions_fixmissingtypeannotation.go:1336-1356 — isNamedDeclarationKind matches TS's
    // isDeclarationKind, which is narrower than Go's IsDeclaration. Go's IsDeclaration returns
    // true for any node with DeclarationData (including CallExpression), while TS's
    // isDeclaration only returns true for specific named declaration kinds.
    internal static bool IsNamedDeclarationKind(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.BindingElement:
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.ClassExpression:
            case SyntaxKind.ClassStaticBlockDeclaration:
            case SyntaxKind.Constructor:
            case SyntaxKind.EnumDeclaration:
            case SyntaxKind.EnumMember:
            case SyntaxKind.ExportSpecifier:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.ImportClause:
            case SyntaxKind.ImportEqualsDeclaration:
            case SyntaxKind.ImportSpecifier:
            case SyntaxKind.InterfaceDeclaration:
            case SyntaxKind.JsxAttribute:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.MethodSignature:
            case SyntaxKind.ModuleDeclaration:
            case SyntaxKind.NamespaceExportDeclaration:
            case SyntaxKind.NamespaceImport:
            case SyntaxKind.NamespaceExport:
            case SyntaxKind.Parameter:
            case SyntaxKind.PropertyAssignment:
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.PropertySignature:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.ShorthandPropertyAssignment:
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.TypeParameter:
            case SyntaxKind.VariableDeclaration:
            case SyntaxKind.JSDocTypedefTag:
            case SyntaxKind.JSDocCallbackTag:
            case SyntaxKind.JSDocPropertyTag:
            case SyntaxKind.NamedTupleMember:
                return true;
        }
        return false;
    }

    // codeactions_fixmissingtypeannotation.go:1358-1362 — isValueSignatureDeclaration checks if
    // a node is a function-like declaration that produces a value.
    internal static bool IsValueSignatureDeclaration(Node? node)
    {
        return node != null && (NodeTests.IsFunctionExpression(node) || NodeTests.IsArrowFunction(node) || NodeTests.IsMethodDeclaration(node) ||
            AstUtilities.IsAccessor(node) || NodeTests.IsFunctionDeclaration(node) || NodeTests.IsConstructorDeclaration(node));
    }

    // codeactions_fixmissingtypeannotation.go:1364-1375 — getIdentifierNameForNode derives a
    // meaningful variable name from a node expression. For property access expressions like
    // `obj.foo`, returns "foo". Otherwise returns "newLocal". Ported from TS's
    // getIdentifierForNode in services/refactors/helpers.ts.
    internal static string GetIdentifierNameForNode(Node node)
    {
        if (NodeTests.IsPropertyAccessExpression(node))
        {
            var name = node.AsPropertyAccessExpression().Name();
            if (NodeTests.IsIdentifier(name!) && !NodeTests.IsPrivateIdentifier(name!) && global::Ts.Scanner.Scanner.IdentifierToKeywordKind(name!.AsIdentifier()) == SyntaxKind.Unknown)
            {
                return name.Text();
            }
        }
        return "newLocal";
    }
}

// codeactions_fixmissingtypeannotation.go:80-86 — typePrintMode.
internal enum TypePrintMode
{
    Full = 0,
    Relative = 1, // typeof X
    Widened = 2,  // widened literal type
}

// codeactions_fixmissingtypeannotation.go:215-227 — isolatedDeclarationsFixer encapsulates the
// state for fixing isolated declarations errors.
internal sealed class IsolatedDeclarationsFixer
{
    internal SourceFile sourceFile = null!;
    internal global::Ts.Compiler.Program program = null!;
    internal global::Ts.Checker.Checker checker = null!;
    internal global::Ts.Ls.Change.Tracker changeTracker = null!;
    internal global::Ts.Ls.AutoImport.ImportAdder? importAdder;
    internal global::Ts.Locale.Locale locale;
    internal Dictionary<Node, bool> fixedNodes = null!;
    internal TypePrintMode typePrintMode;
    internal List<global::Ts.Ast.Symbol> symbolsToImport = [];
    internal bool mutatedTarget; // set by inferType/relativeType when the target was mutated (e.g., spread decomposition)

    // codeactions_fixmissingtypeannotation.go:229-245 — addTypeAnnotation.
    internal string AddTypeAnnotation(TextRange span)
    {
        var nodeWithDiag = global::Ts.AstNav.Tokens.GetTokenAtPosition(sourceFile, span.Pos);

        var expandoFunction = CodeActions.FindExpandoFunction(checker, nodeWithDiag);
        if (expandoFunction != null)
        {
            if (NodeTests.IsFunctionDeclaration(expandoFunction))
            {
                return CreateNamespaceForExpandoProperties(expandoFunction);
            }
            return FixIsolatedDeclarationError(expandoFunction);
        }

        var nodeMissingType = CodeActions.FindAncestorWithMissingType(nodeWithDiag);
        if (nodeMissingType != null)
        {
            return FixIsolatedDeclarationError(nodeMissingType);
        }
        return "";
    }

    // codeactions_fixmissingtypeannotation.go:247-305 — createNamespaceForExpandoProperties.
    internal string CreateNamespaceForExpandoProperties(Node expandoFunc)
    {
        var funcDecl = expandoFunc.AsFunctionDeclaration();
        if (funcDecl.Name() == null)
        {
            return "";
        }

        var t = checker.GetTypeAtLocation(expandoFunc);
        var elements = checker.GetPropertiesOfType(t!);
        if (elements.Length == 0)
        {
            return "";
        }

        var factory = changeTracker.NodeFactory;

        var newProperties = new List<Node>();
        foreach (var symbol in elements)
        {
            if (!global::Ts.Scanner.Scanner.IsIdentifierText(symbol.Name, LanguageVariant.Standard))
            {
                continue;
            }
            // skip symbols that already have a variable declaration
            if (symbol.ValueDeclaration != null && NodeTests.IsVariableDeclaration(symbol.ValueDeclaration))
            {
                continue;
            }

            var symType = checker.GetTypeOfSymbol(symbol);
            var typeNode = TypeToMinimizedReferenceType(symType!, expandoFunc, CodeActions.declarationEmitNodeBuilderFlags);
            if (typeNode == null)
            {
                continue;
            }

            var varDecl = factory.NewVariableDeclaration(factory.NewIdentifier(symbol.Name), null, typeNode, null);
            var exportToken = factory.NewToken(SyntaxKind.ExportKeyword);
            var varDeclList = factory.NewVariableDeclarationList(factory.NewNodeList([varDecl]), NodeFlags.None);
            var varStmt = factory.NewVariableStatement(factory.NewModifierList([exportToken]), varDeclList);
            newProperties.Add(varStmt);
        }

        if (newProperties.Count == 0)
        {
            return "";
        }

        var modifiers = new List<Node>();
        if (AstUtilities.HasSyntacticModifier(expandoFunc, ModifierFlags.Export))
        {
            modifiers.Add(factory.NewToken(SyntaxKind.ExportKeyword));
        }
        modifiers.Add(factory.NewToken(SyntaxKind.DeclareKeyword));

        var namespaceNode = factory.NewModuleDeclaration(
            factory.NewModifierList(modifiers.ToArray()),
            SyntaxKind.NamespaceKeyword,
            factory.NewIdentifier(funcDecl.Name()!.Text()),
            factory.NewModuleBlock(factory.NewNodeList(newProperties.ToArray())));
        // Set the flags for namespace
        namespaceNode.Flags = NodeFlags.Ambient | NodeFlags.ExportContext | NodeFlags.ContextFlags;

        changeTracker.InsertNodeAfter(sourceFile, expandoFunc, namespaceNode);
        return global::Ts.Diagnostics.Diagnostics.Annotate_types_of_properties_expando_function_in_a_namespace.Localize(locale);
    }

    // codeactions_fixmissingtypeannotation.go:320-402 — addInlineAssertion.
    internal string AddInlineAssertion(TextRange span)
    {
        var nodeWithDiag = global::Ts.AstNav.Tokens.GetTokenAtPosition(sourceFile, span.Pos);

        // No inline assertions for expando members
        var expandoFunction = CodeActions.FindExpandoFunction(checker, nodeWithDiag);
        if (expandoFunction != null)
        {
            return "";
        }

        var targetNode = CodeActions.FindBestFittingNode(nodeWithDiag, span);
        if (targetNode == null || CodeActions.IsValueSignatureDeclaration(targetNode) || CodeActions.IsValueSignatureDeclaration(targetNode.Parent))
        {
            return "";
        }

        var isExpressionTarget = AstUtilities.IsExpression(targetNode);
        var isShorthandPropertyAssignmentTarget = NodeTests.IsShorthandPropertyAssignment(targetNode);

        // Go's IsDeclaration is broader than TS's isDeclaration (e.g. CallExpression has
        // DeclarationData in Go but is not a declaration kind in TS). Use
        // isNamedDeclarationKind to match TS behavior.
        if (!isShorthandPropertyAssignmentTarget && CodeActions.IsNamedDeclarationKind(targetNode))
        {
            return "";
        }
        // No inline assertions on binding patterns
        if (AstUtilities.FindAncestor(targetNode, AstUtilities.IsBindingPattern) != null)
        {
            return "";
        }
        // No inline assertions on enum members
        if (AstUtilities.FindAncestor(targetNode, NodeTests.IsEnumMember) != null)
        {
            return "";
        }
        // No support for typeof in extends clauses
        if (isExpressionTarget && (AstUtilities.FindAncestorKind(targetNode, SyntaxKind.HeritageClause) != null || AstUtilities.FindAncestor(targetNode, AstUtilities.IsTypeNode) != null))
        {
            return "";
        }
        // Can't inline type spread elements
        if (NodeTests.IsSpreadElement(targetNode))
        {
            return "";
        }

        var variableDeclaration = AstUtilities.FindAncestorKind(targetNode, SyntaxKind.VariableDeclaration);
        global::Ts.Checker.Type? variableType = null;
        if (variableDeclaration != null)
        {
            variableType = checker.GetTypeAtLocation(variableDeclaration);
        }
        // Can't use typeof on unique symbols
        if (variableType != null && (variableType.Flags & global::Ts.Checker.TypeFlags.UniqueESSymbol) != 0)
        {
            return "";
        }

        if (!isExpressionTarget && !isShorthandPropertyAssignmentTarget)
        {
            return "";
        }

        var typeNode = InferType(targetNode, variableType);
        if (typeNode == null || mutatedTarget)
        {
            return "";
        }

        var factory = changeTracker.NodeFactory;

        if (isShorthandPropertyAssignmentTarget)
        {
            // Insert `: expr as Type` after the shorthand property name
            var clonedName = factory.DeepCloneNode(targetNode.AsShorthandPropertyAssignment().Name()!)!;
            var asExpr = CodeActions.CreateAsExpression(factory, clonedName, typeNode);
            changeTracker.InsertNodeAt(sourceFile, targetNode.End, asExpr, new global::Ts.Ls.Change.NodeOptions { Prefix = ": " });
        }
        else if (isExpressionTarget)
        {
            // Replace expression with `(expression) satisfies Type as Type` or
            // `expression satisfies Type as Type`
            var clonedTarget = factory.DeepCloneNode(targetNode)!;
            if (CodeActions.NeedsParenthesizedExpressionForAssertion(targetNode))
            {
                clonedTarget = factory.NewParenthesizedExpression(clonedTarget);
            }
            var clonedType = factory.DeepCloneNode(typeNode)!;
            var satisfiesAsExpr = factory.NewAsExpression(
                factory.NewSatisfiesExpression(clonedTarget, clonedType),
                typeNode);
            changeTracker.ReplaceNode(sourceFile, targetNode, satisfiesAsExpr, null);
        }
        else
        {
            return "";
        }

        return global::Ts.Diagnostics.Diagnostics.Add_satisfies_and_an_inline_type_assertion_with_0.Localize(locale, CodeActions.TypeToStringForDiag(typeNode, sourceFile, changeTracker));
    }

    // codeactions_fixmissingtypeannotation.go:404-473 — extractAsVariable.
    internal string ExtractAsVariable(TextRange span)
    {
        var nodeWithDiag = global::Ts.AstNav.Tokens.GetTokenAtPosition(sourceFile, span.Pos);
        var targetNode = CodeActions.FindBestFittingNode(nodeWithDiag, span);
        if (targetNode == null || CodeActions.IsValueSignatureDeclaration(targetNode) || CodeActions.IsValueSignatureDeclaration(targetNode.Parent))
        {
            return "";
        }

        if (!AstUtilities.IsExpression(targetNode))
        {
            return "";
        }

        var factory = changeTracker.NodeFactory;

        // Array literals should be marked as const
        if (NodeTests.IsArrayLiteralExpression(targetNode))
        {
            var constRef = factory.NewTypeReferenceNode(factory.NewIdentifier("const"), null);
            var cloned = factory.DeepCloneNode(targetNode)!;
            changeTracker.ReplaceNode(sourceFile, targetNode, CodeActions.CreateAsExpression(factory, cloned, constRef), null);
            return global::Ts.Diagnostics.Diagnostics.Mark_array_literal_as_const.Localize(locale);
        }

        var parentPropertyAssignment = AstUtilities.FindAncestorKind(targetNode, SyntaxKind.PropertyAssignment);
        if (parentPropertyAssignment != null)
        {
            // Identifiers or entity names can already be typeof-ed
            if (parentPropertyAssignment == targetNode.Parent && AstUtilities.IsEntityNameExpression(targetNode))
            {
                return "";
            }

            var tempName = changeTracker.EmitContext.Factory.NewUniqueNameEx(CodeActions.GetIdentifierNameForNode(targetNode), new global::Ts.Printer.AutoGenerateOptions { Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic });

            var replacementTarget = targetNode;
            var initializationNode = targetNode;

            // Handle spread elements: walk up to the spread's parent and handle const assertions
            if (NodeTests.IsSpreadElement(replacementTarget))
            {
                replacementTarget = AstUtilities.WalkUpParenthesizedExpressions(replacementTarget.Parent!)!;
                if (CodeActions.IsConstAssertion(replacementTarget.Parent!))
                {
                    replacementTarget = replacementTarget.Parent;
                    initializationNode = replacementTarget;
                }
                else
                {
                    var constRef = factory.NewTypeReferenceNode(factory.NewIdentifier("const"), null);
                    initializationNode = CodeActions.CreateAsExpression(factory, factory.DeepCloneNode(replacementTarget!)!, constRef);
                }
            }

            if (AstUtilities.IsEntityNameExpression(replacementTarget!))
            {
                return "";
            }

            var clonedInit = factory.DeepCloneNode(initializationNode!)!;
            var varDecl = factory.NewVariableDeclaration(tempName, null, null, clonedInit);
            var varDeclList = factory.NewVariableDeclarationList(factory.NewNodeList([varDecl]), NodeFlags.Const);
            var varStmt = factory.NewVariableStatement(null, varDeclList);

            var statement = AstUtilities.FindAncestor(targetNode, AstUtilities.IsStatement);
            if (statement == null)
            {
                return "";
            }
            changeTracker.InsertNodeBefore(sourceFile, statement, varStmt, false, global::Ts.Ls.Change.LeadingTriviaOption.None);

            var typeQuery = factory.NewTypeQueryNode(tempName, null);
            var asExpr = factory.NewAsExpression(tempName, typeQuery);
            changeTracker.ReplaceNode(sourceFile, replacementTarget!, asExpr, null);

            var idText = CodeActions.TypeToStringForDiag(tempName, sourceFile, changeTracker);
            return global::Ts.Diagnostics.Diagnostics.Extract_to_variable_and_replace_with_0_as_typeof_0.Localize(locale, idText);
        }

        return "";
    }

    // codeactions_fixmissingtypeannotation.go:549-571 — fixIsolatedDeclarationError.
    internal string FixIsolatedDeclarationError(Node node)
    {
        // Avoid creating duplicate fixes for the same node
        if (fixedNodes.TryGetValue(node, out var already) && already)
        {
            return "";
        }
        fixedNodes[node] = true;

        switch (node.Kind)
        {
            case SyntaxKind.Parameter:
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.VariableDeclaration:
                return AddTypeToVariableLike(node);
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.GetAccessor:
                return AddTypeToSignatureDeclaration(node);
            case SyntaxKind.ExportAssignment:
                return TransformExportAssignment(node);
            case SyntaxKind.ClassDeclaration:
                return TransformExtendsClauseWithExpression(node);
            case SyntaxKind.ObjectBindingPattern:
            case SyntaxKind.ArrayBindingPattern:
                return TransformDestructuringPatterns(node);
            default:
                return "";
        }
    }

    // codeactions_fixmissingtypeannotation.go:573-583 — addTypeToSignatureDeclaration.
    internal string AddTypeToSignatureDeclaration(Node funcNode)
    {
        if (funcNode.Type() != null)
        {
            return "";
        }
        var typeNode = InferType(funcNode, null);
        if (typeNode == null)
        {
            return "";
        }
        changeTracker.TryInsertTypeAnnotation(sourceFile, funcNode, typeNode);
        return global::Ts.Diagnostics.Diagnostics.Add_return_type_0.Localize(locale, CodeActions.TypeToStringForDiag(typeNode, sourceFile, changeTracker));
    }

    // codeactions_fixmissingtypeannotation.go:585-612 — transformExportAssignment.
    internal string TransformExportAssignment(Node defaultExport)
    {
        var exportAssignment = defaultExport.AsExportAssignment();
        if (exportAssignment.IsExportEquals)
        {
            return "";
        }

        var expression = exportAssignment.Expression!;
        var typeNode = InferType(expression, null);
        if (typeNode == null)
        {
            return "";
        }

        var factory = changeTracker.NodeFactory;

        var defaultIdentifier = changeTracker.EmitContext.Factory.NewUniqueName("_default");

        // Deep clone the expression so synthesized nodes don't reference original source
        // positions
        var clonedExpression = factory.DeepCloneNode(expression)!;

        var varDecl = factory.NewVariableDeclaration(defaultIdentifier, null, typeNode, clonedExpression);
        var varDeclList = factory.NewVariableDeclarationList(factory.NewNodeList([varDecl]), NodeFlags.Const);
        var varStmt = factory.NewVariableStatement(null, varDeclList);

        var newExport = factory.UpdateExportAssignment(defaultExport.AsExportAssignment(), defaultExport.Modifiers(), false, null, defaultIdentifier);

        changeTracker.ReplaceNodeWithNodes(sourceFile, defaultExport, [varStmt, newExport], null);
        return global::Ts.Diagnostics.Diagnostics.Extract_default_export_to_variable.Localize(locale);
    }

    // codeactions_fixmissingtypeannotation.go:614-661 — transformExtendsClauseWithExpression.
    internal string TransformExtendsClauseWithExpression(Node classDecl)
    {
        var cd = classDecl.AsClassDeclaration();
        Node? extendsClause = null;
        if (cd.HeritageClauses != null)
        {
            foreach (var clause in cd.HeritageClauses.Nodes)
            {
                if (clause.AsHeritageClause().Token == SyntaxKind.ExtendsKeyword)
                {
                    extendsClause = clause;
                    break;
                }
            }
        }
        if (extendsClause == null)
        {
            return "";
        }

        var heritageTypes = extendsClause.AsHeritageClause().Types;
        if (heritageTypes == null || heritageTypes.Nodes.Length == 0)
        {
            return "";
        }
        var heritageExpression = heritageTypes.Nodes[0];
        var expression = heritageExpression.AsExpressionWithTypeArguments().Expression!;

        var heritageTypeNode = InferType(expression, null);
        if (heritageTypeNode == null)
        {
            return "";
        }

        var factory = changeTracker.NodeFactory;

        var baseName = "Anonymous";
        if (cd.Name() != null)
        {
            baseName = cd.Name()!.Text() + "Base";
        }
        var baseClassName = changeTracker.EmitContext.Factory.NewUniqueNameEx(baseName, new global::Ts.Printer.AutoGenerateOptions { Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic });

        // Create: const <BaseName>: <type> = <expression>;
        var clonedExpression = factory.DeepCloneNode(expression)!;
        var varDecl = factory.NewVariableDeclaration(baseClassName, null, heritageTypeNode, clonedExpression);
        var varDeclList = factory.NewVariableDeclarationList(factory.NewNodeList([varDecl]), NodeFlags.Const);
        var varStmt = factory.NewVariableStatement(null, varDeclList);

        changeTracker.InsertNodeBefore(sourceFile, classDecl, varStmt, false, global::Ts.Ls.Change.LeadingTriviaOption.None);

        // Replace the heritage expression with the base class name
        changeTracker.ReplaceNode(sourceFile, heritageExpression, factory.NewExpressionWithTypeArguments(baseClassName, null), null);

        return global::Ts.Diagnostics.Diagnostics.Extract_base_class_to_variable.Localize(locale);
    }

    // codeactions_fixmissingtypeannotation.go:663-727 — transformDestructuringPatterns.
    internal string TransformDestructuringPatterns(Node bindingPattern)
    {
        var enclosingVariableDeclaration = bindingPattern.Parent!;
        if (!NodeTests.IsVariableDeclaration(enclosingVariableDeclaration))
        {
            return "";
        }
        var enclosingVarStmt = enclosingVariableDeclaration.Parent!.Parent!;
        if (!NodeTests.IsVariableStatement(enclosingVarStmt))
        {
            return "";
        }

        var initializer = enclosingVariableDeclaration.Initializer();
        if (initializer == null)
        {
            return "";
        }

        var factory = changeTracker.NodeFactory;
        var newNodes = new List<Node>();

        Node baseExprNode;
        if (!NodeTests.IsIdentifier(initializer))
        {
            // Create a temporary variable for complex expressions
            var tempName = changeTracker.EmitContext.Factory.NewUniqueNameEx("dest", new global::Ts.Printer.AutoGenerateOptions { Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic });
            var clonedInitializer = factory.DeepCloneNode(initializer)!;
            var varDecl = factory.NewVariableDeclaration(tempName, null, null, clonedInitializer);
            var varDeclList = factory.NewVariableDeclarationList(factory.NewNodeList([varDecl]), NodeFlags.Const);
            var varStmt = factory.NewVariableStatement(null, varDeclList);
            newNodes.Add(varStmt);
            baseExprNode = tempName;
        }
        else
        {
            // Use a new identifier to avoid referencing original source positions
            baseExprNode = factory.NewIdentifier(initializer.Text());
        }

        // Extract each binding element as a separate variable with type annotation
        ExtractBindingElements(bindingPattern, baseExprNode, newNodes, enclosingVarStmt);

        if (newNodes.Count == 0)
        {
            return "";
        }

        // If the enclosing variable statement has multiple declarations, preserve the
        // non-destructuring ones
        var declList = enclosingVarStmt.AsVariableStatement().DeclarationList!.AsVariableDeclarationList();
        if (declList.Declarations!.Nodes.Length > 1)
        {
            var remainingDecls = new List<Node>();
            foreach (var d in declList.Declarations.Nodes)
            {
                if (d != enclosingVariableDeclaration)
                {
                    remainingDecls.Add(d);
                }
            }
            if (remainingDecls.Count > 0)
            {
                newNodes.Add(factory.UpdateVariableStatement(
                    enclosingVarStmt.AsVariableStatement(),
                    enclosingVarStmt.AsVariableStatement().Modifiers(),
                    factory.UpdateVariableDeclarationList(
                        declList,
                        factory.NewNodeList(remainingDecls.ToArray()),
                        declList.Flags)));
            }
        }

        changeTracker.ReplaceNodeWithNodes(sourceFile, enclosingVarStmt, newNodes.ToArray(), null);
        return global::Ts.Diagnostics.Diagnostics.Extract_binding_expressions_to_variable.Localize(locale);
    }

    // codeactions_fixmissingtypeannotation.go:729-795 — extractBindingElements.
    internal void ExtractBindingElements(Node bindingPattern, Node baseExpr, List<Node> newNodes, Node enclosingVarStmt)
    {
        var factory = changeTracker.NodeFactory;

        if (NodeTests.IsObjectBindingPattern(bindingPattern))
        {
            foreach (var element in bindingPattern.AsBindingPattern().Elements!.Nodes)
            {
                if (NodeTests.IsOmittedExpression(element))
                {
                    continue;
                }
                var be = element.AsBindingElement();
                var name = be.Name();
                if (name == null)
                {
                    continue;
                }

                // Build property access expression
                Node? accessExpr;
                if (be.PropertyName != null && NodeTests.IsComputedPropertyName(be.PropertyName))
                {
                    // Handle computed property names: create a temp variable for the computed
                    // expression
                    var computedExpression = be.PropertyName.AsComputedPropertyName().Expression!;
                    var identifierForComputedProperty = changeTracker.EmitContext.Factory.NewGeneratedNameForNode(computedExpression);
                    var compVarDecl = factory.NewVariableDeclaration(identifierForComputedProperty, null, null, computedExpression);
                    var compVarDeclList = factory.NewVariableDeclarationList(factory.NewNodeList([compVarDecl]), NodeFlags.Const);
                    var compVarStmt = factory.NewVariableStatement(null, compVarDeclList);
                    newNodes.Add(compVarStmt);
                    accessExpr = factory.NewElementAccessExpression(baseExpr, null, identifierForComputedProperty, NodeFlags.None);
                }
                else if (be.PropertyName != null)
                {
                    // Use property name text (handles identifiers, string literals, numeric
                    // literals)
                    var propText = be.PropertyName.Text();
                    accessExpr = factory.NewPropertyAccessExpression(baseExpr, null, factory.NewIdentifier(propText), NodeFlags.None);
                }
                else if (NodeTests.IsIdentifier(name))
                {
                    accessExpr = factory.NewPropertyAccessExpression(baseExpr, null, factory.NewIdentifier(name.Text()), NodeFlags.None);
                }
                else
                {
                    continue;
                }

                if (AstUtilities.IsBindingPattern(name))
                {
                    ExtractBindingElements(name, accessExpr, newNodes, enclosingVarStmt);
                }
                else
                {
                    EmitBindingElementVariable(factory, name, be, accessExpr!, newNodes, enclosingVarStmt);
                }
            }
        }
        else if (NodeTests.IsArrayBindingPattern(bindingPattern))
        {
            var elements = bindingPattern.AsBindingPattern().Elements!.Nodes;
            for (var i = 0; i < elements.Length; i++)
            {
                var element = elements[i];
                if (NodeTests.IsOmittedExpression(element))
                {
                    continue;
                }
                var be = element.AsBindingElement();
                var name = be.Name();
                if (name == null)
                {
                    continue;
                }

                var accessExpr = factory.NewElementAccessExpression(baseExpr, null, factory.NewNumericLiteral(Convert.ToString(i, System.Globalization.CultureInfo.InvariantCulture), TokenFlags.None), NodeFlags.None);

                if (AstUtilities.IsBindingPattern(name))
                {
                    ExtractBindingElements(name, accessExpr, newNodes, enclosingVarStmt);
                }
                else
                {
                    EmitBindingElementVariable(factory, name, be, accessExpr, newNodes, enclosingVarStmt);
                }
            }
        }
    }

    // codeactions_fixmissingtypeannotation.go:797-844 — emitBindingElementVariable creates a
    // variable declaration for a single binding element, handling default initializers by
    // creating a ternary `temp === undefined ? default : temp`.
    internal void EmitBindingElementVariable(
        NodeFactory factory,
        Node name,
        BindingElement be,
        Node accessExpr,
        List<Node> newNodes,
        Node enclosingVarStmt)
    {
        var typeNode = InferType(name, null);
        Node variableInitializer = accessExpr;

        if (be.Initializer != null)
        {
            // Create a temp variable to hold the accessed value, then use a conditional
            // expression to apply the default: temp === undefined ? defaultValue : temp
            var propName = be.PropertyName;
            var tempBaseName = "temp";
            if (propName != null && NodeTests.IsIdentifier(propName))
            {
                tempBaseName = propName.Text();
            }
            var tempName = changeTracker.EmitContext.Factory.NewUniqueNameEx(tempBaseName, new global::Ts.Printer.AutoGenerateOptions { Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic });
            var tempVarDecl = factory.NewVariableDeclaration(tempName, null, null, variableInitializer);
            var tempVarDeclList = factory.NewVariableDeclarationList(factory.NewNodeList([tempVarDecl]), NodeFlags.Const);
            var tempVarStmt = factory.NewVariableStatement(null, tempVarDeclList);
            newNodes.Add(tempVarStmt);

            variableInitializer = factory.NewConditionalExpression(
                factory.NewBinaryExpression(
                    null,
                    tempName,
                    null,
                    factory.NewToken(SyntaxKind.EqualsEqualsEqualsToken),
                    factory.NewIdentifier("undefined")),
                factory.NewToken(SyntaxKind.QuestionToken),
                be.Initializer,
                factory.NewToken(SyntaxKind.ColonToken),
                variableInitializer);
        }

        var exportModifier = GetExportModifier(enclosingVarStmt);
        var varDecl = factory.NewVariableDeclaration(factory.NewIdentifier(name.Text()), null, typeNode, variableInitializer);
        var varDeclList = factory.NewVariableDeclarationList(factory.NewNodeList([varDecl]), NodeFlags.Const);
        var varStmt = factory.NewVariableStatement(exportModifier, varDeclList);
        newNodes.Add(varStmt);
    }

    // codeactions_fixmissingtypeannotation.go:846-852 — getExportModifier.
    internal ModifierList? GetExportModifier(Node enclosingVarStmt)
    {
        if (AstUtilities.HasSyntacticModifier(enclosingVarStmt, ModifierFlags.Export))
        {
            var exportToken = changeTracker.NodeFactory.NewToken(SyntaxKind.ExportKeyword);
            return changeTracker.NodeFactory.NewModifierList([exportToken]);
        }
        return null;
    }

    // codeactions_fixmissingtypeannotation.go:854-922 — inferType.
    internal Node? InferType(Node node, global::Ts.Checker.Type? variableType)
    {
        mutatedTarget = false;

        // Handle Relative mode first: return typeof X for identifiers
        if (typePrintMode == TypePrintMode.Relative)
        {
            return RelativeType(node);
        }

        global::Ts.Checker.Type? t;

        if (CodeActions.IsValueSignatureDeclaration(node))
        {
            var signature = checker.GetSignatureFromDeclaration(node);
            if (signature != null)
            {
                var typePredicate = checker.GetTypePredicateOfSignature(signature);
                if (typePredicate != null)
                {
                    if (typePredicate.Type() == null)
                    {
                        return null;
                    }
                    var enclosingDecl = AstUtilities.FindAncestor(node, AstUtilities.IsDeclaration);
                    if (enclosingDecl == null)
                    {
                        enclosingDecl = sourceFile.AsNode();
                    }
                    var flags = CodeActions.declarationEmitNodeBuilderFlags;
                    if ((typePredicate.Type()!.Flags & global::Ts.Checker.TypeFlags.UniqueESSymbol) != 0)
                    {
                        flags |= global::Ts.NodeBuilder.Flags.AllowUniqueESSymbolType;
                    }
                    var result = checker.TypePredicateToTypePredicateNode(typePredicate, enclosingDecl, flags, null);
                    if (result != null)
                    {
                        return result;
                    }
                    return null;
                }
                t = checker.GetReturnTypeOfSignature(signature);
            }
            else
            {
                t = null;
            }
        }
        else
        {
            t = checker.GetTypeAtLocation(node);
        }

        if (t == null)
        {
            return null;
        }

        // Handle Widened mode: return widened literal type if different
        if (typePrintMode == TypePrintMode.Widened)
        {
            if (variableType != null)
            {
                t = variableType;
            }
            var widenedType = checker.GetWidenedLiteralType(t);
            if (checker.IsTypeAssignableTo(widenedType, t))
            {
                return null; // widened type is same, no fix needed
            }
            t = widenedType;
        }

        var enclosingDeclaration = AstUtilities.FindAncestor(node, AstUtilities.IsDeclaration);
        if (enclosingDeclaration == null)
        {
            enclosingDeclaration = sourceFile.AsNode();
        }

        var builderFlags = CodeActions.declarationEmitNodeBuilderFlags | GetExtraFlags(node, t);

        // For parameters that require adding implicit undefined, add it to the type
        if (NodeTests.IsParameterDeclaration(node) && checker.RequiresAddingImplicitUndefined(node))
        {
            t = checker.GetUnionTypeEx([checker.GetUndefinedType()!, t], global::Ts.Checker.UnionReduction.None);
        }

        var typeNode = TypeToMinimizedReferenceType(t, enclosingDeclaration, builderFlags);
        return typeNode;
    }

    // codeactions_fixmissingtypeannotation.go:924-931 — getExtraFlags.
    internal global::Ts.NodeBuilder.Flags GetExtraFlags(Node node, global::Ts.Checker.Type t)
    {
        if ((NodeTests.IsVariableDeclaration(node) ||
                (NodeTests.IsPropertyDeclaration(node) && AstUtilities.HasSyntacticModifier(node, ModifierFlags.Static | ModifierFlags.Readonly))) &&
            (t.Flags & global::Ts.Checker.TypeFlags.UniqueESSymbol) != 0)
        {
            return global::Ts.NodeBuilder.Flags.AllowUniqueESSymbolType;
        }
        return global::Ts.NodeBuilder.Flags.None;
    }

    // codeactions_fixmissingtypeannotation.go:933-938 — createTypeOfFromEntityNameExpression
    // creates a `typeof X` type query node.
    internal Node CreateTypeOfFromEntityNameExpression(Node node)
    {
        return changeTracker.NodeFactory.NewTypeQueryNode(
            changeTracker.NodeFactory.DeepCloneNode(node)!,
            null);
    }

    // codeactions_fixmissingtypeannotation.go:940-973 — typeFromArraySpreadElements decomposes an
    // array literal with spread elements into separate variables, returning a tuple type of
    // typeof references.
    internal Node? TypeFromArraySpreadElements(Node node, string name)
    {
        var isInConstContext = AstUtilities.FindAncestor(node, CodeActions.IsConstAssertion) != null;
        if (!isInConstContext)
        {
            return null;
        }
        if (name.Length == 0)
        {
            name = "temp";
        }
        var factory = changeTracker.NodeFactory;
        return TypeFromSpreads(
            node,
            name,
            isInConstContext,
            n => n.AsArrayLiteralExpression().Elements!.Nodes,
            NodeTests.IsSpreadElement,
            expr => factory.NewSpreadElement(expr),
            elements => factory.NewArrayLiteralExpression(factory.NewNodeList(elements), true),
            types =>
            {
                var restTypes = new Node[types.Length];
                for (var i = 0; i < types.Length; i++)
                {
                    restTypes[i] = factory.NewRestTypeNode(types[i]);
                }
                return factory.NewTupleTypeNode(factory.NewNodeList(restTypes));
            });
    }

    // codeactions_fixmissingtypeannotation.go:975-1004 — typeFromObjectSpreadAssignment decomposes
    // an object literal with spread assignments into separate variables, returning an
    // intersection type of typeof references.
    internal Node? TypeFromObjectSpreadAssignment(Node node, string name)
    {
        var isInConstContext = AstUtilities.FindAncestor(node, CodeActions.IsConstAssertion) != null;
        if (name.Length == 0)
        {
            name = "temp";
        }
        var factory = changeTracker.NodeFactory;
        return TypeFromSpreads(
            node,
            name,
            isInConstContext,
            n => n.AsObjectLiteralExpression().Properties != null ? n.AsObjectLiteralExpression().Properties!.Nodes : null,
            NodeTests.IsSpreadAssignment,
            expr => factory.NewSpreadAssignment(expr),
            elements => factory.NewObjectLiteralExpression(factory.NewNodeList(elements), true),
            types => factory.NewIntersectionTypeNode(factory.NewNodeList(types)));
    }

    // codeactions_fixmissingtypeannotation.go:1006-1050 — typeFromSpreads is the generic spread
    // decomposition function, ported from TS's typeFromSpreads. It splits a literal with spread
    // elements into separate const variables and returns a composed type.
    internal Node? TypeFromSpreads(
        Node node,
        string name,
        bool isInConstContext,
        Func<Node, Node[]?> getChildren,
        Func<Node, bool> isSpread,
        Func<Node, Node> createSpread,
        Func<Node[], Node> makeNodeOfKind,
        Func<Node[], Node> finalType)
    {
        var factory = changeTracker.NodeFactory;
        var intersectionTypes = new List<Node>();
        var newSpreads = new List<Node>();
        var currentVariableProperties = new List<Node>();

        var statement = AstUtilities.FindAncestor(node, AstUtilities.IsStatement);

        var children = getChildren(node) ?? [];
        foreach (var prop in children)
        {
            if (isSpread(prop))
            {
                FinalizesVariablePart(factory, name, isInConstContext, statement, makeNodeOfKind, createSpread, currentVariableProperties, intersectionTypes, newSpreads);
                if (AstUtilities.IsEntityNameExpression(prop.Expression()!))
                {
                    intersectionTypes.Add(CreateTypeOfFromEntityNameExpression(prop.Expression()!));
                    newSpreads.Add(prop);
                }
                else
                {
                    MakeSpreadVariable(factory, name, isInConstContext, statement, createSpread, prop.Expression()!, intersectionTypes, newSpreads);
                }
            }
            else
            {
                currentVariableProperties.Add(prop);
            }
        }

        if (newSpreads.Count == 0)
        {
            return null;
        }

        FinalizesVariablePart(factory, name, isInConstContext, statement, makeNodeOfKind, createSpread, currentVariableProperties, intersectionTypes, newSpreads);

        changeTracker.ReplaceNode(sourceFile, node, makeNodeOfKind(newSpreads.ToArray()), null);
        mutatedTarget = true;

        return finalType(intersectionTypes.ToArray());
    }

    // codeactions_fixmissingtypeannotation.go:1052-1086 — makeSpreadVariable creates a const
    // variable for a spread expression and adds it to the decomposition.
    internal void MakeSpreadVariable(
        NodeFactory factory,
        string name,
        bool isInConstContext,
        Node? statement,
        Func<Node, Node> createSpread,
        Node expression,
        List<Node> intersectionTypes,
        List<Node> newSpreads)
    {
        var tempName = changeTracker.EmitContext.Factory.NewUniqueNameEx(
            name + "_Part" + Convert.ToString(newSpreads.Count + 1, System.Globalization.CultureInfo.InvariantCulture),
            new global::Ts.Printer.AutoGenerateOptions { Flags = global::Ts.Printer.GeneratedIdentifierFlags.Optimistic });

        Node initializer;
        if (!isInConstContext)
        {
            initializer = factory.DeepCloneNode(expression)!;
        }
        else
        {
            var constRef = factory.NewTypeReferenceNode(factory.NewIdentifier("const"), null);
            initializer = factory.NewAsExpression(factory.DeepCloneNode(expression)!, constRef);
        }

        var varDecl = factory.NewVariableDeclaration(tempName, null, null, initializer);
        var varDeclList = factory.NewVariableDeclarationList(factory.NewNodeList([varDecl]), NodeFlags.Const);
        var varStmt = factory.NewVariableStatement(null, varDeclList);

        if (statement != null)
        {
            changeTracker.InsertNodeBefore(sourceFile, statement, varStmt, false, global::Ts.Ls.Change.LeadingTriviaOption.None);
        }

        intersectionTypes.Add(CreateTypeOfFromEntityNameExpression(tempName));
        newSpreads.Add(createSpread(tempName));
    }

    // codeactions_fixmissingtypeannotation.go:1088-1104 — finalizesVariablePart finalizes
    // accumulated non-spread properties into a variable.
    internal void FinalizesVariablePart(
        NodeFactory factory,
        string name,
        bool isInConstContext,
        Node? statement,
        Func<Node[], Node> makeNodeOfKind,
        Func<Node, Node> createSpread,
        List<Node> currentVariableProperties,
        List<Node> intersectionTypes,
        List<Node> newSpreads)
    {
        if (currentVariableProperties.Count > 0)
        {
            MakeSpreadVariable(factory, name, isInConstContext, statement, createSpread, makeNodeOfKind(currentVariableProperties.ToArray()), intersectionTypes, newSpreads);
            currentVariableProperties.Clear();
        }
    }

    // codeactions_fixmissingtypeannotation.go:1115-1166 — relativeType creates a typeof
    // expression for a node, used in TypePrintMode.Relative. Instead of spelling out the full
    // type, returns `typeof X` for identifiers. For object/array literals with spreads,
    // decomposes into separate variables.
    internal Node? RelativeType(Node node)
    {
        if (NodeTests.IsParameterDeclaration(node))
        {
            return null;
        }
        if (NodeTests.IsShorthandPropertyAssignment(node))
        {
            return CreateTypeOfFromEntityNameExpression(node.AsShorthandPropertyAssignment().Name()!);
        }
        if (AstUtilities.IsEntityNameExpression(node))
        {
            return CreateTypeOfFromEntityNameExpression(node);
        }
        if (CodeActions.IsConstAssertion(node))
        {
            return RelativeType(node.Expression()!);
        }
        if (NodeTests.IsArrayLiteralExpression(node))
        {
            var varDecl = AstUtilities.FindAncestorKind(node, SyntaxKind.VariableDeclaration);
            var partName = "";
            if (varDecl != null && NodeTests.IsIdentifier(varDecl.Name()!))
            {
                partName = varDecl.Name()!.AsIdentifier().Text;
            }
            return TypeFromArraySpreadElements(node.AsArrayLiteralExpression(), partName);
        }
        if (NodeTests.IsObjectLiteralExpression(node))
        {
            var varDecl = AstUtilities.FindAncestorKind(node, SyntaxKind.VariableDeclaration);
            var partName = "";
            if (varDecl != null && NodeTests.IsIdentifier(varDecl.Name()!))
            {
                partName = varDecl.Name()!.AsIdentifier().Text;
            }
            return TypeFromObjectSpreadAssignment(node.AsObjectLiteralExpression(), partName);
        }
        if (NodeTests.IsVariableDeclaration(node) && node.Initializer() != null)
        {
            return RelativeType(node.Initializer()!);
        }
        if (NodeTests.IsConditionalExpression(node))
        {
            var cond = node.AsConditionalExpression();
            var trueType = RelativeType(cond.WhenTrue!);
            if (trueType == null)
            {
                return null;
            }
            var trueMutated = mutatedTarget;
            var falseType = RelativeType(cond.WhenFalse!);
            if (falseType == null)
            {
                return null;
            }
            mutatedTarget = trueMutated || mutatedTarget;
            var factory = changeTracker.NodeFactory;
            return factory.NewUnionTypeNode(factory.NewNodeList([trueType, falseType]));
        }
        return null;
    }

    // codeactions_fixmissingtypeannotation.go:1168-1203 — typeToMinimizedReferenceType converts a
    // type to a type node, then trims trailing type arguments that match their defaults. Ported
    // from TS's services/codefixes/helpers.ts typeToMinimizedReferenceType.
    internal Node? TypeToMinimizedReferenceType(global::Ts.Checker.Type t, Node enclosingDecl, global::Ts.NodeBuilder.Flags flags)
    {
        var idToSymbol = new Dictionary<Node, global::Ts.Ast.Symbol>(global::Ts.Core.ReferenceEqualityComparer<Node>.Instance);
        // !!! When truncation tracking is supported, check if the type was truncated
        // and return factory.NewKeywordTypeNode(ast.KindAnyKeyword) instead of the truncated node.
        var typeNode = checker.TypeToTypeNodeEx(t, enclosingDecl, flags, global::Ts.NodeBuilder.InternalFlags.WriteComputedProps, idToSymbol);
        if (typeNode == null)
        {
            return null;
        }
        if (NodeTests.IsTypeReferenceNode(typeNode) && (t.ObjectFlags & global::Ts.Checker.ObjectFlags.Reference) != 0)
        {
            var typeArgs = checker.GetTypeArguments(t);
            var nodeTypeArgs = typeNode.TypeArguments(); // Node[] (array, not NodeList)
            if (typeArgs.Length > 0 && (nodeTypeArgs?.Length ?? 0) > 0)
            {
                var cutoff = CodeActions.EndOfRequiredTypeParameters(checker, t);
                if (cutoff < nodeTypeArgs!.Length)
                {
                    // Trim trailing default type arguments
                    var trimmedArgs = changeTracker.NodeFactory.NewNodeList(nodeTypeArgs[..cutoff]);
                    typeNode = changeTracker.NodeFactory.UpdateTypeReferenceNode(
                        typeNode.AsTypeReferenceNode(),
                        typeNode.AsTypeReferenceNode().TypeName,
                        trimmedArgs);
                }
            }
        }
        // Convert import type references (e.g. import("./path").Name) to simple type references
        // and collect symbols that need to be imported
        var (referenceTypeNode, importableSymbols) = global::Ts.Ls.AutoImport.AutoImportApi.TryGetAutoImportableReferenceFromTypeNode(typeNode, idToSymbol);
        if (referenceTypeNode != null)
        {
            typeNode = referenceTypeNode;
            symbolsToImport.AddRange(importableSymbols);
        }
        return typeNode;
    }

    // codeactions_fixmissingtypeannotation.go:1258-1275 — addTypeToVariableLike.
    internal string AddTypeToVariableLike(Node decl)
    {
        var typeNode = InferType(decl, null);
        if (typeNode == null)
        {
            return "";
        }
        if (decl.Type() != null)
        {
            changeTracker.ReplaceNode(sourceFile, decl.Type()!, typeNode, null);
        }
        else
        {
            changeTracker.TryInsertTypeAnnotation(sourceFile, decl, typeNode);
            // Parenthesize paren-less arrow function parameters (`x => ...`) so the inserted
            // `: T` produces `(x: T) => ...` instead of the invalid `x: T => ...`. Queued after
            // the type annotation so that the `)` edit at param.End() sorts after the annotation
            // insertion.
            if (NodeTests.IsParameterDeclaration(decl) && decl.Parent != null && NodeTests.IsArrowFunction(decl.Parent))
            {
                changeTracker.ParenthesizeArrowParameters(sourceFile, decl.Parent);
            }
        }
        return global::Ts.Diagnostics.Diagnostics.Add_annotation_of_type_0.Localize(locale, CodeActions.TypeToStringForDiag(typeNode, sourceFile, changeTracker));
    }

    // codeactions_fixmissingtypeannotation.go:1377-1419 — addSymbolToExistingImport finds the
    // existing import declaration for the symbol's module and adds the symbol name to the named
    // imports.
    internal void AddSymbolToExistingImport(global::Ts.Ast.Symbol? sym)
    {
        if (sym == null || sym.Parent == null)
        {
            return;
        }

        // Find the module specifier for this symbol
        var moduleSymbol = sym.Parent;
        var symbolName = sym.Name;

        // Walk the source file's import declarations to find the one importing from the same
        // module
        foreach (var stmt in sourceFile.Statements!.Nodes)
        {
            if (!NodeTests.IsImportDeclaration(stmt))
            {
                continue;
            }
            var importDecl = stmt.AsImportDeclaration();
            if (importDecl.ImportClause == null)
            {
                continue;
            }

            // Check if this import is from the same module
            var importModuleSymbol = checker.GetSymbolAtLocation(importDecl.ModuleSpecifier!);
            if (importModuleSymbol == null || checker.GetMergedSymbol(importModuleSymbol) != checker.GetMergedSymbol(moduleSymbol))
            {
                continue;
            }

            // Found the matching import - add the symbol to named imports
            var importClause = importDecl.ImportClause.AsImportClause();
            if (importClause.NamedBindings != null && NodeTests.IsNamedImports(importClause.NamedBindings))
            {
                // Add to existing named imports
                var existingElements = importClause.NamedBindings.AsNamedImports().Elements!.Nodes;
                var factory = changeTracker.NodeFactory;
                var newSpecifier = factory.NewImportSpecifier(false, null, factory.NewIdentifier(symbolName));
                var newElements = new List<Node>(existingElements) { newSpecifier };
                var newNamedImports = factory.NewNamedImports(factory.NewNodeList(newElements.ToArray()));
                var newImportClause = factory.UpdateImportClause(importClause, importClause.PhaseModifier, importClause.Name(), newNamedImports);
                var newImportDecl = factory.UpdateImportDeclaration(importDecl, importDecl.Modifiers(), newImportClause, importDecl.ModuleSpecifier, importDecl.Attributes);
                changeTracker.ReplaceNode(sourceFile, stmt, newImportDecl, null);
            }
            return;
        }
    }
}

public sealed partial class LanguageService
{
    // codeactions_fixmissingtypeannotation.go:88-126 — getIsolatedDeclarationsCodeActions.
    internal static CodeAction[]? GetIsolatedDeclarationsCodeActionsPort(System.Threading.CancellationToken ctx, CodeFixContext fixContext)
    {
        var (ch, done) = fixContext.Program.GetTypeCheckerForFile(ctx, fixContext.SourceFile);
        try
        {
            var fixes = new List<CodeAction>();

            void AddFix(CodeAction? action)
            {
                if (action == null)
                {
                    return;
                }
                fixes.Add(action);
            }

            // Match TS ordering: Full annotation, Relative annotation, Widened annotation,
            // Full inline, Relative inline, Widened inline, Full extract
            var modes = new[] { TypePrintMode.Full, TypePrintMode.Relative, TypePrintMode.Widened };

            foreach (var mode in modes)
            {
                AddFix(TryCodeAction(ctx, fixContext, ch, f =>
                {
                    f.typePrintMode = mode;
                    return f.AddTypeAnnotation(fixContext.Span);
                }));
            }

            foreach (var mode in modes)
            {
                AddFix(TryCodeAction(ctx, fixContext, ch, f =>
                {
                    f.typePrintMode = mode;
                    return f.AddInlineAssertion(fixContext.Span);
                }));
            }

            // extractAsVariable only in Full mode
            AddFix(TryCodeAction(ctx, fixContext, ch, f =>
            {
                f.typePrintMode = TypePrintMode.Full;
                return f.ExtractAsVariable(fixContext.Span);
            }));

            return fixes.ToArray();
        }
        finally
        {
            done();
        }
    }

    // codeactions_fixmissingtypeannotation.go:128-166 — getAllIsolatedDeclarationsCodeActions.
    internal static CombinedCodeActions? GetAllIsolatedDeclarationsCodeActionsPort(System.Threading.CancellationToken ctx, CodeFixContext fixContext)
    {
        var (ch, done) = fixContext.Program.GetTypeCheckerForFile(ctx, fixContext.SourceFile);
        try
        {
            var changeTracker = global::Ts.Ls.Change.Tracker.NewTracker(fixContext.Program.Options(), fixContext.LS.FormatOptions(), fixContext.LS.Converters());

            var fixer = new IsolatedDeclarationsFixer
            {
                sourceFile = fixContext.SourceFile,
                program = fixContext.Program,
                checker = ch,
                changeTracker = changeTracker,
                locale = global::Ts.Locale.Locale.Default,
                fixedNodes = new Dictionary<Node, bool>(global::Ts.Core.ReferenceEqualityComparer<Node>.Instance),
                typePrintMode = TypePrintMode.Full,
            };

            var allDiags = GetAllDiagnostics(ctx, fixContext.Program, fixContext.SourceFile);
            foreach (var diag in allDiags)
            {
                if (CodeActions.ContainsErrorCode(CodeActions.isolatedDeclarationsFixErrorCodes, diag.Code()))
                {
                    var span = new TextRange(diag.Loc().Pos, diag.Loc().End);
                    fixer.AddTypeAnnotation(span);
                }
            }

            foreach (var sym in fixer.symbolsToImport)
            {
                fixer.AddSymbolToExistingImport(sym);
            }

            var changes = changeTracker.GetChanges();
            changes.TryGetValue(fixContext.SourceFile.FileName(), out var fileChanges);
            if (fileChanges == null || fileChanges.Count == 0)
            {
                return null;
            }

            return new CombinedCodeActions
            {
                Description = global::Ts.Diagnostics.Diagnostics.Add_all_missing_type_annotations.Localize(global::Ts.Locale.Locale.Default),
                Changes = fileChanges.ToArray(),
            };
        }
        finally
        {
            done();
        }
    }

    // codeactions_fixmissingtypeannotation.go:168-213 — tryCodeAction.
    private static CodeAction? TryCodeAction(System.Threading.CancellationToken ctx, CodeFixContext fixContext, global::Ts.Checker.Checker ch, Func<IsolatedDeclarationsFixer, string> fn)
    {
        var changeTracker = global::Ts.Ls.Change.Tracker.NewTracker(fixContext.Program.Options(), fixContext.LS.FormatOptions(), fixContext.LS.Converters());

        global::Ts.Ls.AutoImport.ImportAdder? importAdder = null;
        // importAdder may be nil if the auto-import registry is not available;
        // type node transformation still works without it, just without adding imports.

        var fixer = new IsolatedDeclarationsFixer
        {
            sourceFile = fixContext.SourceFile,
            program = fixContext.Program,
            checker = ch,
            changeTracker = changeTracker,
            importAdder = importAdder,
            locale = global::Ts.Locale.Locale.Default,
            fixedNodes = new Dictionary<Node, bool>(global::Ts.Core.ReferenceEqualityComparer<Node>.Instance),
        };

        var description = fn(fixer);
        if (description.Length == 0)
        {
            return null;
        }

        // Add any symbols that need to be imported to existing import declarations
        foreach (var sym in fixer.symbolsToImport)
        {
            fixer.AddSymbolToExistingImport(sym);
        }

        var changes = changeTracker.GetChanges();
        changes.TryGetValue(fixContext.SourceFile.FileName(), out var fileChanges);

        // Add import edits if import adder has fixes
        if (importAdder != null && importAdder.HasFixes())
        {
            fileChanges = [.. fileChanges ?? [], .. importAdder.Edits() ?? []];
        }

        if (fileChanges == null || fileChanges.Count == 0)
        {
            return null;
        }

        return new CodeAction
        {
            Description = description,
            Changes = fileChanges.ToArray(),
            FixID = CodeActions.fixMissingTypeAnnotationOnExportsFixID,
            FixAllDescription = global::Ts.Diagnostics.Diagnostics.Add_all_missing_type_annotations.Localize(global::Ts.Locale.Locale.Default),
        };
    }
}

