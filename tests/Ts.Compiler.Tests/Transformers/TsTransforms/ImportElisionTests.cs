using Xunit;

namespace Ts.Transformers.TsTransforms.Tests;

public sealed class TsTransformImportElisionTests
{
    [Fact(Skip = "Requires the checker-backed EmitResolver alias-reference graph; Checker/ is owned by the parallel NameLookup task")]
    public void UnusedValueImportIsElided()
    {
    }
}
