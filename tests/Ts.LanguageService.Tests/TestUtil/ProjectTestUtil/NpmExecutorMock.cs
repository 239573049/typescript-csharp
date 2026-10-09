// Ported from tsgo internal/testutil/projecttestutil/npmexecutormock_generated.go (whole file).
//
// Go's moq generates a mock of ata.NpmExecutor with per-method Func fields and call
// recording; C# has no moq, so the equivalent mock is hand-written here with the same
// surface: NpmInstallFunc plus NpmInstallCalls(). Call recording is lock-guarded like the
// generated Go mock's RWMutex (ATA installs run on background threads).

using System;
using System.Collections.Generic;
using System.Threading;

namespace Ts.TestUtil.ProjectTestUtil;

// NpmExecutorMock is a mock implementation of Ts.Project.Ata.NpmExecutor.
public sealed class NpmExecutorMock : global::Ts.Project.Ata.NpmExecutor
{
    // NpmInstallCall holds details about a call to the NpmInstall method.
    public sealed record NpmInstallCall(string Cwd, string[] Args);

    // NpmInstallFunc mocks the NpmInstall method.
    public Func<string, string[], (byte[] Output, Exception? Error)>? NpmInstallFunc;

    // calls tracks calls to the methods.
    private readonly List<NpmInstallCall> callsNpmInstall = [];
    private readonly object lockNpmInstall = new();

    // NpmInstall calls NpmInstallFunc.
    public (byte[] Output, Exception? Error) NpmInstall(string cwd, string[] args)
    {
        var callInfo = new NpmInstallCall(cwd, args);
        lock (lockNpmInstall)
        {
            callsNpmInstall.Add(callInfo);
        }
        if (NpmInstallFunc == null)
        {
            return ([], null);
        }
        return NpmInstallFunc(cwd, args);
    }

    // NpmInstallCalls gets all the calls that were made to NpmInstall.
    // Check the length with:
    //
    //     mockedNpmExecutor.NpmInstallCalls().Count
    public IReadOnlyList<NpmInstallCall> NpmInstallCalls()
    {
        lock (lockNpmInstall)
        {
            return callsNpmInstall.ToArray();
        }
    }
}
