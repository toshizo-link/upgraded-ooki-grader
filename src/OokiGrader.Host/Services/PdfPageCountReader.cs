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

    public string UserDetail => Code switch
    {
        "PDF_ENCRYPTED" => "このPDFはパスワードで保護されています。保護されていないPDFを選択してください。",
        "PDF_PAGE_COUNT_INVALID" => "PDFのページ数が受付範囲外です。答案は1ファイル1ページに分けてください。",
        "PDF_READER_UNAVAILABLE" => "サーバーのPDF読み取り機能を起動できません。管理者にホストの更新状態とログの確認を依頼してください。再送だけでは解消しません。",
        _ => "PDFを読み取れませんでした。PDF閲覧ソフトで開けるか確認し、PDFとして保存し直すか再スキャンしてください。改善しない場合は管理者にこのファイルとエラーコードをお伝えください。",
    };
}

/// <summary>
/// Reads only the PDF page tree. It deliberately avoids rasterizing every page
/// during cost-free batch validation.
/// </summary>
public sealed class LocalPdfPageCountReader(ILogger<LocalPdfPageCountReader>? logger = null) : IPdfPageCountReader
{
    private static readonly SemaphoreSlim PdfGate = new(1, 1);
    private static readonly Action<ILogger, Exception?> LogReadFailure = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1, "PdfPageCountFailed"), "PDF page-count validation failed.");

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
                if (logger is not null) LogReadFailure(logger, exception);
                throw new PdfPageCountException(
                    IsReaderUnavailable(exception) ? "PDF_READER_UNAVAILABLE" : "PDF_INVALID",
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

    private static bool IsReaderUnavailable(Exception exception) =>
        exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
        || (exception.InnerException is { } inner && IsReaderUnavailable(inner));
}
