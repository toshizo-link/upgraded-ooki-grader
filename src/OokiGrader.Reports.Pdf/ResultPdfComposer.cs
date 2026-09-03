using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace OokiGrader.Reports.Pdf;

/// <summary>
/// Produces the teacher-facing handout: the retained scanned answer pages first,
/// followed by the compact structured transcript. When scan retention has
/// expired callers continue to use the transcript renderer by itself.
/// </summary>
public static class ResultPdfComposer
{
    public const int MaximumOriginalPages = 100;

    public static ResultPdfRenderResult PrependOriginal(
        Stream originalPdf,
        ResultPdfRenderResult transcript,
        ResultReportDocument report)
    {
        ArgumentNullException.ThrowIfNull(originalPdf);
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(report);
        if (!originalPdf.CanRead)
        {
            throw new ArgumentException("The original PDF must be readable.", nameof(originalPdf));
        }

        using var original = PdfReader.Open(originalPdf, PdfDocumentOpenMode.Import);
        if (original.PageCount is < 1 or > MaximumOriginalPages)
        {
            throw new InvalidDataException(
                $"The original PDF must contain 1 to {MaximumOriginalPages} pages.");
        }

        using var transcriptStream = new MemoryStream(
            transcript.PdfBytes,
            writable: false);
        using var transcriptDocument = PdfReader.Open(
            transcriptStream,
            PdfDocumentOpenMode.Import);
        if (transcriptDocument.PageCount != transcript.PageCount
            || transcriptDocument.PageCount < 1)
        {
            throw new InvalidDataException("The transcript PDF page count is invalid.");
        }

        using var combined = new PdfDocument();
        combined.Info.Title = $"{report.TestTitle} - 答案・成績表";
        combined.Info.Author = report.SchoolName;
        combined.Info.Subject = $"答案・成績表 {report.ReportId}";
        combined.Info.Creator = $"Ooki Grader {ResultPdfRenderer.CurrentRendererVersion}";
        combined.Info.CreationDate = report.GeneratedAt.UtcDateTime;
        combined.Info.ModificationDate = report.GeneratedAt.UtcDateTime;
        SetDocumentIdentifiers(combined, report, transcript.Sha256);

        for (var index = 0; index < original.PageCount; index++)
        {
            combined.AddPage(original.Pages[index]);
        }
        for (var index = 0; index < transcriptDocument.PageCount; index++)
        {
            combined.AddPage(transcriptDocument.Pages[index]);
        }

        using var output = new MemoryStream();
        combined.Save(output, closeStream: false);
        var bytes = output.ToArray();
        var expectedPageCount = checked(original.PageCount + transcript.PageCount);
        using (var verificationStream = new MemoryStream(bytes, writable: false))
        using (var verification = PdfReader.Open(
            verificationStream,
            PdfDocumentOpenMode.Import))
        {
            if (verification.PageCount != expectedPageCount)
            {
                throw new InvalidDataException(
                    "The combined answer and transcript PDF page count is invalid.");
            }
        }

        return new ResultPdfRenderResult(
            bytes,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            expectedPageCount,
            ResultPdfRenderer.CurrentRendererVersion);
    }

    private static void SetDocumentIdentifiers(
        PdfDocument document,
        ResultReportDocument report,
        string transcriptSha256)
    {
        var seed = Encoding.UTF8.GetBytes(
            $"{report.ReportId}\0" +
            $"{report.GeneratedAt.ToString("O", CultureInfo.InvariantCulture)}\0" +
            $"{report.OriginalScanSha256}\0{transcriptSha256}");
        var digest = Convert.ToHexString(SHA256.HashData(seed)).ToLowerInvariant();
        document.Internals.FirstDocumentID = digest[..32];
        document.Internals.SecondDocumentID = digest[32..];
    }
}
