# Ooki Grader

Ooki Grader is a staff-only grading system for a Japanese cram school. A Windows 11 host computer stores test scans and application data; teachers use a Japanese-first browser interface on the host or school LAN to manage students and answer keys, upload papers, review grading, and track results.

The repository contains an end-to-end teacher workflow: local
PDF/image processing, provider-neutral Gemini/OpenRouter AI dispatch,
coordinate-free
full-page understanding of interwoven Japanese test sheets, exception-only
correction after bulk confirmation, reports, verified backup/offline restore, and guarded
Windows technician tooling. It is still a pre-production system: real-school
accuracy evidence now covers one difficult Japanese fill-in sheet, but a broad
school golden set, production Windows/DR testing, and Authenticode release
signing remain external gates. See
[Implementation status](docs/implementation-status.md) for the exact boundary.

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

For the guarded Host + offline Tool + technician-script package and the Inno
Setup x64 installer, use PowerShell 7.4 and Inno Setup 6 on a controlled
Windows x64 build host:

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

The first command creates an immutable, fully inventoried payload. The second
re-verifies its version, complete checksum coverage, and approved publisher,
then produces `OokiGrader-Setup-0.1.0-x64.exe` plus JSON evidence and a SHA-256
file. Compilation and signing are Windows-only. The checked-in setup source and
wrapper are reproducible, but a setup EXE is not approved for school use until
the target-Windows, Authenticode, LAN, and disaster-recovery gates in
[Implementation status](docs/implementation-status.md) have passed.

## Guides and evaluation evidence

- [Teacher user guide (Japanese PDF)](output/pdf/ooki-grader-user-guide-ja.pdf)
- [Host/app setup and operations guide (Japanese PDF)](output/pdf/ooki-grader-host-operations-guide-ja.pdf)
- [Detailed host/app operations source](docs/operations/host-app-setup-and-operations-ja.md)
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

The host applies EF Core migrations automatically and creates its SQLite database and managed files under `Data:Root`. Relative data paths are resolved from the host content root. To keep development data in an explicit location, set an absolute `Data__Root`; set `Data__ObjectStore` and `Data__Incoming` as well if those should live elsewhere.

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

After bootstrap, open the administrator AI settings, create a Gemini
connection, and enter the credential there. Production Windows stores the
credential in a revisioned DPAPI envelope; non-Windows development keeps it
only in process memory. The checked-in grading path accepts the exact model
identifier `gemini-3.5-flash-lite`.

Run the connection test before activating task profiles. It checks
authentication plus model, image, structured-output, and usage support through
the normal Gemini API. New grading work does not expose a Batch/priority choice;
the host queues it safely and teachers work from one consistent flow. Pricing,
budget, and evaluation controls remain available under the folded advanced
settings. Never put a provider key in source, `appsettings`, a shell argument,
screenshots, or logs.

### Configure OpenRouter (optional)

The same administrator screen can store one OpenRouter connection separately
from Gemini. Use a school-controlled key and an exact OpenRouter model slug.
The host fixes the endpoint to `https://openrouter.ai/api/v1/`, requires
parameter-compatible routing, denies data-collection routes, requires Zero Data
Retention, and runs an image plus strict-structured-output capability test
before creating task profiles. A connection that cannot accept images remains
blocked.

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
| `Security:AllowedOrigin` | Exact origin accepted for API mutations | `https://ooki-grader.local` |
| `Security:RequireSecureCookies` | Uses a Secure `__Host-` session cookie | `true` |
| `Security:SessionIdleMinutes` | Session idle timeout | `30` |
| `Security:SessionAbsoluteHours` | Absolute session lifetime | `12` |
| `Security:BootstrapTokenHours` | First-run token lifetime, clamped to 1–24 hours | `24` |
| `Storage:PhysicalReserveBytes` | Free-space reserve enforced for uploads | `5368709120` |
| `Backup:Enabled` | Scheduled verified backups after destination setup | `false` |
| `Features:Recognition.AutoAssign` | Evaluation-gated automatic student assignment | `false` |
| `Features:Grading.AutoFinalize` | Evaluation-gated unattended finalization | `false` |
| `Features:Input.FullPageFallback` | Legacy compatibility setting; the current AI workflow uses normalized full pages | `false` |

Development overrides the allowed origin to `http://localhost:5173` and disables Secure cookies. Do not carry those two development overrides into a LAN or production deployment.

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
- Automatic image retention is three calendar months, with a 150 GB managed scan-storage cap.

The documents record external API facts as verified on **2026-07-27**. Model availability, pricing, quotas, and provider terms must be rechecked before every production release.

Product trade-offs are resolved in this order: **grading accuracy**, **teacher ease of use**, **total cost**, then implementation convenience. Security and privacy retain a practical school-grade baseline but should not add friction without a concrete risk or provider requirement.
