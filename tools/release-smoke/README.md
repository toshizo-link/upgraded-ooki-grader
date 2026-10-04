# Published Windows upgrade smoke

`published_upgrade_smoke.py` starts the published host executable on an unused
loopback port, with all data under a separate `isolated-upgrade-*` directory.
It never installs a service or changes the installed application's data.

The `prepare` phase starts version 0.9.14 and creates a synthetic teacher,
student, manual template, finalized result, original PDF, and verified result
PDF. It saves a Gemini connection with the old model and a disabled synthetic
School Manager connection. No School Manager messages are sent.

The `finish` phase starts version 0.9.17 against the same data directory. It
checks the original entities and PDF hashes, encrypted credential files, the
stored API-key fingerprint/reference/revision, all four task models, and the
one-time migration record. It also restarts the new host to check that the
migration does not repeat. The transition includes a real Gemini capability
probe; an optional client PDF uses the actual production template worker.

All four profiles select the preferred connection model. The production
adapter routes identity-only work to 3.5 Flash-Lite and quota-rejected important
work to Lite, recording the actual model separately. Repeated explicit HTTP
503 responses also fall back after three primary attempts. The 0.9.17 probe checks
Lite readiness as well as preferred-model readiness (or quota cooldown).

The key must be supplied to `prepare` in the process-only environment variable
`OOKI_GEMINI_API_KEY`. Do not put keys in command arguments, source files, or
logs. The application stores the key using its encrypted secret store. Clear
the environment variable immediately after execution. Keep the isolated data
and evidence outside the repository.

```powershell
python published_upgrade_smoke.py prepare `
  --package '<published 0.9.14 folder>' `
  --work '<private parent folder>\isolated-upgrade-release'

python published_upgrade_smoke.py finish `
  --package '<published 0.9.17 folder>' `
  --work '<private parent folder>\isolated-upgrade-release' `
  --client-pdf '<authorized source PDF>' `
  --expected-question-count 62 `
  --grading-fixtures '<synthetic Japanese test PDFs folder>'
```

Use `--expected-question-count` only with an independently counted expected
number of answer slots. The live acceptance also requires zero blocking
extraction review issues. `--recheck` permits one further real capability probe
if the startup probe did not activate the connection. Obtain working provider
quota before running the live phases.

`--grading-fixtures` enables the real initial-grading worker and imports the
synthetic correct, incorrect and empty completed-test PDFs. It verifies three
scores per PDF and records the dispatched model. School Manager remains disabled.

This smoke verifies the published application's data migration and production
worker. It does not exercise the elevated updater, Windows service handoff, or
School Manager's live sending flow. Those checks require their own evidence.
