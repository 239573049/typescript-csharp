// Ported from tsgo internal/compiler/projectreferencefilemapper.go.
//
// The mapper answers "does this file redirect to/from a project reference?" With no project
// references configured (configToProjectReference et al. stay null), every lookup returns the
// no-redirect answer — which is the whole surface the file-loader pipeline needs. The populated
// (with-references) path is driven by ProjectReferenceParser, which is deferred (see that file).
//
// §1.5 (namespace shadowing): sibling-namespace static classes are fully qualified with global::.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;

namespace Ts.Compiler;

// projectreferencefilemapper.go:14-26
internal sealed class ProjectReferenceFileMapper
{
    internal ProgramOptions Opts = null!;
    internal global::Ts.Module.ResolutionHost? Host;
    internal FileLoader? Loader; // Only present during populating the mapper and parsing, released after that

    internal Dictionary<global::Ts.TsPath.Path, global::Ts.TsOptions.ParsedCommandLine>? ConfigToProjectReference; // All the resolved references needed
    internal Dictionary<global::Ts.TsPath.Path, global::Ts.TsPath.Path[]>? ReferencesInConfigFile; // Map of config file to its references
    internal Dictionary<global::Ts.TsPath.Path, global::Ts.TsOptions.SourceOutputAndProjectReference>? SourceToProjectReference;
    internal Dictionary<global::Ts.TsPath.Path, global::Ts.TsOptions.SourceOutputAndProjectReference>? OutputDtsToProjectReference;

    // Store all the realpath from dts in node_modules to source file from project reference needed during parsing.
    internal readonly SyncMap<global::Ts.TsPath.Path, global::Ts.TsOptions.SourceOutputAndProjectReference?> RealpathDtsToSource = new();

    // projectreferencefilemapper.go:28-46
    internal string GetParseFileRedirect(HasFileName file)
    {
        if (Opts.CanUseProjectReferenceSource())
        {
            // Map to source file from project reference
            var source = GetProjectReferenceFromOutputDts(file.Path());
            source ??= GetSourceToDtsIfSymlink(file);
            if (source is not null)
            {
                return source.Source;
            }
        }
        else
        {
            // Map to dts file from project reference
            var output = GetProjectReferenceFromSource(file.Path());
            if (output is not null && output.OutputDts != "")
            {
                return output.OutputDts;
            }
        }
        return "";
    }

    // projectreferencefilemapper.go:48-62
    internal global::Ts.TsOptions.ParsedCommandLine[]? GetResolvedProjectReferences()
    {
        if (Opts.Config.ConfigFile is null)
        {
            return null;
        }
        if (ReferencesInConfigFile is null ||
            !ReferencesInConfigFile.TryGetValue(Opts.Config.ConfigFile.SourceFile.Path(), out var refs))
        {
            return null;
        }
        var result = new global::Ts.TsOptions.ParsedCommandLine[refs.Length];
        for (int i = 0; i < refs.Length; i++)
        {
            result[i] = ConfigToProjectReference!.GetValueOrDefault(refs[i])!;
        }
        return result;
    }

    // projectreferencefilemapper.go:64-66
    internal global::Ts.TsOptions.SourceOutputAndProjectReference? GetProjectReferenceFromSource(global::Ts.TsPath.Path path) =>
        SourceToProjectReference?.GetValueOrDefault(path);

    // projectreferencefilemapper.go:68-70
    internal global::Ts.TsOptions.SourceOutputAndProjectReference? GetProjectReferenceFromOutputDts(global::Ts.TsPath.Path path) =>
        OutputDtsToProjectReference?.GetValueOrDefault(path);

    // projectreferencefilemapper.go:72-74
    internal bool IsSourceFromProjectReference(global::Ts.TsPath.Path path) =>
        Opts.CanUseProjectReferenceSource() && GetProjectReferenceFromSource(path) is not null;

    // projectreferencefilemapper.go:76-79
    internal CompilerOptions GetCompilerOptionsForFile(HasFileName file)
    {
        var redirect = GetRedirectParsedCommandLineForResolution(file);
        return ModuleFormat.CompilerOptionsForRedirect(Opts.Config.CompilerOptions()!, redirect);
    }

    // projectreferencefilemapper.go:81-84
    internal global::Ts.TsOptions.ParsedCommandLine? GetRedirectParsedCommandLineForResolution(HasFileName file)
    {
        var (redirect, _) = GetRedirectForResolution(file);
        return redirect;
    }

    // projectreferencefilemapper.go:86-105
    internal (global::Ts.TsOptions.ParsedCommandLine? Redirect, string FileName) GetRedirectForResolution(HasFileName file)
    {
        var path = file.Path();
        // Check if outputdts of source file from project reference
        var output = GetProjectReferenceFromSource(path);
        if (output is not null)
        {
            return (output.Resolved, output.Source);
        }

        // Source file from project reference
        var resultFromDts = GetProjectReferenceFromOutputDts(path);
        if (resultFromDts is not null)
        {
            return (resultFromDts.Resolved, resultFromDts.Source);
        }

        var realpathDtsToSource = GetSourceToDtsIfSymlink(file);
        if (realpathDtsToSource is not null)
        {
            return (realpathDtsToSource.Resolved, realpathDtsToSource.Source);
        }
        return (null, file.FileName());
    }

    // projectreferencefilemapper.go:107-110
    internal bool TryGetResolvedReferenceFor(global::Ts.TsPath.Path path, out global::Ts.TsOptions.ParsedCommandLine? config)
    {
        if (ConfigToProjectReference is not null && ConfigToProjectReference.TryGetValue(path, out config))
        {
            return true;
        }
        config = null;
        return false;
    }

    // projectreferencefilemapper.go:158-187
    internal global::Ts.TsOptions.SourceOutputAndProjectReference? GetSourceToDtsIfSymlink(HasFileName file)
    {
        // If preserveSymlinks is true, module resolution won't jump the symlink but the resolved
        // real path may be the .d.ts from project reference. Currently we try the real path only if
        // the file is from node_modules to avoid running real path on all file paths.
        var path = file.Path();
        if (RealpathDtsToSource.TryLoad(path, out var cached))
        {
            return cached;
        }
        if (Loader is not null && Opts.Config.CompilerOptions()!.PreserveSymlinks == Tristate.True)
        {
            var fileName = file.FileName();
            if (!fileName.Contains("/node_modules/", StringComparison.Ordinal))
            {
                RealpathDtsToSource.Store(path, null);
            }
            else
            {
                var realDeclarationPath = Loader.ToPath(Host!.FS().Realpath(fileName));
                if (realDeclarationPath == path)
                {
                    RealpathDtsToSource.Store(path, null);
                }
                else
                {
                    var realpathDtsToSource = GetProjectReferenceFromOutputDts(realDeclarationPath);
                    if (realpathDtsToSource is not null)
                    {
                        RealpathDtsToSource.Store(path, realpathDtsToSource);
                        return realpathDtsToSource;
                    }
                    RealpathDtsToSource.Store(path, null);
                }
            }
        }
        return null;
    }

    // projectreferencefilemapper.go:112-156 — rangeResolvedProjectReference family drives project-reference
    // verification/traversal. With ConfigFile==null it returns false immediately (the no-reference path).
    // PORT-NOTE: the populated recursion (with references) is exercised only once ProjectReferenceParser is
    // fully ported; deferred with the rest of project references.
    internal bool RangeResolvedProjectReference(
        Func<global::Ts.TsPath.Path, global::Ts.TsOptions.ParsedCommandLine?, global::Ts.TsOptions.ParsedCommandLine, int, bool> f)
    {
        if (Opts.Config.ConfigFile is null)
        {
            return false;
        }
        throw new NotImplementedException("project-reference traversal is deferred (phase 4/5)");
    }
}
