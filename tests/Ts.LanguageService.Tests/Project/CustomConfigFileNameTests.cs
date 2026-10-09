// Ported from tsgo internal/project/customconfigfilename_test.go (whole file).
//
// PORT-NOTEs:
//  * One Go test function → one xUnit class; t.Run subtests → [Fact]s named Parent_Subtest.
//  * gotest.tools assert.Equal(t, actual, expected, msg) → Assert.Equal(expected, actual)
//    (argument order swapped); Go message strings are kept as comments where xUnit has no
//    message overload.
//  * The Go `if !bundled.Embedded { t.Skip(...) }` guard is omitted: Bundled.Embedded is
//    `const true` in the C# build, so the branch is unreachable (and CS0162 fatal).
//  * context.Background() → RequestContext.Background.
//  * The "rejects path traversal" / "accepts plain base file names" subtests are skipped:
//    they drive lsutil.ParseUserPreferences (userpreferences.go), which is not ported to
//    C# yet — Ts.Ls.UserPreferences has no raw-dictionary parser.

using System;
using System.Collections.Generic;
using Ts.Core;
using Ts.TestUtil.ProjectTestUtil;
using Xunit;

using DocumentUri = global::Ts.LsProto.DocumentUri;
using LanguageKind = global::Ts.LsProto.LanguageKind;
using UserPreferences = global::Ts.Ls.UserPreferences;

namespace Ts.Project.Tests;

public sealed class CustomConfigFileNameTests
{
    // customconfigfilename_test.go:21-26.
    private static Dictionary<string, object?> Files()
    {
        return new Dictionary<string, object?>
        {
            ["/src/tsconfig.json"] = @"{""compilerOptions"": {""strict"": false}}",
            ["/src/tsconfig.all.json"] = @"{""compilerOptions"": {""strict"": true}}",
            ["/src/index.ts"] = @"export const x = 1;",
        };
    }

    private const string UriString = "file:///src/index.ts";

    // customconfigfilename_test.go:28-50 — "picks up custom config and switches on
    // preference change".
    [Fact]
    public void TestCustomConfigFileName_PicksUpCustomConfigAndSwitchesOnPreferenceChange()
    {
        var files = Files();
        (global::Ts.Project.Session session, SessionUtils _) = SessionUtils.Setup(files);
        DocumentUri uri = UriString;

        session.DidOpenFile(RequestContext.Background, uri, 1, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, uri);
        Assert.Null(err);

        var snapshot = session.Snapshot()!;
        Assert.Equal("/src/tsconfig.json", snapshot.GetDefaultProject(uri)!.Name());
        Assert.Equal(Tristate.False, ls!.GetProgram().Options().Strict);

        UserPreferences prefs = UserPreferences.NewDefaultUserPreferences();
        prefs.CustomConfigFileName = "tsconfig.all.json";
        session.Configure(prefs);

        (ls, err) = session.GetLanguageService(RequestContext.Background, uri);
        Assert.Null(err);

        snapshot = session.Snapshot()!;
        Assert.Equal("/src/tsconfig.all.json", snapshot.GetDefaultProject(uri)!.Name());
        Assert.Equal(Tristate.True, ls!.GetProgram().Options().Strict);
    }

    // customconfigfilename_test.go:52-67 — "uses tsconfig.json when customConfigFileName
    // is empty".
    [Fact]
    public void TestCustomConfigFileName_UsesTsconfigJsonWhenCustomConfigFileNameIsEmpty()
    {
        var files = Files();
        (global::Ts.Project.Session session, SessionUtils _) = SessionUtils.Setup(files);

        UserPreferences prefs = UserPreferences.NewDefaultUserPreferences();
        // default for CustomConfigFileName is "".
        Assert.Equal("", prefs.CustomConfigFileName);
        session.Configure(prefs);

        session.DidOpenFile(RequestContext.Background, UriString, 1, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, UriString);
        Assert.Null(err);

        var snapshot = session.Snapshot()!;
        Assert.Equal("/src/tsconfig.json", snapshot.GetDefaultProject(uri: UriString)!.Name());
    }

    // customconfigfilename_test.go:69-83 — "falls back to tsconfig.json when custom
    // config missing".
    [Fact]
    public void TestCustomConfigFileName_FallsBackToTsconfigJsonWhenCustomConfigMissing()
    {
        var files = Files();
        (global::Ts.Project.Session session, SessionUtils _) = SessionUtils.Setup(files);

        UserPreferences prefs = UserPreferences.NewDefaultUserPreferences();
        prefs.CustomConfigFileName = "tsconfig.nonexistent.json";
        session.Configure(prefs);

        session.DidOpenFile(RequestContext.Background, UriString, 1, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, UriString);
        Assert.Null(err);

        var snapshot = session.Snapshot()!;
        Assert.Equal("/src/tsconfig.json", snapshot.GetDefaultProject(uri: UriString)!.Name());
    }

    // customconfigfilename_test.go:85-121 — "reverts to tsconfig.json when custom config
    // preference is cleared".
    [Fact]
    public void TestCustomConfigFileName_RevertsToTsconfigJsonWhenCustomConfigPreferenceIsCleared()
    {
        var files = Files();
        (global::Ts.Project.Session session, SessionUtils _) = SessionUtils.Setup(files);

        // Step 1: Open file, verify it uses tsconfig.json (strict: false)
        session.DidOpenFile(RequestContext.Background, UriString, 1, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, UriString);
        Assert.Null(err);

        var snapshot = session.Snapshot()!;
        Assert.Equal("/src/tsconfig.json", snapshot.GetDefaultProject(uri: UriString)!.Name());
        Assert.Equal(Tristate.False, ls!.GetProgram().Options().Strict);

        // Step 2: Switch to custom config (strict: true)
        UserPreferences prefs = UserPreferences.NewDefaultUserPreferences();
        prefs.CustomConfigFileName = "tsconfig.all.json";
        session.Configure(prefs);

        (ls, err) = session.GetLanguageService(RequestContext.Background, UriString);
        Assert.Null(err);

        snapshot = session.Snapshot()!;
        Assert.Equal("/src/tsconfig.all.json", snapshot.GetDefaultProject(uri: UriString)!.Name());
        Assert.Equal(Tristate.True, ls!.GetProgram().Options().Strict);

        // Step 3: Clear custom config preference, should revert to tsconfig.json (strict: false)
        prefs = UserPreferences.NewDefaultUserPreferences();
        prefs.CustomConfigFileName = "";
        session.Configure(prefs);

        (ls, err) = session.GetLanguageService(RequestContext.Background, UriString);
        Assert.Null(err);

        snapshot = session.Snapshot()!;
        Assert.Equal("/src/tsconfig.json", snapshot.GetDefaultProject(uri: UriString)!.Name());
        Assert.Equal(Tristate.False, ls!.GetProgram().Options().Strict);
    }

    // customconfigfilename_test.go:123-154 — "schedules diagnostics refresh when custom
    // config preference changes". This test demonstrates the bug reported in #2020: after
    // changing customConfigFileName, the server does not schedule a diagnostics refresh,
    // so the VS Code client never knows to re-pull diagnostics and shows stale results.
    [Fact]
    public void TestCustomConfigFileName_SchedulesDiagnosticsRefreshWhenCustomConfigPreferenceChanges()
    {
        var files = Files();
        (global::Ts.Project.Session session, SessionUtils utils) = SessionUtils.Setup(files);

        session.DidOpenFile(RequestContext.Background, UriString, 1, (string)files["/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, UriString);
        Assert.Null(err);
        session.WaitForBackgroundTasks();

        // Record baseline refresh call count
        int baselineRefreshCount = utils.Client().RefreshDiagnosticsCalls().Count;

        // Change the custom config preference
        UserPreferences prefs = UserPreferences.NewDefaultUserPreferences();
        prefs.CustomConfigFileName = "tsconfig.all.json";
        session.Configure(prefs);

        // GetLanguageService triggers the snapshot update with the new config
        (ls, err) = session.GetLanguageService(RequestContext.Background, UriString);
        Assert.Null(err);
        session.WaitForBackgroundTasks();

        // The server should have scheduled a diagnostics refresh to tell the client
        // to re-pull diagnostics with the new project configuration.
        int refreshCount = utils.Client().RefreshDiagnosticsCalls().Count;
        Assert.True(refreshCount > baselineRefreshCount,
            $"expected RefreshDiagnostics to be called after customConfigFileName change, got {refreshCount} calls (baseline {baselineRefreshCount})");
    }

    // customconfigfilename_test.go:156-175 — "rejects path traversal in
    // customConfigFileName". SKIPPED: lsutil.ParseUserPreferences (userpreferences.go) is
    // not ported to C# yet — there is no raw-preference-dictionary parser in Ts.Ls to
    // drive the validation path under test.
    [Fact(Skip = "lsutil.ParseUserPreferences (internal/ls/lsutil/userpreferences.go) is not ported to C# yet; no raw-dictionary preference parser exists in Ts.Ls")]
    public void TestCustomConfigFileName_RejectsPathTraversalInCustomConfigFileName()
    {
        // Ported body (compiles once ParseUserPreferences lands):
        // foreach (var invalidName in new[]
        // {
        //     "/etc/passwd",
        //     "../tsconfig.json",
        //     "configs/tsconfig.all.json",
        //     "..\\tsconfig.json",
        //     "sub\\dir\\tsconfig.json",
        //     "..",
        //     ".",
        // })
        // {
        //     var prefs = UserPreferences.ParseUserPreferences(new Dictionary<string, object?>
        //     {
        //         ["js/ts"] = new Dictionary<string, object?>
        //         {
        //             ["customConfigFileName"] = invalidName,
        //         },
        //     });
        //     // Go message: "expected customConfigFileName to be cleared for invalid value %q"
        //     Assert.Equal("", prefs.CustomConfigFileName);
        // }
    }

    // customconfigfilename_test.go:177-192 — "accepts plain base file names in
    // customConfigFileName". SKIPPED for the same reason as above.
    [Fact(Skip = "lsutil.ParseUserPreferences (internal/ls/lsutil/userpreferences.go) is not ported to C# yet; no raw-dictionary preference parser exists in Ts.Ls")]
    public void TestCustomConfigFileName_AcceptsPlainBaseFileNamesInCustomConfigFileName()
    {
        // Ported body (compiles once ParseUserPreferences lands):
        // foreach (var validName in new[]
        // {
        //     "tsconfig.all.json",
        //     "tsconfig.editor.json",
        //     "jsconfig.custom.json",
        // })
        // {
        //     var prefs = UserPreferences.ParseUserPreferences(new Dictionary<string, object?>
        //     {
        //         ["js/ts"] = new Dictionary<string, object?>
        //         {
        //             ["customConfigFileName"] = validName,
        //         },
        //     });
        //     // Go message: "expected customConfigFileName to be %q"
        //     Assert.Equal(validName, prefs.CustomConfigFileName);
        // }
    }

    // customconfigfilename_test.go:194-230 — "cleans up inferred project when custom
    // config covers file".
    [Fact]
    public void TestCustomConfigFileName_CleansUpInferredProjectWhenCustomConfigCoversFile()
    {
        // Start without any tsconfig.json so file goes into inferred project, then
        // add a custom config that covers the file and verify it moves out of the
        // inferred project (not just getting a new default, but actually cleaned up).
        var filesNoConfig = new Dictionary<string, object?>
        {
            ["/src/tsconfig.all.json"] = @"{""compilerOptions"": {""strict"": true}, ""include"": [""./**/*""]}",
            ["/src/index.ts"] = @"export const x = 1;",
        };
        DocumentUri uriLocal = "file:///src/index.ts";
        (global::Ts.Project.Session session, SessionUtils _) = SessionUtils.Setup(filesNoConfig);

        session.DidOpenFile(RequestContext.Background, uriLocal, 1, (string)filesNoConfig["/src/index.ts"]!, LanguageKind.TypeScript.Value!);
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, uriLocal);
        Assert.Null(err);

        // Without any config, the file should be in the inferred project only.
        var snapshot = session.Snapshot()!;
        Assert.Equal("/dev/null/inferred", snapshot.GetDefaultProject(uriLocal)!.Name());
        var projects = snapshot.GetProjectsContainingFile(uriLocal);
        // Go message: "expected file to be in exactly 1 project before config change, got %d"
        Assert.Single(projects);

        // Now set custom config to pick up tsconfig.all.json
        UserPreferences prefs = UserPreferences.NewDefaultUserPreferences();
        prefs.CustomConfigFileName = "tsconfig.all.json";
        session.Configure(prefs);

        (ls, err) = session.GetLanguageService(RequestContext.Background, uriLocal);
        Assert.Null(err);

        // File should now be in the configured project only, not duplicated in inferred.
        snapshot = session.Snapshot()!;
        Assert.Equal("/src/tsconfig.all.json", snapshot.GetDefaultProject(uriLocal)!.Name());
        projects = snapshot.GetProjectsContainingFile(uriLocal);
        // Go message: "expected file to be in exactly 1 project after config change, got %d"
        Assert.Single(projects);
    }
}
