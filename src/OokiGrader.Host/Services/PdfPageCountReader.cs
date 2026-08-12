using PDFtoImage;
using PDFtoImage.Exceptions;

namespace OokiGrader.Host.Services;

public interface IPdfPageCountReader
{
    Task<int> GetPageCountAsync(
        Stream source,
        int maximumPages,
        CancellationToken cancellationToken = default);
}

public sealed class PdfPageCountException(
    string code,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
}

/// <summary>
/// Reads only the PDF page tree. It deliberately avoids rasterizing every page
/// during cost-free batch validation.
/// </summary>
public sealed class LocalPdfPageCountReader : IPdfPageCountReader
{
    private static readonly SemaphoreSlim PdfGate = new(1, 1);

    public async Task<int> GetPageCountAsync(
        Stream source,
        int maximumPages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPages);
        if (!source.CanRead)
        {
            throw new ArgumentException(
                "The PDF stream must be readable.",
                nameof(source));
        }

        await PdfGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.CanSeek)
            {
                source.Position = 0;
            }

            int pageCount;
            try
            {
#pragma warning disable CA1416 // Ooki Grader runs only on PDFtoImage-supported host platforms.
                pageCount = Conversion.GetPageCount(
                    source,
                    leaveOpen: true,
                    password: null);
#pragma warning restore CA1416
            }
            catch (PdfPasswordProtectedException exception)
            {
                throw new PdfPageCountException(
                    "PDF_ENCRYPTED",
                    "Encrypted PDFs are not accepted.",
                    exception);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException
                && exception is not PdfPageCountException)
            {
                throw new PdfPageCountException(
                    "PDF_INVALID",
                    "The PDF page count could not be read.",
                    exception);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (pageCount is <= 0 || pageCount > maximumPages)
            {
                throw new PdfPageCountException(
                    "PDF_PAGE_COUNT_INVALID",
                    $"The PDF page count must be between 1 and {maximumPages}.");
            }

            return pageCount;
        }
        finally
        {
            PdfGate.Release();
        }
    }
}
