# Ooki Grader — Phase 3 intake and grading accuracy report

**Evaluation date:** 2026-09-15
**Status:** Local implementation and regression complete; school acceptance
dataset pending
**Evidence class:** Synthetic, provider-free, not deployment approval

## Implemented safety boundary

The application now applies review policy after local grading reconciliation.
A clear, high-confidence result may skip per-question review only when the
final outcome is `correct` and every existing confidence, quality, question,
and contradiction gate passes. The following outcomes always wait for a
teacher:

- `incorrect`
- `blank`
- `partial`
- `unreadable`
- `review`

The initial-grading worker persists those results with
`ReviewRequired=true`, `ReviewStatus=pending`, and moves the paper and run to
`needs_grade_review`. A locally verified correct result retains the current
fast path. The teacher still explicitly finalizes every paper.

## Local grading matrix

The committed seven-case matrix exercises the application validator and the
new reusable accuracy evaluator.

| Category | Cases | Expected result |
|---|---:|---|
| Multiple accepted answers | 2 | canonical and equivalent each receive full credit |
| Kanji required | 2 | Kanji receives credit; kana-only response receives zero plus review |
| Partial credit | 1 | configured half credit plus review |
| Incorrect | 1 | zero plus review |
| Located blank | 1 | zero plus review |

| Metric | Result |
|---|---:|
| Outcome and point agreement | 7/7 (100.00%) |
| Automatic decisions | 3/7 |
| Precision among automatic decisions | 3/3 (100.00%) |
| Unsafe automatic decisions | 0 |
| Incorrect-credit false positives | 0 |
| Risk cases sent to teacher review | 4/4 (100.00%) |

The evaluator also has a negative control proving that an incorrect response
given credit without review is counted simultaneously as disagreement, unsafe
automation, incorrect-credit false positive, and missed risk review.

## Ordered-scan routing

The real `local-raster-v3` PDF preprocessing and alignment path processed the
committed blank Japanese form and two completed variants. The source hashes are
pinned in the automated test and evidence JSON.

| Candidate | Alignment score | Threshold | Rotation | Result |
|---|---:|---:|---:|---|
| asia-check-test-hanako.pdf | 9,890 | 6,500 | 0° | aligned |
| asia-check-test-yuta.pdf | 9,894 | 6,500 | 0° | aligned |

Endpoint integration tests separately retain the 250-basis-point margin rule
and prove that weak, tied, multi-page, or unavailable-reference inputs fail
closed into review. File names cannot override a visual tie.

## Reproducible checks

```text
dotnet test tests/OokiGrader.Application.Tests \
  --filter PhaseThreeLocalGradingEvaluationTests

dotnet test tests/OokiGrader.Preprocessing.Tests \
  --filter AlignsCommittedCompletedSheetsToBlankRoutingReference

dotnet test tests/OokiGrader.IntegrationTests \
  --filter NegativeInitialResultAlwaysWaitsForTeacherReview
```

## Remaining acceptance gate

These checks establish code behavior and a measurable review boundary. They do
not establish grading accuracy for 大木スクール. The remaining gate is the
privacy-reviewed, versioned Japanese school answer set with actual scanner
settings, anonymized handwriting, authoritative answers/rubrics, and
teacher-adjudicated item scores. Automatic assignment and unattended grading
remain disabled until that set meets the documented release thresholds.
