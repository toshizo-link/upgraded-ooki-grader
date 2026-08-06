using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace OokiGrader.Host.Api;

public sealed class ProtectedCursorCodec
{
    private const int CurrentVersion = 1;
    private const int MaximumCursorLength = 8_192;
    private const int MaximumFilterBindingLength = 8_192;
    private const int MaximumRouteLength = 1_024;
    private const string ProtectionPurpose = "OokiGrader.Cursor.v1";
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector;

    public ProtectedCursorCodec(IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _protector = dataProtectionProvider.CreateProtector(ProtectionPurpose);
    }

    public string Encode<TPosition>(
        string route,
        string filterBinding,
        TPosition position)
        where TPosition : notnull
    {
        ValidateBinding(route, filterBinding);
        ArgumentNullException.ThrowIfNull(position);

        var envelope = new CursorEnvelope<TPosition>(
            CurrentVersion,
            route,
            HashBinding(filterBinding),
            position);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            SerializerOptions);
        var protectedBytes = _protector.Protect(plaintext);
        return WebEncoders.Base64UrlEncode(protectedBytes);
    }

    public bool TryDecode<TPosition>(
        string? cursor,
        string route,
        string filterBinding,
        out TPosition position)
        where TPosition : notnull
    {
        position = default!;
        if (string.IsNullOrWhiteSpace(cursor)
            || cursor.Length > MaximumCursorLength
            || !IsValidBinding(route, filterBinding))
        {
            return false;
        }

        try
        {
            var protectedBytes = WebEncoders.Base64UrlDecode(cursor);
            var plaintext = _protector.Unprotect(protectedBytes);
            var envelope = JsonSerializer.Deserialize<CursorEnvelope<TPosition>>(
                plaintext,
                SerializerOptions);
            if (envelope is null
                || envelope.Version != CurrentVersion
                || !string.Equals(envelope.Route, route, StringComparison.Ordinal)
                || !BindingHashesMatch(
                    envelope.FilterBindingHash,
                    HashBinding(filterBinding))
                || envelope.Position is null)
            {
                return false;
            }

            position = envelope.Position;
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    public static string ComputeFilterBinding(
        IEnumerable<KeyValuePair<string, string?>> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var filter in filters
                     .OrderBy(item => item.Key, StringComparer.Ordinal)
                     .ThenBy(item => item.Value is null ? 0 : 1)
                     .ThenBy(
                         item => item.Value ?? string.Empty,
                         StringComparer.Ordinal))
        {
            AppendLengthPrefixed(hash, filter.Key);
            AppendLengthPrefixed(hash, filter.Value);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void ValidateBinding(string route, string filterBinding)
    {
        if (!IsValidBinding(route, filterBinding))
        {
            throw new ArgumentException(
                "A bounded route and filter binding are required.");
        }
    }

    private static bool IsValidBinding(string? route, string? filterBinding) =>
        !string.IsNullOrWhiteSpace(route)
        && route.Length <= MaximumRouteLength
        && !string.IsNullOrWhiteSpace(filterBinding)
        && filterBinding.Length <= MaximumFilterBindingLength;

    private static string HashBinding(string filterBinding) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(filterBinding)))
            .ToLowerInvariant();

    private static bool BindingHashesMatch(string? left, string right)
    {
        if (left is null
            || left.Length != right.Length
            || left.Length != SHA256.HashSizeInBytes * 2)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));
    }

    private static void AppendLengthPrefixed(
        IncrementalHash hash,
        string? value)
    {
        var bytes = value is null
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int) + 1];
        length[0] = value is null ? (byte)0 : (byte)1;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(
            length[1..],
            bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private sealed record CursorEnvelope<TPosition>(
        int Version,
        string Route,
        string FilterBindingHash,
        TPosition Position);
}
