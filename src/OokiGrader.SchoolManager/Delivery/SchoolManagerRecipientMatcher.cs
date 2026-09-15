using System.Globalization;
using System.Text;

namespace OokiGrader.SchoolManager.Delivery;

public sealed record SchoolManagerStudentCandidate(
    string UserId,
    string DepartmentId,
    string UserCode,
    string FullName);

public sealed class SchoolManagerRecipientException(
    string code,
    string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public static class SchoolManagerRecipientMatcher
{
    public static SchoolManagerStudentCandidate Match(
        string studentNumber,
        string displayName,
        IEnumerable<SchoolManagerStudentCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var normalizedNumber = Normalize(studentNumber);
        var normalizedName = Normalize(displayName);
        var matches = candidates.Where(candidate =>
                Normalize(candidate.UserCode) == normalizedNumber
                && Normalize(candidate.FullName) == normalizedName)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new SchoolManagerRecipientException(
                matches.Length == 0
                    ? "recipient_not_found"
                    : "recipient_ambiguous",
                "School Manager must contain exactly one matching student.");
        }

        return matches[0];
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Concat(
            value.Normalize(NormalizationForm.FormKC)
                .Where(character => !char.IsWhiteSpace(character)))
            .ToUpper(CultureInfo.InvariantCulture);
    }
}
