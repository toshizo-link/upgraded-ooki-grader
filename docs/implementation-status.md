# Implementation status

Snapshot: **2026-08-06**.

This page separates code that is executable in this repository from evidence
that still must be collected before a school deployment. It is not a
production-readiness declaration. The teacher-first simplifications recorded
here supersede the older Batch/priority controls in the baseline
[specification](specification/README.md).

## Implemented and executable

### Application foundation and staff security

- Eleven .NET 10 source projects, a React/TypeScript SPA, eight .NET test
  projects, and sixteen EF Core migrations.
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

### School workflow

- Students, aliases, activation, progress, transactional UTF-8/Shift_JIS CSV
  roster import, templates, immutable template versions, rubrics, accepted
  answers, test sessions, rosters, submissions, review, finalization, reopening,
  results, and history.
- Upload-first template creation now uploads all question/answer sources in one
  action, infers conservative source roles and filename metadata, gives a short
  visible override window, detects exact published `(file, source role)` set
  matches for reuse, and starts the configured extraction profile
  automatically.
  The per-file choice distinguishes `問題のみ（未記入）`, `模範解答入り`,
  `記入済み答案（AIが正答を作成）`, and `別紙の模範解答`. The non-model
  answered role never treats visible writing as the answer key; AI solves the
  printed questions independently.
  The current authority contract uses prompt bundle
  `template-extract-v1.8.3`, schema `template_extract_v4`, and pipeline
  `gemini-template-extraction-auto-detail-qc-v9`; existing older profiles
  deliberately remain unavailable until an administrator evaluates and
  activates the current profile.
  Filename guesses remain useful fallbacks when AI confidence is low; a
  high-confidence document-header proposal can replace only fields explicitly
  marked as filename-derived, never teacher-entered metadata.
- Generated-template review shows the original PDF/image beside the complete
  AI draft, so a teacher can compare every question without coordinates or
  crops. A revision-protected, audited bulk action confirms safe
  high-confidence objective proposals while keeping only low-confidence,
  incomplete, subjective, or always-review items for individual correction.
  Publishing remains one separate explicit teacher action.
- Resumable 8 MiB uploads with signatures, SHA-256, exact-content
  deduplication, explicit duplicate/conflicting-attempt resolution, capacity
  admission, abandoned-upload cleanup, and reconciliation.
- Japanese-first teacher/operator UI, scan-operator least privilege, live
  event updates, keyboard-oriented review, draft recovery, session-expiry
  recovery, simplified Gemini administration, budgets, evaluation evidence, backups,
  reports, and operational health. The product chrome is branded simply
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
- Template extraction, name reading, initial grading, and adjudication use the
  complete normalized pages. Legacy region columns remain readable for existing
  databases but are not created or required by the current workflow.

### AI provider and grading path

- Official Google Gemini direct client fixed to the administrator-approved
  model identifier (`gemini-3.5-flash-lite` in the checked-in flow), Google-host
  allow-listing, bounded requests/responses, structured JSON schemas,
  task-specific thinking/media-resolution controls, usage metadata, safe
  failure classification, and capability probes. The current template profile
  uses `medium` thinking and `high` media resolution; grading remains on its
  separately evaluated profile.
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
- Windows DPAPI credential envelopes with revisioned references. Non-Windows
  development uses process-local credential storage and is not a production
  secret-storage claim.
- Teacher-gated template extraction, Japanese name/student-number
  transcription, initial semantic grading, deterministic grading shortcuts,
  second-pass adjudication, retry schedules, and provider-free review fallback.
- Grading prompt `answer-transcribe-grade-v1.2.0` requires located empty answer
  fields to be returned as explicit blanks and reserves missing IDs for content
  that cannot be located. Local reconciliation computes deterministic scores,
  accepts safe choice-label decorations, and turns invalid increments or
  ambiguous observations into per-question zero-point review proposals instead
  of rejecting the whole paper.
- Template extraction schema `template_extract_v4` includes conservative
  title/subject/category/grade/course proposals, exact source-aware answer
  validation, structured review issues, repeated-label and multiple-placeholder
  accounting, and protection against overwriting teacher-entered metadata.
  A single-page source is automatically examined as one full page plus four
  non-overlapping vertical detail views for independent reconciliation; this is
  internal processing and never asks a teacher to draw boxes or coordinates.
  If the first provider result is invalid, at most two bounded recovery calls
  are accepted only when independently valid results agree; disagreement stays
  blocked for teacher review.
- Legacy Gemini Batch persistence and workers remain readable for existing
  data, but the current UI, connection test, new-session flow, and new initial
  grading requests use the normal queued Gemini API only. Batch, economy,
  priority, flush, reconcile, cancel, and expedite controls are not presented
  to teachers or administrators.
- Task profiles, standard-connection capability evidence, activation gates,
  daily/monthly hard budget reservations, pricing snapshots, usage/cost ledgers,
  latency/error metrics, and immutable golden-set evaluation records.

### Reports, backup, restore, and Windows operations

- Deterministic Japanese per-student result PDFs with embedded fonts, stable
  hashing, background generation, status/regeneration/download APIs, and
  renderer tests.
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
  a Windows-only Inno Setup 6 build target. The setup source creates
  `OokiGrader-Setup-<version>-x64.exe`, keeps school data on uninstall, rejects
  cross-version overwrite, and leaves guarded upgrade/restore tools installed.
  Release and setup builders verify complete checksum coverage, approved
  Authenticode signer identity, immutable version contents, and disjoint
  application/data roots; a production signing claim is recorded only after
  the supplied signing hook produces valid signatures on the build host.
- A Japanese teacher guide and a separate nine-page host/app setup and
  operations guide use current application screenshots. The operations guide
  covers the signed two-stage release build, TLS/DNS, initial administration,
  Gemini setup, routine checks, backup, recovery, updates, and incident triage.
- Generated OpenAPI 3.1 contract and immutable TypeScript declaration with a
  drift check.

## Checked-in safety defaults

- Enabled: Gemini direct, optional OpenRouter connection configuration,
  template extraction, semantic grading, adjudication, PDF reports, and
  proactive storage retention. Gemini remains the selected default.
- Disabled: Gemini Batch for new work, cross-provider failover, automatic
  roster assignment, and automatic finalization.
- Backup scheduling is disabled and its destination is blank.
- AI decisions remain reviewable. Automatic assignment/finalization cannot be
  enabled merely by a successful connection probe; school-approved evaluation
  evidence is required.

## Validation completed in this workspace

- Deterministic solution build: zero warnings and zero errors.
- Deterministic .NET suite: 532 passing tests. Seven explicit external tests are
  skipped unless their fixture/provider environment variables are supplied.
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
- The current schema-v4 workflow intentionally returns no writable regions or
  coordinates. Contract and integration tests verify that full pages reach the
  name, grading, and adjudication workers and that no submission crops are
  generated.
- Frontend: TypeScript check, 45 component/API tests, and production Vite
  build passed.
- OpenAPI document generation and generated-client drift check passed.
- All sixteen migrations apply to a fresh database, SQLite foreign-key checks
  return no violations, and a local host boot returned healthy liveness,
  database, schema, and storage readiness.
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
6. Select the production Authenticode publisher/timestamp service, compile and
   sign the checked-in Inno Setup target on the controlled Windows build host,
   publish its SHA-256 independently, and validate the setup signature on
   target machines. The source and reproducible build wrapper now exist, but a
   signed setup EXE cannot be claimed from this macOS workspace.
7. Complete LAN/browser acceptance, security/privacy review, load and soak
   tests, power-loss/low-disk/provider-outage drills, disaster-recovery
   observation, staff training, and a supervised school pilot.
8. Add an independent local OCR engine only if the product requires OCR without
   Gemini. The current no-provider path deliberately routes unreadable work to
   teachers rather than inventing text.
