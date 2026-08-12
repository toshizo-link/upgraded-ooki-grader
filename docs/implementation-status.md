# Implementation status

Snapshot: **2026-08-11**.

This page separates code that is executable in this repository from evidence
that still must be collected before a school deployment. It is not a
production-readiness declaration. The teacher-first simplifications recorded
here supersede the older Batch/priority controls in the baseline
[specification](specification/README.md).

## Implemented and executable

### Application foundation and staff security

- Eleven .NET 10 source projects, a React/TypeScript SPA, eight .NET test
  projects, and twenty-one EF Core migrations.
- SQLite with WAL, foreign keys, integrity constraints/triggers, serialized
  writes, content-addressed storage, audit records, and crash reconciliation
  for promoted-but-unreferenced objects.
- Host-local one-use bootstrap, Argon2id passwords, opaque database sessions,
  role policies, idle/absolute expiry, active-session warning/extension, CSRF,
  strict origin checks, security headers, login throttling, maintenance mode,
  and masked source-address auditing.
- Administrator-managed staff creation, roles, disable/re-enable, password
  reset, forced password change, session revocation, and last-administrator
  protection.
- Mandatory idempotency keys for authenticated replay-sensitive mutations,
  canonical request hashing, response replay, conflict detection, and
  cursor-based pagination for the major collections.
- Student, template, test-session, and finalized-result discovery now shares
  bounded Unicode/Japanese search, combined server filters, complete-corpus
  facets, allowlisted ascending/descending sorts with immutable-ID ties,
  filter-bound protected cursors, 25/50/100/200 page sizes, typed invalid-query
  responses, and URL-restorable accessible Web controls.

### School workflow

- Students, aliases, activation, progress, transactional UTF-8/Shift_JIS CSV
  roster import, templates, immutable template versions, rubrics, accepted
  answers, test sessions, rosters, submissions, review, finalization, reopening,
  results, and history.
- Per-question grading policies include positive Japanese controls for `完答`,
  `順不同`, and `漢字必須`. The first two are additive migration `_0019`
  booleans with legacy `false` defaults and participate in content hashes,
  draft cloning, API/editor round trips, and grading prompts. Local
  reconciliation makes complete-answer partial awards zero/incorrect while
  preserving review, and order-insensitive comparison preserves duplicate
  component counts across explicit Japanese/ASCII separators.
- Template removal is an audited, revision-protected soft archive with restore.
  Default lists and new test sessions exclude archived templates, while
  published versions and historical sessions/results remain readable. Archive
  rejects a template while automatic extraction is active. Closed sessions
  expose their archive transition only as a terminal-work boundary: every
  submission must be finalized/voided and uploads, ordered-scan batches, and
  grading jobs must be terminal. Archived sessions are removed from actionable
  review/finalize queues and are read-only. Students and staff retain
  deactivate/reactivate and disable/re-enable lifecycles instead of destructive
  deletion.
- New template creation selects test type and subject before the upload control
  is enabled. `その他` alone adds the `通常` / `穴埋め` answer-style choice.
  The server owns routing: HOP is one independent template per page; STEP is
  one independent template per consecutive two pages and requires a page count
  divisible by six; class-placement and Other stay as one whole-document unit.
  STEP suffixes reset as `-1`, `-2`, `-3` in each six-page set and are not
  editable or model-generated. Each variation gets independent template,
  version, question, publication, session, and result identities.
- Durable template-generation batches and units snapshot source hashes, page
  ranges, selected routing, prompt/schema versions, immutable profile hashes,
  row versions, local rotations, derived-source provenance, warnings, and
  created template IDs. Invalid STEP packs fail local validation before a job
  or provider cost is reserved, while one failed unit prevents partial batch
  confirmation. EF migration `_0017_DeterministicTemplateGenerationBatches`
  adds this persistence without rewriting published historical versions.
- The current deterministic extraction contract is prompt
  `template-extract-v2.0.0`, schema `template_extract_v5`, and pipeline
  `deterministic-template-generation-v1`. The schema begins with an orientation
  action gate. Upright pages continue to extraction in that response; a valid
  rotation-only response is corrected locally and receives exactly one second
  request. A second rotation request stops with
  `ORIENTATION_RETRY_EXHAUSTED`. No separate classification, split, variation,
  orientation, naming, or grade AI request exists.
- Paper name and printed grade are returned with normal extraction. Filename
  grade is parsed locally and reconciled with paper evidence. The Japanese
  final-check screen resolves grade first, then assigns immutable
  `{subject}{grade}年HOP{unitSequence}`,
  `{subject}{grade}年STEPセット{set}-{variation}`, or
  `{subject}{grade}年クラス分けテスト` names. For these known types the
  AI-read paper name is provenance/reference only. `その他` alone uses an
  editable printed-name proposal and resolves missing/duplicate names before
  one transactional confirmation creates all independent draft templates.
- The superseded upload-first source-role/mode selector and its AI routing
  branches are no longer the entry point for new creation. Historical records,
  source-role columns, published templates, and durable legacy jobs remain
  readable.
- Generated-template review shows the original PDF/image beside the complete
  AI draft, so a teacher can compare every question without coordinates or
  crops. A revision-protected, audited `すべての問題を確認` action confirms every
  complete proposal while keeping only low-confidence, incomplete,
  conflicting, unsupported, or explicitly always-review items for individual
  correction. Descriptive type alone does not force individual review.
  Publishing remains one separate explicit teacher action.
- Resumable 8 MiB uploads with signatures, SHA-256, exact-content
  deduplication, explicit duplicate/conflicting-attempt resolution, capacity
  admission, abandoned-upload cleanup, and reconciliation.
- Unified ordered one-page scan intake. Published versions pin the logical
  submission page count: HOP 1; STEP 2 for the selected, separately registered
  variation/session; and class-placement/Other the complete published template
  count, up to the enforced 50-page ceiling. Immutable client ordinals survive
  parallel transfer and are the ownership authority; filenames, timestamps,
  and completion order are not. Local template-page classification blocks
  ambiguous, duplicate, missing, and out-of-order groups. Valid groups create
  one durable multipage submission with immutable source-page ordinal/hash
  provenance and reuse the existing preprocessing, name, grading, retention,
  and audit path. A later page from another student remains undetectable if it
  has no identifier and occupies the correct template role, so consecutive
  per-student scanning is an explicit operational requirement. Migration
  `_0018_OrderedScanAssembly` is additive and trigger-safe.
- Japanese-first teacher/operator UI, scan-operator least privilege, live
  event updates, keyboard-oriented review, draft recovery, session-expiry
  recovery, one-step Gemini setup, budgets, advanced evaluation evidence,
  backups, reports, and operational health. The product chrome is branded simply
  `Ooki Grader`; new-school bootstrap records `大木スクール` without placing a
  redundant school-name subtitle under the product name.

### Local document and image processing

- PDF/image admission and bounded rasterization, normalized pages, thumbnails,
  page-quality metrics, right-angle/translation alignment, exact and
  perceptual fingerprints, repeated-page detection, and
  cross-submission duplicate evidence.
- TIFF decoding is handled by a bounded managed decoder. It checks directory
  count and expanded pixel limits before raster allocation, processes one page
  at a time, caps retained normalized artifacts, and classifies malformed input
  as a permanent source error. Single-page template TIFFs are normalized to
  verified PNG, while multi-page TIFFs are converted from normalized pages to
  one PDF before provider disclosure so no page is dropped or reordered.
- Coordinate-free page understanding. The extraction contract records logical
  questions, labels, text, answers, points, and grading policy without asking
  teachers or Gemini for rectangles. Japanese sheets may interleave questions,
  maps/tables, shared answer grids, and long written answers on one page.
- Template extraction and adjudication use complete normalized pages. For an
  ordered submission, the first combined analysis chunk reads identity from
  logical page 1 while grading its visible answers; later chunks return no
  identity. Initial grading consumes every normalized page through deterministic consecutive chunks, so
  a 3- to 50-page paper is not truncated or flattened into one unbounded
  provider request. Legacy region columns remain readable for existing
  databases but are not created or required by the current workflow.

### AI provider and grading path

- Official Google Gemini direct client fixed to the configured
  model identifier (`gemini-3.5-flash-lite` in the checked-in flow), Google-host
  allow-listing, bounded requests/responses, structured JSON schemas,
  task-specific thinking/media-resolution controls, usage metadata, safe
  failure classification, and capability probes. The current template profile
  uses `medium` thinking and `high` media resolution; grading remains on its
  separately versioned current profile.
- Official OpenRouter Chat Completions client with a fixed HTTPS endpoint,
  bearer-key isolation, bounded multimodal messages, strict JSON Schema,
  `require_parameters`, data-collection denial, required Zero Data Retention,
  authoritative `usage.cost` capture, requested/routed-provider evidence,
  safe provider failure classification, and an image capability probe.
  Reasoning effort is mapped explicitly and reasoning tokens are not charged
  twice. Gemini and OpenRouter connections are stored separately and selected
  by the explicit task profile; silent cross-provider failover is not used.
  A model is not allowed into the visual workflow unless its connection probe
  proves authentication, exact model availability, image input, structured
  output, and usage metadata.
- Gemini add/replace performs that full capability and representative image-task
  probe against the supplied candidate before persistence. Full success alone
  encrypts the key and atomically makes the exact current profiles for template
  extraction, name transcription, initial grading, and adjudication available
  with `approval_state=capability_passed`. Failure or an ambiguous replacement
  preserves the previous connection, key, and profile set. The manual connection
  test self-heals missing/stale current profiles, and startup reconciles active
  Gemini profiles after prompt/schema/hash changes.
- Windows DPAPI credential envelopes with revisioned references. macOS
  development uses an authenticated encrypted file bound to its persistent
  ASP.NET Core Data Protection key ring, so credentials survive a normal Host
  restart. This is a same-machine development convenience, not a non-Windows
  production secret-storage claim; Testing remains process-local.
- Teacher-gated template extraction, combined page-1 name/student-number
  transcription plus initial semantic grading, deterministic grading shortcuts,
  second-pass adjudication, retry schedules, and provider-free/name-only legacy
  fallback. Roster matching and student assignment remain local teacher gates.
- Every supported question type now selects AI-rubric grading by default. The
  teacher-facing editor has one simple judgment preset with exact, numeric,
  choice, and manual overrides; its partial-point increment defaults to 1 point
  and `always review` defaults off. Clear, valid, high-confidence results can be
  ready to finalize without per-question review, while the teacher still
  explicitly finalizes the paper.
- The normal template completion action is `受付を開始`. It atomically fixes the
  validated draft, captures canonical template metadata, creates an open test
  session, and returns the upload destination. Teachers enter only the test
  date and optional target class; session name, duplicate course, and priority
  controls are absent. Durable request identity closes the response-loss replay
  gap, and sessions pinned to superseded or retired immutable versions continue
  to preprocess and grade normally.
- Grading prompt/schema `submission-analyze-v2.1.0` /
  `submission_analysis_v2` returns identity only from chunk 1 and exact evidence
  media indexes for grading results. Each result reads the original pixels and
  returns transcription plus grading in one response; transcription preserves
  visible line boundaries but is not the sole grading input. Visual CR/LF wrapping
  alone cannot turn otherwise identical accepted content into an incorrect result.
  Identity and grading validate independently. Located empty answer
  fields are returned as explicit blanks, while missing IDs are reserved for content
  that cannot be located. Local reconciliation computes deterministic scores,
  accepts safe choice-label decorations, and turns invalid increments or
  ambiguous observations into per-question zero-point review proposals instead
  of rejecting the whole paper.
- Initial-grading pipeline `gemini-submission-analysis-page-chunks-v5` persists one
  request per ordered page chunk. Each chunk has at most 32 media parts and at
  most the smallest of the configured media cap, 12 MiB raw, and a dynamic
  base64 budget beneath the Gemini client's 18 MiB serialized-request limit.
  One grading run is committed only after all chunks succeed; completed chunks
  are reused on retry, usage/cost is aggregated once, and observations for the
  same question from multiple chunks become the explicit manual-review result
  `ai_chunk_observation_conflict`. Oversized rubrics/instructions or a single
  page that cannot fit are rejected before provider media is read or sent.
- An unassigned completed run is staged as non-current `awaiting_identity`; the
  existing teacher assignment/unidentified action activates it and parked
  adjudication without resending answer pages. Non-student samples never expose
  the staged run.
- The answer-specific grading workspace streams the original/assembled PDF,
  lazily falls back to normalized pages/thumbnails, exposes all current results,
  and supports append-only score/outcome/transcription changes. Its bulk action
  confirms the exact versioned unresolved set (up to 300) without finalizing;
  stale snapshots fail atomically.
- Template extraction schema `template_extract_v5` has an explicit
  `rotate`/`extract` discriminator, exact page manifests, per-page quarter-turn
  instructions, printed name/grade metadata, and the established
  coordinate-free question/answer contract. Cross-field invariants are enforced locally:
  a rotation response cannot contain extraction content, and an extraction
  response must cover every supplied page with zero requested rotation.
- Legacy Gemini Batch persistence and workers remain readable for existing
  data, but the current UI, connection test, new-session flow, and new initial
  grading requests use the normal queued Gemini API only. Batch, economy,
  priority, flush, reconcile, cancel, and expedite controls are not presented
  to teachers or administrators.
- Task profiles, standard-connection capability evidence, atomic activation,
  daily/monthly hard budget reservations, pricing snapshots, usage/cost ledgers,
  latency/error metrics, and immutable golden-set evaluation records. Those
  evaluation records remain release/advanced model evidence and are not a
  routine Gemini setup control; OpenRouter is saved separately, then uses the
  explicit manual `再確認`, evaluation, and profile-activation workflow.
- Age/quota retention includes ordered source PDFs, their assembled submission
  PDF, normalized pages, thumbnails, and grading image artifacts. It releases
  live file references and marks the submission `scan_deleted`, while retaining
  ordered page ordinals/hashes, grading runs/results/revisions, totals, audit
  history, and reports.

### Reports, backup, restore, and Windows operations

- Deterministic Japanese per-student result PDFs with embedded fonts, stable
  hashing, background generation, status/regeneration/download APIs, and
  renderer tests.
- Durable bulk student-result export previews either exact checked submission
  IDs or the server-resolved current report filters. A fingerprinted job
  revalidates every current finalized result and packages at most 100 students
  / 500 canonical PDFs into a deterministic verified ZIP with safe student
  folders and a formula-neutralized UTF-8 manifest. Migration
  `_0020_BulkTranscriptExports` adds only the new request/artifact table;
  progress, stale/superseded handling, idempotency, provenance, private range
  downloads, and count/hash-only audit metadata are implemented.
- Scheduled online SQLite backups, optional managed scans/reports, provider
  credential envelopes, versioned manifests, per-file hashes, verification,
  retention, health, and administrator records. Backup is deliberately
  disabled until an encrypted destination is configured and confirmed.
- Read-only health/backup/restore-plan CLI plus offline restore execution with
  typed maintenance confirmation, path/reparse/overlap guards, manifest and
  per-file re-verification, capacity/schema/integrity checks, staging, operation
  markers, rollback snapshot preservation, atomic directory switch, and
  automatic rollback on a failed switch.
- PowerShell technician tooling for preflight, certificate creation and peer
  trust, install, health, upgrade, repair, restore, uninstall, Windows Service,
  NTFS ACL, private-profile firewall, Event Log, release-package assembly, and
  a Windows-only Inno Setup 6 build target. The supervised on-site entry point,
  `Install-OokiGraderOnSite.ps1`, verifies the immutable package before any
  mutation, creates a free non-exportable private local CA and DNS/IP HTTPS
  certificate, installs and health-checks the host, then exports an immutable
  public-only classroom package that verifies checksums, CA identity, managed
  hosts resolution, genuine HTTPS, and the shared shortcut. It uses the
  canonical `ooki-grader.test` name and a fixed/DHCP-reserved private IP.
  Checksum-verified unsigned on-site custody is a named mode distinct from the
  development override. The optional setup source creates
  `OokiGrader-Setup-<version>-x64.exe`, keeps school data on uninstall, rejects
  cross-version overwrite, and leaves guarded upgrade/restore tools installed.
  Release and setup builders verify complete checksum coverage, approved
  Authenticode signer identity, immutable version contents, and disjoint
  application/data roots; a production signing claim is recorded only after
  the supplied signing hook produces valid signatures on the build host.
- A dedicated Japanese on-site installation guide, a 28-page teacher guide,
  and a 19-page host/app operations guide are checked in. The screenshot
  library contains 48 task screens captured from real app pages with fictional
  data plus five inspected contact sheets. The guides cover the free private-CA
  install/client setup, initial administration, settings-first template
  creation, arbitrary-page ordered intake, grading/finalization, robust list
  discovery, previewed bulk result export, AI setup, routine checks, backup,
  recovery, updates, and incident triage.
- Generated OpenAPI 3.1 contract and immutable TypeScript declaration with a
  drift check.

## Checked-in safety defaults

- Enabled: Gemini direct, optional OpenRouter connection configuration,
  template extraction, semantic grading, adjudication, PDF reports, and
  proactive storage retention. Gemini remains the selected default.
- Disabled: Gemini Batch for new work, cross-provider failover, automatic
  roster assignment, and automatic finalization.
- Backup scheduling is disabled and its destination is blank.
- A full Gemini candidate-key probe enables only the four current AI draft and
  review-support profiles. Automatic roster assignment and automatic
  finalization remain disabled, and teachers still review/publish/finalize.
- Prompt/schema activation is version-exact. Startup and the manual connection
  test reconcile active Gemini profiles to the exact checked-in prompt, schema,
  and hash; in-flight jobs retain their immutable profile snapshot. Formal
  golden-set evaluation remains a release/model-change gate rather than a
  normal school-administrator activation step.

## Validation completed in this workspace

The live template-extraction evidence below was collected against the former
v1.8.3/v4 flow. It remains useful historical accuracy evidence, but it is not
release evidence for the current v2/v5 orientation-gated contract. The automated
v2/v5 contract, HOP/STEP, orientation-retry, persistence, and final-check
scenarios pass; release validation must still rerun the provider-backed
scenarios against the exact current profile and the school's approved golden
set. This engineering gate is separate from the one-step Gemini setup used by
an administrator at school.

- Focused Release builds and ordered-scan domain, persistence, assembly,
  preprocessing, grading-chunk, retention, and upload checks are the current
  acceptance scope. The final repository-wide deterministic suite result and
  tally for this implementation pass are intentionally pending rather than
  recorded here prematurely.
- Real-fixture preprocessing smoke: three pinned scored handwritten exam PDFs
  and two pinned Japanese handwriting images passed source-hash, expected-page,
  normalization, thumbnail, manifest, and fingerprint checks.
- Live Gemini direct smoke: the exact selected model passed the structured
  image capability probe and transcribed a public Apache-2.0 Japanese
  handwriting fixture. This establishes connectivity and contract
  compatibility, not grading accuracy.
- Live coordinate-free Gemini smoke: the exact selected model accepted the
  simplified full-page grading schema and returned a structured result for a
  Japanese completed test page. The same model extracted five logical
  questions from an interwoven Japanese social-studies page with a schema that
  contains no question or answer coordinates. This verifies the HTTP contract
  that previously failed with `gemini_request_invalid`; it is not a school
  scoring-accuracy claim.
- Live Gemini template-extraction demonstration: `gemini-3.5-flash-lite`
  processed the supplied one-page Japanese junior-high social-studies sheet,
  where questions and answer areas are interwoven, and created 16 question
  drafts through the host worker. Because the sheet did not contain an
  authoritative answer key, all 16 remained visibly gated for individual
  teacher review rather than being auto-published. This demonstrates the
  end-to-end workflow; it is not a school golden-set accuracy result.
- Final teacher-flow verification: a Japanese blank social-studies PDF was
  uploaded through the real SPA and classified as `問題のみ（未記入）`.
  Gemini created five questions totaling 50 points; the editor displayed the
  original PDF beside the draft, allowed four safe questions to be confirmed
  together, kept one descriptive question for individual review, and published
  the verified version. A repeated equivalent-answer variant from a separate
  live run is now normalized instead of failing the entire draft, and retrying
  supersedes the recovered failure so it does not remain as a false health
  warning.
- Template prompt v1.7 follow-up: the exact configured model again processed
  the Japanese blank interwoven sheet, returned at least five logical questions
  without coordinates, and produced at least one non-authoritative
  `ai_proposed` expected answer with no supplied-answer provenance. The
  template-only instruction permits internal subject knowledge and no longer
  conflicts with answer-key generation. The prompt no longer prohibits approved
  search grounding, but the current client does not automatically enable it;
  retention, attribution, query billing, and an administrator control require
  explicit school approval before that integration is added.
- Final Japanese fill-in template evaluation: the supplied one-page science
  worksheet, containing 11 handwritten answers embedded in printed sentences,
  ran through the real host API in both `記入済み答案（AIが正答を作成）` and
  `模範解答入り` modes three times each. Prompt v1.8.3 / schema v4 / pipeline
  v9 passed 6/6 runs and 66/66 answer slots for detection, printed order,
  answer correctness, source provenance, one-placeholder normalization, safe
  bulk confirmation, and unpublished-draft status. No administrative field or
  handwritten-answer leakage entered question text. This is strong regression
  evidence for one difficult sheet, not a statistical claim across subjects or
  layouts; see
  `output/accuracy/fill-in-template-generation-report-2026-08-05.md`.
- Live OpenRouter eligibility evaluation: both
  `deepseek/deepseek-v4-flash` and the fixed current snapshot
  `deepseek/deepseek-v4-flash-0731` passed a strict structured-text response,
  but official metadata identified text-only input and the same Japanese
  science worksheet was rejected before inference with no image-capable
  endpoint. Gemini's existing 6/6-run, 66/66-slot visual result is therefore
  the only eligible result for this workflow, and Gemini remains selected.
  The API key was process-only and is absent from evidence; see
  `output/accuracy/openrouter-deepseek-v4-vs-gemini-report-2026-08-05.md`.
- Basic live grading evaluation: the reusable evaluator ran
  `gemini-3.5-flash-lite` twice over three pinned, anonymized scored handwritten
  papers plus one synthetic Japanese control. Objective score agreement was
  53/56 in both runs, semantic short-answer agreement was 6/14 and 4/14, and
  strict local response validation accepted only one of three real-paper
  responses in each run. The report is at
  `output/accuracy/basic-grading-accuracy-report-2026-08-05.md`. This is
  exploratory evidence and does not approve automatic finalization.
- Live model comparison: the same versioned evaluator and immutable inputs ran
  against `gemini-3.5-flash-lite`, `gemini-3.5-flash`, and
  `gemini-3.6-flash`. The 3.1 Pro preview endpoint was visible to the account
  but generation was quota-blocked, while Gemini 3.6 Flash at its default
  `MEDIUM` thinking level returned an invalid point increment and was rejected
  locally. No tested setting met the automatic-finalization safety gates; see
  `output/accuracy/gemini-model-comparison-report-2026-08-05.md`.
- Post-fix live grading verification: prompt v1.2 and local item reconciliation
  were rerun against `gemini-3.5-flash-lite` / `MINIMAL` and
  `gemini-3.6-flash` / `MEDIUM`. Flash Lite returned 105/105 real items,
  correctly represented 31/31 blank short answers, and all three papers passed
  local validation. Gemini 3.6 completed without the prior response-wide
  invalid-increment abort, but still omitted all 31 blanks and returned only
  74/105 items. The configured model therefore remains Flash Lite; see
  `output/accuracy/grading-fix-verification-report-2026-08-05.md`.
- The current v5 template-extraction and v1 grading contracts contain no
  writable regions or coordinates. Contract and integration coverage verifies
  page-1-only ordered name input, consecutive initial-grading page chunks,
  full-page adjudication, and the absence of generated submission crops.
- Frontend TypeScript and focused ordered-upload component/API checks passed;
  the final full frontend tally and production build rerun remain part of the
  implementation-pass handoff.
- OpenAPI document generation and generated-client drift check passed.
- Fresh-database migration, SQLite foreign-key, and local host-readiness checks
  passed for the prior baseline. Focused model/persistence checks cover additive
  migration 0018; the final all-migration release pass remains pending.
  Migration 0017 also passed a
  0016-to-0017-to-0016-to-0017 round trip with the legacy trigger catalog live,
  preserving data and avoiding a `template_version` table rebuild.
- All thirteen PowerShell installer/module sources parse through the PowerShell
  AST; Inno Setup source invariants and installer/restore safety assertions
  pass in the cross-platform test suite.
- Self-contained `win-x64` Host and offline Tool cross-publishes completed, and
  the Host bundle contained the production SPA. Execution still requires the
  real-Windows gates below.
- Current NuGet, frontend npm, and OpenAPI-generator npm vulnerability queries
  report no known advisories after pinning patched generator transitives.

See [handwritten fixture testing](testing/handwritten-exam-fixtures.md) for
sources, licences, hashes, and reproducible commands.

## Remaining release and external gates

The repository should not yet be used for unattended live grading or as the
only copy of school records. The remaining work depends on external evidence or
an intentionally deferred product choice:

1. Build a privacy-reviewed, versioned school golden set containing real
   Japanese cram-school sheets with interwoven answer areas, anonymized
   handwriting, authoritative keys/rubrics, and teacher adjudication. Meet the
   specification's assignment and scoring thresholds before enabling either
   automatic assignment or automatic finalization.
2. Validate standard Gemini requests with the production Google account,
   retention/privacy controls, quotas, current model availability, current
   pricing, concurrency, retries, and provider-side cleanup. Provider facts
   must be rechecked for every release.
3. Validate any proposed OpenRouter image model with the same immutable school
   golden set before activating it. The client and capability gate exist, but
   DeepSeek V4 Flash is text-only, and validated cross-provider failover is
   intentionally absent.
4. Run clean Windows 11 Pro x64 install/start/reboot/upgrade/rollback/repair/
   restore/uninstall drills using the real service identity, NTFS ACLs,
   firewall, Event Log, certificate store, DNS/LAN, DPAPI envelopes, and
   browser clients.
5. Configure an encrypted backup destination and prove isolated restore,
   rollback, scan recovery, credential re-entry/rewrap, retention, recovery
   time, and recovery point objectives. SQLite table-rebuild migrations include
   non-transactional foreign-key PRAGMAs, so production upgrades require a
   fresh verified backup and maintenance window.
6. For the selected supervised on-site path, run a clean Windows drill of the
   checksum-verified unsigned custody confirmation, local-CA issuance, host
   bootstrap, and generated classroom package on every intended client type.
   If a Setup EXE will instead be distributed outside direct technician
   custody, additionally select an Authenticode publisher/timestamp service,
   compile and sign the checked-in Inno Setup target on a controlled Windows
   build host, publish its SHA-256 independently, and validate the setup
   signature on target machines. A signed setup EXE cannot be claimed from this
   macOS workspace.
7. Complete LAN/browser acceptance, security/privacy review, load and soak
   tests, power-loss/low-disk/provider-outage drills, disaster-recovery
   observation, staff training, and a supervised school pilot.
8. Add an independent local OCR engine only if the product requires OCR without
   Gemini. The current no-provider path deliberately routes unreadable work to
   teachers rather than inventing text.

## Deterministic-creation rollout and rollback

Rollout requires a fresh verified backup and maintenance window, migration
rehearsal from the previous SQLite schema, and synthetic HOP, STEP,
class-placement, Other-normal, Other-fill-blank, rotation, and final-check
acceptance runs. After startup, verify that Gemini reconciliation selected the
exact current `template-extract-v2.0.0` / `template_extract_v5` profile; a manual
connection test performs the same self-healing reconciliation after a full
capability pass. Existing published template versions and in-flight immutable
profile snapshots are retained, but no new work enters the superseded creation
path. Golden-set regression remains part of release acceptance, not a manual
profile-approval task for the school administrator.

For an operational stop, disable `Features:Ai.TemplateGeneration`; this blocks
new AI-assisted generation without deleting source files, batches, units, or
audit history. Restore service only after the active profile and worker health
are corrected. For a binary/schema rollback, stop the service and use the
documented offline restore tool with the verified pre-upgrade backup. Never run
an older binary against the migrated live database, hand-delete the additive
tables, mutate published templates, or partially confirm a failed HOP/STEP pack.
