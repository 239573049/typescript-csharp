// Ported from tsgo internal/execute/tsctests/tscwatch_test.go — TestWatch (the commandLineWatch
// scenario table, testdata/baselines/reference/tscWatch/commandLineWatch) and TestTscNoEmitWatch
// (the noEmit watch matrix, tscWatch/noEmit). The tscInput runner lives in TscRunner.cs; each Go
// table entry is one [Fact] (Go loop: tscwatch_test.go:584-586, 685-687).
//
// PORT-NOTE: Go raw literals indented with a tab inside the JSON (`{\n\t"compilerOptions"…`) are
// written with explicit \t escapes so the baselines stay byte-identical.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Ts.TestUtil;
using Ts.Vfs.VfsTest;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscWatchTests
{
    private const string P = "/home/src/workspaces/project/";

    private static Dictionary<string, object> Files(params (string Path, object Content)[] entries)
    {
        var files = new Dictionary<string, object>();
        foreach (var (path, content) in entries)
        {
            files[path] = content;
        }
        return files;
    }

    // tscwatch_test.go:13-36 — bunDependencyTest.
    private static TscInput BunDependencyTest()
    {
        var files = new Dictionary<string, object>();
        var index = new StringBuilder();
        var fileNames = new StringBuilder();
        fileNames.Append("\"index.ts\"");
        for (int i = 0; i < 12; i++)
        {
            var name = $"pkg{i}";
            var value = $"value{i}";
            index.Append($"import {{ {value} }} from \"./node_modules/.bun/{name}/index\"; {value};");
            index.Append('\n');
            files[P + "node_modules/.bun/" + name + "/index.ts"] = $"export const {value} = {i};";
            fileNames.Append($", \"node_modules/.bun/{name}/index.ts\"");
        }
        files[P + "index.ts"] = index.ToString();
        files[P + "tsconfig.json"] = "{\n\t\"compilerOptions\": {},\n\t\"files\": [" + fileNames + "]\n}";
        return new TscInput
        {
            subScenario = "watch handles many bun dependency files",
            files = files,
            commandLineArgs = ["--watch"],
        };
    }

    private static TscInput Watch(string subScenario, Dictionary<string, object> files, string[]? commandLineArgs = null, params TscEdit[] edits) => new()
    {
        subScenario = subScenario,
        files = files,
        commandLineArgs = commandLineArgs ?? ["--watch"],
        edits = edits.Length == 0 ? null : edits,
    };

    private static TscEdit EditWithDiff(string caption, string expectedDiff, Action<TestSys> edit) =>
        new() { caption = caption, expectedDiff = expectedDiff, edit = edit };

    private static void RunWatch(TscInput input) => input.RunAndAssert("commandLineWatch");

    // tscwatch_test.go:38-52
    [Fact]
    public void WatchWithNoTsconfig() => RunWatch(Watch("watch with no tsconfig",
        Files((P + "index.ts", "")),
        ["index.ts", "--watch"]));

    [Fact]
    public void WatchWithTsconfigAndIncremental() => RunWatch(Watch("watch with tsconfig and incremental",
        Files((P + "index.ts", ""), (P + "tsconfig.json", "{}")),
        ["--watch", "--incremental"]));

    // tscwatch_test.go:53
    [Fact]
    public void WatchHandlesManyBunDependencyFiles() => RunWatch(BunDependencyTest());

    // tscwatch_test.go:54-64
    [Fact]
    public void WatchSkipsBuildWhenNoFilesChange() => RunWatch(Watch("watch skips build when no files change",
        Files((P + "index.ts", "const x: number = 1;"), (P + "tsconfig.json", "{}")),
        null,
        TscEdit.NoChange));

    // tscwatch_test.go:65-77
    [Fact]
    public void WatchRebuildsWhenFileIsModified() => RunWatch(Watch("watch rebuilds when file is modified",
        Files((P + "index.ts", "const x: number = 1;"), (P + "tsconfig.json", "{}")),
        null,
        NewTscEdit("modify file", sys => sys.WriteFileNoError(P + "index.ts", "const x: number = 2;"))));

    // tscwatch_test.go:78-95
    [Fact]
    public void WatchRebuildsWhenSourceFileIsDeleted() => RunWatch(Watch("watch rebuilds when source file is deleted",
        Files((P + "a.ts", "import { b } from \"./b\";"), (P + "b.ts", "export const b = 1;"), (P + "tsconfig.json", "{}")),
        null,
        EditWithDiff("delete imported file",
            "incremental resolves to .js output from prior build (TS7016) while clean build cannot find module at all (TS2307)",
            sys => sys.RemoveNoError(P + "b.ts"))));

    // tscwatch_test.go:96-108
    [Fact]
    public void WatchDetectsNewFileResolvingFailedImport() => RunWatch(Watch("watch detects new file resolving failed import",
        Files((P + "a.ts", "import { b } from \"./b\";"), (P + "tsconfig.json", "{}")),
        null,
        NewTscEdit("create missing file", sys => sys.WriteFileNoError(P + "b.ts", "export const b = 1;"))));

    // Directory-level change detection via imports
    // tscwatch_test.go:110-122
    [Fact]
    public void WatchDetectsImportedFileAddedInNewDirectory() => RunWatch(Watch("watch detects imported file added in new directory",
        Files((P + "index.ts", "import { util } from \"./lib/util\";"), (P + "tsconfig.json", "{}")),
        null,
        NewTscEdit("create directory and imported file", sys => sys.WriteFileNoError(P + "lib/util.ts", "export const util = \"hello\";"))));

    // tscwatch_test.go:123-140
    [Fact]
    public void WatchDetectsImportedDirectoryRemoved() => RunWatch(Watch("watch detects imported directory removed",
        Files((P + "index.ts", "import { util } from \"./lib/util\";"), (P + "lib/util.ts", "export const util = \"hello\";"), (P + "tsconfig.json", "{}")),
        null,
        EditWithDiff("remove directory with imported file",
            "incremental resolves to .js output from prior build (TS7016) while clean build cannot find module at all (TS2307)",
            sys => sys.RemoveNoError(P + "lib/util.ts"))));

    // tscwatch_test.go:141-156
    [Fact]
    public void WatchDetectsImportPathRestructured() => RunWatch(Watch("watch detects import path restructured",
        Files((P + "index.ts", "import { util } from \"./lib/util\";"), (P + "lib/util.ts", "export const util = \"v1\";"), (P + "tsconfig.json", "{}")),
        null,
        NewTscEdit("move file to new path and update import", sys =>
        {
            sys.RemoveNoError(P + "lib/util.ts");
            sys.WriteFileNoError(P + "src/util.ts", "export const util = \"v2\";");
            sys.WriteFileNoError(P + "index.ts", "import { util } from \"./src/util\";");
        })));

    // tsconfig include/exclude change detection
    // tscwatch_test.go:158-177
    [Fact]
    public void WatchRebuildsWhenTsconfigIncludePatternAddsFile() => RunWatch(Watch("watch rebuilds when tsconfig include pattern adds file",
        Files((P + "index.ts", "const x = 1;"), (P + "tsconfig.json", "{\n\t\"compilerOptions\": {},\n\t\"include\": [\"*.ts\"]\n}")),
        null,
        NewTscEdit("widen include pattern to add src dir", sys =>
        {
            sys.WriteFileNoError(P + "src/extra.ts", "export const extra = 2;");
            sys.WriteFileNoError(P + "tsconfig.json", "{\n\t\"compilerOptions\": {},\n\t\"include\": [\"*.ts\", \"src/**/*.ts\"]\n}");
        })));

    // tscwatch_test.go:178-190
    [Fact]
    public void WatchRebuildsWhenTsconfigIsModifiedToChangeStrict() => RunWatch(Watch("watch rebuilds when tsconfig is modified to change strict",
        Files((P + "index.ts", "const x = null; const y: string = x;"), (P + "tsconfig.json", "{}")),
        null,
        NewTscEdit("enable strict mode", sys => sys.WriteFileNoError(P + "tsconfig.json", "{\"compilerOptions\": {\"strict\": true}}"))));

    // Path resolution: tsconfig include pointing to non-existent directory
    // tscwatch_test.go:192-207
    [Fact]
    public void WatchDetectsFileAddedToPreviouslyNonExistentIncludePath() => RunWatch(Watch("watch detects file added to previously non-existent include path",
        Files((P + "index.ts", "const x = 1;"), (P + "tsconfig.json", "{\n\t\"compilerOptions\": {},\n\t\"include\": [\"index.ts\", \"src/**/*.ts\"]\n}")),
        null,
        NewTscEdit("create src dir with ts file matching include", sys => sys.WriteFileNoError(P + "src/helper.ts", "export const helper = \"added\";"))));

    // tscwatch_test.go:208-223
    [Fact]
    public void WatchDetectsNewFileInExistingIncludeDirectory() => RunWatch(Watch("watch detects new file in existing include directory",
        Files((P + "src/a.ts", "export const a = 1;"), (P + "tsconfig.json", "{\n\t\"compilerOptions\": {},\n\t\"include\": [\"src/**/*.ts\"]\n}")),
        null,
        NewTscEdit("add new file to existing src directory", sys => sys.WriteFileNoError(P + "src/b.ts", "export const b = 2;"))));

    // Wildcard include: nested subdirectory detection
    // tscwatch_test.go:225-240
    [Fact]
    public void WatchDetectsFileAddedInNewNestedSubdirectory() => RunWatch(Watch("watch detects file added in new nested subdirectory",
        Files((P + "src/a.ts", "export const a = 1;"), (P + "tsconfig.json", "{\n\t\"compilerOptions\": {},\n\t\"include\": [\"src/**/*.ts\"]\n}")),
        null,
        NewTscEdit("create nested dir with ts file", sys => sys.WriteFileNoError(P + "src/deep/nested/util.ts", "export const util = \"nested\";"))));

    // tscwatch_test.go:241-257
    [Fact]
    public void WatchDetectsFileAddedInMultipleNewSubdirectoriesSimultaneously() => RunWatch(Watch("watch detects file added in multiple new subdirectories simultaneously",
        Files((P + "src/a.ts", "export const a = 1;"), (P + "tsconfig.json", "{\n\t\"compilerOptions\": {},\n\t\"include\": [\"src/**/*.ts\"]\n}")),
        null,
        NewTscEdit("create multiple new subdirs with files", sys =>
        {
            sys.WriteFileNoError(P + "src/models/user.ts", "export interface User { name: string; }");
            sys.WriteFileNoError(P + "src/utils/format.ts", "export function format(s: string): string { return s.trim(); }");
        })));

    // tscwatch_test.go:258-280
    [Fact]
    public void WatchDetectsNestedSubdirectoryRemovedAndRecreated() => RunWatch(Watch("watch detects nested subdirectory removed and recreated",
        Files((P + "src/lib/helper.ts", "export const helper = \"v1\";"), (P + "tsconfig.json", "{\n\t\"compilerOptions\": {},\n\t\"include\": [\"src/**/*.ts\"]\n}")),
        null,
        EditWithDiff("remove nested dir",
            "incremental has prior state and does not report no-inputs error",
            sys => sys.RemoveNoError(P + "src/lib/helper.ts")),
        NewTscEdit("recreate nested dir with new content", sys => sys.WriteFileNoError(P + "src/lib/helper.ts", "export const helper = \"v2\";"))));

    // Path resolution: import from non-existent node_modules package
    // tscwatch_test.go:282-296
    [Fact]
    public void WatchDetectsNodeModulesPackageAdded() => RunWatch(Watch("watch detects node modules package added",
        Files((P + "index.ts", "import { lib } from \"mylib\";"), (P + "tsconfig.json", "{}")),
        null,
        NewTscEdit("install package in node_modules", sys =>
        {
            sys.WriteFileNoError(P + "node_modules/mylib/package.json", "{\"name\": \"mylib\", \"main\": \"index.js\", \"types\": \"index.d.ts\"}");
            sys.WriteFileNoError(P + "node_modules/mylib/index.js", "exports.lib = \"hello\";");
            sys.WriteFileNoError(P + "node_modules/mylib/index.d.ts", "export declare const lib: string;");
        })));

    private static Dictionary<string, object> MylibInstalledFiles() => Files(
        (P + "index.ts", "import { lib } from \"mylib\";"),
        (P + "tsconfig.json", "{}"),
        (P + "node_modules/mylib/package.json", "{\"name\": \"mylib\", \"main\": \"index.js\", \"types\": \"index.d.ts\"}"),
        (P + "node_modules/mylib/index.js", "exports.lib = \"hello\";"),
        (P + "node_modules/mylib/index.d.ts", "export declare const lib: string;"));

    // Path resolution: node_modules package removed
    // tscwatch_test.go:298-318
    [Fact]
    public void WatchDetectsNodeModulesPackageRemoved() => RunWatch(Watch("watch detects node modules package removed",
        MylibInstalledFiles(),
        null,
        NewTscEdit("remove node_modules package", sys =>
        {
            sys.RemoveNoError(P + "node_modules/mylib/index.d.ts");
            sys.RemoveNoError(P + "node_modules/mylib/index.js");
            sys.RemoveNoError(P + "node_modules/mylib/package.json");
        })));

    // Path resolution: node_modules removed then reinstalled (npm ci after rm -rf)
    // tscwatch_test.go:320-345
    [Fact]
    public void WatchDetectsNodeModulesReinstalledAfterDeletion() => RunWatch(Watch("watch detects node modules reinstalled after deletion",
        MylibInstalledFiles(),
        null,
        NewTscEdit("delete node_modules entirely", sys =>
        {
            sys.RemoveNoError(P + "node_modules/mylib/index.d.ts");
            sys.RemoveNoError(P + "node_modules/mylib/index.js");
            sys.RemoveNoError(P + "node_modules/mylib/package.json");
        }),
        NewTscEdit("reinstall node_modules", sys =>
        {
            sys.WriteFileNoError(P + "node_modules/mylib/package.json", "{\"name\": \"mylib\", \"main\": \"index.js\", \"types\": \"index.d.ts\"}");
            sys.WriteFileNoError(P + "node_modules/mylib/index.js", "exports.lib = \"hello\";");
            sys.WriteFileNoError(P + "node_modules/mylib/index.d.ts", "export declare const lib: string;");
        })));

    // Config file lifecycle
    // tscwatch_test.go:347-363
    [Fact]
    public void WatchHandlesTsconfigDeleted() => RunWatch(Watch("watch handles tsconfig deleted",
        Files((P + "index.ts", "const x = 1;"), (P + "tsconfig.json", "{}")),
        null,
        EditWithDiff("delete tsconfig",
            "incremental reports config read error while clean build without tsconfig prints usage help",
            sys => sys.RemoveNoError(P + "tsconfig.json"))));

    // tscwatch_test.go:364-383
    [Fact]
    public void WatchHandlesTsconfigWithExtendsBaseModified() => RunWatch(Watch("watch handles tsconfig with extends base modified",
        Files(
            (P + "index.ts", "const x = null; const y: string = x;"),
            (P + "base.json", "{\n\t\"compilerOptions\": { \"strict\": false }\n}"),
            (P + "tsconfig.json", "{\n\t\"extends\": \"./base.json\"\n}")),
        null,
        NewTscEdit("modify base config to enable strict", sys => sys.WriteFileNoError(P + "base.json", "{\n\t\"compilerOptions\": { \"strict\": true }\n}"))));

    // tscwatch_test.go:384-397
    [Fact]
    public void WatchRebuildsWhenTsconfigIsTouchedButContentUnchanged() => RunWatch(Watch("watch rebuilds when tsconfig is touched but content unchanged",
        Files((P + "index.ts", "const x = 1;"), (P + "tsconfig.json", "{}")),
        null,
        NewTscEdit("touch tsconfig without changing content", sys =>
        {
            var content = sys.ReadFileNoError(P + "tsconfig.json");
            sys.WriteFileNoError(P + "tsconfig.json", content);
        })));

    // tscwatch_test.go:398-414
    [Fact]
    public void WatchWithTsconfigFilesListEntryDeleted() => RunWatch(Watch("watch with tsconfig files list entry deleted",
        Files(
            (P + "a.ts", "export const a = 1;"),
            (P + "b.ts", "export const b = 2;"),
            (P + "tsconfig.json", "{\n\t\"compilerOptions\": {},\n\t\"files\": [\"a.ts\", \"b.ts\"]\n}")),
        null,
        NewTscEdit("delete file listed in files array", sys => sys.RemoveNoError(P + "b.ts"))));

    // Module resolution & dependencies
    // tscwatch_test.go:416-436
    [Fact]
    public void WatchDetectsModuleGoingMissingThenComingBack() => RunWatch(Watch("watch detects module going missing then coming back",
        Files((P + "index.ts", "import { util } from \"./util\";"), (P + "util.ts", "export const util = \"v1\";"), (P + "tsconfig.json", "{}")),
        null,
        EditWithDiff("delete util module",
            "incremental resolves to .js output from prior build while clean build cannot find module",
            sys => sys.RemoveNoError(P + "util.ts")),
        NewTscEdit("recreate util module with new content", sys => sys.WriteFileNoError(P + "util.ts", "export const util = \"v2\";"))));

    // tscwatch_test.go:437-450
    [Fact]
    public void WatchDetectsScopedPackageInstalled() => RunWatch(Watch("watch detects scoped package installed",
        Files((P + "index.ts", "import { lib } from \"@scope/mylib\";"), (P + "tsconfig.json", "{}")),
        null,
        NewTscEdit("install scoped package", sys =>
        {
            sys.WriteFileNoError(P + "node_modules/@scope/mylib/package.json", "{\"name\": \"@scope/mylib\", \"types\": \"index.d.ts\"}");
            sys.WriteFileNoError(P + "node_modules/@scope/mylib/index.d.ts", "export declare const lib: string;");
        })));

    // tscwatch_test.go:451-466
    [Fact]
    public void WatchDetectsPackageJsonTypesFieldEdited() => RunWatch(Watch("watch detects package json types field edited",
        Files(
            (P + "index.ts", "import { lib } from \"mylib\";"),
            (P + "tsconfig.json", "{}"),
            (P + "node_modules/mylib/package.json", "{\"name\": \"mylib\", \"types\": \"old.d.ts\"}"),
            (P + "node_modules/mylib/old.d.ts", "export declare const lib: number;"),
            (P + "node_modules/mylib/new.d.ts", "export declare const lib: string;")),
        null,
        NewTscEdit("change package.json types field", sys => sys.WriteFileNoError(P + "node_modules/mylib/package.json", "{\"name\": \"mylib\", \"types\": \"new.d.ts\"}"))));

    // tscwatch_test.go:467-481
    [Fact]
    public void WatchDetectsAtTypesPackageInstalledLater() => RunWatch(Watch("watch detects at-types package installed later",
        Files(
            (P + "index.ts", "import * as lib from \"untyped-lib\";"),
            (P + "tsconfig.json", "{}"),
            (P + "node_modules/untyped-lib/index.js", "module.exports = {};")),
        null,
        NewTscEdit("install @types for the library", sys =>
        {
            sys.WriteFileNoError(P + "node_modules/@types/untyped-lib/index.d.ts", "declare module \"untyped-lib\" { export const value: string; }");
            sys.WriteFileNoError(P + "node_modules/@types/untyped-lib/package.json", "{\"name\": \"@types/untyped-lib\", \"types\": \"index.d.ts\"}");
        })));

    // File operations
    // tscwatch_test.go:483-503
    [Fact]
    public void WatchDetectsFileRenamedAndRenamedBack() => RunWatch(Watch("watch detects file renamed and renamed back",
        Files((P + "index.ts", "import { helper } from \"./helper\";"), (P + "helper.ts", "export const helper = 1;"), (P + "tsconfig.json", "{}")),
        null,
        EditWithDiff("rename helper to helper2",
            "incremental resolves to .js output from prior build while clean build cannot find module",
            sys => sys.RenameFileNoError(P + "helper.ts", P + "helper2.ts")),
        NewTscEdit("rename back to helper", sys => sys.RenameFileNoError(P + "helper2.ts", P + "helper.ts"))));

    // tscwatch_test.go:504-519
    [Fact]
    public void WatchDetectsFileDeletedAndNewFileAddedSimultaneously() => RunWatch(Watch("watch detects file deleted and new file added simultaneously",
        Files((P + "a.ts", "import { b } from \"./b\";"), (P + "b.ts", "export const b = 1;"), (P + "tsconfig.json", "{}")),
        null,
        NewTscEdit("delete b.ts and create c.ts with updated import", sys =>
        {
            sys.RemoveNoError(P + "b.ts");
            sys.WriteFileNoError(P + "c.ts", "export const c = 2;");
            sys.WriteFileNoError(P + "a.ts", "import { c } from \"./c\";");
        })));

    // tscwatch_test.go:520-534
    [Fact]
    public void WatchHandlesFileRapidlyRecreated() => RunWatch(Watch("watch handles file rapidly recreated",
        Files((P + "index.ts", "import { val } from \"./data\";"), (P + "data.ts", "export const val = \"original\";"), (P + "tsconfig.json", "{}")),
        null,
        NewTscEdit("delete and immediately recreate with new content", sys =>
        {
            sys.RemoveNoError(P + "data.ts");
            sys.WriteFileNoError(P + "data.ts", "export const val = \"recreated\";");
        })));

    // Symlinks — only node_modules symlinks are resolved via Realpath,
    // matching the TypeScript compiler's behavior (see program.ts:2119).
    // tscwatch_test.go:537-551
    [Fact]
    public void WatchDetectsChangeInSymlinkedNodeModulesFile() => RunWatch(Watch("watch detects change in symlinked node_modules file",
        Files(
            (P + "index.ts", "import { shared } from \"shared\";"),
            ("/home/src/workspaces/shared/index.ts", "export const shared = \"v1\";"),
            (P + "node_modules/shared/index.ts", VfsTestUtil.Symlink("/home/src/workspaces/shared/index.ts")),
            (P + "tsconfig.json", "{}")),
        null,
        NewTscEdit("modify symlink target", sys => sys.WriteFileNoError("/home/src/workspaces/shared/index.ts", "export const shared = \"v2\";"))));

    // Ancestor fallback stability — when a tsconfig include references a
    // directory that doesn't exist
    // tscwatch_test.go:554-566
    [Fact]
    public void WatchStabilityWithAncestorDirectoryFallback() => RunWatch(Watch("watch stability with ancestor directory fallback",
        Files((P + "index.ts", "const x: number = 1;"), (P + "tsconfig.json", "{ \"include\": [\"*.ts\", \"missing/**/*\"] }")),
        null,
        NewTscEdit("trivial file change", sys => sys.WriteFileNoError(P + "index.ts", "const x: number = 2;"))));

    // Ancestor fallback: creating deeply nested directories that didn't
    // exist at initial build time should trigger a rebuild and re-watch.
    // tscwatch_test.go:569-581
    [Fact]
    public void WatchDetectsFileAddedInDeeplyNestedNonExistentIncludePath() => RunWatch(Watch("watch detects file added in deeply nested non-existent include path",
        Files((P + "index.ts", "const x: number = 1;"), (P + "tsconfig.json", "{ \"include\": [\"*.ts\", \"deep/nested/dir/**/*\"] }")),
        null,
        NewTscEdit("create deeply nested file matching include", sys => sys.WriteFileNoError(P + "deep/nested/dir/added.ts", "export const added = 1;"))));
    // tscwatch_test.go:589-606 — listToTsconfig. The tsconfig text and the "optionString" used to
    // rebuild the same config with/without the leading noEmit option.
    private static (string TsconfigText, string OptionString) ListToTsconfig(string @base, params string[] tsconfigOpts)
    {
        var optionString = string.Join(",\n            ", tsconfigOpts);
        var tsconfigText = new StringBuilder("{\n\t\"compilerOptions\": {\n");
        var after = "            ";
        if (@base.Length != 0)
        {
            tsconfigText.Append("            ").Append(@base);
            after = ",\n            ";
        }
        if (tsconfigOpts.Length != 0)
        {
            tsconfigText.Append(after).Append(optionString);
        }
        tsconfigText.Append("\n\t}\n}");
        return (tsconfigText.ToString(), optionString);
    }

    // tscwatch_test.go:608-611 — toTsconfig.
    private static string ToTsconfig(string @base, string compilerOpts)
    {
        var (tsconfigText, _) = ListToTsconfig(@base, compilerOpts);
        return tsconfigText;
    }

    // tscwatch_test.go:651-653 — newTscEdit.
    private static TscEdit NewTscEdit(string name, Action<TestSys> edit) =>
        new() { caption = name, edit = edit };

    // tscwatch_test.go:613-649 — noEmitWatchTestInput.
    private static TscInput NoEmitWatchTestInput(
        string subScenario,
        string[] commandLineArgs,
        string aText,
        string[]? tsconfigOptions)
    {
        const string NoEmitOpt = "\"noEmit\": true";
        var (tsconfigText, optionString) = ListToTsconfig(NoEmitOpt, tsconfigOptions ?? []);
        return new TscInput
        {
            subScenario = subScenario,
            commandLineArgs = commandLineArgs,
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/a.ts"] = aText,
                ["/home/src/workspaces/project/tsconfig.json"] = tsconfigText,
            },
            edits =
            [
                NewTscEdit("fix error", sys => sys.WriteFileNoError("/home/src/workspaces/project/a.ts", "const a = \"hello\";")),
                NewTscEdit("emit after fixing error", sys => sys.WriteFileNoError("/home/src/workspaces/project/tsconfig.json", ToTsconfig("", optionString))),
                NewTscEdit("no emit run after fixing error", sys => sys.WriteFileNoError("/home/src/workspaces/project/tsconfig.json", ToTsconfig(NoEmitOpt, optionString))),
                NewTscEdit("introduce error", sys => sys.WriteFileNoError("/home/src/workspaces/project/a.ts", aText)),
                NewTscEdit("emit when error", sys => sys.WriteFileNoError("/home/src/workspaces/project/tsconfig.json", ToTsconfig("", optionString))),
                NewTscEdit("no emit run when error", sys => sys.WriteFileNoError("/home/src/workspaces/project/tsconfig.json", ToTsconfig(NoEmitOpt, optionString))),
            ],
        };
    }

    // tscwatch_test.go:658-683 — the ordered testCase list; one [Fact] per entry (Go loop: 685-687).
    private static TscInput[] NoEmitWatchTestCases() =>
    [
        NoEmitWatchTestInput("syntax errors", ["-w"], "const a = \"hello", null),
        NoEmitWatchTestInput("semantic errors", ["-w"], "const a: number = \"hello\"", null),
        NoEmitWatchTestInput("dts errors without dts enabled", ["-w"], "const a = class { private p = 10; };", null),
        NoEmitWatchTestInput("dts errors", ["-w"], "const a = class { private p = 10; };", ["\"declaration\": true"]),
    ];

    [Fact]
    public void WatchSyntaxErrors() => NoEmitWatchTestCases()[0].RunAndAssert("noEmit");

    [Fact]
    public void WatchSemanticErrors() => NoEmitWatchTestCases()[1].RunAndAssert("noEmit");

    [Fact]
    public void WatchDtsErrorsWithoutDtsEnabled() => NoEmitWatchTestCases()[2].RunAndAssert("noEmit");

    [Fact]
    public void WatchDtsErrors() => NoEmitWatchTestCases()[3].RunAndAssert("noEmit");
}
