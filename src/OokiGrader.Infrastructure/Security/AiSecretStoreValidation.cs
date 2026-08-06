using System.Globalization;
using System.Text;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;

namespace OokiGrader.Infrastructure.Security;

internal static class AiSecretStoreValidation
{
    public const int MaximumSecretCharacters = 4_096;
    public const int MaximumSecretBytes = 16_384;
    public const int MaximumEnvelopeBytes = 65_536;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static AiSecretReference CreateReference(
        string scheme,
        string ownerId,
        long credentialRevision)
    {
        ValidateScheme(scheme);
        ValidateOwnerAndRevision(ownerId, credentialRevision);
        return new AiSecretReference(
            $"{scheme}/{ownerId}/" +
            $"{credentialRevision.ToString("D20", CultureInfo.InvariantCulture)}.secret");
    }

    public static ParsedAiSecretReference ParseReference(
        AiSecretReference reference,
        string expectedScheme)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ValidateScheme(expectedScheme);

        var value = reference.Value;
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || value.Contains('\\', StringComparison.Ordinal)
            || Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException(
                "The AI secret reference is invalid.",
                nameof(reference));
        }

        var parts = value.Split(
            '/',
            StringSplitOptions.None);
        if (parts.Length != 3
            || !string.Equals(parts[0], expectedScheme, StringComparison.Ordinal)
            || !UlidId.IsCanonical(parts[1])
            || !parts[2].EndsWith(".secret", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The AI secret reference is invalid.",
                nameof(reference));
        }

        var revisionText = parts[2][..^".secret".Length];
        if (!long.TryParse(
                revisionText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var revision)
            || revision <= 0
            || !string.Equals(
                revisionText,
                revision.ToString("D20", CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The AI secret reference is invalid.",
                nameof(reference));
        }

        return new ParsedAiSecretReference(value, parts[1], revision);
    }

    public static byte[] EncodeSecret(ReadOnlySpan<char> secret)
    {
        if (secret.IsEmpty || secret.Length > MaximumSecretCharacters)
        {
            throw new ArgumentException(
                $"An AI secret must contain between 1 and " +
                $"{MaximumSecretCharacters} characters.",
                nameof(secret));
        }

        foreach (var character in secret)
        {
            if (char.IsControl(character))
            {
                throw new ArgumentException(
                    "An AI secret cannot contain control characters.",
                    nameof(secret));
            }
        }

        var byteCount = StrictUtf8.GetByteCount(secret);
        if (byteCount is <= 0 or > MaximumSecretBytes)
        {
            throw new ArgumentException(
                $"The UTF-8 AI secret cannot exceed {MaximumSecretBytes} bytes.",
                nameof(secret));
        }

        var bytes = GC.AllocateUninitializedArray<byte>(byteCount);
        StrictUtf8.GetBytes(secret, bytes);
        return bytes;
    }

    public static void ValidateDecodedSecret(ReadOnlySpan<byte> secret)
    {
        if (secret.IsEmpty || secret.Length > MaximumSecretBytes)
        {
            throw new InvalidDataException("The protected AI secret has an invalid length.");
        }

        try
        {
            _ = StrictUtf8.GetCharCount(secret);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "The protected AI secret is not valid UTF-8.",
                exception);
        }
    }

    public static void ValidateOwnerAndRevision(
        string ownerId,
        long credentialRevision)
    {
        if (!UlidId.IsCanonical(ownerId))
        {
            throw new ArgumentException(
                "The AI secret owner must be a canonical ULID.",
                nameof(ownerId));
        }

        if (credentialRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(credentialRevision),
                "The AI credential revision must be positive.");
        }
    }

    private static void ValidateScheme(string scheme)
    {
        if (scheme is not ("dpapi-v1" or "memory-v1"))
        {
            throw new ArgumentException(
                "The AI secret reference scheme is unsupported.",
                nameof(scheme));
        }
    }
}

internal sealed record ParsedAiSecretReference(
    string Value,
    string OwnerId,
    long CredentialRevision);
