// Sanity coverage for Ts.Vfs.WrapVfs. No Go counterpart file exists (tsgo's wrapvfs
// package ships without tests); these pin the replacement-vs-delegate dispatch.

using Ts.Vfs;
using Ts.Vfs.WrapVfs;
using Xunit;

namespace Ts.Vfs.Tests;

public class WrapVfsTests
{
    [Fact]
    public void ReplacementOverridesMethod()
    {
        var underlying = new FakeFs(new()
        {
            ["/a.ts"] = "original",
        });
        var wrapped = WrapVfsUtil.Wrap(underlying, new Replacements
        {
            FileExists = _ => true,
            ReadFile = (string path, out string contents) =>
            {
                contents = "replaced";
                return true;
            },
        });

        Assert.True(wrapped.FileExists("/missing.ts"));
        Assert.Empty(underlying.FileExistsCalls);

        Assert.True(wrapped.TryReadFile("/a.ts", out string contents));
        Assert.Equal("replaced", contents);
        Assert.Empty(underlying.ReadFileCalls);
    }

    [Fact]
    public void NullReplacementDelegatesToWrapped()
    {
        var underlying = new FakeFs(new()
        {
            ["/a.ts"] = "original",
        });
        var wrapped = WrapVfsUtil.Wrap(underlying, new Replacements());

        Assert.True(wrapped.FileExists("/a.ts"));
        Assert.Single(underlying.FileExistsCalls);

        Assert.True(wrapped.TryReadFile("/a.ts", out string contents));
        Assert.Equal("original", contents);
        Assert.Single(underlying.ReadFileCalls);

        Assert.True(wrapped.UseCaseSensitiveFileNames());
        Assert.Equal(1, underlying.UseCaseSensitiveFileNamesCalls);

        Assert.Equal("/a.ts", wrapped.Realpath("/a.ts"));
        Assert.Single(underlying.RealpathCalls);

        wrapped.WriteFile("/w.ts", "data");
        Assert.Equal(("/w.ts", "data"), Assert.Single(underlying.WriteFileCalls));
    }

    [Fact]
    public void PartialReplacementsMix()
    {
        var underlying = new FakeFs(new()
        {
            ["/a.ts"] = "original",
        });
        var wrapped = WrapVfsUtil.Wrap(underlying, new Replacements
        {
            Realpath = path => "/redirected" + path,
        });

        Assert.Equal("/redirected/a.ts", wrapped.Realpath("/a.ts"));
        Assert.Empty(underlying.RealpathCalls);

        // Non-replaced members still hit the wrapped FS.
        Assert.True(wrapped.FileExists("/a.ts"));
        Assert.Single(underlying.FileExistsCalls);
    }
}
