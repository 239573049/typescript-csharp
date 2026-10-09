// Ported from tsgo internal/project/ata/ata_test.go (whole file).
//
// PORT-NOTEs:
//  * One Go test function (TestATA) → one xUnit class; t.Run subtests → [Fact]s named
//    TestATA_<Subtest> (the two ataDisabledCases table rows become one [Fact] per row).
//  * The Go `if !bundled.Embedded { t.Skip(...) }` guard is omitted: Bundled.Embedded is
//    `const true` in the C# build, so the branch is unreachable (and CS0162 fatal).
//  * context.Background() → RequestContext.Background; lsproto.LanguageKindJavaScript →
//    LanguageKind.JavaScript.Value!.
//  * `assert.NilError(t, err)` → Assert.Null(err) on the (ls, err) tuple slot; Go
//    `utils.FS().WriteFile(...)` error checks are dropped — the C# IFileSystem throws on
//    failure (05 §4), failing the test exactly where Go's assert.NilError would.
//  * `slices.Contains(args, x)` → Assert.Contains(x, args); `assert.DeepEqual(t, args,
//    []string{...})` → Assert.Equal(new[]{...}, args) (order-bearing sequence compare).
//  * The three preference-driven subtests ("ATA disabled via unified/deprecated setting",
//    "ATA re-enabled after being disabled") construct UserPreferences fields directly
//    instead of lsutil.ParseUserPreferences(tc.config): the raw-dictionary preference
//    parser (internal/ls/lsutil/userpreferences.go) is not ported yet. The parsed shapes
//    map to AutomaticTypeAcquisitionEnabled=False (unified) and
//    DisableAutomaticTypeAcquisition=True (deprecated); the behavior under test — no npm
//    install while ATA is off, and a diagnostics refresh when it turns back on — is
//    unchanged. (Same approach as CustomConfigFileNameTests' non-parser paths.)
//  * "deduplicate from local @types packages" keeps its Go t.Skip status.

using System.Collections.Generic;
using Ts.Core;
using Ts.TestUtil.ProjectTestUtil;
using Xunit;

using DocumentUri = global::Ts.LsProto.DocumentUri;
using FileChangeType = global::Ts.LsProto.FileChangeType;
using FileEvent = global::Ts.LsProto.FileEvent;
using LanguageKind = global::Ts.LsProto.LanguageKind;
using UserPreferences = global::Ts.Ls.UserPreferences;

namespace Ts.Project.Tests.Ata;

public sealed class AtaTests
{
    // ata_test.go:22-56 — "local module should not be picked up".
    [Fact]
    public void TestATA_LocalModuleShouldNotBePickedUp()
    {
        var files = new Dictionary<string, object?>
        {
            ["/user/username/projects/project/app.js"] = @"const c = require('./config');",
            ["/user/username/projects/project/config.js"] = @"export let x = 1",
            ["/user/username/projects/project/jsconfig.json"] = @"{
					""compilerOptions"": { ""moduleResolution"": ""commonjs"" },
					""typeAcquisition"": { ""enable"": true }
				}",
        };

        var testOptions = new TypingsInstallerOptions
        {
            TypesRegistry = ["config"],
        };

        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.SetupWithTypingsInstaller(files, testOptions);
        DocumentUri uri = "file:///user/username/projects/project/app.js";
        string content = (string)files["/user/username/projects/project/app.js"]!;

        // Open the file
        session.DidOpenFile(RequestContext.Background, uri, 1, content, LanguageKind.JavaScript.Value!);
        session.WaitForBackgroundTasks();
        (global::Ts.Ls.LanguageService? ls, System.Exception? err) = session.GetLanguageService(RequestContext.Background, uri);
        Assert.Null(err);
        // Verify the local config.js file is included in the program
        var program = ls!.GetProgram();
        Assert.NotNull(program);
        var configFile = program!.GetSourceFile("/user/username/projects/project/config.js");
        Assert.True(configFile != null, "local config.js should be included");

        // Verify that only types-registry was installed (no @types/config since it's a local module)
        var npmCalls = utils.NpmExecutor().NpmInstallCalls();
        Assert.Single(npmCalls);
        Assert.Equal("types-registry@latest", npmCalls[0].Args[2]);
    }

    // ata_test.go:58-90 — "configured projects".
    [Fact]
    public void TestATA_ConfiguredProjects()
    {
        var files = new Dictionary<string, object?>
        {
            ["/user/username/projects/project/app.js"] = @"",
            ["/user/username/projects/project/tsconfig.json"] = @"{
				""compilerOptions"": { ""allowJs"": true },
				""typeAcquisition"": { ""enable"": true },
			}",
            ["/user/username/projects/project/package.json"] = @"{
				""name"": ""test"",
				""dependencies"": {
					""jquery"": ""^3.1.0""
				}
			}",
        };

        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.SetupWithTypingsInstaller(files, new TypingsInstallerOptions
        {
            PackageToFile = new Dictionary<string, string>
            {
                ["jquery"] = @"declare const $: { x: number }",
            },
        });

        session.DidOpenFile(RequestContext.Background, "file:///user/username/projects/project/app.js", 1, (string)files["/user/username/projects/project/app.js"]!, LanguageKind.JavaScript.Value!);
        session.WaitForBackgroundTasks();
        var npmCalls = utils.NpmExecutor().NpmInstallCalls();
        Assert.Equal(2, npmCalls.Count);
        Assert.Equal(ProjectTestUtilConstants.TestTypingsLocation, npmCalls[0].Cwd);
        Assert.Equal("types-registry@latest", npmCalls[0].Args[2]);
        Assert.Equal(ProjectTestUtilConstants.TestTypingsLocation, npmCalls[1].Cwd);
        Assert.Contains("@types/jquery@latest", (IEnumerable<string>)npmCalls[1].Args);
        Assert.Single(utils.Client().RefreshDiagnosticsCalls());
    }

    // ata_test.go:92-127 — "inferred projects".
    [Fact]
    public void TestATA_InferredProjects()
    {
        var files = new Dictionary<string, object?>
        {
            ["/user/username/projects/project/app.js"] = @"",
            ["/user/username/projects/project/package.json"] = @"{
				""name"": ""test"",
				""dependencies"": {
					""jquery"": ""^3.1.0""
				}
			}",
        };

        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.SetupWithTypingsInstaller(files, new TypingsInstallerOptions
        {
            PackageToFile = new Dictionary<string, string>
            {
                ["jquery"] = @"declare const $: { x: number }",
            },
        });

        session.DidOpenFile(RequestContext.Background, "file:///user/username/projects/project/app.js", 1, (string)files["/user/username/projects/project/app.js"]!, LanguageKind.JavaScript.Value!);
        session.WaitForBackgroundTasks();
        // Check that npm install was called twice
        var calls = utils.NpmExecutor().NpmInstallCalls();
        Assert.Equal(2, calls.Count); // Go message: "Expected exactly 2 npm install calls"
        Assert.Equal(ProjectTestUtilConstants.TestTypingsLocation, calls[0].Cwd);
        Assert.Equal(new[] { "install", "--ignore-scripts", "types-registry@latest" }, calls[0].Args);
        Assert.Equal(ProjectTestUtilConstants.TestTypingsLocation, calls[1].Cwd);
        Assert.Equal("@types/jquery@latest", calls[1].Args[2]);

        // Verify the types file was installed
        (global::Ts.Ls.LanguageService? ls, System.Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///user/username/projects/project/app.js");
        Assert.Null(err);
        var program = ls!.GetProgram();
        var jqueryTypesFile = program!.GetSourceFile(ProjectTestUtilConstants.TestTypingsLocation + "/node_modules/@types/jquery/index.d.ts");
        Assert.True(jqueryTypesFile != null, "jquery types should be installed");
    }

    // ata_test.go:129-153 — "type acquisition with disableFilenameBasedTypeAcquisition:true".
    [Fact]
    public void TestATA_TypeAcquisitionWithDisableFilenameBasedTypeAcquisitionTrue()
    {
        var files = new Dictionary<string, object?>
        {
            ["/user/username/projects/project/jquery.js"] = @"",
            ["/user/username/projects/project/tsconfig.json"] = @"{
				""compilerOptions"": { ""allowJs"": true },
				""typeAcquisition"": { ""enable"": true, ""disableFilenameBasedTypeAcquisition"": true }
			}",
        };

        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.SetupWithTypingsInstaller(files, new TypingsInstallerOptions
        {
            TypesRegistry = ["jquery"],
        });

        // Should only get types-registry install, no jquery install since filename-based acquisition is disabled
        session.DidOpenFile(RequestContext.Background, "file:///user/username/projects/project/jquery.js", 1, (string)files["/user/username/projects/project/jquery.js"]!, LanguageKind.JavaScript.Value!);
        session.WaitForBackgroundTasks();

        // Check that npm install was called once (only types-registry)
        var calls = utils.NpmExecutor().NpmInstallCalls();
        Assert.Single(calls); // Go message: "Expected exactly 1 npm install call"
        Assert.Equal(ProjectTestUtilConstants.TestTypingsLocation, calls[0].Cwd);
        Assert.Equal(new[] { "install", "--ignore-scripts", "types-registry@latest" }, calls[0].Args);
    }

    // ata_test.go:155-190 — "discover from node_modules".
    [Fact]
    public void TestATA_DiscoverFromNodeModules()
    {
        var files = new Dictionary<string, object?>
        {
            ["/user/username/projects/project/app.js"] = "",
            ["/user/username/projects/project/package.json"] = @"{
			    ""dependencies"": {
					""jquery"": ""1.0.0""
				}
			}",
            ["/user/username/projects/project/jsconfig.json"] = @"{}",
            ["/user/username/projects/project/node_modules/commander/index.js"] = "",
            ["/user/username/projects/project/node_modules/commander/package.json"] = @"{ ""name"": ""commander"" }",
            ["/user/username/projects/project/node_modules/jquery/index.js"] = "",
            ["/user/username/projects/project/node_modules/jquery/package.json"] = @"{ ""name"": ""jquery"" }",
            ["/user/username/projects/project/node_modules/jquery/nested/package.json"] = @"{ ""name"": ""nested"" }",
        };

        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.SetupWithTypingsInstaller(files, new TypingsInstallerOptions
        {
            TypesRegistry = ["nested", "commander"],
            PackageToFile = new Dictionary<string, string>
            {
                ["jquery"] = "declare const jquery: { x: number }",
            },
        });

        session.DidOpenFile(RequestContext.Background, "file:///user/username/projects/project/app.js", 1, (string)files["/user/username/projects/project/app.js"]!, LanguageKind.JavaScript.Value!);
        session.WaitForBackgroundTasks();

        // Check that npm install was called twice
        var calls = utils.NpmExecutor().NpmInstallCalls();
        Assert.Equal(2, calls.Count); // Go message: "Expected exactly 2 npm install calls"
        Assert.Equal(ProjectTestUtilConstants.TestTypingsLocation, calls[0].Cwd);
        Assert.Equal(new[] { "install", "--ignore-scripts", "types-registry@latest" }, calls[0].Args);
        Assert.Equal(ProjectTestUtilConstants.TestTypingsLocation, calls[1].Cwd);
        Assert.Equal("@types/jquery@latest", calls[1].Args[2]);
    }

    // ata_test.go:192-220 — "discover from node_modules empty types".
    [Fact]
    public void TestATA_DiscoverFromNodeModulesEmptyTypes()
    {
        var files = new Dictionary<string, object?>
        {
            ["/user/username/projects/project/app.js"] = "",
            ["/user/username/projects/project/package.json"] = @"{""dependencies"": {""jquery"": ""1.0.0""}}",
            ["/user/username/projects/project/jsconfig.json"] = @"{""compilerOptions"": {""types"": []}}",
            ["/user/username/projects/project/node_modules/commander/index.js"] = "",
            ["/user/username/projects/project/node_modules/commander/package.json"] = @"{ ""name"": ""commander"" }",
            ["/user/username/projects/project/node_modules/jquery/index.js"] = "",
            ["/user/username/projects/project/node_modules/jquery/package.json"] = @"{ ""name"": ""jquery"" }",
            ["/user/username/projects/project/node_modules/jquery/nested/package.json"] = @"{ ""name"": ""nested"" }",
        };

        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.SetupWithTypingsInstaller(files, new TypingsInstallerOptions
        {
            TypesRegistry = ["nested", "commander"],
            PackageToFile = new Dictionary<string, string>
            {
                ["jquery"] = "declare const jquery: { x: number }",
            },
        });

        session.DidOpenFile(RequestContext.Background, "file:///user/username/projects/project/app.js", 1, (string)files["/user/username/projects/project/app.js"]!, LanguageKind.JavaScript.Value!);
        session.WaitForBackgroundTasks();

        // Only types-registry should be installed
        var calls = utils.NpmExecutor().NpmInstallCalls();
        Assert.Single(calls);
        Assert.Equal(new[] { "install", "--ignore-scripts", "types-registry@latest" }, calls[0].Args);
    }

    // ata_test.go:222-250 — "discover from node_modules explicit types".
    [Fact]
    public void TestATA_DiscoverFromNodeModulesExplicitTypes()
    {
        var files = new Dictionary<string, object?>
        {
            ["/user/username/projects/project/app.js"] = "",
            ["/user/username/projects/project/package.json"] = @"{""dependencies"": {""jquery"": ""1.0.0""}}",
            ["/user/username/projects/project/jsconfig.json"] = @"{""compilerOptions"": {""types"": [""jquery""]}}",
            ["/user/username/projects/project/node_modules/commander/index.js"] = "",
            ["/user/username/projects/project/node_modules/commander/package.json"] = @"{ ""name"": ""commander"" }",
            ["/user/username/projects/project/node_modules/jquery/index.js"] = "",
            ["/user/username/projects/project/node_modules/jquery/package.json"] = @"{ ""name"": ""jquery"" }",
            ["/user/username/projects/project/node_modules/jquery/nested/package.json"] = @"{ ""name"": ""nested"" }",
        };

        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.SetupWithTypingsInstaller(files, new TypingsInstallerOptions
        {
            TypesRegistry = ["nested", "commander"],
            PackageToFile = new Dictionary<string, string>
            {
                ["jquery"] = "declare const jquery: { x: number }",
            },
        });

        session.DidOpenFile(RequestContext.Background, "file:///user/username/projects/project/app.js", 1, (string)files["/user/username/projects/project/app.js"]!, LanguageKind.JavaScript.Value!);
        session.WaitForBackgroundTasks();

        // Only types-registry should be installed
        var calls = utils.NpmExecutor().NpmInstallCalls();
        Assert.Single(calls);
        Assert.Equal(new[] { "install", "--ignore-scripts", "types-registry@latest" }, calls[0].Args);
    }

    // ata_test.go:252-281 — "discover from node_modules empty types has import".
    [Fact]
    public void TestATA_DiscoverFromNodeModulesEmptyTypesHasImport()
    {
        var files = new Dictionary<string, object?>
        {
            ["/user/username/projects/project/app.js"] = @"import ""jquery"";",
            ["/user/username/projects/project/package.json"] = @"{""dependencies"": {""jquery"": ""1.0.0""}}",
            ["/user/username/projects/project/jsconfig.json"] = @"{""compilerOptions"": {""types"": []}}",
            ["/user/username/projects/project/node_modules/commander/index.js"] = "",
            ["/user/username/projects/project/node_modules/commander/package.json"] = @"{ ""name"": ""commander"" }",
            ["/user/username/projects/project/node_modules/jquery/index.js"] = "",
            ["/user/username/projects/project/node_modules/jquery/package.json"] = @"{ ""name"": ""jquery"" }",
            ["/user/username/projects/project/node_modules/jquery/nested/package.json"] = @"{ ""name"": ""nested"" }",
        };

        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.SetupWithTypingsInstaller(files, new TypingsInstallerOptions
        {
            TypesRegistry = ["nested", "commander"],
            PackageToFile = new Dictionary<string, string>
            {
                ["jquery"] = "declare const jquery: { x: number }",
            },
        });

        session.DidOpenFile(RequestContext.Background, "file:///user/username/projects/project/app.js", 1, (string)files["/user/username/projects/project/app.js"]!, LanguageKind.JavaScript.Value!);
        session.WaitForBackgroundTasks();

        // types-registry + jquery types
        var calls = utils.NpmExecutor().NpmInstallCalls();
        Assert.Equal(2, calls.Count);
        Assert.Equal(new[] { "install", "--ignore-scripts", "types-registry@latest" }, calls[0].Args);
        Assert.Contains("@types/jquery@latest", (IEnumerable<string>)calls[1].Args);
    }

    // ata_test.go:283-315 — "discover from bower_components".
    [Fact]
    public void TestATA_DiscoverFromBowerComponents()
    {
        var files = new Dictionary<string, object?>
        {
            ["/user/username/projects/project/app.js"] = @"",
            ["/user/username/projects/project/jsconfig.json"] = @"{}",
            ["/user/username/projects/project/bower_components/jquery/index.js"] = "",
            ["/user/username/projects/project/bower_components/jquery/bower.json"] = @"{ ""name"": ""jquery"" }",
        };

        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.SetupWithTypingsInstaller(files, new TypingsInstallerOptions
        {
            PackageToFile = new Dictionary<string, string>
            {
                ["jquery"] = "declare const jquery: { x: number }",
            },
        });

        session.DidOpenFile(RequestContext.Background, "file:///user/username/projects/project/app.js", 1, (string)files["/user/username/projects/project/app.js"]!, LanguageKind.JavaScript.Value!);
        session.WaitForBackgroundTasks();

        // Check that npm install was called twice
        var calls = utils.NpmExecutor().NpmInstallCalls();
        Assert.Equal(2, calls.Count); // Go message: "Expected exactly 2 npm install calls"
        Assert.Equal(ProjectTestUtilConstants.TestTypingsLocation, calls[0].Cwd);
        Assert.Equal(new[] { "install", "--ignore-scripts", "types-registry@latest" }, calls[0].Args);
        Assert.Equal(ProjectTestUtilConstants.TestTypingsLocation, calls[1].Cwd);
        Assert.Equal("@types/jquery@latest", calls[1].Args[2]);

        // Verify the types file was installed
        (global::Ts.Ls.LanguageService? ls, System.Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///user/username/projects/project/app.js");
        Assert.Null(err);
        var jqueryTypesFile = ls!.GetProgram()!.GetSourceFile(ProjectTestUtilConstants.TestTypingsLocation + "/node_modules/@types/jquery/index.d.ts");
        Assert.True(jqueryTypesFile != null, "jquery types should be installed");
    }

    // ata_test.go:317-352 — "discover from bower.json".
    [Fact]
    public void TestATA_DiscoverFromBowerJson()
    {
        var files = new Dictionary<string, object?>
        {
            ["/user/username/projects/project/app.js"] = @"",
            ["/user/username/projects/project/jsconfig.json"] = @"{}",
            ["/user/username/projects/project/bower.json"] = @"{
				""dependencies"": {
                    ""jquery"": ""^3.1.0""
                }
			}",
        };

        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.SetupWithTypingsInstaller(files, new TypingsInstallerOptions
        {
            PackageToFile = new Dictionary<string, string>
            {
                ["jquery"] = "declare const jquery: { x: number }",
            },
        });

        session.DidOpenFile(RequestContext.Background, "file:///user/username/projects/project/app.js", 1, (string)files["/user/username/projects/project/app.js"]!, LanguageKind.JavaScript.Value!);
        session.WaitForBackgroundTasks();

        // Check that npm install was called twice
        var calls = utils.NpmExecutor().NpmInstallCalls();
        Assert.Equal(2, calls.Count); // Go message: "Expected exactly 2 npm install calls"
        Assert.Equal(ProjectTestUtilConstants.TestTypingsLocation, calls[0].Cwd);
        Assert.Equal(new[] { "install", "--ignore-scripts", "types-registry@latest" }, calls[0].Args);
        Assert.Equal(ProjectTestUtilConstants.TestTypingsLocation, calls[1].Cwd);
        Assert.Equal("@types/jquery@latest", calls[1].Args[2]);

        // Verify the types file was installed
        (global::Ts.Ls.LanguageService? ls, System.Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///user/username/projects/project/app.js");
        Assert.Null(err);
        var jqueryTypesFile = ls!.GetProgram()!.GetSourceFile(ProjectTestUtilConstants.TestTypingsLocation + "/node_modules/@types/jquery/index.d.ts");
        Assert.True(jqueryTypesFile != null, "jquery types should be installed");
    }

    // ata_test.go:354-399 — "Malformed package.json should be watched".
    [Fact]
    public void TestATA_MalformedPackageJsonShouldBeWatched()
    {
        var files = new Dictionary<string, object?>
        {
            ["/user/username/projects/project/app.js"] = "",
            ["/user/username/projects/project/package.json"] = @"{""dependencies"": { ""co } }",
        };

        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.SetupWithTypingsInstaller(files, new TypingsInstallerOptions
        {
            PackageToFile = new Dictionary<string, string>
            {
                ["commander"] = "export let x: number",
            },
        });

        DocumentUri uri = "file:///user/username/projects/project/app.js";
        session.DidOpenFile(RequestContext.Background, uri, 1, (string)files["/user/username/projects/project/app.js"]!, LanguageKind.JavaScript.Value!);
        session.WaitForBackgroundTasks();

        // Initially only types-registry update attempted
        var calls = utils.NpmExecutor().NpmInstallCalls();
        Assert.Single(calls);
        Assert.Equal(new[] { "install", "--ignore-scripts", "types-registry@latest" }, calls[0].Args);

        // Fix package.json and notify watcher
        utils.FS().WriteFile(
            "/user/username/projects/project/package.json",
            @"{ ""dependencies"": { ""commander"": ""0.0.2"" } }");
        session.DidChangeWatchedFiles(RequestContext.Background,
        [
            new FileEvent
            {
                Type = FileChangeType.Changed,
                Uri = "file:///user/username/projects/project/package.json",
            },
        ]);
        // diagnostics refresh triggered - simulate by getting the language service
        _ = session.GetLanguageService(RequestContext.Background, uri);
        session.WaitForBackgroundTasks();

        calls = utils.NpmExecutor().NpmInstallCalls();
        Assert.Equal(2, calls.Count);
        Assert.Contains("@types/commander@latest", (IEnumerable<string>)calls[1].Args);

        // Verify types file present
        (global::Ts.Ls.LanguageService? ls, System.Exception? err) = session.GetLanguageService(RequestContext.Background, uri);
        Assert.Null(err);
        var program = ls!.GetProgram();
        Assert.True(program!.GetSourceFile(ProjectTestUtilConstants.TestTypingsLocation + "/node_modules/@types/commander/index.d.ts") != null);
    }

    // ata_test.go:401-430 — "should redo resolution that resolved to '.js' file after
    // typings are installed".
    [Fact]
    public void TestATA_ShouldRedoResolutionThatResolvedToJsFileAfterTypingsAreInstalled()
    {
        var files = new Dictionary<string, object?>
        {
            ["/user/username/projects/project/app.js"] = @"\n                import * as commander from ""commander"";\n            ",
            ["/user/username/projects/node_modules/commander/index.js"] = "module.exports = 0",
        };

        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.SetupWithTypingsInstaller(files, new TypingsInstallerOptions
        {
            PackageToFile = new Dictionary<string, string>
            {
                ["commander"] = "export let commander: number",
            },
        });

        DocumentUri uri = "file:///user/username/projects/project/app.js";
        session.DidOpenFile(RequestContext.Background, uri, 1, (string)files["/user/username/projects/project/app.js"]!, LanguageKind.JavaScript.Value!);
        session.WaitForBackgroundTasks();

        var calls = utils.NpmExecutor().NpmInstallCalls();
        Assert.Equal(2, calls.Count);
        Assert.Contains("@types/commander@latest", (IEnumerable<string>)calls[1].Args);

        (global::Ts.Ls.LanguageService? ls, System.Exception? err) = session.GetLanguageService(RequestContext.Background, uri);
        Assert.Null(err);
        var program = ls!.GetProgram();
        // Types file present
        Assert.True(program!.GetSourceFile(ProjectTestUtilConstants.TestTypingsLocation + "/node_modules/@types/commander/index.d.ts") != null);
        // JS resolution should be dropped
        Assert.True(program.GetSourceFile("/user/username/projects/node_modules/commander/index.js") == null);
    }

    // ata_test.go:432-458 — "expired cache entry (inferred project, should install typings)".
    [Fact]
    public void TestATA_ExpiredCacheEntryInferredProjectShouldInstallTypings()
    {
        var files = new Dictionary<string, object?>
        {
            ["/user/username/projects/project/app.js"] = "",
            ["/user/username/projects/project/package.json"] = @"{""name"":""test"",""dependencies"":{""jquery"":""^3.1.0""}}",
            [ProjectTestUtilConstants.TestTypingsLocation + "/node_modules/@types/jquery/index.d.ts"] = "export const x = 10;",
            [ProjectTestUtilConstants.TestTypingsLocation + "/package.json"] = @"{""dependencies"":{""types-registry"":""^0.1.317""},""devDependencies"":{""@types/jquery"":""^1.0.0""}}",
            [ProjectTestUtilConstants.TestTypingsLocation + "/package-lock.json"] = @"{""dependencies"":{""@types/jquery"":{""version"":""1.0.0""}}}",
        };

        (global::Ts.Project.Session session, SessionUtils _) = SessionUtils.SetupWithTypingsInstaller(files, new TypingsInstallerOptions
        {
            PackageToFile = new Dictionary<string, string>
            {
                ["jquery"] = "export const y = 10",
            },
        });

        DocumentUri uri = "file:///user/username/projects/project/app.js";
        session.DidOpenFile(RequestContext.Background, uri, 1, (string)files["/user/username/projects/project/app.js"]!, LanguageKind.JavaScript.Value!);
        session.WaitForBackgroundTasks();

        (global::Ts.Ls.LanguageService? ls, System.Exception? err) = session.GetLanguageService(RequestContext.Background, uri);
        Assert.Null(err);
        var program = ls!.GetProgram();
        // Expect updated content from installed typings
        Assert.Equal("export const y = 10", program!.GetSourceFile(ProjectTestUtilConstants.TestTypingsLocation + "/node_modules/@types/jquery/index.d.ts")!.Text());
    }

    // ata_test.go:460-484 — "non-expired cache entry (inferred project, should not install
    // typings)".
    [Fact]
    public void TestATA_NonExpiredCacheEntryInferredProjectShouldNotInstallTypings()
    {
        var files = new Dictionary<string, object?>
        {
            ["/user/username/projects/project/app.js"] = "",
            ["/user/username/projects/project/package.json"] = @"{""name"":""test"",""dependencies"":{""jquery"":""^3.1.0""}}",
            [ProjectTestUtilConstants.TestTypingsLocation + "/node_modules/@types/jquery/index.d.ts"] = "export const x = 10;",
            [ProjectTestUtilConstants.TestTypingsLocation + "/package.json"] = @"{""dependencies"":{""types-registry"":""^0.1.317""},""devDependencies"":{""@types/jquery"":""^1.3.0""}}",
            [ProjectTestUtilConstants.TestTypingsLocation + "/package-lock.json"] = @"{""dependencies"":{""@types/jquery"":{""version"":""1.3.0""}}}",
        };

        (global::Ts.Project.Session session, SessionUtils _) = SessionUtils.SetupWithTypingsInstaller(files, new TypingsInstallerOptions
        {
            TypesRegistry = ["jquery"],
        });

        DocumentUri uri = "file:///user/username/projects/project/app.js";
        session.DidOpenFile(RequestContext.Background, uri, 1, (string)files["/user/username/projects/project/app.js"]!, LanguageKind.JavaScript.Value!);
        session.WaitForBackgroundTasks();

        (global::Ts.Ls.LanguageService? ls, System.Exception? err) = session.GetLanguageService(RequestContext.Background, uri);
        Assert.Null(err);
        var program = ls!.GetProgram();
        // Expect existing content unchanged
        Assert.Equal("export const x = 10;", program!.GetSourceFile(ProjectTestUtilConstants.TestTypingsLocation + "/node_modules/@types/jquery/index.d.ts")!.Text());
    }

    // ata_test.go:486-517 — "deduplicate from local @types packages". Kept skipped exactly
    // as in Go ("Todo - implement removing local @types from include list").
    [Fact(Skip = "Todo - implement removing local @types from include list")]
    public void TestATA_DeduplicateFromLocalTypesPackages()
    {
        var files = new Dictionary<string, object?>
        {
            ["/user/username/projects/project/app.js"] = "",
            ["/user/username/projects/project/node_modules/@types/node/index.d.ts"] = "declare var node;",
            ["/user/username/projects/project/jsconfig.json"] = @"{
				""typeAcquisition"": { ""include"": [""node""] }
			}",
        };

        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.SetupWithTypingsInstaller(files, new TypingsInstallerOptions
        {
            TypesRegistry = ["node"],
        });

        DocumentUri uri = "file:///user/username/projects/project/app.js";
        session.DidOpenFile(RequestContext.Background, uri, 1, (string)files["/user/username/projects/project/app.js"]!, LanguageKind.JavaScript.Value!);
        session.WaitForBackgroundTasks();

        // Only the types-registry should be installed; @types/node should NOT be installed since it exists locally
        var npmCalls = utils.NpmExecutor().NpmInstallCalls();
        Assert.Single(npmCalls);
        Assert.Equal(ProjectTestUtilConstants.TestTypingsLocation, npmCalls[0].Cwd);
        Assert.Equal(new[] { "install", "--ignore-scripts", "types-registry@latest" }, npmCalls[0].Args);

        // And the program should include the local @types/node declaration file
        (global::Ts.Ls.LanguageService? ls, System.Exception? err) = session.GetLanguageService(RequestContext.Background, uri);
        Assert.Null(err);
        var program = ls!.GetProgram();
        Assert.True(program!.GetSourceFile("/user/username/projects/project/node_modules/@types/node/index.d.ts") != null);
    }

    // ata_test.go:519-545 — "expired cache entry (inferred project, should install typings)
    // lockfile3".
    [Fact]
    public void TestATA_ExpiredCacheEntryInferredProjectShouldInstallTypingsLockfile3()
    {
        var files = new Dictionary<string, object?>
        {
            ["/user/username/projects/project/app.js"] = "",
            ["/user/username/projects/project/package.json"] = @"{""name"":""test"",""dependencies"":{""jquery"":""^3.1.0""}}",
            [ProjectTestUtilConstants.TestTypingsLocation + "/node_modules/@types/jquery/index.d.ts"] = "export const x = 10;",
            [ProjectTestUtilConstants.TestTypingsLocation + "/package.json"] = @"{""dependencies"":{""types-registry"":""^0.1.317""},""devDependencies"":{""@types/jquery"":""^1.0.0""}}",
            [ProjectTestUtilConstants.TestTypingsLocation + "/package-lock.json"] = @"{""packages"":{""node_modules/@types/jquery"":{""version"":""1.0.0""}}}",
        };

        (global::Ts.Project.Session session, SessionUtils _) = SessionUtils.SetupWithTypingsInstaller(files, new TypingsInstallerOptions
        {
            PackageToFile = new Dictionary<string, string>
            {
                ["jquery"] = "export const y = 10",
            },
        });

        DocumentUri uri = "file:///user/username/projects/project/app.js";
        session.DidOpenFile(RequestContext.Background, uri, 1, (string)files["/user/username/projects/project/app.js"]!, LanguageKind.JavaScript.Value!);
        session.WaitForBackgroundTasks();

        (global::Ts.Ls.LanguageService? ls, System.Exception? err) = session.GetLanguageService(RequestContext.Background, uri);
        Assert.Null(err);
        var program = ls!.GetProgram();
        // Expect updated content from installed typings
        Assert.Equal("export const y = 10", program!.GetSourceFile(ProjectTestUtilConstants.TestTypingsLocation + "/node_modules/@types/jquery/index.d.ts")!.Text());
    }

    // ata_test.go:547-571 — "non-expired cache entry (inferred project, should not install
    // typings) lockfile3".
    [Fact]
    public void TestATA_NonExpiredCacheEntryInferredProjectShouldNotInstallTypingsLockfile3()
    {
        var files = new Dictionary<string, object?>
        {
            ["/user/username/projects/project/app.js"] = "",
            ["/user/username/projects/project/package.json"] = @"{""name"":""test"",""dependencies"":{""jquery"":""^3.1.0""}}",
            [ProjectTestUtilConstants.TestTypingsLocation + "/node_modules/@types/jquery/index.d.ts"] = "export const x = 10;",
            [ProjectTestUtilConstants.TestTypingsLocation + "/package.json"] = @"{""dependencies"":{""types-registry"":""^0.1.317""},""devDependencies"":{""@types/jquery"":""^1.3.0""}}",
            [ProjectTestUtilConstants.TestTypingsLocation + "/package-lock.json"] = @"{""packages"":{""node_modules/@types/jquery"":{""version"":""1.3.0""}}}",
        };

        (global::Ts.Project.Session session, SessionUtils _) = SessionUtils.SetupWithTypingsInstaller(files, new TypingsInstallerOptions
        {
            TypesRegistry = ["jquery"],
        });

        DocumentUri uri = "file:///user/username/projects/project/app.js";
        session.DidOpenFile(RequestContext.Background, uri, 1, (string)files["/user/username/projects/project/app.js"]!, LanguageKind.JavaScript.Value!);
        session.WaitForBackgroundTasks();

        (global::Ts.Ls.LanguageService? ls, System.Exception? err) = session.GetLanguageService(RequestContext.Background, uri);
        Assert.Null(err);
        var program = ls!.GetProgram();
        // Expect existing content unchanged
        Assert.Equal("export const x = 10;", program!.GetSourceFile(ProjectTestUtilConstants.TestTypingsLocation + "/node_modules/@types/jquery/index.d.ts")!.Text());
    }

    // ata_test.go:573-621 — "should install typings for unresolved imports".
    [Fact]
    public void TestATA_ShouldInstallTypingsForUnresolvedImports()
    {
        var files = new Dictionary<string, object?>
        {
            ["/user/username/projects/project/app.js"] = @"
				import * as fs from ""fs"";
                import * as commander from ""commander"";
                import * as component from ""@ember/component"";
			",
        };

        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.SetupWithTypingsInstaller(files, new TypingsInstallerOptions
        {
            PackageToFile = new Dictionary<string, string>
            {
                ["node"] = "export let node: number",
                ["commander"] = "export let commander: number",
                ["ember__component"] = "export let ember__component: number",
            },
        });

        session.DidOpenFile(RequestContext.Background, "file:///user/username/projects/project/app.js", 1, (string)files["/user/username/projects/project/app.js"]!, LanguageKind.JavaScript.Value!);
        session.WaitForBackgroundTasks();

        // Check that npm install was called twice
        var calls = utils.NpmExecutor().NpmInstallCalls();
        Assert.Equal(2, calls.Count); // Go message: "Expected exactly 2 npm install calls"
        Assert.Equal(ProjectTestUtilConstants.TestTypingsLocation, calls[0].Cwd);
        Assert.Equal(new[] { "install", "--ignore-scripts", "types-registry@latest" }, calls[0].Args);

        // The second call should install all three packages at once
        Assert.Equal(ProjectTestUtilConstants.TestTypingsLocation, calls[1].Cwd);
        Assert.Equal("install", calls[1].Args[0]);
        Assert.Equal("--ignore-scripts", calls[1].Args[1]);
        // Check that all three packages are in the install command
        string[] installArgs = calls[1].Args;
        Assert.Contains("@types/ember__component@latest", (IEnumerable<string>)installArgs);
        Assert.Contains("@types/commander@latest", (IEnumerable<string>)installArgs);
        Assert.Contains("@types/node@latest", (IEnumerable<string>)installArgs);

        // Verify the types files were installed
        (global::Ts.Ls.LanguageService? ls, System.Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///user/username/projects/project/app.js");
        Assert.Null(err);
        var program = ls!.GetProgram();
        var nodeTypesFile = program!.GetSourceFile(ProjectTestUtilConstants.TestTypingsLocation + "/node_modules/@types/node/index.d.ts");
        Assert.True(nodeTypesFile != null, "node types should be installed");
        var commanderTypesFile = program.GetSourceFile(ProjectTestUtilConstants.TestTypingsLocation + "/node_modules/@types/commander/index.d.ts");
        Assert.True(commanderTypesFile != null, "commander types should be installed");
        var emberComponentTypesFile = program.GetSourceFile(ProjectTestUtilConstants.TestTypingsLocation + "/node_modules/@types/ember__component/index.d.ts");
        Assert.True(emberComponentTypesFile != null, "ember__component types should be installed");
    }

    // ata_test.go:623-665 — "ATA with WatchEnabled false should not panic". Test that ATA
    // works correctly when `WatchEnabled` is false but `TypingsLocation` is set. Previously
    // if `WatchEnabled` was false but `TypingsLocation` was set, ATA would run but crash
    // when cloning file-watcher data for a new snapshot.
    [Fact]
    public void TestATA_AtaWithWatchEnabledFalseShouldNotPanic()
    {
        var files = new Dictionary<string, object?>
        {
            ["/user/username/projects/project/app.js"] = @"",
            ["/user/username/projects/project/package.json"] = @"{
				""name"": ""test"",
				""dependencies"": {
					""jquery"": ""^3.1.0""
				}
			}",
        };

        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.SetupWithOptionsAndTypingsInstaller(files, new global::Ts.Project.SessionOptions
        {
            CurrentDirectory = "/",
            DefaultLibraryPath = global::Ts.Bundled.Bundled.LibPath(),
            TypingsLocation = ProjectTestUtilConstants.TestTypingsLocation,
            PositionEncoding = global::Ts.LsProto.PositionEncodingKind.UTF8,
            WatchEnabled = false,
            LoggingEnabled = true,
        }, new TypingsInstallerOptions
        {
            PackageToFile = new Dictionary<string, string>
            {
                ["jquery"] = @"declare const $: { x: number }",
            },
        });

        // Open a file to trigger project creation and ATA.
        session.DidOpenFile(RequestContext.Background, "file:///user/username/projects/project/app.js", 1, (string)files["/user/username/projects/project/app.js"]!, LanguageKind.JavaScript.Value!);
        session.WaitForBackgroundTasks();

        // ATA should have run
        var calls = utils.NpmExecutor().NpmInstallCalls();
        Assert.Equal(2, calls.Count); // Go message: "Expected exactly 2 npm install calls"

        // Getting the language service should not panic after
        // applying ATA changes and grabbing the latest snapshot.
        (global::Ts.Ls.LanguageService? ls, System.Exception? err) = session.GetLanguageService(RequestContext.Background, "file:///user/username/projects/project/app.js");
        Assert.Null(err);
        Assert.NotNull(ls);
    }

    // ata_test.go:693-719 — ataDisabledCases[0], "ATA disabled via unified setting"
    // (js/ts.tsserver.automaticTypeAcquisition.enabled = false). The C# port sets the parsed
    // UserPreferences field directly (see file-header PORT-NOTE).
    [Fact]
    public void TestATA_AtaDisabledViaUnifiedSetting()
    {
        var files = new Dictionary<string, object?>
        {
            ["/user/username/projects/project/app.js"] = @"",
            ["/user/username/projects/project/package.json"] = @"{
				""name"": ""test"",
				""dependencies"": {
					""jquery"": ""^3.1.0""
				}
			}",
        };

        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.SetupWithTypingsInstaller(files, new TypingsInstallerOptions
        {
            PackageToFile = new Dictionary<string, string>
            {
                ["jquery"] = @"declare const $: { x: number }",
            },
        });

        session.Configure(new UserPreferences
        {
            AutomaticTypeAcquisitionEnabled = Tristate.False,
        });
        session.DidOpenFile(RequestContext.Background, "file:///user/username/projects/project/app.js", 1, (string)files["/user/username/projects/project/app.js"]!, LanguageKind.JavaScript.Value!);
        session.WaitForBackgroundTasks();

        var calls = utils.NpmExecutor().NpmInstallCalls();
        Assert.Empty(calls); // Go message: "Expected no npm install calls when ATA is disabled via unified setting"
    }

    // ata_test.go:693-719 — ataDisabledCases[1], "ATA disabled via deprecated setting"
    // (typescript.disableAutomaticTypeAcquisition = true). The C# port sets the parsed
    // UserPreferences field directly (see file-header PORT-NOTE).
    [Fact]
    public void TestATA_AtaDisabledViaDeprecatedSetting()
    {
        var files = new Dictionary<string, object?>
        {
            ["/user/username/projects/project/app.js"] = @"",
            ["/user/username/projects/project/package.json"] = @"{
				""name"": ""test"",
				""dependencies"": {
					""jquery"": ""^3.1.0""
				}
			}",
        };

        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.SetupWithTypingsInstaller(files, new TypingsInstallerOptions
        {
            PackageToFile = new Dictionary<string, string>
            {
                ["jquery"] = @"declare const $: { x: number }",
            },
        });

        session.Configure(new UserPreferences
        {
            DisableAutomaticTypeAcquisition = Tristate.True,
        });
        session.DidOpenFile(RequestContext.Background, "file:///user/username/projects/project/app.js", 1, (string)files["/user/username/projects/project/app.js"]!, LanguageKind.JavaScript.Value!);
        session.WaitForBackgroundTasks();

        var calls = utils.NpmExecutor().NpmInstallCalls();
        Assert.Empty(calls); // Go message: "Expected no npm install calls when ATA is disabled via deprecated setting"
    }

    // ata_test.go:722-766 — "ATA re-enabled after being disabled triggers diagnostics
    // refresh". The disable step sets the parsed UserPreferences field directly (see
    // file-header PORT-NOTE); Go re-enables via ParseUserPreferences({}) — all defaults.
    [Fact]
    public void TestATA_AtaReEnabledAfterBeingDisabledTriggersDiagnosticsRefresh()
    {
        var files = new Dictionary<string, object?>
        {
            ["/user/username/projects/project/app.js"] = @"",
            ["/user/username/projects/project/package.json"] = @"{
				""name"": ""test"",
				""dependencies"": {
					""jquery"": ""^3.1.0""
				}
			}",
        };

        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.SetupWithTypingsInstaller(files, new TypingsInstallerOptions
        {
            PackageToFile = new Dictionary<string, string>
            {
                ["jquery"] = @"declare const $: { x: number }",
            },
        });

        // Disable ATA
        session.Configure(new UserPreferences
        {
            AutomaticTypeAcquisitionEnabled = Tristate.False,
        });

        session.DidOpenFile(RequestContext.Background, "file:///user/username/projects/project/app.js", 1, (string)files["/user/username/projects/project/app.js"]!, LanguageKind.JavaScript.Value!);
        session.WaitForBackgroundTasks();

        var calls = utils.NpmExecutor().NpmInstallCalls();
        Assert.Empty(calls); // Go message: "Expected no npm install calls when ATA is disabled"

        int baselineRefreshCount = utils.Client().RefreshDiagnosticsCalls().Count;

        // Re-enable ATA
        session.Configure(UserPreferences.NewDefaultUserPreferences());
        session.WaitForBackgroundTasks();

        int refreshCount = utils.Client().RefreshDiagnosticsCalls().Count;
        Assert.True(refreshCount > baselineRefreshCount, "Expected RefreshDiagnostics call after ATA re-enabled");
    }
}
