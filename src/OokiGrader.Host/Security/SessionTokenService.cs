using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace OokiGrader.Host.Security;

public interface ISessionTokenService
{
    SessionTokenPair Create();

    string Hash(string token);

    bool Verify(string token, string expectedHash);
}

public sealed record SessionTokenPair(
    string SessionToken,
    string SessionTokenHash,
    string CsrfToken,
    string CsrfTokenHash);

public sealed class SessionTokenService : ISessionTokenService
{
    public SessionTokenPair Create()
    {
        var sessionToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var csrfToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        return new SessionTokenPair(
            sessionToken,
            Hash(sessionToken),
            csrfToken,
            Hash(csrfToken));
    }

    public string Hash(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    }

    public bool Verify(string token, string expectedHash)
    {
        if (string.IsNullOrWhiteSpace(token) || expectedHash.Length != 64)
        {
            return false;
        }

        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        Span<byte> actual = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(token), actual);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
