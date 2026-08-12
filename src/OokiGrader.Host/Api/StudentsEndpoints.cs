using System.Security.Claims;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Identifiers;
using OokiGrader.Contracts;
using OokiGrader.Domain.Grading;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Api;

public static class StudentsEndpoints
{
    private const string StudentsListRoute = "GET:/api/v1/students";

    public static IEndpointRouteBuilder MapStudentsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/students")
            .WithTags("Students")
            .RequireAuthorization("results")
            .RequireRateLimiting("search");

        group.MapGet("/", ListStudents);
        group.MapPost("/", CreateStudent).RequireAuthorization("teacher");
        group.MapGet("/{studentId}", GetStudent);
        group.MapPatch("/{studentId}", UpdateStudent).RequireAuthorization("teacher");
        group.MapPost("/{studentId}:deactivate", DeactivateStudent)
            .RequireAuthorization("teacher");
        group.MapPost("/{studentId}:reactivate", ReactivateStudent)
            .RequireAuthorization("teacher");
        group.MapGet("/{studentId}/aliases", ListAliases);
        group.MapPost("/{studentId}/aliases", CreateAlias)
            .RequireAuthorization("teacher");
        group.MapDelete("/{studentId}/aliases/{aliasId}", DeleteAlias)
            .RequireAuthorization("teacher");
        group.MapGet("/{studentId}/progress", GetProgress)
            .RequireAuthorization("results");
        return endpoints;
    }

    [SuppressMessage(
        "Globalization",
        "CA1309:Use ordinal string comparison",
        Justification =
            "EF Core translates this predicate to SQLite BINARY collation but cannot translate CompareOrdinal.")]
    private static async Task<IResult> ListStudents(
        HttpContext context,
        OokiGraderDbContext db,
        string? search,
        string? status,
        bool? active,
        string? @class,
        string? course,
        string? grade,
        string? sort,
        bool? includeFacets,
        string? cursor,
        int? pageSize,
        int? limit,
        ProtectedCursorCodec cursorCodec,
        CancellationToken cancellationToken)
    {
        if (!ListQuery.TryPageSize(
                context,
                pageSize,
                limit,
                out var take,
                out var pageSizeError))
        {
            return pageSizeError!;
        }

        var query = db.Students.AsNoTracking();
        var normalizedStatus = CursorPagination.TrimToNull(status);
        if (normalizedStatus is not null
            && normalizedStatus is not ("active" or "inactive"))
        {
            return ListQuery.Invalid(
                context,
                "status は active または inactive を指定してください。");
        }

        var statusFromActive = active.HasValue
            ? active.Value ? "active" : "inactive"
            : null;
        if (normalizedStatus is not null
            && statusFromActive is not null
            && normalizedStatus != statusFromActive)
        {
            return ListQuery.Invalid(
                context,
                "status と active には同じ在籍状態を指定してください。");
        }

        normalizedStatus ??= statusFromActive;

        if (normalizedStatus is not null)
        {
            query = query.Where(student => student.Status == normalizedStatus);
        }

        if (!ListQuery.TryTrimFilter(
                context,
                @class,
                "class",
                out var classLabel,
                out var filterError)
            || !ListQuery.TryTrimFilter(
                context,
                course,
                "course",
                out var normalizedCourse,
                out filterError)
            || !ListQuery.TryTrimFilter(
                context,
                grade,
                "grade",
                out var normalizedGrade,
                out filterError))
        {
            return filterError!;
        }

        if (classLabel is not null)
        {
            query = query.Where(student => student.SchoolClass == classLabel);
        }

        if (normalizedCourse is not null)
        {
            query = query.Where(student => student.Course == normalizedCourse);
        }

        if (normalizedGrade is not null)
        {
            query = query.Where(student => student.GradeLabel == normalizedGrade);
        }

        if (!ListQuery.TryNormalizeSearch(
                context,
                search,
                out var normalizedSearch,
                out var searchTokens,
                out var searchError))
        {
            return searchError!;
        }

        foreach (var token in searchTokens)
        {
            var pattern = ListQuery.ContainsPattern(token);
            query = query.Where(student =>
                EF.Functions.Like(
                    student.StudentNumberNormalized,
                    pattern,
                    "\\")
                || EF.Functions.Like(
                    student.FamilyNameNormalized,
                    pattern,
                    "\\")
                || EF.Functions.Like(
                    student.GivenNameNormalized,
                    pattern,
                    "\\")
                || EF.Functions.Like(
                    student.FamilyNameNormalized
                        + student.GivenNameNormalized,
                    pattern,
                    "\\")
                || (student.FamilyNameKanaNormalized != null
                    && EF.Functions.Like(
                        student.FamilyNameKanaNormalized,
                        pattern,
                        "\\"))
                || (student.GivenNameKanaNormalized != null
                    && EF.Functions.Like(
                        student.GivenNameKanaNormalized,
                        pattern,
                        "\\"))
                || EF.Functions.Like(
                    (student.FamilyNameKanaNormalized ?? string.Empty)
                        + (student.GivenNameKanaNormalized ?? string.Empty),
                    pattern,
                    "\\")
                || student.Aliases.Any(alias =>
                    EF.Functions.Like(
                        alias.NormalizedValue,
                        pattern,
                        "\\")));
        }

        var normalizedSort = CursorPagination.TrimToNull(sort)
            ?? "studentNumber";
        if (normalizedSort is not (
            "studentNumber"
            or "-studentNumber"
            or "name"
            or "-name"
            or "updatedAt"
            or "-updatedAt"))
        {
            return ListQuery.Invalid(
                context,
                "sort は studentNumber、name、updatedAt のいずれかに、必要なら先頭の - を付けて指定してください。");
        }

        var cursorSort = normalizedSort switch
        {
            "studentNumber" => "studentNumberNormalized,id",
            "-studentNumber" => "-studentNumberNormalized,id",
            "name" => "nameNormalized,id",
            "-name" => "-nameNormalized,id",
            "updatedAt" => "updatedAt,id",
            _ => "-updatedAt,id",
        };
        var filterBinding = CursorPagination.Bind(
            ("class", classLabel),
            ("course", normalizedCourse),
            ("grade", normalizedGrade),
            ("search", normalizedSearch),
            ("sort", cursorSort),
            ("status", normalizedStatus));
        if (!CursorPagination.TryRead(
                context,
                cursorCodec,
                cursor,
                StudentsListRoute,
                filterBinding,
                out StudentCursorPosition position,
                out var cursorError))
        {
            return cursorError!;
        }

        if (position is not null
            && (string.IsNullOrEmpty(position.Id)
                || position.Id.Length > ListQuery.MaximumIdLength
                || (normalizedSort is "updatedAt" or "-updatedAt"
                    ? position.Timestamp is null
                    : string.IsNullOrEmpty(position.Text)
                        || position.Text.Length > 1_000)))
        {
            return CursorPagination.Invalid(context);
        }

        var total = await query.CountAsync(cancellationToken);
        if (position is not null)
        {
            query = normalizedSort switch
            {
                "studentNumber" => query.Where(student =>
                    string.Compare(
                        student.StudentNumberNormalized,
                        position.Text) > 0
                    || (student.StudentNumberNormalized == position.Text
                        && string.Compare(student.Id, position.Id) > 0)),
                "-studentNumber" => query.Where(student =>
                    string.Compare(
                        student.StudentNumberNormalized,
                        position.Text) < 0
                    || (student.StudentNumberNormalized == position.Text
                        && string.Compare(student.Id, position.Id) > 0)),
                "name" => query.Where(student =>
                    string.Compare(
                        student.FamilyNameNormalized + student.GivenNameNormalized,
                        position.Text) > 0
                    || (student.FamilyNameNormalized + student.GivenNameNormalized
                            == position.Text
                        && string.Compare(student.Id, position.Id) > 0)),
                "-name" => query.Where(student =>
                    string.Compare(
                        student.FamilyNameNormalized + student.GivenNameNormalized,
                        position.Text) < 0
                    || (student.FamilyNameNormalized + student.GivenNameNormalized
                            == position.Text
                        && string.Compare(student.Id, position.Id) > 0)),
                "updatedAt" => query.Where(student =>
                    student.UpdatedAt > position.Timestamp
                    || (student.UpdatedAt == position.Timestamp
                        && string.Compare(student.Id, position.Id) > 0)),
                _ => query.Where(student =>
                    student.UpdatedAt < position.Timestamp
                    || (student.UpdatedAt == position.Timestamp
                        && string.Compare(student.Id, position.Id) > 0)),
            };
        }

        IOrderedQueryable<StudentEntity> ordered = normalizedSort switch
        {
            "studentNumber" => query.OrderBy(
                student => student.StudentNumberNormalized),
            "-studentNumber" => query.OrderByDescending(
                student => student.StudentNumberNormalized),
            "name" => query.OrderBy(student =>
                student.FamilyNameNormalized + student.GivenNameNormalized),
            "-name" => query.OrderByDescending(student =>
                student.FamilyNameNormalized + student.GivenNameNormalized),
            "updatedAt" => query.OrderBy(student => student.UpdatedAt),
            _ => query.OrderByDescending(student => student.UpdatedAt),
        };
        var students = await ordered
            .ThenBy(student => student.Id)
            .Take(take + 1)
            .Select(student => new
            {
                student.Id,
                student.StudentNumber,
                student.DisplayName,
                student.FamilyName,
                student.GivenName,
                student.FamilyNameKana,
                student.GivenNameKana,
                kana = (student.FamilyNameKana ?? string.Empty)
                    + (student.GivenNameKana ?? string.Empty),
                student.GradeLabel,
                classLabel = student.SchoolClass,
                student.Course,
                enrollmentStatus = student.Status,
                active = student.Status == "active",
                student.Revision,
                student.UpdatedAt,
            })
            .ToListAsync(cancellationToken);
        var hasMore = students.Count > take;
        if (hasMore)
        {
            students.RemoveAt(take);
        }

        var nextCursor = students.Count == 0
            ? null
            : CursorPagination.Next(
                cursorCodec,
                StudentsListRoute,
                filterBinding,
                hasMore,
                new StudentCursorPosition(
                    normalizedSort switch
                    {
                        "studentNumber" or "-studentNumber" =>
                            NormalizeSearch(students[^1].StudentNumber),
                        "name" or "-name" =>
                            NormalizeSearch(students[^1].FamilyName)
                                + NormalizeSearch(students[^1].GivenName),
                        _ => null,
                    },
                    normalizedSort is "updatedAt" or "-updatedAt"
                        ? students[^1].UpdatedAt
                        : null,
                    students[^1].Id));
        var facets = includeFacets == true
            ? await LoadStudentFacetsAsync(db, cancellationToken)
            : null;

        return Results.Ok(new
        {
            items = students,
            nextCursor,
            totalApproximate = total,
            facets,
        });
    }

    private sealed record StudentCursorPosition(
        string? Text,
        DateTimeOffset? Timestamp,
        string Id);

    private static async Task<object> LoadStudentFacetsAsync(
        OokiGraderDbContext db,
        CancellationToken cancellationToken)
    {
        var gradeRows = await db.Students
            .AsNoTracking()
            .Where(student => student.GradeLabel != null
                && student.GradeLabel != string.Empty
                && student.GradeLabel.Length <= ListQuery.MaximumFilterLength)
            .GroupBy(student => student.GradeLabel!)
            .Select(group => new { Value = group.Key, Count = group.Count() })
            .OrderBy(item => item.Value)
            .Take(ListQuery.MaximumFacetValues)
            .ToArrayAsync(cancellationToken);
        var grades = gradeRows
            .Select(item => new FacetValue(item.Value, item.Value, item.Count))
            .ToArray();
        var classRows = await db.Students
            .AsNoTracking()
            .Where(student => student.SchoolClass != null
                && student.SchoolClass != string.Empty
                && student.SchoolClass.Length <= ListQuery.MaximumFilterLength)
            .GroupBy(student => student.SchoolClass!)
            .Select(group => new { Value = group.Key, Count = group.Count() })
            .OrderBy(item => item.Value)
            .Take(ListQuery.MaximumFacetValues)
            .ToArrayAsync(cancellationToken);
        var classes = classRows
            .Select(item => new FacetValue(item.Value, item.Value, item.Count))
            .ToArray();
        var courseRows = await db.Students
            .AsNoTracking()
            .Where(student => student.Course != null
                && student.Course != string.Empty
                && student.Course.Length <= ListQuery.MaximumFilterLength)
            .GroupBy(student => student.Course!)
            .Select(group => new { Value = group.Key, Count = group.Count() })
            .OrderBy(item => item.Value)
            .Take(ListQuery.MaximumFacetValues)
            .ToArrayAsync(cancellationToken);
        var courses = courseRows
            .Select(item => new FacetValue(item.Value, item.Value, item.Count))
            .ToArray();
        return new { grades, classes, courses };
    }

    private sealed record FacetValue(string Value, string Label, int Count);

    private static async Task<IResult> GetStudent(
        string studentId,
        HttpContext context,
        ClaimsPrincipal principal,
        OokiGraderDbContext db,
        CancellationToken cancellationToken)
    {
        var student = await db.Students
            .AsNoTracking()
            .Include(item => item.Aliases)
            .SingleOrDefaultAsync(item => item.Id == studentId, cancellationToken);
        if (student is null)
        {
            return Results.NotFound();
        }

        ApiHelpers.SetRevisionEtag(context.Response, student.Revision);
        var mayReadPrivateNotes = principal.IsInRole("administrator")
            || principal.IsInRole("teacher");
        return Results.Ok(new
        {
            student.Id,
            student.StudentNumber,
            student.DisplayName,
            student.FamilyName,
            student.GivenName,
            student.FamilyNameKana,
            student.GivenNameKana,
            kana = (student.FamilyNameKana ?? string.Empty)
                + (student.GivenNameKana ?? string.Empty),
            student.GradeLabel,
            classLabel = student.SchoolClass,
            student.Course,
            enrollmentStatus = student.Status,
            active = student.Status == "active",
            notes = mayReadPrivateNotes ? student.PrivateNotes : null,
            aliases = student.Aliases
                .OrderBy(alias => alias.CreatedAt)
                .Select(alias => new
                {
                    alias.Id,
                    text = alias.DisplayValue,
                    aliasType = alias.AliasType,
                    normalizedText = alias.NormalizedValue,
                }),
            student.CreatedAt,
            student.UpdatedAt,
            student.Revision,
        });
    }

    private static async Task<IResult> CreateStudent(
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] CreateStudentRequest request,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var validation = ValidateStudent(
            request.StudentNumber,
            request.FamilyName,
            request.GivenName,
            request.FamilyNameKana,
            request.GivenNameKana,
            request.DisplayName);
        if (validation is not null)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "STUDENT_INVALID",
                "生徒情報を保存できません",
                "入力内容を確認してください。",
                validation);
        }

        var normalizedNumber = NormalizeSearch(request.StudentNumber);
        if (await db.Students.AnyAsync(
            student => student.StudentNumberNormalized == normalizedNumber
                && student.Status != "merged",
            cancellationToken))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "STUDENT_NUMBER_DUPLICATE",
                "生徒番号が重複しています",
                "同じ生徒番号の在籍・非在籍レコードがあります。");
        }

        var now = timeProvider.GetUtcNow();
        var student = new StudentEntity
        {
            Id = UlidId.New(now),
            StudentNumber = request.StudentNumber.Trim(),
            StudentNumberNormalized = normalizedNumber,
            FamilyName = request.FamilyName.Trim(),
            GivenName = request.GivenName.Trim(),
            FamilyNameKana = request.FamilyNameKana.Trim(),
            GivenNameKana = request.GivenNameKana.Trim(),
            FamilyNameNormalized = NormalizeSearch(request.FamilyName),
            GivenNameNormalized = NormalizeSearch(request.GivenName),
            FamilyNameKanaNormalized = NormalizeSearch(request.FamilyNameKana),
            GivenNameKanaNormalized = NormalizeSearch(request.GivenNameKana),
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
                ? $"{request.FamilyName.Trim()} {request.GivenName.Trim()}"
                : request.DisplayName.Trim(),
            GradeLabel = TrimOrNull(request.GradeLabel),
            Course = TrimOrNull(request.Course),
            SchoolClass = TrimOrNull(request.SchoolClass),
            PrivateNotes = TrimOrNull(request.Notes),
            Status = "active",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Students.Add(student);
        AddAudit(
            db,
            now,
            principal,
            context,
            "student.created",
            student.Id);
        await db.SaveChangesAsync(cancellationToken);

        ApiHelpers.SetRevisionEtag(context.Response, student.Revision);
        return Results.Created($"/api/v1/students/{student.Id}", new
        {
            student.Id,
            student.StudentNumber,
            student.DisplayName,
            enrollmentStatus = student.Status,
            active = true,
            student.Revision,
        });
    }

    private static async Task<IResult> UpdateStudent(
        string studentId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] UpdateStudentRequest request,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!ApiHelpers.TryReadExpectedRevision(
            context.Request,
            request.Revision,
            out var expectedRevision))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status428PreconditionRequired,
                "REVISION_REQUIRED",
                "更新条件が必要です",
                "画面を再読み込みしてから、もう一度お試しください。");
        }

        var student = await db.Students.SingleOrDefaultAsync(
            item => item.Id == studentId,
            cancellationToken);
        if (student is null)
        {
            return Results.NotFound();
        }

        if (student.Revision != expectedRevision)
        {
            ApiHelpers.SetRevisionEtag(context.Response, student.Revision);
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status412PreconditionFailed,
                "REVISION_STALE",
                "別の職員が先に更新しました",
                "最新の内容を確認してから変更をやり直してください。");
        }

        var validation = ValidateStudent(
            request.StudentNumber,
            request.FamilyName,
            request.GivenName,
            request.FamilyNameKana,
            request.GivenNameKana,
            request.DisplayName);
        if (validation is not null)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "STUDENT_INVALID",
                "生徒情報を保存できません",
                "入力内容を確認してください。",
                validation);
        }

        var normalizedNumber = NormalizeSearch(request.StudentNumber);
        if (await db.Students.AnyAsync(
            item => item.Id != studentId
                && item.StudentNumberNormalized == normalizedNumber
                && item.Status != "merged",
            cancellationToken))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "STUDENT_NUMBER_DUPLICATE",
                "生徒番号が重複しています",
                "別の生徒が同じ番号を使用しています。");
        }

        student.StudentNumber = request.StudentNumber.Trim();
        student.StudentNumberNormalized = normalizedNumber;
        student.FamilyName = request.FamilyName.Trim();
        student.GivenName = request.GivenName.Trim();
        student.FamilyNameKana = request.FamilyNameKana.Trim();
        student.GivenNameKana = request.GivenNameKana.Trim();
        student.FamilyNameNormalized = NormalizeSearch(request.FamilyName);
        student.GivenNameNormalized = NormalizeSearch(request.GivenName);
        student.FamilyNameKanaNormalized = NormalizeSearch(request.FamilyNameKana);
        student.GivenNameKanaNormalized = NormalizeSearch(request.GivenNameKana);
        student.DisplayName = request.DisplayName.Trim();
        student.GradeLabel = TrimOrNull(request.GradeLabel);
        student.Course = TrimOrNull(request.Course);
        student.SchoolClass = TrimOrNull(request.SchoolClass);
        student.PrivateNotes = TrimOrNull(request.Notes);
        AddAudit(
            db,
            timeProvider.GetUtcNow(),
            principal,
            context,
            "student.updated",
            student.Id);
        await db.SaveChangesAsync(cancellationToken);

        ApiHelpers.SetRevisionEtag(context.Response, student.Revision);
        return Results.Ok(new
        {
            student.Id,
            student.StudentNumber,
            student.DisplayName,
            student.Revision,
        });
    }

    private static Task<IResult> DeactivateStudent(
        string studentId,
        HttpContext context,
        ClaimsPrincipal principal,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        ChangeStatus(
            studentId,
            "inactive",
            "student.deactivated",
            context,
            principal,
            db,
            timeProvider,
            cancellationToken);

    private static Task<IResult> ReactivateStudent(
        string studentId,
        HttpContext context,
        ClaimsPrincipal principal,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        ChangeStatus(
            studentId,
            "active",
            "student.reactivated",
            context,
            principal,
            db,
            timeProvider,
            cancellationToken);

    private static async Task<IResult> ChangeStatus(
        string studentId,
        string status,
        string eventType,
        HttpContext context,
        ClaimsPrincipal principal,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var student = await db.Students.SingleOrDefaultAsync(
            item => item.Id == studentId,
            cancellationToken);
        if (student is null)
        {
            return Results.NotFound();
        }

        student.Status = status;
        AddAudit(
            db,
            timeProvider.GetUtcNow(),
            principal,
            context,
            eventType,
            student.Id);
        await db.SaveChangesAsync(cancellationToken);
        ApiHelpers.SetRevisionEtag(context.Response, student.Revision);
        return Results.Ok(new
        {
            student.Id,
            enrollmentStatus = student.Status,
            active = status == "active",
            student.Revision,
        });
    }

    private static async Task<IResult> ListAliases(
        string studentId,
        OokiGraderDbContext db,
        CancellationToken cancellationToken)
    {
        var exists = await db.Students.AnyAsync(
            student => student.Id == studentId,
            cancellationToken);
        if (!exists)
        {
            return Results.NotFound();
        }

        var aliases = await db.StudentAliases
            .AsNoTracking()
            .Where(alias => alias.StudentId == studentId)
            .OrderBy(alias => alias.CreatedAt)
            .Select(alias => new
            {
                alias.Id,
                text = alias.DisplayValue,
                aliasType = alias.AliasType,
                normalizedText = alias.NormalizedValue,
            })
            .ToListAsync(cancellationToken);
        return Results.Ok(aliases);
    }

    private static async Task<IResult> CreateAlias(
        string studentId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] AliasWriteRequest request,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var value = request.Text ?? request.Value;
        var kind = NormalizeAliasType(request.AliasType ?? request.Kind ?? "other");
        var allowedKinds = new HashSet<string>(StringComparer.Ordinal)
        {
            "kanji",
            "kana",
            "romanized",
            "old_name",
            "spacing",
            "handwriting_hint",
            "other",
        };
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 400
            || !allowedKinds.Contains(kind))
        {
            return Results.UnprocessableEntity();
        }

        if (!await db.Students.AnyAsync(
            student => student.Id == studentId,
            cancellationToken))
        {
            return Results.NotFound();
        }

        var normalized = NormalizeSearch(value);
        if (await db.StudentAliases.AnyAsync(
            alias => alias.StudentId == studentId
                && alias.NormalizedValue == normalized
                && alias.AliasType == kind,
            cancellationToken))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "ALIAS_DUPLICATE",
                "同じ別名が登録されています",
                "既存の別名を確認してください。");
        }

        var now = timeProvider.GetUtcNow();
        var alias = new StudentAliasEntity
        {
            Id = UlidId.New(now),
            StudentId = studentId,
            AliasType = kind,
            DisplayValue = value.Trim(),
            NormalizedValue = normalized,
            CreatedByStaffUserId = ApiHelpers.StaffId(principal),
            CreatedAt = now,
        };
        db.StudentAliases.Add(alias);
        AddAudit(
            db,
            now,
            principal,
            context,
            "student.alias_created",
            studentId);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created(
            $"/api/v1/students/{studentId}/aliases/{alias.Id}",
            new
            {
                alias.Id,
                text = alias.DisplayValue,
                aliasType = alias.AliasType,
                normalizedText = alias.NormalizedValue,
            });
    }

    private static async Task<IResult> DeleteAlias(
        string studentId,
        string aliasId,
        HttpContext context,
        ClaimsPrincipal principal,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var alias = await db.StudentAliases.SingleOrDefaultAsync(
            item => item.Id == aliasId && item.StudentId == studentId,
            cancellationToken);
        if (alias is null)
        {
            return Results.NotFound();
        }

        db.StudentAliases.Remove(alias);
        AddAudit(
            db,
            timeProvider.GetUtcNow(),
            principal,
            context,
            "student.alias_deleted",
            studentId);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> GetProgress(
        string studentId,
        DateOnly? from,
        DateOnly? to,
        string? subject,
        OokiGraderDbContext db,
        CancellationToken cancellationToken)
    {
        var student = await db.Students
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == studentId, cancellationToken);
        if (student is null)
        {
            return Results.NotFound();
        }

        var rangeEnd = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var rangeStart = from ?? rangeEnd.AddMonths(-6);
        if (rangeStart > rangeEnd)
        {
            return Results.UnprocessableEntity();
        }

        var seriesQuery = db.Submissions
            .AsNoTracking()
            .Where(submission => submission.AssignedStudentId == studentId
                && submission.FinalizedAt != null
                && submission.VoidedAt == null
                && submission.CanonicalForSession
                && submission.TestSession.TestDate >= rangeStart
                && submission.TestSession.TestDate <= rangeEnd
                && submission.CurrentGradingRunId != null);
        if (!string.IsNullOrWhiteSpace(subject))
        {
            seriesQuery = seriesQuery.Where(submission =>
                (submission.TestSession.TemplateSubjectSnapshot
                    ?? submission.TestSession.TemplateVersion.TestTemplate.Subject)
                == subject);
        }

        var series = await seriesQuery
            .OrderBy(submission => submission.TestSession.TestDate)
            .ThenBy(submission => submission.Id)
            .Select(submission => new
            {
                submissionId = submission.Id,
                testDate = submission.TestSession.TestDate,
                testTitle = submission.TestSession.TitleOverride
                    ?? submission.TestSession.TemplateTitleSnapshot
                    ?? submission.TestSession.TemplateVersion.TestTemplate.Title,
                earnedPointsMilli = submission.GradingRuns
                    .Where(run => run.Id == submission.CurrentGradingRunId)
                    .Select(run => run.EarnedPointsMilli)
                    .First(),
                possiblePointsMilli = submission.GradingRuns
                    .Where(run => run.Id == submission.CurrentGradingRunId)
                    .Select(run => run.PossiblePointsMilli)
                    .First(),
                resultRevision = submission.GradingRuns
                    .Where(run => run.Id == submission.CurrentGradingRunId)
                    .Select(run => (int)run.ResultSourceRevision)
                    .First(),
                correct = submission.GradingRuns
                    .Where(run => run.Id == submission.CurrentGradingRunId)
                    .SelectMany(run => run.QuestionResults)
                    .Count(result =>
                        result.Revisions
                            .Where(revision => revision.Id == result.CurrentRevisionId)
                            .Select(revision => revision.Outcome)
                            .FirstOrDefault() == "correct"),
                partial = submission.GradingRuns
                    .Where(run => run.Id == submission.CurrentGradingRunId)
                    .SelectMany(run => run.QuestionResults)
                    .Count(result =>
                        result.Revisions
                            .Where(revision => revision.Id == result.CurrentRevisionId)
                            .Select(revision => revision.Outcome)
                            .FirstOrDefault() == "partial"),
                incorrect = submission.GradingRuns
                    .Where(run => run.Id == submission.CurrentGradingRunId)
                    .SelectMany(run => run.QuestionResults)
                    .Count(result =>
                        result.Revisions
                            .Where(revision => revision.Id == result.CurrentRevisionId)
                            .Select(revision => revision.Outcome)
                            .FirstOrDefault() == "incorrect"),
                blank = submission.GradingRuns
                    .Where(run => run.Id == submission.CurrentGradingRunId)
                    .SelectMany(run => run.QuestionResults)
                    .Count(result =>
                        result.Revisions
                            .Where(revision => revision.Id == result.CurrentRevisionId)
                            .Select(revision => revision.Outcome)
                            .FirstOrDefault() == "blank"),
            })
            .ToListAsync(cancellationToken);

        var points = series.Select(point => new
        {
            point.submissionId,
            point.testDate,
            point.testTitle,
            point.earnedPointsMilli,
            point.possiblePointsMilli,
            percentageBasisPoints = point.possiblePointsMilli == 0
                ? 0
                : (int)Math.Round(
                    point.earnedPointsMilli * 10_000m / point.possiblePointsMilli,
                    MidpointRounding.AwayFromZero),
            point.correct,
            point.partial,
            point.incorrect,
            point.blank,
            point.resultRevision,
        });
        return Results.Ok(new
        {
            student = new { student.Id, student.DisplayName },
            range = new { from = rangeStart, to = rangeEnd, timeZone = "Asia/Tokyo" },
            series = points,
        });
    }

    private static string NormalizeSearch(string value) =>
        JapaneseTextNormalizer.NormalizeForComparison(
            value,
            new JapaneseNormalizationOptions { RemoveAllWhitespace = true });

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeAliasType(string value) => value switch
    {
        "handwritten" => "handwriting_hint",
        "oldSurname" => "old_name",
        "romanization" => "romanized",
        _ => value,
    };

    private static List<object>? ValidateStudent(
        string studentNumber,
        string familyName,
        string givenName,
        string familyNameKana,
        string givenNameKana,
        string? displayName)
    {
        var errors = new List<object>();
        AddRequired(errors, "studentNumber", studentNumber, 200);
        AddRequired(errors, "familyName", familyName, 200);
        AddRequired(errors, "givenName", givenName, 200);
        AddRequired(errors, "familyNameKana", familyNameKana, 200);
        AddRequired(errors, "givenNameKana", givenNameKana, 200);
        if (displayName?.Length > 400)
        {
            errors.Add(new
            {
                field = "displayName",
                code = "TOO_LONG",
                message = "表示名が長すぎます。",
            });
        }

        return errors.Count == 0 ? null : errors;
    }

    private static void AddRequired(
        List<object> errors,
        string field,
        string? value,
        int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new { field, code = "REQUIRED", message = "必須項目です。" });
        }
        else if (value.Length > maxLength)
        {
            errors.Add(new { field, code = "TOO_LONG", message = "入力が長すぎます。" });
        }
    }

    private static void AddAudit(
        OokiGraderDbContext db,
        DateTimeOffset now,
        ClaimsPrincipal principal,
        HttpContext context,
        string eventType,
        string studentId) =>
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now),
            OccurredAt = now,
            ActorStaffUserId = ApiHelpers.StaffId(principal),
            EventType = eventType,
            ObjectType = "student",
            ObjectId = studentId,
            Outcome = "succeeded",
            CorrelationId = context.TraceIdentifier,
        });

    private sealed record AliasWriteRequest(
        string? Text,
        string? Value,
        string? AliasType,
        string? Kind);
}
