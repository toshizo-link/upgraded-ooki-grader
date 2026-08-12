# Testing, accuracy, quality, and observability

> **Current-flow note (2026-08-06):** Batch/priority/failover cases below are
> retained only as regression coverage for legacy persistence and recovery.
> Release acceptance for new work uses the provider-neutral standard queue;
> those controls are not exposed to teachers.

## 1. Quality strategy

Ooki Grader's quality hierarchy is:

1. avoid awarding incorrect credit or assigning the wrong student;
2. minimize teacher correction effort;
3. produce repeatable, explainable results;
4. meet usable turnaround;
5. minimize provider and operational cost.

A cheaper or faster profile cannot ship if it misses the accuracy gate. AI model names and generic benchmarks are not acceptance evidence; school-style scans and teacher-adjudicated truth are.

## 2. Test levels

| Level | Scope | Run |
|---|---|---|
| Static | compile, lint, types, analyzers, licenses, secrets | every change |
| Unit | normalization, Kanji, scores, state machines, policies | every change |
| Component | repositories, file store, PDF, adapters with fixtures | every change |
| Contract | Ooki API, Gemini/OpenRouter simulated/recorded schemas | every change |
| Integration | SQLite + filesystem + workers + child processes | every change/nightly |
| Browser E2E | complete staff workflows | PR smoke/nightly full |
| AI evaluation | real paid provider on approved de-identified set | candidate/profile release |
| Performance | uploads, DB, preprocessing, UI, queues | nightly/release |
| Resilience | crashes, outage, disk, duplicate delivery | nightly/release |
| Security baseline | auth, upload, paths, secrets, dependency | PR/release |
| UAT | teachers with representative school workflow | pilot/release |

Tests never depend on the live AI provider for ordinary CI. Provider adapters use deterministic simulators and sanitized recorded responses. Live calls are an explicit budgeted evaluation job.

## 3. Golden evaluation dataset

### 3.1 Composition target before production

Minimum:

- 30 distinct test templates;
- Japanese language/Kanji, English, mathematics, science, and social studies where relevant;
- 500 completed paper submissions;
- 10,000 question answers;
- 300 distinct school-approved or synthetic student-name styles/crops;
- printed, pencil, ballpoint, faint, erased, overwritten, and messy samples;
- scanner models/settings used by the school;
- 200 intentionally blank answers;
- 200 unreadable/cropped/skewed cases;
- 300 Kanji-required cases including kana-only equivalents;
- 200 partial-credit/rubric cases;
- 100 adversarial/instruction-like answers;
- 10 blank tests with separate answer keys;
- 10 tests with model answers written/printed directly on the uploaded test;
- conflicting/missing/unmatched model-answer sources.

The dataset should grow during pilot. Small early sets report confidence intervals and cannot enable auto-finalization.

### 3.2 Ground truth

- Two qualified teachers independently label disputed/semantic items.
- Disagreement is adjudicated and preserved.
- Ground truth includes raw transcription, accepted answer, score, Kanji rule, blank/unreadable, student identity, regions, and source-answer provenance.
- Reviewers do not see provider/model when adjudicating.
- Changes create dataset versions; past profile evaluations remain reproducible.
- Evaluation data never mixes into production progress.

### 3.3 Partitions

- development: prompt/preprocessing iteration;
- calibration: confidence thresholds and local matching;
- holdout: release decision, never prompt-tuned directly;
- challenge: worst quality/adversarial/rare layouts;
- post-pilot drift: recent school samples.

Avoid page/answer/student leakage between partitions where it would inflate results.

## 4. Accuracy metrics and gates

All gates apply to the **eligible-quality subset** defined in advance. Coverage is reported separately so a system cannot achieve precision by reviewing everything.

### 4.1 Automatic grading-key generation

| Metric | Production gate |
|---|---:|
| Printed question detection recall | ≥ 99.0% |
| Question label/text materially correct | ≥ 98.0% |
| Answer-region intersection-over-union median | ≥ 0.90 |
| Supplied printed model-answer exact transcription | ≥ 99.0% |
| Supplied handwritten model-answer exact transcription | ≥ 97.0% |
| Supplied answer mapped to correct question | ≥ 99.5% |
| Supplied answer silently replaced by solved answer | 0 cases |
| Conflict/missing supplied answer correctly blocks publish | 100% challenge cases |

All drafts still require teacher publish. A failure lowers automation usefulness but cannot publish automatically.

### 4.2 Student name assignment

| Metric | Gate |
|---|---:|
| Precision among auto-assigned submissions | ≥ 99.5% |
| Wrong exact student-number assignments | 0 |
| Expected auto-assignment coverage on clear samples | ≥ 85% target, not safety gate |
| Ambiguous close-name cases sent to review | ≥ 99% |

Use Wilson lower confidence bound or another predeclared method; do not claim 99.5% from a handful of cases. If the lower bound does not meet the gate, auto-assignment remains disabled.

### 4.3 Answer transcription

Report character error rate and exact-answer rate by:

- question type;
- pencil/pen;
- Kanji/kana/Latin/numeric;
- scanner/quality;
- full-page vs crop/contact-sheet input;
- provider/model/profile.

Initial targets:

- clear numeric/choice exact transcription ≥ 99.5%;
- clear printed/short handwritten Japanese exact transcription ≥ 98%;
- Kanji character error rate low enough that grading precision gate passes.

### 4.4 Grading

| Metric | Gate |
|---|---:|
| Auto-finalized objective question precision | ≥ 99.5% |
| Auto-finalized incorrect-credit false-positive rate | ≤ 0.5% |
| Objective/short-answer agreement after required review routing | ≥ 97% |
| Kana-only accepted when Kanji required | 0 auto-credit cases |
| Score outside configured range/increment | 0 |
| Total arithmetic mismatch | 0 |
| Subjective item auto-finalized | 0 |
| Unreadable item silently finalized as blank/wrong | 0 |

Semantic short-answer and descriptive results are proposals until teacher
finalization of the paper. Initial goal is ≥92% exact score agreement, with
all lower-confidence, conflicting, partial, or unreadable results reviewed;
descriptive type alone does not force per-question review.

### 4.5 Ease-of-use

- median teacher time to review a clear 50-question paper ≤ 60 seconds after AI draft;
- model-answer source creation completed without developer help by ≥90% of trained UAT teachers;
- normal upload-to-review workflow task success ≥95%;
- critical user error rate 0 in UAT;
- System Usability Scale target ≥80 or equivalent predeclared measure;
- correction reason distribution is monitored to find confusing automation.

### 4.6 Cost and latency

Report, do not hide:

- cost per template generated;
- cost per paper and per question;
- cost per finalized paper including rechecks/retries;
- estimated teacher minutes per paper;
- standard AI request turnaround p50/p95 by provider/model;
- OpenRouter queue + provider latency p50/p95;
- schema/error/retry rate;
- cost difference between crop/contact-sheet/full-page strategies.

The recommended profile minimizes cost only among profiles meeting accuracy/ease gates.

## 5. Provider/profile evaluation protocol

This protocol applies to every checked-in release-profile candidate and every
advanced/manual provider-profile candidate, including OpenRouter. It does not
run as part of routine Gemini key creation or replacement at an installed
school. That path instead requires the full candidate-key authentication,
exact-model, image, strict-structured-output, usage, and representative
image-task probe to pass before any persistence.

For every release or advanced/manual candidate profile:

1. freeze provider, exact model, routing, endpoint, prompt, schema, reasoning/media settings, preprocessing, and evaluator version;
2. verify capabilities with synthetic fixtures;
3. run development set;
4. tune only on development/calibration;
5. run holdout once for release decision;
6. compute all metrics with sample counts and uncertainty;
7. compare teacher review time blind;
8. record provider-reported usage/actual cost;
9. run challenge set;
10. sign and store immutable evaluation report.

For OpenRouter:

- record requested and actual returned model/provider/endpoint where available;
- evaluate with the intended `require_parameters`, fallback, and routing settings;
- if endpoint routing can change behavior, stratify metrics by actual route;
- a fallback model is evaluated as its own profile.

For official Gemini:

- test standard and Batch strategies;
- verify result parsing and batch keyed mapping;
- test File API expiry/cleanup and non-idempotent creation recovery.

## 6. Model-answer source tests

Required cases:

- printed answer inside blank line;
- handwritten answer in pencil;
- red teacher annotation and circled answer;
- separate answer-key list `問1: ...`;
- answer key with different pagination;
- multiple acceptable answers separated by slash;
- answer explanation mistaken for answer;
- sample answer in question text that is not authoritative;
- two supplied sources disagree;
- checked source has one missing answer;
- answer contains Kanji and must preserve script;
- AI solved comparison disagrees;
- only solved paper available, with answer removed from question text;
- non-model answered paper produces `ai_proposed`, never
  `provided_model_answer`, even when its visible response happens to be
  correct;
- non-model answered paper with an intentionally wrong response is solved
  independently rather than copied;
- non-model answered paper paired with a separate answer key uses the matched
  key answer and its authoritative provenance;
- ambiguous filled paper is not silently classified as authoritative;
- mapping confidence low and manual remap.

Invariant: `provided_model_answer` can originate only from a source explicitly
marked as containing/separate model answers. A
`contains_non_model_answers` source can never supply that provenance.

## 7. Deterministic unit-test catalog

### Japanese text

- NFKC width normalization;
- ordinary and ideographic spaces;
- hiragana/katakana kept distinct unless configured;
- Kanji/Han detection including iteration marks;
- furigana not confused with answer;
- combining marks;
- old/new character variants only when configured;
- punctuation/newline handling;
- exact phonetic exception;
- empty/whitespace response.

### Scoring

- points milli arithmetic;
- partial increments;
- no negative/over-max;
- zero maximum invalid;
- total on override/reopen/regrade;
- percentage basis points rounding;
- duplicate logical question IDs;
- active run/supersession.

### Dates/retention

- three calendar months across February/leap year;
- May 31 to February end;
- DST-independent `Asia/Tokyo`;
- exact cutoff equality;
- quota high/low marks;
- shared deduplicated object references;
- interrupted two-phase deletion;
- deleted scan leaves result.

### Student matching

- width/space normalization;
- same surname/given names;
- old surname alias;
- student number conflict;
- inactive/expected roster;
- first/second margin;
- duplicate submission.

## 8. Component and integration tests

### Database

- migrations from every supported release;
- foreign keys and unique constraints;
- WAL crash/restart;
- write concurrency/ETags;
- online backup;
- integrity failure handling;
- 10,000-student/200,000-submission fixture.

### File store

- atomic promotion;
- hash dedupe/collision defense;
- interrupted intent;
- missing object;
- path traversal and reparse point;
- byte counters;
- range download;
- `410` after deletion;
- low disk admission.

### Preprocessing

- supported PDF/image types;
- corrupt/encrypted/huge/decompression cases;
- orientation/skew;
- page matching/order/missing/duplicate;
- blur/cutoff/darkness;
- answer crop coordinates;
- child-process timeout/crash;
- no answer-stroke destruction in golden pixel comparisons.

### Reports

- Japanese fonts embedded;
- no missing glyphs;
- long question wrapping;
- page breaks/header repeats;
- zero/one/many questions;
- scan deleted;
- corrected/superseded result;
- PDF parser validation and pixel-render comparison.

## 9. API, browser, and workflow tests

E2E scenarios:

- bootstrap and role matrix;
- roster CSV encodings/errors;
- grading key from blank source;
- grading key from checked model-answer source;
- publish immutability/version clone;
- upload interrupt/resume/duplicate;
- offline upload/provider recovery;
- ambiguous name review;
- Kanji-required grading;
- AI output invalid/retry/review;
- override/finalize/reopen;
- progress date/filter;
- PDF export;
- age/quota cleanup;
- Gemini candidate-key initial setup: full pass atomically persists and enables
  exactly four current profiles, while initial failure persists nothing;
- Gemini replacement failure/timeout/ambiguous result preserves the previous
  key, connection revision, and four active profile revisions;
- successful stored-key `:test` and startup reconciliation self-heal
  exact-current Gemini profiles without changing in-flight profile snapshots;
- normal Gemini UI has no evaluation, pilot-approval, or manual-activation
  controls; OpenRouter/legacy advanced endpoints remain compatible;
- both provider settings and advanced profile switch;
- maintenance/backup/restore.

Use accessibility automation plus manual keyboard/screen-reader smoke testing.

## 10. Resilience and fault injection

Inject:

- process kill during upload finalize;
- process kill during file rename/database intent;
- SQLite busy/I/O error;
- child rasterizer hang/crash;
- network disconnect before/after provider send;
- Gemini candidate probe timeout/ambiguous response before persistence;
- transaction failure at each secret/connection/four-profile commit boundary,
  proving no partial commit and preservation of the prior working state;
- official Gemini batch create ambiguous response;
- batch output missing/duplicate keys;
- OpenRouter 402, 429 with `Retry-After`, 502/503, error inside 200 body;
- schema truncation/content filter;
- provider actual model mismatch;
- host reboot with jobs leased;
- physical disk crosses reserve;
- backup destination removed;
- clock offset;
- certificate expiry;
- two teachers override concurrently.

Pass means no silent data loss, unauthorized change, duplicate effective grade, or uncontrolled duplicate provider submission.

## 11. Performance tests

Hardware-matched tests:

- 50 simultaneous browser sessions;
- 10 concurrent uploads, 30 files/session;
- 250 MB maximum upload and resume;
- 1,000-page local rejection/splitting boundary;
- preprocessing queue under 32 GB and 16 GB memory profiles;
- 100,000 results progress/search;
- 10,000-question template corpus;
- retention deletion of 150 GiB synthetic sparse/representative store;
- report burst of 100;
- direct Gemini batch assembly up to app limits with mocked provider;
- OpenRouter adaptive concurrency under rate limits.

Capture CPU, working set, handles, disk queue, SQLite wait/WAL, response p50/p95/p99, and queue delay.

## 12. Security baseline tests

Proportionate and automated:

- secrets absent from source/build/frontend/logs;
- authentication/session/CSRF/role/IDOR;
- path traversal and malicious filename;
- PDF/image malformed corpus;
- XSS through names/question/answer/provider text;
- SQL injection probes;
- rate/body limits;
- TLS/firewall/ACL installer checks;
- API keys unretrievable from peers;
- diagnostic bundle redaction.

## 13. Observability acceptance

Every production incident scenario must be diagnosable using:

- business state;
- job/dispatch state;
- correlation ID;
- provider/profile/model/request ID;
- sanitized error;
- input/result hashes;
- measured usage/cost;
- audit/revision history.

Dashboards must reveal:

- accuracy drift via teacher override rate by profile/question type;
- name-auto-assignment correction rate;
- supplied-answer mapping corrections;
- schema failure and unreadable rates;
- provider route changes;
- cost per paper;
- queue/disk/backup health.

Alerts must not include student names or answer text.

## 14. CI quality gates

Pull request:

- clean build;
- formatter/lint/type;
- unit/component/contract;
- changed migration test;
- OpenAPI client drift;
- secret/license/vulnerability scan;
- targeted browser smoke;
- coverage on changed critical code;
- no snapshot update without review.

Release candidate:

- full integration/E2E;
- performance baseline;
- resilience suite;
- installer upgrade/clean install;
- Japanese PDF visual approval;
- both provider adapter simulations;
- live candidate-profile evaluation;
- UAT;
- backup/restore;
- SBOM/signature.

## 15. Release decision

Production release requires:

- no open P0 defect;
- all arithmetic/data-integrity invariants;
- checked-in task-profile revisions proposed for release pass their accuracy
  gates; installed-school Gemini activation of those exact revisions passes the
  full capability/image-task gate;
- auto-assignment/finalization disabled where sample confidence is insufficient;
- teacher UAT sign-off;
- measured cost within configured expectation;
- commissioning/rollback/runbooks complete;
- backup restore proven.

A near-budget deadline never justifies waiving accuracy or integrity gates.
