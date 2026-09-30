# Published Windows upgrade smoke

`published_upgrade_smoke.py` starts the published host executable on an unused
loopback port, with all data under a separate `isolated-upgrade-*` directory.
It never installs a service or changes the installed application's data.

The `prepare` phase starts version 0.9.14 and creates a synthetic teacher,
student, manual template, finalized result, original PDF, and verified result
PDF. It saves a Gemini connection with the old model and a disabled synthetic
School Manager connection. No School Manager messages are sent.

The `finish` phase starts version 0.9.15 against the same data directory. It
checks the original entities and PDF hashes, encrypted credential files, the
stored API-key fingerprint/reference/revision, all four task models, and the
one-time migration record. It also restarts the new host to check that the
migration does not repeat. The transition includes a real Gemini capability
probe; an optional client PDF uses the actual production template worker.

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
  --package '<published 0.9.15 folder>' `
  --work '<private parent folder>\isolated-upgrade-release' `
  --client-pdf '<authorized source PDF>' `
  --expected-question-count 62
```

Use `--expected-question-count` only with an independently counted expected
number of answer slots. The live acceptance also requires zero blocking
extraction review issues. `--recheck` permits one further real capability probe
if the startup probe did not activate the connection. Obtain working provider
quota before running the live phases.

This smoke verifies the published application's data migration and production
worker. It does not exercise the elevated updater, Windows service handoff, or
School Manager's live sending flow. Those checks require their own evidence.
