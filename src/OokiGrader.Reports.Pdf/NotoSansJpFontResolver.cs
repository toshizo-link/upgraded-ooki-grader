using System.Reflection;
using PdfSharp.Fonts;

namespace OokiGrader.Reports.Pdf;

internal sealed class NotoSansJpFontResolver : IFontResolver
{
    internal const string FamilyName = "Ooki Noto Sans JP";
    private const string FaceName = "ooki-noto-sans-jp-regular";
    private const string FontResourceName =
        "OokiGrader.Reports.Pdf.Assets.NotoSansJP.ttf";
    private static readonly object RegistrationLock = new();
    private static readonly Lazy<byte[]> FontBytes = new(
        LoadFontBytes,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public FontResolverInfo? ResolveTypeface(
        string familyName,
        bool bold,
        bool italic)
    {
        return string.Equals(familyName, FamilyName, StringComparison.OrdinalIgnoreCase)
            ? new FontResolverInfo(
                FaceName,
                mustSimulateBold: bold,
                mustSimulateItalic: italic)
            : null;
    }

    public byte[]? GetFont(string faceName) =>
        string.Equals(faceName, FaceName, StringComparison.Ordinal)
            ? FontBytes.Value
            : null;

    internal static void EnsureRegistered()
    {
        lock (RegistrationLock)
        {
            if (GlobalFontSettings.FontResolver is NotoSansJpFontResolver)
            {
                return;
            }

            if (GlobalFontSettings.FontResolver is not null)
            {
                throw new InvalidOperationException(
                    "A different PDFsharp font resolver is already registered. " +
                    "Register the Ooki font resolver before creating PDF fonts.");
            }

            GlobalFontSettings.FontResolver = new NotoSansJpFontResolver();
        }
    }

    private static byte[] LoadFontBytes()
    {
        using var stream = typeof(NotoSansJpFontResolver)
            .Assembly
            .GetManifestResourceStream(FontResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded font resource '{FontResourceName}' was not found.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
