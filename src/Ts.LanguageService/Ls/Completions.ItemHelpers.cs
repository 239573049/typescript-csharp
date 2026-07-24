// Ported from tsgo internal/ls/completions.go — the completion-item construction helpers:
// isRecommendedCompletionMatch/word-boundary + filterText cluster (:2300-2477), symbol filters
// (:2479-2644), snippet/name utilities (:3125-3260), literal items (:2007-2035),
// createLSPCompletionItem (:4426-4518) and the clientSupports* capability probes (:4822-4845);
// plus quote (utilities.go:307-316).

using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using CheckerType = Ts.Checker.Type;
using TsChecker = Ts.Checker.Checker;

namespace Ts.Ls;

public sealed partial class LanguageService
{
    // completions.go:2300-2304 — isRecommendedCompletionMatch.
    internal static bool IsRecommendedCompletionMatch(Symbol localSymbol, Symbol? recommendedCompletion, TsChecker typeChecker)
    {
        return localSymbol == recommendedCompletion ||
            (localSymbol.Flags & SymbolFlags.ExportValue) != 0 && typeChecker.GetExportSymbolOfSymbol(localSymbol) == recommendedCompletion;
    }

    // completions.go:2306-2310 — wordSeparators. Ported from vscode.
    private static readonly HashSet<int> WordSeparators =
    [
        '`', '~', '!', '@', '%', '^', '&', '*', '(', ')', '-', '=', '+', '[', '{', ']', '}', '\\', '|',
        ';', ':', '\'', '"', ',', '.', '<', '>', '/', '?',
    ];

    // completions.go:2312-2331 — getWordLengthAndStart: finds the length and first rune of the word
    // that ends at the given position. e.g. for "abc def.ghi|jkl", the word length is 3 and the word
    // start is 'g'.
    internal static (int WordLength, int WordStart) GetWordLengthAndStart(SourceFile sourceFile, int position)
    {
        // !!! Port other case of vscode's `DEFAULT_WORD_REGEXP` that covers words that start like numbers, e.g. -123.456abcd.
        var text = sourceFile.Text();
        var totalSize = 0;
        var firstRune = 0;
        var pos = position;
        while (pos > 0)
        {
            int r;
            int size;
            var c = text[pos - 1];
            if (char.IsLowSurrogate(c) && pos >= 2 && char.IsHighSurrogate(text[pos - 2]))
            {
                r = char.ConvertToUtf32(text[pos - 2], c);
                size = 2;
            }
            else
            {
                r = c;
                size = 1;
            }
            if (WordSeparators.Contains(r) || char.IsWhiteSpace(text, pos - size))
            {
                break;
            }
            totalSize += size;
            firstRune = r;
            pos -= size;
        }
        // If word starts with `@`, disregard this first character.
        if (firstRune == '@')
        {
            totalSize -= 1;
            firstRune = totalSize > 0 ? text[position - totalSize] : 0;
            if (totalSize >= 2 && char.IsHighSurrogate(text[position - totalSize]) && char.IsLowSurrogate(text[position - totalSize + 1]))
            {
                firstRune = char.ConvertToUtf32(text[position - totalSize], text[position - totalSize + 1]);
            }
        }
        return (totalSize, firstRune);
    }

    // completions.go:2336-2346 — trimElementAccess: `["ab c"]` -> `ab c`; `['ab c']` -> `ab c`;
    // `[123]` -> `123`.
    internal static string TrimElementAccess(string text)
    {
        if (text.StartsWith('['))
        {
            text = text[1..];
        }
        if (text.EndsWith(']'))
        {
            text = text[..^1];
        }
        if (text.StartsWith('\'') && text.EndsWith('\''))
        {
            text = text[1..^1];
        }
        if (text.StartsWith('"') && text.EndsWith('"'))
        {
            text = text[1..^1];
        }
        return text;
    }

    // completions.go:2348-2420 — getFilterText. Ported from vscode ts extension.
    internal static string GetFilterText(
        SourceFile file,
        int position,
        string insertText,
        string label,
        int wordStart,
        string dotAccessor)
    {
        // Private field completion, e.g. label `#bar`.
        if (label.StartsWith('#'))
        {
            var after = label[1..];
            if (insertText.Length != 0)
            {
                if (insertText.StartsWith("this.#", global::System.StringComparison.Ordinal))
                {
                    if (wordStart == '#')
                    {
                        // `method() { this.#| }`
                        // `method() { #| }`
                        return "";
                    }
                    else
                    {
                        // `method() { this.| }`
                        // `method() { | }`
                        return insertText["this.#".Length..];
                    }
                }
            }
            else
            {
                if (wordStart == '#')
                {
                    // `method() { this.#| }`
                    return "";
                }
                else
                {
                    // `method() { this.| }`
                    // `method() { | }`
                    return after;
                }
            }
        }

        // For `this.` completions, generally don't set the filter text since we don't want them to be overly deprioritized. microsoft/vscode#74164
        if (insertText.StartsWith("this.", global::System.StringComparison.Ordinal))
        {
            return "";
        }

        // Handle the case:
        // ```
        // const xyz = { 'ab c': 1 };
        // xyz.ab|
        // ```
        // In which case we want to insert a bracket accessor but should use `.abc` as the filter text instead of
        // the bracketed insert text.
        if (insertText.StartsWith('['))
        {
            return dotAccessor + TrimElementAccess(insertText);
        }

        if (insertText.StartsWith("?.", global::System.StringComparison.Ordinal))
        {
            // Handle this case like the case above:
            // ```
            // const xyz = { 'ab c': 1 } | undefined;
            // xyz.ab|
            // ```
            // filterText should be `.ab c` instead of `?.['ab c']`.
            if (insertText.StartsWith("?.[", global::System.StringComparison.Ordinal))
            {
                return dotAccessor + TrimElementAccess(insertText[2..]);
            }
            else
            {
                // ```
                // const xyz = { abc: 1 } | undefined;
                // xyz.ab|
                // ```
                // filterText should be `.abc` instead of `?.abc.
                return dotAccessor + insertText[2..];
            }
        }

        // In all other cases, fall back to using the insertText.
        return insertText;
    }

    // completions.go:2422-2434 — getDotAccessor. Ported from vscode's `provideCompletionItems`.
    internal static string GetDotAccessor(SourceFile file, int position)
    {
        var text = file.Text()[..position];
        if (text.EndsWith("?.", global::System.StringComparison.Ordinal))
        {
            return file.Text()[(position - 2)..position];
        }
        if (text.EndsWith('.'))
        {
            return file.Text()[(position - 1)..position];
        }
        return "";
    }

    // completions.go:2464-2477 — getLineEndOfPosition.
    internal static int GetLineEndOfPosition(SourceFile file, int pos)
    {
        var line = LsDeps.GetLineOfPosition(file, pos);
        var lineStarts = global::Ts.Scanner.Scanner.GetECMALineStarts(file);
        int lastCharPos;
        if (line + 1 >= lineStarts.Length)
        {
            lastCharPos = file.End;
        }
        else
        {
            lastCharPos = lineStarts[line + 1] - 1;
        }
        var fullText = file.Text();
        if (lastCharPos > 0 && lastCharPos < fullText.Length && fullText[lastCharPos] == '\n' && fullText[lastCharPos - 1] == '\r')
        {
            return lastCharPos - 1;
        }
        return lastCharPos;
    }

    // completions.go:2479-2482 — isClassLikeMemberCompletion.
    internal static bool IsClassLikeMemberCompletion(Symbol symbol, Node location, SourceFile file)
    {
        // !!! class member completions
        return false;
    }

    // completions.go:2484-2488 — symbolAppearsToBeTypeOnly.
    internal static bool SymbolAppearsToBeTypeOnly(Symbol symbol, TsChecker typeChecker)
    {
        var flags = TsChecker.SkipAlias(symbol, typeChecker)!.CombinedLocalAndExportSymbolFlags();
        return (flags & SymbolFlags.Value) == 0 &&
            ((symbol.Declarations ?? []).Length == 0 || !AstUtilities.IsInJSFile(symbol.Declarations![0]) || (flags & SymbolFlags.Type) != 0);
    }

    // completions.go:2490-2583 — shouldIncludeSymbol.
    internal static bool ShouldIncludeSymbol(
        Symbol symbol,
        CompletionDataData data,
        Node? closestSymbolDeclaration,
        SourceFile file,
        TsChecker typeChecker,
        global::Ts.Core.CompilerOptions compilerOptions)
    {
        var allFlags = symbol.Flags;
        var location = data.location!;
        // export = /**/ here we want to get all meanings, so any symbol is ok
        if (location.Parent != null && NodeTests.IsExportAssignment(location.Parent))
        {
            return true;
        }

        // Filter out variables from their own initializers
        // `const a = /* no 'a' here */`
        if (closestSymbolDeclaration != null &&
            NodeTests.IsVariableDeclaration(closestSymbolDeclaration) &&
            symbol.ValueDeclaration == closestSymbolDeclaration)
        {
            return false;
        }

        // Filter out current and latter parameters from defaults
        // `function f(a = /* no 'a' and 'b' here */, b) { }` or
        // `function f<T = /* no 'T' and 'T2' here */>(a: T, b: T2) { }`
        Node? symbolDeclaration = null;
        if (symbol.ValueDeclaration != null)
        {
            symbolDeclaration = symbol.ValueDeclaration;
        }
        else if ((symbol.Declarations ?? []).Length > 0)
        {
            symbolDeclaration = symbol.Declarations![0];
        }

        if (closestSymbolDeclaration != null && symbolDeclaration != null)
        {
            if (NodeTests.IsParameterDeclaration(closestSymbolDeclaration) && NodeTests.IsParameterDeclaration(symbolDeclaration))
            {
                var parameters = closestSymbolDeclaration.Parent!.ParameterList()!;
                if (symbolDeclaration.Pos >= closestSymbolDeclaration.Pos &&
                    symbolDeclaration.Pos < parameters.End)
                {
                    return false;
                }
            }
            else if (NodeTests.IsTypeParameterDeclaration(closestSymbolDeclaration) &&
                NodeTests.IsTypeParameterDeclaration(symbolDeclaration))
            {
                if (closestSymbolDeclaration == symbolDeclaration && data.contextToken != null && data.contextToken.Kind == SyntaxKind.ExtendsKeyword)
                {
                    // filter out the directly self-recursive type parameters
                    // `type A<K extends /* no 'K' here*/> = K`
                    return false;
                }
                if (IsInTypeParameterDefault(data.contextToken) && !NodeTests.IsInferTypeNode(closestSymbolDeclaration.Parent!))
                {
                    var typeParameters = closestSymbolDeclaration.Parent!.TypeParameterList();
                    if (typeParameters != null && symbolDeclaration.Pos >= closestSymbolDeclaration.Pos &&
                        symbolDeclaration.Pos < typeParameters.End)
                    {
                        return false;
                    }
                }
            }
        }

        // External modules can have global export declarations that will be
        // available as global keywords in all scopes. But if the external module
        // already has an explicit export and user only wants to use explicit
        // module imports then the global keywords will be filtered out so auto
        // import suggestions will win in the completion.
        var symbolOrigin = TsChecker.SkipAlias(symbol, typeChecker)!;
        // We only want to filter out the global keywords.
        // Auto Imports are not available for scripts so this conditional is always false.
        if (file.ExternalModuleIndicator != null &&
            compilerOptions.AllowUmdGlobalAccess != Tristate.True &&
            symbol != symbolOrigin &&
            data.symbolToSortTextMap != null &&
            data.symbolToSortTextMap.TryGetValue(AstUtilities.GetSymbolId(symbol), out var sortText) &&
            sortText == SortTextGlobalsOrKeywordsValue &&
            symbol.Parent != null && TsChecker.IsExternalModuleSymbol(symbol.Parent))
        {
            return false;
        }

        allFlags |= symbolOrigin.CombinedLocalAndExportSymbolFlags();
        if ((symbol.Flags & SymbolFlags.Alias) != 0)
        {
            allFlags |= typeChecker.GetSymbolFlags(symbol);
        }

        // import m = /**/ <-- It can only access namespace (if typing import = x. this would get member symbols and not namespace)
        if (LsMeaning.IsInRightSideOfInternalImportEqualsDeclaration(data.location!))
        {
            return (allFlags & SymbolFlags.Namespace) != 0;
        }

        if (data.isTypeOnlyLocation)
        {
            // It's a type, but you can reach it by namespace.type as well.
            return SymbolCanBeReferencedAtTypeLocation(symbol, typeChecker, []);
        }

        // expressions are value space (which includes the value namespaces)
        return (allFlags & SymbolFlags.Value) != 0;
    }

    // completions.go:2585-2644 — getCompletionEntryDisplayNameForSymbol.
    internal static (string DisplayName, bool NeedsConvertPropertyAccess) GetCompletionEntryDisplayNameForSymbol(
        Symbol symbol,
        SymbolOriginInfo? origin,
        CompletionKind completionKind,
        bool isJsxIdentifierExpected)
    {
        if (OriginIsIgnore(origin))
        {
            return ("", false);
        }

        string name;
        if (OriginIncludesSymbolName(origin))
        {
            name = origin!.SymbolName();
        }
        else
        {
            name = AstUtilities.SymbolName(symbol);
        }
        if (name.Length == 0 ||
            // If the symbol is external module, don't show it in the completion list
            // (i.e declare module "http" { const x; } | // <= request completion here, "http" should not be there)
            (symbol.Flags & SymbolFlags.Module) != 0 && StartsWithQuote(name) ||
            // If the symbol is the internal name of an ES symbol, it is not a valid entry. Internal names for ES symbols start with "__@"
            TsChecker.IsKnownSymbol(symbol))
        {
            return ("", false);
        }

        var variant = isJsxIdentifierExpected ? global::Ts.Core.LanguageVariant.Jsx : global::Ts.Core.LanguageVariant.Standard;
        // name is a valid identifier or private identifier text
        if (global::Ts.Scanner.Scanner.IsIdentifierText(name, variant) ||
            symbol.ValueDeclaration != null && AstUtilities.IsPrivateIdentifierClassElementDeclaration(symbol.ValueDeclaration))
        {
            return (name, false);
        }
        if ((symbol.Flags & SymbolFlags.Alias) != 0)
        {
            // Allow non-identifier import/export aliases since we can insert them as string literals
            return (name, true);
        }

        switch (completionKind)
        {
            case CompletionKind.MemberLike:
                if (OriginIsComputedPropertyName(origin))
                {
                    return (origin!.SymbolName(), false);
                }
                return ("", false);
            case CompletionKind.ObjectPropertyDeclaration:
                // TODO: microsoft/TypeScript#18169
                return (JsonQuoteString(name), false);
            case CompletionKind.PropertyAccess:
            case CompletionKind.Global:
                // For a 'this.' completion it will be in a global context, but may have a non-identifier name.
                // Don't add a completion for a name starting with a space. See https://github.com/Microsoft/TypeScript/pull/20547
                if (name.Length > 0 && name[0] == ' ')
                {
                    return ("", false);
                }
                return (name, true);
            case CompletionKind.None:
            case CompletionKind.String:
                return (name, false);
            default:
                throw new global::System.InvalidOperationException($"Unexpected completion kind: {completionKind}");
        }
    }

    // completions.go:3131-3141 — isStringAndEmptyAnonymousObjectIntersection: checks whether type is
    // `string & {}`, which is semantically equivalent to string but is not reduced.
    internal static bool IsStringAndEmptyAnonymousObjectIntersection(TsChecker typeChecker, CheckerType t)
    {
        if (!t.IsIntersection())
        {
            return false;
        }
        return t.Types().Length == 2 &&
            (AreIntersectedTypesAvoidingStringReduction(typeChecker, t.Types()[0], t.Types()[1]) ||
                AreIntersectedTypesAvoidingStringReduction(typeChecker, t.Types()[1], t.Types()[0]));
    }

    // completions.go:3144-3146 — areIntersectedTypesAvoidingStringReduction.
    internal static bool AreIntersectedTypesAvoidingStringReduction(TsChecker typeChecker, CheckerType t1, CheckerType t2)
    {
        return t1.IsString() && typeChecker.IsEmptyAnonymousObjectType(t2);
    }

    // completions.go:3148-3150 — escapeSnippetText.
    internal static string EscapeSnippetText(string text)
    {
        return text.Replace("$", "\\$", global::System.StringComparison.Ordinal);
    }

    // completions.go:3156-3193 — generateIdentifierForArbitraryString.
    internal static string GenerateIdentifierForArbitraryString(string text)
    {
        var needsUnderscore = false;
        var identifier = new global::System.Text.StringBuilder();

        // Convert "(example, text)" into "_example_text_"
        for (var pos = 0; pos < text.Length;)
        {
            int ch;
            int size;
            if (char.IsHighSurrogate(text[pos]) && pos + 1 < text.Length && char.IsLowSurrogate(text[pos + 1]))
            {
                ch = char.ConvertToUtf32(text[pos], text[pos + 1]);
                size = 2;
            }
            else
            {
                ch = text[pos];
                size = 1;
            }
            bool validChar;
            if (pos == 0)
            {
                validChar = global::Ts.Scanner.Scanner.IsIdentifierStart(ch);
            }
            else
            {
                validChar = global::Ts.Scanner.Scanner.IsIdentifierPart(ch);
            }
            if (validChar)
            {
                if (needsUnderscore)
                {
                    identifier.Append('_');
                }
                identifier.Append(char.ConvertFromUtf32(ch));
                needsUnderscore = false;
            }
            else
            {
                needsUnderscore = true;
            }
            pos += size;
        }

        if (needsUnderscore)
        {
            identifier.Append('_');
        }

        // Default to "_" if the provided text was empty
        var id = identifier.ToString();
        if (id.Length == 0)
        {
            return "_";
        }

        return id;
    }

    // completions.go:3196-3246 — getCompletionsSymbolKind. Copied from vscode TS extension.
    internal static global::Ts.LsProto.CompletionItemKind GetCompletionsSymbolKind(ScriptElementKind kind)
    {
        switch (kind)
        {
            case ScriptElementKind.PrimitiveType:
            case ScriptElementKind.Keyword:
                return global::Ts.LsProto.CompletionItemKind.Keyword;
            case ScriptElementKind.ConstElement:
            case ScriptElementKind.LetElement:
            case ScriptElementKind.VariableElement:
            case ScriptElementKind.LocalVariableElement:
            case ScriptElementKind.Alias:
            case ScriptElementKind.ParameterElement:
                return global::Ts.LsProto.CompletionItemKind.Variable;

            case ScriptElementKind.MemberVariableElement:
            case ScriptElementKind.MemberGetAccessorElement:
            case ScriptElementKind.MemberSetAccessorElement:
                return global::Ts.LsProto.CompletionItemKind.Field;

            case ScriptElementKind.FunctionElement:
            case ScriptElementKind.LocalFunctionElement:
                return global::Ts.LsProto.CompletionItemKind.Function;

            case ScriptElementKind.MemberFunctionElement:
            case ScriptElementKind.ConstructSignatureElement:
            case ScriptElementKind.CallSignatureElement:
            case ScriptElementKind.IndexSignatureElement:
                return global::Ts.LsProto.CompletionItemKind.Method;

            case ScriptElementKind.EnumElement:
                return global::Ts.LsProto.CompletionItemKind.Enum;

            case ScriptElementKind.EnumMemberElement:
                return global::Ts.LsProto.CompletionItemKind.EnumMember;

            case ScriptElementKind.ModuleElement:
            case ScriptElementKind.ExternalModuleName:
                return global::Ts.LsProto.CompletionItemKind.Module;

            case ScriptElementKind.ClassElement:
            case ScriptElementKind.TypeElement:
                return global::Ts.LsProto.CompletionItemKind.Class;

            case ScriptElementKind.InterfaceElement:
                return global::Ts.LsProto.CompletionItemKind.Interface;

            case ScriptElementKind.Warning:
                return global::Ts.LsProto.CompletionItemKind.Text;

            case ScriptElementKind.ScriptElement:
                return global::Ts.LsProto.CompletionItemKind.File;

            case ScriptElementKind.Directory:
                return global::Ts.LsProto.CompletionItemKind.Folder;

            case ScriptElementKind.String:
                return global::Ts.LsProto.CompletionItemKind.Constant;

            default:
                return global::Ts.LsProto.CompletionItemKind.Property;
        }
    }

    // completions.go:3252-3259 — CompareCompletionEntries.
    // Editors will use the `sortText` and then fall back to `name` for sorting, but leave ties in response order.
    // So, it's important that we sort those ties in the order we want them displayed if it matters. We don't
    // strictly need to sort by name or SortText here since clients are going to do it anyway, but we have to
    // do the work of comparing them so we can sort those ties appropriately.
    internal static int CompareCompletionEntries(global::Ts.LsProto.CompletionItem a, global::Ts.LsProto.CompletionItem b)
    {
        var result = global::Ts.StringUtil.StringUtil.CompareStringsCaseInsensitiveThenSensitive(a.SortText!, b.SortText!);
        if (result == global::Ts.StringUtil.StringUtil.ComparisonEqual)
        {
            result = global::Ts.StringUtil.StringUtil.CompareStringsCaseInsensitiveThenSensitive(a.Label, b.Label);
        }
        return result;
    }

    // completions.go:3480-3484 — isMemberCompletionKind.
    internal static bool IsMemberCompletionKind(CompletionKind kind)
    {
        return kind is CompletionKind.ObjectPropertyDeclaration or CompletionKind.Global or CompletionKind.PropertyAccess;
    }

    // completions.go:2007-2022 — completionNameForLiteral.
    internal static string CompletionNameForLiteral(SourceFile file, UserPreferences preferences, object literal)
    {
        switch (literal)
        {
            case string s:
                return Quote(file, preferences, s);
            case global::Ts.JsNum.Number n:
                // Go core.StringifyJson over jsnum.Number formats per RFC 8785 (= ES ToString),
                // which is exactly jsnum's own string form.
                return n.ToString();
            case global::Ts.JsNum.PseudoBigInt p:
                return p.ToString() + "n";
        }
        throw new global::System.InvalidOperationException($"Unhandled literal value: {literal}");
    }

    // completions.go:2024-2035 — createCompletionItemForLiteral.
    internal static global::Ts.LsProto.CompletionItem CreateCompletionItemForLiteral(
        SourceFile file,
        UserPreferences preferences,
        object literal)
    {
        return new global::Ts.LsProto.CompletionItem
        {
            Label = CompletionNameForLiteral(file, preferences, literal),
            Kind = global::Ts.LsProto.CompletionItemKind.Constant,
            SortText = SortTextLocationPriorityValue,
            CommitCharacters = [],
        };
    }

    // utilities.go:25 — quoteReplacer: strings.NewReplacer("'", `\'`, `\"`, `"`) — one pass,
    // earlier pairs win at each position.
    private static string QuoteReplace(string text)
    {
        var sb = new global::System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length;)
        {
            if (text[i] == '\'')
            {
                sb.Append("\\'");
                i++;
            }
            else if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == '"')
            {
                sb.Append('"');
                i += 2;
            }
            else
            {
                sb.Append(text[i]);
                i++;
            }
        }
        return sb.ToString();
    }

    // utilities.go:307-316 — quote.
    internal static string Quote(SourceFile file, UserPreferences preferences, string text)
    {
        // Editors can pass in undefined or empty string - we want to infer the preference in those cases.
        var quotePreference = LsDeps.GetQuotePreference(file, preferences);
        var quoted = JsonQuoteString(text);
        if (quotePreference == QuotePreference.Single)
        {
            quoted = QuoteReplace(global::Ts.StringUtil.StringUtil.StripQuotes(quoted));
        }
        return quoted;
    }

    // completions.go:3123-3129 — quotePropertyName.
    internal static string QuotePropertyName(SourceFile file, UserPreferences preferences, string name)
    {
        if (name.Length > 0 && char.IsAsciiDigit(name[0]))
        {
            return name;
        }
        return Quote(file, preferences, name);
    }

    // core.StringifyJson over a string — typed emitter for the AOT-banned reflection marshal
    // (plan/07). Mirrors go-json-experiment/jsontext string quoting: escape `"`, `\`, control
    // chars below 0x20 (named escapes for \b \f \n \r \t), and lone surrogates as U+FFFD; no
    // HTML escaping; all other characters pass through verbatim.
    internal static string JsonQuoteString(string text)
    {
        var sb = new global::System.Text.StringBuilder(text.Length + 2);
        sb.Append('"');
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", global::System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else if (char.IsHighSurrogate(c))
                    {
                        if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                        {
                            sb.Append(c).Append(text[i + 1]);
                            i++;
                        }
                        else
                        {
                            sb.Append('�');
                        }
                    }
                    else if (char.IsLowSurrogate(c))
                    {
                        sb.Append('�');
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    // completions.go:4822-4845 — clientSupports* read the LSP client capabilities off the request
    // context. PORT-NOTE: the raw API carries no LSP context yet (same shape as hover's
    // documentation-format probe); Go's zero-value ClientCapabilities yields false/empty for every
    // probe, so these return the headless defaults until the lsp-server wave threads real
    // capabilities through.
    private static bool ClientSupportsItemLabelDetails() => false;

    private static bool ClientSupportsItemSnippet() => false;

    private static bool ClientSupportsItemCommitCharacters() => false;

    private static bool ClientSupportsItemInsertReplace() => false;

    private static bool ClientSupportsDefaultCommitCharacters() => false;

    private static bool ClientSupportsDefaultEditRange() => false;

    // completions.go:4426-4518 — createLSPCompletionItem.
    internal global::Ts.LsProto.CompletionItem CreateLSPCompletionItem(
        string name,
        string insertText,
        string filterText,
        string sortText,
        ScriptElementKind elementKind,
        ScriptElementKindModifier kindModifiers,
        global::Ts.LsProto.Range? replacementSpan,
        string[]? commitCharacters,
        global::Ts.LsProto.CompletionItemLabelDetails? labelDetails,
        SourceFile file,
        int position,
        bool isMemberCompletion,
        bool isSnippet,
        bool hasAction,
        bool preselect,
        string source,
        object? autoImportFix,
        string? detail)
    {
        var kind = GetCompletionsSymbolKind(elementKind);
        var data = new global::Ts.LsProto.CompletionItemData
        {
            FileName = file.FileName(),
            Position = position,
            Source = source,
            Name = name,
            AutoImport = autoImportFix,
        };

        // Text edit
        global::Ts.LsProto.TextEditOrInsertReplaceEdit? textEdit = null;
        if (replacementSpan != null)
        {
            textEdit = new global::Ts.LsProto.TextEditOrInsertReplaceEdit
            {
                TextEdit = new global::Ts.LsProto.TextEdit
                {
                    NewText = insertText.Length == 0 ? name : insertText,
                    Range = replacementSpan,
                },
            };
        }

        // Filter text

        // Ported from vscode ts extension.
        var (wordSize, wordStart) = GetWordLengthAndStart(file, position);
        var dotAccessor = GetDotAccessor(file, position - wordSize);
        if (filterText.Length == 0)
        {
            filterText = GetFilterText(file, position, insertText, name, wordStart, dotAccessor);
        }

        // Adjustements based on kind modifiers.
        global::Ts.LsProto.CompletionItemTag[]? tags = null;
        // Copied from vscode ts extension: `MyCompletionItem.constructor`.
        if ((kindModifiers & ScriptElementKindModifier.Optional) != 0)
        {
            if (insertText.Length == 0)
            {
                insertText = name;
            }
            if (filterText.Length == 0)
            {
                filterText = name;
            }
            name += "?";
        }
        if ((kindModifiers & ScriptElementKindModifier.Deprecated) != 0)
        {
            tags = [global::Ts.LsProto.CompletionItemTag.Deprecated];
        }

        if (hasAction && source.Length != 0)
        {
            // !!! adjust label like vscode does
        }

        // Client assumes plain text by default.
        global::Ts.LsProto.InsertTextFormat? insertTextFormat = null;
        if (isSnippet)
        {
            insertTextFormat = global::Ts.LsProto.InsertTextFormat.Snippet;
        }

        return new global::Ts.LsProto.CompletionItem
        {
            Label = name,
            LabelDetails = labelDetails,
            Kind = kind,
            Tags = tags,
            Detail = detail,
            Preselect = preselect ? true : null,
            SortText = sortText,
            FilterText = filterText.Length == 0 ? null : filterText,
            InsertText = insertText.Length == 0 ? null : insertText,
            InsertTextFormat = insertTextFormat,
            TextEdit = textEdit,
            CommitCharacters = commitCharacters,
            Data = data,
        };
    }
}
