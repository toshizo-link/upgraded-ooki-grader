# Ooki Grader — Basic grading accuracy report

**Evaluation date:** 2026-08-05  
**Status:** Exploratory; teacher-review-only  
**Release decision:** **Not approved for automatic finalization**

## Executive summary

Gemini 3.5 Flash-Lite was tested twice through Ooki Grader's direct Gemini
client, approved initial-grading prompt bundle, structured response schema, and
local response validator.

The model returned all 105 requested items from the three real handwritten
papers in both runs. Raw objective score agreement was 53/56 (94.6%) in both
runs, but hypothetical automatic-credit precision for those raw proposals was
93.3% and the incorrect-credit
false-positive rate was 7.4%. These miss the current 99.5% precision and 0.5%
false-positive gates.

Semantic short-answer exact score agreement was 6/14 (42.9%) in run 1 and
4/14 (28.6%) in run 2. Two of the three real-paper responses also failed the
system's deterministic consistency validator in both runs. The system would
therefore have accepted only 35/105 returned real-paper items (33.3% usable
coverage) and routed the other two papers to attention instead of silently
applying contradictory grades.

The five-question synthetic Japanese canonical-answer sheet scored 5/5 for
both transcription and grading in both runs and passed local validation. This
is a useful functional check, but it uses a handwriting-style font and is not
evidence of production accuracy on real Japanese student handwriting.

## Evaluated configuration

| Item | Value |
|---|---|
| Requested and returned model | `gemini-3.5-flash-lite` |
| Prompt | `answer-transcribe-grade-v1.1.0` |
| Response schema | `answer_transcribe_grade_v1` |
| Prompt content hash | `9c2ae3112d824c1419ad82ee7e9b3fcc32176a787d1a12008ad716b78f27c24a` |
| Provider path | Official Gemini `generateContent`, via `GeminiDirectClient` |
| Media mode | Full-page PNG, high media resolution |
| Repeat count | 2 identical-dataset runs |
| Total tokens | 36,886 in run 1; 36,990 in run 2 |

The evaluation used the production-compatible initial-grading request and the
actual `AiGradingResponseValidator`. The English corpus wording was generalized
from “preserve Japanese script” to “preserve the observed script”; all other
grading and coverage instructions remained aligned with the worker contract.

## Dataset

### Real handwritten scored papers

- 3 anonymized English university examination papers, 8 pages total.
- 105 question slots: 60 multiple-choice slots and 45 short-answer slots.
- 56 attempted A–D response labels suitable for item-level transcription and
  key-derived objective grading.
- 14 nonblank short answers with explicit teacher item scores.
- 31 blank short-answer cells.
- Source: the pinned CC BY 4.0 Mendeley fixture described in
  `docs/testing/handwritten-exam-fixtures.md`.

The source CSV's aggregate multiple-choice marks conflict with the supplied
answer key for Student 18 and Student 19. Aggregate paper scores were therefore
excluded. Objective truth was derived only from the item-level response label
and supplied item answer key.

### Japanese functional control

- 1 synthetic Japanese middle-school geography answer sheet.
- 5 canonical answers, including one explanatory response.
- Interwoven printed questions and answer fields matching the intended school
  layout.
- The writing is rendered with a handwriting-style font, not written by a
  student.

## Results

### Real-paper objective items

Results were identical across the two runs.

| Metric | Result | 95% Wilson interval / gate | Decision |
|---|---:|---:|---|
| Returned item coverage | 105/105 (100.0%) | — | Pass |
| Strict choice transcription | 44/56 (78.6%) | 66.2%–87.3% | Below target |
| Key-derived choice score agreement | 53/56 (94.6%) | 85.4%–98.2% | Below 97% agreement gate |
| Raw-proposal automatic-credit precision | 28/30 (93.3%) | gate ≥99.5% | Fail |
| Incorrect-credit false-positive rate | 2/27 (7.4%) | gate ≤0.5% | Fail |
| Correct-answer under-credit rate | 1/29 (3.4%) | — | Needs improvement |

The confusion counts were 28 true credits, 25 true rejections, 2 incorrect
credits, and 1 missed credit. Several strict transcription mismatches were
punctuation forms such as `A.` rather than `A`, but the current deterministic
choice policy does not strip that punctuation. Other errors were genuine
question-to-answer mapping mistakes, including reading a printed question
number as the selected answer.

### Teacher-scored short answers

| Metric | Run 1 | Run 2 |
|---|---:|---:|
| Exact teacher-score agreement | 6/14 (42.9%) | 4/14 (28.6%) |
| 95% Wilson interval | 21.4%–67.4% | 11.7%–54.6% |
| Mean absolute error | 0.64 points | 0.79 points |
| Over-credit | 2/14 | 4/14 |
| Under-credit | 6/14 | 6/14 |
| Blank detection | 31/31 (100.0%) | 31/31 (100.0%) |

Twelve of the fourteen explicit short-answer scores were stable between the
two runs (85.7% repeat agreement). Two Student 18 responses moved by one point
between runs. The supplied reference answers are broad prose examples rather
than detailed element-level rubrics, so these results test the current weak
rubric input as well as the model. Ooki Grader correctly keeps semantic scores
teacher-review-only.

### Local validation and usable coverage

| Metric | Run 1 | Run 2 |
|---|---:|---:|
| Real papers accepted by full response validation | 1/3 | 1/3 |
| Real items in accepted responses | 35/105 (33.3%) | 35/105 (33.3%) |
| Rejected response error | `ai_response_deterministic_contradiction` ×2 | same |
| Japanese synthetic response accepted | Yes | Yes |

The validator prevented contradictory deterministic results from being
persisted, which is safe. However, one malformed objective transcription made
the whole paper unusable, so the current failure granularity creates excessive
teacher work.

### Japanese synthetic control

| Metric | Run 1 | Run 2 |
|---|---:|---:|
| Exact answer transcription | 5/5 | 5/5 |
| Exact score agreement | 5/5 | 5/5 |
| Local validator accepted response | Yes | Yes |

This result demonstrates that the intended Japanese interwoven layout can be
read in a clean controlled case. It cannot estimate real Japanese handwriting
accuracy.

### Repeatability and latency

- Raw proposed scores were identical for 103/105 real item slots (98.1%).
- Objective proposed scores were identical for 60/60 slots (100.0%).
- Short-answer proposed scores were identical for 43/45 slots (95.6%).
- Exact raw transcriptions were identical for 95/105 slots (90.5%).
- Run 1 sequential provider time was 37.3 seconds total; individual requests
  ranged from 2.6 to 11.9 seconds.
- Run 2 sequential provider time was 363.2 seconds total; individual requests
  ranged from 28.1 to 120.4 seconds.
- Three of four run-2 requests exceeded the product's default 75-second
  connection timeout. The evaluator used the supported five-minute maximum,
  so the calls completed instead of being recorded as ambiguous timeouts.

## Supporting deterministic evidence

The live accuracy result was complemented by focused local checks:

- 42/42 grading, normalization, point-policy, and aggregation tests passed.
- 6/6 AI grading response-validation tests passed.
- 17/17 initial-grading and adjudication workflow tests passed.
- All 5 pinned external files passed the real preprocessing smoke test.
- The live model capability and one-question full-page schema probes passed.

These checks establish implementation and safety-contract conformance. They do
not increase the empirical accuracy sample size.

## Findings and recommended next work

1. **Keep all AI grades in teacher review.** This run does not justify
   auto-finalization or production profile approval.
2. **Make choice parsing explicit.** Normalize only safe choice-label forms
   such as `A`, `A.`, circled labels, and Japanese `ア` variants before applying
   the choice policy. Keep ambiguous multi-character text in review.
3. **Do not reject an otherwise useful whole paper for one item.** Preserve
   valid items and quarantine only deterministic contradictions for teacher
   review, while retaining a paper-level warning.
4. **Use teacher-authored element rubrics for semantic questions.** The current
   single prose answer is insufficient for reproducible partial credit.
5. **Re-evaluate with real Japanese school data.** Build a versioned,
   de-identified set with genuine student handwriting, interwoven layouts,
   teacher-adjudicated item scores, Kanji rules, and detailed rubrics.
6. **Measure timeout policy with the intended processing strategy.** Direct
   urgent requests need a timeout supported by observed p95 latency; economy
   grading should be evaluated separately through Gemini Batch.

## Limitations and interpretation

- The real scored corpus is English and outside the intended Japanese
  middle-school domain.
- Only 14 explicit nonblank short-answer scores are available.
- Images were rendered to 2,976 × 4,209 PNG pages for this evaluation; this is
  not byte-identical to the default 300-DPI production preprocessing output.
- Repeating the same papers measures stability, not a larger independent
  sample. Confidence intervals use the unique labeled items only.
- Even perfect results on this sample would be insufficient to prove the
  production 99.5% gates with a reliable lower confidence bound.
- No profile was approved or activated from this exploratory run.

## Evidence files

- `output/accuracy/gemini-3.5-flash-lite-basic-evidence-run-1.json`  
  SHA-256: `af61bd8ec23c722b8af5f21f1a32d70b638eedba3baa63e564837c1498b8315c`
- `output/accuracy/gemini-3.5-flash-lite-basic-evidence-run-2.json`  
  SHA-256: `5dfd9043fe1226fe416aa5f8526b77ef4317a67cb89124a942dff5addc793ab5`
- Reusable evaluator:
  `tests/OokiGrader.ProviderContract.Tests/GeminiGradingAccuracyEvaluationTests.cs`
