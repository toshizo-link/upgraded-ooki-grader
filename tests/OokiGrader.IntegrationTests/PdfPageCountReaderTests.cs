using OokiGrader.Host.Services;
using PdfSharp.Pdf;
using OokiGrader.Preprocessing;

namespace OokiGrader.IntegrationTests;

public sealed class PdfPageCountReaderTests
{
    [ProvidedPdfFact("OOKI_CUSTOMER_TEMPLATE_PDF")]
    public async Task ProvidedTwoPageTemplateCanBeReadAndPreparedForAi()
    {
        await using var source = File.OpenRead(Environment.GetEnvironmentVariable("OOKI_CUSTOMER_TEMPLATE_PDF")!);
        Assert.Equal(2, await new LocalPdfPageCountReader().GetPageCountAsync(source, 200));
        source.Position = 0;
        var prepared = await new PdfPageRangeExtractor(new PreprocessingService()).ExtractAsync(
            source, "customer-template.pdf", 1, 2, new Dictionary<int, int>());
        await using var derived = new MemoryStream(prepared.Bytes, writable: false);
        Assert.Equal(2, await new LocalPdfPageCountReader().GetPageCountAsync(derived, 200));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ReadsUploadedPdfFromAsyncFileStream(int pages)
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var pdf = new PdfDocument())
            {
                for (var index = 0; index < pages; index++) pdf.AddPage();
                pdf.Save(path);
            }
            await using var source = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            Assert.Equal(pages, await new LocalPdfPageCountReader()
                .GetPageCountAsync(source, 2));
            Assert.True(source.CanRead);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
