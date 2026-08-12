using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Grading;
using OokiGrader.Application.Identity;
using OokiGrader.Infrastructure.Persistence;

namespace OokiGrader.Host.Jobs;

internal static class CombinedIdentityEvidenceBuilder
{
    private const int MaximumRosterSize = 50_000;

    public static async Task<NameAssignmentEvidence> BuildAsync(
        OokiGraderDbContext db,
        string testSessionId,
        ValidatedAiIdentityTranscription transcription,
        string aiRequestId,
        string inputManifestHash,
        string pipelineVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(testSessionId);
        ArgumentNullException.ThrowIfNull(transcription);

        var session = await db.TestSessions
            .AsNoTracking()
            .Where(item => item.Id == testSessionId)
            .Select(item => new
            {
                item.ExpectedRosterEnabled,
                Expected = item.RosterMembers.Select(member => new
                {
                    member.StudentId,
                    member.Expected,
                }),
            })
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        var expectedByStudent = session.Expected.ToDictionary(
            item => item.StudentId,
            item => session.ExpectedRosterEnabled && item.Expected,
            StringComparer.Ordinal);
        var expectedIds = expectedByStudent.Keys.ToArray();
        var students = await db.Students
            .AsNoTracking()
            .Include(item => item.Aliases)
            .Where(item => item.Status == "active" || expectedIds.Contains(item.Id))
            .OrderBy(item => item.Id)
            .Take(MaximumRosterSize + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        if (students.Length > MaximumRosterSize)
        {
            return NoCandidateEvidence(
                transcription,
                aiRequestId,
                inputManifestHash,
                pipelineVersion,
                "roster_too_large");
        }

        var roster = students.Select(student => new RosterSnapshot(
            student.Id,
            student.Revision,
            student.StudentNumber,
            student.FamilyName,
            student.GivenName,
            student.DisplayName,
            student.FamilyNameKana,
            student.GivenNameKana,
            student.SchoolClass,
            expectedByStudent.GetValueOrDefault(student.Id),
            student.Aliases
                .OrderBy(alias => alias.Id, StringComparer.Ordinal)
                .Select(alias => new AliasSnapshot(
                    alias.Id,
                    alias.DisplayValue,
                    alias.RecognitionEnabled))
                .ToArray())).ToArray();
        var match = LocalRosterMatcher.Match(
            new IdentityTranscription(
                transcription.VisibleName,
                transcription.VisibleStudentNumber,
                transcription.Legibility,
                transcription.ProviderConfidenceBasisPoints),
            roster.Select(ToCandidate).ToArray());
        var rosterById = roster.ToDictionary(
            item => item.StudentId,
            StringComparer.Ordinal);
        var candidates = match.Candidates.Select(candidate =>
        {
            var source = rosterById[candidate.StudentId];
            return new NameCandidateEvidence
            {
                StudentId = candidate.StudentId,
                StudentNumber = source.StudentNumber,
                DisplayName = source.DisplayName,
                Kana = JoinKana(source.FamilyNameKana, source.GivenNameKana),
                ClassLabel = source.ClassLabel,
                RankScore = candidate.RankScore,
                Expected = candidate.Expected,
                StudentNumberConflict = candidate.StudentNumberConflict,
                NameSimilarityBasisPoints = candidate.NameSimilarityBasisPoints,
                Evidence = candidate.Evidence,
            };
        }).ToArray();

        return new NameAssignmentEvidence
        {
            SchemaVersion = "name_assignment_evidence_v2",
            PipelineVersion = pipelineVersion,
            AiRequestId = aiRequestId,
            InputManifestHash = inputManifestHash,
            RosterManifestHash = ComputeRosterManifestHash(roster),
            IdentityPageNumber = 1,
            Transcription = ToEvidence(transcription),
            MatchingPolicyVersion = match.PolicyVersion,
            Disposition = match.Disposition,
            NormalizedVisibleName = match.NormalizedVisibleName,
            NormalizedVisibleStudentNumber =
                match.NormalizedVisibleStudentNumber,
            FirstSecondMargin = match.FirstSecondMargin,
            AutomaticAssignmentEnabled = false,
            Candidates = candidates,
        };
    }

    private static NameAssignmentEvidence NoCandidateEvidence(
        ValidatedAiIdentityTranscription transcription,
        string aiRequestId,
        string inputManifestHash,
        string pipelineVersion,
        string rosterManifestHash) =>
        new()
        {
            SchemaVersion = "name_assignment_evidence_v2",
            PipelineVersion = pipelineVersion,
            AiRequestId = aiRequestId,
            InputManifestHash = inputManifestHash,
            RosterManifestHash = rosterManifestHash,
            IdentityPageNumber = 1,
            Transcription = ToEvidence(transcription),
            MatchingPolicyVersion = LocalRosterMatcher.PolicyVersion,
            Disposition = "no_match",
            AutomaticAssignmentEnabled = false,
            Candidates = [],
        };

    private static NameTranscriptionEvidence ToEvidence(
        ValidatedAiIdentityTranscription transcription) =>
        new()
        {
            VisibleName = transcription.VisibleName,
            VisibleStudentNumber = transcription.VisibleStudentNumber,
            Legibility = transcription.Legibility,
            ProviderConfidenceBasisPoints =
                transcription.ProviderConfidenceBasisPoints,
            UnexpectedContent = transcription.UnexpectedContent,
        };

    private static RosterIdentityCandidate ToCandidate(RosterSnapshot item) =>
        new(
            item.StudentId,
            item.StudentNumber,
            item.FamilyName,
            item.GivenName,
            item.DisplayName,
            item.FamilyNameKana,
            item.GivenNameKana,
            item.Expected,
            item.Aliases.Select(alias => new RosterIdentityAlias(
                alias.Value,
                alias.RecognitionEnabled)).ToArray());

    private static string ComputeRosterManifestHash(
        IEnumerable<RosterSnapshot> roster)
    {
        var canonical = new StringBuilder();
        foreach (var student in roster.OrderBy(
                     item => item.StudentId,
                     StringComparer.Ordinal))
        {
            Append(canonical, student.StudentId);
            Append(canonical, student.Revision.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            Append(canonical, student.StudentNumber);
            Append(canonical, student.FamilyName);
            Append(canonical, student.GivenName);
            Append(canonical, student.DisplayName);
            Append(canonical, student.FamilyNameKana ?? string.Empty);
            Append(canonical, student.GivenNameKana ?? string.Empty);
            Append(canonical, student.ClassLabel ?? string.Empty);
            Append(canonical, student.Expected ? "1" : "0");
            foreach (var alias in student.Aliases)
            {
                Append(canonical, alias.Id);
                Append(canonical, alias.Value);
                Append(canonical, alias.RecognitionEnabled ? "1" : "0");
            }
        }

        return Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static void Append(StringBuilder destination, string value) =>
        destination.Append(value.Length).Append(':').Append(value).Append('\n');

    private static string? JoinKana(string? familyName, string? givenName)
    {
        var values = new[] { familyName, givenName }
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
        return values.Length == 0 ? null : string.Join(' ', values);
    }

    private sealed record RosterSnapshot(
        string StudentId,
        long Revision,
        string StudentNumber,
        string FamilyName,
        string GivenName,
        string DisplayName,
        string? FamilyNameKana,
        string? GivenNameKana,
        string? ClassLabel,
        bool Expected,
        IReadOnlyList<AliasSnapshot> Aliases);

    private sealed record AliasSnapshot(
        string Id,
        string Value,
        bool RecognitionEnabled);
}
