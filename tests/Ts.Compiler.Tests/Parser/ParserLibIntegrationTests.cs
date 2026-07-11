// Phase-3 integration smoke: parse the real embedded lib.*.d.ts files end-to-end.
// These are large, valid TypeScript declaration files; the parser must consume each
// without crashing and produce zero parse diagnostics. This is the available substitute
// for the G3.1 full-corpus zero-crash gate, which is blocked by R24 (dumptool needs a
// tsgo git checkout). See plan/09-phases.md §5, plan/12-risks.md R24.

using System;
using System.IO;
using System.Reflection;
using Ts.Ast;
using Ts.Core;
using Xunit;

namespace Ts.Parser.Tests;

public class ParserLibIntegrationTests
{
    private static string ReadLib(string name)
    {
        Assembly asm = typeof(Ts.Core.TsVersion).Assembly;
        string resource = $"bundled/libs/{name}";
        using Stream? s = asm.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"embedded lib not found: {resource}");
        using var reader = new StreamReader(s);
        return reader.ReadToEnd();
    }

    [Theory]
    [InlineData("lib.es5.d.ts")]
    [InlineData("lib.es2015.core.d.ts")]
    [InlineData("lib.es2020.promise.d.ts")]
    [InlineData("lib.decorators.d.ts")]
    public void ParsesRealLibDtsWithoutCrashOrDiagnostics(string libName)
    {
        string text = ReadLib(libName);
        Assert.True(text.Length > 1000, $"{libName} should be a substantial file");

        var opts = new SourceFileParseOptions { FileName = "/" + libName, Path = new Ts.TsPath.Path("/" + libName) };
        SourceFile sf = global::Ts.Parser.Parser.ParseSourceFile(opts, text, ScriptKind.TS);

        Assert.True(sf.IsDeclarationFile, $"{libName} is a .d.ts");
        Diagnostic[] diags = sf.Diagnostics() ?? Array.Empty<Diagnostic>();
        Assert.True(diags.Length == 0, $"{libName} parsed with {diags.Length} diagnostics (first code: {(diags.Length > 0 ? diags[0].Code() : 0)} @ {(diags.Length > 0 ? diags[0].Pos() : 0)})");
        Assert.True((sf.Statements?.Nodes.Length ?? 0) >= 1, $"{libName} should yield a non-empty statement list");
    }
}
