// Ported from tsgo internal/fourslash/fourslash.go (runner foundation subset).
//
// Ts.TestUtil intentionally has no product dependency on Ts.LanguageService. The fourslash
// test project links this source file with TS_FOURSLASH_RUNNER defined so that the runner can
// use both the shared parsed model and the language-service assembly without changing that
// dependency boundary.

#if TS_FOURSLASH_RUNNER

using System;
using System.Collections.Generic;
using System.Linq;
using Ts.Core;
using Ts.Ls;
using Ts.Vfs.VfsTest;

namespace Ts.TestUtil.Fourslash;

public sealed class FourslashSession
{
    private const string RootDirectory = "/";

    private readonly FourslashProject model;
    private readonly LanguageService languageService;
    private string activeFileName;
    private int currentCaretPosition;

    public FourslashSession(FourslashProject model)
    {
        this.model = model ?? throw new ArgumentNullException(nameof(model));
        if (model.Files.Count == 0)
        {
            throw new ArgumentException("A fourslash session requires at least one file.", nameof(model));
        }

        global::Ts.Compiler.Program program = CreateProgram(model);
        languageService = LanguageService.NewLanguageService(program);
        activeFileName = model.Files[0].VirtualFileName;
    }

    public void GoToMarker(string markerName)
    {
        ArgumentNullException.ThrowIfNull(markerName);

        // fourslash.go:GoToMarker/goToMarker: resolving a marker also changes the active file,
        // moves the caret to its raw position, and clears any selection state. This foundation
        // session has no selection yet, so only the first two pieces have stored state.
        FourslashMarkerPosition marker = model.GetMarker(markerName);
        activeFileName = marker.FileName;
        currentCaretPosition = marker.Position;
    }

    public void VerifyQuickInfoIs(string expected)
    {
        ArgumentNullException.ThrowIfNull(expected);

        QuickInfo? quickInfo = languageService.GetQuickInfoAtPosition(
            activeFileName,
            currentCaretPosition);
        if (quickInfo is null)
        {
            throw new NotImplementedException("LS: GetQuickInfoAtPosition not landed");
        }

        if (!string.Equals(quickInfo.DisplayString, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Quick info mismatch. Expected '" + expected + "', got '" + quickInfo.DisplayString + "'.");
        }
    }

    public void VerifyQuickInfoAt(string markerName, string expected)
    {
        GoToMarker(markerName);
        VerifyQuickInfoIs(expected);
    }

    public void VerifyGoToDefinition(string fromMarker, string toMarker)
    {
        GoToMarker(fromMarker);
        DefinitionLocation[] definitions = languageService.GetDefinitionAtPosition(
            activeFileName,
            currentCaretPosition);
        if (definitions.Length == 0)
        {
            throw new NotImplementedException("LS: GetSymbolAtLocation not landed");
        }

        FourslashMarkerPosition expected = model.GetMarker(toMarker);
        foreach (DefinitionLocation definition in definitions)
        {
            if (string.Equals(definition.FileName, expected.FileName, StringComparison.Ordinal) &&
                definition.TextSpan.Pos == expected.Position)
            {
                return;
            }
        }

        throw new InvalidOperationException(
            "Go-to-definition did not resolve marker '" + fromMarker + "' to marker '" + toMarker + "'.");
    }

    public void VerifyCompletionsContain(string[] expectedNames)
    {
        ArgumentNullException.ThrowIfNull(expectedNames);

        global::Ts.Ls.CompletionList? completions = languageService.GetCompletionsAtPosition(
            activeFileName,
            currentCaretPosition);
        if (completions is null)
        {
            throw new InvalidOperationException("Completion request produced no list.");
        }

        foreach (string expectedName in expectedNames)
        {
            bool found = false;
            foreach (global::Ts.Ls.CompletionItem entry in completions.Items ?? [])
            {
                if (string.Equals(entry.Item!.Label, expectedName, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                throw new InvalidOperationException(
                    "Completion list did not contain expected entry '" + expectedName + "'.");
            }
        }
    }

    private static global::Ts.Compiler.Program CreateProgram(FourslashProject model)
    {
        var fileContents = new Dictionary<string, string>(model.Files.Count, StringComparer.Ordinal);
        var rootFileNames = new string[model.Files.Count];
        for (int index = 0; index < model.Files.Count; index++)
        {
            FourslashFile file = model.Files[index];
            if (!fileContents.TryAdd(file.VirtualFileName, file.Content))
            {
                throw new InvalidOperationException("Duplicate fourslash file name: " + file.VirtualFileName);
            }
            rootFileNames[index] = file.VirtualFileName;
        }

        global::Ts.Vfs.IFileSystem fileSystem = VfsTestUtil.FromMap(
            fileContents,
            useCaseSensitiveFileNames: true);
        var commandLine = new List<string>(model.Options.Count * 2 + rootFileNames.Length);
        // map-iter: 已排序 — option order is deterministic and command-line semantics are order-sensitive.
        foreach (string optionName in model.Options.Keys.OrderBy(
            static name => name,
            StringComparer.Ordinal))
        {
            commandLine.Add("--" + optionName);
            string optionValue = model.Options[optionName];
            if (optionValue.Length != 0)
            {
                commandLine.Add(optionValue);
            }
        }
        commandLine.AddRange(rootFileNames);

        var configHost = new FourslashConfigHost(fileSystem);
        global::Ts.TsOptions.ParsedCommandLine config =
            global::Ts.TsOptions.CommandLineParser.ParseCommandLine(commandLine.ToArray(), configHost);
        CompilerOptions compilerOptions = config.CompilerOptions()!;

        // NewFourslash defaults for an inferred project. NoLib is the current C# harness's
        // hermetic equivalent of tsgo's bundled.WrapFS until bundled libraries are exposed here.
        compilerOptions.SkipDefaultLibCheck = Tristate.True;
        if (compilerOptions.Target == ScriptTarget.None)
        {
            compilerOptions.Target = ScriptTarget.LatestStandard;
        }
        if (compilerOptions.Jsx == JsxEmit.None)
        {
            compilerOptions.Jsx = JsxEmit.Preserve;
        }
        compilerOptions.NoLib = Tristate.True;
        compilerOptions.Types ??= [];

        global::Ts.Compiler.CompilerHost compilerHost =
            global::Ts.Compiler.CompilerHosts.NewCompilerHost(
                RootDirectory,
                fileSystem,
                "/lib",
                extendedConfigCache: null,
                trace: null);
        var program = new global::Ts.Compiler.Program(
            new global::Ts.Compiler.ProgramOptions
            {
                Host = compilerHost,
                Config = config,
                SingleThreaded = Tristate.True,
            });
        program.BindSourceFiles();
        return program;
    }

    private sealed class FourslashConfigHost(global::Ts.Vfs.IFileSystem fileSystem) :
        global::Ts.TsOptions.ParseConfigHost
    {
        public global::Ts.Vfs.IFileSystem FS() => fileSystem;

        public string GetCurrentDirectory() => RootDirectory;
    }
}

#endif
