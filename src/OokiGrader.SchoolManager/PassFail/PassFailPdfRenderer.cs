using System.Security.Cryptography;
using OokiGrader.Reports.Pdf;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace OokiGrader.SchoolManager.PassFail;

public sealed record PassFailPdfRenderResult(
    byte[] Bytes,
    string Sha256,
    int PageCount,
    string RendererVersion);

public static class PassFailPdfRenderer
{
    public const string CurrentRendererVersion = "pdfsharp-6.2.4-pass-fail-1";
    private const double PageWidth = 841.89;
    private const double PageHeight = 595.28;
    private const double Margin = 26;
    private const double LabelWidth = 66;
    private const double HeaderHeight = 42;
    private const double RowHeight = 18;
    private const int ColumnsPerPage = 12;
    private const int RowsPerPage = 24;

    public static PassFailPdfRenderResult Render(
        PassFailProjection projection,
        DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (projection.Columns.Count == 0 || projection.Rows.Count == 0)
        {
            throw new ArgumentException(
                "The pass/fail projection must contain columns and rows.",
                nameof(projection));
        }

        NotoSansJpFontResolver.EnsureRegistered();
        using var document = new PdfDocument();
        document.Info.Title = $"{projection.Title} {projection.ClassLabel}";
        document.Info.Subject = "匿名化合否表";
        document.Info.Creator = $"Ooki Grader {CurrentRendererVersion}";
        document.Info.CreationDate = generatedAt.UtcDateTime;
        document.Info.ModificationDate = generatedAt.UtcDateTime;

        var titleFont = new XFont(
            NotoSansJpFontResolver.FamilyName,
            14,
            XFontStyleEx.Bold);
        var smallFont = new XFont(NotoSansJpFontResolver.FamilyName, 7.2);
        var smallBold = new XFont(
            NotoSansJpFontResolver.FamilyName,
            7.2,
            XFontStyleEx.Bold);
        var muted = new XSolidBrush(XColor.FromArgb(82, 94, 112));
        var ink = new XSolidBrush(XColor.FromArgb(24, 33, 48));
        var accent = new XSolidBrush(XColor.FromArgb(22, 112, 126));
        var target = new XSolidBrush(XColor.FromArgb(229, 246, 248));
        var alternate = new XSolidBrush(XColor.FromArgb(247, 249, 252));
        var border = new XPen(XColor.FromArgb(199, 208, 219), 0.55);

        for (var columnStart = 0;
             columnStart < projection.Columns.Count;
             columnStart += ColumnsPerPage)
        {
            var columnCount = Math.Min(
                ColumnsPerPage,
                projection.Columns.Count - columnStart);
            for (var rowStart = 0;
                 rowStart < projection.Rows.Count;
                 rowStart += RowsPerPage)
            {
                var rowCount = Math.Min(
                    RowsPerPage,
                    projection.Rows.Count - rowStart);
                var page = document.AddPage();
                page.Orientation = PdfSharp.PageOrientation.Landscape;
                page.Width = XUnit.FromPoint(PageWidth);
                page.Height = XUnit.FromPoint(PageHeight);
                using var graphics = XGraphics.FromPdfPage(page);
                graphics.DrawRectangle(XBrushes.White, 0, 0, PageWidth, PageHeight);
                DrawHeader(
                    graphics,
                    projection,
                    generatedAt,
                    columnStart,
                    columnCount,
                    titleFont,
                    smallFont,
                    ink,
                    muted);
                DrawTable(
                    graphics,
                    projection,
                    columnStart,
                    columnCount,
                    rowStart,
                    rowCount,
                    smallFont,
                    smallBold,
                    ink,
                    accent,
                    target,
                    alternate,
                    border);
                graphics.DrawString(
                    $"{document.PageCount} ページ",
                    smallFont,
                    muted,
                    new XRect(Margin, PageHeight - 20, PageWidth - (2 * Margin), 10),
                    XStringFormats.TopRight);
            }
        }

        var expectedPageCount = document.PageCount;
        using var output = new MemoryStream();
        document.Save(output, closeStream: false);
        var bytes = output.ToArray();
        using var verified = PdfReader.Open(
            new MemoryStream(bytes),
            PdfDocumentOpenMode.Import);
        if (verified.PageCount != expectedPageCount)
        {
            throw new InvalidDataException("The pass/fail PDF page count is invalid.");
        }

        return new PassFailPdfRenderResult(
            bytes,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            verified.PageCount,
            CurrentRendererVersion);
    }

    private static void DrawHeader(
        XGraphics graphics,
        PassFailProjection projection,
        DateTimeOffset generatedAt,
        int columnStart,
        int columnCount,
        XFont titleFont,
        XFont smallFont,
        XBrush ink,
        XBrush muted)
    {
        graphics.DrawString(
            projection.Title,
            titleFont,
            ink,
            new XRect(Margin, 20, 540, 22),
            XStringFormats.TopLeft);
        graphics.DrawString(
            $"学年 {projection.GradeLabel}　クラス {projection.ClassLabel}　" +
            "氏名・生徒ID削除済み",
            smallFont,
            muted,
            new XRect(Margin, 43, 520, 14),
            XStringFormats.TopLeft);
        graphics.DrawString(
            $"更新 {generatedAt.ToOffset(TimeSpan.FromHours(9)):yyyy/MM/dd HH:mm}　" +
            $"項目 {columnStart + 1}–{columnStart + columnCount} / {projection.Columns.Count}",
            smallFont,
            muted,
            new XRect(560, 26, PageWidth - Margin - 560, 16),
            XStringFormats.TopRight);
    }

    private static void DrawTable(
        XGraphics graphics,
        PassFailProjection projection,
        int columnStart,
        int columnCount,
        int rowStart,
        int rowCount,
        XFont font,
        XFont boldFont,
        XBrush ink,
        XBrush accent,
        XBrush targetBrush,
        XBrush alternateBrush,
        XPen border)
    {
        var top = 68d;
        var tableWidth = PageWidth - (2 * Margin);
        var valueWidth = (tableWidth - LabelWidth) / columnCount;
        graphics.DrawRectangle(accent, Margin, top, tableWidth, HeaderHeight);
        DrawCellText(
            graphics,
            "識別",
            boldFont,
            XBrushes.White,
            new XRect(Margin, top, LabelWidth, HeaderHeight));
        for (var index = 0; index < columnCount; index++)
        {
            var x = Margin + LabelWidth + (index * valueWidth);
            DrawCellText(
                graphics,
                projection.Columns[columnStart + index],
                font,
                XBrushes.White,
                new XRect(x, top, valueWidth, HeaderHeight));
            graphics.DrawLine(border, x, top, x, top + HeaderHeight);
        }

        for (var index = 0; index < rowCount; index++)
        {
            var row = projection.Rows[rowStart + index];
            var y = top + HeaderHeight + (index * RowHeight);
            var background = row.Label == "本人"
                ? targetBrush
                : index % 2 == 1 ? alternateBrush : XBrushes.White;
            graphics.DrawRectangle(background, Margin, y, tableWidth, RowHeight);
            DrawCellText(
                graphics,
                row.Label,
                row.Label == "本人" ? boldFont : font,
                ink,
                new XRect(Margin, y, LabelWidth, RowHeight));
            for (var column = 0; column < columnCount; column++)
            {
                var x = Margin + LabelWidth + (column * valueWidth);
                DrawCellText(
                    graphics,
                    row.Values[columnStart + column],
                    font,
                    ink,
                    new XRect(x, y, valueWidth, RowHeight));
                graphics.DrawLine(border, x, y, x, y + RowHeight);
            }

            graphics.DrawLine(
                border,
                Margin,
                y + RowHeight,
                Margin + tableWidth,
                y + RowHeight);
        }

        graphics.DrawRectangle(
            border,
            Margin,
            top,
            tableWidth,
            HeaderHeight + (rowCount * RowHeight));
    }

    private static void DrawCellText(
        XGraphics graphics,
        string value,
        XFont font,
        XBrush brush,
        XRect bounds)
    {
        var text = value.Length <= 18 ? value : string.Concat(value.AsSpan(0, 17), "…");
        graphics.DrawString(
            text,
            font,
            brush,
            new XRect(bounds.X + 2, bounds.Y + 1, bounds.Width - 4, bounds.Height - 2),
            XStringFormats.Center);
    }
}
