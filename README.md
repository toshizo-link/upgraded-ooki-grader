# Ooki Grader

Ooki Grader is a staff-only grading system for a Japanese cram school. A Windows 11 host computer stores test scans and application data; teachers use a Japanese-first browser interface on the host or school LAN to manage students and answer keys, upload papers, review grading, and track results.

The repository contains an end-to-end teacher workflow: local
PDF/image processing, provider-neutral Gemini/OpenRouter AI dispatch,
coordinate-free
full-page understanding of interwoven Japanese test sheets, exception-only
correction after bulk confirmation, reports, verified backup/offline restore, and guarded
Windows technician tooling. It is still a pre-production system: real-school
accuracy evidence now covers one difficult Japanese fill-in sheet, but a broad
school golden set and production Windows/DR testing remain external gates.
Authenticode signing remains a gate only for a conventionally distributed
Setup EXE; a separate, supervised on-site path accepts a personally delivered,
fully checksum-verified release without requiring a paid signing certificate. See
[Implementation status](docs/implementation-status.md) for the exact boundary.

The teacher UI now shares server-backed multi-term search, exact filters,
allowlisted sorting, active-filter summaries, and cursor paging across students,
test sessions, templates, and finalized reports. Teachers can export checked
results or every exportable result matching the current report filters as a
durable, previewed ZIP containing the canonical Japanese result PDFs and a
UTF-8 manifest CSV.

## Repository layout

- `src/OokiGrader.Domain` — grading, template, student-matching, scoring, and retention rules;
- `src/OokiGrader.Application` — application abstractions and orchestration;
- `src/OokiGrader.Ai.*` — provider-neutral AI contracts, official Gemini and OpenRouter standard clients, and legacy Gemini Batch compatibility code;
- `src/OokiGrader.Infrastructure` — EF Core/SQLite persistence, migrations, durable jobs, audit, and content-addressed storage;
- `src/OokiGrader.Preprocessing` — bounded PDF/image normalization, alignment, quality, thumbnails, and fingerprints;
- `src/OokiGrader.Reports.Pdf` — deterministic Japanese result reports;
- `src/OokiGrader.Host` — ASP.NET Core API, authentication, uploads, and background workers;
- `src/OokiGrader.Tool` and `installer` — offline health/restore CLI and Windows technician scripts;
- `src/OokiGrader.Web` — React/TypeScript teacher SPA;
- `tests` — domain through integration/provider/PDF/installer tests;
- `tools` — pinned external fixture downloaders and OpenAPI client generation;
- `docs/specification` — normative product and system specification.

## Prerequisites

- [.NET SDK 10.0.302](global.json). A later 10.0 patch is accepted by `global.json`.
- Node.js `^20.19`, `^22.13`, or `>=24`; Node.js 24 or newer is recommended. npm is included with Node.js.
- A trusted ASP.NET Core development HTTPS certificate for local browser development:

  ```text
  dotnet dev-certs https --trust
  ```

The production target remains a current Windows 11 Pro x64 host with HTTPS and modern Edge or Chrome clients. The development commands also work on macOS and Linux.

## Restore and build

Run these commands from the repository root:

```text
dotnet restore OokiGrader.slnx
npm ci --prefix src/OokiGrader.Web
dotnet build OokiGrader.slnx --configuration Release --no-restore
npm --prefix src/OokiGrader.Web run build
```

The web build is written to `src/OokiGrader.Web/dist`.

### Publish the combined host and SPA

`dotnet publish` runs a reproducible `npm ci`, builds the SPA, and includes only `dist` in the host's `wwwroot`. A Windows x64 self-contained publish can be produced with:

```text
dotnet publish src/OokiGrader.Host/OokiGrader.Host.csproj --configuration Release --runtime win-x64 --self-contained true --output artifacts/publish/win-x64
```

### Recommended supervised on-site package

For a small school where the technician personally carries the release to the
host, build the guarded Host + offline Tool + technician-script package. A paid
certificate is not required for this path:

```powershell
pwsh -File installer/New-OokiGraderReleasePackage.ps1 `
  -Version 0.1.0 `
  -OutputRoot C:\OokiGrader-Releases
```

Copy the resulting immutable `OokiGrader-0.1.0-win-x64` folder to a
technician-controlled USB drive. On the Windows 11 host, open PowerShell 7 as
Administrator in that folder and run:

```powershell
pwsh -NoLogo -NoProfile -File .\Install-OokiGraderOnSite.ps1
```

The guided script defaults to `https://ooki-grader.test/`, verifies the complete
release inventory, creates a free school-local CA and HTTPS certificate,
installs the Windows service, scopes the firewall to the school subnet, checks
database/storage/real HTTPS readiness, and produces a public-only classroom-PC
setup folder. Copy that folder—not the host certificate or private key—to each
authorized PC and right-click `Install-On-This-PC.cmd` → **Run as
administrator**. It validates its own checksums, installs the public CA and
managed hosts entry, verifies HTTPS without a warning bypass, and creates the
desktop shortcut.

This mode deliberately requires physical custody, a fixed or DHCP-reserved host
IP, an exact checksum manifest, typed confirmations, and a Windows Private
network. Checksums do not prove publisher identity, so never use the unsigned
mode for a package received through an untrusted download or third party.

### Optional signed Setup EXE

For broader distribution, use PowerShell 7.4 and Inno Setup 6 on a controlled
Windows x64 build host and provide an Authenticode signing hook:

```powershell
dotnet restore OokiGrader.slnx --runtime win-x64
pwsh -File installer/New-OokiGraderReleasePackage.ps1 `
  -Version 0.1.0 `
  -OutputRoot C:\OokiGrader-Releases `
  -SigningHook C:\secure\Sign-Ooki.ps1

pwsh -File installer/New-OokiGraderWindowsInstaller.ps1 `
  -PackageRoot C:\OokiGrader-Releases\OokiGrader-0.1.0-win-x64 `
  -Version 0.1.0 `
  -OutputRoot C:\OokiGrader-Releases `
  -ExpectedSignerThumbprint '<approved signer thumbprint>' `
  -SigningHook C:\secure\Sign-Ooki.ps1
```

The first command creates an immutable, fully inventoried and signed payload. The second
re-verifies its version, complete checksum coverage, and approved publisher,
then produces `OokiGrader-Setup-0.1.0-x64.exe` plus JSON evidence and a SHA-256
file. Compilation and signing are Windows-only. The checked-in setup source and
wrapper are reproducible, but a setup EXE is not approved for school use until
the target-Windows, Authenticode, LAN, and disaster-recovery gates in
[Implementation status](docs/implementation-status.md) have passed.

## Guides and evaluation evidence

- [On-site installation guide (Japanese PDF; supervised no-paid-certificate path)](output/pdf/ooki-grader-onsite-installation-guide-ja.pdf)
- [On-site installation source (Japanese)](docs/operations/onsite-installation-ja.md)
- [Teacher user guide (Japanese PDF; 28-page task walkthrough with real-app screens and fictional demo data)](output/pdf/ooki-grader-user-guide-ja.pdf)
- [Host/app setup and operations guide (Japanese PDF; 19 pages covering daily checks, bulk reports, backup, incidents, recovery, and updates)](output/pdf/ooki-grader-host-operations-guide-ja.pdf)
- [Detailed host/app operations source](docs/operations/host-app-setup-and-operations-ja.md)
- [Real-app manual screenshot manifest (fictional data only)](output/playwright/manual-20260810/MANIFEST.md)
- [Japanese fill-in template accuracy report](output/accuracy/fill-in-template-generation-report-2026-08-05.md)
- [OpenRouter DeepSeek V4 Flash / Gemini comparison](output/accuracy/openrouter-deepseek-v4-vs-gemini-report-2026-08-05.md)

## Run locally

Development uses two processes. Start the API first on `https://localhost:7047`; this matches the checked-in Vite proxy.

PowerShell:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "https://localhost:7047"
dotnet run --project src/OokiGrader.Host/OokiGrader.Host.csproj --no-launch-profile
```

macOS/Linux:

```sh
ASPNETCORE_ENVIRONMENT=Development \
ASPNETCORE_URLS=https://localhost:7047 \
dotnet run --project src/OokiGrader.Host/OokiGrader.Host.csproj --no-launch-profile
```

In a second terminal:

```text
npm --prefix src/OokiGrader.Web run dev
```

Open `http://localhost:5173`. The SPA proxies `/api` requests to the host while preserving the browser origin required by the request guard. To use a different API URL, set `OOKI_API_PROXY` when starting Vite and set `Security__AllowedOrigin` on the host to the exact browser origin.

The host applies EF Core migrations automatically. Relative data paths are
resolved from the host content root. When using an absolute development data
root, set `Data__Root`, `Data__ObjectStore`, `Data__Incoming`, and
`Data__Reports` together; otherwise the database and uploaded files can point
at different trees after a restart.

### First-run bootstrap

On the first host startup:

1. The host creates `bootstrap-token.txt` directly inside `Data:Root`. The default token lifetime is 24 hours.
2. Open the SPA from the host computer itself. Bootstrap completion is deliberately restricted to a loopback connection.
3. Enter the token, administrator username, display name, and a password of at
   least 12 characters. This school-specific build records the school as
   `大木スクール`.
4. After successful completion, the administrator is created, the token is invalidated, and the token file is deleted.
5. Sign in with the new administrator account.

For an unattended development setup, `Security__BootstrapToken` can provide the initial token before the first startup. Do not put a production bootstrap token or provider key in source control.

The liveness and readiness probes are available at `/health/live` and `/health/ready`. Development OpenAPI JSON is available at `/openapi/v1.json`.

### Configure Gemini

After bootstrap, open the administrator AI settings, add or replace the Gemini
API key, and choose **接続を確認して有効化**. The host tests the supplied
candidate before it changes any saved state. Authentication, exact-model,
image-input, strict structured-output, usage-metadata, and representative image
task checks must all pass. Only then does the host encrypt and persist the key
and atomically make the exact current profiles for template extraction, name
transcription, initial grading, and adjudication available. A failed or
ambiguous replacement leaves the previous working key, connection, and task
profiles unchanged.

Production Windows stores the credential in a revisioned DPAPI envelope. macOS
development stores it in an authenticated encrypted file bound to the
persistent ASP.NET Core Data Protection key ring under `Data:Root`, so a normal
Host restart does not require the key to be entered again. This macOS store is
development-only; moving the data root or restoring it on another machine still
requires re-entry. The checked-in grading path accepts the exact model
identifier `gemini-3.5-flash-lite`.

The normal Gemini screen does not ask a school administrator to create an
evaluation record, approve a pilot, or activate four profiles by hand. Its
manual connection test repairs missing or stale current profiles after a full
capability pass, and startup reconciles active Gemini profiles after a
checked-in prompt/schema/hash revision change. New grading work does not expose a
Batch/priority choice; the host queues it safely and teachers work from one
consistent flow. Timeout, concurrency, pricing, and budget settings remain
under folded details. Advanced model evaluation and manual profile endpoints
remain available for OpenRouter and backward-compatible technical operation.
Never put a provider key in source, `appsettings`, a shell argument,
screenshots, or logs.

Capability-gated activation makes AI drafting available; it never starts
reception, assigns a student, or finalizes a result. Teachers still compare the
source, correct the AI draft, explicitly start reception for the confirmed
template, and explicitly finalize grades. Starting reception atomically fixes
the immutable template version and opens its first test session; teachers do
not publish and then create the same test again in a second screen. Formal
golden-set accuracy evaluation remains a release and model-change quality gate,
not a routine Gemini setup step.

### Configure OpenRouter (optional)

The same administrator screen can store one OpenRouter connection separately
from Gemini. OpenRouter does not use Gemini's one-step `testAndEnable` path:
save the advanced connection first, then explicitly run **再確認**. Use a
school-controlled key and an exact OpenRouter model slug. The host fixes the
endpoint to `https://openrouter.ai/api/v1/`, requires parameter-compatible
routing, denies data-collection routes, requires Zero Data Retention, and runs
an image plus strict-structured-output capability test during that manual
recheck. A connection that cannot accept images remains blocked; separately
evaluated profiles still require the advanced manual activation workflow.

Pricing is stored per provider and exact model from an official provider URL.
Gemini usage is settled from returned token counts and the approved price
snapshot. OpenRouter's returned `usage.cost` is authoritative when present;
the host keeps the conservative reserved amount instead of treating a missing
cost as free, and records the routed provider for audit.

`deepseek/deepseek-v4-flash` and the fixed
`deepseek/deepseek-v4-flash-0731` snapshot are text-only. Both passed a live
structured-text probe but rejected the supplied Japanese worksheet image, so
they are not eligible for template generation, name reading, or image grading.
Gemini 3.5 Flash Lite remains the checked-in default and there is no automatic
cross-provider failover. See the
[visual-workflow eligibility report](output/accuracy/openrouter-deepseek-v4-vs-gemini-report-2026-08-05.md).

## Test and quality checks

After the restore/build above:

```text
dotnet test OokiGrader.slnx --configuration Release --no-build
npm --prefix src/OokiGrader.Web test
npm --prefix src/OokiGrader.Web run check
npm --prefix src/OokiGrader.Web run build
npm --prefix src/OokiGrader.Web run api:check
npm --prefix src/OokiGrader.Web audit
npm --prefix tools/openapi-client audit
dotnet list OokiGrader.slnx package --vulnerable --include-transitive
```

Use `npm --prefix src/OokiGrader.Web run test:watch` for interactive frontend test development.
Public handwritten-exam and Japanese-handwriting smoke fixtures are opt-in;
their licences, pinned hashes, downloaders, and commands are documented in
[fixture testing](docs/testing/handwritten-exam-fixtures.md).

## Configuration and security

Configuration follows normal ASP.NET Core precedence. Use `appsettings.json`, an environment-specific file, or environment variables with `__` as the section separator. Important settings include:

| Setting | Purpose | Checked-in default |
| --- | --- | --- |
| `Data:Root` | SQLite database, bootstrap token, and managed data root | `.data` |
| `Data:ObjectStore` | Content-addressed object storage | `.data/objects` |
| `Data:Incoming` | Resumable upload staging | `.data/incoming` |
| `Data:Reports` | Generated report artifacts | `.data/reports` |
| `Security:AllowedOrigin` | Exact origin accepted for API mutations | `https://ooki-grader.test` |
| `Security:RequireSecureCookies` | Uses a Secure `__Host-` session cookie | `true` |
| `Security:SessionIdleMinutes` | Session idle timeout | `30` |
| `Security:SessionAbsoluteHours` | Absolute session lifetime | `12` |
| `Security:BootstrapTokenHours` | First-run token lifetime, clamped to 1–24 hours | `24` |
| `Storage:PhysicalReserveBytes` | Free-space reserve enforced for uploads | `5368709120` |
| `Backup:Enabled` | Scheduled verified backups after destination setup | `false` |
| `TemplateGeneration:MaximumUnitsPerBatch` | Maximum deterministic template units created from one source PDF | `200` |
| `TemplateGeneration:MaximumSourcePages` | Maximum source-PDF pages accepted by the deterministic planner | `1000` |
| `Workers:TemplateGenerationUnit:PollInterval` | Durable unit-worker queue polling interval | `00:00:01` |
| `Workers:TemplateGenerationUnit:LeaseDuration` | Lease duration for one deterministic extraction unit | `00:10:00` |
| `Workers:TemplateGenerationUnit:MaximumProviderMediaBytes` | Maximum derived unit size sent to the active provider | `12582912` |
| `Workers:TemplateGenerationUnit:MaximumStoredResponseCharacters` | Bound on retained structured response text | `1000000` |
| `Workers:AiInitialGrading:MaximumMediaBytes` | Configured chunk-media ceiling; the worker additionally caps raw media at 12 MiB and below the dynamic 18 MiB serialized-request budget | `17825792` |
| `Features:Ai.TemplateGeneration` | Enables AI-assisted template generation, including the deterministic batch path | `true` |
| `Features:Recognition.AutoAssign` | Evaluation-gated automatic student assignment | `false` |
| `Features:Grading.AutoFinalize` | Evaluation-gated unattended finalization | `false` |
| `Features:Input.FullPageFallback` | Legacy compatibility setting; the current AI workflow uses normalized full pages | `false` |

Development overrides the allowed origin to `http://localhost:5173` and disables Secure cookies. Do not carry those two development overrides into a LAN or production deployment.

### Deterministic template creation rollout and rollback

New creation starts with the teacher selecting `HOP`, `STEP`,
`クラス分けテスト`, or `その他` and one of the four supported subjects before
upload. Only `その他` adds the `通常` / `穴埋め` choice. The host plans HOP as
one independent template per page and STEP as one independent template per two
pages; STEP rejects any PDF whose page count is not divisible by six before AI
work or budget reservation. Class-placement and Other PDFs stay whole. There
is no AI test-type, split, variation, subject, naming, grade, or
orientation-preflight task.

Template extraction uses one orientation-gated request. Upright media is
extracted in that same response. A valid `rotate` response causes the host to
apply per-page quarter turns locally and make exactly one corrected-media
request; another rotation request is a blocking failure. Paper name and grade
are returned with extraction, and filename grade is parsed locally. Final check
resolves the grade first, then the host assigns immutable names to known types:
HOP `{subject}{grade}年HOP{unitSequence}`, STEP
`{subject}{grade}年STEPセット{set}-{variation}`, and class placement
`{subject}{grade}年クラス分けテスト`. For these types, the AI-read printed
title is retained only as provenance/reference and never controls or edits the
final name. Only `その他` uses an editable title, initially proposed from the
printed title when available, before the batch is transactionally converted to
independent draft templates.

Roll out only after a verified backup, migration rehearsal, verification that
startup or a successful manual connection test reconciled the exact v2/v5
current profile, and synthetic HOP/STEP acceptance runs. Formal provider-backed
and golden-set checks remain release evidence, not a school-administrator
approval screen. The migration is additive and keeps historical templates and
legacy jobs readable. To stop new generation, set
`Features:Ai.TemplateGeneration` to `false`; queued and stored batch records
remain durable for diagnosis. Application rollback must use the repository's
offline restore procedure against the pre-upgrade verified backup when the
older binary cannot read the newer schema. Do not downgrade the executable over
a migrated live database, delete batch rows, or reactivate the removed legacy
creation UI as a shortcut.

### Ordered one-page scan intake

Completed papers use one ordered intake pipeline driven by the published
template version's expected submission page count. HOP groups one one-page PDF
per submission. Each registered STEP variation/session (`-1`, `-2`, or `-3`)
is a separate test and groups two consecutive PDFs; the original six-page STEP
pack is not one grading session. Class-placement and Other group the complete
published page count, from one through the supported maximum of 50 pages. The
browser records an explicit one-based input ordinal before parallel upload;
server completion order, timestamps, and filenames never decide ownership.

Every uploaded file must be a one-page PDF. The host classifies it locally
against every template page, requires an unambiguous page role, and blocks the
batch on missing, repeated, foreign, ambiguous, or out-of-order pages. Valid
groups are materialized as one logical multipage submission with immutable
source-page hash/ordinal provenance, then enter the existing preprocessing,
student-name, and grading pipeline. The student's written name is expected on
logical page 1 and applies to the rest of that ordered group. For the normal
Gemini path, the first bounded grading request reads that identity field and
grades the visible answers in one response; the roster is still matched only
on the host and a teacher still confirms the student. Later chunks never return
identity. The legacy name-only request remains a fallback when combined
analysis is unavailable.

Initial grading keeps a long paper as one logical submission but sends its
normalized pages in deterministic consecutive chunks. A chunk has at most 32
page images and at most the smallest of the configured media limit, 12 MiB of
raw media, and the dynamic budget that keeps Gemini's complete serialized
request below 18 MiB. Every chunk is durable and idempotent, and the host creates
one combined grading run only after all chunks succeed. If more than one chunk
claims the same question, that question is assigned zero proposed points and
requires teacher review instead of being resolved by request order. Oversized
prompts or a single page that cannot fit fail locally before provider disclosure.
Within each chunk Gemini reads the original page pixels and returns the visible
answer transcription, outcome, and points in the same response. The prompt keeps
visible line boundaries for audit but forbids treating visual line wrapping or
layout whitespace alone as an incorrect answer; a narrow host check also repairs
an AI false negative when an accepted answer differs only by CR/LF wrapping.
Until the teacher confirms or explicitly leaves the identity unidentified, a
completed run is staged and hidden from finalization; confirming identity
activates that same run without sending the answer pages again.

Teachers can open one answer-specific grading workspace from the review queue
or test-session detail. It displays the complete original/assembled PDF (with
lazy normalized-page fallback), all question results, and append-only score,
outcome, and transcription edits. `未確認を一括確認` preserves every proposed
value and resolves only the exact versioned unresolved set shown in the dialog;
it does not finalize the submission and rejects the entire action if another
teacher changed any selected result.

This workflow deliberately accepts the school's scanner-order contract: page
2 and later are assumed to belong to the student whose page 1 immediately
precedes them. Without an identifier on later pages, software cannot detect a
correctly positioned page from a different student. Operators must therefore
scan each student's complete paper consecutively and review the visible group
boundaries before freezing the batch.

The normal age/quota retention policy covers the original one-page scans, the
assembled PDF, normalized pages, thumbnails, and grading image evidence. After
deletion, live file links are removed and the submission is `scan_deleted`,
while ordered page numbers/hashes, structured grading runs and revisions,
totals, audit records, and result reports remain available.

Authenticated mutations require both the opaque session cookie and a rotated CSRF token. All mutations require exactly one `Origin` header matching `Security:AllowedOrigin`. Passwords are hashed with Argon2id, published template versions are protected by database integrity triggers, SQLite uses WAL and foreign keys, and the object store is content-addressed.

Treat `Data:Root` as sensitive school data: restrict filesystem access, use
full-disk encryption on the Windows host, and do not expose the host directly
to the public internet. Configure and confirm an encrypted backup destination
before enabling scheduling. Use the online backup service and offline restore
tool; do not treat RAID as backup or casually copy a live SQLite database/WAL
set. A backup is not production evidence until an isolated restore drill has
succeeded.

## Specification

Start with the [specification index](docs/specification/README.md).

The documents cover:

- product requirements and acceptance criteria;
- Windows/LAN architecture and technology choices;
- domain model, database schema, filesystem layout, and retention;
- official Gemini API and OpenRouter integration, automatic grading-key generation, name recognition, and economical grading;
- internal REST API and background-job contracts;
- Japanese-first teacher UX;
- security, privacy, deployment, backup, and operations;
- test strategy, AI quality gates, observability, and incident handling;
- phased implementation plan, work breakdown, risks, and release gates.

The specification is the target design; [Implementation status](docs/implementation-status.md) records which parts are executable today.

## Important product constraints

- The application is for authorized school staff, not for student self-service.
- Test images remain on the host except for approved, short-lived normalized
  pages sent to the active, image-capable Gemini or OpenRouter profile for
  template creation, name reading, grading, or adjudication.
- The checked-in default uses a school-supplied official Gemini API key.
  An optional school-supplied OpenRouter connection is supported, but only a
  model that passes both the image capability probe and the school accuracy
  gate can be activated. Cross-provider failover is intentionally disabled.
- AI output is advisory until it passes confidence rules or a teacher confirms it.
- Teachers never draw question boxes or enter coordinates. The selected AI
  provider receives complete normalized pages and matches each logical question
  by its printed label, wording, and page context.
- New template creation always selects test type and subject before upload.
  HOP/STEP boundaries and STEP `-1`/`-2`/`-3` suffixes are host-owned,
  deterministic rules; the three STEP variations are unrelated templates after
  creation.
- The question editor exposes `完答`, `順不同`, and `漢字必須` independently.
  Complete-answer questions are all-or-nothing, order-insensitive answers
  compare explicitly separated components with duplicate counts preserved, and
  the Kanji policy remains compatible with the `allowNonKanji` API field.
- Removing a template is a recoverable archive, not destructive erasure.
  Archived templates cannot be edited or selected for new tests, while their
  published versions, existing sessions, grading results, and audit history
  remain readable and can be restored from the archived filter. Archive waits
  for any automatic draft extraction to finish. A closed test session can be
  archived only after every submission and background intake/grading task is
  terminal; archived sessions leave action queues and become read-only.
- Automatic image retention is three calendar months, with a 150 GB managed scan-storage cap.

The documents record external API facts as verified on **2026-07-27**. Model availability, pricing, quotas, and provider terms must be rechecked before every production release.

Product trade-offs are resolved in this order: **grading accuracy**, **teacher ease of use**, **total cost**, then implementation convenience. Security and privacy retain a practical school-grade baseline but should not add friction without a concrete risk or provider requirement.
