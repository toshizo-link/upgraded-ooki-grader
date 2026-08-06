# Ooki Grader — grading fix verification

**Evaluation date:** 2026-08-05  
**Status:** Exploratory; teacher-review-only  
**Decision:** Keep `gemini-3.5-flash-lite` / `MINIMAL` as the configured model.

## Outcome

The failures found in the first model comparison are now isolated at the
question level instead of rejecting an otherwise usable paper. The grading
contract also explicitly distinguishes a located blank answer from a question
that cannot be found.

The production candidate, Gemini 3.5 Flash Lite, returned all 105 real-paper
items, identified all 31 blank short answers, and passed local validation for
all three papers. Gemini 3.6 Flash completed without the previous invalid-point
abort, but it still omitted all 31 blank answers. It should not replace the
configured model.

Automatic finalization remains disabled. The real handwriting corpus is small,
English, and insufficient to approve unattended grading for 大木スクール.

## Fixes implemented

- Prompt versions are now `answer-transcribe-grade-v1.2.0` and
  `answer-recheck-v1.2.0`.
- A located empty answer must be returned with `blank=true`, an empty
  transcription, zero points, and outcome `blank`.
- `missing_question_ids` is reserved for a printed question or answer location
  that cannot be located after inspecting all supplied pages.
- Proposed points must be within the question maximum and an exact configured
  increment.
- Deterministic and transcription-then-rules questions are scored locally from
  the transcription. A contradictory AI score is replaced by the local result
  and marked `ai_deterministic_recomputed` for review.
- An invalid semantic-rubric point award becomes a zero-point review proposal
  marked `ai_invalid_point_award`; it no longer rejects the whole paper.
- Ambiguous, unreadable, or cropped observations remain zero-point review items.
- Unambiguous choice decorations such as `A.`, `Ａ．`, `(A)`, `Ⓐ`, `（ア）`,
  `㋐`, and `①` map to configured canonical choices. Multi-label or prose-like
  values remain review-only.
- Re-adjudication preserves the exact local reconciliation reason in its audit
  revision.

## Live results after the fix

Both runs used the same source files, media, schema, and prompt hash
`ceb520ff96033b866f66a021bdc149e1e29c4169125859733bd552f16dc0617b`.

| Metric | 3.5 Flash Lite / MINIMAL | 3.6 Flash / MEDIUM |
|---|---:|---:|
| Real items returned | 105/105 | 74/105 |
| Real responses accepted structurally | 3/3 | 3/3 |
| Located blanks returned correctly | 31/31 | 0/31 |
| Objective score agreement | 53/56 | 53/56 |
| Incorrect-credit false positives | 1/27 | 1/27 |
| Nonblank short-answer exact score agreement | 5/14 | 7/14 |
| Japanese synthetic transcription | 5/5 | 5/5 |
| System review-safe quarantines, real papers | 1 | 1 |

The 3.6 run's structural acceptance does not make its missing answers usable:
the application creates explicit review placeholders for those 31 IDs. On the
Japanese control, 3.6 proposed full credit for a response it labeled
`ambiguous`; the local policy replaced that proposal with zero points plus
teacher review. This is the intended fail-closed behavior.

Compared with the earlier 3.5 Flash Lite comparison run, real-paper validator
acceptance improved from 1/3 to 3/3, objective score agreement improved from
51/56 to 53/56, and incorrect-credit false positives fell from 3/27 to 1/27.
These single-run changes may include model nondeterminism, so they are evidence
of contract compatibility, not a statistically reliable model improvement.

## Automated verification

- .NET: 419 passed, 6 opt-in external tests skipped, 0 failed.
- Web: 29 passed, TypeScript check passed, production build passed.
- Focused adjudication integration: 8 passed.
- Changed C# files pass `dotnet format --verify-no-changes`.
- Two post-fix live evaluation runs passed their executable harness.

## Remaining limitations

1. Build a privacy-reviewed golden set of genuine Japanese 大木スクール exam
   pages with teacher-adjudicated item scores.
2. Keep semantic answers and all AI-generated grading teacher-review-only until
   the documented safety thresholds are met.
3. Do not use Gemini 3.6 Flash for the current workflow unless its blank-answer
   coverage is fixed and repeatedly verified.
4. Gemini 3.1 Pro Preview was not retested because Google provides no free API
   tier for that model and the supplied project had no generation quota.

## Evidence

- `gemini-3.5-flash-lite-fix-verification-run-1.json`  
  SHA-256 `5999eb7425201361a754e15fc4b066260c62396306e1589c10428ea31bb435c4`
- `gemini-3.6-flash-fix-verification-run-1.json`  
  SHA-256 `25e1dd728793459b40230ad6272097f4c7c3cd376efd6b80f501b2daf5e42a78`

The API credential is not present in these artifacts. No application profile
was approved or activated by the evaluator.
