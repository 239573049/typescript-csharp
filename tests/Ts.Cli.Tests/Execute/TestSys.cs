// Ported from tsgo internal/execute/tsctests/sys.go — the deterministic tsc-test System with
// baselining hooks (output sanitizer, FS diff, program/signature baselines, mock watches).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Ts.Collections;
using Ts.Execute;
using Ts.TestUtil;
using Ts.TestUtil.FsBaselineUtil;
using Ts.Vfs;
using Ts.Vfs.IoVfs;
using Ts.Vfs.VfsTest;

namespace Ts.Cli.Tests.Execute;

using PathT = global::Ts.TsPath.Path;
using TsPathApi = global::Ts.TsPath.TsPath;

// sys.go:33
// FileMap map[string]any — string or MapFile values.

public static class TscTestDefaults
{
    // sys.go:35
    public const string TscLibPath = "/home/src/tslibs/TS/Lib";

    // sys.go:37-60
    public static readonly string TscDefaultLibContent = StringTestUtil.Dedent("""

/// <reference no-default-lib="true"/>
interface Boolean {}
interface Function {}
interface CallableFunction {}
interface NewableFunction {}
interface IArguments {}
interface Number { toExponential: any; }
interface Object {}
interface RegExp {}
interface String { charAt: any; }
interface Array<T> { length: number; [n: number]: T; }
interface ReadonlyArray<T> {}
interface SymbolConstructor {
    (desc?: string | number): symbol;
    for(name: string): symbol;
    readonly toStringTag: symbol;
}
declare var Symbol: SymbolConstructor;
interface Symbol {
    readonly [Symbol.toStringTag]: string;
}
declare const console: { log(msg: any): void; };

""");

    // sys.go:62-70
    public static string GetTestLibPathFor(string libName)
    {
        string libFile;
        if (global::Ts.TsOptions.EnumMaps.LibMap.Has(libName) && global::Ts.TsOptions.EnumMaps.LibMap.GetOrZero(libName) is string mapped)
        {
            libFile = mapped;
        }
        else
        {
            libFile = "lib." + libName + ".d.ts";
        }
        return TscLibPath + "/" + libFile;
    }
}

// sys.go:72-90
internal sealed class TestClock : IClock
{
    internal DateTime start;
    private DateTime now;
    private readonly System.Threading.Lock nowMu = new();

    public DateTime Now()
    {
        lock (nowMu)
        {
            if (now == default)
            {
                now = start;
            }
            now = now.AddSeconds(1); // Simulate some time passing
            return now;
        }
    }

    public TimeSpan SinceStart() => Now() - start;
}

// sys.go:155-169
internal sealed class TestSys : ISystem, ICommandLineTesting, global::Ts.Execute.WatchManagement.ICommandLineTestingWithWatchBackend
{
    internal StringBuilder currentWrite = null!;
    private readonly StringBuilder programBaselines = new();
    private readonly StringBuilder programIncludeBaselines = new();
    internal TracerForBaselining tracer = null!;
    internal FSDiffer fsDiffer = null!;
    internal bool forIncrementalCorrectness;
    internal MockWatchBackend mockWatchBackend = null!;

    internal TestFs fs = null!;
    internal string defaultLibraryPath = "";
    internal string cwd = "";
    internal Dictionary<string, string>? env;
    internal TestClock clock = null!;

    private TextWriter writer = null!;

    // sys.go:92-101
    internal static TestSys NewTscSystem(Dictionary<string, object> files, bool useCaseSensitiveFileNames, string cwd)
    {
        var clock = new TestClock { start = DateTime.UtcNow };
        return new TestSys
        {
            fs = new TestFs(VfsTestUtil.FromMapWithClock(files, useCaseSensitiveFileNames, clock)),
            cwd = cwd,
            clock = clock,
        };
    }

    // sys.go:103-115
    internal static Dictionary<string, object> GetFileMapWithBuild(Dictionary<string, object> files, string[] commandLineArgs)
    {
        var sys = NewTestSys(new TscInput
        {
            files = new Dictionary<string, object>(files),
        }, false);
        global::Ts.Execute.Tsc.CommandLine(sys, commandLineArgs, sys);
        sys.fs.writtenFiles.Range(key =>
        {
            if (sys.FsFromFileMap().TryReadFile(key, out var text))
            {
                files[key] = text;
            }
            return true;
        });
        return files;
    }

    // sys.go:117-153
    internal static TestSys NewTestSys(TscInput tscInput, bool forIncrementalCorrectness)
    {
        var cwd = tscInput.cwd;
        if (cwd.Length == 0)
        {
            cwd = "/home/src/workspaces/project";
        }
        var libPath = TscTestDefaults.TscLibPath;
        if (tscInput.windowsStyleRoot.Length > 0)
        {
            libPath = tscInput.windowsStyleRoot + libPath[1..];
        }
        var currentWrite = new StringBuilder();
        var sys = NewTscSystem(tscInput.files, !tscInput.ignoreCase, cwd);
        sys.defaultLibraryPath = libPath;
        sys.currentWrite = currentWrite;
        sys.writer = new StringWriter(currentWrite);
        sys.tracer = new TracerForBaselining(new global::Ts.TsPath.ComparePathsOptions
        {
            UseCaseSensitiveFileNames = !tscInput.ignoreCase,
            CurrentDirectory = cwd,
        }, currentWrite);
        sys.env = tscInput.env;
        sys.forIncrementalCorrectness = forIncrementalCorrectness;
        sys.mockWatchBackend = new MockWatchBackend();
        sys.mockWatchBackend.DirectoryExists = sys.fs.FS.DirectoryExists;
        sys.fsDiffer = new FSDiffer
        {
            FS = (IFsWithSys)sys.fs.FS,
            DefaultLibs = () => sys.fs.defaultLibs,
            WrittenFiles = sys.fs.writtenFiles,
        };

        // Ensure the default library file is present
        sys.EnsureLibPathExists("lib.d.ts");
        foreach (var libFile in global::Ts.TsOptions.EnumMaps.TargetToLibMap().Values) // map-iter: 序无关 (idempotent ensure)
        {
            sys.EnsureLibPathExists(libFile);
        }
        foreach (var libFile in SetExtensions.Keys(global::Ts.TsOptions.EnumMaps.LibFilesSet)) // map-iter: 序无关 (idempotent ensure)
        {
            sys.EnsureLibPathExists(libFile);
        }
        return sys;
    }

    // sys.go:176-182
    public DateTime Now() => clock.Now();

    public TimeSpan SinceStart() => clock.SinceStart();

    // sys.go:184-186
    public IFileSystem FS() => fs;

    // sys.go:188-190
    internal IFsWithSys FsFromFileMap() => fsDiffer.FS;

    // sys.go:192-194
    internal MapFS MapFs() => fsDiffer.MapFs();

    // sys.go:196-208
    private void EnsureLibPathExists(string path)
    {
        path = defaultLibraryPath + "/" + path;
        if (!FsFromFileMap().TryReadFile(path, out _))
        {
            fs.defaultLibs ??= new SyncSet<string>();
            fs.defaultLibs.Add(path);
            FsFromFileMap().WriteFile(path, TscTestDefaults.TscDefaultLibContent);
        }
    }

    // sys.go:210-235
    public string DefaultLibraryPath() => defaultLibraryPath;

    public string GetCurrentDirectory() => cwd;

    public TextWriter Writer() => writer;

    public bool WriteOutputIsTTY() => true;

    public int GetWidthOfTerminal()
    {
        var widthStr = GetEnvironmentVariable("TS_TEST_TERMINAL_WIDTH");
        if (widthStr.Length > 0)
        {
            return int.Parse(widthStr, CultureInfo.InvariantCulture);
        }
        return 0;
    }

    public string GetEnvironmentVariable(string name) => env?.GetValueOrDefault(name) ?? "";

    // sys.go:237-262
    public void OnEmittedFiles(global::Ts.Compiler.EmitResult result, SyncMap<PathT, DateTime>? mTimesCache)
    {
        if (result != null)
        {
            foreach (var file in result.EmittedFiles ?? [])
            {
                var modTime = MapFs().GetModTime(file);
                var serializedDiff = fsDiffer.SerializedDiff();
                if (serializedDiff != null &&
                    serializedDiff.Snap.TryGetValue(file, out var diff) && diff.MTime == modTime)
                {
                    // Even though written, timestamp was reverted
                    continue;
                }

                // Ensure that the timestamp for emitted files is in the order
                var now = Now();
                FsFromFileMap().Chtimes(file, default, now);
                // Update the mTime cache in --b mode to store the updated timestamp so tests will behave deteministically when finding newest output
                if (mTimesCache != null)
                {
                    var path = TsPathApi.ToPath(file, GetCurrentDirectory(), FS().UseCaseSensitiveFileNames());
                    if (mTimesCache.TryLoad(path, out _))
                    {
                        mTimesCache.Store(path, now);
                    }
                }
            }
        }
    }

    // sys.go:264-294
    public void OnListFilesStart(TextWriter w) => w.Write(Markers.ListFileStart + "\n");

    public void OnListFilesEnd(TextWriter w) => w.Write(Markers.ListFileEnd + "\n");

    public void OnStatisticsStart(TextWriter w) => w.Write(Markers.StatisticsStart + "\n");

    public void OnStatisticsEnd(TextWriter w) => w.Write(Markers.StatisticsEnd + "\n");

    public void OnBuildStatusReportStart(TextWriter w) => w.Write(Markers.BuildStatusReportStart + "\n");

    public void OnBuildStatusReportEnd(TextWriter w) => w.Write(Markers.BuildStatusReportEnd + "\n");

    public void OnWatchStatusReportStart() => Writer().Write(Markers.WatchStatusReportStart + "\n");

    public void OnWatchStatusReportEnd() => Writer().Write(Markers.WatchStatusReportEnd + "\n");

    // sys.go:296-305
    public Action<global::Ts.Diagnostics.Message, object?[]>? GetTrace(TextWriter w, global::Ts.Locale.Locale locale)
    {
        return (msg, args) =>
        {
            w.Write(Markers.TraceStart + "\n");
            try
            {
                // With tsc -b building projects in parallel we cannot serialize the package.json lookup trace
                // so trace as if it wasnt cached
                var str = global::Ts.Diagnostics.Diagnostics.Localize(msg, locale, args);
                tracer.TraceWithWriter(w, str, ReferenceEquals(w, Writer()));
            }
            finally
            {
                w.Write(Markers.TraceEnd + "\n");
            }
        };
    }

    // sys.go:307-319
    private void WriteHeaderToBaseline(StringBuilder builder, global::Ts.Execute.Incremental.Program program)
    {
        if (builder.Length != 0)
        {
            builder.Append('\n');
        }

        var configFilePath = program.Options().ConfigFilePath;
        if (configFilePath.Length > 0)
        {
            builder.Append(TsPathApi.GetRelativePathFromDirectory(cwd, configFilePath, new global::Ts.TsPath.ComparePathsOptions
            {
                UseCaseSensitiveFileNames = FS().UseCaseSensitiveFileNames(),
                CurrentDirectory = GetCurrentDirectory(),
            }));
            builder.Append("::\n");
        }
    }

    // sys.go:321-323
    public global::Ts.Execute.WatchManagement.WatchBackend WatchBackend() => mockWatchBackend;

    // sys.go:325-393
    public void OnProgram(global::Ts.Execute.Incremental.Program? program)
    {
        if (program == null)
        {
            return;
        }
        WriteHeaderToBaseline(programBaselines, program);

        var testingData = program.GetTestingData()!;
        programBaselines.Append("SemanticDiagnostics::\n");
        foreach (var file in program.GetProgram().SourceFiles())
        {
            if (testingData.SemanticDiagnosticsPerFile.TryLoad(file.Path(), out var diagnostics))
            {
                if (!testingData.OldProgramSemanticDiagnosticsPerFile.TryLoad(file.Path(), out var oldDiagnostics) || !ReferenceEquals(oldDiagnostics, diagnostics))
                {
                    programBaselines.Append("*refresh*    ");
                    programBaselines.Append(file.FileName());
                    programBaselines.Append('\n');
                }
            }
            else
            {
                programBaselines.Append("*not cached* ");
                programBaselines.Append(file.FileName());
                programBaselines.Append('\n');
            }
        }

        // Write signature updates
        programBaselines.Append("Signatures::\n");
        foreach (var file in program.GetProgram().SourceFiles())
        {
            if (testingData.UpdatedSignatureKinds.TryGetValue(file.Path(), out var kind))
            {
                switch (kind)
                {
                    case global::Ts.Execute.Incremental.SignatureUpdateKind.ComputedDts:
                        programBaselines.Append("(computed .d.ts) ");
                        programBaselines.Append(file.FileName());
                        programBaselines.Append('\n');
                        break;
                    case global::Ts.Execute.Incremental.SignatureUpdateKind.StoredAtEmit:
                        programBaselines.Append("(stored at emit) ");
                        programBaselines.Append(file.FileName());
                        programBaselines.Append('\n');
                        break;
                    case global::Ts.Execute.Incremental.SignatureUpdateKind.UsedVersion:
                        programBaselines.Append("(used version)   ");
                        programBaselines.Append(file.FileName());
                        programBaselines.Append('\n');
                        break;
                }
            }
        }

        List<string>? filesWithoutIncludeReason = null;
        List<string>? fileNotInProgramWithIncludeReason = null;
        var includeReasons = program.GetProgram().GetIncludeReasons();
        foreach (var file in program.GetProgram().SourceFiles())
        {
            if (!includeReasons.ContainsKey(file.Path()))
            {
                (filesWithoutIncludeReason ??= new List<string>()).Add(file.Path().ToString());
            }
        }
        foreach (var path in includeReasons.Keys) // map-iter: 序无关 (audit list; rare failure path)
        {
            if (program.GetProgram().GetSourceFileByPath(path) == null && !program.GetProgram().IsMissingPath(path))
            {
                (fileNotInProgramWithIncludeReason ??= new List<string>()).Add(path.ToString());
            }
        }
        if (filesWithoutIncludeReason is { Count: > 0 } || fileNotInProgramWithIncludeReason is { Count: > 0 })
        {
            WriteHeaderToBaseline(programIncludeBaselines, program);
            programIncludeBaselines.Append("!!! Expected all files to have include reasons\nfilesWithoutIncludeReason::\n");
            foreach (var file in filesWithoutIncludeReason ?? new List<string>())
            {
                programIncludeBaselines.Append("  ").Append(file).Append('\n');
            }
            programIncludeBaselines.Append("filesNotInProgramWithIncludeReason::\n");
            foreach (var file in fileNotInProgramWithIncludeReason ?? new List<string>())
            {
                programIncludeBaselines.Append("  ").Append(file).Append('\n');
            }
        }
    }

    // sys.go:395-406
    internal string BaselinePrograms(StringBuilder baseline, string header)
    {
        baseline.Append(programBaselines);
        programBaselines.Clear();
        var result = "";
        if (programIncludeBaselines.Length > 0)
        {
            result += $"\n\n{header}\n!!! Include reasons expectations don't match pls review!!!\n";
            result += programIncludeBaselines.ToString();
            programIncludeBaselines.Clear();
            baseline.Append(result);
        }
        return result;
    }

    // sys.go:408-417
    internal void SerializeState(StringBuilder baseline)
    {
        BaselineOutput(baseline);
        BaselineFSwithDiff(baseline);
        // todo watch
        // this.serializeWatches(baseline);
        // this.timeoutCallbacks.serialize(baseline);
        // this.immediateCallbacks.serialize(baseline);
        // this.pendingInstalls.serialize(baseline);
        // this.service?.baseline();
    }

    // sys.go:419-435
    internal static class Markers
    {
        internal const string FakeTimeStamp = "HH:MM:SS AM";
        internal const string FakeDuration = "d.ddds";

        internal const string BuildStartingAt = "build starting at ";
        internal const string BuildFinishedIn = "build finished in ";
        internal const string ListFileStart = "!!! List files start";
        internal const string ListFileEnd = "!!! List files end";
        internal const string StatisticsStart = "!!! Statistics start";
        internal const string StatisticsEnd = "!!! Statistics end";
        internal const string BuildStatusReportStart = "!!! Build Status Report Start";
        internal const string BuildStatusReportEnd = "!!! Build Status Report End";
        internal const string WatchStatusReportStart = "!!! Watch Status Report Start";
        internal const string WatchStatusReportEnd = "!!! Watch Status Report End";
        internal const string TraceStart = "!!! Trace start";
        internal const string TraceEnd = "!!! Trace end";
    }

    // sys.go:437-441
    private void BaselineOutput(StringBuilder baseline)
    {
        baseline.Append("\nOutput::\n");
        baseline.Append(GetOutput(false));
    }

    // sys.go:528-536
    internal string GetOutput(bool forComparing)
    {
        var lines = currentWrite.ToString().Split('\n');
        var transformer = new OutputSanitizer
        {
            forComparing = forComparing,
            lines = lines,
        };
        return transformer.TransformLines();
    }

    // sys.go:538-541
    internal void ClearOutput()
    {
        currentWrite.Clear();
        tracer.Reset();
    }

    // sys.go:543-545
    internal void BaselineFSwithDiff(StringBuilder baseline)
    {
        using var w = new StringWriter(baseline);
        fsDiffer.BaselineFSwithDiff(w);
    }

    // sys.go:547-592 — edit helpers.
    internal void WriteFileNoError(string path, string content) => FsFromFileMap().WriteFile(path, content);

    internal void RemoveNoError(string path) => FsFromFileMap().Remove(path);

    internal string ReadFileNoError(string path)
    {
        if (!FsFromFileMap().TryReadFile(path, out var content))
        {
            throw new InvalidOperationException("File not found: " + path);
        }
        return content;
    }

    internal void RenameFileNoError(string oldPath, string newPath)
    {
        WriteFileNoError(newPath, ReadFileNoError(oldPath));
        RemoveNoError(oldPath);
    }

    internal void ReplaceFileText(string path, string oldText, string newText)
    {
        var content = ReadFileNoError(path);
        var idx = content.IndexOf(oldText, StringComparison.Ordinal);
        if (idx >= 0)
        {
            content = content[..idx] + newText + content[(idx + oldText.Length)..];
        }
        WriteFileNoError(path, content);
    }

    internal void ReplaceFileTextAll(string path, string oldText, string newText)
    {
        var content = ReadFileNoError(path);
        content = content.Replace(oldText, newText, StringComparison.Ordinal);
        WriteFileNoError(path, content);
    }

    internal void AppendFileText(string path, string text)
    {
        var content = ReadFileNoError(path);
        WriteFileNoError(path, content + text);
    }

    internal void PrependFileText(string path, string text)
    {
        var content = ReadFileNoError(path);
        WriteFileNoError(path, text + content);
    }
}

// sys.go:443-526 — output sanitizer.
internal sealed class OutputSanitizer
{
    internal bool forComparing;
    internal string[] lines = [];
    private int index;
    private readonly List<string> outputLines = new();

    // sys.go:450-456
    private static readonly string EnglishVersion = global::Ts.Diagnostics.Diagnostics.Localize(
        global::Ts.Diagnostics.Diagnostics.Version_0, global::Ts.Locale.Locale.Default, global::Ts.Core.TsVersion.Version());

    private static readonly string FakeEnglishVersion = global::Ts.Diagnostics.Diagnostics.Localize(
        global::Ts.Diagnostics.Diagnostics.Version_0, global::Ts.Locale.Locale.Default, HarnessUtil.FakeTSVersion);

    private static readonly global::Ts.Locale.Locale Czech = CzechLocale();

    private static global::Ts.Locale.Locale CzechLocale()
    {
        global::Ts.Locale.Locale.TryParse("cs", out var locale);
        return locale;
    }

    private static readonly string CzechVersion = global::Ts.Diagnostics.Diagnostics.Localize(
        global::Ts.Diagnostics.Diagnostics.Version_0, Czech, global::Ts.Core.TsVersion.Version());

    private static readonly string FakeCzechVersion = global::Ts.Diagnostics.Diagnostics.Localize(
        global::Ts.Diagnostics.Diagnostics.Version_0, Czech, HarnessUtil.FakeTSVersion);

    // sys.go:458-464
    private void AddOutputLine(string s)
    {
        s = s.Replace("'" + global::Ts.Core.TsVersion.Version() + "'", "'" + HarnessUtil.FakeTSVersion + "'", StringComparison.Ordinal);
        s = s.Replace(EnglishVersion, FakeEnglishVersion, StringComparison.Ordinal);
        s = s.Replace(CzechVersion, FakeCzechVersion, StringComparison.Ordinal);
        s = FSDiffer.SanitizeInternalSymbolName(s);
        outputLines.Add(s);
    }

    // sys.go:466-473
    private string SanitizeBuildStatusTimeStamp()
    {
        var statusLine = lines[index];
        var hhSeparator = statusLine.IndexOf(':', StringComparison.Ordinal);
        if (hhSeparator < 2)
        {
            throw new InvalidOperationException("Expected timestamp");
        }
        return statusLine[..(hhSeparator - 2)] + TestSys.Markers.FakeTimeStamp + statusLine[(hhSeparator + TestSys.Markers.FakeTimeStamp.Length - 2)..];
    }

    // sys.go:475-499
    internal string TransformLines()
    {
        for (; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.StartsWith(TestSys.Markers.BuildStartingAt, StringComparison.Ordinal))
            {
                if (!forComparing)
                {
                    AddOutputLine(TestSys.Markers.BuildStartingAt + TestSys.Markers.FakeTimeStamp);
                }
                continue;
            }
            if (line.StartsWith(TestSys.Markers.BuildFinishedIn, StringComparison.Ordinal))
            {
                if (!forComparing)
                {
                    AddOutputLine(TestSys.Markers.BuildFinishedIn + TestSys.Markers.FakeDuration);
                }
                continue;
            }
            if (!AddOrSkipLinesForComparing(TestSys.Markers.ListFileStart, TestSys.Markers.ListFileEnd, false, null) &&
                !AddOrSkipLinesForComparing(TestSys.Markers.StatisticsStart, TestSys.Markers.StatisticsEnd, true, null) &&
                !AddOrSkipLinesForComparing(TestSys.Markers.TraceStart, TestSys.Markers.TraceEnd, false, null) &&
                !AddOrSkipLinesForComparing(TestSys.Markers.BuildStatusReportStart, TestSys.Markers.BuildStatusReportEnd, false, SanitizeBuildStatusTimeStamp) &&
                !AddOrSkipLinesForComparing(TestSys.Markers.WatchStatusReportStart, TestSys.Markers.WatchStatusReportEnd, false, SanitizeBuildStatusTimeStamp))
            {
                AddOutputLine(line);
            }
        }
        return string.Join("\n", outputLines);
    }

    // sys.go:501-526
    private bool AddOrSkipLinesForComparing(
        string lineStart,
        string lineEnd,
        bool skipEvenIfNotComparing,
        Func<string>? sanitizeFirstLine)
    {
        if (lines[index] != lineStart)
        {
            return false;
        }
        index++;
        var isFirstLine = true;
        for (; index < lines.Length; index++)
        {
            if (lines[index] == lineEnd)
            {
                return true;
            }
            if (!forComparing && !skipEvenIfNotComparing)
            {
                var line = lines[index];
                if (isFirstLine && sanitizeFirstLine != null)
                {
                    line = sanitizeFirstLine();
                    isFirstLine = false;
                }
                AddOutputLine(line);
            }
        }
        throw new InvalidOperationException("Expected lineEnd" + lineEnd + " not found after " + lineStart);
    }
}
