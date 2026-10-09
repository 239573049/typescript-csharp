// Ported from tsgo internal/project/api.go (whole file).

using APISnapshotRequest = Ts.Project.Snapshot.APISnapshotRequest;
using SnapshotChange = Ts.Project.Snapshot.SnapshotChange;

namespace Ts.Project;

// Go declares APIUpdate as a method on *Session (project/api.go:14); it is expressed
// as a partial-class member here so the file mapping api.go → Api.cs holds while the
// Api layer can call it as a plain instance method (the earlier extension-method form
// did not bind at call sites that never import Ts.Project).
public sealed partial class Session
{
    // APIUpdate creates a new snapshot incorporating the given file changes and the
    // supplied API open/close request. The apiRequest may open or close projects and
    // files; opens are tracked in the snapshot (ref-counted) so they persist across
    // future updates, and closes release a previously taken ref. Even an empty
    // apiRequest ensures all API-opened projects and files are kept up to date.
    // Returns a ref'd snapshot (which the caller must Deref when done) and any error
    // encountered while applying the request, e.g. failing to load a project to open.
    public Snapshot APIUpdate(
        global::Ts.Core.RequestContext ctx,
        FileChangeSummary apiFileChanges,
        APISnapshotRequest apiRequest)
    {
        lock (SnapshotUpdateLock)
        {
            CancelScheduledSnapshotUpdate();

            (FileChangeSummary fileChanges, var overlays, var ataChanges, _) = FlushChanges(ctx);
            FileChangeSummary.Merge(ref fileChanges, apiFileChanges);

            Snapshot newSnapshot = UpdateSnapshotRef(ctx, overlays, new SnapshotChange
            {
                ApiRequest = apiRequest,
                FileChanges = fileChanges,
                AtaChanges = ataChanges,
            });
            // Go: return newSnapshot, newSnapshot.apiError (api.go:28). The API wave's
            // registered deviation signals the error by throwing and lets no snapshot
            // escape, and Go's caller derefs before checking the error — so the error-path
            // Deref is owned here (project wave).
            if (newSnapshot.apiError is not null)
            {
                newSnapshot.Deref(this);
                throw newSnapshot.apiError;
            }
            return newSnapshot;
        }
    }
}
