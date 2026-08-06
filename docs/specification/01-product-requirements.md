# Product requirements and acceptance criteria

## 1. Requirement conventions

Every production requirement has an identifier. Acceptance tests should reference these identifiers. Priorities use:

- **P0:** required for first production release;
- **P1:** required before broad rollout but may follow a controlled pilot;
- **P2:** planned enhancement.

Unless stated otherwise:

- dates shown to users use Japanese local time and the `Asia/Tokyo` time zone;
- timestamps are stored in UTC;
- destructive staff actions require authorization, confirmation, and an audit event;
- list endpoints are paginated and searchable;
- a failed AI call must not lose the uploaded scan or teacher edits.

## 2. Authentication, authorization, and staff administration

### FR-AUTH-001 — Initial administrator bootstrap (P0)

The installer MUST create exactly one initial administrator through a local, host-only setup flow. The bootstrap secret MUST expire after first successful use or 24 hours, whichever comes first.

**Acceptance criteria**

- A remote peer cannot reach the bootstrap route.
- The administrator sets a unique username and password.
- No default password remains after completion.
- A second bootstrap attempt returns a completed state and no secret.

### FR-AUTH-002 — Staff sign-in and sign-out (P0)

Staff MUST authenticate before accessing student, scan, grade, progress, export, system, or API data.

**Acceptance criteria**

- Valid credentials create a secure server-side session.
- Invalid credentials return one generic error without revealing whether the account exists.
- Five failed attempts in 15 minutes trigger a temporary account/IP throttle.
- Sign-out invalidates the server session.
- Disabled accounts cannot create or refresh sessions.

### FR-AUTH-003 — Role-based authorization (P0)

The system MUST enforce the roles described in the vision document on the server for every request. Hiding UI controls is not sufficient.

### FR-AUTH-004 — Staff account lifecycle (P0)

Administrators MUST be able to create, rename, disable, re-enable, reset, and role-assign staff accounts. Accounts referenced by audit records MUST NOT be hard-deleted.

### FR-AUTH-005 — Session behavior (P0)

Sessions MUST expire after 30 minutes of inactivity and after 12 hours absolute duration. A deployment MAY configure shorter values. Active edits SHOULD warn the user two minutes before expiry.

## 3. Student roster

### FR-STU-001 — Student CRUD (P0)

Teachers and administrators MUST be able to create and edit a student with:

- internal student number;
- family name and given name;
- family/given name in kana;
- preferred display name;
- optional school/class/course labels;
- enrollment status;
- optional notes protected from scan-operator access.

The display name, kana, and student number are required for active students. The internal database identifier MUST NOT be derived from a mutable student number.

### FR-STU-002 — Name aliases (P0)

Each student MAY have multiple recognition aliases, including old surname, common spacing, romanization, and expected handwritten variants. Aliases MUST be normalized and uniqueness conflicts shown before saving.

### FR-STU-003 — CSV import/export (P0 import, P1 export)

The roster importer MUST:

- accept UTF-8 with BOM and Shift_JIS CSV;
- show column mapping and a preview;
- validate the entire file before applying changes;
- support create, update by student number, and skip;
- reject ambiguous duplicate student numbers;
- produce a row-level error report;
- apply a valid import transactionally.

### FR-STU-004 — Deactivation, merge, and erasure (P0/P1)

- Deactivation MUST preserve historical results.
- A P1 merge operation MUST move aliases and result references to the survivor while recording a reversible audit mapping.
- Permanent personal-data erasure MUST be a separate administrator workflow governed by the school's policy; it MUST NOT be conflated with scan retention.

### FR-STU-005 — Roster search (P0)

Search MUST match student number, Japanese name with or without spaces, kana, and configured aliases. Results MUST visually distinguish inactive students.

## 4. Test sources and automatic grading-key generation

**Product terminology:** the user-facing feature is **採点基準の自動作成 / Automatic Grading-Key Generation**, not “Q&A pair generation.” A grading key contains question definitions, authoritative/model answers, accepted variants, points, answer regions, Kanji policy, and scoring rubrics. “Q&A pair” may be used informally but is not the primary UI term.

### FR-TPL-001 — Test source ingestion (P0)

A teacher MUST be able to create a test template from:

- one PDF;
- multiple PDF parts;
- JPEG, PNG, or TIFF page images;
- pages scanned elsewhere and uploaded from any peer.

For every source, the system MUST propose one source role from safe local
evidence such as the file name and the other files selected in the same
operation. The proposed role MUST remain visible and editable before the
grading-key request is submitted:

- `blankTest` — blank question/test paper;
- `containsModelAnswers` — the question paper itself contains filled/printed model answers;
- `containsNonModelAnswers` — the question paper contains answers that are not
  an authoritative model answer; AI must solve the printed questions
  independently;
- `separateAnswerKey` — a separate solutions/model-answer document.

The teacher only needs to intervene when the proposal is wrong or ambiguous.
Manual role selection remains available. The UI uses one per-file select with:

- `問題のみ（未記入）`;
- `模範解答入り`;
- `記入済み答案（AIが正答を作成）`;
- `別紙の模範解答`.

The third choice is intentionally explicit: written answers on that paper are
not treated as correct answers. The AI derives its proposed answer from the
printed question and supporting material. If the system cannot safely tell
whether a filled paper is authoritative, it MUST prefer a non-authoritative
proposal and ask for this one quick confirmation rather than silently trusting
the writing as a model answer.

After upload completes, the role-override window closes, and before creating a
new draft, the system MUST compare the exact `(content-addressed source,
source role)` set with published template versions. An exact match is offered
for reuse by default, while deliberate creation of a new draft remains
available. Identical bytes with a different answer-authority role MUST NOT be
treated as an exact match.

The system MUST preserve the original, create normalized page images, detect page count, and report corrupt, encrypted, unsupported, or low-quality files.

### FR-TPL-002 — Template metadata (P0)

A template MUST include title, subject, optional grade/course/category, source,
notes, and default point policy. Title is required. The upload flow MUST infer
initial title/subject values from local file names, and the extraction task MAY
replace recognized placeholder values with high-confidence printed metadata.
Explicit teacher values MUST NOT be overwritten. Templates have `draft`,
`active`, `retired`, and `archived` lifecycle states.

### FR-TPL-003 — Automated grading-key draft generation (P0)

From the normalized blank/model-answer/non-model-answer/answer-key sources and
their roles, the system MUST request a structured AI draft containing:

- question number/label and ordering;
- full question text suitable for later report display;
- question type;
- answer region and optional question region coordinates;
- expected answer;
- accepted variants;
- maximum points;
- grading mode;
- rubric/notes;
- confidence and warnings;
- default value for “allow answers not written in Kanji.”

Generated content MUST remain a draft until a teacher publishes it. The default
review experience MUST show blocking or low-confidence exceptions first. A
single audited action MAY verify all non-blocking, high-confidence proposals;
it MUST skip unresolved answers, invalid/missing regions, unsupported content,
source-authority conflicts, and other blocking issues.

### FR-TPL-003A — Supplied model-answer authority (P0)

When a source is marked `containsModelAnswers` or `separateAnswerKey`, the system MUST:

- treat visible supplied answers as the authoritative answer source;
- extract/transcribe them instead of silently replacing them with an independently solved answer;
- store answer provenance as `provided_model_answer` with source file/page/region;
- separate answer annotations from question text used in reports;
- mark unreadable, missing, unmatched, or conflicting answers for teacher review;
- show any independently computed AI answer only as a separate comparison warning, never as the authoritative value;
- preserve the exact supplied script, including Kanji/kana, before normalization;
- allow the teacher to correct the extraction.

If multiple authoritative sources disagree, publication is blocked until the
teacher selects/corrects the answer. If an authoritative source does not
contain an answer for a question, the field remains unresolved by default. A
separate optional action MAY propose a missing answer, but it must be labeled
`AI-proposed`, not `provided`.

### FR-TPL-003B — Source pairing and mapping (P0)

The system MUST map blank-test questions, filled model-answer pages,
non-model answered pages, and separate answer-key entries by page layout,
printed question label, and question text. The teacher can manually remap an
unmatched source answer. A test containing model answers MAY be the only
uploaded source; in that case the system derives question text while excluding
the answer annotation from report question text.

### FR-TPL-003C — Non-model-answer independence (P0)

When a source is marked `containsNonModelAnswers`, the system MUST:

- treat every visible written/filled answer as non-authoritative evidence that
  MUST NOT become the expected answer;
- extract the printed question text and supporting material while excluding
  the visible response from the question text;
- independently solve each question and store the result as `ai_proposed`,
  unless a paired authoritative source supplies that question's answer;
- leave answer-source provenance empty for the independent proposal;
- prefer a paired `containsModelAnswers` or `separateAnswerKey` answer when one
  exists, preserving that answer's authoritative provenance;
- raise a teacher-review issue when the printed question and written response
  cannot be separated safely or the independent solution is uncertain.

Changing this role after generation MUST require regenerating the proposal so
that an answer previously treated as authoritative cannot remain silently in
the draft. The teacher can always correct the resulting proposal before
publication.

### FR-TPL-004 — Manual question editor (P0)

Teachers MUST be able to:

- add, delete, duplicate, and reorder questions;
- edit text, expected answers, variants, points, type, and rubric;
- draw, move, and resize question/answer/name regions over a page preview;
- split one detected question into several or merge detections;
- apply a Kanji policy to selected/all questions;
- preview the total points;
- run validations before publish.

All unsaved editing SHOULD be protected with local draft recovery.

### FR-TPL-005 — Kanji answer policy (P0)

Every question MUST expose the checkbox:

> 漢字以外の解答を許可する  
> Allow an answer not written in Kanji

Rules:

- checked: a semantically correct hiragana, katakana, or supported phonetic form MAY receive credit according to normal variant/rubric rules;
- unchecked: when the canonical expected answer contains Kanji, a phonetic-only equivalent MUST NOT receive credit;
- unchecked has no special effect if the canonical expected answer contains no Kanji;
- the teacher MAY list specific non-Kanji variants as accepted; this explicit exception overrides the checkbox and is visibly marked;
- the stored result MUST include whether the Kanji rule affected the grade.

### FR-TPL-006 — Publish and version (P0)

Publishing MUST create an immutable template version. It MUST be rejected when:

- no page exists;
- no question exists;
- a question has no positive maximum score;
- the same question order/label conflicts;
- required answer regions are outside the page;
- the sum of points does not match an explicitly configured target;
- an AI draft still contains unresolved blocking warnings.

Editing a published version creates a new draft version. Existing grading runs remain linked to the version used.

### FR-TPL-007 — Regrade on template change (P1)

Activating a new template version MUST NOT silently change historical results. A teacher MAY explicitly create a regrade run for selected submissions. Both the original and replacement results remain auditable.

## 5. Test sessions and scan upload

### FR-SES-001 — Test session (P0)

A teacher MUST create a test session associating:

- one published template version;
- a test date;
- optional class/course;
- optional expected student set;
- grading priority (`economy` default or `expedite`);
- open/closed state.

The test date, not upload time, drives progress and reports.

### FR-UPL-001 — Peer upload (P0)

Any authorized host or peer browser MUST be able to upload supported files to an open session. The peer MUST upload through authenticated HTTPS; direct filesystem or database access is prohibited.

### FR-UPL-002 — Resumable and integrity-checked upload (P0)

Uploads larger than 8 MiB MUST use resumable chunks. The server MUST:

- issue an upload identifier;
- accept chunks with offset and length validation;
- enforce a 250 MB file limit;
- compute SHA-256 while streaming;
- validate actual file signature/MIME;
- write to an isolated incoming directory;
- atomically promote only a complete file;
- remove abandoned temporary uploads after 24 hours.

### FR-UPL-003 — Duplicate handling (P0)

The system MUST detect:

- an exact duplicate file by content hash;
- a possible duplicate submission by session, page fingerprint, and student candidate;
- repeated pages within one submission.

Exact duplicates MUST not consume additional scan quota. The user chooses to link the existing upload, cancel, or deliberately create a separate submission where allowed.

### FR-UPL-004 — Scan preprocessing and quality (P0)

The pipeline MUST:

- rasterize PDFs at a controlled resolution;
- apply orientation correction, deskew, margin crop, contrast normalization, and light denoise without altering answer strokes;
- preserve an unmodified original;
- compute blur, clipping, darkness, and resolution signals;
- align pages against the blank template;
- detect missing, extra, or misordered pages;
- create thumbnails and answer crops.

Blocking quality errors go to an operator queue. Non-blocking warnings remain visible during grade review.

### FR-UPL-005 — Upload status (P0)

The uploader MUST see progress and terminal/queued states: `uploading`, `validating`, `preprocessing`, `awaiting_ai`, `needs_attention`, `ready_for_review`, `finalized`, or `failed`. Closing the browser MUST NOT cancel completed chunks or server processing.

## 6. Student name recognition

### FR-NAME-001 — Name region (P0)

Every template version MUST define zero or one primary student-name region per applicable page and MAY define student-number and kana regions. A teacher can edit the region.

### FR-NAME-002 — Roster-constrained recognition (P0)

The system MUST extract a name candidate from the cropped region and compare it against active students/aliases in the test session scope. It MUST return:

- recognized text;
- up to five ranked student candidates;
- calibrated confidence;
- evidence signals such as exact number match, name similarity, and image quality;
- `auto_assign`, `needs_review`, or `no_match`.

### FR-NAME-003 — Conservative automatic assignment (P0)

Automatic assignment MUST occur only when all configured conditions pass, including:

- confidence at or above the calibrated threshold;
- a sufficient gap between first and second candidates;
- no conflicting exact student number;
- acceptable crop quality;
- candidate belongs to expected roster if a roster is set.

The initial threshold MUST be calibrated to achieve at least 99.5% precision on the school validation set. Until calibrated, all assignments require review.

### FR-NAME-004 — Manual assignment and correction (P0)

Authorized staff MUST be able to search and assign a student, mark the paper unidentified, or mark it as a non-student sample. Changing an assignment after finalization MUST require a reason and must recalculate affected progress.

### FR-NAME-005 — Duplicate student submissions (P0)

If the same student has multiple submissions in one session, the system MUST block silent finalization and require a teacher to select the canonical submission, mark an attempt number, or flag a mistaken identity.

## 7. Grading

### FR-GRD-001 — Grading run (P0)

Each submitted paper MUST have one or more immutable grading runs. A run records template version, processing pipeline version, provider/model, prompt/schema version, input artifact hashes, per-question results, cost, and timestamps.

### FR-GRD-002 — Hybrid grading (P0)

The system MUST select the lowest-risk method by question:

1. deterministic parsing/comparison for multiple choice, selected numeric, and exact-token questions;
2. AI transcription followed by local rule evaluation for short answers;
3. AI rubric evaluation for semantic short answers;
4. mandatory teacher review for subjective, unsupported, or low-confidence answers.

The model MUST NOT calculate the test total.

### FR-GRD-003 — Per-question output (P0)

Every question result MUST include:

- question identifier and display label;
- answer transcription;
- normalized answer;
- awarded and maximum points;
- outcome (`correct`, `partial`, `incorrect`, `blank`, `unreadable`, `needs_review`);
- grading method;
- confidence;
- Kanji-rule outcome;
- concise reason code and optional teacher-facing explanation;
- crop/image reference while retained;
- review status.

### FR-GRD-004 — Blank and unreadable distinction (P0)

The system MUST distinguish a confidently blank answer from an unreadable/cropped/ambiguous answer. `unreadable` MUST require review and MUST NOT default to zero as a finalized judgment.

### FR-GRD-005 — Partial credit (P0)

Partial credit is allowed only if the published rubric defines it. Awarded points MUST be one of the configured increments and between zero and maximum.

### FR-GRD-006 — Confidence review rules (P0)

A result enters the review queue when any of the following is true:

- confidence is below its question-type threshold;
- model output fails schema or consistency checks;
- answer crop is low quality or misaligned;
- the model marks unreadable/ambiguous;
- a safety filter blocks processing;
- Kanji presence is uncertain and Kanji is required;
- subjective grading is used;
- points/reason conflict with deterministic policy;
- the template question was flagged as risky.

### FR-GRD-007 — Teacher review and override (P0)

A teacher MUST be able to view the blank-page question region, expected answer/rubric, student answer crop, AI transcription, explanation, and proposed score together. An override requires a reason code and optional note. The previous value remains immutable.

### FR-GRD-008 — Finalization (P0)

A paper can be finalized only when:

- a student is assigned or a permitted unidentified status is selected;
- all blocking question reviews are resolved;
- awarded points are valid;
- the locally computed total matches stored per-question points;
- no duplicate-submission conflict remains.

Finalization records staff/time and updates student progress. Reopening requires teacher permission and a reason.

### FR-GRD-009 — Economy and expedite processing (P0)

- with official Gemini, `economy` uses Gemini Batch API and clearly states that provider processing can take up to 24 hours;
- with OpenRouter, `economy` uses the configured accuracy-approved cost profile through durable bounded queued requests; it MUST NOT claim a provider batch discount;
- `expedite` raises queue priority and uses the configured fast profile/standard inference, with a cost estimate;
- administrators can disable expedite mode;
- changing priority is audited;
- dispatch/batch grouping MUST NOT mix provider credentials or incompatible model/prompt/schema versions.

### FR-GRD-010 — Failure and retry (P0)

Transient failures use bounded exponential backoff with jitter. Permanent failures enter an actionable queue. Retrying MUST preserve original inputs and MUST not create a new grading run until a valid response is accepted.

## 8. Results and progress

### FR-RES-001 — Result detail (P0)

Authorized staff MUST see student, test date, template/version, total, percentage, status, each question and answer result, override history, and scan availability. When a scan has been deleted, the UI states the deletion date/reason without a broken link.

### FR-PRG-001 — Date-filtered student progress (P0)

A teacher MUST select a student and inclusive start/end dates and see:

- chronological score-percentage line graph;
- points earned/possible;
- test title/date;
- correct/partial/incorrect/blank counts;
- optional filters for subject, category, course, and template;
- a table equivalent to the graph.

Only the latest finalized, non-superseded run is included by default.

### FR-PRG-002 — Graph edge cases (P0)

- No data shows an explanatory empty state.
- One test shows one labeled point, not a misleading trend.
- Tests with zero possible points are excluded and flagged as data errors.
- Same-day tests are separately identifiable.
- Date filters are validated and persisted in the URL.

### FR-PRG-003 — Recalculation (P0)

Student reassignment, grade override, regrade activation, result voiding, or template metadata correction MUST invalidate and recompute relevant aggregates transactionally.

## 9. PDF export

### FR-EXP-001 — Per-student test-result PDF (P0)

Staff MUST be able to generate/download a PDF for one finalized student submission containing:

- school and report title;
- student display name;
- test title and date;
- total score, maximum, and percentage;
- each question number and question text from the exact template version;
- student's recognized answer;
- awarded/maximum points and result mark;
- teacher-visible correction/comment when configured;
- generation timestamp and report identifier.

### FR-EXP-002 — Japanese rendering (P0)

The export MUST embed licensed Japanese fonts, line-wrap Japanese text, prevent missing glyphs, repeat table headers across pages, and avoid splitting a short question row when space permits. A generated PDF MUST be readable without fonts installed on the viewing computer.

### FR-EXP-003 — Export privacy and provenance (P0)

The PDF MUST NOT include internal confidence, model prompts, API key/cost, private staff notes, or the original scan unless explicitly selected in a future feature. It MUST show that a corrected grade is the current grade without exposing staff identity unless policy enables it.

### FR-EXP-004 — Reproducibility (P0)

An export record MUST store the result revision, template version, renderer version, SHA-256, creating user, and time. Regeneration after a grade change creates a new export revision. Stored PDFs follow a configurable record-retention policy independent of scan retention.

## 10. Scan retention and storage quota

### FR-RET-001 — Managed bytes (P0)

The 150 GB quota MUST include:

- submitted original test files;
- normalized submitted pages;
- submitted-page thumbnails;
- answer and name crops;
- temporary grading renditions containing student answers.

It MUST exclude:

- blank template source/pages;
- database, logs, software, and backups;
- exported result PDFs.

The UI MUST show each category and total so exclusions cannot hide disk exhaustion.

### FR-RET-002 — Three-calendar-month deletion (P0)

At least daily, the service MUST delete managed scan payload whose submission upload timestamp is earlier than the corresponding local date/time three calendar months before the job. Example: a run on May 31 treats February 28/29 according to calendar arithmetic, not a fixed 90-day approximation.

### FR-RET-003 — Quota cleanup (P0)

When managed usage reaches 150 GB, cleanup MUST delete the oldest scan payload until usage is at or below the 145 GB low-water mark. Cleanup ordering is by upload completion time, then submission identifier. The service SHOULD start warning at 135 GB and proactively clean at 145 GB.

### FR-RET-004 — What deletion preserves (P0)

Scan cleanup MUST preserve:

- student and session association;
- test/template version;
- transcribed/normalized answers;
- question outcomes and points;
- override and finalization history;
- progress aggregates or source records;
- export provenance;
- audit and deletion log.

It MUST remove all retained original/derived submitted images and invalidate image URLs.

### FR-RET-005 — Safe deletion (P0)

Cleanup MUST use a two-phase process:

1. transactionally mark payload `deletion_pending` and write a deletion manifest;
2. remove verified files, update byte counters, set `deleted`, and append an immutable audit event.

Interrupted deletion is reconciled on restart. File paths are taken only from validated database records under configured roots.

### FR-RET-006 — Capacity admission (P0)

Before accepting a new upload, the host MUST verify enough free physical disk for the upload, preprocessing expansion, database operation, and a configurable 5 GB emergency reserve. If safe cleanup cannot make room, the upload is rejected before completion with operator guidance. The application MUST NOT fill the Windows system volume.

## 11. Administration, cost, and audit

### FR-ADM-001 — BYO AI provider configuration (P0)

Only an administrator on the host or an authenticated administrator over HTTPS may configure official Gemini and/or OpenRouter connections and replace their keys. Every key MUST be:

- tested against a non-personal health prompt;
- stored encrypted with Windows-protected key material;
- masked after save;
- never returned by an API;
- usable only by the host AI adapter.

The production connection test MUST verify authentication/credits, the configured model, image input, strict structured output, usage/cost metadata, and provider-specific capabilities. Gemini tests Batch API when selected. OpenRouter tests model and endpoint parameter support with `require_parameters` behavior.

### FR-ADM-002 — Model and prompt configuration (P0)

The direct-Gemini default is `gemini-3.5-flash-lite`; the OpenRouter default remains a separately validated model slug. Administrators can define task profiles for template extraction, name recognition, initial grading, and adjudication. A provider/model/routing change requires capability plus accuracy validation and creates a configuration revision. Requested model, actual returned model/provider where available, endpoint, prompt, schema, and pipeline versions are captured for every AI result.

### FR-ADM-003 — Provider selection and validated failover (P0)

The system MUST support:

- official Gemini only;
- OpenRouter only;
- both configured, with an explicit provider per task profile;
- optional failover only to a separately validated compatible profile.

It MUST NOT silently switch to a cheaper or different model that has not passed the active grading validation suite. OpenRouter routing MUST require the requested parameters, and cross-model fallback is off by default.

### FR-COST-001 — Usage ledger (P0)

The system MUST store provider-reported input, cached, output, and thinking token counts per request where available, plus pricing-snapshot identifier and estimated USD/JPY cost. Exchange rate is administrator-supplied and labeled as an estimate.

### FR-COST-002 — Budget controls (P0)

Administrators MUST configure warning and hard-stop budgets by day and month. At the hard stop:

- new AI work remains queued as `budget_blocked`;
- local upload/review/export continues;
- an administrator can raise the budget or explicitly authorize a bounded override;
- no existing result is deleted.

### FR-AUD-001 — Audit log (P0)

Append-only audit events MUST cover authentication, account/role change, roster mutation/import, template publish, upload/delete, student assignment, grading retry, override, finalization/reopen, export, settings/key/model/budget change, backup/restore, and retention cleanup.

Each event includes UTC timestamp, local display time, actor/service identity, action, object type/ID, outcome, source IP, correlation ID, and a redacted before/after summary. Passwords, session tokens, API keys, full prompts, and raw images MUST NOT appear.

### FR-OPS-001 — System health (P0)

Administrators MUST see host service, database, file store, free disk, managed quota, queues, last cleanup, last backup, Internet/provider connectivity, current model, estimated spending, and certificate-expiry status.

### FR-OPS-002 — Maintenance mode (P0)

Administrators MUST be able to enter maintenance mode, which blocks new uploads/mutations, lets in-flight atomic operations complete, and permits backup/restore/upgrade. Read-only result access MAY remain available.

## 12. Non-functional requirements

### NFR-PERF-001 — LAN responsiveness

At baseline load on recommended hardware:

- authenticated page shell: p95 under 1 second after static asset cache;
- roster/search/list API: p95 under 500 ms;
- result detail excluding full image: p95 under 750 ms;
- 20 MB upload over 1 Gbps LAN: server overhead under 3 seconds beyond transfer time;
- thumbnail first display: under 2 seconds after preprocessing completes;
- progress query for one student/three years: p95 under 1 second.

### NFR-REL-001 — Durability

Acknowledged upload completion means the file has been atomically placed and metadata committed. A process crash may delay processing but MUST NOT produce a silently partial file. SQLite foreign keys, WAL, checksums, and integrity checks are required.

### NFR-REL-002 — Availability and recovery

- school-hours availability target: 99.5%, excluding scheduled maintenance;
- metadata recovery point objective: 24 hours maximum, 4 hours recommended;
- metadata recovery time objective: 4 hours;
- scan recovery is best-effort within backup policy and does not override automatic retention.

### NFR-SEC-001 — Network boundary

The service binds only to configured private interfaces and HTTPS port. The Windows firewall permits inbound traffic only on the Private profile and approved local subnets. Database and file-store ports/shares are not exposed.

### NFR-I18N-001 — Locale

Japanese (`ja-JP`) is the default and fully supported locale. Dates use unambiguous Japanese forms such as `2026年7月27日`; CSV encoding behavior is explicit. Internal identifiers and timestamps are locale-neutral.

### NFR-ACC-001 — Accessibility

Core workflows MUST be keyboard operable, retain visible focus, meet WCAG 2.2 AA color contrast, not rely on color alone, and provide table alternatives for charts.

### NFR-MAINT-001 — Upgrade

Upgrades MUST be signed, versioned, logged, backed up before schema change, and either forward-only with documented restore or automatically rolled back before accepting traffic. The app and schema compatibility matrix is explicit.

## 13. Cross-feature acceptance scenarios

### AS-01 — Normal economy flow

Given a published test and active student roster, when an operator uploads 30 papers from a peer, then the host preserves originals, preprocesses pages, dispatches them through the configured economy provider profile (direct Gemini batch or OpenRouter queued requests), auto-assigns only safe names, queues uncertain items, lets a teacher resolve them, calculates totals locally, and updates progress after finalization.

### AS-02 — Internet outage

Given no Internet, when staff upload scans, then uploads and local preprocessing complete, items show `waiting_for_provider`, no data is lost, and work resumes automatically with bounded rate after connectivity returns.

### AS-03 — Provider timeout

Given a direct Gemini economy batch that has not completed for 24 hours, the system shows delayed status and provider operation ID, does not create a duplicate batch automatically, and offers an administrator a reconcile/cancel/expedite decision. Given OpenRouter throttling/outage, queued requests remain individually retryable and the UI does not describe them as a Gemini batch.

### AS-04 — Scan aged out

Given a finalized scan older than three calendar months, when cleanup runs, then image payload disappears, result detail shows “scan deleted by retention,” progress is unchanged, the previously generated report remains available under its own policy, and deletion is auditable.

### AS-05 — Kanji required

Given expected `漢字`—whether teacher-entered or extracted from a source marked as containing model answers—and the checkbox is unchecked, when the recognized student answer is `かんじ`, then it is not automatically marked correct; the result records `kanji_required_not_met`. If the teacher explicitly added `かんじ` as an accepted exception, the configured exception applies.

### AS-08 — Uploaded paper includes model answers

Given a teacher selects `模範解答入り` for an uploaded solved paper, when
grading-key generation runs, then the visible supplied answer is stored with
`provided_model_answer` provenance and appears as the authoritative answer in
the draft. If the AI independently believes another answer is correct, it
shows a conflict warning and does not overwrite the supplied answer.

### AS-09 — Uploaded paper contains non-model answers

Given a teacher selects `記入済み答案（AIが正答を作成）`, when grading-key
generation runs, then no written answer on that source is stored as
`provided_model_answer`. The AI independently solves the printed questions and
labels its answers `ai_proposed`. If a separate authoritative answer key was
also uploaded, its matched answers take precedence with their original
provenance.

## 14. Original-request traceability

| Requested capability | Normative requirements | Design detail |
|---|---|---|
| Host filesystem storage and peer access | FR-UPL-001–005, FR-RET-001, NFR-SEC-001 | architecture and data-storage documents |
| Automatic Q&A/grading-key generation from blank scan/PDF | FR-TPL-001–007 | AI sections 4–7, UX template editor |
| Uploaded source includes authoritative model answers | FR-TPL-003A–003B, AS-08 | source roles/provenance/conflict workflow |
| Uploaded source includes non-model answers | FR-TPL-003B–003C, AS-09 | independent-solution role and non-authoritative provenance rules |
| Allow/disallow non-Kanji answers | FR-TPL-005, FR-GRD-003/006, AS-05 | Japanese normalization/Kanji engine |
| Automatic grading | FR-GRD-001–010 | hybrid grading and provider-dispatch design |
| Automatic student-name recognition | FR-NAME-001–005 | AI transcription plus local roster matching |
| Per-student progress with date range/graph | FR-PRG-001–003 | progress API and accessible UX |
| Delete older than three months or above 150 GB | FR-RET-001–006, AS-04 | two-phase retention/quota design |
| Per-student test-result PDF | FR-EXP-001–004 | export API, Japanese PDF UX/testing |
| BYO official Gemini API | FR-ADM-001–003, FR-GRD-009 | direct standard/Files/Batch adapter |
| BYO OpenRouter API | FR-ADM-001–003, FR-GRD-009 | strict multimodal queued adapter |
| Accuracy/ease/cost priority | success criteria, FR-GRD-006–009, FR-COST-001–002 | quality gates and task-profile selection |

### AS-06 — Wrong name risk

Given two students with similar names and no exact student number, when first and second candidate scores are close, then the submission remains unassigned and appears in name review; the system does not silently choose the first candidate.

### AS-07 — Teacher correction

Given a finalized question marked wrong, when a teacher reopens and awards credit with reason “accepted equivalent,” then a new result revision becomes current, the old judgment remains in history, the total/progress update, and any old PDF is marked superseded.
