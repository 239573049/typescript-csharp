// Deterministic smoke coverage for the execute watcher and --build project graph. The manual
// IWatcher invokes callbacks synchronously; no test uses FileSystemWatcher timing or sleeps.

using System;
using System.Collections.Generic;
using System.IO;
using Ts.Execute;
using Ts.Vfs;
using Ts.Vfs.VfsTest;
using Xunit;

namespace Ts.Cli.Tests;

public sealed class WatchBuildTests
{
    private sealed class FakeSystem : ISystem
    {
        private readonly StringWriter writer = new();
        private readonly IFileSystem fs;
        private readonly string cwd;

        internal FakeSystem(IFileSystem fs, string cwd)
        {
            this.fs = fs;
            this.cwd = cwd;
        }

        internal string Output => writer.ToString();
        public TextWriter Writer() => writer;
        public IFileSystem FS() => fs;
        public string DefaultLibraryPath() => "/lib";
        public string GetCurrentDirectory() => cwd;
        public bool WriteOutputIsTTY() => false;
        public int GetWidthOfTerminal() => 0;
        public string GetEnvironmentVariable(string name) => name == "NO_COLOR" ? "1" : "";
        public DateTime Now() => new(2026, 7, 13, 9, 8, 7, DateTimeKind.Utc);
        public TimeSpan SinceStart() => TimeSpan.Zero;
    }

    private sealed class TestingHooks : ICommandLineTesting, global::Ts.Execute.WatchManagement.ICommandLineTestingWithWatchBackend
    {
        internal readonly ManualWatcher ManualWatcher = new();
        internal readonly List<string> Compilations = new();
        internal readonly List<int> CompilationDiagnosticCounts = new();
        internal readonly List<string> WatchStatuses = new();
        internal readonly List<string> BuildStatuses = new();
        internal int Programs;

        public global::Ts.FsWatch.IWatcher Watcher() => ManualWatcher;

        // Go CommandLineTestingWithWatchBackend — the 1:1 watcher/orchestrator obtain their
        // fswatch backend through this seam in test mode.
        public global::Ts.Execute.WatchManagement.WatchBackend WatchBackend() =>
            new global::Ts.Execute.WatchManagement.FSWatchBackend { Inner = ManualWatcher };

        public void OnProgram(global::Ts.Execute.Incremental.Program? program) => Programs++;

        public void OnCompilation(global::Ts.TsOptions.ParsedCommandLine config)
        {
            string configName = config.ConfigName();
            Compilations.Add(configName == "" ? (config.FileNames() ?? Array.Empty<string>())[0] : configName);
        }

        public void OnCompilationComplete(global::Ts.TsOptions.ParsedCommandLine config, int diagnosticCount)
        {
            _ = config;
            CompilationDiagnosticCounts.Add(diagnosticCount);
        }

        public void OnWatchStatus(string message) => WatchStatuses.Add(message);
        public void OnBuildStatus(string message) => BuildStatuses.Add(message);
    }

    private sealed class ManualWatcher : global::Ts.FsWatch.IWatcher
    {
        private sealed class Registration
        {
            internal string Path = "";
            internal bool IsFile;
            internal bool Recursive;
            internal global::Ts.FsWatch.WatchCallback Callback = null!;
            internal bool Active = true;
        }

        private sealed class Handle : global::Ts.FsWatch.IWatch
        {
            private readonly Registration registration;

            internal Handle(Registration registration)
            {
                this.registration = registration;
            }

            public void Close() => registration.Active = false;
            public void Dispose() => Close();
        }

        private readonly List<Registration> registrations = new();

        public string Name() => "manual";
        public bool Available() => true;
        public bool HasFastRecursiveBackend() => true;

        public global::Ts.FsWatch.IWatch WatchDirectory(
            string dir,
            global::Ts.FsWatch.WatchCallback fn,
            params global::Ts.FsWatch.IWatchOption[] opts)
        {
            var options = new global::Ts.FsWatch.WatchOptions();
            foreach (var option in opts)
            {
                option.ApplyWatchOption(options);
            }
            return Add(dir, isFile: false, options.Recursive, fn);
        }

        public IReadOnlyList<global::Ts.FsWatch.IWatch> WatchDirectories(
            IReadOnlyList<global::Ts.FsWatch.WatchDirectoryRequest> requests)
        {
            var result = new global::Ts.FsWatch.IWatch[requests.Count];
            for (int i = 0; i < requests.Count; i++)
            {
                var request = requests[i];
                var options = new global::Ts.FsWatch.IWatchOption[request.Options?.Count ?? 0];
                for (int j = 0; j < options.Length; j++)
                {
                    options[j] = request.Options![j];
                }
                result[i] = WatchDirectory(request.Dir, request.Callback, options);
            }
            return result;
        }

        public global::Ts.FsWatch.IWatch WatchFile(string path, global::Ts.FsWatch.WatchCallback fn)
        {
            return Add(path, isFile: true, recursive: false, fn);
        }

        internal void Trigger(string path)
        {
            var callbacks = new List<global::Ts.FsWatch.WatchCallback>();
            foreach (var registration in registrations)
            {
                if (!registration.Active)
                {
                    continue;
                }
                bool matches = registration.IsFile
                    ? string.Equals(registration.Path, path, StringComparison.Ordinal)
                    : IsUnderDirectory(path, registration.Path, registration.Recursive);
                if (matches)
                {
                    callbacks.Add(registration.Callback);
                }
            }

            var events = new global::Ts.FsWatch.Event[]
            {
                new(global::Ts.FsWatch.EventKind.EventUpdate, path),
            };
            foreach (var callback in callbacks)
            {
                callback(events, null);
            }
        }

        internal bool HasFileWatch(string path)
        {
            foreach (var registration in registrations)
            {
                if (registration.Active && registration.IsFile &&
                    string.Equals(registration.Path, path, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        internal bool HasDirectoryWatch(string path)
        {
            foreach (var registration in registrations)
            {
                if (registration.Active && !registration.IsFile &&
                    string.Equals(registration.Path, path, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private global::Ts.FsWatch.IWatch Add(
            string path,
            bool isFile,
            bool recursive,
            global::Ts.FsWatch.WatchCallback callback)
        {
            var registration = new Registration
            {
                Path = path,
                IsFile = isFile,
                Recursive = recursive,
                Callback = callback,
            };
            registrations.Add(registration);
            return new Handle(registration);
        }

        private static bool IsUnderDirectory(string path, string directory, bool recursive)
        {
            string prefix = directory.EndsWith("/", StringComparison.Ordinal) ? directory : directory + "/";
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }
            return recursive || path.IndexOf('/', prefix.Length) < 0;
        }
    }

    [Theory]
    [InlineData("-w")]
    [InlineData("--watch")]
    public void WatchAliases_ParseAsWatch(string option)
    {
        var fs = global::Ts.Vfs.VfsTest.VfsTestUtil.FromMap(
            new Dictionary<string, string>(),
            useCaseSensitiveFileNames: true);
        var parsed = global::Ts.TsOptions.CommandLineParser.ParseCommandLine(
            [option, "--ignoreConfig", "/src/only.ts"],
            new FakeSystem(fs, "/src"));

        Assert.Equal(global::Ts.Core.Tristate.True, parsed.CompilerOptions()!.Watch);
        Assert.Empty(parsed.Errors ?? Array.Empty<global::Ts.Ast.Diagnostic>());
    }

    [Theory]
    [InlineData("-b")]
    [InlineData("--build")]
    public void BuildAliases_ParseAsBuild(string option)
    {
        var fs = global::Ts.Vfs.VfsTest.VfsTestUtil.FromMap(
            new Dictionary<string, string>(),
            useCaseSensitiveFileNames: true);
        var parsed = global::Ts.TsOptions.CommandLineParser.ParseBuildCommandLine(
            [option, "project"],
            new FakeSystem(fs, "/src"));

        Assert.Equal(new[] { "project" }, parsed.Projects);
        Assert.Empty(parsed.Errors ?? Array.Empty<global::Ts.Ast.Diagnostic>());
    }

    [Fact]
    public void Watch_RecompilesAfterManualFileEvent_AndReportsExactGoStatuses()
    {
        // Project lives at tsgo's canonical test depth — CanWatchDirectory rejects paths at
        // /<one>/<two> depth (watchbackend.go perceived-OS-root heuristics), exactly like Go.
        var fs = VfsTestUtil.FromMap(
            new Dictionary<string, string>
            {
                ["/user/username/projects/src/tsconfig.json"] = "{\"compilerOptions\":{\"noLib\":true,\"noEmit\":true},\"include\":[\"*.ts\"]}",
                ["/user/username/projects/src/only.ts"] = MinimalGlobals + "const value = 1;\n",
            },
            useCaseSensitiveFileNames: true);
        var sys = new FakeSystem(fs, "/user/username/projects/src");
        var testing = new TestingHooks();

        var result = Tsc.CommandLine(
            sys,
            ["--watch", "--project", "/user/username/projects/src/tsconfig.json"],
            testing);

        Assert.Equal(ExitStatus.Success, result.Status);
        Assert.NotNull(result.Watcher);
        // The 1:1 watcher reports each build via the Go OnProgram hook and registers directory
        // watches only (Go watcher.go computeDesiredWatches — per-file watches do not exist).
        Assert.Equal(1, testing.Programs);
        Assert.True(testing.ManualWatcher.HasDirectoryWatch("/user/username/projects/src"));
        fs.WriteFile("/user/username/projects/src/only.ts", MinimalGlobals + "const value = 2;\n");
        testing.ManualWatcher.Trigger("/user/username/projects/src/only.ts");
        result.Watcher!.DoCycle();

        Assert.Equal(2, testing.Programs);
        Assert.Equal(
            new[]
            {
                "Starting compilation in watch mode...",
                "Found 0 errors. Watching for file changes.",
                "File change detected. Starting incremental compilation...",
                "Found 0 errors. Watching for file changes.",
            },
            testing.WatchStatuses);
        Assert.Contains("Starting compilation in watch mode...", sys.Output, StringComparison.Ordinal);
        Assert.Contains("File change detected. Starting incremental compilation...", sys.Output, StringComparison.Ordinal);
        Assert.Contains("Found 0 errors. Watching for file changes.", sys.Output, StringComparison.Ordinal);
        result.Watcher.Dispose();
    }

    [Fact]
    public void Build_TwoProjectReferenceChain_CompilesDependencyFirst()
    {
        var fs = VfsTestUtil.FromMap(
            new Dictionary<string, string>
            {
                ["/src/A/tsconfig.json"] = "{\"compilerOptions\":{\"composite\":true,\"noLib\":true},\"files\":[\"globals.d.ts\",\"index.ts\"]}",
                ["/src/A/globals.d.ts"] = MinimalGlobals,
                ["/src/A/index.ts"] = "const a = 1;\n",
                ["/src/B/tsconfig.json"] = "{\"compilerOptions\":{\"composite\":true,\"noLib\":true},\"files\":[\"globals.d.ts\",\"index.ts\"],\"references\":[{\"path\":\"../A\"}]}",
                ["/src/B/globals.d.ts"] = MinimalGlobals,
                ["/src/B/index.ts"] = "const b = 2;\n",
            },
            useCaseSensitiveFileNames: true);
        var sys = new FakeSystem(fs, "/src");
        var testing = new TestingHooks();

        var result = Tsc.CommandLine(sys, ["--build", "--verbose", "B"], testing);

        Assert.Equal(ExitStatus.Success, result.Status);
        // The 1:1 build orchestrator reports per-project progress through the build-status hook
        // (Go CommandLineTesting.OnBuildStatus); OnCompilation is a tsc-compilation-path hook.
        // Full --build --verbose status sequence: project list, per-project up-to-date probe
        // result, then the build itself (matches tsgo's builder status stream).
        Assert.Equal(
            new[]
            {
                "Projects in this build: \r\n    * A/tsconfig.json\r\n    * B/tsconfig.json",
                "Project 'A/tsconfig.json' is out of date because output file 'A/tsconfig.tsbuildinfo' does not exist",
                "Building project 'A/tsconfig.json'...",
                "Project 'B/tsconfig.json' is out of date because output file 'B/tsconfig.tsbuildinfo' does not exist",
                "Building project 'B/tsconfig.json'...",
            },
            testing.BuildStatuses);
    }

    private const string MinimalGlobals =
        "interface Array<T> {}\n" +
        "interface Boolean {}\n" +
        "interface CallableFunction {}\n" +
        "interface Function {}\n" +
        "interface IArguments {}\n" +
        "interface NewableFunction {}\n" +
        "interface Number {}\n" +
        "interface Object {}\n" +
        "interface RegExp {}\n" +
        "interface String {}\n";
}
