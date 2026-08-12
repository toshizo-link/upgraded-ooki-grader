using OokiGrader.Domain.Common;
using OokiGrader.Domain.Templates;

namespace OokiGrader.Application.Templates;

public sealed record PaperGradeResult(
    GradeLevel Grade,
    bool IsUnambiguous,
    string? PrintedLabel = null,
    string? ErrorCode = null);

public sealed record GradeResolution(
    GradeLevel Grade,
    bool IsResolved,
    GradeEvidence Evidence,
    string? ErrorCode)
{
    public GradeLevel ResolvedGrade => Grade;

    public bool RequiresUserSelection => !IsResolved;
}

public static class GradeResolutionService
{
    public const string RequiredErrorCode = "GRADE_REQUIRED";
    public const string ConflictErrorCode = "GRADE_CONFLICT";

    public static GradeResolution Resolve(
        FileNameGradeResult filename,
        PaperGradeResult paper,
        GradeLevel? userSelection)
    {
        ArgumentNullException.ThrowIfNull(filename);
        ArgumentNullException.ThrowIfNull(paper);

        if (userSelection is not null)
        {
            if (!IsSupported(userSelection.Value))
            {
                throw new DomainValidationException(
                [
                    new DomainError(
                        "GRADE_INVALID",
                        "User-selected grade must be from grade 1 through 6.",
                        "userSelection"),
                ]);
            }

            return Resolved(userSelection.Value, GradeEvidence.User);
        }

        if (filename.ErrorCode is not null)
        {
            return Unresolved(filename.ErrorCode);
        }

        if (paper.ErrorCode is not null)
        {
            return Unresolved(paper.ErrorCode);
        }

        var filenameGrade = PresentGrade(filename.Grade, filename.IsUnambiguous);
        var paperGrade = PresentGrade(paper.Grade, paper.IsUnambiguous);

        if (filenameGrade is not null && paperGrade is not null)
        {
            return filenameGrade == paperGrade
                ? Resolved(
                    filenameGrade.Value,
                    GradeEvidence.FileNameAndPaper)
                : Unresolved(ConflictErrorCode);
        }

        if (filenameGrade is not null)
        {
            return Resolved(filenameGrade.Value, GradeEvidence.FileName);
        }

        if (paperGrade is not null)
        {
            return Resolved(paperGrade.Value, GradeEvidence.Paper);
        }

        return Unresolved(RequiredErrorCode);
    }

    private static GradeLevel? PresentGrade(
        GradeLevel grade,
        bool isUnambiguous) =>
        isUnambiguous && IsSupported(grade) ? grade : null;

    private static bool IsSupported(GradeLevel grade) =>
        grade is >= GradeLevel.Grade1 and <= GradeLevel.Grade6;

    private static GradeResolution Resolved(
        GradeLevel grade,
        GradeEvidence evidence) =>
        new(grade, IsResolved: true, evidence, ErrorCode: null);

    private static GradeResolution Unresolved(string errorCode) =>
        new(
            GradeLevel.Unknown,
            IsResolved: false,
            GradeEvidence.None,
            errorCode);
}
