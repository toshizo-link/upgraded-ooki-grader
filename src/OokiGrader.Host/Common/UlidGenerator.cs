using System.Security.Cryptography;

namespace OokiGrader.Host.Common;

public interface IUlidGenerator
{
    string NewId();
}

public sealed class UlidGenerator(TimeProvider timeProvider) : IUlidGenerator
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public string NewId()
    {
        Span<byte> bytes = stackalloc byte[16];
        var timestamp = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        bytes[0] = (byte)(timestamp >> 40);
        bytes[1] = (byte)(timestamp >> 32);
        bytes[2] = (byte)(timestamp >> 24);
        bytes[3] = (byte)(timestamp >> 16);
        bytes[4] = (byte)(timestamp >> 8);
        bytes[5] = (byte)timestamp;
        RandomNumberGenerator.Fill(bytes[6..]);

        Span<char> output = stackalloc char[26];
        output[0] = Alphabet[bytes[0] >> 5];
        output[1] = Alphabet[bytes[0] & 31];
        output[2] = Alphabet[bytes[1] >> 3];
        output[3] = Alphabet[((bytes[1] & 7) << 2) | (bytes[2] >> 6)];
        output[4] = Alphabet[(bytes[2] >> 1) & 31];
        output[5] = Alphabet[((bytes[2] & 1) << 4) | (bytes[3] >> 4)];
        output[6] = Alphabet[((bytes[3] & 15) << 1) | (bytes[4] >> 7)];
        output[7] = Alphabet[(bytes[4] >> 2) & 31];
        output[8] = Alphabet[((bytes[4] & 3) << 3) | (bytes[5] >> 5)];
        output[9] = Alphabet[bytes[5] & 31];
        output[10] = Alphabet[bytes[6] >> 3];
        output[11] = Alphabet[((bytes[6] & 7) << 2) | (bytes[7] >> 6)];
        output[12] = Alphabet[(bytes[7] >> 1) & 31];
        output[13] = Alphabet[((bytes[7] & 1) << 4) | (bytes[8] >> 4)];
        output[14] = Alphabet[((bytes[8] & 15) << 1) | (bytes[9] >> 7)];
        output[15] = Alphabet[(bytes[9] >> 2) & 31];
        output[16] = Alphabet[((bytes[9] & 3) << 3) | (bytes[10] >> 5)];
        output[17] = Alphabet[bytes[10] & 31];
        output[18] = Alphabet[bytes[11] >> 3];
        output[19] = Alphabet[((bytes[11] & 7) << 2) | (bytes[12] >> 6)];
        output[20] = Alphabet[(bytes[12] >> 1) & 31];
        output[21] = Alphabet[((bytes[12] & 1) << 4) | (bytes[13] >> 4)];
        output[22] = Alphabet[((bytes[13] & 15) << 1) | (bytes[14] >> 7)];
        output[23] = Alphabet[(bytes[14] >> 2) & 31];
        output[24] = Alphabet[((bytes[14] & 3) << 3) | (bytes[15] >> 5)];
        output[25] = Alphabet[bytes[15] & 31];

        return new string(output);
    }
}
