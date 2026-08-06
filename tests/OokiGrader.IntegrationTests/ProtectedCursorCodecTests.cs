using Microsoft.AspNetCore.DataProtection;
using OokiGrader.Host.Api;

namespace OokiGrader.IntegrationTests;

public sealed class ProtectedCursorCodecTests
{
    private const string StudentsRoute = "GET:/api/v1/students";

    [Fact]
    public void RoundTripsPositionForSameRouteAndFilters()
    {
        var codec = CreateCodec();
        var filterBinding = CreateFilterBinding(
            ("cohortId", "01J00000000000000000000000"),
            ("status", "active"));
        var expected = new TestPosition(
            new DateTimeOffset(2026, 7, 27, 10, 30, 0, TimeSpan.Zero),
            "01J00000000000000000000001");

        var cursor = codec.Encode(StudentsRoute, filterBinding, expected);
        var decoded = codec.TryDecode(
            cursor,
            StudentsRoute,
            filterBinding,
            out TestPosition actual);

        Assert.True(decoded);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void FilterBindingIsOrderIndependentAndDistinguishesNullFromEmpty()
    {
        var forward = CreateFilterBinding(
            ("name", "採点"),
            ("status", "active"));
        var reverse = CreateFilterBinding(
            ("status", "active"),
            ("name", "採点"));
        var nullValue = CreateFilterBinding(("status", null));
        var emptyValue = CreateFilterBinding(("status", string.Empty));

        Assert.Equal(forward, reverse);
        Assert.NotEqual(nullValue, emptyValue);
    }

    [Fact]
    public void RefusesCursorForDifferentRoute()
    {
        var codec = CreateCodec();
        var filterBinding = CreateFilterBinding(("status", "active"));
        var cursor = codec.Encode(
            StudentsRoute,
            filterBinding,
            new TestPosition(DateTimeOffset.UnixEpoch, "student-1"));

        var decoded = codec.TryDecode(
            cursor,
            "GET:/api/v1/classes",
            filterBinding,
            out TestPosition _);

        Assert.False(decoded);
    }

    [Fact]
    public void RefusesCursorForDifferentFilters()
    {
        var codec = CreateCodec();
        var activeFilters = CreateFilterBinding(("status", "active"));
        var cursor = codec.Encode(
            StudentsRoute,
            activeFilters,
            new TestPosition(DateTimeOffset.UnixEpoch, "student-1"));

        var decoded = codec.TryDecode(
            cursor,
            StudentsRoute,
            CreateFilterBinding(("status", "inactive")),
            out TestPosition _);

        Assert.False(decoded);
    }

    [Fact]
    public void RefusesTamperedCursor()
    {
        var codec = CreateCodec();
        var filterBinding = CreateFilterBinding(("status", "active"));
        var cursor = codec.Encode(
            StudentsRoute,
            filterBinding,
            new TestPosition(DateTimeOffset.UnixEpoch, "student-1"));
        var replacement = cursor[0] == 'A' ? 'B' : 'A';
        var tampered = $"{replacement}{cursor[1..]}";

        var decoded = codec.TryDecode(
            tampered,
            StudentsRoute,
            filterBinding,
            out TestPosition _);

        Assert.False(decoded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64!")]
    public void RefusesMalformedCursor(string cursor)
    {
        var codec = CreateCodec();

        var decoded = codec.TryDecode(
            cursor,
            StudentsRoute,
            CreateFilterBinding(("status", "active")),
            out TestPosition _);

        Assert.False(decoded);
    }

    private static ProtectedCursorCodec CreateCodec() =>
        new(new EphemeralDataProtectionProvider());

    private static string CreateFilterBinding(
        params (string Key, string? Value)[] filters) =>
        ProtectedCursorCodec.ComputeFilterBinding(
            filters.Select(filter =>
                new KeyValuePair<string, string?>(
                    filter.Key,
                    filter.Value)));

    private sealed record TestPosition(
        DateTimeOffset UpdatedAt,
        string Id);
}
