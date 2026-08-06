using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace OokiGrader.Reports.Pdf;

public sealed class ResultPdfRenderer : IResultPdfRenderer
{
    public const string CurrentRendererVersion = "pdfsharp-6.2.4-layout-1";
    private const double PageWidth = 595.28;
    private const double PageHeight = 841.89;
    private const double MarginLeft = 42;
    private const double MarginRight = 42;
    private const double HeaderTop = 28;
    private const double HeaderBottom = 62;
    private const double FooterTop = 812;
    private const double ContentBottom = 800;
    private const double TableHeaderHeight = 27;
    private const double CellPadding = 6;
    private const double BodyLineHeight = 13.2;
    private const double MinimumRowHeight = 31;
    private const double LabelColumnWidth = 38;
    private const double QuestionColumnWidth = 216;
    private const double AnswerColumnWidth = 128;
    private const double ScoreColumnWidth = 76;
    private const double OutcomeColumnWidth = 53.28;
    private const int MaximumQuestions = 2_000;

    private static readonly XColor Ink = XColor.FromArgb(31, 41, 55);
    private static readonly XColor MutedInk = XColor.FromArgb(87, 99, 116);
    private static readonly XColor Accent = XColor.FromArgb(22, 112, 126);
    private static readonly XColor AccentPale = XColor.FromArgb(232, 246, 247);
    private static readonly XColor Border = XColor.FromArgb(207, 215, 224);
    private static readonly XColor AlternateRow = XColor.FromArgb(248, 250, 252);
    private static readonly XColor CorrectedPale = XColor.FromArgb(255, 247, 224);
    private static readonly HashSet<string> AllowedOutcomes =
        new(StringComparer.Ordinal)
        {
            "correct",
            "partial",
            "incorrect",
            "blank",
            "unreadable",
        };

    public ResultPdfRenderResult Render(ResultReportDocument report)
    {
        Validate(report);
        NotoSansJpFontResolver.EnsureRegistered();

        using var document = new PdfDocument();
        document.Info.Title = $"{report.TestTitle} - 採点結果";
        document.Info.Author = report.SchoolName;
        document.Info.Subject = $"結果帳票 {report.ReportId}";
        document.Info.Creator =
            $"Ooki Grader {CurrentRendererVersion}";
        document.Info.CreationDate = report.GeneratedAt.UtcDateTime;
        document.Info.ModificationDate = report.GeneratedAt.UtcDateTime;
        SetDeterministicDocumentIdentifiers(document, report);

        var fonts = ReportFonts.Create();
        var pages = new List<PageCanvas>();
        var canvas = AddPage(document, pages, report, fonts);
        DrawFirstPageSummary(canvas, report, fonts);
        DrawTableHeader(canvas, fonts);

        if (report.Questions.Count == 0)
        {
            DrawEmptyState(canvas, fonts);
        }
        else
        {
            for (var index = 0; index < report.Questions.Count; index++)
            {
                DrawQuestion(
                    document,
                    pages,
                    ref canvas,
                    report,
                    report.Questions[index],
                    index,
                    fonts);
            }
        }

        DrawClosingNote(document, pages, ref canvas, report, fonts);
        DrawFooters(pages, report, fonts);
        foreach (var page in pages)
        {
            page.Graphics.Dispose();
        }

        using var output = new MemoryStream();
        document.Save(output, closeStream: false);
        var bytes = CanonicalizePdf(output.ToArray(), report);
        var verifiedPageCount = VerifyPdf(bytes, pages.Count);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes))
            .ToLowerInvariant();
        return new ResultPdfRenderResult(
            bytes,
            sha256,
            verifiedPageCount,
            CurrentRendererVersion);
    }

    private static PageCanvas AddPage(
        PdfDocument document,
        List<PageCanvas> pages,
        ResultReportDocument report,
        ReportFonts fonts)
    {
        var page = document.AddPage();
        page.Width = XUnit.FromPoint(PageWidth);
        page.Height = XUnit.FromPoint(PageHeight);
        var graphics = XGraphics.FromPdfPage(page);
        graphics.DrawRectangle(XBrushes.White, 0, 0, PageWidth, PageHeight);
        graphics.DrawString(
            report.SchoolName,
            fonts.Small,
            new XSolidBrush(MutedInk),
            new XRect(MarginLeft, HeaderTop, 300, 18),
            XStringFormats.TopLeft);
        graphics.DrawString(
            "採点結果",
            fonts.Small,
            new XSolidBrush(Accent),
            new XRect(
                PageWidth - MarginRight - 160,
                HeaderTop,
                160,
                18),
            XStringFormats.TopRight);
        graphics.DrawLine(
            new XPen(Border, 0.7),
            MarginLeft,
            HeaderBottom - 7,
            PageWidth - MarginRight,
            HeaderBottom - 7);

        var canvas = new PageCanvas(page, graphics, HeaderBottom + 9);
        pages.Add(canvas);
        return canvas;
    }

    private static void DrawFirstPageSummary(
        PageCanvas canvas,
        ResultReportDocument report,
        ReportFonts fonts)
    {
        var titleLines = WrapText(
            canvas.Graphics,
            report.TestTitle,
            fonts.Title,
            PageWidth - MarginLeft - MarginRight);
        DrawLines(
            canvas.Graphics,
            titleLines,
            fonts.Title,
            new XSolidBrush(Ink),
            MarginLeft,
            canvas.Y,
            22);
        canvas.Y += titleLines.Count * 22 + 8;

        var studentText = string.IsNullOrWhiteSpace(report.StudentNumber)
            ? $"生徒　{report.StudentDisplayName}"
            : $"生徒　{report.StudentDisplayName}　（{report.StudentNumber}）";
        canvas.Graphics.DrawString(
            studentText,
            fonts.Body,
            new XSolidBrush(Ink),
            new XPoint(MarginLeft, canvas.Y + 12));
        canvas.Graphics.DrawString(
            $"実施日　{report.TestDate:yyyy年M月d日}　　採点基準　第{report.TemplateVersionNumber}版",
            fonts.Body,
            new XSolidBrush(MutedInk),
            new XPoint(MarginLeft, canvas.Y + 32));
        canvas.Y += 45;

        const double summaryHeight = 64;
        canvas.Graphics.DrawRoundedRectangle(
            new XSolidBrush(AccentPale),
            MarginLeft,
            canvas.Y,
            PageWidth - MarginLeft - MarginRight,
            summaryHeight,
            6,
            6);
        var score = FormatPoints(report.EarnedPointsMilli);
        var maximum = FormatPoints(report.PossiblePointsMilli);
        var percentage = report.PossiblePointsMilli == 0
            ? "算出不可"
            : $"{ComputePercentage(report.EarnedPointsMilli, report.PossiblePointsMilli):0.0}%";
        canvas.Graphics.DrawString(
            "合計",
            fonts.Small,
            new XSolidBrush(MutedInk),
            new XPoint(MarginLeft + 16, canvas.Y + 19));
        canvas.Graphics.DrawString(
            $"{score} / {maximum} 点",
            fonts.Score,
            new XSolidBrush(Accent),
            new XPoint(MarginLeft + 16, canvas.Y + 47));
        canvas.Graphics.DrawString(
            $"得点率　{percentage}",
            fonts.Body,
            new XSolidBrush(Ink),
            new XPoint(MarginLeft + 258, canvas.Y + 28));
        canvas.Graphics.DrawString(
            report.IsCorrectedGrade ? "現在の訂正済み結果" : "現在の確定結果",
            fonts.Small,
            new XSolidBrush(report.IsCorrectedGrade ? XColors.DarkGoldenrod : MutedInk),
            new XPoint(MarginLeft + 258, canvas.Y + 48));
        canvas.Y += summaryHeight + 17;
    }

    private static void DrawTableHeader(PageCanvas canvas, ReportFonts fonts)
    {
        var widths = ColumnWidths();
        var labels = new[] { "番号", "問題", "解答", "得点", "判定" };
        var x = MarginLeft;
        for (var index = 0; index < labels.Length; index++)
        {
            canvas.Graphics.DrawRectangle(
                new XSolidBrush(Accent),
                x,
                canvas.Y,
                widths[index],
                TableHeaderHeight);
            canvas.Graphics.DrawRectangle(
                new XPen(XColors.White, 0.4),
                x,
                canvas.Y,
                widths[index],
                TableHeaderHeight);
            canvas.Graphics.DrawString(
                labels[index],
                fonts.TableHeader,
                XBrushes.White,
                new XRect(x + 3, canvas.Y + 1, widths[index] - 6, TableHeaderHeight),
                XStringFormats.Center);
            x += widths[index];
        }

        canvas.Y += TableHeaderHeight;
    }

    private static void DrawQuestion(
        PdfDocument document,
        List<PageCanvas> pages,
        ref PageCanvas canvas,
        ResultReportDocument report,
        ResultReportQuestion question,
        int rowIndex,
        ReportFonts fonts)
    {
        var questionText = question.QuestionText;
        if (report.IncludeTeacherComments
            && !string.IsNullOrWhiteSpace(question.TeacherComment))
        {
            questionText += $"\nコメント: {question.TeacherComment}";
        }

        var answerText = string.IsNullOrWhiteSpace(question.RecognizedAnswer)
            ? question.Outcome == "blank" ? "（空欄）" : "（認識なし）"
            : question.RecognizedAnswer!;
        var questionLines = WrapText(
            canvas.Graphics,
            questionText,
            fonts.TableBody,
            QuestionColumnWidth - (2 * CellPadding));
        var answerLines = WrapText(
            canvas.Graphics,
            answerText,
            fonts.TableBody,
            AnswerColumnWidth - (2 * CellPadding));
        var totalLines = Math.Max(1, Math.Max(questionLines.Count, answerLines.Count));
        var fullRowHeight = Math.Max(
            MinimumRowHeight,
            (totalLines * BodyLineHeight) + (2 * CellPadding));
        var fullPageRowCapacity = MaximumLinesForHeight(
            ContentBottom - (HeaderBottom + 9) - TableHeaderHeight);

        if (fullRowHeight <= ContentBottom - canvas.Y
            || fullRowHeight <=
                ContentBottom - (HeaderBottom + 9) - TableHeaderHeight)
        {
            if (fullRowHeight > ContentBottom - canvas.Y)
            {
                canvas = AddPage(document, pages, report, fonts);
                DrawTableHeader(canvas, fonts);
            }

            DrawQuestionChunk(
                canvas,
                question,
                rowIndex,
                questionLines,
                answerLines,
                0,
                totalLines,
                fullRowHeight,
                continuation: false,
                fonts);
            canvas.Y += fullRowHeight;
            return;
        }

        var offset = 0;
        var continuation = false;
        while (offset < totalLines)
        {
            var availableLines = MaximumLinesForHeight(ContentBottom - canvas.Y);
            if (availableLines <= 0)
            {
                canvas = AddPage(document, pages, report, fonts);
                DrawTableHeader(canvas, fonts);
                availableLines = fullPageRowCapacity;
            }

            var linesInChunk = Math.Min(availableLines, totalLines - offset);
            var height = Math.Max(
                MinimumRowHeight,
                (linesInChunk * BodyLineHeight) + (2 * CellPadding));
            DrawQuestionChunk(
                canvas,
                question,
                rowIndex,
                questionLines,
                answerLines,
                offset,
                linesInChunk,
                height,
                continuation,
                fonts);
            canvas.Y += height;
            offset += linesInChunk;
            continuation = true;
            if (offset < totalLines)
            {
                canvas = AddPage(document, pages, report, fonts);
                DrawTableHeader(canvas, fonts);
            }
        }
    }

    private static void DrawQuestionChunk(
        PageCanvas canvas,
        ResultReportQuestion question,
        int rowIndex,
        List<string> questionLines,
        List<string> answerLines,
        int offset,
        int lineCount,
        double height,
        bool continuation,
        ReportFonts fonts)
    {
        var widths = ColumnWidths();
        var background = question.IsCorrected
            ? CorrectedPale
            : rowIndex % 2 == 0
                ? XColors.White
                : AlternateRow;
        var x = MarginLeft;
        foreach (var width in widths)
        {
            canvas.Graphics.DrawRectangle(
                new XSolidBrush(background),
                x,
                canvas.Y,
                width,
                height);
            canvas.Graphics.DrawRectangle(
                new XPen(Border, 0.55),
                x,
                canvas.Y,
                width,
                height);
            x += width;
        }

        var label = continuation ? "続き" : question.DisplayLabel;
        canvas.Graphics.DrawString(
            label,
            fonts.TableBody,
            new XSolidBrush(Ink),
            new XRect(
                MarginLeft + 3,
                canvas.Y + CellPadding,
                LabelColumnWidth - 6,
                Math.Min(height - (2 * CellPadding), BodyLineHeight)),
            XStringFormats.TopCenter);

        DrawLineSlice(
            canvas.Graphics,
            questionLines,
            offset,
            lineCount,
            fonts.TableBody,
            new XSolidBrush(Ink),
            MarginLeft + LabelColumnWidth + CellPadding,
            canvas.Y + CellPadding);
        DrawLineSlice(
            canvas.Graphics,
            answerLines,
            offset,
            lineCount,
            fonts.TableBody,
            new XSolidBrush(Ink),
            MarginLeft + LabelColumnWidth + QuestionColumnWidth + CellPadding,
            canvas.Y + CellPadding);

        if (!continuation)
        {
            var score =
                $"{FormatPoints(question.AwardedPointsMilli)} / " +
                $"{FormatPoints(question.MaximumPointsMilli)}";
            var scoreX =
                MarginLeft + LabelColumnWidth + QuestionColumnWidth + AnswerColumnWidth;
            canvas.Graphics.DrawString(
                score,
                fonts.TableBody,
                new XSolidBrush(Ink),
                new XRect(scoreX + 3, canvas.Y, ScoreColumnWidth - 6, height),
                XStringFormats.Center);
            var outcomeX = scoreX + ScoreColumnWidth;
            var outcomeBrush = new XSolidBrush(OutcomeColor(question.Outcome));
            if (question.IsCorrected)
            {
                canvas.Graphics.DrawString(
                    LocalizeOutcome(question.Outcome),
                    fonts.TableBody,
                    outcomeBrush,
                    new XRect(
                        outcomeX + 2,
                        canvas.Y + (height / 2) - 15,
                        OutcomeColumnWidth - 4,
                        14),
                    XStringFormats.Center);
                canvas.Graphics.DrawString(
                    "訂正済",
                    fonts.Footer,
                    new XSolidBrush(XColors.DarkGoldenrod),
                    new XRect(
                        outcomeX + 2,
                        canvas.Y + (height / 2),
                        OutcomeColumnWidth - 4,
                        12),
                    XStringFormats.Center);
            }
            else
            {
                canvas.Graphics.DrawString(
                    LocalizeOutcome(question.Outcome),
                    fonts.TableBody,
                    outcomeBrush,
                    new XRect(
                        outcomeX + 2,
                        canvas.Y,
                        OutcomeColumnWidth - 4,
                        height),
                    XStringFormats.Center);
            }
        }
    }

    private static void DrawEmptyState(PageCanvas canvas, ReportFonts fonts)
    {
        const double height = 56;
        canvas.Graphics.DrawRectangle(
            new XSolidBrush(AlternateRow),
            MarginLeft,
            canvas.Y,
            PageWidth - MarginLeft - MarginRight,
            height);
        canvas.Graphics.DrawRectangle(
            new XPen(Border, 0.55),
            MarginLeft,
            canvas.Y,
            PageWidth - MarginLeft - MarginRight,
            height);
        canvas.Graphics.DrawString(
            "設問結果はありません。",
            fonts.Body,
            new XSolidBrush(MutedInk),
            new XRect(
                MarginLeft,
                canvas.Y,
                PageWidth - MarginLeft - MarginRight,
                height),
            XStringFormats.Center);
        canvas.Y += height;
    }

    private static void DrawClosingNote(
        PdfDocument document,
        List<PageCanvas> pages,
        ref PageCanvas canvas,
        ResultReportDocument report,
        ReportFonts fonts)
    {
        const double noteHeight = 34;
        if (canvas.Y + noteHeight > ContentBottom)
        {
            canvas = AddPage(document, pages, report, fonts);
        }

        canvas.Y += 12;
        canvas.Graphics.DrawString(
            "この帳票は確定済みの現在結果から作成されています。答案画像は含まれません。",
            fonts.Small,
            new XSolidBrush(MutedInk),
            new XRect(
                MarginLeft,
                canvas.Y,
                PageWidth - MarginLeft - MarginRight,
                18),
            XStringFormats.TopLeft);
    }

    private static void DrawFooters(
        IReadOnlyList<PageCanvas> pages,
        ResultReportDocument report,
        ReportFonts fonts)
    {
        for (var index = 0; index < pages.Count; index++)
        {
            var graphics = pages[index].Graphics;
            graphics.DrawLine(
                new XPen(Border, 0.6),
                MarginLeft,
                FooterTop - 5,
                PageWidth - MarginRight,
                FooterTop - 5);
            graphics.DrawString(
                $"帳票ID: {report.ReportId}",
                fonts.Footer,
                new XSolidBrush(MutedInk),
                new XRect(MarginLeft, FooterTop, 250, 14),
                XStringFormats.TopLeft);
            graphics.DrawString(
                $"作成: {report.GeneratedAt:yyyy-MM-dd HH:mm} UTC",
                fonts.Footer,
                new XSolidBrush(MutedInk),
                new XRect(MarginLeft + 175, FooterTop, 220, 14),
                XStringFormats.TopCenter);
            graphics.DrawString(
                $"{index + 1} / {pages.Count}",
                fonts.Footer,
                new XSolidBrush(MutedInk),
                new XRect(
                    PageWidth - MarginRight - 80,
                    FooterTop,
                    80,
                    14),
                XStringFormats.TopRight);
        }
    }

    private static List<string> WrapText(
        XGraphics graphics,
        string value,
        XFont font,
        double maximumWidth)
    {
        var normalized = NormalizeVisibleText(value);
        var lines = new List<string>();
        foreach (var paragraph in normalized.Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            var current = new StringBuilder();
            var enumerator = StringInfo.GetTextElementEnumerator(paragraph);
            while (enumerator.MoveNext())
            {
                var element = enumerator.GetTextElement();
                current.Append(element);
                if (graphics.MeasureString(current.ToString(), font).Width
                    <= maximumWidth)
                {
                    continue;
                }

                current.Length -= element.Length;
                if (current.Length > 0)
                {
                    lines.Add(current.ToString().TrimEnd());
                    current.Clear();
                }

                current.Append(element.TrimStart());
            }

            lines.Add(current.ToString());
        }

        return lines.Count == 0 ? [string.Empty] : lines;
    }

    private static string NormalizeVisibleText(string value)
    {
        var normalized = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\t', ' ')
            .Normalize(NormalizationForm.FormC)
            .Trim();
        var buffer = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (rune.Value == '\n'
                || !Rune.IsControl(rune))
            {
                buffer.Append(rune.ToString());
            }
        }

        return buffer.ToString();
    }

    private static void DrawLines(
        XGraphics graphics,
        List<string> lines,
        XFont font,
        XBrush brush,
        double x,
        double y,
        double lineHeight)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            graphics.DrawString(
                lines[index],
                font,
                brush,
                new XPoint(x, y + ((index + 1) * lineHeight) - 4));
        }
    }

    private static void DrawLineSlice(
        XGraphics graphics,
        List<string> lines,
        int offset,
        int count,
        XFont font,
        XBrush brush,
        double x,
        double y)
    {
        for (var index = 0; index < count; index++)
        {
            var sourceIndex = offset + index;
            if (sourceIndex >= lines.Count)
            {
                break;
            }

            graphics.DrawString(
                lines[sourceIndex],
                font,
                brush,
                new XPoint(x, y + ((index + 1) * BodyLineHeight) - 3));
        }
    }

    private static int MaximumLinesForHeight(double height)
    {
        var available = height - (2 * CellPadding);
        return available <= 0
            ? 0
            : Math.Max(0, (int)Math.Floor(available / BodyLineHeight));
    }

    private static double[] ColumnWidths() =>
        [
            LabelColumnWidth,
            QuestionColumnWidth,
            AnswerColumnWidth,
            ScoreColumnWidth,
            OutcomeColumnWidth,
        ];

    private static string FormatPoints(long milliPoints)
    {
        var whole = milliPoints / 1_000;
        var fraction = Math.Abs(milliPoints % 1_000);
        return fraction == 0
            ? whole.ToString(CultureInfo.InvariantCulture)
            : $"{whole.ToString(CultureInfo.InvariantCulture)}." +
                $"{fraction:000}".TrimEnd('0');
    }

    private static decimal ComputePercentage(long earned, long possible) =>
        possible <= 0
            ? 0
            : Math.Clamp((decimal)earned * 100m / possible, 0m, 100m);

    private static string LocalizeOutcome(string outcome)
    {
        return outcome switch
        {
            "correct" => "正解",
            "partial" => "部分点",
            "incorrect" => "不正解",
            "blank" => "空欄",
            "unreadable" => "判読不可",
            _ => throw new ArgumentOutOfRangeException(
                nameof(outcome),
                "Unsupported result outcome."),
        };
    }

    private static XColor OutcomeColor(string outcome) => outcome switch
    {
        "correct" => XColor.FromArgb(18, 118, 82),
        "partial" => XColor.FromArgb(157, 98, 4),
        "incorrect" => XColor.FromArgb(178, 52, 52),
        "blank" => MutedInk,
        "unreadable" => XColor.FromArgb(120, 80, 153),
        _ => Ink,
    };

    private static int VerifyPdf(byte[] bytes, int expectedPageCount)
    {
        if (bytes.Length < 8
            || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
        {
            throw new InvalidDataException(
                "The report renderer did not produce a PDF signature.");
        }

        using var input = new MemoryStream(bytes, writable: false);
        using var parsed = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        if (parsed.PageCount != expectedPageCount || parsed.PageCount < 1)
        {
            throw new InvalidDataException(
                "The rendered PDF page count could not be verified.");
        }

        return parsed.PageCount;
    }

    private static void SetDeterministicDocumentIdentifiers(
        PdfDocument document,
        ResultReportDocument report)
    {
        var seed = Encoding.UTF8.GetBytes(
            $"{report.ReportId}\0{report.GeneratedAt:O}\0" +
            ResultReportSourceHasher.Compute(report));
        var digest = Convert.ToHexString(SHA256.HashData(seed))
            .ToLowerInvariant();
        document.Internals.FirstDocumentID = digest[..32];
        document.Internals.SecondDocumentID = digest[32..];
    }

    private static byte[] CanonicalizePdf(
        byte[] bytes,
        ResultReportDocument report)
    {
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{report.ReportId}\0" +
            $"{report.GeneratedAt.ToString("O", CultureInfo.InvariantCulture)}\0" +
            ResultReportSourceHasher.Compute(report)));
        var subsetTag = string.Create(
            6,
            seed,
            static (destination, state) =>
            {
                for (var index = 0; index < destination.Length; index++)
                {
                    destination[index] = (char)('A' + (state[index] % 26));
                }
            });
        var firstGuid = new Guid(seed.AsSpan(0, 16))
            .ToString("D", CultureInfo.InvariantCulture);
        var secondGuid = new Guid(seed.AsSpan(16, 16))
            .ToString("D", CultureInfo.InvariantCulture);
        var syntax = Encoding.Latin1.GetString(bytes);
        syntax = Regex.Replace(
            syntax,
            "[A-Z]{6}(?=\\+Noto#20Sans#20JP#20Thin)",
            subsetTag,
            RegexOptions.CultureInvariant);
        syntax = Regex.Replace(
            syntax,
            "(?<=<xmpMM:DocumentID>uuid:)[0-9a-f-]{36}" +
            "(?=</xmpMM:DocumentID>)",
            firstGuid,
            RegexOptions.CultureInvariant);
        syntax = Regex.Replace(
            syntax,
            "(?<=<xmpMM:InstanceID>uuid:)[0-9a-f-]{36}" +
            "(?=</xmpMM:InstanceID>)",
            secondGuid,
            RegexOptions.CultureInvariant);
        var canonical = Encoding.Latin1.GetBytes(syntax);
        if (canonical.Length != bytes.Length)
        {
            throw new InvalidDataException(
                "PDF metadata canonicalization changed the document length.");
        }

        return canonical;
    }

    private static void Validate(ResultReportDocument report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Required(report.ReportId, nameof(report.ReportId), 200);
        Required(report.SchoolName, nameof(report.SchoolName), 200);
        Required(report.StudentDisplayName, nameof(report.StudentDisplayName), 200);
        Optional(report.StudentNumber, nameof(report.StudentNumber), 100);
        Required(report.TestTitle, nameof(report.TestTitle), 500);
        if (report.TemplateVersionNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(report),
                "The template version number must be positive.");
        }

        if (report.ResultRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(report),
                "The result revision must be positive.");
        }

        if (report.EarnedPointsMilli < 0
            || report.PossiblePointsMilli < 0
            || report.EarnedPointsMilli > report.PossiblePointsMilli)
        {
            throw new ArgumentOutOfRangeException(
                nameof(report),
                "The report total must be within its possible score.");
        }

        ArgumentNullException.ThrowIfNull(report.Questions);
        if (report.Questions.Count > MaximumQuestions)
        {
            throw new ArgumentOutOfRangeException(
                nameof(report),
                $"A report cannot contain more than {MaximumQuestions} questions.");
        }

        long earned = 0;
        long possible = 0;
        foreach (var question in report.Questions)
        {
            ArgumentNullException.ThrowIfNull(question);
            Required(question.DisplayLabel, nameof(question.DisplayLabel), 100);
            Required(question.QuestionText, nameof(question.QuestionText), 16_000);
            Optional(question.RecognizedAnswer, nameof(question.RecognizedAnswer), 16_000);
            Optional(question.TeacherComment, nameof(question.TeacherComment), 4_000);
            if (question.AwardedPointsMilli < 0
                || question.MaximumPointsMilli < 0
                || question.AwardedPointsMilli > question.MaximumPointsMilli)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(report),
                    "Question points must be within the maximum.");
            }

            if (!AllowedOutcomes.Contains(question.Outcome))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(report),
                    "A question outcome is unsupported.");
            }

            earned = checked(earned + question.AwardedPointsMilli);
            possible = checked(possible + question.MaximumPointsMilli);
        }

        if (earned != report.EarnedPointsMilli
            || possible != report.PossiblePointsMilli)
        {
            throw new ArgumentException(
                "Report totals must equal the question totals.",
                nameof(report));
        }
    }

    private static void Required(string value, string parameter, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new ArgumentException(
                $"{parameter} is required and must be at most {maximumLength} characters.",
                parameter);
        }
    }

    private static void Optional(
        string? value,
        string parameter,
        int maximumLength)
    {
        if (value?.Length > maximumLength)
        {
            throw new ArgumentException(
                $"{parameter} must be at most {maximumLength} characters.",
                parameter);
        }
    }

    private sealed class PageCanvas(
        PdfPage page,
        XGraphics graphics,
        double y)
    {
        public PdfPage Page { get; } = page;
        public XGraphics Graphics { get; } = graphics;
        public double Y { get; set; } = y;
    }

    private sealed record ReportFonts(
        XFont Title,
        XFont Score,
        XFont Body,
        XFont Small,
        XFont TableHeader,
        XFont TableBody,
        XFont Footer)
    {
        public static ReportFonts Create()
        {
            var options = new XPdfFontOptions(
                PdfFontEncoding.Unicode,
                PdfFontEmbedding.TryComputeSubset);
            return new ReportFonts(
                new XFont(
                    NotoSansJpFontResolver.FamilyName,
                    16,
                    XFontStyleEx.Regular,
                    options),
                new XFont(
                    NotoSansJpFontResolver.FamilyName,
                    20,
                    XFontStyleEx.Regular,
                    options),
                new XFont(
                    NotoSansJpFontResolver.FamilyName,
                    10,
                    XFontStyleEx.Regular,
                    options),
                new XFont(
                    NotoSansJpFontResolver.FamilyName,
                    8,
                    XFontStyleEx.Regular,
                    options),
                new XFont(
                    NotoSansJpFontResolver.FamilyName,
                    8.5,
                    XFontStyleEx.Regular,
                    options),
                new XFont(
                    NotoSansJpFontResolver.FamilyName,
                    8.5,
                    XFontStyleEx.Regular,
                    options),
                new XFont(
                    NotoSansJpFontResolver.FamilyName,
                    7,
                    XFontStyleEx.Regular,
                    options));
        }
    }
}
