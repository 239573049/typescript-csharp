// Ported from tsgo internal/lsp/stack_sanitizer_test.go (whole file).
//
// PORT-NOTES:
//  - The three Go raw-string inputs are embedded byte-for-byte (tabs and all);
//    the closing `"""` sits at column 0 so C# strips no indentation and adds
//    no trailing newline (C# multiline raw strings end before the final
//    newline, exactly like Go's backtick literals).
//  - Go's t.Name() feeds the baseline header; the Go test name is passed
//    explicitly so the mirrored reference baselines under
//    testdata/baselines/reference/lsp/stackSanitizer stay byte-exact.

using System;
using System.Text;
using System.Text.RegularExpressions;
using Ts.TestUtil;
using Xunit;

namespace Ts.Lsp.Tests;

public sealed partial class StackSanitizerTests
{
    // stack_sanitizer_test.go:11-47 — TestSanitizedDebugStackTraceCompletionsRequest.
    // This test uses non-trimmed paths to emulate debug builds.
    // Most users won't actually see this.
    [Fact]
    public void SanitizedDebugStackTraceCompletionsRequest()
    {
        var input = """
goroutine 1196 [running]:
runtime/debug.Stack()
        /usr/local/go/src/runtime/debug/stack.go:26 +0x8e
github.com/microsoft/typescript-go/internal/lsp.(*Server).recover(0xc0001dae08, {0x14bc418, 0xc00bc60960}, 0xc00baf16e0)
        /workspaces/typescript-go/internal/lsp/server.go:777 +0x65
panic({0x1077b40?, 0x1abcb70?})
        /usr/local/go/src/runtime/panic.go:783 +0x136
github.com/microsoft/typescript-go/internal/ls.(*LanguageService).getCompletionData.func15()
        /workspaces/typescript-go/internal/ls/completions.go:1303 +0xfa
github.com/microsoft/typescript-go/internal/ls.(*LanguageService).getCompletionData.func18()
        /workspaces/typescript-go/internal/ls/completions.go:1548 +0x2df
github.com/microsoft/typescript-go/internal/ls.(*LanguageService).getCompletionData(0xc004b08240, {0x14bc418, 0xc00bc60a20}, 0xc0069ef908, 0xc000272008, 0x1b, 0xc002b28e00)
        /workspaces/typescript-go/internal/ls/completions.go:1581 +0x2b92
github.com/microsoft/typescript-go/internal/ls.(*LanguageService).getCompletionsAtPosition(0xc004b08240, {0x14bc418, 0xc00bc60a20}, 0xc000272008, 0x1b, 0x0)
        /workspaces/typescript-go/internal/ls/completions.go:347 +0x690
github.com/microsoft/typescript-go/internal/ls.(*LanguageService).ProvideCompletion(0xc004b08240, {0x14bc418, 0xc00bc60a20}, {0xc0092e02a0, 0x28}, {0x2, 0x4}, 0xc004580c30)
        /workspaces/typescript-go/internal/ls/completions.go:47 +0x207
github.com/microsoft/typescript-go/internal/lsp.(*Server).handleCompletion(0xc0001dae08, {0x14bc418, 0xc00bc60960}, 0xc004b08240, 0xc00baf14d0)
        /workspaces/typescript-go/internal/lsp/server.go:1102 +0xe5
github.com/microsoft/typescript-go/internal/lsp.registerLanguageServiceWithAutoImportsRequestHandler[...].func1({0x14bc418, 0xc00bc60960}, 0xc00baf16e0)
        /workspaces/typescript-go/internal/lsp/server.go:682 +0x32a
github.com/microsoft/typescript-go/internal/lsp.(*Server).handleRequestOrNotification(0xc0001dae08, {0x14bc418, 0xc00bc60960}, 0xc00baf16e0)
        /workspaces/typescript-go/internal/lsp/server.go:531 +0x11e
github.com/microsoft/typescript-go/internal/lsp.(*Server).dispatchLoop.func1()
        /workspaces/typescript-go/internal/lsp/server.go:414 +0x65
created by github.com/microsoft/typescript-go/internal/lsp.(*Server).dispatchLoop in goroutine 19
        /workspaces/typescript-go/internal/lsp/server.go:438 +0x60
""";

        var t = new BaselineTestContext();
        Baseline.Run(t, "completionsDebugStackTrace.md", SanitizedStackTraceBaselineContents("TestSanitizedDebugStackTraceCompletionsRequest", input, StackSanitizer.SanitizeStackTrace(input)), new Baseline.Options
        {
            Subfolder = "lsp/stackSanitizer/",
        });
        Assert.False(t.Failed, string.Join("\n", t.Errors));
    }

    // stack_sanitizer_test.go:49-84 — TestSanitizedReleaseStackTraceCompletionsRequest.
    [Fact]
    public void SanitizedReleaseStackTraceCompletionsRequest()
    {
        var input = """
runtime error: invalid memory address or nil pointer dereference
goroutine 2331 [running]:
runtime/debug.Stack()
	runtime/debug/stack.go:26 +0x5e
github.com/microsoft/typescript-go/internal/lsp.(*Server).recover(0xc0001c6e08, {0x441ae5?, 0xc000e976c0?}, 0xc00ab6c7b0)
	github.com/microsoft/typescript-go/internal/lsp/server.go:777 +0x58
panic({0xc323a0?, 0x1780b90?})
	runtime/panic.go:783 +0x132
github.com/microsoft/typescript-go/internal/ls.(*LanguageService).getCompletionData.func15()
	github.com/microsoft/typescript-go/internal/ls/completions.go:1303 +0xba
github.com/microsoft/typescript-go/internal/ls.(*LanguageService).getCompletionData.func18(...)
	github.com/microsoft/typescript-go/internal/ls/completions.go:1548
github.com/microsoft/typescript-go/internal/ls.(*LanguageService).getCompletionData(0xc008329200, {0x10f6688, 0xc00c2871d0}, 0xc00190b308, 0xc0001fe008, 0x1b, 0xc0008a2f00)
	github.com/microsoft/typescript-go/internal/ls/completions.go:1581 +0x1ed4
github.com/microsoft/typescript-go/internal/ls.(*LanguageService).getCompletionsAtPosition(0xc008329200, {0x10f6688, 0xc00c2871d0}, 0xc0001fe008, 0x1b, 0x0)
	github.com/microsoft/typescript-go/internal/ls/completions.go:347 +0x35f
github.com/microsoft/typescript-go/internal/ls.(*LanguageService).ProvideCompletion(0xc008329200, {0x10f6688, 0xc00c287110}, {0xc00b472030?, 0xc00c287110?}, {0xb472030?, 0xc0?}, 0xc00c3ea000)
	github.com/microsoft/typescript-go/internal/ls/completions.go:47 +0x11c
github.com/microsoft/typescript-go/internal/lsp.(*Server).handleCompletion(0x418834?, {0x10f6688?, 0xc00c287110?}, 0xc00b472030?, 0x10f6688?)
	github.com/microsoft/typescript-go/internal/lsp/server.go:1105 +0x39
github.com/microsoft/typescript-go/internal/lsp.init.func1.registerLanguageServiceWithAutoImportsRequestHandler[...].28({0x10f6688, 0xc00c287110}, 0xc00ab6c7b0)
	github.com/microsoft/typescript-go/internal/lsp/server.go:682 +0x16c
github.com/microsoft/typescript-go/internal/lsp.(*Server).handleRequestOrNotification(0xc0001c6e08, {0x10f66c0?, 0xc006589180?}, 0xc00ab6c7b0)
	github.com/microsoft/typescript-go/internal/lsp/server.go:531 +0x1c6
github.com/microsoft/typescript-go/internal/lsp.(*Server).dispatchLoop.func1()
	github.com/microsoft/typescript-go/internal/lsp/server.go:414 +0x3a
created by github.com/microsoft/typescript-go/internal/lsp.(*Server).dispatchLoop in goroutine 35
	github.com/microsoft/typescript-go/internal/lsp/server.go:438 +0x9f1
""";

        var t = new BaselineTestContext();
        Baseline.Run(t, "completionsReleaseStackTrace.md", SanitizedStackTraceBaselineContents("TestSanitizedReleaseStackTraceCompletionsRequest", input, StackSanitizer.SanitizeStackTrace(input)), new Baseline.Options
        {
            Subfolder = "lsp/stackSanitizer/",
        });
        Assert.False(t.Failed, string.Join("\n", t.Errors));
    }

    // stack_sanitizer_test.go:86-96 — sanitizedStackTraceBaselineContents.
    private static string SanitizedStackTraceBaselineContents(string testName, string input, string output)
    {
        var builder = new StringBuilder();
        builder.Append("Test name: `");
        builder.Append(testName);
        builder.Append("`\n\n# Unsanitized input:\n\n````\n");
        builder.Append(input);
        builder.Append("\n````\n\n# Sanitized output:\n\n````\n");
        builder.Append(output);
        builder.Append("\n````\n");
        return builder.ToString();
    }

    // stack_sanitizer_test.go:98-102 — vscodeGenericSecretRegex.
    // Mirror of the "Generic Secret" pattern from VS Code's
    // removePropertiesWithPossibleUserInfo. If this matches the sanitized output,
    // VS Code's telemetry pipeline will replace the entire string with
    // `<REDACTED: Generic Secret>`, destroying the stack trace.
    [GeneratedRegex(@"(?i)(key|token|sig|secret|signature|password|passwd|pwd|android:value)[^a-zA-Z0-9]")]
    private static partial Regex VscodeGenericSecretRegex();

    // stack_sanitizer_test.go:104-132 — TestSanitizedStackTraceDefeatsVSCodeGenericSecretRegex.
    [Fact]
    public void SanitizedStackTraceDefeatsVSCodeGenericSecretRegex()
    {
        // Frame names contain identifiers that contain trigger keywords:
        // `getSignatureHelp` (signature), `LookupKey` (key), `validateToken` (token),
        // `signRequest` (sig), `setPwd` (pwd), and a file `signature.go`.
        var input = """
goroutine 7 [running]:
runtime/debug.Stack()
	runtime/debug/stack.go:26 +0x5e
github.com/microsoft/typescript-go/internal/ls.(*LanguageService).getSignatureHelp(0x1)
	github.com/microsoft/typescript-go/internal/ls/signature.go:42 +0x10
github.com/microsoft/typescript-go/internal/ls.LookupKey(0x2)
	github.com/microsoft/typescript-go/internal/ls/keys.go:7 +0x10
github.com/microsoft/typescript-go/internal/ls.validateToken(0x3)
	github.com/microsoft/typescript-go/internal/ls/token.go:9 +0x10
github.com/microsoft/typescript-go/internal/ls.signRequest(0x4)
	github.com/microsoft/typescript-go/internal/ls/sig.go:11 +0x10
github.com/microsoft/typescript-go/internal/ls.setPwd(0x5)
	github.com/microsoft/typescript-go/internal/ls/pwd.go:13 +0x10
""";

        string output = StackSanitizer.SanitizeStackTrace(input);
        Match loc = VscodeGenericSecretRegex().Match(output);
        if (loc.Success)
        {
            Assert.Fail($"sanitized stack trace would be redacted by VS Code's Generic Secret regex at [{loc.Index}, {loc.Index + loc.Length}): \"{loc.Value}\"\nfull output:\n{output}");
        }

        var t = new BaselineTestContext();
        Baseline.Run(t, "genericSecretWorkaround.md", SanitizedStackTraceBaselineContents("TestSanitizedStackTraceDefeatsVSCodeGenericSecretRegex", input, output), new Baseline.Options
        {
            Subfolder = "lsp/stackSanitizer/",
        });
        Assert.False(t.Failed, string.Join("\n", t.Errors));
    }
}
