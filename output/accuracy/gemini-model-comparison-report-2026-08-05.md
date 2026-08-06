# Ooki Grader — Gemini model comparison

**Evaluation date:** 2026-08-05

**Status:** Exploratory; teacher-review-only

**Release decision:** **Keep the current model for now; do not enable automatic finalization**

> This report is the pre-fix baseline. Blank/missing handling, point-level
> quarantine, deterministic reconciliation, and choice-label normalization
> were subsequently implemented and rerun. See
> [grading-fix-verification-report-2026-08-05.md](grading-fix-verification-report-2026-08-05.md).

## Executive summary

Ooki Grader's current grading prompt, full-page image request, structured JSON
schema, and local response validator were tested against three Gemini Flash
models. Gemini 3.1 Pro Preview was also attempted, but the supplied project had
no generation quota for it.

No tested setting met the system's automatic-grading safety gates. The safest
operational decision is to keep `gemini-3.5-flash-lite` at `MINIMAL` for now,
with every AI result remaining subject to teacher review. It was the only
completed setting that returned an explicit observation for all 105 real-paper
question slots, and it had the lowest measured sequential provider time in this
campaign. It still produced three incorrect-credit false positives and failed
local deterministic consistency validation on two of three real papers.

`gemini-3.5-flash` at `MEDIUM` was the strongest alternative in raw objective
agreement and passed two of three real-paper responses, but it classified all
31 blank short-answer slots as missing instead of returning blank observations.
Its incorrect-credit false-positive rate was 7.4%, and it consumed more time
and tokens. It is promising enough for a future repeated evaluation after the
blank-versus-missing contract is made more explicit, but it is not a safe
production switch today.

`gemini-3.6-flash` at `MINIMAL` reduced incorrect-credit false positives to one,
but returned only 74/105 real items, passed only one real-paper response, and
had weaker short-answer agreement. At its documented default `MEDIUM` thinking
level it returned a `300` milli-point award where the rubric requires 1,000-point
increments; the evaluator correctly rejected the response before persistence.

The API name for “Gemini 3.1 Pro” is `gemini-3.1-pro-preview`. The account's
Models API listed it with `generateContent`, but its first grading request
returned `gemini_rate_limited`. Google documents no free API tier for this
preview model, so it was not retried and no accuracy score is reported.

## Test design

The three completed settings used byte-identical inputs and the same grading
contract:

| Item | Fixed value |
|---|---|
| Prompt | `answer-transcribe-grade-v1.1.0` |
| Response schema | `answer_transcribe_grade_v1` |
| Prompt content hash | `9c2ae3112d824c1419ad82ee7e9b3fcc32176a787d1a12008ad716b78f27c24a` |
| Harness | `gemini-initial-grading-full-page-v3-model-comparison-direct-run` |
| Provider path | Official Gemini `generateContent` through `GeminiDirectClient` |
| Media | Full-page PNG, `MEDIA_RESOLUTION_HIGH` |
| Invariant manifest hash | `f99b7e3606306daa2595f9a0ca943fba17fcaf3c755557d8979903fe595f740b` |

The invariant manifest hash covers the prompt/schema metadata, dataset
description, all source hashes, and every evaluation-image hash. Requested and
actual model names matched for every successful call.

The real corpus contains three anonymized English handwritten university exam
papers: 105 question slots, 56 attempted A–D choices, 14 nonblank short answers
with explicit teacher item scores, and 31 blank short-answer cells. A separate
five-question synthetic Japanese geography sheet provides only a controlled
functional check; it is not genuine student handwriting.

The comparison uses one contemporaneous completed run per setting. It does not
establish repeatability. Two earlier 3.5 Flash-Lite runs remain useful context,
but repetitions of the same papers do not increase the independent sample size.

## Results

### Completion and system acceptance

| Requested setting | Live outcome | Real coverage | Real responses accepted | System-usable real items | Japanese control |
|---|---|---:|---:|---:|---:|
| `gemini-3.5-flash-lite` / `MINIMAL` | Completed | 105/105 (100.0%) | 1/3 | 35/105 (33.3%) | 5/5 |
| `gemini-3.5-flash` / `MEDIUM` | Completed | 74/105 (70.5%) | 2/3 | 51/105 (48.6%) | 5/5 |
| `gemini-3.6-flash` / `MINIMAL` | Completed | 74/105 (70.5%) | 1/3 | 27/105 (25.7%) | 5/5 |
| `gemini-3.6-flash` / `MEDIUM` | Contract failure | — | 0 | 0 | Not reached |
| `gemini-3.1-pro-preview` / `LOW` | Quota-blocked | — | 0 | 0 | Not reached |

Both full-size Flash models returned all 60 objective items and all 14 nonblank
short answers, but put each of the 31 truly blank short-answer IDs in
`missing_question_ids`. The current workflow treats missing questions as
unobserved work requiring attention; it does not silently assume they are
blank. The reported 0/31 blank-observation rate therefore reflects a real
workflow-coverage problem, even though the omitted slots were in fact blank.

The 3.6/MEDIUM failure was a local safety success: the provider returned a
schema-shaped response, but one proposed score was `300` milli-points despite a
1,000-milli-point increment. The evaluator rejected it before evidence could be
counted as an accuracy run.

### Objective grading

| Setting | Strict transcription | Score agreement | Auto-credit precision | Incorrect-credit FP rate | Under-credit FN rate |
|---|---:|---:|---:|---:|---:|
| 3.5 Flash-Lite / MINIMAL | 43/56 (76.8%) | 51/56 (91.1%) | 27/30 (90.0%) | 3/27 (11.1%) | 2/29 (6.9%) |
| 3.5 Flash / MEDIUM | 45/56 (80.4%) | 53/56 (94.6%) | 28/30 (93.3%) | 2/27 (7.4%) | 1/29 (3.4%) |
| 3.6 Flash / MINIMAL | 44/56 (78.6%) | 52/56 (92.9%) | 26/27 (96.3%) | 1/27 (3.7%) | 3/29 (10.3%) |

None met the current gates of at least 99.5% objective auto-credit precision,
at most 0.5% incorrect-credit false positives, and at least 97% agreement after
review routing. Results are raw model proposals; the local validator prevented
contradictory deterministic results from being accepted as valid papers.

### Teacher-scored short answers

| Setting | Exact score agreement | 95% Wilson interval | Mean absolute error | Over-credit | Under-credit |
|---|---:|---:|---:|---:|---:|
| 3.5 Flash-Lite / MINIMAL | 7/14 (50.0%) | 26.8%–73.2% | 0.50 points | 2 | 5 |
| 3.5 Flash / MEDIUM | 7/14 (50.0%) | 26.8%–73.2% | 0.57 points | 2 | 5 |
| 3.6 Flash / MINIMAL | 5/14 (35.7%) | 16.3%–61.2% | 0.64 points | 0 | 9 |

The sample is too small and the supplied prose rubrics are too weak for a
production semantic-grading claim. All semantic proposals must continue to be
reviewed by a teacher.

### Latency and token use

| Setting | Four-call sequential provider time | Slowest call | Total tokens | Thinking tokens |
|---|---:|---:|---:|---:|
| 3.5 Flash-Lite / MINIMAL | 62.4 s | 28.6 s | 37,035 | 0 reported |
| 3.6 Flash / MINIMAL | 83.5 s | 43.2 s | 32,337 | 0 reported |
| 3.5 Flash / MEDIUM | 136.5 s | 62.7 s | 47,202 | 14,074 |

These are sequential provider latencies from one run, not throughput or p95
estimates. An earlier 3.5 Flash-Lite repeat took substantially longer, so this
small table must not be used for capacity planning.

### Japanese functional control

Every completed setting transcribed and scored the five synthetic Japanese
items exactly and passed local validation. This confirms that all three models
can handle the clean interwoven Japanese layout. It says nothing reliable about
real Japanese student handwriting, erasures, faint pencil, crowded answers, or
school-specific rubrics.

## Model availability and cost context

- `gemini-3.6-flash` is a stable model with image input and structured output.
- `gemini-3.1-pro-preview` is the current API ID for Gemini 3.1 Pro; it is a
  preview endpoint, not a stable `gemini-3.1-pro` alias.
- Google documents standard free API tiers for the tested Flash models, but no
  free API tier for 3.1 Pro Preview. The supplied project could list Pro but
  could not generate with it.
- Google's pricing page states that free-tier content may be used to improve
  its products. Real school data must not be tested on that tier without an
  approved privacy/legal decision and appropriate provider configuration.

Official references:

- [Gemini 3.6 Flash model card](https://ai.google.dev/gemini-api/docs/models/gemini-3.6-flash)
- [Gemini 3.5 Flash model card](https://ai.google.dev/gemini-api/docs/models/gemini-3.5-flash)
- [Gemini 3.1 Pro Preview model card](https://ai.google.dev/gemini-api/docs/models/gemini-3.1-pro-preview)
- [Gemini API pricing](https://ai.google.dev/gemini-api/docs/pricing)
- [Gemini thinking-level support](https://ai.google.dev/gemini-api/docs/generate-content/thinking)

## Recommendation

1. Keep `gemini-3.5-flash-lite` / `MINIMAL` as the current application model
   only because it has the best full-result coverage and lower observed cost and
   latency. Keep mandatory teacher review; do not approve automatic finalization.
2. Clarify the prompt contract: visible empty answer fields must be returned as
   `blank=true`; `missing_question_ids` must be reserved for questions that
   cannot be located or observed. Re-evaluate 3.5 Flash after that change.
3. Preserve strict point-increment and deterministic-consistency validation.
   The 3.6/MEDIUM result demonstrates why provider JSON schema alone is not
   sufficient.
4. Do not draw a conclusion about 3.1 Pro. Enable a capped paid test project
   only if a Pro comparison is still wanted, then run the exact same harness at
   `LOW` and record the spend and quota configuration.
5. Build a privacy-reviewed golden set of genuine Japanese exam pages from
   大木スクール, with teacher-adjudicated item scores, before selecting a
   production model or changing review policy.

## Evidence

Completed evidence:

- `gemini-3.5-flash-lite-model-comparison-run-1.json`

  SHA-256 `99892fea02c716ad50ab405cdc6574795727cd83b6ad113ceab80b4d4f5ffe80`
- `gemini-3.5-flash-medium-model-comparison-run-1.json`

  SHA-256 `de4f67c95f821b6d97c102411813c07667b0de3bd96ccdcbfc1edcf249b2ffda`
- `gemini-3.6-flash-model-comparison-run-1.json`

  SHA-256 `6e5bb7ea80be586df533fced22403549e73f9c42c4e63437e4580fe7f1fc7252`

Failure logs:

- `gemini-3.6-flash-medium-model-comparison-run-1.log` — invalid point
  increment; SHA-256
  `dd9404bb48c134be659d7255ac2183b841c01b4b0e5338ee9383cbaeb7fe5c46`
- `gemini-3.1-pro-preview-model-comparison-run-1.log` —
  `gemini_rate_limited`; SHA-256
  `3e4b79c9c4809e81762031a7cd1b243e60b2b83aa77a35c2012abf1973861d1e`

The API credential is not present in these artifacts. No application model
profile was approved or activated by this evaluation.
