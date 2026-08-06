using OokiGrader.Application.Identifiers;

namespace OokiGrader.Application.Tests;

public sealed class UlidIdTests
{
    [Fact]
    public void NewProducesCanonicalSortableIdentifiers()
    {
        var earlier = UlidId.New(DateTimeOffset.FromUnixTimeMilliseconds(1_000));
        var later = UlidId.New(DateTimeOffset.FromUnixTimeMilliseconds(2_000));

        Assert.True(UlidId.IsCanonical(earlier));
        Assert.True(UlidId.IsCanonical(later));
        Assert.True(string.CompareOrdinal(earlier, later) < 0);
    }
}
