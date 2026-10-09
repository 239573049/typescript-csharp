// Unit tests for the --showConfig face (showconfig.go → ShowConfig.cs): ConvertToTSConfig's
// serialization (enum→name, path relativization, implied options appended in impliedOptions
// order, lib key restoration, paths nested maps, references/compileOnSave/exclude faces) and
// MarshalIndentWrite's byte shape (4-space indent, omitzero drops).
//
// Mirrors the shape of tsgo's tsctests/showconfig_test.go scenarios; the byte-exact baseline
// runner itself is deferred to the phase-4/8 batch harness (same note as TsConfigParsingTests).

using System;
using System.Collections.Generic;
using System.IO;
using Ts.TsOptions;
using Ts.Vfs;
using Ts.Vfs.VfsTest;
using Xunit;

namespace Ts.TsOptions.Tests;

public class ShowConfigTests
{
    private sealed class TestConfigHost : ParseConfigHost
    {
        private readonly IFileSystem fs;
        private readonly string cwd;

        public TestConfigHost(IFileSystem fs, string cwd)
        {
            this.fs = fs;
            this.cwd = cwd;
        }

        public IFileSystem FS() => fs;
        public string GetCurrentDirectory() => cwd;
    }

    // Load /src/tsconfig.json and render --showConfig JSON for it.
    private static string Show(Dictionary<string, string> files, string configFileName = "/src/tsconfig.json", string cwd = "/src")
    {
        var host = new TestConfigHost(VfsTestUtil.FromMap(files, useCaseSensitiveFileNames: true), cwd);
        var (cmd, errors) = TsConfigParsing.GetParsedCommandLineOfConfigFile(configFileName, null, null, host, null);
        Assert.NotNull(cmd);
        // tsc.go:215-221 exits on config errors before showConfig; keep the invariant visible here.
        Assert.True(errors == null || errors.Length == 0, "config errors: " + (errors == null ? "" : string.Join("\n", errors)));
        var tsConfig = ShowConfig.ConvertToTSConfig(cmd!, configFileName);
        var w = new StringWriter();
        ShowConfig.MarshalIndentWrite(w, tsConfig);
        return w.ToString();
    }

    private static void AssertLines(string output, params string[] lines)
    {
        // Every line must appear, in order, with its 4-space JSON indentation preserved.
        int index = 0;
        foreach (var line in lines)
        {
            var found = output.IndexOf(line, index, StringComparison.Ordinal);
            Assert.True(found >= 0, $"expected line {line} after offset {index} in:\n{output}");
            index = found + line.Length;
        }
    }

    [Fact]
    public void ImpliedOptions_AreAppended_AfterExplicitOnes_InDeclarationOrder()
    {
        // tsgo baseline "Show TSConfig with transitively implied options": module nodenext
        // implies moduleResolution nodenext + moduleDetection force, appended in this order.
        var output = Show(new Dictionary<string, string>
        {
            ["/src/tsconfig.json"] = "{ \"compilerOptions\": { \"module\": \"nodenext\" } }",
            ["/src/index.ts"] = "export const a = 1;",
        });

        AssertLines(output,
            "{",
            "    \"compilerOptions\": {",
            "        \"module\": \"nodenext\",",
            "        \"moduleResolution\": \"nodenext\",",
            "        \"moduleDetection\": \"force\"",
            "    },");
        Assert.Contains("\"files\": [", output);
        Assert.DoesNotContain("\"include\"", output);
        Assert.DoesNotContain("\"showConfig\"", output);
        Assert.DoesNotContain("\"configFilePath\"", output);
    }

    [Fact]
    public void EnumBooleanAndPathOptions_Serialize_LikeTsgo()
    {
        // tsgo baseline "Show TSConfig with compileOnSave and more": strict/target/module/
        // esModuleInterop serialize in CompilerOptions field order; useDefineForClassFields
        // is implied (false under target es5 vs the ESNext default).
        var output = Show(new Dictionary<string, string>
        {
            ["/src/tsconfig.json"] = @"{
                ""compilerOptions"": {
                    ""esModuleInterop"": true,
                    ""target"": ""es5"",
                    ""module"": ""commonjs"",
                    ""strict"": true
                }
            }",
            ["/src/index.ts"] = "export const a = 1;",
        });

        AssertLines(output,
            "    \"compilerOptions\": {",
            "        \"module\": \"commonjs\",",
            "        \"strict\": true,",
            "        \"target\": \"es5\",",
            "        \"esModuleInterop\": true,",
            "        \"useDefineForClassFields\": false");
    }

    [Fact]
    public void LibList_RestoresEnumKeys_NotDtsFileNames()
    {
        var output = Show(new Dictionary<string, string>
        {
            ["/src/tsconfig.json"] = "{ \"compilerOptions\": { \"lib\": [\"es5\", \"es2015.core\"] }, \"files\": [] }",
            ["/src/index.ts"] = "export const a = 1;",
        });

        AssertLines(output,
            "        \"lib\": [",
            "            \"es5\",",
            "            \"es2015.core\"");
        Assert.DoesNotContain("lib.es5.d.ts", output);
    }

    [Fact]
    public void Paths_NestedMap_PreservesInsertionOrder_AndValues()
    {
        var output = Show(new Dictionary<string, string>
        {
            ["/src/tsconfig.json"] = @"{
                ""compilerOptions"": {
                    ""baseUrl"": ""."",
                    ""paths"": {
                        ""@root/*"": [""./*""],
                        ""*"": [""node_modules/*"", ""src/types/*""]
                    }
                },
                ""include"": [""src/**/*""]
            }",
            ["/src/index.ts"] = "export const a = 1;",
        });

        AssertLines(output,
            "        \"paths\": {",
            "            \"@root/*\": [",
            "                \"./*\"",
            "            ],",
            "            \"*\": [",
            "                \"node_modules/*\",",
            "                \"src/types/*\"");
    }

    [Fact]
    public void ReferencesAndExclude_AreCarriedThrough_CompileOnSaveOnlyWhenTrue()
    {
        var output = Show(new Dictionary<string, string>
        {
            ["/src/tsconfig.json"] = @"{
                ""compilerOptions"": { ""composite"": true, ""strict"": true },
                ""compileOnSave"": true,
                ""exclude"": [""dist""],
                ""include"": [""src/*""],
                ""references"": [{ ""path"": ""./test"" }]
            }",
            ["/src/index.ts"] = "export const a = 1;",
        });

        AssertLines(output,
            "    \"references\": [",
            "        {",
            "            \"path\": \"./test\"",
            "        }",
            "    ],");
        AssertLines(output,
            "    \"include\": [",
            "        \"src/*\"",
            "    ],",
            "    \"exclude\": [",
            "        \"dist\"",
            "    ]");
        // Go tsconfigparsing.go:938 keeps convertCompileOnSaveOptionFromJson commented out, so
        // ParsedCommandLine.CompileOnSave stays nil and the key is never emitted — matching the
        // tsgo baseline "Show TSConfig with compileOnSave and more" (input has compileOnSave:
        // true, output has none).
        Assert.DoesNotContain("\"compileOnSave\"", output);
        // Implied from composite: declaration + incremental (declarationMap needs declarationMap set).
        Assert.Contains("\"declaration\": true", output);
        Assert.Contains("\"incremental\": true", output);
    }

    [Fact]
    public void DefaultIncludeSpec_IsFiltered_OutOfOutput()
    {
        var output = Show(new Dictionary<string, string>
        {
            ["/src/tsconfig.json"] = "{ \"compilerOptions\": { \"strict\": true } }",
            ["/src/index.ts"] = "export const a = 1;",
        });

        // include ["**/*"] (the default spec) is omitted; exclude defaults to nothing here.
        Assert.DoesNotContain("\"include\"", output);
        Assert.DoesNotContain("**/*", output);
    }

    [Fact]
    public void NoTrailingNewline_AfterClosingBrace()
    {
        // go-json-experiment MarshalWrite appends no trailing newline.
        var output = Show(new Dictionary<string, string>
        {
            ["/src/tsconfig.json"] = "{ \"compilerOptions\": { \"strict\": true }, \"files\": [\"index.ts\"] }",
            ["/src/index.ts"] = "export const a = 1;",
        });
        Assert.Equal('}', output[^1]);
        Assert.StartsWith("{", output);
    }
}
