// Round-trip byte-equality tests for the .tsbuildinfo data model (BuildInfo.cs) against
// real tsgo baseline artifacts (fixtures extracted from testdata/baselines/reference/tsbuild).
// The .tsbuildinfo phase gate requires Marshal output byte-identical to tsgo's json.Marshal.

using System.IO;
using Ts.Execute.Incremental;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class BuildInfoTests
{
    private static string ReadFixture(string name)
    {
        var path = Path.Combine(RepoPaths.RootPath, "fixtures", "incremental", name);
        return File.ReadAllText(path).TrimEnd('\n');
    }

    private static void AssertRoundTrip(string original)
    {
        var buildInfo = BuildInfo.Unmarshal(original);
        Assert.NotNull(buildInfo);
        var marshaled = buildInfo!.Marshal();
        Assert.Equal(original, marshaled);
    }

    // tsbuild/demo baseline: root ranges ([[2,3],5]), errors flag, lib-name fileNames,
    // fileInfos with affectsGlobalScope + signature compression.
    [Fact]
    public void RoundTrip_DemoAnimals() => AssertRoundTrip(ReadFixture("demo-animals.tsbuildinfo.json"));

    // tsbuild/noEmit baseline: bare-string fileInfo compression (CommonJS, no global scope),
    // semanticDiagnosticsPerFile with messageArgs + relatedInformation (file id refs),
    // affectedFilesPendingEmit mixed shapes ([2,[4],3,[5]]), emitSignatures, options object.
    [Fact]
    public void RoundTrip_NoEmitChangesComposite() => AssertRoundTrip(ReadFixture("noemit-changes-composite.tsbuildinfo.json"));

    // Malformed input must yield null (Go json.Unmarshal err path), not throw.
    [Fact]
    public void Unmarshal_MalformedReturnsNull()
    {
        Assert.Null(BuildInfo.Unmarshal("not json"));
        Assert.Null(BuildInfo.Unmarshal("[1,2,3]"));
    }

    // plan/01 §xxh3 保真分级 pinned vector: xxh3_128("export const a = 10;") lowercase hex.
    [Fact]
    public void ComputeHash_PinnedVector()
    {
        Assert.Equal("3a1e9965f8602302ee6ae53756eac8be", IncrementalHash.ComputeHash("export const a = 10;", false));
        Assert.Equal("3a1e9965f8602302ee6ae53756eac8be-export const a = 10;", IncrementalHash.ComputeHash("export const a = 10;", true));
    }
}
