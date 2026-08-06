using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace OokiGrader.Reports.Pdf;

public static class ResultReportSourceHasher
{
    public static string Compute(ResultReportDocument report)
    {
        ArgumentNullException.ThrowIfNull(report);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, report.SchoolName);
        Append(hash, report.StudentDisplayName);
        Append(hash, report.StudentNumber);
        Append(hash, report.TestTitle);
        Append(
            hash,
            report.TestDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Append(hash, report.TemplateVersionNumber);
        Append(hash, report.ResultRevision);
        Append(hash, report.EarnedPointsMilli);
        Append(hash, report.PossiblePointsMilli);
        Append(hash, report.IsCorrectedGrade);
        Append(hash, report.IncludeTeacherComments);
        Append(hash, report.Questions.Count);
        foreach (var question in report.Questions)
        {
            Append(hash, question.DisplayLabel);
            Append(hash, question.QuestionText);
            Append(hash, question.RecognizedAnswer);
            Append(hash, question.AwardedPointsMilli);
            Append(hash, question.MaximumPointsMilli);
            Append(hash, question.Outcome);
            Append(hash, question.IsCorrected);
            Append(
                hash,
                report.IncludeTeacherComments ? question.TeacherComment : null);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, object? value)
    {
        var json = JsonSerializer.Serialize(value);
        var bytes = Encoding.UTF8.GetBytes(json);
        hash.AppendData(bytes);
        hash.AppendData([0]);
    }
}
