// Ported from tsgo internal/ls/completions.go — the JSDoc completion item cluster:
// cloneItems (:3275-3285), jsDocCompletionInfo (:5364-5384), the jsDocTagNames tables
// (:5386-5505), getJSDocParameterCompletions and its @param annotation workers (:5507-5906),
// and getJSDocParameterNameCompletions (:5908-5947).

using System.Collections.Generic;
using System;
using Ts.Ast;
using Ts.Core;
using TsChecker = Ts.Checker.Checker;

namespace Ts.Ls;

public sealed partial class LanguageService
{
    // completions.go:5364-5384 — jsDocCompletionInfo.
    internal CompletionList JsDocCompletionInfo(int position, SourceFile file, List<CompletionItem> items)
    {
        var defaultCommitCharacters = GetDefaultCommitCharacters(false /*isNewIdentifierLocation*/);
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

    // completions.go:5386-5471 — jsDocTagNames.
    private static readonly string[] JsDocTagNames =
    [
        "abstract", "access", "alias", "argument", "async", "augments", "author", "borrows",
        "callback", "class", "classdesc", "constant", "constructor", "constructs", "copyright",
        "default", "deprecated", "description", "emits", "enum", "event", "example", "exports",
        "extends", "external", "field", "file", "fileoverview", "fires", "function", "generator",
        "global", "hideconstructor", "host", "ignore", "implements", "import", "inheritdoc",
        "inner", "instance", "interface", "kind", "lends", "license", "link", "linkcode",
        "linkplain", "listens", "member", "memberof", "method", "mixes", "module", "name",
        "namespace", "overload", "override", "package", "param", "private", "prop", "property",
        "protected", "public", "readonly", "requires", "returns", "satisfies", "see", "since",
        "static", "summary", "template", "this", "throws", "todo", "tutorial", "type", "typedef",
        "var", "variation", "version", "virtual", "yields",
    ];

    // completions.go:5473-5485 — jsDocTagNameCompletionItems (sync.OnceValue → Lazy).
    private static readonly Lazy<global::Ts.LsProto.CompletionItem[]> JsDocTagNameCompletionItems = new(() =>
    {
        var items = new List<global::Ts.LsProto.CompletionItem>(JsDocTagNames.Length);
        foreach (var tagName in JsDocTagNames)
        {
            items.Add(new global::Ts.LsProto.CompletionItem
            {
                Label = tagName,
                Kind = global::Ts.LsProto.CompletionItemKind.Keyword,
                SortText = SortTextLocationPriorityValue,
            });
        }
        return items.ToArray();
    });

    // completions.go:5487-5497 — jsDocTagCompletionItems.
    private static readonly Lazy<global::Ts.LsProto.CompletionItem[]> JsDocTagCompletionItems = new(() =>
    {
        var items = new List<global::Ts.LsProto.CompletionItem>(JsDocTagNames.Length);
        foreach (var tagName in JsDocTagNames)
        {
            items.Add(new global::Ts.LsProto.CompletionItem
            {
                Label = "@" + tagName,
                Kind = global::Ts.LsProto.CompletionItemKind.Keyword,
                SortText = SortTextLocationPriorityValue,
            });
        }
        return items.ToArray();
    });

    // completions.go:5499-5501 — getJSDocTagNameCompletions.
    internal static List<CompletionItem> GetJSDocTagNameCompletions() => new(CloneItems(JsDocTagNameCompletionItems.Value) ?? []);

    // completions.go:5503-5505 — getJSDocTagCompletions.
    internal static List<CompletionItem> GetJSDocTagCompletions() => new(CloneItems(JsDocTagCompletionItems.Value) ?? []);

    // completions.go:5507-5648 — getJSDocParameterCompletions.
    internal static List<CompletionItem> GetJSDocParameterCompletions(
        SourceFile file,
        int position,
        TsChecker typeChecker,
        global::Ts.Core.CompilerOptions options,
        UserPreferences preferences,
        bool tagNameOnly)
    {
        var currentToken = global::Ts.AstNav.Tokens.GetTokenAtPosition(file, position);
        if (!AstUtilities.IsJSDocTag(currentToken) && !NodeTests.IsJSDoc(currentToken))
        {
            return [];
        }
        Node jsDoc;
        if (NodeTests.IsJSDoc(currentToken))
        {
            jsDoc = currentToken;
        }
        else
        {
            jsDoc = currentToken.Parent!;
        }
        if (!NodeTests.IsJSDoc(jsDoc))
        {
            return [];
        }
        var fun = jsDoc.Parent!;
        if (!AstUtilities.IsFunctionLike(fun))
        {
            return [];
        }

        var isJS = AstUtilities.IsSourceFileJS(file);
        // isSnippet := clientSupportsItemSnippet(clientOptions)
        var isSnippet = false; // !!! need snippet printer
        var paramTagCount = 0;
        Node[] tags = [];
        if (jsDoc.AsJSDoc().Tags != null)
        {
            tags = jsDoc.AsJSDoc().Tags!.Nodes;
        }
        foreach (var tag in tags)
        {
            if (NodeTests.IsJSDocParameterTag(tag) &&
                global::Ts.AstNav.Tokens.GetStartOfNode(tag, file, false /*includeJSDoc*/) < position &&
                NodeTests.IsIdentifier(tag.Name()!))
            {
                paramTagCount++;
            }
        }
        var paramIndex = -1;
        var result = new List<CompletionItem>();
        foreach (var param in fun.Parameters() ?? [])
        {
            paramIndex++;
            if (paramIndex < paramTagCount)
            {
                // This parameter is already annotated.
                continue;
            }
            if (NodeTests.IsIdentifier(param.Name()!)) // Named parameter
            {
                var tabstopCounter = 1;
                var paramName = param.Name()!.Text();
                var displayText = GetJSDocParamAnnotation(
                    paramName,
                    param.Initializer(),
                    param.AsParameterDeclaration().DotDotDotToken,
                    isJS,
                    false, /*isObject*/
                    false, /*isSnippet*/
                    typeChecker,
                    options,
                    preferences,
                    ref tabstopCounter);
                var snippetText = "";
                if (isSnippet)
                {
                    snippetText = GetJSDocParamAnnotation(
                        paramName,
                        param.Initializer(),
                        param.AsParameterDeclaration().DotDotDotToken,
                        isJS,
                        false, /*isObject*/
                        true, /*isSnippet*/
                        typeChecker,
                        options,
                        preferences,
                        ref tabstopCounter);
                }
                if (tagNameOnly) // Remove `@`
                {
                    displayText = displayText[1..];
                    if (snippetText.Length != 0)
                    {
                        snippetText = snippetText[1..];
                    }
                }

                result.Add(new CompletionItem
                {
                    Item = new global::Ts.LsProto.CompletionItem
                    {
                        Label = displayText,
                        Kind = global::Ts.LsProto.CompletionItemKind.Variable,
                        SortText = SortTextLocationPriorityValue,
                        InsertText = snippetText.Length == 0 ? null : snippetText,
                        InsertTextFormat = isSnippet ? global::Ts.LsProto.InsertTextFormat.Snippet : null,
                    },
                });
            }
            else if (paramIndex == paramTagCount)
            {
                // Destructuring parameter; do it positionally
                var paramPath = $"param{paramIndex}";
                var displayTextResult = GenerateJSDocParamTagsForDestructuring(
                    paramPath,
                    param.Name()!,
                    param.Initializer(),
                    param.AsParameterDeclaration().DotDotDotToken,
                    isJS,
                    false, /*isSnippet*/
                    typeChecker,
                    options,
                    preferences);
                var snippetText = "";
                if (isSnippet)
                {
                    var snippetTextResult = GenerateJSDocParamTagsForDestructuring(
                        paramPath,
                        param.Name()!,
                        param.Initializer(),
                        param.AsParameterDeclaration().DotDotDotToken,
                        isJS,
                        true, /*isSnippet*/
                        typeChecker,
                        options,
                        preferences);
                    snippetText = string.Join(options.NewLine.GetNewLineCharacter() + "* ", snippetTextResult);
                }
                var displayText = string.Join(options.NewLine.GetNewLineCharacter() + "* ", displayTextResult);
                if (tagNameOnly) // Remove `@`
                {
                    displayText = displayText.StartsWith('@') ? displayText[1..] : displayText;
                    snippetText = snippetText.StartsWith('@') ? snippetText[1..] : snippetText;
                }
                result.Add(new CompletionItem
                {
                    Item = new global::Ts.LsProto.CompletionItem
                    {
                        Label = displayText,
                        Kind = global::Ts.LsProto.CompletionItemKind.Variable,
                        SortText = SortTextLocationPriorityValue,
                        InsertText = snippetText.Length == 0 ? null : snippetText,
                        InsertTextFormat = isSnippet ? global::Ts.LsProto.InsertTextFormat.Snippet : null,
                    },
                });
            }
        }
        return result;
    }

    // completions.go:5650-5733 — getJSDocParamAnnotation.
    internal static string GetJSDocParamAnnotation(
        string paramName,
        Node? initializer,
        Node? dotDotDotToken,
        bool isJS,
        bool isObject,
        bool isSnippet,
        TsChecker typeChecker,
        global::Ts.Core.CompilerOptions options,
        UserPreferences preferences,
        ref int tabstopCounter)
    {
        if (initializer != null)
        {
            paramName = GetJSDocParamNameWithInitializer(paramName, initializer);
        }
        if (isSnippet)
        {
            paramName = EscapeSnippetText(paramName);
        }
        if (isJS)
        {
            var t = "*";
            if (isObject)
            {
                global::Ts.Core.Debug.Assert(dotDotDotToken == null, "Cannot annotate a rest parameter with type 'object'.");
                t = "object";
            }
            else
            {
                if (initializer != null)
                {
                    var inferredType = typeChecker.GetTypeAtLocation(initializer.Parent!);
                    if ((inferredType.Flags & (global::Ts.Checker.TypeFlags.Any | global::Ts.Checker.TypeFlags.Void)) == 0)
                    {
                        var file = AstUtilities.GetSourceFileOfNode(initializer)!;
                        var quotePreference = LsDeps.GetQuotePreference(file, preferences);
                        var builderFlags = quotePreference == QuotePreference.Single
                            ? global::Ts.NodeBuilder.Flags.UseSingleQuotesForStringLiteralType
                            : global::Ts.NodeBuilder.Flags.None;
                        var typeNode = typeChecker.TypeToTypeNode(
                            inferredType,
                            AstUtilities.FindAncestor(initializer, AstUtilities.IsFunctionLike),
                            builderFlags,
                            null /*idToSymbol*/);
                        if (typeNode != null)
                        {
                            var emitContext = global::Ts.Printer.EmitContext.NewEmitContext();
                            // !!! snippet p
                            var p = global::Ts.Printer.Printer.NewPrinter(new global::Ts.Printer.PrinterOptions
                            {
                                RemoveComments = true,
                                // !!!
                                // Module: options.Module,
                                // ModuleResolution: options.ModuleResolution,
                                // Target: options.Target,
                            }, new global::Ts.Printer.PrintHandlers(), emitContext);
                            emitContext.SetEmitFlags(typeNode, global::Ts.Printer.EmitFlags.SingleLine);
                            t = p.Emit(typeNode, file);
                        }
                    }
                }
                if (isSnippet && t == "*")
                {
                    var tabstop = tabstopCounter;
                    tabstopCounter++;
                    t = $"${{{tabstop}:{t}}}";
                }
            }
            var dotDotDot = !isObject && dotDotDotToken != null ? "..." : "";
            var description = "";
            if (isSnippet)
            {
                var tabstop = tabstopCounter;
                tabstopCounter++;
                description = $"${{{tabstop}}}";
            }
            return $"@param {{{dotDotDot}{t}}} {paramName} {description}";
        }
        else
        {
            var description = "";
            if (isSnippet)
            {
                var tabstop = tabstopCounter;
                tabstopCounter++;
                description = $"${{{tabstop}}}";
            }
            return $"@param {paramName} {description}";
        }
    }

    // completions.go:5735-5741 — getJSDocParamNameWithInitializer.
    internal static string GetJSDocParamNameWithInitializer(string paramName, Node initializer)
    {
        var initializerText = global::Ts.Scanner.Scanner.GetTextOfNode(initializer).Trim();
        if (initializerText.Contains('\n') || initializerText.Length > 80)
        {
            return $"[{paramName}]";
        }
        return $"[{paramName}={initializerText}]";
    }

    // completions.go:5743-5779 — generateJSDocParamTagsForDestructuring.
    internal static List<string> GenerateJSDocParamTagsForDestructuring(
        string path,
        Node pattern,
        Node? initializer,
        Node? dotDotDotToken,
        bool isJS,
        bool isSnippet,
        TsChecker typeChecker,
        global::Ts.Core.CompilerOptions options,
        UserPreferences preferences)
    {
        var tabstopCounter = 1;
        if (!isJS)
        {
            return [GetJSDocParamAnnotation(
                path,
                initializer,
                dotDotDotToken,
                isJS,
                false, /*isObject*/
                isSnippet,
                typeChecker,
                options,
                preferences,
                ref tabstopCounter)];
        }
        return JsDocParamPatternWorker(
            path,
            pattern,
            initializer,
            dotDotDotToken,
            isJS,
            isSnippet,
            typeChecker,
            options,
            preferences,
            ref tabstopCounter);
    }

    // completions.go:5781-5842 — jsDocParamPatternWorker.
    private static List<string> JsDocParamPatternWorker(
        string path,
        Node pattern,
        Node? initializer,
        Node? dotDotDotToken,
        bool isJS,
        bool isSnippet,
        TsChecker typeChecker,
        global::Ts.Core.CompilerOptions options,
        UserPreferences preferences,
        ref int counter)
    {
        if (NodeTests.IsObjectBindingPattern(pattern) && dotDotDotToken == null)
        {
            var childCounter = counter;
            var rootParam = GetJSDocParamAnnotation(
                path,
                initializer,
                dotDotDotToken,
                isJS,
                true, /*isObject*/
                isSnippet,
                typeChecker,
                options,
                preferences,
                ref childCounter);
            List<string>? childTags = [];
            foreach (var element in pattern.Elements() ?? [])
            {
                var elementTags = JsDocParamElementWorker(
                    path,
                    element,
                    initializer,
                    dotDotDotToken,
                    isJS,
                    isSnippet,
                    typeChecker,
                    options,
                    preferences,
                    ref childCounter);
                if (elementTags.Count == 0)
                {
                    childTags = null;
                    break;
                }
                childTags.AddRange(elementTags);
            }
            if (childTags is { Count: > 0 })
            {
                counter = childCounter;
                var combined = new List<string> { rootParam };
                combined.AddRange(childTags);
                return combined;
            }
        }
        return [GetJSDocParamAnnotation(
            path,
            initializer,
            dotDotDotToken,
            isJS,
            false, /*isObject*/
            isSnippet,
            typeChecker,
            options,
            preferences,
            ref counter)];
    }

    // completions.go:5844-5906 — jsDocParamElementWorker: assumes binding element is inside object
    // binding pattern. We can't deeply annotate an array binding pattern.
    private static List<string> JsDocParamElementWorker(
        string path,
        Node element,
        Node? initializer,
        Node? dotDotDotToken,
        bool isJS,
        bool isSnippet,
        TsChecker typeChecker,
        global::Ts.Core.CompilerOptions options,
        UserPreferences preferences,
        ref int counter)
    {
        if (NodeTests.IsIdentifier(element.Name()!)) // `{ b }` or `{ b: newB }`
        {
            string propertyName;
            if (element.PropertyName() != null)
            {
                AstUtilities.TryGetTextOfPropertyName(element.PropertyName()!, out propertyName);
            }
            else
            {
                propertyName = element.Name()!.Text();
            }
            if (propertyName.Length == 0)
            {
                return [];
            }
            var paramName = $"{path}.{propertyName}";
            return [GetJSDocParamAnnotation(
                paramName,
                element.Initializer(),
                element.AsBindingElement().DotDotDotToken,
                isJS,
                false, /*isObject*/
                isSnippet,
                typeChecker,
                options,
                preferences,
                ref counter)];
        }
        else if (element.PropertyName() != null) // `{ b: {...} }` or `{ b: [...] }`
        {
            AstUtilities.TryGetTextOfPropertyName(element.PropertyName()!, out var propertyName);
            if (propertyName.Length == 0)
            {
                return [];
            }
            return JsDocParamPatternWorker(
                $"{path}.{propertyName}",
                element.Name()!,
                element.Initializer(),
                element.AsBindingElement().DotDotDotToken,
                isJS,
                isSnippet,
                typeChecker,
                options,
                preferences,
                ref counter);
        }
        return [];
    }

    // completions.go:5908-5947 — getJSDocParameterNameCompletions.
    internal static List<CompletionItem> GetJSDocParameterNameCompletions(Node tag)
    {
        if (!NodeTests.IsIdentifier(tag.Name()!))
        {
            return [];
        }
        var nameThusFar = tag.Name()!.Text();
        var jsDoc = tag.Parent!;
        var fn = jsDoc.Parent!;
        if (!AstUtilities.IsFunctionLike(fn))
        {
            return [];
        }

        Node[] tags = [];
        if (jsDoc.AsJSDoc().Tags != null)
        {
            tags = jsDoc.AsJSDoc().Tags!.Nodes;
        }

        var result = new List<CompletionItem>();
        foreach (var param in fn.Parameters() ?? [])
        {
            if (!NodeTests.IsIdentifier(param.Name()!))
            {
                continue;
            }

            var name = param.Name()!.Text();
            if (CoreUtil.Some(tags, t =>
                    t != tag &&
                    NodeTests.IsJSDocParameterTag(t) &&
                    NodeTests.IsIdentifier(t.Name()!) &&
                    t.Name()!.Text() == name) ||
                nameThusFar.Length != 0 && !name.StartsWith(nameThusFar, global::System.StringComparison.Ordinal))
            {
                continue;
            }

            result.Add(new CompletionItem
            {
                Item = new global::Ts.LsProto.CompletionItem
                {
                    Label = name,
                    Kind = global::Ts.LsProto.CompletionItemKind.Variable,
                    SortText = SortTextLocationPriorityValue,
                },
            });
        }
        return result;
    }
}
