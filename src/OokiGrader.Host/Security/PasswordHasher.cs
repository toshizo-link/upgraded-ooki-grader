using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace OokiGrader.Host.Security;

public interface IPasswordHasher
{
    Task<string> HashAsync(string password, CancellationToken cancellationToken = default);

    Task<bool> VerifyAsync(
        string password,
        string encodedHash,
        CancellationToken cancellationToken = default);
}

public sealed class PasswordHasher : IPasswordHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int MemoryKiB = 65_536;
    private const int Iterations = 3;
    private const int Parallelism = 2;
    private const string Prefix = "$argon2id$v=19$";

    public async Task<string> HashAsync(
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        cancellationToken.ThrowIfCancellationRequested();

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = await DeriveAsync(password, salt, MemoryKiB, Iterations, Parallelism);
        return $"{Prefix}m={MemoryKiB},t={Iterations},p={Parallelism}" +
               $"${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public async Task<bool> VerifyAsync(
        string password,
        string encodedHash,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(password)
            || !TryParse(encodedHash, out var salt, out var expected, out var memory, out var iterations, out var parallelism))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var actual = await DeriveAsync(password, salt, memory, iterations, parallelism);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static Task<byte[]> DeriveAsync(
        string password,
        byte[] salt,
        int memory,
        int iterations,
        int parallelism)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memory,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };

        return argon.GetBytesAsync(HashBytes);
    }

    private static bool TryParse(
        string encoded,
        out byte[] salt,
        out byte[] hash,
        out int memory,
        out int iterations,
        out int parallelism)
    {
        salt = [];
        hash = [];
        memory = 0;
        iterations = 0;
        parallelism = 0;

        if (!encoded.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = encoded[Prefix.Length..].Split('$');
        if (parts.Length != 3)
        {
            return false;
        }

        var parameters = parts[0]
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Split('=', 2))
            .Where(item => item.Length == 2)
            .ToDictionary(item => item[0], item => item[1], StringComparer.Ordinal);

        try
        {
            if (!int.TryParse(parameters.GetValueOrDefault("m"), out memory)
                || !int.TryParse(parameters.GetValueOrDefault("t"), out iterations)
                || !int.TryParse(parameters.GetValueOrDefault("p"), out parallelism)
                || memory is < 8_192 or > 1_048_576
                || iterations is < 1 or > 10
                || parallelism is < 1 or > 16)
            {
                return false;
            }

            salt = Convert.FromBase64String(parts[1]);
            hash = Convert.FromBase64String(parts[2]);
            return salt.Length >= 16 && hash.Length == HashBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
