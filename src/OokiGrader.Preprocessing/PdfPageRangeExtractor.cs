namespace OokiGrader.Preprocessing;

/// <summary>
/// Deterministically materializes a PDF from an inclusive original page range.
/// The implementation rasterizes through the existing bounded preprocessing
/// pipeline so that metadata normalization and resource limits remain uniform.
/// </summary>
public sealed class PdfPageRangeExtractor(
    IPreprocessingService preprocessingService) : IPdfPageRangeExtractor
{
    public async Task<DerivedPdfResult> ExtractAsync(
        Stream source,
        string sourceName,
        int firstPage,
        int lastPage,
        IReadOnlyDictionary<int, int> rotations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(rotations);
        if (firstPage < 1 || lastPage < firstPage)
        {
            throw new ArgumentOutOfRangeException(
                nameof(firstPage),
                "The inclusive PDF page range is invalid.");
        }

        foreach (var rotation in rotations)
        {
            if (rotation.Key < firstPage
                || rotation.Key > lastPage
                || rotation.Value is not (0 or 90 or 180 or 270))
            {
                throw new ArgumentException(
                    "Rotation entries must reference the selected range and use a quarter turn.",
                    nameof(rotations));
            }
        }

        var result = await preprocessingService.ProcessAsync(
                source,
                new PreprocessingInput(
                    "application/pdf",
                    sourceName,
                    FirstPdfPage: firstPage,
                    LastPdfPage: lastPage),
                cancellationToken)
            .ConfigureAwait(false);
        var expectedPageCount = lastPage - firstPage + 1;
        if (result.Pages.Count != expectedPageCount)
        {
            throw new PreprocessingException(
                "page_range_invalid",
                "The requested page range exceeds the PDF page count.");
        }

        var selectedPages = new List<PreprocessedPage>(lastPage - firstPage + 1);
        var applied = new List<AppliedPageRotation>(lastPage - firstPage + 1);
        for (var pageNumber = firstPage; pageNumber <= lastPage; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = result.Pages[pageNumber - firstPage];
            var degrees = rotations.GetValueOrDefault(pageNumber);
            var pageId = $"page-{pageNumber}";
            selectedPages.Add(PageQuarterTurnRotator.Rotate(
                page,
                degrees,
                degrees == 0 ? "none" : "gemini"));
            applied.Add(new AppliedPageRotation(
                pageId,
                pageNumber,
                degrees,
                degrees == 0 ? "none" : "gemini",
                degrees == 0 ? 1 : 0));
        }

        var pdf = PreprocessedDocumentEncoder.ToPdf(
            selectedPages,
            cancellationToken);
        return new DerivedPdfResult(
            pdf,
            Fingerprinting.Sha256(pdf),
            selectedPages.Count,
            firstPage,
            lastPage,
            applied);
    }
}
