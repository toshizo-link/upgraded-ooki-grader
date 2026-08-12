# Decisions, risks, assumptions, and glossary

## 1. Architecture decision summary

### ADR-001 — Host-centric LAN web application

**Decision:** One Windows 11 host owns service, database, and files; peers use HTTPS browsers.

**Why:** central consistency, simple peer setup, host filesystem requirement, easy support.

**Consequences:** host is a single point of availability; backup/UPS/repair matter.

### ADR-002 — Modular monolith

**Decision:** One Windows service/process for web and durable hosted workers, with native preprocessing isolated in child processes.

**Why:** one-school scale does not justify distributed services; simple installation.

**Consequences:** module boundaries need code tests; heavy work must not block web threads.

### ADR-003 — SQLite WAL for v1

**Decision:** Embedded SQLite, one host writer, WAL, write coordinator, online backup.

**Why:** target concurrency fits, no database service administration.

**Consequences:** scale profile is explicit; migrate to PostgreSQL only when measured thresholds require it.

### ADR-004 — Filesystem content-addressed object store

**Decision:** Images/PDFs are NTFS files keyed by SHA-256; metadata stays in database.

**Why:** matches requirement, supports streaming/dedup/retention.

**Consequences:** database/filesystem intents and reconciliation are required.

### ADR-005 — Both official Gemini and OpenRouter

**Decision:** Two first-class adapters behind one canonical task interface.

**Why:** direct Gemini gives discounted Batch API and native capabilities; OpenRouter gives model/provider choice and unified cost/routing.

**Consequences:** provider behavior, errors, input transport, cost, and batching differ and must not leak into grading logic.

### ADR-006 — Per-task accuracy-validated profiles

**Decision:** Template extraction, name recognition, initial grading, and
adjudication use versioned task profiles. The checked-in Gemini defaults are
selected by formal release evaluation, then a school candidate-key setup may
activate the four exact-current revisions atomically after the full capability
and synthetic image-task probe (`approval_state=capability_passed`). OpenRouter,
custom routing, and backward-compatible manual paths activate only separately
evaluated revisions.

**Why:** the cheapest model may be suitable for names but not complex answer-key extraction; accuracy is the top priority.

**Consequences:** release evaluation tooling, capability evidence, atomic
candidate replacement, and immutable profile provenance are core product
features. Routine Gemini setup does not expose
pilot/evaluation/manual-activation controls and cannot authorize automatic publication, assignment, or
finalization.

### ADR-007 — Batch semantics remain provider-specific

**Decision:** Direct Gemini economy uses true asynchronous Batch API. OpenRouter uses application-managed queued individual requests and is never represented as discounted provider batch.

**Why:** current OpenRouter official documentation has no general async discounted chat batch API.

**Consequences:** costs/latencies differ; UI and runbooks distinguish them.

### ADR-008 — “Automatic Grading-Key Generation” terminology

**Decision:** User-facing feature is `採点基準の自動作成`, not Q&A pair or answer-sheet generation.

**Why:** it includes question text, answers, variants, points, regions, Kanji policy, and rubric. “Answer sheet” in school contexts can mean the blank form students fill out.

**Consequences:** domain may still use `Question`, `AcceptedAnswer`, and `Rubric`, but UI/help uses grading key.

### ADR-009 — Supplied model answers are authoritative

**Decision:** Selecting `模範解答入り` marks visible answers as
`provided_model_answer`. AI transcribes/maps them and may not replace them with
its own solution.

**Why:** the teacher-provided answer is the intended key.

**Consequences:** provenance, source mapping, conflict UI, and publish blocking are required.

### ADR-009A — Non-model answers are ignored as answer authority

**Decision:** Selecting `記入済み答案（AIが正答を作成）` assigns
`contains_non_model_answers`. AI extracts the printed questions but must not use
the written responses as expected answers; it solves independently and records
the result as `ai_proposed`. A paired authoritative key still takes precedence.

**Why:** a student's or practice response is useful as a copy of the test but
may be wrong. Treating it as a model answer would silently poison every grade.

**Consequences:** source-role selection is explicit, prompts isolate printed
questions from filled responses, validators reject authoritative provenance
from this role, and a role change requires regeneration.

### ADR-010 — Hybrid deterministic/AI grading

**Decision:** AI-rubric judgment is the template-editor default for every
supported question type. Teachers can opt individual questions into
exact/variant, numeric, choice, or manual grading. Local code always validates
point bounds and explicit constraints and computes totals; low-confidence or
otherwise unsafe proposals still require review.

**Why:** one predictable default reduces setup work, while explicit local
presets preserve explainable hard rules where the teacher wants them.

**Consequences:** question type/configuration quality matters.

### ADR-011 — Conservative automation

**Decision:** Uncertain names/answers enter review. Auto-assignment/finalization starts off and is enabled only after calibration.

**Why:** a wrong confident grade costs more teacher/student trust than an extra review.

**Consequences:** queue UX must be excellent.

### ADR-012 — Scan retention preserves result records

**Decision:** Three calendar months and 150 GiB apply to submitted scan payload; structured grades/transcriptions/provenance remain.

**Why:** satisfies storage requirement while keeping progress/report value.

**Consequences:** result UI must work without images and disclose deletion.

### ADR-013 — 150 GiB hard / 145 GiB target

**Decision:** Internally use binary GiB; warn 135, proactively clean 145, hard cap 150, physical reserve 5.

**Why:** hysteresis prevents constant cleanup and disk exhaustion.

**Consequences:** UI explains quota vs physical disk.

### ADR-014 — Browser UI, no native peer client

**Decision:** Japanese React/TypeScript SPA served by host.

**Why:** easiest peer deployment and update; supports rich image editor.

**Consequences:** browser support and secure local certificate setup required.

### ADR-015 — Practical privacy baseline

**Decision:** School has accepted provider processing; v1 avoids complex consent/privacy workflows. Keep staff auth, host-only keys, protected storage, retention, and required provider-account constraints.

**Why:** accuracy, ease, and cost are the requested priorities.

**Consequences:** no student portal; no consent dashboard/legal-hold/ZDR management in v1.

## 2. Risk register

Scale: probability and impact `Low`, `Medium`, `High`. Owner roles are assigned during kickoff.

| ID | Risk | P | I | Mitigation / trigger |
|---|---|---|---|---|
| R-01 | Handwriting transcription gives plausible wrong text | High | High | gold set, confidence/review, crop/full-page benchmark, adjudication profile |
| R-02 | AI awards incorrect credit | Medium | High | deterministic rules, high precision gate, teacher review, drift rollback |
| R-03 | Similar names auto-assign wrong student | Medium | High | roster constraint, margin, 99.5% precision lower bound, auto off initially |
| R-04 | Provided model answer mapped to wrong question | Medium | High | provenance/source region, 99.5% gate, conflict blocking, teacher publish |
| R-05 | AI silently “corrects” supplied answer | Low after controls | High | schema invariant, separate solved comparison, challenge tests |
| R-06 | Kanji-required kana answer receives credit | Medium | High | local Unicode/script rule, ambiguity review, zero-case release gate |
| R-07 | Template extraction misses a question | Medium | High | teacher publish checklist, question count/layout warnings |
| R-08 | Poor scan quality removes/obscures strokes | High | High | scanner commissioning, quality metrics, non-destructive preprocess, reupload |
| R-09 | Direct Gemini batch delayed up to 24h | Medium | Medium | visible status, expedite, schedule early, optional validated OpenRouter |
| R-10 | Gemini batch duplicate billed after ambiguous create | Low | Medium | prepared manifest/reconcile/no blind retry |
| R-11 | OpenRouter has no discounted batch | Certain | Medium | direct Gemini recommendation for bulk; compare cheaper validated models |
| R-12 | OpenRouter route/provider behavior changes output | Medium | High | require parameters, record actual route, pin/validate routing, drift metrics |
| R-13 | Provider model deprecated or alias changes | Medium | High | capability probes, exact profiles, release monitoring, approved fallback |
| R-14 | API credits/key/quota stop work | Medium | Medium | budgets/health/queue, two connection option, runbooks |
| R-15 | Cost higher than expected due visual/output/retries | Medium | Medium | usage actuals, contact-sheet benchmark, bounded output, hard budgets |
| R-16 | Teacher review queue too slow | Medium | High | question-first UX, shortcuts, accuracy/ease metric, fix top override causes |
| R-17 | Host disk fills during rasterization | Medium | High | reserve/admission, 145 target, conservative expansion, separate data disk |
| R-18 | Retention deletes unreviewed old paper | Low | High | prefer finalized; critical alert before unfinalized; timely queue ops |
| R-19 | Manual deletion corrupts object store | Medium | High | ACL/no share, administrator training, reconcile/backup |
| R-20 | Host hardware fails | Medium | High | UPS, SMART/health, daily verified backup, restore drill |
| R-21 | SQLite contention at actual volume | Low–Medium | Medium | performance telemetry, one writer, short tx, PostgreSQL threshold |
| R-22 | PDF/image library vulnerability/crash | Medium | High | isolated process, patch/SBOM, malformed corpus |
| R-23 | Japanese PDF has missing glyph/layout | Medium | Medium | bundled font, visual regression, long-text fixtures |
| R-24 | Teacher edits wrong published version/session | Medium | High | immutable versions, prominent version/date, publish/finalize checklist |
| R-25 | Duplicate submission skews progress | Medium | Medium | hash/page/student conflict, canonical attempt resolution |
| R-26 | Incorrect override/reassignment history lost | Low | High | append-only revisions/audit, optimistic concurrency |
| R-27 | Backup exists but cannot restore | Medium | High | verify hashes, quarterly drill, key re-entry instructions |
| R-28 | LAN certificate/DNS setup breaks peers | Medium | Medium | technician script, fixed DNS, certificate warning/runbook |
| R-29 | Privacy/security scope grows and delays accuracy | Medium | Medium | ADR-015; practical baseline; defer nonessential workflows |
| R-30 | School samples insufficient for claimed precision | High early | High | auto features stay disabled; grow dataset; report uncertainty |

## 3. Product assumptions

Accepted unless discovery disproves:

- one school site and one host;
- staff, not students, use the app;
- Windows 11 peers can use modern Edge/Chrome;
- school permits a technician to configure DNS/certificate/firewall;
- host can reach provider over Internet;
- school supplies official Gemini/OpenRouter key(s)/billing/credits;
- scanner output can be PDF/JPEG/PNG/TIFF;
- teacher publishes every grading key;
- test date is entered at session creation;
- scan payload may be deleted before grade history;
- reports need Japanese, not digitally signed, v1;
- teachers accept practical provider processing;
- manual review is acceptable for uncertain cases.

## 4. Open decisions for M0

These do not block this specification but affect implementation defaults:

| ID | Decision | Evidence needed | Default if unanswered |
|---|---|---|---|
| O-01 | Actual peak students/papers/pages | one-year school estimate | baseline sizing table |
| O-02 | Primary scanner settings | sample scans | 300 dpi grayscale/color tested |
| O-03 | Initial checked-in release-default task profiles | side-by-side holdout | highest accuracy, then review time/cost |
| O-04 | Adjudication model/profile | ambiguous-answer evaluation | same initial profile + teacher review |
| O-05 | Subjects/question types in MVP | representative tests | objective/numeric/exact/short semantic |
| O-06 | Model-answer document formats | sample solved papers/keys | four source roles in spec |
| O-07 | Auto-finalization desired | teacher policy + metrics | off |
| O-08 | Name auto-assignment desired | validation size/precision | off until gate |
| O-09 | Report logo/text/comments | school brand sample | simple school name/title |
| O-10 | Grade/report retention | school policy | grades indefinite operationally; scans fixed |
| O-11 | Backup destination | hardware/network | encrypted removable/local alternate disk |
| O-12 | Host hardware | inventory | recommended profile |

## 5. Deferred decisions

- PostgreSQL deployment;
- scanner hot folder/TWAIN/WIA;
- QR/barcode covers;
- multi-school/cloud;
- student/guardian portal;
- LMS/SIS sync;
- digital report signatures;
- essay auto-grading;
- local offline model;
- complex privacy/consent/ZDR UI;
- legal holds;
- long-term scan archive.

## 6. Glossary

| Term | Japanese/UI | Meaning |
|---|---|---|
| Ooki Grader | Ooki Grader | Product |
| Grading key | 採点基準 | Versioned questions, answers, points, regions, Kanji policy, rubrics |
| Automatic grading-key generation | 採点基準の自動作成 | AI-assisted creation of a grading-key draft |
| Model answer | 模範解答 | Teacher/source-provided intended correct answer; not “LLM output” |
| Contains model answers | 模範解答入り | Source role making visible answers authoritative |
| Contains non-model answers | 記入済み答案（AIが正答を作成） | Filled test whose visible answers are ignored as authority while AI solves independently |
| Provided model answer | `provided_model_answer` | Answer extracted from an authoritative model-answer/separate-key source |
| AI-proposed answer | AI提案 | Independently model-solved suggestion from a blank or non-model answered test; not authoritative until teacher verifies |
| Blank test | 問題のみ（未記入） | Question paper without filled answers |
| Separate answer key | 別紙の模範解答 | Solutions document separate from test pages |
| Template | テストひな形 | Logical test across versions |
| Template version | 採点基準の版 | Immutable published definition used by grading |
| Test session | テスト実施 | One dated administration using one version |
| Submission | 答案 | One uploaded completed student paper |
| Grading run | 採点実行 | Immutable machine/system evaluation attempt |
| Question result | 設問結果 | Transcription, outcome, points, reason for one question |
| Result revision | 採点修正 | Append-only effective correction |
| Finalize | 確定 | Make result current for progress/export |
| Reopen | 確定解除 | Return finalized result to review with reason |
| Economy / direct Gemini Batch | 旧Gemini一括採点 | Legacy persistence/recovery term; disabled for new work and absent from the current UI |
| OpenRouter queued | OpenRouter採点待ち | Durable individual OpenRouter requests; not provider batch |
| Expedite | 旧優先処理 | Legacy term; disabled for new work and absent from the current UI |
| Task profile | AI処理プロファイル | Provider/model/prompt/schema/input/strategy revision for one task |
| Capability probe | 接続・機能テスト | Synthetic check of key/model/features |
| Accuracy evaluation | 精度評価 | Versioned golden-set result for release qualification and advanced/manual profile activation; not a routine Gemini key-setup field |
| Auto-assignment | 自動割り当て | High-confidence local student match from AI transcription |
| Review queue | 確認待ち | Items requiring teacher/operator action |
| Kanji policy | 漢字条件 | Whether a non-Kanji answer can receive credit |
| Managed scan payload | 答案画像データ | Originals/derivatives counted within 150 GiB |
| Retention | 保存期間 | Three-calendar-month/space deletion rule |
| Tombstone | 削除記録 | Metadata that explains missing scan while preserving grade |
| Dispatch group | AI処理グループ | Local grouping for visibility; may be Gemini batch or OpenRouter queue |
| Provider operation ID | プロバイダー処理ID | Remote request/batch identity for reconciliation |
| Input manifest hash | 入力構成ハッシュ | Hash proving exact artifacts/config used |
| ULID | — | Sortable opaque internal identifier |

## 7. Source references verified 2026-07-27

Official Gemini:

- [Gemini 3.5 Flash-Lite](https://ai.google.dev/gemini-api/docs/models/gemini-3.5-flash-lite)
- [Batch API](https://ai.google.dev/gemini-api/docs/batch-api)
- [Structured outputs](https://ai.google.dev/gemini-api/docs/structured-output)
- [Document processing](https://ai.google.dev/gemini-api/docs/document-processing)
- [Files API](https://ai.google.dev/gemini-api/docs/files)
- [Pricing](https://ai.google.dev/gemini-api/docs/pricing)
- [API keys](https://ai.google.dev/gemini-api/docs/api-key)

OpenRouter:

- [Quickstart](https://openrouter.ai/docs/quickstart)
- [API overview and usage stats](https://openrouter.ai/docs/api_reference/overview)
- [Image input](https://openrouter.ai/docs/guides/overview/multimodal/image-understanding)
- [Multimodal overview](https://openrouter.ai/docs/guides/overview/multimodal/overview)
- [Structured outputs](https://openrouter.ai/docs/guides/features/structured-outputs)
- [Provider routing](https://openrouter.ai/docs/guides/routing/provider-selection)
- [Usage accounting](https://openrouter.ai/docs/cookbook/administration/usage-accounting)
- [Gemini 3.1 Flash-Lite model route](https://openrouter.ai/google/gemini-3.1-flash-lite)

Japanese personal information:

- [PPC APPI general guidelines](https://www.ppc.go.jp/personalinfo/legal/guidelines_tsusoku/)

Microsoft platform:

- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- [Host ASP.NET Core in a Windows Service](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/windows-service?view=aspnetcore-10.0)
- [EF Core SQLite provider](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/)

External behavior may change. Release engineering rechecks model IDs, supported parameters, pricing, limits, terms, and deprecations.
