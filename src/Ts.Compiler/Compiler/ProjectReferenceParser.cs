// Ported from tsgo internal/compiler/projectreferenceparser.go.
//
// PORT-NOTE (phase 4/5): the parser walks resolved project-reference configs, parsing each and
// populating the projectReferenceFileMapper. It is only constructed when the config actually
// declares project references (fileLoader.AddProjectReferenceTasks returns early otherwise), and
// its deep path calls ParsedCommandLine.ParseInputOutputNames / ResolvedProjectReferencePaths /
// SourceToProjectReference — surfaces that are themselves stubbed in the TsOptions port. The whole
// with-references path is deferred; parse() throws until it lands. The no-reference pipeline (the
// phase 4/5 milestone) never reaches this file.

using System;
using System.Collections.Generic;
using Ts.Core;

namespace Ts.Compiler;

// projectreferenceparser.go:13-17
internal sealed class ProjectReferenceParseTask
{
    internal string ConfigName = "";
    internal global::Ts.TsOptions.ParsedCommandLine? Resolved;
    internal List<ProjectReferenceParseTask>? SubTasks;
}

// projectreferenceparser.go:34-40
internal static class ProjectReferenceParseTasks
{
    internal static List<ProjectReferenceParseTask> Create(string[] projectReferences)
    {
        var result = new List<ProjectReferenceParseTask>(projectReferences.Length);
        foreach (var configName in projectReferences)
        {
            result.Add(new ProjectReferenceParseTask { ConfigName = configName });
        }
        return result;
    }
}

// projectreferenceparser.go:42-46
internal sealed class ProjectReferenceParser
{
    internal FileLoader Loader = null!;
    internal IWorkGroup Wg = null!;

    // projectreferenceparser.go:48-53
    internal void Parse(List<ProjectReferenceParseTask> tasks)
    {
        _ = tasks;
        throw new NotImplementedException("project-reference parsing is deferred (phase 4/5)");
    }
}
