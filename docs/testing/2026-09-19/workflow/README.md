# Real-life workflow guide and expanded test evidence

19 September 2026, Asia/Tokyo. Source tested: `ddb6836b56dd502a92540ec5937ad645d972867e`; installed application: **0.9.8**. The newer fixed package was not successfully installed during this run.

[Download the 134-page PDF guide](../../../../output/pdf/OokiGrader-Real-Life-Workflow-and-Test-Guide-2026-09-19.pdf?raw=true) · [Download original evidence ZIP](https://media.githubusercontent.com/media/toshizo-link/upgraded-ooki-grader/main/docs/testing/2026-09-19/workflow/OokiGrader-Workflow-Evidence.zip) · [Machine-readable summary](RESULTS.json)

The guide contains 50 scenario-based chapters, 64 original application screenshots and 65 annotated screenshot pages. Each chapter explains the situation, steps, expected outcome, recovery and actual test boundary. All 134 pages were rendered with Poppler and visually reviewed. Screenshot annotations are overlays on real captures; no unavailable feature was illustrated with a fabricated screen.

## Executed results

| Check | Result |
|---|---|
| Fresh .NET Release solution run | 1,078 passed; 0 failed; 7 skipped |
| Fresh frontend suite | 178 passed across 26 files |
| Total automated passes | **1,256** |
| Additional installed-app API assertions | **38 passed**: 8 guardrails, 20 account/permission checks, 10 lifecycle checks |
| Frontend production build | Passed; existing 570.20 kB chunk warning remains |
| Generated OpenAPI contract check | Passed |
| PDF/ZIP validation | Generated PDFs parsed; bulk ZIP CRC and manifest checked |
| Retained baseline PDF | Original SHA-256 still matches; this is not a completed update comparison |

Live browser work covered dashboard navigation, all student-detail tabs, search and class filters, student creation and alias management, deactivation/reactivation, template/source inspection, reception creation, manual identity matching, teacher grading, finalization, reopening, bulk confirmation, correction of a wrong pupil, PDF creation, bulk export, reception closing, progress charts, staff/settings/storage/jobs inspection, and expired-session/login-throttling behavior. Roster import, fixture upload and some lifecycle mutations used the authenticated application API; the guide identifies these boundaries. A browser file-chooser timeout prevented the ordinary UI upload/import path from being accepted as passed.

The controlled practice paper intentionally passed through a wrong-pupil assignment, then was corrected from Bob to the Alice identity printed on the scan. The final score is 20/30. `practice-result.pdf` is an **obsolete intermediate Bob report** retained solely as evidence; `practice-result-corrected.pdf` is the regenerated Alice report. No attachment or message was sent to School Manager.

## Reproduced defects

1. **Student form loses the class.** WF-104 was created with Workflow A; after save the class is absent in both UI and API. The frontend uses `classLabel` while the request contract expects `schoolClass`. The CSV path preserved schoolClass and class filtering worked. This defect was not fixed in this reporting run.
2. **Misleading kanji-rule badge.** English questions with API value `kanjiRuleOutcome = not_applicable` show the applied-rule badge because the UI tests a nonempty string. The score remains 20/30. This defect was not fixed in this reporting run.

## Open acceptance gaps

- **All four live AI workflows remain unverified:** template extraction, name reading, initial grading and adjudication. Installed Gemini 3.8 Flash capability checking reports `gemini_request_invalid`; all four profiles are inactive. Thinking-payload regression tests pass in the newer source, but that does not establish a live provider success.
- **Update and populated-data retention remain incomplete.** The elevated updater launch was canceled by Windows/UAC. Automatic approval review rejected both separate test-instance launch attempts with only `blocked by policy`; no alternate launch bypass was attempted. The existing baseline remains intact on installed 0.9.8.
- **School Manager and the class matrix are unavailable on installed 0.9.8.** Their purported API reads return HTTP 200 `text/html` (SPA fallback), not JSON. These are not successful integration checks. No dedicated School Manager dummy guardian was supplied and no live send/receipt was verified. Automated integration tests simulate the external client.
- **Classmate labels are currently anonymized** as `同級生01`, `同級生02`, etc.; the recipient is `本人`. Peers are not shown by student number. The user's requested student-number preference has not been implemented. The internal teacher-facing named matrix is a separate feature.
- Fresh-machine install, full backup/restore, rollback, uninstall, reboot recovery, separate-device access, physical storage exhaustion, destructive cleanup, large live class exports and multi-page live AI processing were not fully exercised. The guide documents their procedure and clearly labels this boundary.

## Evidence integrity and reproducibility

The ZIP contains original screenshots/DOM snapshots, nine TRX files, the backend test-case inventory, console logs, synthetic roster fixtures, API observations, assertion summaries, generated sample PDFs and a checked bulk ZIP. `SHA256SUMS.txt` inside the ZIP lists every packaged file. Credential readers, cookies, DPAPI files and local authentication helpers are excluded. All people and class records used in the screenshots are synthetic.

The initial guardrail expectation (409 for revision 0) was corrected to the contract's 428; a separate stale revision assertion returned 412. An initial lifecycle harness used the wrong response field/ETag format, then inadvertently retained a stale If-Match header in PowerShell's web session. These harness attempts remain in the evidence and are distinguished from product defects. Corrected assertions passed. Archive ETags use the quoted `rev-N` form.

At completion the synthetic staff account is disabled, the unused test template is archived, maintenance is off, and the practice reception is closed with its finalized Alice result intact. The original retention fixture was preserved. Repeated account tests reached the login rate limit; its visible rejection and session-expiry recovery are documented without circumventing the limiter. A single retry after the cooldown succeeded at 04:02 JST; the administrator browser session is restored. The final DOM observation is included as `login-cooldown-recovery.txt` (recorded after PDF generation).

To rebuild the PDF on Windows, install Python dependencies `reportlab`, `pypdf` and `Pillow`, extract the evidence ZIP, set `OOKI_WORKFLOW_EVIDENCE` to that extracted directory, then run `output/pdf/author_workflow_content.py` and `output/pdf/build_workflow_guide.py`. The builder uses Windows Arial and Meiryo fonts, validates suite totals and records page/layout checks. Regeneration changes PDF metadata and therefore its hash. Original console commands appear in the guide and logs; reproducing authenticated live tests requires a separately provisioned private test account.

The PDF is a detailed field guide and evidence record, **not certification that every feature passed**. Full release acceptance requires correcting the two UI defects, completing the populated upgrade, proving each live AI workflow and verifying School Manager receipt with a controlled dummy account.
