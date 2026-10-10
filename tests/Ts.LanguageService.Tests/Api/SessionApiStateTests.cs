// Ported from tsgo internal/api/session_apistate_test.go (whole file: both test functions).
//
// PORT-NOTES:
//  * Go calls the session-private handlers directly with struct literals (same package).
//    The C# handlers are private too, reached through InternalsVisibleTo("Ts.LanguageService.Tests")
//    — the same shape Go uses. Going through HandleRequest/JSON would not be faithful: Go's
//    DocumentIdentifier.UnmarshalJSONFrom (proto.go:223-259) reads only `uri` in object form, so a
//    JSON round-trip deliberately drops `fileName`; the Go test never serializes.
//  * Go `session.openProjects.Len()` / `openFiles.Has(...)` → Ts.Collections.SetExtensions
//    Len/Has on the session's internal sets.
//  * Go `defer projectSession.Close()` → try/finally; `defer session.Close()` likewise.
//  * gotest.tools `assert.Equal(t, actual, expected)` → Assert.Equal(expected, actual).

using System;
using System.Collections.Generic;

using Ts.Api;
using Ts.Core;
using Ts.LsProto;
using Ts.TestUtil.ProjectTestUtil;
using Xunit;

using ApiSession = Ts.Api.Session;
using Path = Ts.TsPath.Path;
using ProjectSession = Ts.Project.Session;

namespace Ts.Api.Tests;

public sealed class SessionApiStateTests
{
    // session_apistate_test.go:18-195 — TestSessionTracksAndReleasesAPIRefs.
    //
    // TestSessionTracksAndReleasesAPIRefs verifies that an API session holds at most
    // one ref per opened project/file (opens are idempotent) and releases exactly
    // those refs when the session is closed, so it never leaks or over-releases refs
    // in the underlying (potentially shared) project session.

    // session_apistate_test.go:24-55 — "project opens are idempotent and released on close".
    [Fact]
    public void SessionTracksAndReleasesAPIRefs_ProjectOpensAreIdempotentAndReleasedOnClose()
    {
        const string configFileName = "/home/projects/p/tsconfig.json";
        var files = new Dictionary<string, object?>
        {
            [configFileName] = """{ "compilerOptions": { "strict": true } }""",
            ["/home/projects/p/src/index.ts"] = "export const x = 1;",
        };
        (ProjectSession projectSession, SessionUtils _) = SessionUtils.Setup(files);
        try
        {
            var session = ApiSession.NewSession(projectSession, null);

            session.HandleUpdateSnapshot(RequestContext.Background, new UpdateSnapshotParams
            {
                OpenProjects = [new DocumentIdentifier { FileName = configFileName }],
            });
            Assert.Equal(1, global::Ts.Collections.SetExtensions.Len(session.openProjects));

            // Opening the same project again must not take an additional ref.
            session.HandleUpdateSnapshot(RequestContext.Background, new UpdateSnapshotParams
            {
                OpenProjects = [new DocumentIdentifier { FileName = configFileName }],
            });
            Assert.Equal(1, global::Ts.Collections.SetExtensions.Len(session.openProjects));

            Assert.True(projectSession.Snapshot()!.ProjectCollection.ConfiguredProject(new Path(configFileName)) != null);

            // Closing the session releases the single API ref, so the project is no
            // longer kept loaded.
            session.Close();
            Assert.Equal(0, global::Ts.Collections.SetExtensions.Len(session.openProjects));
            Assert.True(projectSession.Snapshot()!.ProjectCollection.ConfiguredProject(new Path(configFileName)) == null);
        }
        finally
        {
            projectSession.Close();
        }
    }

    // session_apistate_test.go:57-90 — "explicit close releases the project ref".
    [Fact]
    public void SessionTracksAndReleasesAPIRefs_ExplicitCloseReleasesTheProjectRef()
    {
        const string configFileName = "/home/projects/p/tsconfig.json";
        var files = new Dictionary<string, object?>
        {
            [configFileName] = """{ "compilerOptions": { "strict": true } }""",
            ["/home/projects/p/src/index.ts"] = "export const x = 1;",
        };
        (ProjectSession projectSession, SessionUtils _) = SessionUtils.Setup(files);
        try
        {
            var session = ApiSession.NewSession(projectSession, null);
            try
            {
                session.HandleUpdateSnapshot(RequestContext.Background, new UpdateSnapshotParams
                {
                    OpenProjects = [new DocumentIdentifier { FileName = configFileName }],
                });
                Assert.Equal(1, global::Ts.Collections.SetExtensions.Len(session.openProjects));
                Assert.True(projectSession.Snapshot()!.ProjectCollection.ConfiguredProject(new Path(configFileName)) != null);

                // Closing a project we hold releases the ref and unloads the project.
                session.HandleUpdateSnapshot(RequestContext.Background, new UpdateSnapshotParams
                {
                    CloseProjects = [new DocumentIdentifier { FileName = configFileName }],
                });
                Assert.Equal(0, global::Ts.Collections.SetExtensions.Len(session.openProjects));
                Assert.True(projectSession.Snapshot()!.ProjectCollection.ConfiguredProject(new Path(configFileName)) == null);

                // Closing a project we don't hold is a no-op (never over-releases).
                session.HandleUpdateSnapshot(RequestContext.Background, new UpdateSnapshotParams
                {
                    CloseProjects = [new DocumentIdentifier { FileName = configFileName }],
                });
                Assert.Equal(0, global::Ts.Collections.SetExtensions.Len(session.openProjects));
            }
            finally
            {
                session.Close();
            }
        }
        finally
        {
            projectSession.Close();
        }
    }

    // session_apistate_test.go:92-142 — "file opens are idempotent and released on close".
    [Fact]
    public void SessionTracksAndReleasesAPIRefs_FileOpensAreIdempotentAndReleasedOnClose()
    {
        const string fileName = "/home/projects/p/src/index.ts";
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/p/tsconfig.json"] = """{ "compilerOptions": { "strict": true } }""",
            [fileName] = "export const x = 1;",
        };
        (ProjectSession projectSession, SessionUtils _) = SessionUtils.Setup(files);
        try
        {
            var session = ApiSession.NewSession(projectSession, null);

            session.HandleUpdateSnapshot(RequestContext.Background, new UpdateSnapshotParams
            {
                OpenFiles = [new DocumentIdentifier { FileName = fileName }],
            });
            Assert.Equal(1, global::Ts.Collections.SetExtensions.Len(session.openFiles));

            // Re-opening the same file must not take an additional ref.
            session.HandleUpdateSnapshot(RequestContext.Background, new UpdateSnapshotParams
            {
                OpenFiles = [new DocumentIdentifier { FileName = fileName }],
            });
            Assert.Equal(1, global::Ts.Collections.SetExtensions.Len(session.openFiles));

            // The file should resolve to the configured project via ancestor search.
            Assert.True(projectSession.Snapshot()!.ProjectCollection.ConfiguredProject(new Path("/home/projects/p/tsconfig.json")) != null);

            // Closing a file we don't hold is a no-op (never over-releases).
            session.HandleUpdateSnapshot(RequestContext.Background, new UpdateSnapshotParams
            {
                CloseFiles = [new DocumentIdentifier { FileName = "/home/projects/p/other.ts" }],
            });
            Assert.Equal(1, global::Ts.Collections.SetExtensions.Len(session.openFiles));

            // Explicitly closing the held file releases the ref.
            session.HandleUpdateSnapshot(RequestContext.Background, new UpdateSnapshotParams
            {
                CloseFiles = [new DocumentIdentifier { FileName = fileName }],
            });
            Assert.Equal(0, global::Ts.Collections.SetExtensions.Len(session.openFiles));

            // Closing the file also tears down the configured project that was
            // auto-loaded to serve it, instead of leaking it.
            // Go message: "configured project auto-loaded for the API-opened file should be unloaded after close"
            Assert.True(projectSession.Snapshot()!.ProjectCollection.ConfiguredProject(new Path("/home/projects/p/tsconfig.json")) == null);

            session.Close();
            Assert.Equal(0, global::Ts.Collections.SetExtensions.Len(session.openFiles));
        }
        finally
        {
            projectSession.Close();
        }
    }

    // session_apistate_test.go:144-194 — "relative file paths normalize consistently for open and close".
    [Fact]
    public void SessionTracksAndReleasesAPIRefs_RelativeFilePathsNormalizeConsistently()
    {
        // The project session's current directory is "/", so a relative path
        // resolves to the corresponding absolute path.
        var files = new Dictionary<string, object?>
        {
            ["/src/tsconfig.json"] = """{ "compilerOptions": { "strict": true } }""",
            ["/src/index.ts"] = "export const x = 1;",
        };
        (ProjectSession projectSession, SessionUtils _) = SessionUtils.Setup(files);
        try
        {
            var session = ApiSession.NewSession(projectSession, null);
            try
            {
                // Open via a relative path; it should be tracked under the absolute path
                // and resolve to the containing configured project.
                UpdateSnapshotResponse openResp = session.HandleUpdateSnapshot(RequestContext.Background, new UpdateSnapshotParams
                {
                    OpenFiles = [new DocumentIdentifier { FileName = "src/index.ts" }],
                });
                Assert.Equal(1, global::Ts.Collections.SetExtensions.Len(session.openFiles));
                Assert.True(global::Ts.Collections.SetExtensions.Has(session.openFiles, new Path("/src/index.ts")));
                Assert.True(projectSession.Snapshot()!.ProjectCollection.ConfiguredProject(new Path("/src/tsconfig.json")) != null);

                // getDefaultProjectForFile must also resolve a relative path to the same
                // configured project (it builds a URI from the identifier internally).
                ProjectResponse? proj = session.HandleGetDefaultProjectForFile(RequestContext.Background, new GetDefaultProjectForFileParams
                {
                    Snapshot = openResp.Snapshot,
                    File = new DocumentIdentifier { FileName = "src/index.ts" },
                });
                // Go message: "relative path should resolve to a default project"
                Assert.True(proj != null);
                Assert.Equal("/src/tsconfig.json", proj!.ConfigFileName);

                // Re-opening via the absolute path must match the relative open (no new ref).
                session.HandleUpdateSnapshot(RequestContext.Background, new UpdateSnapshotParams
                {
                    OpenFiles = [new DocumentIdentifier { FileName = "/src/index.ts" }],
                });
                Assert.Equal(1, global::Ts.Collections.SetExtensions.Len(session.openFiles));

                // Closing via a relative path must match the path stored when opening.
                session.HandleUpdateSnapshot(RequestContext.Background, new UpdateSnapshotParams
                {
                    CloseFiles = [new DocumentIdentifier { FileName = "src/index.ts" }],
                });
                Assert.Equal(0, global::Ts.Collections.SetExtensions.Len(session.openFiles));
                // Go message: "configured project should be unloaded after closing the relatively-pathed file"
                Assert.True(projectSession.Snapshot()!.ProjectCollection.ConfiguredProject(new Path("/src/tsconfig.json")) == null);
            }
            finally
            {
                session.Close();
            }
        }
        finally
        {
            projectSession.Close();
        }
    }

    // session_apistate_test.go:197-255 — TestUpdateSnapshotResponseSkipsUnloadedAncestorProject.
    //
    // TestUpdateSnapshotResponseSkipsUnloadedAncestorProject verifies that API
    // updateSnapshot does not report unloaded ancestor project placeholders. This
    // covers the case where opening a file loads its nearest configured project
    // while solution search discovers an ancestor tsconfig placeholder whose command
    // line is still nil.
    [Fact]
    public void UpdateSnapshotResponseSkipsUnloadedAncestorProject()
    {
        const string nestedConfigFileName = "/repo/packages/app/tsconfig.json";
        const string ancestorConfigFileName = "/repo/packages/tsconfig.json";
        const string fileName = "/repo/packages/app/src/index.ts";
        var files = new Dictionary<string, object?>
        {
            [ancestorConfigFileName] = """{ "files": [] }""",
            [nestedConfigFileName] = """
                {
                    "compilerOptions": { "composite": true },
                    "include": ["**/*"]
                }
                """,
            [fileName] = "let s: string = 1234;",
        };
        (ProjectSession projectSession, SessionUtils _) = SessionUtils.Setup(files);
        try
        {
            projectSession.DidOpenFile(RequestContext.Background, "file://" + fileName, 1, (string)files[fileName]!, LanguageKind.TypeScript.Value!);
            global::Ts.Project.Snapshot snapshot = projectSession.Snapshot()!;
            global::Ts.Project.Project? nestedProject = snapshot.ProjectCollection.ConfiguredProject(new Path(nestedConfigFileName));
            Assert.True(nestedProject != null);
            Assert.True(nestedProject!.CommandLine != null);
            global::Ts.Project.Project? ancestorProject = snapshot.ProjectCollection.ConfiguredProject(new Path(ancestorConfigFileName));
            Assert.True(ancestorProject != null);
            Assert.True(ancestorProject!.CommandLine == null);

            var session = ApiSession.NewSession(projectSession, null);
            try
            {
                UpdateSnapshotResponse response = session.HandleUpdateSnapshot(RequestContext.Background, new UpdateSnapshotParams
                {
                    OpenProjects = [new DocumentIdentifier { FileName = nestedConfigFileName }],
                });

                bool foundNestedProject = false;
                bool foundAncestorProject = false;
                foreach (ProjectResponse project in response.Projects)
                {
                    if (project.ConfigFileName == nestedConfigFileName)
                    {
                        foundNestedProject = true;
                        Assert.True(project.RootFiles != null);
                        Assert.True(project.CompilerOptions != null);
                    }
                    else if (project.ConfigFileName == ancestorConfigFileName)
                    {
                        foundAncestorProject = true;
                    }
                }
                Assert.True(foundNestedProject);
                Assert.False(foundAncestorProject);
            }
            finally
            {
                session.Close();
            }
        }
        finally
        {
            projectSession.Close();
        }
    }
}
