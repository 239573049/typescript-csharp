// Ported from tsgo internal/api/session_completion_test.go (whole file: both test functions).
//
// PORT-NOTES:
//  * Go calls the session-private handlers directly with struct literals (same package);
//    the C# handlers are internal, reached through InternalsVisibleTo("Ts.LanguageService.Tests")
//    — the same shape Go uses. A JSON round-trip would be unfaithful (DocumentIdentifier's
//    UnmarshalJSONFrom reads only `uri` in object form, proto.go:223-259).
//  * Go `defer session.Close()` / `defer projectSession.Close()` → try/finally.
//  * gotest.tools `assert.X` → xUnit Assert (argument order swapped for Equal).

using System.Collections.Generic;

using Ts.Api;
using Ts.Core;
using Ts.TestUtil.ProjectTestUtil;
using Xunit;

using ApiSession = Ts.Api.Session;
using ProjectSession = Ts.Project.Session;

namespace Ts.Api.Tests;

public sealed class SessionCompletionTests
{
    // session_completion_test.go:25-88 — TestCompletionSymbolTypeIsResolvable.
    //
    // TestCompletionSymbolTypeIsResolvable reproduces a crash where requesting the
    // type of a completion-provided symbol panicked with a nil pointer dereference.
    //
    // Completion ran on an ephemeral query checker (default lifetime), so members of
    // a generic type such as `string[]` (= Array<string>) were returned as
    // *instantiated* symbols whose per-checker instantiation links live only on that
    // query checker. GetTypeOfSymbol runs on the persistent API checker — a
    // different instance — where those links are absent, so getTypeOfInstantiatedSymbol
    // dereferenced a nil target and brought down the connection.
    //
    // The fix pins symbol-producing completion to the API checker, so the returned
    // handles resolve on the same checker the client re-queries.
    [Fact]
    public void CompletionSymbolTypeIsResolvable()
    {
        const string fileName = "/home/projects/p/src/index.ts";
        // The caret sits right after `people.`, requesting members of `string[]`.
        const string content = "declare const people: string[];\npeople.";

        var files = new Dictionary<string, object?>
        {
            ["/home/projects/p/tsconfig.json"] = """{ "compilerOptions": { "strict": true } }""",
            [fileName] = content,
        };
        (ProjectSession projectSession, SessionUtils _) = SessionUtils.Setup(files);
        try
        {
            var session = ApiSession.NewSession(projectSession, null);
            try
            {
                RequestContext ctx = RequestContext.Background;

                UpdateSnapshotResponse snapshotResp = session.HandleUpdateSnapshot(ctx, new UpdateSnapshotParams
                {
                    OpenFiles = [new DocumentIdentifier { FileName = fileName }],
                });

                ProjectResponse? proj = session.HandleGetDefaultProjectForFile(ctx, new GetDefaultProjectForFileParams
                {
                    Snapshot = snapshotResp.Snapshot,
                    File = new DocumentIdentifier { FileName = fileName },
                });
                Assert.True(proj != null, "file should resolve to a default project");

                // content is pure ASCII, so the UTF-16 caret offset equals the byte length.
                CompletionInfoResponse? completions = session.HandleGetCompletionsAtPosition(ctx, new GetCompletionsAtPositionParams
                {
                    Snapshot = snapshotResp.Snapshot,
                    Project = proj!.Id,
                    File = new DocumentIdentifier { FileName = fileName },
                    Position = (uint)content.Length,
                    IncludeSymbol = true,
                });
                Assert.True(completions != null, "expected a completion list for array members");

                // Resolving the type of every completion symbol must not panic, and known
                // members like `push` must produce a concrete type.
                bool sawSymbol = false;
                bool sawPush = false;
                foreach (CompletionEntryResponse entry in completions!.Entries)
                {
                    if (entry.Symbol is null)
                    {
                        continue;
                    }
                    sawSymbol = true;
                    TypeResponse? typeResp = session.HandleGetTypeOfSymbol(ctx, new GetTypeOfSymbolParams
                    {
                        Snapshot = snapshotResp.Snapshot,
                        Project = proj.Id,
                        Symbol = entry.Symbol.Id,
                    });
                    // Go message: "type of completion symbol %q should resolve"
                    Assert.True(typeResp != null, $"type of completion symbol \"{entry.Name}\" should resolve");
                    if (entry.Name == "push")
                    {
                        sawPush = true;
                    }
                }
                Assert.True(sawSymbol, "completion entries should include resolvable symbols");
                Assert.True(sawPush, "array member completions should include `push`");
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

    // session_completion_test.go:90-141 — TestCompletionOnInferredProject.
    //
    // TestCompletionOnInferredProject reproduces a crash where requesting completions
    // for a loose file — one not part of any tsconfig.json, so it resolves to an
    // inferred project — panicked with "ConfigFilePath called on non-configured
    // project".
    //
    // setupLanguageService called Project.ConfigFilePath(), which is only valid for
    // configured projects and panics for inferred ones. The fix uses Project.ID(),
    // which returns the project's path for both configured and inferred projects without panicking.
    [Fact]
    public void CompletionOnInferredProject()
    {
        // No tsconfig.json anywhere, so this file belongs to an inferred project.
        const string fileName = "/home/projects/p/src/index.ts";
        const string content = "declare const people: string[];\npeople.";

        var files = new Dictionary<string, object?>
        {
            [fileName] = content,
        };
        (ProjectSession projectSession, SessionUtils _) = SessionUtils.Setup(files);
        try
        {
            var session = ApiSession.NewSession(projectSession, null);
            try
            {
                RequestContext ctx = RequestContext.Background;

                UpdateSnapshotResponse snapshotResp = session.HandleUpdateSnapshot(ctx, new UpdateSnapshotParams
                {
                    OpenFiles = [new DocumentIdentifier { FileName = fileName }],
                });

                ProjectResponse? proj = session.HandleGetDefaultProjectForFile(ctx, new GetDefaultProjectForFileParams
                {
                    Snapshot = snapshotResp.Snapshot,
                    File = new DocumentIdentifier { FileName = fileName },
                });
                Assert.True(proj != null, "file should resolve to an inferred default project");

                // This request previously panicked in setupLanguageService.
                // content is pure ASCII, so the UTF-16 caret offset equals the byte length.
                CompletionInfoResponse? completions = session.HandleGetCompletionsAtPosition(ctx, new GetCompletionsAtPositionParams
                {
                    Snapshot = snapshotResp.Snapshot,
                    Project = proj!.Id,
                    File = new DocumentIdentifier { FileName = fileName },
                    Position = (uint)content.Length,
                });
                Assert.True(completions != null, "expected a completion list for array members");
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
