# Ooki Grader manual screenshot manifest

Captured on 2026-08-10 and 2026-08-11 from the real React/ASP.NET Core application at a
desktop viewport of 1440 × 1050 unless noted otherwise. All student names,
numbers, test dates, and test content shown here are fictional manual/demo
data. No API keys, passwords, bootstrap tokens, or other secrets appear in
these images.

## First use, students, and template creation

| File | Manual task shown |
| --- | --- |
| `00-first-admin-bootstrap.png` | Current first-run, host-only administrator setup screen; fields intentionally empty. |
| `01-login.png` | Staff login. |
| `02-dashboard.png` | Signed-in dashboard and navigation. |
| `03-students-empty.png` | Empty student ledger. |
| `04-student-add-filled.png` | Adding fictional student 桜井 花子. |
| `05-student-detail.png` | Student detail and lifecycle controls. |
| `06-students-list.png` | Student list after registration. |
| `07-templates-filtered.png` | Filtering the template list. |
| `08-template-create-settings-empty.png` | New-template settings before required selections. |
| `09-template-create-settings-hop.png` | HOP and subject selected before PDF upload. |
| `10-template-upload-plan.png` | Uploaded PDF and deterministic HOP one-page split plan. |
| `11-template-grading-flags-published.png` | Published template grading settings (read-only reference). |
| `12-template-grading-flags-all-checked.png` | Editable draft with 完答, 順不同, and 漢字必須 enabled. |
| `40-template-deterministic-names.png` | Current real-app completion screen for a 72-page STEP batch, showing 36 immutable subject/grade/set/variation names (1440 × 1100; no student data). |
| `49-template-ai-defaults.png` | Current grading controls with the one-action `すべての問題を確認` flow, 1-point question value, and `AIで判定（おすすめ）` selected (1744 × 1027). The historical top toolbar is outside the guide crop. |
| `50-template-ai-defaults-details.png` | Current advanced grading controls showing a 1-point partial-credit increment and the optional 完答／順不同／漢字必須 controls (1744 × 1027). The guide crop omits the historical top toolbar. |
| `51-template-review-default-off.png` | Current advanced grading controls showing `採点後に必ず先生が確認する` off by default (1744 × 1027). The historical top toolbar is outside the guide crop. |

## Template archive and restore

| File | Manual task shown |
| --- | --- |
| `13-template-before-archive.png` | Template selected before archiving. |
| `14-template-archive-confirmation.png` | Archive confirmation and preserved-history explanation. |
| `15-template-archived-filter.png` | Archived filter and restore action. |
| `16-template-restore-confirmation.png` | Restore confirmation. |

## Test session, ordered scans, and name review

| File | Manual task shown |
| --- | --- |
| `17-sessions-empty-open.png` | Open-session list before creating a session. |
| `18-session-create-filled.png` | Current TemplateEditor `受付を開始` modal: canonical test name, subject, grade, category, and course are fixed; only the test date and optional target class are editable (1744 × 1027). The footer action was not submitted. |
| `19-session-ordered-scan-empty.png` | Ordered one-page scan intake before selecting files. |
| `20-ordered-scan-grouping.png` | Two selected pages grouped as two one-page HOP submissions (full page). |
| `21-ordered-scan-pages-received.png` | Frozen order after all pages reached the host (full page). |
| `22-ordered-scan-completed.png` | Server-validated page roles, completed batch, and created submissions (full page). |
| `23-name-review-unassigned.png` | Name-review screen before a manual match. |
| `24-name-review-student-selected.png` | Fictional student selected and ready to assign. |

## Administration

| File | Manual task shown |
| --- | --- |
| `25-admin-system-health.png` | System health, action items, AI status, and backup readiness (full page). |
| `26-admin-ai-settings.png` | Gemini connection status, usage, and active task profiles (full page; no secret shown). |
| `27-admin-staff.png` | Staff account list and lifecycle operations. |
| `28-admin-storage.png` | Managed scan quota, physical disk, retention, and category usage (full page). |
| `29-admin-jobs.png` | Background jobs requiring administrator attention. |
| `39-admin-ai-add-connection.png` | Empty Gemini API-key setup dialog; no credential is present. |
| `41-admin-ai-one-step.png` | Current one-step Gemini administration after a successful stored-key recheck: no evaluation/pilot/manual-activation controls, and all four AI functions show `利用できます` (1265 × 1216; isolated copied database, no secret or credential mutation). |

## Grading, finalization, results, and PDF reports

| File | Manual task shown |
| --- | --- |
| `30-reports-empty.png` | Current empty report state before any result is finalized. |
| `31-grading-review.png` | Question-by-question grading-review queue; the historical source capture ends at the right viewport edge. |
| `32-grading-points-adjusted.png` | Teacher-adjusted points and review checklist. |
| `33-finalize-queue.png` | Answers ready for finalization. |
| `34-finalize-confirmation.png` | Finalization confirmation dialog. |
| `35-reports-finalized.png` | Empty report state from the historical demonstration; despite the retained filename, it does not show a finalized row. |
| `36-result-detail.png` | Final result with per-question outcomes. |
| `37-result-pdf-dialog.png` | PDF-generation checklist and action. |
| `38-result-pdf-ready.png` | Completed result PDF ready to download. |
| `52-submission-grading-workspace.png` | Current per-submission grading workspace for a fictional two-page STEP answer: the server-assembled PDF, both page thumbnails, all 32 question results, selected-question editor, exact test metadata, and per-submission bulk-confirm action (1744 × 1027). |
| `53-submission-grading-bulk-confirm.png` | Current exact-snapshot bulk-confirm dialog for the same fictional two-page STEP answer; no score, transcription, or confirmation was changed or submitted (1744 × 1027). |

## Robust lists and bulk result export

These captures use a 1440 × 1000 viewport and a copied, isolated database. The
additional students, sessions, templates, and scores are fictional. The ZIP was
rendered by the real background worker from the selected finalized results.

| File | Manual task shown |
| --- | --- |
| `40-reports-filter-sort.png` | Report search with an exact subject filter, active-filter summary, student-name sort, result count, and page-size control. |
| `41-reports-selected.png` | Two exact finalized-result rows selected for export, alongside the alternative all-filtered-results action. |
| `42-reports-bulk-preview.png` | Selected-row export preview with exact student/result counts and required acknowledgment. |
| `44-reports-bulk-ready.png` | Verified two-result ZIP ready for download after the real worker rendered both Japanese PDFs and the CSV manifest. |
| `45-reports-filtered-preview.png` | Filter-mode preview confirming that all four matching results—not only the visible/selected rows—will be exported. |
| `46-students-filter-sort.png` | Student list filtered by class and course, sorted by name descending, with active-filter chips and paging. |
| `47-templates-filter-sort.png` | Template list filtered to active Japanese-language templates and sorted by title. |
| `48-sessions-filter-sort.png` | Session list using state/class filters and name sorting. |

Files `00`–`17`, `19`–`30`, and `40`–`48` are fresh captures from isolated
2026-08-10 runtimes. Files `18` and `49`–`51` were captured on 2026-08-11 from
isolated online backups of the local application database. For file `18`, only
the isolated account and fictional display-only course were adjusted; the modal
was opened and a fictional class was typed, but `受付を開始` was never submitted.
The source and isolated databases retained the same session count. No live
template, session, credential, or publication state was changed.
Files `52`–`53` were captured on 2026-08-11 from another isolated online backup
after opening the new grading workspace. The isolated account password alone
was reset for login. The STEP PDF and grading data were read only; the bulk
confirmation dialog was not acknowledged or submitted. The live database and
live application process were not modified for either capture.
During that run, the real ordered-scan flow completed and manual name assignment
succeeded, but the subsequent initial-grading worker stopped with
`ai_initial_grading_worker_error` (`KeyNotFoundException`). To avoid changing or
exposing the saved Gemini credential merely for screenshots, files `31`–`39`
were re-encoded as PNG from the repository's previously captured, visually
verified real-app demonstration using the same fictional students and exam.

## Contact sheets

- `contact-sheet-01-onboarding-template.png`
- `contact-sheet-02-flags-archive.png`
- `contact-sheet-03-session-review.png`
- `contact-sheet-04-admin-results.png`
- `contact-sheet-05-lists-bulk-export.png`
- `contact-sheet-06-grading-workspace.png`

Each individual screenshot and all six contact sheets were visually inspected
for clipping, unexpected overlays, accidental secrets, and non-fictional names.
