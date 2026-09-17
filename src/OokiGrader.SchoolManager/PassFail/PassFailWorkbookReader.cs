using System.Globalization;
using System.Text;
using ExcelDataReader;

namespace OokiGrader.SchoolManager.PassFail;

public sealed class PassFailWorkbookException(
    string code,
    string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class PassFailWorkbookReader
{
    private const int MaximumRows = 10_000;
    private const int MaximumColumns = 500;

    static PassFailWorkbookReader()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static PassFailSourceTable Read(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
        {
            throw new ArgumentException("The workbook stream must be readable.", nameof(input));
        }

        using var reader = ExcelReaderFactory.CreateReader(
            input,
            new ExcelReaderConfiguration { LeaveOpen = true });
        do
        {
            var rows = ReadSheet(reader);
            var table = TryCreateTable(rows);
            if (table is not null)
            {
                return table;
            }
        }
        while (reader.NextResult());

        throw new PassFailWorkbookException(
            "pass_fail_header_missing",
            "No worksheet contained student ID and class columns.");
    }

    private static List<string[]> ReadSheet(IExcelDataReader reader)
    {
        var rows = new List<string[]>();
        while (reader.Read())
        {
            if (rows.Count >= MaximumRows)
            {
                throw new PassFailWorkbookException(
                    "pass_fail_row_limit_exceeded",
                    "The workbook contains too many rows.");
            }

            if (reader.FieldCount > MaximumColumns)
            {
                throw new PassFailWorkbookException(
                    "pass_fail_column_limit_exceeded",
                    "The workbook contains too many columns.");
            }

            var row = new string[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++)
            {
                row[index] = FormatCell(reader.GetValue(index));
            }

            rows.Add(row);
        }

        return rows;
    }

    private static PassFailSourceTable? TryCreateTable(List<string[]> rows)
    {
        for (var headerIndex = 0; headerIndex < rows.Count; headerIndex++)
        {
            var header = rows[headerIndex];
            var studentIdIndexes = FindColumns(header, IsStudentIdHeader);
            var classIndexes = FindColumns(header, IsClassHeader);
            if (studentIdIndexes.Length == 0 || classIndexes.Length == 0)
            {
                continue;
            }

            var gradeIndexes = FindColumns(header, IsGradeHeader);
            var sensitiveIndexes = FindColumns(header, IsSensitiveHeader);
            var identityIndexes = FindColumns(header, IsIdentityHeader);
            var excludedIndexes = identityIndexes
                .Concat(classIndexes)
                .Concat(gradeIndexes)
                .Concat(sensitiveIndexes)
                .ToHashSet();
            var includedIndexes = Enumerable.Range(0, header.Length)
                .Where(index => !excludedIndexes.Contains(index))
                .Where(index => !string.IsNullOrWhiteSpace(header[index]))
                .ToArray();
            if (includedIndexes.Length == 0)
            {
                throw new PassFailWorkbookException(
                    "pass_fail_columns_missing",
                    "The pass/fail table has no distributable columns.");
            }

            var title = rows.Take(headerIndex)
                .SelectMany(row => row)
                .Select(value => value.Trim())
                .FirstOrDefault(value => value.Length > 0)
                ?? "合否表";
            var titleGrade = PassFailGrade.InferFromText(title);
            var resultRows = new List<PassFailSourceRow>();
            var sensitiveTokens = new HashSet<string>(StringComparer.Ordinal);
            for (var rowIndex = headerIndex + 1; rowIndex < rows.Count; rowIndex++)
            {
                var row = rows[rowIndex];
                var studentNumber = Cell(row, studentIdIndexes[0]);
                var classLabel = Cell(row, classIndexes[0]);
                if (studentNumber.Length == 0 || classLabel.Length == 0)
                {
                    continue;
                }

                foreach (var index in identityIndexes.Concat(sensitiveIndexes))
                {
                    var token = Cell(row, index);
                    if (token.Length > 0)
                    {
                        sensitiveTokens.Add(token);
                    }
                }

                var gradeKey = ResolveRowGrade(row, gradeIndexes, titleGrade);
                if (gradeKey.Length == 0)
                {
                    throw new PassFailWorkbookException(
                        "pass_fail_grade_missing",
                        "Each pass/fail row must have one unambiguous grade.");
                }

                var values = includedIndexes
                    .Select(index => Cell(row, index))
                    .ToArray();
                if (values.Any(value => !IsSafeResultValue(value)))
                {
                    throw new PassFailWorkbookException(
                        "pass_fail_value_unsafe",
                        "Pass/fail result cells may contain only bounded result marks or numbers.");
                }

                resultRows.Add(new PassFailSourceRow(
                    studentNumber,
                    gradeKey,
                    classLabel,
                    values));
            }

            if (resultRows.Count == 0)
            {
                throw new PassFailWorkbookException(
                    "pass_fail_rows_missing",
                    "The pass/fail table has no student rows.");
            }

            return new PassFailSourceTable(
                title,
                includedIndexes.Select(index => header[index].Trim()).ToArray(),
                resultRows,
                sensitiveTokens.ToArray());
        }

        return null;
    }

    private static int[] FindColumns(
        string[] header,
        Func<string, bool> predicate) => header
        .Select((value, index) => new { value, index })
        .Where(item => predicate(item.value))
        .Select(item => item.index)
        .ToArray();

    private static string ResolveRowGrade(
        string[] row,
        IReadOnlyList<int> gradeIndexes,
        string titleGrade)
    {
        var populatedGradeCells = gradeIndexes
            .Select(index => Cell(row, index))
            .Where(value => value.Length > 0)
            .ToArray();
        if (populatedGradeCells.Length == 0)
        {
            return titleGrade;
        }

        var rowGrades = populatedGradeCells
            .Select(PassFailGrade.Normalize)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (rowGrades.Length != 1 || rowGrades[0].Length == 0)
        {
            return string.Empty;
        }

        return rowGrades[0];
    }

    private static bool IsStudentIdHeader(string value)
    {
        var normalized = NormalizeHeader(value);
        return normalized is "四谷大塚ID" or "生徒ID" or "生徒番号"
            || normalized.EndsWith("ユーザーコード", StringComparison.Ordinal);
    }

    private static bool IsIdentityHeader(string value)
    {
        var normalized = NormalizeHeader(value);
        return IsStudentIdHeader(value)
            || normalized.Contains("ID", StringComparison.Ordinal)
            || normalized.Contains("番号", StringComparison.Ordinal)
            || normalized.Contains("コード", StringComparison.Ordinal)
            || normalized.Contains("学籍", StringComparison.Ordinal)
            || normalized.Contains("出席", StringComparison.Ordinal)
            || normalized.Contains("受験", StringComparison.Ordinal)
            || normalized is "NO" or "NO.";
    }

    private static bool IsClassHeader(string value)
    {
        var normalized = NormalizeHeader(value);
        return normalized is "クラス" or "所属クラス";
    }

    private static bool IsGradeHeader(string value)
    {
        var normalized = NormalizeHeader(value);
        return normalized is "学年" or "在籍学年";
    }

    private static bool IsSensitiveHeader(string value)
    {
        var normalized = NormalizeHeader(value);
        return normalized.Contains("氏名", StringComparison.Ordinal)
            || normalized.Contains("名前", StringComparison.Ordinal)
            || normalized.Contains("生徒名", StringComparison.Ordinal)
            || normalized.Contains("カナ", StringComparison.Ordinal)
            || normalized.Contains("フリガナ", StringComparison.Ordinal);
    }

    private static bool IsSafeResultValue(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim();
        if (normalized.Length == 0
            || normalized is "○" or "◎" or "●" or "×" or "△" or "▲" or "▽"
                or "合格" or "不合格" or "未受験" or "欠席")
        {
            return true;
        }

        if (normalized.Length > 8
            || !decimal.TryParse(
                normalized.TrimEnd('%'),
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var number))
        {
            return false;
        }

        return number is >= -999 and <= 999;
    }

    private static string NormalizeHeader(string value) =>
        string.Concat(value.Normalize(NormalizationForm.FormKC)
            .Where(character => !char.IsWhiteSpace(character)))
        .ToUpperInvariant();

    private static string Cell(string[] row, int index) =>
        index >= 0 && index < row.Length ? row[index].Trim() : string.Empty;

    private static string FormatCell(object? value) => value switch
    {
        null => string.Empty,
        double number when Math.Abs(number % 1) < double.Epsilon =>
            number.ToString("0", CultureInfo.InvariantCulture),
        double number => number.ToString("G15", CultureInfo.InvariantCulture),
        float number => number.ToString("G9", CultureInfo.InvariantCulture),
        DateTime date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool boolean => boolean ? "TRUE" : "FALSE",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}
