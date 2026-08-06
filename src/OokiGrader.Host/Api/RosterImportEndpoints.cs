using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Domain.Grading;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Api;

internal static class RosterImportEndpoints
{
    private const long MaximumCsvBytes = 10 * 1024 * 1024;
    private const int MaximumRows = 20_000;

    public static IEndpointRouteBuilder MapRosterImportEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/roster-imports")
            .RequireAuthorization("teacher");
        group.MapPost("/", StageAsync);
        group.MapGet("/{importId}", GetPreview);
        group.MapPost("/{importId}:apply", ApplyAsync);
        group.MapGet("/{importId}/errors.csv", DownloadErrors);
        return endpoints;
    }

    private static async Task<IResult> StageAsync(
        HttpContext context,
        RosterImportStore store,
        OokiGraderDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status415UnsupportedMediaType,
                "CSV_FORM_REQUIRED",
                "CSV ファイルが必要です",
                "multipart/form-data で CSV ファイルを送信してください。");
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0 || file.Length > MaximumCsvBytes)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "CSV_SIZE_INVALID",
                "CSV ファイルを読み込めません",
                "空ではない 10 MiB 以下の CSV ファイルを選択してください。");
        }

        var requestedEncoding = form["encoding"].FirstOrDefault() ?? "auto";
        byte[] bytes;
        await using (var source = file.OpenReadStream())
        {
            using var memory = new MemoryStream((int)file.Length);
            await source.CopyToAsync(memory, cancellationToken);
            bytes = memory.ToArray();
        }

        string csv;
        string detectedEncoding;
        try
        {
            (csv, detectedEncoding) = DecodeCsv(bytes, requestedEncoding);
        }
        catch (DecoderFallbackException)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "CSV_ENCODING_INVALID",
                "文字コードを判定できません",
                "UTF-8（BOM 付き）または Shift_JIS の CSV を使用してください。");
        }

        IReadOnlyList<string>[] records;
        try
        {
            records = ParseCsv(csv);
        }
        catch (FormatException exception)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "CSV_FORMAT_INVALID",
                "CSV の形式が正しくありません",
                exception.Message);
        }

        if (records.Length < 2)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "CSV_HAS_NO_ROWS",
                "取り込める行がありません",
                "見出し行と 1 行以上の生徒データが必要です。");
        }

        if (records.Length - 1 > MaximumRows)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "CSV_TOO_MANY_ROWS",
                "CSV の行数が上限を超えています",
                $"1 回に取り込める生徒は {MaximumRows:N0} 名までです。");
        }

        var headers = records[0]
            .Select((value, index) => string.IsNullOrWhiteSpace(value)
                ? $"column_{index + 1}"
                : value.Trim())
            .ToArray();
        var mapping = ResolveColumns(headers);
        var missing = RequiredColumns
            .Where(column => !mapping.ContainsKey(column))
            .ToArray();
        if (missing.Length > 0)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "CSV_COLUMNS_MISSING",
                "必要な列がありません",
                $"必要な列: {string.Join("、", missing.Select(DisplayColumn))}");
        }

        var rows = new List<RosterImportRow>(records.Length - 1);
        var errors = new List<RosterImportError>();
        var duplicateNumbers = new Dictionary<string, int>(StringComparer.Ordinal);
        var skippedBlankRows = 0;
        for (var index = 1; index < records.Length; index++)
        {
            var values = records[index];
            if (values.All(string.IsNullOrWhiteSpace))
            {
                skippedBlankRows++;
                continue;
            }

            var rowNumber = index + 1;
            var row = ToRosterRow(values, mapping, rowNumber);
            rows.Add(row);
            ValidateRow(row, errors);
            if (row.StudentNumberNormalized.Length > 0)
            {
                if (duplicateNumbers.TryGetValue(
                        row.StudentNumberNormalized,
                        out var firstRow))
                {
                    errors.Add(new RosterImportError(
                        rowNumber,
                        $"生徒番号が {firstRow} 行目と重複しています。"));
                }
                else
                {
                    duplicateNumbers[row.StudentNumberNormalized] = rowNumber;
                }
            }
        }

        var numbers = rows
            .Select(row => row.StudentNumberNormalized)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var existingNumbers = await database.Students
            .AsNoTracking()
            .Where(student =>
                numbers.Contains(student.StudentNumberNormalized)
                && student.Status != "merged")
            .Select(student => student.StudentNumberNormalized)
            .ToHashSetAsync(StringComparer.Ordinal, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var staged = new RosterImportBatch(
            UlidId.New(now),
            SafeFileName(file.FileName),
            detectedEncoding,
            headers,
            rows,
            errors,
            rows.Count(row => !existingNumbers.Contains(row.StudentNumberNormalized)),
            rows.Count(row => existingNumbers.Contains(row.StudentNumberNormalized)),
            skippedBlankRows,
            now,
            now.AddHours(1));
        store.Put(staged);
        return Results.Created(
            $"/api/v1/roster-imports/{staged.ImportId}",
            ToPreview(staged));
    }

    private static IResult GetPreview(
        string importId,
        HttpContext context,
        RosterImportStore store)
    {
        if (!store.TryGet(importId, out var batch))
        {
            return ImportNotFound(context);
        }

        return Results.Ok(ToPreview(batch));
    }

    private static async Task<IResult> ApplyAsync(
        string importId,
        ApplyRosterImportRequest request,
        HttpContext context,
        RosterImportStore store,
        OokiGraderDbContext database,
        IAuditSink audit,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!store.TryGet(importId, out var batch))
        {
            return ImportNotFound(context);
        }

        if (batch.Errors.Count > 0)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "CSV_VALIDATION_FAILED",
                "エラーのある CSV は適用できません",
                "エラー一覧を確認し、CSV を修正してからやり直してください。");
        }

        if (batch.AppliedResult is not null)
        {
            return Results.Ok(batch.AppliedResult);
        }

        var strategy = request.Strategy ?? "create-update-skip";
        if (strategy is not ("create-update-skip" or "create-only" or "update-only"))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "CSV_STRATEGY_INVALID",
                "更新方法が正しくありません",
                "画面を読み込み直して更新方法を選び直してください。");
        }

        var numbers = batch.Rows
            .Select(row => row.StudentNumberNormalized)
            .ToArray();
        await using var transaction = await database.Database
            .BeginTransactionAsync(cancellationToken);
        var existing = await database.Students
            .Where(student =>
                numbers.Contains(student.StudentNumberNormalized)
                && student.Status != "merged")
            .ToDictionaryAsync(
                student => student.StudentNumberNormalized,
                StringComparer.Ordinal,
                cancellationToken);
        var now = timeProvider.GetUtcNow();
        var created = 0;
        var updated = 0;
        var skipped = batch.SkippedCount;
        foreach (var row in batch.Rows)
        {
            if (existing.TryGetValue(row.StudentNumberNormalized, out var student))
            {
                if (strategy == "create-only")
                {
                    skipped++;
                    continue;
                }

                ApplyRow(student, row);
                student.UpdatedAt = now;
                updated++;
                continue;
            }

            if (strategy == "update-only")
            {
                skipped++;
                continue;
            }

            var newStudent = new StudentEntity
            {
                Id = UlidId.New(now),
                CreatedAt = now,
                UpdatedAt = now,
                Status = "active",
            };
            ApplyRow(newStudent, row);
            database.Students.Add(newStudent);
            existing[row.StudentNumberNormalized] = newStudent;
            created++;
        }

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var result = new RosterImportResult(created, updated, skipped);
        store.MarkApplied(importId, result);
        await audit.AppendAsync(
            new AuditWrite(
                "roster.import.applied",
                "rosterImport",
                importId,
                "success",
                ApiHelpers.StaffId(context.User),
                context.TraceIdentifier,
                SafeMetadataJson:
                    $"{{\"created\":{created},\"updated\":{updated},\"skipped\":{skipped}}}"),
            cancellationToken);
        return Results.Ok(result);
    }

    private static IResult DownloadErrors(
        string importId,
        HttpContext context,
        RosterImportStore store)
    {
        if (!store.TryGet(importId, out var batch))
        {
            return ImportNotFound(context);
        }

        var csv = new StringBuilder("\uFEFF行,エラー\r\n");
        foreach (var error in batch.Errors)
        {
            csv.Append(error.Row.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .Append('"')
                .Append(error.Message.Replace("\"", "\"\"", StringComparison.Ordinal))
                .Append("\"\r\n");
        }

        return Results.File(
            Encoding.UTF8.GetBytes(csv.ToString()),
            "text/csv; charset=utf-8",
            "roster-import-errors.csv");
    }

    private static (string Text, string DetectedEncoding) DecodeCsv(
        byte[] bytes,
        string requestedEncoding)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (requestedEncoding == "shift-jis")
        {
            return (Encoding.GetEncoding(
                    932,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback)
                .GetString(bytes), "Shift_JIS");
        }

        var utf8 = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);
        if (requestedEncoding == "utf-8-bom")
        {
            return (utf8.GetString(RemoveUtf8Bom(bytes)), "UTF-8 (BOM)");
        }

        if (HasUtf8Bom(bytes))
        {
            return (utf8.GetString(bytes.AsSpan(3)), "UTF-8 (BOM)");
        }

        try
        {
            return (utf8.GetString(bytes), "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.GetEncoding(
                    932,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback)
                .GetString(bytes), "Shift_JIS");
        }
    }

    private static byte[] RemoveUtf8Bom(byte[] bytes) =>
        HasUtf8Bom(bytes)
            ? bytes[3..]
            : bytes;

    private static bool HasUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3
        && bytes[0] == 0xEF
        && bytes[1] == 0xBB
        && bytes[2] == 0xBF;

    private static IReadOnlyList<string>[] ParseCsv(string csv)
    {
        var records = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < csv.Length; index++)
        {
            var character = csv[index];
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < csv.Length && csv[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(character);
                }

                continue;
            }

            switch (character)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    if (index + 1 < csv.Length && csv[index + 1] == '\n')
                    {
                        index++;
                    }

                    row.Add(field.ToString());
                    field.Clear();
                    records.Add(row);
                    row = [];
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    records.Add(row);
                    row = [];
                    break;
                default:
                    field.Append(character);
                    break;
            }
        }

        if (quoted)
        {
            throw new FormatException("引用符が閉じられていないフィールドがあります。");
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            records.Add(row);
        }

        return records
            .Where(record => record.Count > 1 || record.Any(value => value.Length > 0))
            .ToArray();
    }

    private static Dictionary<string, int> ResolveColumns(string[] headers)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < headers.Length; index++)
        {
            var normalized = NormalizeHeader(headers[index]);
            foreach (var pair in HeaderAliases)
            {
                if (!result.ContainsKey(pair.Key) && pair.Value.Contains(normalized))
                {
                    result[pair.Key] = index;
                    break;
                }
            }
        }

        return result;
    }

    private static RosterImportRow ToRosterRow(
        IReadOnlyList<string> values,
        Dictionary<string, int> mapping,
        int rowNumber)
    {
        string Read(string key) =>
            mapping.TryGetValue(key, out var index) && index < values.Count
                ? values[index].Trim()
                : string.Empty;
        var studentNumber = Read("studentNumber");
        return new RosterImportRow(
            rowNumber,
            studentNumber,
            Normalize(studentNumber),
            Read("familyName"),
            Read("givenName"),
            Read("familyNameKana"),
            Read("givenNameKana"),
            Read("displayName"),
            Read("gradeLabel"),
            Read("schoolClass"),
            Read("course"),
            Read("notes"));
    }

    private static void ValidateRow(
        RosterImportRow row,
        List<RosterImportError> errors)
    {
        if (string.IsNullOrWhiteSpace(row.StudentNumber))
        {
            errors.Add(new RosterImportError(row.RowNumber, "生徒番号が空です。"));
        }

        if (string.IsNullOrWhiteSpace(row.FamilyName)
            || string.IsNullOrWhiteSpace(row.GivenName))
        {
            errors.Add(new RosterImportError(row.RowNumber, "姓と名は必須です。"));
        }

        if (string.IsNullOrWhiteSpace(row.FamilyNameKana)
            || string.IsNullOrWhiteSpace(row.GivenNameKana))
        {
            errors.Add(new RosterImportError(row.RowNumber, "姓カナと名カナは必須です。"));
        }

        if (row.StudentNumber.Length > 200
            || row.FamilyName.Length > 200
            || row.GivenName.Length > 200
            || row.FamilyNameKana.Length > 200
            || row.GivenNameKana.Length > 200)
        {
            errors.Add(new RosterImportError(row.RowNumber, "入力値が長すぎます。"));
        }
    }

    private static void ApplyRow(StudentEntity student, RosterImportRow row)
    {
        student.StudentNumber = row.StudentNumber;
        student.StudentNumberNormalized = row.StudentNumberNormalized;
        student.FamilyName = row.FamilyName;
        student.GivenName = row.GivenName;
        student.FamilyNameKana = row.FamilyNameKana;
        student.GivenNameKana = row.GivenNameKana;
        student.FamilyNameNormalized = Normalize(row.FamilyName);
        student.GivenNameNormalized = Normalize(row.GivenName);
        student.FamilyNameKanaNormalized = Normalize(row.FamilyNameKana);
        student.GivenNameKanaNormalized = Normalize(row.GivenNameKana);
        student.DisplayName = string.IsNullOrWhiteSpace(row.DisplayName)
            ? $"{row.FamilyName} {row.GivenName}"
            : row.DisplayName;
        student.GradeLabel = NullIfEmpty(row.GradeLabel);
        student.SchoolClass = NullIfEmpty(row.SchoolClass);
        student.Course = NullIfEmpty(row.Course);
        student.PrivateNotes = NullIfEmpty(row.Notes);
    }

    private static object ToPreview(RosterImportBatch batch) =>
        new
        {
            importId = batch.ImportId,
            fileName = batch.FileName,
            detectedEncoding = batch.DetectedEncoding,
            headers = batch.Headers,
            sampleRows = batch.Rows.Take(10).Select(row => new Dictionary<string, string>
            {
                ["生徒番号"] = row.StudentNumber,
                ["姓"] = row.FamilyName,
                ["名"] = row.GivenName,
                ["姓カナ"] = row.FamilyNameKana,
                ["名カナ"] = row.GivenNameKana,
                ["クラス"] = row.SchoolClass,
            }),
            createCount = batch.CreateCount,
            updateCount = batch.UpdateCount,
            skipCount = batch.SkippedCount,
            errorCount = batch.Errors.Count,
            errors = batch.Errors,
            createdAt = batch.CreatedAt,
        };

    private static IResult ImportNotFound(HttpContext context) =>
        ApiHelpers.Problem(
            context,
            StatusCodes.Status404NotFound,
            "ROSTER_IMPORT_NOT_FOUND",
            "取り込みデータが見つかりません",
            "取り込みデータの有効期限が切れています。CSV を選び直してください。");

    private static string Normalize(string? value) =>
        JapaneseTextNormalizer.NormalizeForComparison(
            value,
            new JapaneseNormalizationOptions { RemoveAllWhitespace = true });

    private static string NormalizeHeader(string value) =>
        value.Trim().Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

    private static string SafeFileName(string value)
    {
        var name = Path.GetFileName(value);
        return name.Length <= 255 ? name : name[..255];
    }

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string DisplayColumn(string key) => key switch
    {
        "studentNumber" => "生徒番号",
        "familyName" => "姓",
        "givenName" => "名",
        "familyNameKana" => "姓カナ",
        "givenNameKana" => "名カナ",
        _ => key,
    };

    private static readonly string[] RequiredColumns =
    [
        "studentNumber",
        "familyName",
        "givenName",
        "familyNameKana",
        "givenNameKana",
    ];

    private static readonly IReadOnlyDictionary<string, HashSet<string>> HeaderAliases =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["studentNumber"] = ["studentnumber", "生徒番号", "学籍番号", "番号"],
            ["familyName"] = ["familyname", "lastname", "姓", "名字"],
            ["givenName"] = ["givenname", "firstname", "名"],
            ["familyNameKana"] = ["familynamekana", "lastnamekana", "姓カナ", "姓かな"],
            ["givenNameKana"] = ["givennamekana", "firstnamekana", "名カナ", "名かな"],
            ["displayName"] = ["displayname", "表示名"],
            ["gradeLabel"] = ["gradelabel", "学年"],
            ["schoolClass"] = ["schoolclass", "class", "クラス", "組"],
            ["course"] = ["course", "コース"],
            ["notes"] = ["notes", "備考", "メモ"],
        };

    private sealed record ApplyRosterImportRequest(string? Strategy);
}

internal sealed class RosterImportStore(TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<string, RosterImportBatch> _batches =
        new(StringComparer.Ordinal);

    public void Put(RosterImportBatch batch)
    {
        RemoveExpired();
        _batches[batch.ImportId] = batch;
    }

    public bool TryGet(string id, out RosterImportBatch batch)
    {
        RemoveExpired();
        return _batches.TryGetValue(id, out batch!);
    }

    public void MarkApplied(string id, RosterImportResult result)
    {
        if (_batches.TryGetValue(id, out var batch))
        {
            _batches[id] = batch with { AppliedResult = result };
        }
    }

    private void RemoveExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var item in _batches)
        {
            if (item.Value.ExpiresAt <= now)
            {
                _batches.TryRemove(item.Key, out _);
            }
        }
    }
}

internal sealed record RosterImportBatch(
    string ImportId,
    string FileName,
    string DetectedEncoding,
    IReadOnlyList<string> Headers,
    IReadOnlyList<RosterImportRow> Rows,
    IReadOnlyList<RosterImportError> Errors,
    int CreateCount,
    int UpdateCount,
    int SkippedCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    RosterImportResult? AppliedResult = null);

internal sealed record RosterImportRow(
    int RowNumber,
    string StudentNumber,
    string StudentNumberNormalized,
    string FamilyName,
    string GivenName,
    string FamilyNameKana,
    string GivenNameKana,
    string DisplayName,
    string GradeLabel,
    string SchoolClass,
    string Course,
    string Notes);

internal sealed record RosterImportError(int Row, string Message);

internal sealed record RosterImportResult(int Created, int Updated, int Skipped);
