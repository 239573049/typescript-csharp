// Ported from tsgo internal/ls/lsutil/userpreferences.go (completion-relevant subset).
// PORT-NOTE: the full UserPreferences surface (format settings, auto-import/organize-imports
// preference fields, and the reflect-tag-driven raw/config population at userpreferences.go:150+)
// lands with the lsp/config wave; Go's reflection walk becomes a generated per-field switch per
// plan/07. This subset carries exactly the fields getCompletionData and its helpers read.

using Ts.Core;

namespace Ts.Ls;

// userpreferences.go:45 — UserPreferences represents TypeScript language service preferences.
public sealed class UserPreferences
{
    // userpreferences.go:48 — QuotePreference (string enum: "" | "auto" | "double" | "single").
    public string QuotePreference = "";
    // userpreferences.go:78 — JsxAttributeCompletionStyle ("auto" | "braces" | "none").
    public string JsxAttributeCompletionStyle = "";
    // If enabled, TypeScript will search through all external modules' exports and add them to the completions list.
    // This affects lone identifier completions but not completions on the right hand side of `obj.`.
    public Tristate IncludeCompletionsForModuleExports;
    // Enables auto-import-style completions on partially-typed import statements. E.g., allows
    // `import write|` to be completed to `import { writeFile } from "fs"`.
    public Tristate IncludeCompletionsForImportStatements;
    // Unless this option is `false`,  member completion lists triggered with `.` will include entries
    // on potentially-null and potentially-undefined values, with insertion text to replace
    // preceding `.` tokens with `?.`.
    public Tristate IncludeAutomaticOptionalChainCompletions;
    // If enabled, completions for class members (e.g. methods and properties) will include
    // a whole declaration for the member.
    public Tristate IncludeCompletionsWithClassMemberSnippets;
    // If enabled, object literal methods will have a method declaration completion entry in addition
    // to the regular completion entry containing just the method name.
    public Tristate IncludeCompletionsWithObjectLiteralMethodSnippets;
    // If enabled, completions will include snippet-style entries (e.g. class member snippets).
    public Tristate IncludeCompletionsWithSnippetText;
    // userpreferences.go:15-35 — NewDefaultUserPreferences (completion-relevant subset).
    public static UserPreferences NewDefaultUserPreferences() => new()
    {
        IncludeCompletionsForModuleExports = Tristate.True,
        IncludeCompletionsForImportStatements = Tristate.True,
    };
}
