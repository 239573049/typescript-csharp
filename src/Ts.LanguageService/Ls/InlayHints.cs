// Ported from tsgo internal/ls/inlay_hints.go.
//
// PORT-NOTE: the Go entry point takes lsproto.InlayHintParams (LSP range + URI) and reads
// l.UserPreferences().InlayHints. The C# foundation's position currency is the raw UTF-16
// offset and the shared UserPreferences does not carry the InlayHints field yet, so the
// entry takes fileName + an LSP range + an explicit InlayHintsPreferences (null = Go's
// zero-value default: every hint kind disabled — see InlayHints.Support.cs). The quote
// preference rides the shared LsDeps.GetQuotePreference. checker.GetTypePredicateOfSignature,
// checker.GetReturnTypeOfSignature and checker.TypePredicateToTypePredicateNode reach the
// C# port through the CheckerLsExports public re-exports (Checker/Checker.LsExports.cs —
// exports.go / printer.go pass-throughs).
// ctx.Err() checks map to CancellationToken.IsCancellationRequested. Positions are UTF-16
// code units.

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Ast; // instance-syntax resolution of the Ts.Ast.NodeAccessors extension methods
using Ts.Checker; // CheckerLsExports extension methods (GetTypePredicateOfSignature, GetReturnTypeOfSignature, TypePredicateToTypePredicateNode)
using Ts.Core; // Tristate.IsTrue / IsTrueOrUnknown extension methods
using InlayHint = Ts.LsProto.InlayHint;
using InlayHintKind = Ts.LsProto.InlayHintKind;
using InlayHintLabelPart = Ts.LsProto.InlayHintLabelPart;
using StringOrInlayHintLabelParts = Ts.LsProto.StringOrInlayHintLabelParts;

namespace Ts.Ls;

public sealed partial class LanguageService
{
    // inlay_hints.go:24 — ProvideInlayHint.
    public InlayHint[] ProvideInlayHint(
        string documentURI,
        global::Ts.LsProto.Range range,
        InlayHintsPreferences? preferences = null,
        CancellationToken cancellationToken = default)
    {
        var inlayHintPreferences = preferences ?? default;
        if (!IsAnyInlayHintEnabled(inlayHintPreferences))
        {
            return [];
        }

        var file = GetProgramAndFile(documentURI);
        var program = GetProgram();
        var quotePreference = LsDeps.GetQuotePreference(file, UserPreferences());

        var (checker, done) = host.GetTypeCheckerForFile(cancellationToken, file);
        try
        {
            var inlayHintState = new InlayHintState
            {
                cancellationToken = cancellationToken,
                span = FromLspRange(file, range),
                preferences = inlayHintPreferences,
                quotePreference = quotePreference,
                file = file,
                checker = checker,
            };
            inlayHintState.Visit(file.AsNode());
            return inlayHintState.result.ToArray();
        }
        finally
        {
            done();
        }
    }

    // inlay_hints.go:52 — inlayHintState. (Go's `converters` field dissolves into the
    // static LsDeps position codec.)
    private sealed class InlayHintState
    {
        public CancellationToken cancellationToken;
        public global::Ts.Core.TextRange span;
        public InlayHintsPreferences preferences;
        public QuotePreference quotePreference;
        public SourceFile file = null!;
        public global::Ts.Checker.Checker checker = null!;
        public readonly List<InlayHint> result = new();

        // inlay_hints.go:63 — visit.
        public bool Visit(Node? node)
        {
            if (node == null || node.End - node.Pos == 0 || (node.Flags & NodeFlags.Reparsed) != 0)
            {
                return false;
            }

            switch (node.Kind)
            {
                case SyntaxKind.ModuleDeclaration or SyntaxKind.ClassDeclaration or SyntaxKind.InterfaceDeclaration or SyntaxKind.FunctionDeclaration or SyntaxKind.ClassExpression or SyntaxKind.FunctionExpression or SyntaxKind.MethodDeclaration or SyntaxKind.ArrowFunction:
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return true;
                    }
                    break;
            }

            if (!span.Intersects(node.Loc))
            {
                return false;
            }

            if (AstUtilities.IsTypeNode(node) && !NodeTests.IsExpressionWithTypeArguments(node))
            {
                return false;
            }

            if (preferences.IncludeInlayVariableTypeHints.IsTrue() && NodeTests.IsVariableDeclaration(node))
            {
                VisitVariableLikeDeclaration(node);
            }
            else if (preferences.IncludeInlayPropertyDeclarationTypeHints.IsTrue() && NodeTests.IsPropertyDeclaration(node))
            {
                VisitVariableLikeDeclaration(node);
            }
            else if (preferences.IncludeInlayEnumMemberValueHints.IsTrue() && NodeTests.IsEnumMember(node))
            {
                VisitEnumMember(node);
            }
            else if (ShouldShowParameterNameHints(preferences) && (NodeTests.IsCallExpression(node) || NodeTests.IsNewExpression(node)))
            {
                VisitCallOrNewExpression(node);
            }
            else
            {
                if (preferences.IncludeInlayFunctionParameterTypeHints.IsTrue() &&
                    AstUtilities.IsFunctionLikeDeclaration(node) &&
                    AstUtilities.HasContextSensitiveParameters(node))
                {
                    VisitFunctionLikeForParameterType(node);
                }
                if (preferences.IncludeInlayFunctionLikeReturnTypeHints.IsTrue() &&
                    IsSignatureSupportingReturnAnnotation(node))
                {
                    VisitFunctionDeclarationLikeForReturnType(node);
                }
            }
            return node.ForEachChild(Visit);
        }

        // inlay_hints.go:108 — visitFunctionDeclarationLikeForReturnType.
        // FunctionDeclaration | MethodDeclaration | GetAccessor | FunctionExpression | ArrowFunction
        private void VisitFunctionDeclarationLikeForReturnType(Node decl)
        {
            if (NodeTests.IsArrowFunction(decl))
            {
                if (global::Ts.AstNav.Tokens.FindChildOfKind(decl, SyntaxKind.OpenParenToken, file) == null)
                {
                    return;
                }
            }

            var typeAnnotation = decl.Type();
            if (typeAnnotation != null || decl.Body() == null)
            {
                return;
            }

            var signature = checker.GetSignatureFromDeclaration(decl);
            if (signature == null)
            {
                return;
            }

            var typePredicate = checker.GetTypePredicateOfSignature(signature);

            if (typePredicate != null && typePredicate.Type() != null)
            {
                var predicateHintParts = TypePredicateToInlayHintParts(typePredicate);
                AddTypeHints(predicateHintParts, GetTypeAnnotationPosition(decl));
                return;
            }

            var returnType = checker.GetReturnTypeOfSignature(signature);
            if (IsModuleReferenceType(returnType))
            {
                return;
            }

            var hintParts = TypeToInlayHintParts(returnType);
            AddTypeHints(hintParts, GetTypeAnnotationPosition(decl));
        }

        // inlay_hints.go:142 — visitCallOrNewExpression.
        private void VisitCallOrNewExpression(Node expr)
        {
            var args = expr.Arguments();
            if (args == null || args.Length == 0)
            {
                return;
            }

            var signature = checker.GetResolvedSignature(expr);
            if (signature == null)
            {
                return;
            }

            var signatureParamPos = 0;
            foreach (var originalArg in args)
            {
                var arg = AstUtilities.SkipParentheses(originalArg);
                if (ShouldShowLiteralParameterNameHintsOnly(preferences) && !IsHintableLiteral(arg))
                {
                    signatureParamPos++;
                    continue;
                }

                var spreadArgs = 0;
                if (NodeTests.IsSpreadElement(arg))
                {
                    var spreadType = checker.GetTypeAtLocation(arg.Expression()!);
                    if (spreadType.IsTupleType())
                    {
                        var elementFlags = spreadType.Target().AsTupleType().ElementFlags();
                        var fixedLength = spreadType.Target().AsTupleType().FixedLength();
                        if (fixedLength == 0)
                        {
                            continue;
                        }
                        var firstOptionalIndex = Array.FindIndex(elementFlags, f => (f & global::Ts.Checker.ElementFlags.Required) == 0);
                        var requiredArgs = firstOptionalIndex < 0 ? fixedLength : firstOptionalIndex;
                        if (requiredArgs > 0)
                        {
                            spreadArgs = requiredArgs;
                        }
                    }
                }

                var identifierInfo = GetParameterIdentifierInfoAtPosition(signature, signatureParamPos);
                signatureParamPos = signatureParamPos + (spreadArgs > 0 ? spreadArgs : 1);
                if (identifierInfo == null)
                {
                    return;
                }

                var parameter = identifierInfo.parameter;
                var parameterName = identifierInfo.name;
                var isFirstVariadicArgument = identifierInfo.isRestParameter;
                var parameterNameNotSameAsArgument = preferences.IncludeInlayParameterNameHintsWhenArgumentMatchesName.IsTrue() ||
                    !IdentifierOrAccessExpressionPostfixMatchesParameterName(arg, parameterName);
                if (!parameterNameNotSameAsArgument && !isFirstVariadicArgument)
                {
                    continue;
                }

                if (LeadingCommentsContainsParameterName(originalArg, parameterName))
                {
                    continue;
                }

                AddParameterHints(
                    parameterName,
                    parameter,
                    global::Ts.AstNav.Tokens.GetStartOfNode(originalArg, file, false /*includeJSDoc*/),
                    isFirstVariadicArgument);
            }
        }

        // inlay_hints.go:208 — visitEnumMember.
        private void VisitEnumMember(Node member)
        {
            if (member.Initializer() != null)
            {
                return;
            }

            var enumValue = checker.GetConstantValue(member);
            if (enumValue != null)
            {
                AddEnumMemberValueHints(global::Ts.Evaluator.EvaluatorFactory.AnyToString(enumValue), member.End);
            }
        }

        // inlay_hints.go:219 — visitVariableLikeDeclaration.
        private void VisitVariableLikeDeclaration(Node decl)
        {
            if (decl.Initializer() == null &&
                !(NodeTests.IsPropertyDeclaration(decl) && (checker.GetTypeAtLocation(decl).Flags & global::Ts.Checker.TypeFlags.Any) == 0) ||
                AstUtilities.IsBindingPattern(decl.Name()) || (NodeTests.IsVariableDeclaration(decl) && !IsHintableDeclaration(decl)))
            {
                return;
            }

            var typeAnnotation = decl.Type();
            if (typeAnnotation != null)
            {
                return;
            }

            var declarationType = checker.GetTypeAtLocation(decl);
            if (IsModuleReferenceType(declarationType))
            {
                return;
            }

            var hintParts = TypeToInlayHintParts(declarationType);
            string hintText = "";
            if (hintParts.String != null)
            {
                hintText = hintParts.String;
            }
            else if (hintParts.InlayHintLabelParts != null)
            {
                var b = new System.Text.StringBuilder();
                foreach (var part in hintParts.InlayHintLabelParts)
                {
                    b.Append(part.Value);
                }
                hintText = b.ToString();
            }
            if (!preferences.IncludeInlayVariableTypeHintsWhenTypeMatchesName.IsTrue() &&
                !NodeTests.IsComputedPropertyName(decl.Name()!) &&
                global::Ts.StringUtil.StringUtil.EquateStringCaseInsensitive(decl.Name()!.Text(), hintText))
            {
                return;
            }
            AddTypeHints(hintParts, decl.Name()!.End);
        }

        // inlay_hints.go:255 — visitFunctionLikeForParameterType.
        private void VisitFunctionLikeForParameterType(Node node)
        {
            var signature = checker.GetSignatureFromDeclaration(node);
            if (signature == null)
            {
                return;
            }

            var pos = 0;
            foreach (var param in node.Parameters())
            {
                if (IsHintableDeclaration(param))
                {
                    global::Ts.Ast.Symbol? symbol;
                    if (AstUtilities.IsThisParameter(param))
                    {
                        symbol = signature.ThisParameter();
                    }
                    else
                    {
                        symbol = signature.Parameters()[pos];
                    }
                    AddParameterTypeHint(param, symbol);
                }
                if (AstUtilities.IsThisParameter(param))
                {
                    continue;
                }
                pos++;
            }
        }

        // inlay_hints.go:279 — addParameterTypeHint.
        private void AddParameterTypeHint(Node node, global::Ts.Ast.Symbol? symbol)
        {
            var typeAnnotation = node.Type();
            if (typeAnnotation != null || symbol == null)
            {
                return;
            }
            var typeHints = GetParameterDeclarationTypeHints(symbol);
            if (typeHints == null)
            {
                return;
            }
            int pos;
            if (node.QuestionToken() != null)
            {
                pos = node.QuestionToken()!.End;
            }
            else
            {
                pos = node.Name()!.End;
            }
            AddTypeHints(typeHints, pos);
        }

        // inlay_hints.go:297 — getParameterDeclarationTypeHints.
        private StringOrInlayHintLabelParts? GetParameterDeclarationTypeHints(global::Ts.Ast.Symbol symbol)
        {
            var valueDeclaration = symbol.ValueDeclaration;
            if (valueDeclaration == null || !NodeTests.IsParameterDeclaration(valueDeclaration))
            {
                return null;
            }

            var signatureParamType = checker.GetTypeOfSymbolAtLocation(symbol, valueDeclaration);
            if (IsModuleReferenceType(signatureParamType))
            {
                return null;
            }

            return TypeToInlayHintParts(signatureParamType);
        }

        // inlay_hints.go:311 — typeToInlayHintParts.
        private StringOrInlayHintLabelParts TypeToInlayHintParts(global::Ts.Checker.Type t)
        {
            var flags = global::Ts.NodeBuilder.Flags.IgnoreErrors | global::Ts.NodeBuilder.Flags.AllowUniqueESSymbolType |
                global::Ts.NodeBuilder.Flags.UseAliasDefinedOutsideCurrentScope;
            var idToSymbol = new Dictionary<Node, global::Ts.Ast.Symbol>();
            // !!! Avoid type node reuse so we collect identifier symbols.
            var typeNode = checker.TypeToTypeNode(t, null /*enclosingDeclaration*/, flags, idToSymbol);
            global::Ts.Core.Debug.Assert(typeNode != null, "should always get typenode");
            return new StringOrInlayHintLabelParts
            {
                InlayHintLabelParts = GetInlayHintLabelParts(typeNode!, idToSymbol).ToArray(),
            };
        }

        // inlay_hints.go:323 — typePredicateToInlayHintParts.
        private StringOrInlayHintLabelParts TypePredicateToInlayHintParts(global::Ts.Checker.TypePredicate typePredicate)
        {
            var flags = global::Ts.NodeBuilder.Flags.IgnoreErrors | global::Ts.NodeBuilder.Flags.AllowUniqueESSymbolType |
                global::Ts.NodeBuilder.Flags.UseAliasDefinedOutsideCurrentScope;
            var idToSymbol = new Dictionary<Node, global::Ts.Ast.Symbol>();
            // !!! Avoid type node reuse so we collect identifier symbols.
            var typeNode = checker.TypePredicateToTypePredicateNode(typePredicate, null /*enclosingDeclaration*/, flags, idToSymbol);
            global::Ts.Core.Debug.Assert(typeNode != null, "should always get typePredicateNode");
            return new StringOrInlayHintLabelParts
            {
                InlayHintLabelParts = GetInlayHintLabelParts(typeNode!, idToSymbol).ToArray(),
            };
        }

        // inlay_hints.go:335 — addTypeHints.
        private void AddTypeHints(StringOrInlayHintLabelParts hint, int position)
        {
            StringOrInlayHintLabelParts label;
            if (hint.String != null)
            {
                label = new StringOrInlayHintLabelParts { String = ": " + hint.String };
            }
            else
            {
                // Go: append([]*InlayHintLabelPart{{Value: ": "}}, *hint.InlayHintLabelParts...)
                var parts = new InlayHintLabelPart[hint.InlayHintLabelParts!.Length + 1];
                parts[0] = new InlayHintLabelPart { Value = ": " };
                hint.InlayHintLabelParts.CopyTo(parts, 1);
                label = new StringOrInlayHintLabelParts { InlayHintLabelParts = parts };
            }
            result.Add(new InlayHint
            {
                Label = label,
                Position = LsDeps.PositionToLineAndCharacter(file, position),
                Kind = InlayHintKind.Type,
                PaddingLeft = true,
            });
        }

        // inlay_hints.go:349 — addEnumMemberValueHints.
        private void AddEnumMemberValueHints(string text, int position)
        {
            result.Add(new InlayHint
            {
                Label = new StringOrInlayHintLabelParts
                {
                    String = "= " + text,
                },
                Position = LsDeps.PositionToLineAndCharacter(file, position),
                PaddingLeft = true,
            });
        }

        // inlay_hints.go:359 — addParameterHints.
        private void AddParameterHints(string text, Node? parameter, int position, bool isFirstVariadicArgument)
        {
            var hintText = (isFirstVariadicArgument ? "..." : "") + text;
            var displayParts = new InlayHintLabelPart[]
            {
                GetNodeDisplayPart(hintText, parameter!),
                new()
                {
                    Value = ":",
                },
            };
            var labelParts = new StringOrInlayHintLabelParts { InlayHintLabelParts = displayParts };

            result.Add(new InlayHint
            {
                Label = labelParts,
                Position = LsDeps.PositionToLineAndCharacter(file, position),
                Kind = InlayHintKind.Parameter,
                PaddingRight = true,
            });
        }

        // inlay_hints.go:422 — getInlayHintLabelParts.
        private List<InlayHintLabelPart> GetInlayHintLabelParts(Node node, Dictionary<Node, global::Ts.Ast.Symbol> idToSymbol)
        {
            var parts = new List<InlayHintLabelPart>();

            void VisitForDisplayParts(Node? node)
            {
                if (node == null)
                {
                    return;
                }

                var tokenString = global::Ts.Scanner.Scanner.TokenToString(node.Kind);
                if (tokenString != "")
                {
                    parts.Add(new InlayHintLabelPart { Value = tokenString });
                    return;
                }

                if (AstUtilities.IsLiteralExpression(node))
                {
                    parts.Add(new InlayHintLabelPart { Value = GetLiteralText(node) });
                    return;
                }

                switch (node.Kind)
                {
                    case SyntaxKind.Identifier:
                        {
                            var identifierText = node.Text();
                            Node? name = null;
                            if (idToSymbol.TryGetValue(node, out var symbol) && symbol != null && symbol.Declarations is { Length: > 0 })
                            {
                                name = AstUtilities.GetNameOfDeclaration(symbol.Declarations[0]);
                            }
                            if (name != null)
                            {
                                parts.Add(GetNodeDisplayPart(identifierText, name));
                            }
                            else
                            {
                                parts.Add(new InlayHintLabelPart { Value = identifierText });
                            }
                            break;
                        }
                    case SyntaxKind.QualifiedName:
                        VisitForDisplayParts(node.AsQualifiedName().Left);
                        parts.Add(new InlayHintLabelPart { Value = "." });
                        VisitForDisplayParts(node.AsQualifiedName().Right);
                        break;
                    case SyntaxKind.TypePredicate:
                        {
                            if (node.AsTypePredicateNode().AssertsModifier != null)
                            {
                                parts.Add(new InlayHintLabelPart { Value = "asserts " });
                            }
                            VisitForDisplayParts(node.AsTypePredicateNode().ParameterName);
                            if (node.Type() != null)
                            {
                                parts.Add(new InlayHintLabelPart { Value = " is " });
                                VisitForDisplayParts(node.Type());
                            }
                            break;
                        }
                    case SyntaxKind.TypeReference:
                        {
                            VisitForDisplayParts(node.AsTypeReferenceNode().TypeName);
                            if (node.TypeArguments() is { Length: > 0 } typeArguments)
                            {
                                parts.Add(new InlayHintLabelPart { Value = "<" });
                                VisitDisplayPartList(typeArguments, ",");
                                parts.Add(new InlayHintLabelPart { Value = ">" });
                            }
                            break;
                        }
                    case SyntaxKind.TypeParameter:
                        {
                            if (node.ModifierNodes() is { Length: > 0 } typeParameterModifiers)
                            {
                                VisitDisplayPartList(typeParameterModifiers, "");
                            }
                            VisitForDisplayParts(node.Name());
                            if (node.AsTypeParameterDeclaration().Constraint != null)
                            {
                                parts.Add(new InlayHintLabelPart { Value = " extends " });
                                VisitForDisplayParts(node.AsTypeParameterDeclaration().Constraint);
                            }
                            if (node.AsTypeParameterDeclaration().DefaultType != null)
                            {
                                parts.Add(new InlayHintLabelPart { Value = " = " });
                                VisitForDisplayParts(node.AsTypeParameterDeclaration().DefaultType);
                            }
                            break;
                        }
                    case SyntaxKind.Parameter:
                        {
                            if (node.ModifierNodes() is { Length: > 0 } parameterModifiers)
                            {
                                VisitDisplayPartList(parameterModifiers, " ");
                            }
                            if (node.AsParameterDeclaration().DotDotDotToken != null)
                            {
                                parts.Add(new InlayHintLabelPart { Value = "..." });
                            }
                            VisitForDisplayParts(node.Name());
                            if (node.QuestionToken() != null)
                            {
                                parts.Add(new InlayHintLabelPart { Value = "?" });
                            }
                            if (node.Type() != null)
                            {
                                parts.Add(new InlayHintLabelPart { Value = ": " });
                                VisitForDisplayParts(node.Type());
                            }
                            break;
                        }
                    case SyntaxKind.ConstructorType:
                        parts.Add(new InlayHintLabelPart { Value = "new " });
                        VisitParametersAndTypeParameters(node);
                        parts.Add(new InlayHintLabelPart { Value = " => " });
                        VisitForDisplayParts(node.Type());
                        break;
                    case SyntaxKind.TypeQuery:
                        {
                            parts.Add(new InlayHintLabelPart { Value = "typeof " });
                            VisitForDisplayParts(node.AsTypeQueryNode().ExprName);
                            if (node.TypeArguments() is { Length: > 0 } typeQueryTypeArguments)
                            {
                                parts.Add(new InlayHintLabelPart { Value = "<" });
                                VisitDisplayPartList(typeQueryTypeArguments, ", ");
                                parts.Add(new InlayHintLabelPart { Value = ">" });
                            }
                            break;
                        }
                    case SyntaxKind.TypeLiteral:
                        {
                            parts.Add(new InlayHintLabelPart { Value = "{" });
                            if (node.Members() is { Length: > 0 } members)
                            {
                                parts.Add(new InlayHintLabelPart { Value = " " });
                                VisitDisplayPartList(members, "; ");
                                parts.Add(new InlayHintLabelPart { Value = " " });
                            }
                            parts.Add(new InlayHintLabelPart { Value = "}" });
                            break;
                        }
                    case SyntaxKind.ArrayType:
                        VisitForDisplayParts(node.AsArrayTypeNode().ElementType);
                        parts.Add(new InlayHintLabelPart { Value = "[]" });
                        break;
                    case SyntaxKind.TupleType:
                        parts.Add(new InlayHintLabelPart { Value = "[" });
                        VisitDisplayPartList(node.Elements()!, ", ");
                        parts.Add(new InlayHintLabelPart { Value = "]" });
                        break;
                    case SyntaxKind.NamedTupleMember:
                        {
                            if (node.AsNamedTupleMember().DotDotDotToken != null)
                            {
                                parts.Add(new InlayHintLabelPart { Value = "..." });
                            }
                            VisitForDisplayParts(node.Name());
                            if (node.QuestionToken() != null)
                            {
                                parts.Add(new InlayHintLabelPart { Value = "?" });
                            }
                            parts.Add(new InlayHintLabelPart { Value = ": " });
                            VisitForDisplayParts(node.Type());
                            break;
                        }
                    case SyntaxKind.OptionalType:
                        VisitForDisplayParts(node.Type());
                        parts.Add(new InlayHintLabelPart { Value = "?" });
                        break;
                    case SyntaxKind.RestType:
                        parts.Add(new InlayHintLabelPart { Value = "..." });
                        VisitForDisplayParts(node.Type());
                        break;
                    case SyntaxKind.UnionType:
                        if (node.AsUnionTypeNode().Types != null)
                        {
                            VisitDisplayPartList(node.AsUnionTypeNode().Types!.Nodes, " | ");
                        }
                        break;
                    case SyntaxKind.IntersectionType:
                        if (node.AsIntersectionTypeNode().Types != null)
                        {
                            VisitDisplayPartList(node.AsIntersectionTypeNode().Types!.Nodes, " & ");
                        }
                        break;
                    case SyntaxKind.ConditionalType:
                        VisitForDisplayParts(node.AsConditionalTypeNode().CheckType);
                        parts.Add(new InlayHintLabelPart { Value = " extends " });
                        VisitForDisplayParts(node.AsConditionalTypeNode().ExtendsType);
                        parts.Add(new InlayHintLabelPart { Value = " ? " });
                        VisitForDisplayParts(node.AsConditionalTypeNode().TrueType);
                        parts.Add(new InlayHintLabelPart { Value = " : " });
                        VisitForDisplayParts(node.AsConditionalTypeNode().FalseType);
                        break;
                    case SyntaxKind.InferType:
                        parts.Add(new InlayHintLabelPart { Value = "infer " });
                        VisitForDisplayParts(node.AsInferTypeNode().TypeParameter);
                        break;
                    case SyntaxKind.ParenthesizedType:
                        parts.Add(new InlayHintLabelPart { Value = "(" });
                        VisitForDisplayParts(node.Type());
                        parts.Add(new InlayHintLabelPart { Value = ")" });
                        break;
                    case SyntaxKind.TypeOperator:
                        parts.Add(new InlayHintLabelPart { Value = global::Ts.Scanner.Scanner.TokenToString(node.AsTypeOperatorNode().Operator) });
                        VisitForDisplayParts(node.Type());
                        break;
                    case SyntaxKind.IndexedAccessType:
                        VisitForDisplayParts(node.AsIndexedAccessTypeNode().ObjectType);
                        parts.Add(new InlayHintLabelPart { Value = "[" });
                        VisitForDisplayParts(node.AsIndexedAccessTypeNode().IndexType);
                        parts.Add(new InlayHintLabelPart { Value = "]" });
                        break;
                    case SyntaxKind.MappedType:
                        {
                            parts.Add(new InlayHintLabelPart { Value = "{ " });
                            if (node.AsMappedTypeNode().ReadonlyToken != null)
                            {
                                if (node.AsMappedTypeNode().ReadonlyToken!.Kind == SyntaxKind.PlusToken)
                                {
                                    parts.Add(new InlayHintLabelPart { Value = "+" });
                                }
                                else if (node.AsMappedTypeNode().ReadonlyToken!.Kind == SyntaxKind.MinusToken)
                                {
                                    parts.Add(new InlayHintLabelPart { Value = "-" });
                                }
                                parts.Add(new InlayHintLabelPart { Value = "readonly " });
                            }
                            parts.Add(new InlayHintLabelPart { Value = "[" });
                            VisitForDisplayParts(node.AsMappedTypeNode().TypeParameter);
                            if (node.AsMappedTypeNode().NameType != null)
                            {
                                parts.Add(new InlayHintLabelPart { Value = " as " });
                                VisitForDisplayParts(node.AsMappedTypeNode().NameType);
                            }
                            parts.Add(new InlayHintLabelPart { Value = "]" });
                            if (node.QuestionToken() != null)
                            {
                                if (node.QuestionToken()!.Kind == SyntaxKind.PlusToken)
                                {
                                    parts.Add(new InlayHintLabelPart { Value = "+" });
                                }
                                else if (node.QuestionToken()!.Kind == SyntaxKind.MinusToken)
                                {
                                    parts.Add(new InlayHintLabelPart { Value = "-" });
                                }
                                parts.Add(new InlayHintLabelPart { Value = "?" });
                            }
                            parts.Add(new InlayHintLabelPart { Value = ": " });
                            if (node.Type() != null)
                            {
                                VisitForDisplayParts(node.Type());
                            }
                            parts.Add(new InlayHintLabelPart { Value = "; }" });
                            break;
                        }
                    case SyntaxKind.LiteralType:
                        VisitForDisplayParts(node.AsLiteralTypeNode().Literal);
                        break;
                    case SyntaxKind.FunctionType:
                        VisitParametersAndTypeParameters(node);
                        parts.Add(new InlayHintLabelPart { Value = " => " });
                        VisitForDisplayParts(node.Type());
                        break;
                    case SyntaxKind.ImportType:
                        {
                            if (node.AsImportTypeNode().IsTypeOf)
                            {
                                parts.Add(new InlayHintLabelPart { Value = "typeof " });
                            }
                            parts.Add(new InlayHintLabelPart { Value = "import(" });
                            VisitForDisplayParts(node.AsImportTypeNode().Argument);
                            parts.Add(new InlayHintLabelPart { Value = ")" });
                            if (node.AsImportTypeNode().Qualifier != null)
                            {
                                parts.Add(new InlayHintLabelPart { Value = "." });
                                VisitForDisplayParts(node.AsImportTypeNode().Qualifier);
                            }
                            if (node.TypeArguments() is { Length: > 0 } importTypeArguments)
                            {
                                parts.Add(new InlayHintLabelPart { Value = "<" });
                                VisitDisplayPartList(importTypeArguments, ", ");
                                parts.Add(new InlayHintLabelPart { Value = ">" });
                            }
                            break;
                        }
                    case SyntaxKind.PropertySignature:
                        {
                            if (node.ModifierNodes() is { Length: > 0 } propertySignatureModifiers)
                            {
                                VisitDisplayPartList(propertySignatureModifiers, " ");
                                parts.Add(new InlayHintLabelPart { Value = " " });
                            }
                            VisitForDisplayParts(node.Name());
                            if (node.PostfixToken() != null)
                            {
                                parts.Add(
                                    new InlayHintLabelPart
                                    {
                                        Value = global::Ts.Scanner.Scanner.TokenToString(node.PostfixToken()!.Kind),
                                    });
                            }
                            if (node.Type() != null)
                            {
                                parts.Add(new InlayHintLabelPart { Value = ": " });
                                VisitForDisplayParts(node.Type());
                            }
                            break;
                        }
                    case SyntaxKind.IndexSignature:
                        {
                            parts.Add(new InlayHintLabelPart { Value = "[" });
                            VisitDisplayPartList(node.Parameters(), ", ");
                            parts.Add(new InlayHintLabelPart { Value = "]" });
                            if (node.Type() != null)
                            {
                                parts.Add(new InlayHintLabelPart { Value = ": " });
                                VisitForDisplayParts(node.Type());
                            }
                            break;
                        }
                    case SyntaxKind.MethodSignature:
                        {
                            if (node.ModifierNodes() is { Length: > 0 } methodSignatureModifiers)
                            {
                                VisitDisplayPartList(methodSignatureModifiers, " ");
                                parts.Add(new InlayHintLabelPart { Value = " " });
                            }
                            VisitForDisplayParts(node.Name());
                            if (node.PostfixToken() != null)
                            {
                                parts.Add(
                                    new InlayHintLabelPart
                                    {
                                        Value = global::Ts.Scanner.Scanner.TokenToString(node.PostfixToken()!.Kind),
                                    });
                            }
                            VisitParametersAndTypeParameters(node);
                            if (node.Type() != null)
                            {
                                parts.Add(new InlayHintLabelPart { Value = ": " });
                                VisitForDisplayParts(node.Type());
                            }
                            break;
                        }
                    case SyntaxKind.CallSignature:
                        VisitParametersAndTypeParameters(node);
                        if (node.Type() != null)
                        {
                            parts.Add(new InlayHintLabelPart { Value = ": " });
                            VisitForDisplayParts(node.Type());
                        }
                        break;
                    case SyntaxKind.ConstructSignature:
                        parts.Add(new InlayHintLabelPart { Value = "new " });
                        VisitParametersAndTypeParameters(node);
                        if (node.Type() != null)
                        {
                            parts.Add(new InlayHintLabelPart { Value = ": " });
                            VisitForDisplayParts(node.Type());
                        }
                        break;
                    case SyntaxKind.ArrayBindingPattern:
                        parts.Add(new InlayHintLabelPart { Value = "[" });
                        VisitDisplayPartList(node.Elements()!, ", ");
                        parts.Add(new InlayHintLabelPart { Value = "]" });
                        break;
                    case SyntaxKind.ObjectBindingPattern:
                        {
                            parts.Add(new InlayHintLabelPart { Value = "{" });
                            if (node.Elements() is { Length: > 0 } bindingElements)
                            {
                                parts.Add(new InlayHintLabelPart { Value = " " });
                                VisitDisplayPartList(bindingElements, ", ");
                                parts.Add(new InlayHintLabelPart { Value = " " });
                            }
                            parts.Add(new InlayHintLabelPart { Value = "}" });
                            break;
                        }
                    case SyntaxKind.BindingElement:
                        VisitForDisplayParts(node.Name());
                        break;
                    case SyntaxKind.PrefixUnaryExpression:
                        parts.Add(
                            new InlayHintLabelPart
                            {
                                Value = global::Ts.Scanner.Scanner.TokenToString(node.AsPrefixUnaryExpression().Operator),
                            });
                        VisitForDisplayParts(node.AsPrefixUnaryExpression().Operand);
                        break;
                    case SyntaxKind.TemplateLiteralType:
                        VisitForDisplayParts(node.AsTemplateLiteralTypeNode().Head);
                        foreach (var templateSpan in node.AsTemplateLiteralTypeNode().TemplateSpans!.Nodes)
                        {
                            VisitForDisplayParts(templateSpan);
                        }
                        break;
                    case SyntaxKind.TemplateHead:
                        parts.Add(new InlayHintLabelPart { Value = GetLiteralText(node) });
                        break;
                    case SyntaxKind.TemplateLiteralTypeSpan:
                        VisitForDisplayParts(node.Type());
                        VisitForDisplayParts(node.AsTemplateLiteralTypeSpan().Literal);
                        break;
                    case SyntaxKind.TemplateMiddle or SyntaxKind.TemplateTail:
                        parts.Add(new InlayHintLabelPart { Value = GetLiteralText(node) });
                        break;
                    case SyntaxKind.ThisType:
                        parts.Add(new InlayHintLabelPart { Value = "this" });
                        break;
                    case SyntaxKind.ComputedPropertyName:
                        parts.Add(new InlayHintLabelPart { Value = "[" });
                        VisitForDisplayParts(node.Expression());
                        parts.Add(new InlayHintLabelPart { Value = "]" });
                        break;
                    case SyntaxKind.PropertyAccessExpression:
                        VisitForDisplayParts(node.Expression());
                        parts.Add(new InlayHintLabelPart { Value = "." });
                        VisitForDisplayParts(node.Name());
                        break;
                    case SyntaxKind.ElementAccessExpression:
                        VisitForDisplayParts(node.Expression());
                        parts.Add(new InlayHintLabelPart { Value = "[" });
                        VisitForDisplayParts(node.AsElementAccessExpression().ArgumentExpression);
                        parts.Add(new InlayHintLabelPart { Value = "]" });
                        break;
                    default:
                        global::Ts.Core.Debug.FailBadSyntaxKind(node);
                        break;
                }
            }

            void VisitDisplayPartList(Node[] nodes, string separator)
            {
                for (var i = 0; i < nodes.Length; i++)
                {
                    if (i > 0)
                    {
                        parts.Add(new InlayHintLabelPart { Value = separator });
                    }
                    VisitForDisplayParts(nodes[i]);
                }
            }

            void VisitParametersAndTypeParameters(Node node)
            {
                if (node.TypeParameters() is { Length: > 0 } typeParameters)
                {
                    parts.Add(new InlayHintLabelPart { Value = "<" });
                    VisitDisplayPartList(typeParameters, ", ");
                    parts.Add(new InlayHintLabelPart { Value = ">" });
                }
                parts.Add(new InlayHintLabelPart { Value = "(" });
                VisitDisplayPartList(node.Parameters(), ", ");
                parts.Add(new InlayHintLabelPart { Value = ")" });
            }

            VisitForDisplayParts(node);
            return parts;
        }

        // inlay_hints.go:768 — getNodeDisplayPart.
        private InlayHintLabelPart GetNodeDisplayPart(string text, Node node)
        {
            var nodeFile = AstUtilities.GetSourceFileOfNode(node)!;
            var pos = global::Ts.AstNav.Tokens.GetStartOfNode(node, nodeFile, false /*includeJSDoc*/);
            var end = node.End;
            return new InlayHintLabelPart
            {
                Value = text,
                Location = new global::Ts.LsProto.Location
                {
                    Uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(nodeFile.FileName()),
                    Range = LsDeps.ToLspRange(nodeFile, new global::Ts.Core.TextRange(pos, end)),
                },
            };
        }

        // inlay_hints.go:781 — getLiteralText.
        private string GetLiteralText(Node node)
        {
            switch (node.Kind)
            {
                case SyntaxKind.StringLiteral:
                    if (quotePreference == QuotePreference.Single)
                    {
                        return "'" + LsDeps.EscapeString(node.Text(), '\'') + "'";
                    }
                    return "\"" + LsDeps.EscapeString(node.Text(), '"') + "\"";
                case SyntaxKind.TemplateHead or SyntaxKind.TemplateMiddle or SyntaxKind.TemplateTail:
                    {
                        var rawText = node.RawText();
                        if (rawText == "")
                        {
                            rawText = LsDeps.EscapeString(node.Text(), '`');
                        }
                        switch (node.Kind)
                        {
                            case SyntaxKind.TemplateHead:
                                return "`" + rawText + "${";
                            case SyntaxKind.TemplateMiddle:
                                return "}" + rawText + "${";
                            case SyntaxKind.TemplateTail:
                                return "}" + rawText + "`";
                        }
                        break;
                    }
            }
            return node.Text();
        }

        // inlay_hints.go:805 — parameterInfo.
        private sealed record ParameterInfo(Node parameter, string name, bool isRestParameter);

        // inlay_hints.go:811 — getParameterIdentifierInfoAtPosition.
        private ParameterInfo? GetParameterIdentifierInfoAtPosition(global::Ts.Checker.Signature signature, int pos)
        {
            var parameters = signature.Parameters();
            var paramCount = parameters.Length - (signature.HasRestParameter() ? 1 : 0);
            if (pos < paramCount)
            {
                var param = parameters[pos];
                var paramId = GetParameterDeclarationIdentifier(param);
                if (paramId == null)
                {
                    return null;
                }
                return new ParameterInfo(
                    paramId,
                    paramId.Text(),
                    false);
            }

            global::Ts.Ast.Symbol? restParameter = null;
            Node? restId = null;
            if (paramCount < parameters.Length)
            {
                restParameter = parameters[paramCount];
                restId = GetParameterDeclarationIdentifier(restParameter);
            }
            if (restId == null)
            {
                return null;
            }

            var restType = checker.GetTypeOfSymbol(restParameter!);
            if (restType.IsTupleType())
            {
                var targetTuple = restType.Target().AsTupleType();
                var elementInfos = targetTuple.ElementInfos();
                var associatedNames = new List<Node?>(elementInfos.Length);
                foreach (var elementInfo in elementInfos)
                {
                    var labeledElement = elementInfo.LabeledDeclaration();
                    associatedNames.Add(labeledElement);
                }
                var index = pos - paramCount;
                if (index < associatedNames.Count)
                {
                    var associatedName = associatedNames[index];
                    if (associatedName != null)
                    {
                        global::Ts.Core.Debug.Assert(NodeTests.IsIdentifier(associatedName.Name()!));
                        bool isRestTupleElement;
                        if (NodeTests.IsNamedTupleMember(associatedName))
                        {
                            isRestTupleElement = associatedName.AsNamedTupleMember().DotDotDotToken != null;
                        }
                        else
                        {
                            isRestTupleElement = associatedName.AsParameterDeclaration().DotDotDotToken != null;
                        }
                        return new ParameterInfo(
                            associatedName.Name()!,
                            associatedName.Name()!.Text(),
                            isRestTupleElement);
                    }
                }

                return null;
            }

            if (pos == paramCount)
            {
                return new ParameterInfo(
                    restId,
                    restParameter!.Name,
                    true);
            }
            return null;
        }

        // inlay_hints.go:876 — getParameterDeclarationIdentifier.
        private static Node? GetParameterDeclarationIdentifier(global::Ts.Ast.Symbol symbol)
        {
            if (symbol.ValueDeclaration != null && NodeTests.IsParameterDeclaration(symbol.ValueDeclaration) && NodeTests.IsIdentifier(symbol.ValueDeclaration.Name()!))
            {
                return symbol.ValueDeclaration.Name();
            }
            return null;
        }

        // inlay_hints.go:883 — identifierOrAccessExpressionPostfixMatchesParameterName.
        private static bool IdentifierOrAccessExpressionPostfixMatchesParameterName(Node expr, string parameterName)
        {
            if (NodeTests.IsIdentifier(expr))
            {
                return expr.Text() == parameterName;
            }
            if (NodeTests.IsPropertyAccessExpression(expr))
            {
                return expr.Name()!.Text() == parameterName;
            }
            return false;
        }

        // inlay_hints.go:893 — leadingCommentsContainsParameterName.
        private bool LeadingCommentsContainsParameterName(Node node, string name)
        {
            if (!global::Ts.Scanner.Scanner.IsIdentifierText(name, file.LanguageVariant))
            {
                return false;
            }

            var ranges = LsDeps.GetLeadingCommentRangesOfNode(node, file);
            var fileText = file.Text();
            foreach (var r in ranges)
            {
                var commentText = TrimCommentDelimiterRunes(fileText[r.Pos..r.End]);
                if (commentText == name)
                {
                    return true;
                }
            }

            return false;
        }

        // Go strings.TrimFunc(fileText[...], func(r rune) bool { return unicode.IsSpace(r) || r == '/' || r == '*' }).
        private static string TrimCommentDelimiterRunes(string s)
        {
            var start = 0;
            var end = s.Length;
            while (start < end && (char.IsWhiteSpace(s[start]) || s[start] == '/' || s[start] == '*'))
            {
                start++;
            }
            while (end > start && (char.IsWhiteSpace(s[end - 1]) || s[end - 1] == '/' || s[end - 1] == '*'))
            {
                end--;
            }
            return s[start..end];
        }

        // inlay_hints.go:912 — getTypeAnnotationPosition.
        private int GetTypeAnnotationPosition(Node decl)
        {
            var closeParenToken = global::Ts.AstNav.Tokens.FindChildOfKind(decl, SyntaxKind.CloseParenToken, file);
            if (closeParenToken != null)
            {
                return closeParenToken.End;
            }
            return decl.ParameterList()!.End;
        }
    }

    // inlay_hints.go:377 — shouldShowParameterNameHints.
    private static bool ShouldShowParameterNameHints(InlayHintsPreferences preferences)
    {
        return preferences.IncludeInlayParameterNameHints == IncludeInlayParameterNameHints.Literals ||
            preferences.IncludeInlayParameterNameHints == IncludeInlayParameterNameHints.All;
    }

    // inlay_hints.go:382 — shouldShowLiteralParameterNameHintsOnly.
    private static bool ShouldShowLiteralParameterNameHintsOnly(InlayHintsPreferences preferences)
    {
        return preferences.IncludeInlayParameterNameHints == IncludeInlayParameterNameHints.Literals;
    }

    // inlay_hints.go:387 — isSignatureSupportingReturnAnnotation.
    // node is FunctionDeclaration | ArrowFunction | FunctionExpression | MethodDeclaration | GetAccessor
    private static bool IsSignatureSupportingReturnAnnotation(Node node)
    {
        return NodeTests.IsArrowFunction(node) || NodeTests.IsFunctionExpression(node) || NodeTests.IsFunctionDeclaration(node) ||
            NodeTests.IsMethodDeclaration(node) || NodeTests.IsGetAccessorDeclaration(node);
    }

    // inlay_hints.go:392 — isHintableDeclaration.
    private static bool IsHintableDeclaration(Node node)
    {
        if ((AstUtilities.IsPartOfParameterDeclaration(node) || NodeTests.IsVariableDeclaration(node) && AstUtilities.IsVarConst(node)) &&
            node.Initializer() != null)
        {
            var initializer = AstUtilities.SkipParentheses(node.Initializer()!);
            return !(IsHintableLiteral(initializer) || NodeTests.IsNewExpression(initializer) ||
                NodeTests.IsObjectLiteralExpression(initializer) || AstUtilities.IsAssertionExpression(initializer));
        }
        return true;
    }

    // inlay_hints.go:402 — isHintableLiteral.
    private static bool IsHintableLiteral(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.PrefixUnaryExpression:
                {
                    var operand = node.AsPrefixUnaryExpression().Operand;
                    return AstUtilities.IsLiteralExpression(operand!) || NodeTests.IsIdentifier(operand!) && IsInfinityOrNaNString(operand!.Text());
                }
            case SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword or SyntaxKind.NullKeyword or SyntaxKind.NoSubstitutionTemplateLiteral or SyntaxKind.TemplateExpression:
                return true;
            case SyntaxKind.Identifier:
                {
                    var name = node.Text();
                    return name == "undefined" || IsInfinityOrNaNString(name);
                }
        }
        return AstUtilities.IsLiteralExpression(node);
    }

    // inlay_hints.go:417 — isModuleReferenceType.
    private static bool IsModuleReferenceType(global::Ts.Checker.Type t)
    {
        var symbol = t.Symbol;
        return symbol != null && (symbol.Flags & SymbolFlags.Module) != 0;
    }

    // inlay_hints.go:920 — isAnyInlayHintEnabled.
    private static bool IsAnyInlayHintEnabled(InlayHintsPreferences preferences)
    {
        return preferences.IncludeInlayParameterNameHints != IncludeInlayParameterNameHints.None ||
            preferences.IncludeInlayFunctionParameterTypeHints.IsTrue() ||
            preferences.IncludeInlayVariableTypeHints.IsTrue() ||
            preferences.IncludeInlayPropertyDeclarationTypeHints.IsTrue() ||
            preferences.IncludeInlayFunctionLikeReturnTypeHints.IsTrue() ||
            preferences.IncludeInlayEnumMemberValueHints.IsTrue();
    }
}
