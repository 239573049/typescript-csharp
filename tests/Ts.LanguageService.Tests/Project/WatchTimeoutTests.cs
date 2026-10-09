// Ported from tsgo internal/project/watchtimeout_test.go (whole file).
//
// PORT-NOTEs:
//  * Go runs this test under `testing/synctest` (fake time): `synctest.Wait()` parks until
//    every goroutine blocks and `time.Sleep` advances the fake clock instantly. The BCL has
//    no synctest; the C# port runs the same scenario on real time. The two
//    Wait/sleep/Wait sequences each collapse into one `session.WaitForBackgroundTasks()`:
//    updateWatches runs on the session's background queue, so draining the queue is exactly
//    the "every goroutine finished the blocked WatchFiles attempt" state, and the extra
//    2s fake-time advance is unnecessary because the production 1s watchRequestTimeout
//    (a real CancelAfter here) has already elapsed by the time the blocked mock returns.
//  * Go's mock blocks with `<-ctx.Done(); return ctx.Err()`; the C# mock blocks on
//    `ctx.WaitHandle.WaitOne()` and returns an OperationCanceledException (05 §4:
//    error → exception; the production code only inspects error != nil).
//  * `firstBatchDone` (Go atomic.Bool) → int + Volatile/Interlocked.
//  * The Go `if !bundled.Embedded { t.Skip(...) }` guard is omitted: Bundled.Embedded is
//    `const true` in the C# build, so the branch is unreachable (and CS0162 fatal).
//  * `defer session.Close()` → try/finally.

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.TestUtil.ProjectTestUtil;
using Xunit;

using FileSystemWatcher = global::Ts.LsProto.FileSystemWatcher;
using DocumentUri = global::Ts.LsProto.DocumentUri;
using LanguageKind = global::Ts.LsProto.LanguageKind;
using Position = global::Ts.LsProto.Position;
using Range = global::Ts.LsProto.Range;
using TextDocumentContentChangePartial = global::Ts.LsProto.TextDocumentContentChangePartial;
using TextDocumentContentChangePartialOrWholeDocument = global::Ts.LsProto.TextDocumentContentChangePartialOrWholeDocument;
using WatcherID = global::Ts.Project.WatcherID;

namespace Ts.Project.Tests;

public sealed class WatchTimeoutTests
{
    // watchtimeout_test.go:18-151 — TestUpdateWatchTimeoutAndRollback, subtest "watch
    // retries on next snapshot update after timeout with same watcher identity".
    [Fact]
    public void TestUpdateWatchTimeoutAndRollback_WatchRetriesOnNextSnapshotUpdateAfterTimeoutWithSameWatcherIdentity()
    {
        var files = new Dictionary<string, object?>
        {
            ["/home/projects/TS/p1/tsconfig.json"] = @"{
				""compilerOptions"": { ""noLib"": true, ""strict"": true }
			}",
            ["/home/projects/TS/p1/src/index.ts"] = @"export const x = 1;",
        };

        (global::Ts.Project.SessionInit init, SessionUtils utils) = SessionUtils.GetSessionInitOptions(files, null, new TypingsInstallerOptions());

        // Track WatchFiles calls: record which watcher IDs were attempted
        // and which succeeded.
        var mu = new object();
        var attemptedIDs = new List<WatcherID>();
        var successfulIDs = new List<WatcherID>();
        int firstBatchDone = 0;
        utils.Client().WatchFilesFunc = (CancellationToken ctx, WatcherID id, FileSystemWatcher[] watchers) =>
        {
            lock (mu)
            {
                attemptedIDs.Add(id);
            }
            if (Volatile.Read(ref firstBatchDone) == 0)
            {
                // Block until the context times out to simulate a slow client.
                // (Go: <-ctx.Done(); return ctx.Err().)
                ctx.WaitHandle.WaitOne();
                return new OperationCanceledException(ctx);
            }
            // After the first batch, succeed immediately.
            lock (mu)
            {
                successfulIDs.Add(id);
            }
            return null;
        };

        global::Ts.Project.Session session = global::Ts.Project.Session.NewSession(init);
        try
        {
            DocumentUri uri = "file:///home/projects/TS/p1/src/index.ts";

            // Step 1: Open the file. This creates the project and triggers
            // updateWatches. All WatchFiles calls block and time out because
            // the client is slow, so the registry is rolled back and the
            // watchers are marked as pending.
            session.DidOpenFile(global::Ts.Core.RequestContext.Background, uri, 1, (string)files["/home/projects/TS/p1/src/index.ts"]!, LanguageKind.TypeScript.Value!);

            // Let the background goroutine block on WatchFiles, then advance
            // (fake) time past the 1s watchRequestTimeout and let it finish.
            // (Go: synctest.Wait(); time.Sleep(2 * time.Second); synctest.Wait().)
            session.WaitForBackgroundTasks();

            List<WatcherID> firstAttemptIDs;
            lock (mu)
            {
                firstAttemptIDs = new List<WatcherID>(attemptedIDs);
            }
            Assert.True(firstAttemptIDs.Count >= 1,
                $"expected at least one WatchFiles call during initial open, got {firstAttemptIDs.Count}");

            // No watcher IDs should have succeeded.
            lock (mu)
            {
                Assert.True(successfulIDs.Count == 0, "expected no successful watches after timeout");
            }

            // Step 2: Allow subsequent WatchFiles calls to succeed.
            Volatile.Write(ref firstBatchDone, 1);

            // Step 3: Make a single character change to the open file. This
            // doesn't change any watcher identities — the program files remain
            // the same, so the programFilesWatch ID is unchanged. The pending
            // tracking ensures updateWatches retries the failed registrations.
            session.DidChangeFile(global::Ts.Core.RequestContext.Background, uri, 2,
            [
                new TextDocumentContentChangePartialOrWholeDocument
                {
                    Partial = new TextDocumentContentChangePartial
                    {
                        Range = new Range
                        {
                            Start = new Position { Line = 0, Character = 18 },
                            End = new Position { Line = 0, Character = 19 },
                        },
                        Text = "2",
                    },
                },
            ]);

            // Step 4: Flush the pending change by requesting the language service.
            // This triggers getSnapshot → updateSnapshot → updateWatches.
            (_, System.Exception? err) = session.GetLanguageService(global::Ts.Core.RequestContext.Background, uri);
            Assert.Null(err);

            // Let the background task run updateWatches.
            // (Go: synctest.Wait(); time.Sleep(2 * time.Second); synctest.Wait().)
            session.WaitForBackgroundTasks();

            // Verify: WatchFiles was called again with the same watcher IDs,
            // and this time the calls succeeded.
            lock (mu)
            {
                var retryIDs = attemptedIDs.GetRange(firstAttemptIDs.Count, attemptedIDs.Count - firstAttemptIDs.Count);
                Assert.True(retryIDs.Count >= 1,
                    $"expected WatchFiles to be retried after character change, got {retryIDs.Count} new calls (total {attemptedIDs.Count}, first batch {firstAttemptIDs.Count})");

                // Verify that the retry used the same watcher IDs as the first attempt.
                var firstAttemptSet = new HashSet<WatcherID>(firstAttemptIDs);
                var retrySet = new HashSet<WatcherID>(retryIDs);
                // map-iter: 序无关 — Go iterates these maps in random order; the body only
                // asserts membership, which is order-independent.
                foreach (WatcherID id in retrySet)
                {
                    Assert.True(firstAttemptSet.Contains(id),
                        $"retry watcher ID {id} was not in the first attempt; first attempt IDs={string.Join(", ", firstAttemptIDs)} retry IDs={string.Join(", ", retryIDs)}");
                }

                // Verify at least one retried watcher succeeded.
                int successfulRetriedCount = 0;
                foreach (WatcherID id in successfulIDs)
                {
                    if (retrySet.Contains(id))
                    {
                        successfulRetriedCount++;
                    }
                }
                Assert.True(successfulRetriedCount >= 1,
                    $"expected at least one retried watcher to succeed, got {successfulRetriedCount} (successful IDs={string.Join(", ", successfulIDs)}, retry IDs={string.Join(", ", retryIDs)})");
            }
        }
        finally
        {
            session.Close();
        }
    }
}
