// Ported from tsgo internal/ls/string_completions.go — the type-side half: the completion
// model (:30-51), getStringLiteralCompletions/convert* (:53-255), the entries switch
// (:256-431), the fromTypes/fromProperties builders (:433-578) and the trailing type helpers
// (:1882-2015); plus skipConstraint (utilities.go:1015-1023).
// PORT-NOTE: the path/module-specifier half (:580-1890 — module names, node_modules
// enumeration, paths patterns) and the triple-slash reference tail (:2076-2211) are the next
// batch; their two call sites here are stubbed to Go's nil path until then.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using CheckerType = Ts.Checker.Type;
using TsChecker = Ts.Checker.Checker;

namespace Ts.Ls;

// string_completions.go:30-33 — completionsFromTypes.
internal sealed class CompletionsFromTypes
{
    internal CheckerType[] types = [];
    internal bool isNewIdentifier;
}

// string_completions.go:35-38 — completionsFromProperties.
internal sealed class CompletionsFromProperties
{
    internal Symbol[] symbols = [];
    internal bool hasIndexSignature;
}

// string_completions.go:40-45 — pathCompletion.
internal sealed class PathCompletion
{
    internal string name = "";
    internal ScriptElementKind kind;
    internal string extension = "";
    internal global::Ts.Core.TextRange? textRange;
}

// string_completions.go:47-51 — stringLiteralCompletions (3-arm union).
internal sealed class StringLiteralCompletionsResult
{
    internal CompletionsFromTypes? fromTypes;
    internal CompletionsFromProperties? fromProperties;
    internal List<PathCompletion>? fromPaths;
}

public sealed partial class LanguageService
{
    // string_completions.go:53-89 — getStringLiteralCompletions (real body; replaces the
    // dispatcher-side stub).
    internal CompletionList? GetStringLiteralCompletionsWorker(
        SourceFile file,
        int position,
        Node? contextToken,
        TsChecker checker,
        global::Ts.Core.CompilerOptions compilerOptions,
        bool includeSymbols)
    {
        if (IsInReferenceComment(file, position))
        {
            var entries = GetTripleSlashReferenceCompletions(file, position, checker);
            return ConvertPathCompletions(entries, file, position);
        }
        if (LsDeps.IsInString(file, position, contextToken))
        {
            if (contextToken == null || !AstUtilities.IsStringLiteralLike(contextToken))
            {
                return null;
            }
            var entries = GetStringLiteralCompletionEntries(
                file,
                contextToken,
                position,
                checker);
            return ConvertStringLiteralCompletions(
                entries,
                contextToken,
                file,
                position,
                checker,
                compilerOptions,
                includeSymbols);
        }
        return null;
    }

    // string_completions.go:91-197 — convertStringLiteralCompletions.
    internal CompletionList? ConvertStringLiteralCompletions(
        StringLiteralCompletionsResult? completion,
        Node contextToken,
        SourceFile file,
        int position,
        TsChecker typeChecker,
        global::Ts.Core.CompilerOptions options,
        bool includeSymbols)
    {
        if (completion == null)
        {
            return null;
        }

        var optionalReplacementRange = CreateRangeFromStringLiteralLikeContent(file, contextToken, position);
        if (completion.fromPaths != null)
        {
            return ConvertPathCompletions(completion.fromPaths, file, position);
        }
        else if (completion.fromProperties != null)
        {
            var fromProperties = completion.fromProperties;
            var data = new CompletionDataData
            {
                symbols = fromProperties.symbols,
                completionKind = CompletionKind.String,
                isNewIdentifierLocation = fromProperties.hasIndexSignature,
                location = file,
                contextToken = contextToken,
            };
            var (_, items) = GetCompletionEntriesFromSymbols(
                typeChecker,
                data,
                contextToken, /*replacementToken*/
                position,
                file,
                options,
                includeSymbols);
            var defaultCommitCharacters = GetDefaultCommitCharacters(fromProperties.hasIndexSignature);
            var itemDefaults = SetItemDefaults(
                position,
                file,
                items,
                defaultCommitCharacters,
                optionalReplacementRange);
            return new CompletionList
            {
                IsIncomplete = false,
                ItemDefaults = itemDefaults,
                Items = items.ToArray(),
            };
        }
        else if (completion.fromTypes != null)
        {
            var fromTypes = completion.fromTypes;
            global::Ts.Printer.QuoteChar quoteChar;
            if (contextToken.Kind == SyntaxKind.NoSubstitutionTemplateLiteral)
            {
                quoteChar = global::Ts.Printer.QuoteChar.Backtick;
            }
            else if (contextToken.Text().StartsWith('\''))
            {
                quoteChar = global::Ts.Printer.QuoteChar.SingleQuote;
            }
            else
            {
                quoteChar = global::Ts.Printer.QuoteChar.DoubleQuote;
            }
            var items = new List<CompletionItem>(fromTypes.types.Length);
            foreach (var t in fromTypes.types)
            {
                var name = global::Ts.Printer.PrinterUtilities.EscapeString((string)t.AsLiteralType().Value()!, quoteChar);
                var lspItem = CreateLSPCompletionItem(
                    name,
                    "", /*insertText*/
                    "", /*filterText*/
                    SortTextLocationPriorityValue,
                    ScriptElementKind.String,
                    ScriptElementKindModifier.None,
                    GetReplacementRangeForContextToken(file, contextToken, position),
                    null, /*commitCharacters*/
                    null, /*labelDetails*/
                    file,
                    position,
                    false, /*isMemberCompletion*/
                    false, /*isSnippet*/
                    false, /*hasAction*/
                    false, /*preselect*/
                    "",    /*source*/
                    null,  /*autoImportEntryData*/
                    null /*detail*/);
                items.Add(new CompletionItem
                {
                    Item = lspItem,
                });
            }
            var defaultCommitCharacters = GetDefaultCommitCharacters(fromTypes.isNewIdentifier);
            var itemDefaults = SetItemDefaults(
                position,
                file,
                items,
                defaultCommitCharacters,
                null /*optionalReplacementSpan*/);
            return new CompletionList
            {
                IsIncomplete = false,
                ItemDefaults = itemDefaults,
                Items = items.ToArray(),
            };
        }
        return null;
    }

    // string_completions.go:199-255 — convertPathCompletions.
    internal CompletionList ConvertPathCompletions(
        List<PathCompletion> pathCompletions,
        SourceFile file,
        int position)
    {
        var isNewIdentifierLocation = true; // The user may type in a path that doesn't yet exist, creating a "new identifier" with respect to the collection of identifiers the server is aware of.
        var defaultCommitCharacters = GetDefaultCommitCharacters(isNewIdentifierLocation);
        var items = new List<CompletionItem>(pathCompletions.Count);
        foreach (var pathCompletion in pathCompletions)
        {
            global::Ts.LsProto.Range? replacementSpan = null;
            if (pathCompletion.textRange != null)
            {
                replacementSpan = CreateLspRangeFromBounds(pathCompletion.textRange.Value.Pos, pathCompletion.textRange.Value.End, file);
            }
            var detail = pathCompletion.name;
            if (!pathCompletion.name.EndsWith(pathCompletion.extension, StringComparison.Ordinal))
            {
                detail += pathCompletion.extension;
            }
            var lspItem = CreateLSPCompletionItem(
                pathCompletion.name,
                "", /*insertText*/
                "", /*filterText*/
                SortTextLocationPriorityValue,
                pathCompletion.kind,
                KindModifiersFromExtension(pathCompletion.extension),
                replacementSpan,
                null, /*commitCharacters*/
                null, /*labelDetails*/
                file,
                position,
                false, /*isMemberCompletion*/
                false, /*isSnippet*/
                false, /*hasAction*/
                false, /*preselect*/
                "",    /*source*/
                null,  /*autoImportEntryData*/
                detail);
            items.Add(new CompletionItem
            {
                Item = lspItem,
            });
        }
        var itemDefaults = SetItemDefaults(
            position,
            file,
            items,
            defaultCommitCharacters,
            null /*optionalReplacementSpan*/);
        return new CompletionList
        {
            IsIncomplete = false,
            ItemDefaults = itemDefaults,
            Items = items.ToArray(),
        };
    }

    // string_completions.go:256-431 — getStringLiteralCompletionEntries.
    internal StringLiteralCompletionsResult? GetStringLiteralCompletionEntries(
        SourceFile file,
        Node node,
        int position,
        TsChecker typeChecker)
    {
        var parent = WalkUpParenthesesForString(node.Parent!);
        switch (parent.Kind)
        {
            case SyntaxKind.LiteralType:
            {
                var grandparent = WalkUpParenthesesForString(parent.Parent!);
                if (grandparent.Kind == SyntaxKind.ImportType)
                {
                    return GetStringLiteralCompletionsFromModuleNames(file, node, typeChecker);
                }
                return FromUnionableLiteralType(grandparent, parent, position, typeChecker);
            }
            case SyntaxKind.PropertyAssignment:
                if (NodeTests.IsObjectLiteralExpression(parent.Parent!) && parent.Name() == node)
                {
                    // Get quoted name of properties of the object literal expression
                    // i.e. interface ConfigFiles {
                    //          'jspm:dev': string
                    //      }
                    //      let files: ConfigFiles = {
                    //          '/*completion position*/'
                    //      }
                    //
                    //      function foo(c: ConfigFiles) {}
                    //      foo({
                    //          '/*completion position*/'
                    //      });
                    return new StringLiteralCompletionsResult
                    {
                        fromProperties = StringLiteralCompletionsForObjectLiteral(typeChecker, parent.Parent!),
                    };
                }
                if (AstUtilities.FindAncestor(parent.Parent, AstUtilities.IsCallLikeExpression) != null)
                {
                    var uniques = new HashSet<string>(StringComparer.Ordinal);
                    var stringLiteralTypes = new List<CheckerType>();
                    stringLiteralTypes.AddRange(GetStringLiteralTypes(typeChecker.GetContextualType(node, global::Ts.Checker.ContextFlags.None), uniques, typeChecker));
                    stringLiteralTypes.AddRange(GetStringLiteralTypes(typeChecker.GetContextualType(node, global::Ts.Checker.ContextFlags.IgnoreNodeInferences), uniques, typeChecker));
                    return ToStringLiteralCompletionsFromTypes(stringLiteralTypes.ToArray());
                }
                return new StringLiteralCompletionsResult
                {
                    fromTypes = FromContextualType(global::Ts.Checker.ContextFlags.None, node, typeChecker),
                };
            case SyntaxKind.ElementAccessExpression:
            {
                var expression = parent.Expression()!;
                var argumentExpression = parent.AsElementAccessExpression().ArgumentExpression!;
                if (node == AstUtilities.SkipParentheses(argumentExpression))
                {
                    // Get all names of properties on the expression
                    // i.e. interface A {
                    //      'prop1': string
                    // }
                    // let a: A;
                    // a['/*completion position*/']
                    var t = typeChecker.GetTypeAtLocation(expression);
                    return new StringLiteralCompletionsResult
                    {
                        fromProperties = StringLiteralCompletionsFromProperties(t, typeChecker),
                    };
                }
                return null;
            }
            case SyntaxKind.CallExpression:
            case SyntaxKind.NewExpression:
            case SyntaxKind.JsxAttribute:
                if (!IsRequireCallArgument(node) && !AstUtilities.IsImportCall(parent))
                {
                    Node argumentNode;
                    if (parent.Kind == SyntaxKind.JsxAttribute)
                    {
                        argumentNode = parent.Parent!;
                    }
                    else
                    {
                        argumentNode = node;
                    }
                    var argumentInfo = GetArgumentInfoForCompletions(argumentNode, position, file, typeChecker);
                    // Get string literal completions from specialized signatures of the target
                    // i.e. declare function f(a: 'A');
                    // f("/*completion position*/")
                    if (argumentInfo == null)
                    {
                        return null;
                    }

                    var result = GetStringLiteralCompletionsFromSignature(argumentInfo.invocation, node, argumentInfo, typeChecker);
                    if (result != null)
                    {
                        return new StringLiteralCompletionsResult
                        {
                            fromTypes = result,
                        };
                    }
                    return new StringLiteralCompletionsResult
                    {
                        fromTypes = FromContextualType(global::Ts.Checker.ContextFlags.None, node, typeChecker),
                    };
                }
                goto case SyntaxKind.ImportDeclaration; // is `require("")` or `require(""` or `import("")`
            case SyntaxKind.ImportDeclaration:
            case SyntaxKind.ExportDeclaration:
            case SyntaxKind.ExternalModuleReference:
            case SyntaxKind.JSDocImportTag:
                // Get all known external module names or complete a path to a module
                // i.e. import * as ns from "/*completion position*/";
                //      var y = import("/*completion position*/");
                //      import x = require("/*completion position*/");
                //      var y = require("/*completion position*/");
                //      export * from "/*completion position*/";
                return GetStringLiteralCompletionsFromModuleNames(file, node, typeChecker);
            case SyntaxKind.CaseClause:
            {
                var tracker = CaseClauseTracker.NewCaseClauseTracker(typeChecker, parent.Parent!.AsCaseBlock().Clauses!.Nodes);
                var contextualTypes = FromContextualType(global::Ts.Checker.ContextFlags.IgnoreNodeInferences, node, typeChecker);
                if (contextualTypes == null)
                {
                    return null;
                }
                var literals = CoreUtil.Filter(contextualTypes.types, t => !tracker.HasValue(t.AsLiteralType().Value()!)) ?? [];
                return new StringLiteralCompletionsResult
                {
                    fromTypes = new CompletionsFromTypes
                    {
                        types = literals,
                        isNewIdentifier = false,
                    },
                };
            }
            case SyntaxKind.ImportSpecifier:
            case SyntaxKind.ExportSpecifier:
            {
                // Complete string aliases in `import { "|" } from` and `export { "|" } from`
                var specifier = parent;
                if (specifier.PropertyName() is { } propertyName && node != propertyName)
                {
                    return null; // Don't complete in `export { "..." as "|" } from`
                }
                var namedImportsOrExports = specifier.Parent!;
                Node? moduleSpecifier;
                if (namedImportsOrExports.Kind == SyntaxKind.NamedImports)
                {
                    moduleSpecifier = namedImportsOrExports.Parent!.Parent;
                }
                else
                {
                    moduleSpecifier = namedImportsOrExports.Parent;
                }
                if (moduleSpecifier == null)
                {
                    return null;
                }
                var moduleSpecifierSymbol = typeChecker.GetSymbolAtLocation(moduleSpecifier);
                if (moduleSpecifierSymbol == null)
                {
                    return null;
                }
                var exports = typeChecker.GetExportsAndPropertiesOfModule(moduleSpecifierSymbol);
                var existing = new HashSet<string>(StringComparer.Ordinal);
                foreach (var n in namedImportsOrExports.Elements() ?? [])
                {
                    existing.Add(n.PropertyNameOrName()!.Text());
                }
                var uniques = CoreUtil.Filter(exports, e =>
                    e.Name != InternalSymbolName.Default && !existing.Contains(e.Name)) ?? [];
                return new StringLiteralCompletionsResult
                {
                    fromProperties = new CompletionsFromProperties
                    {
                        symbols = uniques,
                        hasIndexSignature = false,
                    },
                };
            }
            case SyntaxKind.BinaryExpression:
                if (parent.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.InKeyword)
                {
                    var t = typeChecker.GetTypeAtLocation(parent.AsBinaryExpression().Right!);
                    var properties = GetPropertiesForCompletion(t, typeChecker);
                    return new StringLiteralCompletionsResult
                    {
                        fromProperties = new CompletionsFromProperties
                        {
                            symbols = CoreUtil.Filter(properties, s =>
                                s.ValueDeclaration == null || !AstUtilities.IsPrivateIdentifierClassElementDeclaration(s.ValueDeclaration)) ?? [],
                            hasIndexSignature = false,
                        },
                    };
                }
                return new StringLiteralCompletionsResult
                {
                    fromTypes = FromContextualType(global::Ts.Checker.ContextFlags.None, node, typeChecker),
                };
            default:
            {
                var result = FromContextualType(global::Ts.Checker.ContextFlags.IgnoreNodeInferences, node, typeChecker);
                if (result != null)
                {
                    return new StringLiteralCompletionsResult
                    {
                        fromTypes = result,
                    };
                }
                return new StringLiteralCompletionsResult
                {
                    fromTypes = FromContextualType(global::Ts.Checker.ContextFlags.None, node, typeChecker),
                };
            }
        }
    }

    // string_completions.go:433-437 — fromContextualType. Get completion for string literal from
    // string literal type i.e. var x: "hi" | "hello" = "/*completion position*/"
    internal static CompletionsFromTypes? FromContextualType(global::Ts.Checker.ContextFlags contextFlags, Node node, TsChecker typeChecker)
    {
        return ToCompletionsFromTypes(GetStringLiteralTypes(GetContextualTypeFromParent(node, typeChecker, contextFlags), null, typeChecker));
    }

    // string_completions.go:439-447 — toCompletionsFromTypes.
    internal static CompletionsFromTypes? ToCompletionsFromTypes(CheckerType[] types)
    {
        if (types.Length == 0)
        {
            return null;
        }
        return new CompletionsFromTypes
        {
            types = types,
            isNewIdentifier = false,
        };
    }

    // string_completions.go:449-457 — toStringLiteralCompletionsFromTypes.
    internal static StringLiteralCompletionsResult? ToStringLiteralCompletionsFromTypes(CheckerType[] types)
    {
        var result = ToCompletionsFromTypes(types);
        if (result == null)
        {
            return null;
        }
        return new StringLiteralCompletionsResult
        {
            fromTypes = result,
        };
    }

    // string_completions.go:459-546 — fromUnionableLiteralType.
    internal static StringLiteralCompletionsResult? FromUnionableLiteralType(
        Node grandparent,
        Node parent,
        int position,
        TsChecker typeChecker)
    {
        switch (grandparent.Kind)
        {
            case SyntaxKind.CallExpression:
            case SyntaxKind.ExpressionWithTypeArguments:
            case SyntaxKind.JsxOpeningElement:
            case SyntaxKind.JsxSelfClosingElement:
            case SyntaxKind.NewExpression:
            case SyntaxKind.TaggedTemplateExpression:
            case SyntaxKind.TypeReference:
            {
                var typeArgument = AstUtilities.FindAncestor(parent, n => n.Parent == grandparent);
                if (typeArgument != null)
                {
                    var t = typeChecker.GetTypeArgumentConstraint(typeArgument);
                    return new StringLiteralCompletionsResult
                    {
                        fromTypes = new CompletionsFromTypes
                        {
                            types = GetStringLiteralTypes(t, null, typeChecker),
                            isNewIdentifier = false,
                        },
                    };
                }
                return null;
            }
            case SyntaxKind.IndexedAccessType:
            {
                // Get all apparent property names
                // i.e. interface Foo {
                //          foo: string;
                //          bar: string;
                //      }
                //      let x: Foo["/*completion position*/"]
                var indexType = grandparent.AsIndexedAccessTypeNode().IndexType!;
                var objectType = grandparent.AsIndexedAccessTypeNode().ObjectType!;
                if (!indexType.Loc.ContainsInclusive(position))
                {
                    return null;
                }
                var t = typeChecker.GetTypeFromTypeNode(objectType);
                return new StringLiteralCompletionsResult
                {
                    fromProperties = StringLiteralCompletionsFromProperties(t, typeChecker),
                };
            }
            case SyntaxKind.UnionType:
            {
                var result = FromUnionableLiteralType(
                    WalkUpParenthesesForString(grandparent.Parent!),
                    parent,
                    position,
                    typeChecker);
                if (result == null)
                {
                    return null;
                }
                var alreadyUsedTypes = GetAlreadyUsedTypesInStringLiteralUnion(grandparent, parent);
                if (result.fromProperties != null)
                {
                    var fromProperties = result.fromProperties;
                    return new StringLiteralCompletionsResult
                    {
                        fromProperties = new CompletionsFromProperties
                        {
                            symbols = CoreUtil.Filter(
                                fromProperties.symbols,
                                s => !alreadyUsedTypes.Contains(s.Name)) ?? [],
                            hasIndexSignature = fromProperties.hasIndexSignature,
                        },
                    };
                }
                else if (result.fromTypes != null)
                {
                    var fromTypes = result.fromTypes;
                    return new StringLiteralCompletionsResult
                    {
                        fromTypes = new CompletionsFromTypes
                        {
                            types = CoreUtil.Filter(fromTypes.types, t =>
                                !alreadyUsedTypes.Contains((string)t.AsLiteralType().Value()!)) ?? [],
                            isNewIdentifier = false,
                        },
                    };
                }
                return null;
            }
            case SyntaxKind.PropertySignature:
                return new StringLiteralCompletionsResult
                {
                    fromTypes = new CompletionsFromTypes
                    {
                        types = GetStringLiteralTypes(GetConstraintOfTypeArgumentProperty(grandparent, typeChecker), null, typeChecker),
                        isNewIdentifier = false,
                    },
                };
            default:
                return null;
        }
    }

    // string_completions.go:548-569 — stringLiteralCompletionsForObjectLiteral.
    internal static CompletionsFromProperties? StringLiteralCompletionsForObjectLiteral(
        TsChecker typeChecker,
        Node objectLiteralExpression)
    {
        var contextualType = typeChecker.GetContextualType(objectLiteralExpression, global::Ts.Checker.ContextFlags.None);
        if (contextualType == null)
        {
            return null;
        }

        var completionsType = typeChecker.GetContextualType(objectLiteralExpression, global::Ts.Checker.ContextFlags.IgnoreNodeInferences);
        var symbols = GetPropertiesForObjectExpression(
            contextualType,
            completionsType,
            objectLiteralExpression,
            typeChecker);

        return new CompletionsFromProperties
        {
            symbols = symbols,
            hasIndexSignature = HasIndexSignature(contextualType, typeChecker),
        };
    }

    // string_completions.go:571-578 — stringLiteralCompletionsFromProperties.
    internal static CompletionsFromProperties StringLiteralCompletionsFromProperties(CheckerType t, TsChecker typeChecker)
    {
        return new CompletionsFromProperties
        {
            symbols = CoreUtil.Filter(typeChecker.GetApparentProperties(t), s =>
                !(s.ValueDeclaration != null && AstUtilities.IsPrivateIdentifierClassElementDeclaration(s.ValueDeclaration))) ?? [],
            hasIndexSignature = HasIndexSignature(t, typeChecker),
        };
    }

    // string_completions.go:1882-1891 — walkUpParentheses. (Named *ForString to avoid colliding
    // with checker-side helpers in the partial class.)
    internal static Node WalkUpParenthesesForString(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ParenthesizedType:
                return AstUtilities.WalkUpParenthesizedTypes(node)!;
            case SyntaxKind.ParenthesizedExpression:
                return AstUtilities.WalkUpParenthesizedExpressions(node)!;
            default:
                return node;
        }
    }

    // string_completions.go:1893-1912 — getStringLiteralTypes.
    internal static CheckerType[] GetStringLiteralTypes(CheckerType? t, HashSet<string>? uniques, TsChecker typeChecker)
    {
        if (t == null)
        {
            return [];
        }
        uniques ??= new HashSet<string>(StringComparer.Ordinal);
        t = SkipConstraint(t, typeChecker);
        if (t.IsUnion())
        {
            var types = new List<CheckerType>();
            foreach (var elementType in t.Types())
            {
                types.AddRange(GetStringLiteralTypes(elementType, uniques, typeChecker));
            }
            return types.ToArray();
        }
        if (t.IsStringLiteral() && !t.IsEnumLiteral() && uniques.Add((string)t.AsLiteralType().Value()!))
        {
            return [t];
        }
        return [];
    }

    // utilities.go:1015-1023 — skipConstraint.
    internal static CheckerType SkipConstraint(CheckerType t, TsChecker typeChecker)
    {
        if (t.IsTypeParameter())
        {
            var c = typeChecker.GetBaseConstraintOfType(t);
            if (c != null)
            {
                return c;
            }
        }
        return t;
    }

    // string_completions.go:1914-1927 — getAlreadyUsedTypesInStringLiteralUnion.
    internal static List<string> GetAlreadyUsedTypesInStringLiteralUnion(Node union, Node current)
    {
        var typesList = union.AsUnionTypeNode().Types;
        if (typesList == null)
        {
            return [];
        }
        var values = new List<string>();
        foreach (var typeNode in typesList.Nodes)
        {
            if (typeNode != current && NodeTests.IsLiteralTypeNode(typeNode) &&
                NodeTests.IsStringLiteral(typeNode.AsLiteralTypeNode().Literal!))
            {
                values.Add(typeNode.AsLiteralTypeNode().Literal!.Text());
            }
        }
        return values;
    }

    // string_completions.go:1929-1935 — hasIndexSignature.
    internal static bool HasIndexSignature(CheckerType t, TsChecker typeChecker)
    {
        return typeChecker.GetStringIndexType(t) != null || typeChecker.GetNumberIndexType(t) != null;
    }

    // string_completions.go:1937-1940 — isRequireCallArgument. Matches `require(""` / `require("")`.
    internal static bool IsRequireCallArgument(Node node)
    {
        return NodeTests.IsCallExpression(node.Parent!) && (node.Parent!.Arguments() ?? []).Length > 0 && node.Parent!.Arguments()![0] == node &&
            NodeTests.IsIdentifier(node.Parent!.Expression()!) && node.Parent!.Expression()!.Text() == "require";
    }

    // string_completions.go:1942-1973 — kindModifiersFromExtension.
    internal static ScriptElementKindModifier KindModifiersFromExtension(string extension)
    {
        switch (extension)
        {
            case ".d.ts":
                return ScriptElementKindModifier.Dts;
            case ".js":
                return ScriptElementKindModifier.Js;
            case ".json":
                return ScriptElementKindModifier.Json;
            case ".jsx":
                return ScriptElementKindModifier.Jsx;
            case ".ts":
                return ScriptElementKindModifier.Ts;
            case ".tsx":
                return ScriptElementKindModifier.Tsx;
            case ".d.mts":
                return ScriptElementKindModifier.Dmts;
            case ".mjs":
                return ScriptElementKindModifier.Mjs;
            case ".mts":
                return ScriptElementKindModifier.Mts;
            case ".d.cts":
                return ScriptElementKindModifier.Dcts;
            case ".cjs":
                return ScriptElementKindModifier.Cjs;
            case ".cts":
                return ScriptElementKindModifier.Cts;
            case ".tsbuildinfo":
                throw new InvalidOperationException($"Extension {extension} is unsupported.");
            default:
                return ScriptElementKindModifier.None;
        }
    }

    // string_completions.go:1975-2015 — getStringLiteralCompletionsFromSignature.
    internal static CompletionsFromTypes? GetStringLiteralCompletionsFromSignature(
        Node call,
        Node arg,
        ArgumentInfoForCompletions argumentInfo,
        TsChecker typeChecker)
    {
        var isNewIdentifier = false;
        var uniques = new HashSet<string>(StringComparer.Ordinal);
        Node editingArgument;
        if (AstUtilities.IsJsxOpeningLikeElement(call))
        {
            editingArgument = AstUtilities.FindAncestor(arg.Parent, NodeTests.IsJsxAttribute)
                ?? throw new InvalidOperationException("Expected jsx opening-like element to have a jsx attribute as ancestor.");
        }
        else
        {
            editingArgument = arg;
        }
        var candidates = typeChecker.GetCandidateSignaturesForStringLiteralCompletions(call, editingArgument);
        var types = new List<CheckerType>();
        foreach (var candidate in candidates)
        {
            if (!candidate.HasRestParameter() && argumentInfo.argumentCount > (candidate.Parameters() ?? []).Length)
            {
                continue;
            }
            var t = typeChecker.GetTypeParameterAtPosition(candidate, argumentInfo.argumentIndex);
            if (AstUtilities.IsJsxOpeningLikeElement(call))
            {
                var propType = typeChecker.GetTypeOfPropertyOfType(t, editingArgument.AsJsxAttribute().Name()!.Text());
                if (propType != null)
                {
                    t = propType;
                }
            }
            isNewIdentifier = isNewIdentifier || t.IsString();
            types.AddRange(GetStringLiteralTypes(t, uniques, typeChecker));
        }
        if (types.Count > 0)
        {
            return new CompletionsFromTypes
            {
                types = types.ToArray(),
                isNewIdentifier = isNewIdentifier,
            };
        }
        return null;
    }

    // string_completions.go:2076-2083 — isInReferenceComment.
    // PORT-NOTE: real body (triple-slash directive parsing at :2085-2211) ships with the
    // path/module-specifier batch; until then reference-comment positions take Go's false path.
    internal static bool IsInReferenceComment(SourceFile file, int position)
    {
        _ = file;
        _ = position;
        return false;
    }

    // string_completions.go:2159-2211 — getTripleSlashReferenceCompletions.
    // PORT-NOTE: ships with the path/module-specifier batch (needs VFS directory enumeration).
    internal List<PathCompletion> GetTripleSlashReferenceCompletions(SourceFile file, int position, TsChecker checker)
    {
        _ = file;
        _ = position;
        _ = checker;
        return [];
    }

    // string_completions.go:580-596 — getStringLiteralCompletionsFromModuleNames.
    // PORT-NOTE: the module-specifier/path half of string_completions.go (:580-1890) is the next
    // batch (module resolution walk + node_modules/typings enumeration + paths patterns); until
    // it lands, module-specifier strings produce no path list (Go's nil path).
    internal StringLiteralCompletionsResult? GetStringLiteralCompletionsFromModuleNames(
        SourceFile file,
        Node node,
        TsChecker typeChecker)
    {
        _ = file;
        _ = node;
        _ = typeChecker;
        return null;
    }
}
