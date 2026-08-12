# Vision, scope, and success criteria

## 1. Product vision

Ooki Grader reduces the repetitive work of grading paper tests without removing teacher control. It gives a Japanese cram school one dependable, local system of record for blank test definitions, submitted paper scans, student identities, grades, corrections, progress views, and exported reports.

The product is intentionally a **host-centric LAN application**:

- the school retains local ownership of files;
- staff can use all existing Windows 11 computers without installing a full desktop client;
- there is one authoritative database and one file-retention process;
- only the host communicates with the external AI provider;
- the school can keep uploading and reviewing existing data during an internet outage, while AI tasks wait safely in a queue.

AI is used where visual or language understanding is valuable. Deterministic software owns identity records, scoring arithmetic, retention, permissions, audit history, and business rules. A teacher can always inspect and override an AI result.

## 2. Goals

### G-01 — Centralized local scan management

All staff computers on the authorized school LAN can upload and view test scans through the application. Original files and derivatives are stored on the host filesystem, never scattered across peer computers as application state.

### G-02 — Fast test setup

A teacher can import a blank test, a test containing model answers, a completed
test whose answers are not authoritative, and/or a separate answer key; receive
an editable **grading-key draft** containing questions, answer proposals with
clear provenance, accepted variants, points, Kanji policy, and rubrics; correct
it; and publish it for grading.

### G-03 — Accurate, usable, cost-controlled grading assistance

The school can configure the official Gemini API, OpenRouter, or both. The
normal Gemini flow tests a candidate credential and the exact current visual
contract before saving it, then makes the four advisory AI tasks available as
one atomic operation. OpenRouter remains an advanced, explicitly profiled
option. The product records token usage and estimated/actual cost, avoids
duplicate work, and keeps release/model-change accuracy evaluation separate
from routine school setup. AI availability never removes teacher review,
publication, assignment, or finalization gates.

### G-04 — Safe student matching

The system recognizes the handwritten or printed name, compares it only against the school roster, automatically assigns a student only above a calibrated precision threshold, and sends ambiguous cases to staff.

### G-05 — Defensible grades

Every question result records the test-template version, submitted answer, grading method, confidence, reason code, model/prompt version where applicable, and teacher overrides. Total scores are computed locally from question results.

### G-06 — Useful learning history

Teachers can filter a student's finalized tests by date and test/category, see score and accuracy trends, open the underlying result detail while scans are retained, and export a clear Japanese PDF report.

### G-07 — Predictable retention

The scan store never grows without bound. Scan payload older than three calendar months is deleted, and an oldest-first quota policy keeps managed scan storage at or below 150 GB. Grade records survive scan deletion.

### G-08 — Operable at a school

Installation can be performed by a technician. After setup, ordinary operation, backup, restore, key rotation, queue recovery, and storage cleanup are visible and documented for a school administrator.

## 3. Non-goals for version 1

The following are explicitly out of scope unless a later specification adds them:

- student or guardian accounts, student-facing web pages, or internet-facing access;
- remote multi-school tenancy or cloud-hosted primary storage;
- direct access to the host folder via SMB as an application workflow;
- direct scanner-driver control such as TWAIN/WIA from peer browsers;
- automatic creation of pedagogically correct answer keys without teacher approval;
- automatically finalizing essays or subjective long-form responses without
  explicit teacher finalization of the completed paper;
- facial recognition, behavioral scoring, plagiarism detection, or proctoring;
- a native Android/iOS application;
- syncing to a school information system, accounting platform, LMS, or messaging service;
- editing the original paper image;
- indefinite archival of original scans;
- making consequential enrollment, discipline, class-placement, or employment decisions;
- guaranteeing that an AI result is correct.

## 4. Users and roles

### 4.1 System administrator

Usually a trusted school manager or technician. Can:

- configure site, network, retention, AI key, model, budgets, and backups;
- create and disable staff accounts;
- view system health, audit events, deletion history, and cost usage;
- restore data and run maintenance;
- perform every teacher and operator action.

### 4.2 Teacher

Can:

- manage students and aliases;
- create and publish test templates;
- create test sessions;
- upload papers;
- review student matching and grades;
- override grades with a required reason;
- view progress and export reports.

Cannot read the API key, change security/retention controls, restore backups, or erase audit history.

### 4.3 Scan operator

Can:

- view the permitted roster and active test sessions;
- upload and re-upload scan files;
- inspect upload and page-quality errors;
- resolve obvious page ordering and duplicate-upload issues if granted.

Cannot publish answer keys, finalize grades, see global progress analytics, export reports, or configure AI.

### 4.4 Read-only reviewer

Optional role for a school director. Can view finalized scores, reports, progress, and audit summaries but cannot change records or view unfinalized answer crops unless explicitly authorized.

### 4.5 Student

Not an application user. A student is a data subject represented by a roster record. Reports are printed or otherwise delivered by authorized school staff outside Ooki Grader.

## 5. Operating assumptions

The baseline sizing target is:

| Dimension | Baseline | Design verification target |
|---|---:|---:|
| School sites per installation | 1 | 1 |
| Registered students | 2,000 | 10,000 |
| Simultaneous browser users | 20 | 50 |
| Peer computers | 25 | 50 |
| Test templates | 2,000 | 10,000 |
| Completed test submissions/year | 50,000 | 200,000 |
| Questions per submission | 50 typical | 300 maximum |
| Pages per submission | 4 typical | 50 maximum |
| Single local upload | 20 MB typical | 250 MB maximum |
| Managed scan store | 150 GB hard cap | fixed requirement |
| LAN | 1 Gbps recommended | 100 Mbps minimum |
| Internet | intermittent tolerated | required for AI only |

These values are capacity targets, not license limits. Crossing a target should generate telemetry and a capacity warning, not corrupt data.

## 6. Product principles

1. **Accuracy first:** a model/prompt bundle reaches a release only after the
   release quality gate on real school-style tests; routine Gemini credential
   setup capability-gates advisory profiles and never authorizes automatic
   publication, assignment, or finalization.
2. **Teacher authority:** teachers approve answer keys and can correct every grade.
3. **Ease of use:** normal scanning, reviewing, and exporting require no provider knowledge.
4. **Precision before automation:** an uncertain name or grade is queued, not guessed.
5. **Cost visibility:** economical routes are preferred only when they preserve the required accuracy.
6. **Local-first custody:** long-lived student data stays on the host.
7. **Immutable provenance:** published templates and finalized grading runs are versioned.
8. **Deterministic totals:** software, not the model, calculates points and totals.
9. **Visible failure:** queues, errors, provider delay, and storage pressure are shown in the UI.
10. **Japanese-first quality:** names, Kanji policy, dates, fonts, line breaking, and PDF layout are treated as core behavior.

## 7. Definition of product success

The pilot is successful only when all of the following hold for a representative, school-approved evaluation set:

- teachers can set up a supported blank test without developer intervention;
- at least 95% of clearly segmented objective questions agree with the adjudicated teacher grade;
- the precision of automatic student assignment is at least 99.5% at the chosen threshold; lower-confidence cases may reduce automation coverage but must not lower precision;
- no Kanji-required test case is marked correct solely because a phonetic equivalent has the same meaning;
- 100% of teacher overrides are attributable and auditable;
- no finalized total differs from the sum of its stored question points;
- a test remains uploadable from a peer while the Internet is unavailable and grades automatically when service returns;
- managed scan bytes return below the quota low-water mark after cleanup;
- a restore drill recovers accounts, roster, templates, scores, audit records, and configuration within the documented recovery target;
- a teacher can produce a valid Japanese PDF result without the original scan;
- the API key cannot be retrieved through browser APIs, logs, exports, backups, or peer filesystem access.

AI quality targets in this document are launch gates to validate, not claims that a provider model will automatically meet them.

## 8. Release boundaries

### MVP

- staff authentication and roles;
- student roster CRUD and CSV import;
- blank test PDF/image upload;
- AI-generated grading-key draft with manual editor;
- “contains model answers” source mode that extracts supplied answers as authoritative instead of solving independently;
- “contains non-model answers” source mode that ignores visible responses and
  independently generates answer proposals;
- per-question non-Kanji-answer checkbox;
- test sessions and resumable scan upload;
- student-name candidate recognition;
- objective/short-answer grading with review queue;
- result detail, teacher override, and finalization;
- per-student trend graph;
- per-test student PDF export;
- 150 GB/three-month scan cleanup;
- official Gemini API integration, including Gemini 3.5 Flash-Lite Batch API;
- OpenRouter integration with compatible multimodal structured-output models;
- candidate-first Gemini setup with atomic current task profiles, plus advanced
  provider/model profiles and capability/accuracy validation;
- Windows service installer, backup, health page, and audit log.

### Post-MVP candidates

- QR/barcode cover sheets;
- scanner hot-folder ingestion;
- CSV bulk result export;
- test/category comparison and cohort analytics;
- rubric libraries;
- automatic template identification;
- local OCR acceleration;
- PostgreSQL deployment profile for very high volume;
- offline local model adapter;
- school information system integration;
- signed report PDFs.

## 9. Key constraints requiring explicit acceptance

1. A direct Gemini batch job may take up to 24 hours according to Google's target. That route cannot promise instant results.
2. OpenRouter currently has no documented general discounted asynchronous chat batch endpoint; app-side queued dispatch improves throughput/operability but does not itself reduce per-token prices.
3. The school must provision an approved official Gemini and/or OpenRouter account/key with sufficient billing/credits.
4. Students and minors must not be given access when the configured provider's current terms prohibit it.
5. The school has accepted the provider-processing model; the installer only needs to ensure the selected account/key satisfies the provider's technical and account requirements.
6. Scans are intentionally deleted. After deletion, teachers keep structured results and reports but cannot reopen the original handwriting image.
7. A failed host disk without a working backup can lose records.
8. Handwriting recognition and semantic grading remain probabilistic. The school must maintain a review procedure.
