// Ported from tsgo internal/execute/tsctests/runner.go — the tsc/tsbuild baseline runner
// (initial compile + edits, incremental-vs-clean diff audit, baseline compare against
// testdata/baselines/reference/{tsc,tsbuild}[Watch]).

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Ts.Core;
using Ts.Execute;
using Ts.TestUtil;


namespace Ts.Cli.Tests.Execute;

using TsPathApi = global::Ts.TsPath.TsPath;

// runner.go:18-31
internal sealed class TscEdit
{
    internal string caption = "";
    internal string[]? commandLineArgs = null;
    internal Action<TestSys>? edit = null;
    internal string expectedDiff = "";

    internal static readonly TscEdit NoChange = new() { caption = "no change" };

    internal static readonly TscEdit[] NoChangeOnlyEdit = [NoChange];
}

// runner.go:33-42
internal sealed class TscInput
{
    internal string subScenario = "";
    internal string[] commandLineArgs = [];
    internal Dictionary<string, object> files = new();
    internal string cwd = "";
    internal TscEdit[]? edits = null;
    internal Dictionary<string, string>? env = null;
    internal bool ignoreCase = false;
    internal string windowsStyleRoot = "";

    // runner.go:44-64
    internal CommandLineResult ExecuteCommand(TestSys sys, StringBuilder baselineBuilder, string[] commandLineArgs)
    {
        baselineBuilder.Append("tsgo ").Append(string.Join(" ", commandLineArgs)).Append('\n');
        var result = Tsc.CommandLine(sys, commandLineArgs, sys);
        switch (result.Status)
        {
            case ExitStatus.Success:
                baselineBuilder.Append("ExitStatus:: Success");
                break;
            case ExitStatus.DiagnosticsPresent_OutputsSkipped:
                baselineBuilder.Append("ExitStatus:: DiagnosticsPresent_OutputsSkipped");
                break;
            case ExitStatus.DiagnosticsPresent_OutputsGenerated:
                baselineBuilder.Append("ExitStatus:: DiagnosticsPresent_OutputsGenerated");
                break;
            case ExitStatus.InvalidProject_OutputsSkipped:
                baselineBuilder.Append("ExitStatus:: InvalidProject_OutputsSkipped");
                break;
            case ExitStatus.ProjectReferenceCycle_OutputsSkipped:
                baselineBuilder.Append("ExitStatus:: ProjectReferenceCycle_OutputsSkipped");
                break;
            case ExitStatus.NotImplemented:
                baselineBuilder.Append("ExitStatus:: NotImplemented");
                break;
            default:
                throw new InvalidOperationException($"UnknownExitStatus {result.Status}");
        }
        return result;
    }

    // runner.go:66-144
    internal void Run(IBaselineTestContext t, string scenario)
    {
        // initial test tsc compile
        var baselineBuilder = new StringBuilder();
        var sys = TestSys.NewTestSys(this, false);
        baselineBuilder
            .Append("currentDirectory::")
            .Append(sys.GetCurrentDirectory())
            .Append("\nuseCaseSensitiveFileNames::")
            .Append(sys.FS().UseCaseSensitiveFileNames() ? "true" : "false")
            .Append("\nInput::\n");
        sys.BaselineFSwithDiff(baselineBuilder);
        var result = ExecuteCommand(sys, baselineBuilder, commandLineArgs);
        sys.SerializeState(baselineBuilder);
        if (result.Watcher != null && sys.mockWatchBackend.HasWatches())
        {
            baselineBuilder.Append(sys.mockWatchBackend.WatchState());
        }
        var unexpectedDiff = new StringBuilder();
        unexpectedDiff.Append(sys.BaselinePrograms(baselineBuilder, "Initial build"));

        var editList = edits ?? [];
        for (var index = 0; index < editList.Length; index++)
        {
            var doEdit = editList[index];
            sys.ClearOutput();
            var wg = WorkGroup.Create(false);
            TestSys? nonIncrementalSys = null;
            var editCommandLineArgs = doEdit.commandLineArgs ?? commandLineArgs;
            wg.Queue(() =>
            {
                baselineBuilder.Append($"\n\nEdit [{index}]:: {doEdit.caption}\n");
                doEdit.edit?.Invoke(sys);
                var changedPaths = sys.fsDiffer.ChangedPaths();
                sys.BaselineFSwithDiff(baselineBuilder);

                if (result.Watcher == null)
                {
                    ExecuteCommand(sys, baselineBuilder, editCommandLineArgs);
                }
                else
                {
                    sys.mockWatchBackend.SendChangedPaths(changedPaths);
                    result.Watcher.DoCycle();
                }
                sys.SerializeState(baselineBuilder);
                if (result.Watcher != null && sys.mockWatchBackend.HasWatches())
                {
                    baselineBuilder.Append(sys.mockWatchBackend.WatchState());
                }
                unexpectedDiff.Append(sys.BaselinePrograms(baselineBuilder, $"Edit [{index}]:: {doEdit.caption}\n"));
            });
            var editIndex = index;
            wg.Queue(() =>
            {
                // Compute build with all the edits
                nonIncrementalSys = TestSys.NewTestSys(this, true);
                for (var i = 0; i < editIndex + 1; i++)
                {
                    editList[i].edit?.Invoke(nonIncrementalSys);
                }
                Tsc.CommandLine(nonIncrementalSys, editCommandLineArgs, nonIncrementalSys);
            });
            wg.RunAndWait();

            var diff = GetDiffForIncremental(sys, nonIncrementalSys!);
            if (diff.Length > 0)
            {
                baselineBuilder.Append($"\n\nDiff:: {(doEdit.expectedDiff.Length == 0 ? "!!! Unexpected diff, please review and either fix or write explanation as expectedDiff !!!" : doEdit.expectedDiff)}\n");
                baselineBuilder.Append(diff);
                if (doEdit.expectedDiff.Length == 0)
                {
                    unexpectedDiff.Append($"Edit [{index}]:: {doEdit.caption}\n!!! Unexpected diff, please review and either fix or write explanation as expectedDiff !!!\n{diff}\n");
                }
            }
            else if (doEdit.expectedDiff.Length > 0)
            {
                baselineBuilder.Append($"\n\nDiff:: {doEdit.expectedDiff} !!! Diff not found but explanation present, please review and remove the explanation !!!\n");
                unexpectedDiff.Append($"Edit [{index}]:: {doEdit.caption}\n!!! Diff not found but explanation present, please review and remove the explanation !!!\n");
            }
        }
        global::Ts.TestUtil.Baseline.Run(t, subScenario.Replace(" ", "-") + ".js", baselineBuilder.ToString(), new global::Ts.TestUtil.Baseline.Options
        {
            Subfolder = Path.Combine(GetBaselineSubFolder(), scenario),
        });
        if (unexpectedDiff.Length > 0)
        {
            t.Error($"Test {subScenario} has unexpected diff {unexpectedDiff} with incremental build, please review the baseline file");
        }
    }

    // runner.go:146-178
    private static string GetDiffForIncremental(TestSys incrementalSys, TestSys nonIncrementalSys)
    {
        var diffBuilder = new StringBuilder();

        var nonIncrementalOutputs = nonIncrementalSys.fs.writtenFiles.ToSlice();
        Array.Sort(nonIncrementalOutputs, StringComparer.Ordinal);
        foreach (var nonIncrementalOutput in nonIncrementalOutputs)
        {
            if (TsPathApi.FileExtensionIs(nonIncrementalOutput, TsPathApi.ExtensionTsBuildInfo) ||
                nonIncrementalOutput.EndsWith(".readable.baseline.txt", StringComparison.Ordinal))
            {
                // Just check existence
                if (!incrementalSys.FsFromFileMap().FileExists(nonIncrementalOutput))
                {
                    diffBuilder.Append(global::Ts.TestUtil.Baseline.DiffText("nonIncremental " + nonIncrementalOutput, "incremental " + nonIncrementalOutput, "Exists", ""));
                    diffBuilder.Append('\n');
                }
            }
            else
            {
                if (!nonIncrementalSys.FsFromFileMap().TryReadFile(nonIncrementalOutput, out var nonIncrementalText))
                {
                    throw new InvalidOperationException("Written file not found " + nonIncrementalOutput);
                }
                if (!incrementalSys.FsFromFileMap().TryReadFile(nonIncrementalOutput, out var incrementalText) || incrementalText != nonIncrementalText)
                {
                    diffBuilder.Append(global::Ts.TestUtil.Baseline.DiffText("nonIncremental " + nonIncrementalOutput, "incremental " + nonIncrementalOutput, nonIncrementalText, incrementalText));
                    diffBuilder.Append('\n');
                }
            }
        }

        var incrementalOutput = incrementalSys.GetOutput(true);
        var nonIncrementalOutput2 = nonIncrementalSys.GetOutput(true);
        if (incrementalOutput != nonIncrementalOutput2)
        {
            diffBuilder.Append(global::Ts.TestUtil.Baseline.DiffText("nonIncremental.output.txt", "incremental.output.txt", nonIncrementalOutput2, incrementalOutput));
        }
        return diffBuilder.ToString();
    }

    // runner.go:180-202
    internal string GetBaselineSubFolder()
    {
        var commandName = "tsc";
        foreach (var arg in commandLineArgs)
        {
            if (arg is "-b" or "--b" or "-build" or "--build")
            {
                commandName = "tsbuild";
                break;
            }
        }
        var w = "";
        foreach (var arg in commandLineArgs)
        {
            if (arg is "-w" or "--w" or "-watch" or "--watch")
            {
                w = "Watch";
                break;
            }
        }
        return commandName + w;
    }
}
