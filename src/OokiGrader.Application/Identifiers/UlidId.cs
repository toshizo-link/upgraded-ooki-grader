using System.Numerics;
using System.Security.Cryptography;

namespace OokiGrader.Application.Identifiers;

public static class UlidId
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string New(DateTimeOffset? timestamp = null)
    {
        Span<byte> bytes = stackalloc byte[16];
        var milliseconds = (timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();

        if (milliseconds is < 0 or > 281_474_976_710_655)
        {
            throw new ArgumentOutOfRangeException(nameof(timestamp));
        }

        bytes[0] = (byte)(milliseconds >> 40);
        bytes[1] = (byte)(milliseconds >> 32);
        bytes[2] = (byte)(milliseconds >> 24);
        bytes[3] = (byte)(milliseconds >> 16);
        bytes[4] = (byte)(milliseconds >> 8);
        bytes[5] = (byte)milliseconds;
        RandomNumberGenerator.Fill(bytes[6..]);

        var value = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        Span<char> encoded = stackalloc char[26];

        for (var index = encoded.Length - 1; index >= 0; index--)
        {
            encoded[index] = Alphabet[(int)(value & 31)];
            value >>= 5;
        }

        return new string(encoded);
    }

    public static bool IsCanonical(string? value)
    {
        if (value is null || value.Length != 26 || value[0] > '7')
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!Alphabet.Contains(character, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
