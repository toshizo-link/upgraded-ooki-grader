namespace OokiGrader.Host.Uploads;

public sealed record VerifiedFileType(string MimeType, string Extension);

public static class FileSignatureValidator
{
    public static async Task<VerifiedFileType?> IdentifyAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new ArgumentException("Signature validation requires a seekable stream.", nameof(stream));
        }

        var originalPosition = stream.Position;
        stream.Position = 0;
        var header = new byte[8];
        var read = await stream.ReadAsync(header, cancellationToken);
        stream.Position = originalPosition;

        if (read >= 5
            && header[0] == '%'
            && header[1] == 'P'
            && header[2] == 'D'
            && header[3] == 'F'
            && header[4] == '-')
        {
            return new VerifiedFileType("application/pdf", "pdf");
        }

        if (read >= 3 && header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff)
        {
            return new VerifiedFileType("image/jpeg", "jpg");
        }

        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        if (read >= png.Length && header.AsSpan().StartsWith(png))
        {
            return new VerifiedFileType("image/png", "png");
        }

        var littleEndianTiff = read >= 4
            && header[0] == 0x49
            && header[1] == 0x49
            && header[2] == 0x2a
            && header[3] == 0x00;
        var bigEndianTiff = read >= 4
            && header[0] == 0x4d
            && header[1] == 0x4d
            && header[2] == 0x00
            && header[3] == 0x2a;
        return littleEndianTiff || bigEndianTiff
            ? new VerifiedFileType("image/tiff", "tiff")
            : null;
    }
}
