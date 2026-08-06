# Windows deployment and operations

> **Current implementation note (2026-08-05):** This file retains parts of the
> original target design for traceability. The executable teacher flow uses
> normal queued requests with Gemini (`gemini-3.5-flash-lite`) as the default;
> legacy Batch controls remain hidden. An optional OpenRouter standard client
> and administrator connection test are implemented, but only image-capable,
> structured-output models with approved accuracy evidence can be activated.
> DeepSeek V4 Flash is text-only and therefore blocked from the current visual
> workflow. Cross-provider automatic failover remains disabled. The repository
> includes an Inno Setup 6 x64 installer target, but signing and target-Windows
> drills remain external release gates.
> Use the [implementation status](../implementation-status.md) and the current
> [Japanese host/app operations guide](../operations/host-app-setup-and-operations-ja.md)
> for deployment decisions.

## 1. Deployment objective

A technician installs one dependable Windows 11 host. After commissioning:

- staff open one HTTPS URL from any authorized peer;
- the service starts without an interactive Windows login;
- uploads and non-AI operations continue during Internet outages;
- either official Gemini, OpenRouter, or both can be configured;
- routine backup, queue, storage, certificate, and update status is visible in the app;
- an ordinary teacher does not need to understand Windows services, databases, model slugs, or provider routing.

Containers, WSL, Kubernetes, public cloud hosting, and peer database clients are not part of the v1 deployment.

## 2. Hardware profile

### 2.1 Recommended host

| Component | Recommended | Minimum pilot |
|---|---|---|
| OS | Windows 11 Pro, current supported release | Windows 11 Pro |
| CPU | 8 modern physical cores / 16 threads | 4 cores / 8 threads |
| RAM | 32 GB | 16 GB |
| Application/system SSD | 256 GB+ NVMe | 128 GB free-enough system disk |
| Data SSD | 512 GB+ enterprise/quality NVMe or SATA SSD | 256 GB with verified capacity model |
| Managed scan quota | 150 GiB | 150 GiB fixed |
| Backup | separate encrypted 512 GB+ destination | separate encrypted destination |
| Network | wired 1 Gbps | wired 100 Mbps |
| Power | UPS with USB shutdown support | surge protection |

Why the data disk is larger than 150 GiB:

- originals plus derived images temporarily coexist;
- the database, templates, reports, temporary work, and logs are outside the managed scan quota;
- Windows and SQLite need free space;
- a 5 GiB emergency reserve is mandatory;
- SSD performance and reliability degrade when nearly full.

RAID is not a backup. If the host uses a single disk, daily verified backup is essential.

### 2.2 Peer

- Windows 11;
- Edge or Chrome current/previous major;
- 4 GB available memory recommended during large multi-upload;
- stable wired or Wi-Fi LAN;
- local CA trusted.

## 3. Software packaging

Release artifacts:

```text
OokiGrader-Setup-<version>-x64.exe
OokiGrader-Setup-<version>-x64.exe.sig
checksums.txt
sbom.spdx.json
release-notes-ja.md
technician-install-guide-ja.md
```

The host build is self-contained x64 .NET and does not require a separately managed runtime. It includes:

- host Windows service;
- static frontend;
- database migrations;
- restricted preprocessing helpers;
- Japanese fonts and license notices;
- repair/backup/restore CLI;
- default configuration schema;
- no API key or school data.

All binaries and installer are code-signed. Release checksums are published through the project's controlled distribution channel.

## 4. Installation modes

### 4.1 Host install

Installs:

- `OokiGrader.Host` Windows service;
- application files in `C:\Program Files\Ooki Grader\`;
- service configuration in a protected ProgramData path;
- data root on selected volume, recommended `D:\OokiGraderData`;
- service virtual account and ACLs;
- HTTPS certificate binding;
- Windows firewall rule;
- database;
- recovery/diagnostic CLI;
- Start-menu links for host console and web app.

### 4.2 Peer setup

Optional technician package/script:

- installs the school-local CA certificate;
- creates an Ooki Grader browser shortcut/PWA;
- verifies DNS, TLS, and host health;
- does not install service, key, database, or data folder;
- can be rerun idempotently.

Manual peer setup is acceptable for a small school.

### 4.3 Repair/upgrade

The same installer detects the installation and offers:

- verify/repair application binaries and service;
- renew/replace certificate;
- change host DNS name/IP binding;
- move data root through a verified copy workflow;
- upgrade;
- uninstall application while preserving data by default.

Uninstalling data is a separate explicit operation with typed confirmation and backup reminder.

## 5. Host installation procedure

### 5.1 Preflight

The installer checks and records:

- administrator privilege;
- Windows edition/build/support;
- x64 CPU;
- RAM/CPU;
- NTFS data volume and free bytes;
- BitLocker state;
- existing ports/certificates/service;
- host name and private IP;
- network profile is Private;
- system time/time zone;
- Defender health;
- backup destination;
- Internet reachability to configured provider endpoints if provider setup is being performed.

Blocking:

- unsupported Windows;
- non-NTFS data root;
- data root inside a profile/temp/synchronized cloud folder;
- insufficient capacity;
- unresolved prior migration/restore;
- port conflict without technician resolution.

Warnings:

- no BitLocker;
- Wi-Fi-only host;
- no UPS;
- missing backup;
- public network profile;
- host using DHCP without reservation.

### 5.2 Files, service, and ACL

1. Verify installer signature.
2. Stop existing service for upgrade.
3. Install versioned application files to a staging version directory.
4. Create/verify virtual service identity.
5. Create data directories with explicit ACL.
6. Install service with delayed automatic start.
7. Set service recovery:
   - restart after 10 seconds on first failure;
   - restart after 60 seconds on second;
   - no endless rapid loop; log critical after repeated failure.
8. Set working directory and protected config pointer.
9. Run database migration/health pre-start.
10. Atomically switch current version.
11. Start service and wait for readiness.

### 5.3 Network

1. Select canonical DNS name.
2. Add router/local DNS record and DHCP reservation.
3. Create/import CA and host certificate with DNS/IP SANs.
4. Bind Kestrel to configured private interface and 443.
5. Create firewall rule scoped to Private profile and school subnet.
6. Verify HTTP is closed or redirects only as configured.
7. From one peer, validate DNS, certificate chain, login page, and upload route.

### 5.4 First-run wizard

Host-local wizard:

1. school display name, locale, time zone;
2. first administrator;
3. data/quota confirmation;
4. backup target/schedule;
5. provider connection(s);
6. task-profile defaults;
7. test synthetic capability call;
8. create a sample non-student template/submission;
9. print/save commissioning report.

Provider setup can be skipped; uploads/local work still function and AI jobs show configuration required.

## 6. AI provider commissioning

### 6.1 Official Gemini

Technician:

1. obtains the school's current Gemini API key;
2. confirms billing/quota appropriate for student processing;
3. enters key once;
4. selects the pinned `gemini-3.5-flash-lite` model;
5. runs image + strict structured-output probe;
6. records the current official price snapshot;
7. configures the daily/monthly Ooki budget;
8. sets bounded standard-request concurrency;
9. validates one school-approved sample before active status.

### 6.2 OpenRouter

Technician:

1. creates a school key with credit limit/guardrail;
2. ensures adequate credits or configured BYOK;
3. enters key once;
4. enters the exact candidate model slug;
5. runs real text and image Chat Completions probes for exact-model, parameter, strict JSON-schema, and usage/cost support;
6. requires compatible parameters, data-collection denial, and Zero Data Retention routing;
7. records the current official provider/model price snapshot;
8. validates the same sample used for direct Gemini;
9. sets bounded concurrency and retry behavior without automatic provider fallback.

### 6.3 Dual-provider setup

Optional evaluation, not automatic routing:

- choose one active initial-grading profile;
- optionally choose the other connection/model as a validated adjudication profile;
- require each selected profile to pass the same relevant quality gate;
- keep cross-provider automatic failover disabled;
- confirm UI and audit evidence show the requested model, routed provider, and cost;
- confirm retries do not bill or settle the same request twice.

### 6.4 Default profile recommendation

Until school data proves otherwise:

- template generation: higher-accuracy validated profile, because setup is infrequent and errors propagate;
- name transcription: lowest-cost profile that meets auto-assignment precision;
- initial objective/short-answer grading: the explicitly active, evaluated standard-request profile; Gemini 3.5 Flash Lite remains the default;
- ambiguous answer adjudication: the best validated model/profile, possibly through OpenRouter;
- all tasks use the same durable queue semantics; teachers are not asked to choose Batch, economy, priority, or expedite modes.

## 7. Scanner and upload setup

Ooki Grader does not control scanner drivers in v1. Technician configures scanners to produce:

- PDF preferred for multi-page tests;
- JPEG/PNG/TIFF accepted;
- 300 dpi recommended;
- color or grayscale according to handwriting quality tests;
- automatic orientation only if reliable;
- no aggressive despeckle/threshold that removes pencil;
- one student paper per file when possible;
- maximum 250 MB local upload.

Recommended operational naming can include session/date but identity is not trusted from filename.

Test at commissioning:

- pencil, pen, eraser marks;
- skewed pages;
- duplex blank backs;
- page order;
- faint Kanji;
- small furigana;
- 30-file peer upload;
- interrupted upload/resume.

## 8. Scheduled operations

| Local time | Operation | Concurrency/impact |
|---|---|---|
| every minute | job lease recovery/dispatch | low |
| every 5 minutes | provider connectivity and queued dispatch | synthetic probe only when breaker needs it |
| hourly | abandoned temp cleanup, byte-counter reconcile sample | low |
| 02:00 | metadata backup | SQLite online backup |
| 03:00 | age/quota retention | bounded manifests |
| 03:30 | orphan/file-intent reconcile | bounded |
| Sunday 04:00 | database integrity/full file sample | maintenance-aware |
| Monthly first Sunday | log/archive cleanup, certificate/update review | admin report |
| Quarterly | restore drill reminder | manual verified drill |

Jobs pause or reduce concurrency when school-hours load or disk pressure crosses thresholds.

## 9. Health model

### 9.1 Liveness

`/health/live` answers only whether the process event loop is alive. It does not touch provider or large disk operations.

### 9.2 Readiness

`/health/ready` requires:

- database opens and schema matches;
- data root accessible and writable;
- no active restore/migration;
- physical free reserve intact;
- certificate usable;
- host able to serve authenticated local work.

Provider outage does not make the whole app unready; AI status becomes degraded.

### 9.3 Component states

Each component reports `healthy`, `degraded`, `unhealthy`, or `unknown` with last checked time and actionable code:

- database;
- file store;
- physical/managed storage;
- background workers;
- official Gemini connection/profile;
- OpenRouter connection/profile;
- direct Gemini batches;
- backup;
- certificate;
- clock;
- update status.

## 10. Observability

Local metrics:

- HTTP request count/duration/status by route template;
- active sessions;
- upload bytes/rate/failures/resumes;
- preprocessing queue duration/failures/quality categories;
- AI queue wait, provider latency, retries, 429/5xx, schema failures;
- direct Gemini batch size/turnaround/failure/reconciliation;
- OpenRouter concurrency/route/model/actual cost;
- model/profile grading confidence and teacher correction rate;
- managed/physical bytes and deletion rate;
- database size/write wait/checkpoint/integrity;
- export duration/failure/page count;
- backup age/duration/verification.

No external monitoring service is required. Metrics may be stored locally with 90-day aggregation. Optional remote support monitoring requires separate configuration.

## 11. Alert thresholds

| Alert | Warning | Critical |
|---|---|---|
| Managed scans | 135 GiB | 150 GiB or unable to clean |
| Physical free | <15 GiB | <5 GiB reserve |
| Backup age | >26 hours | >72 hours |
| Certificate | <60 days | <14 days |
| DB integrity | n/a | any failure |
| AI authentication | first failure | persistent/invalid key |
| AI credits/budget | 80% | hard stop/402 |
| Direct batch | >12 hours | >24 hours/reconcile required |
| OpenRouter queue oldest | >15 min target | >60 min or breaker open |
| Preprocess failures | >5% recent | >20% or worker crash loop |
| Name auto precision monitor | below validated lower bound | auto-assignment disabled |
| Grade correction monitor | significant drift | profile disabled/rollback |

Critical accuracy drift takes precedence over throughput or cost.

## 12. Backup operations

### 12.1 Daily

1. confirm destination;
2. create database online snapshot;
3. capture config/template/report manifest;
4. copy changed objects;
5. verify hashes;
6. encrypt/finalize;
7. record success;
8. remove expired backup sets.

Default schedule:

- daily for 14 days;
- weekly for 8 weeks;
- monthly according to school record policy;
- managed scan payload excluded unless explicitly enabled for short rolling backup.

### 12.2 Restore drill

On isolated test host or alternate directory:

- install compatible build;
- restore latest backup;
- re-enter provider keys;
- log in;
- open roster/templates/results;
- generate one report;
- run integrity and counts;
- document recovery time and exceptions;
- securely remove drill copy.

## 13. Upgrade and rollback

### 13.1 Pre-upgrade

- read release notes;
- verify OS/hardware;
- enter maintenance mode;
- drain/cancel safe local work;
- do not cancel a remote Gemini batch solely for upgrade;
- create verified backup;
- record current app/schema/profile versions.

### 13.2 Upgrade

- stage signed binaries;
- run compatibility precheck;
- stop service;
- migrate schema;
- switch version;
- start read-only health;
- reconcile leases/file intents/provider jobs;
- smoke test;
- leave maintenance.

### 13.3 Rollback

If schema is backward compatible, switch to previous app. If not, restore the pre-upgrade backup and object manifest. Never run an older binary against an unsupported newer schema.

Provider model/prompt rollback is independent: activate prior approved task-profile revision for new work, and leave existing result provenance intact.

## 14. Runbooks

### RB-01 — Host unavailable

1. Check power/UPS/network.
2. Confirm Windows boot and time.
3. Check `OokiGrader.Host` service and Event Log.
4. Run host-local health CLI.
5. If crash loop, enter safe/maintenance mode.
6. Preserve logs and latest data.
7. Repair binary or restore per evidence.
8. Verify database/files before peer traffic.

### RB-02 — Disk pressure

1. Stop new uploads automatically.
2. View physical vs managed categories.
3. Run/reconcile retention.
4. Remove only application-tracked temp/orphan items through app.
5. Do not manually delete database/object files.
6. Move data root or expand disk if non-managed classes caused pressure.
7. verify counters and resume.

### RB-03 — Official Gemini key/model failure

1. Pause affected profile.
2. Run synthetic connection test.
3. Check key status, billing, model lifecycle, quota.
4. Replace key/model only through versioned profile.
5. Run validation sample.
6. Activate or use already-approved fallback.
7. resume at low concurrency.

### RB-04 — Direct Gemini batch ambiguous

1. Do not click generic retry or create a new batch.
2. Run provider reconcile by unique display name/time.
3. Adopt exactly one remote operation if matched.
4. If none after window, approve one resubmit.
5. If multiple, stop and investigate duplicate billing; apply only one response per local request hash.

### RB-05 — OpenRouter 402/429/503

1. Inspect credits/key limit, `Retry-After`, route requirements, endpoint availability.
2. Keep requests queued.
3. Reduce concurrency for 429/503.
4. Add credits or adjust school-approved guardrail for 402.
5. Use validated fallback only if enabled.
6. Confirm cost/actual route before full resume.

### RB-06 — Accuracy regression

1. Disable auto-finalization and auto-name assignment for affected profile.
2. Pause new AI work if false positives are material.
3. Identify model/routing/prompt/preprocess change.
4. Re-run golden set.
5. Roll back profile or raise review threshold.
6. identify and re-review affected results by provenance.
7. correct and record incident.

### RB-07 — Database integrity failure

1. Enter maintenance and stop writes.
2. preserve current files/database/WAL.
3. run read-only diagnostics on copy.
4. choose repair only with database specialist or restore verified backup.
5. reconcile object store.
6. validate counts/totals/audit.
7. resume only after sign-off.

### RB-08 — Certificate expired/name changed

1. use host-local repair console;
2. issue/import correct SAN certificate;
3. bind and verify;
4. distribute CA if changed;
5. test one peer without warning;
6. retire old private key/certificate.

### RB-09 — Backup failed

1. inspect destination/power/capacity/credentials;
2. do not delete last good backup;
3. re-run;
4. verify new manifest;
5. escalate if age exceeds critical threshold.

## 15. Diagnostic bundle

Default contents:

- application/OS versions;
- sanitized configuration;
- health and queue summaries;
- migration/integrity status;
- recent structured logs with redaction;
- storage/backup/certificate metadata;
- provider status/error codes, operation IDs, model/profile versions;
- no keys, sessions, names, answers, images, full prompts, or PDFs.

Bundle is encrypted or stored in an administrator-selected protected location and expires locally after seven days.

## 16. Commissioning acceptance checklist

- [ ] Host meets capacity and has stable LAN identity.
- [ ] Data root is NTFS, protected, and not shared.
- [ ] Service starts after reboot without login.
- [ ] Peer certificate/DNS/browser access works.
- [ ] First administrator created; bootstrap disabled.
- [ ] Roles tested from peer.
- [ ] 30-file upload/resume/duplicate test passes.
- [ ] Model-answer-containing source generates correct answer provenance.
- [ ] Official Gemini connection passes if configured.
- [ ] OpenRouter connection passes if configured.
- [ ] Active task profiles have recorded validation.
- [ ] One end-to-end sample is finalized and exported in Japanese.
- [ ] 150 GiB quota/warning settings confirmed.
- [ ] Simulated old scan deletion preserves result.
- [ ] Daily backup completes and verifies.
- [ ] Restore drill plan assigned.
- [ ] Provider budgets/credits and school contacts documented.
- [ ] Technician hands over quick guide and recovery contact.

## 17. School quick-start operation

Ordinary teachers need only:

1. sign in;
2. add/import students;
3. create a grading key from blank/model-answer sources and publish it;
4. create/open a test session;
5. upload papers;
6. resolve highlighted name/grade items;
7. finalize;
8. view progress or export PDF.

Provider, job, storage, backup, and certificate details remain in administrator screens unless action is required.
