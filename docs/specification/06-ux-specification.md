# UX and interaction specification

## 1. UX goals

The interface is optimized for staff who grade many papers in short sessions. It should feel like a dependable school tool rather than an AI demo.

Priorities:

1. make the next required action obvious;
2. keep student identity, paper, rubric, and score context visible during review;
3. make uncertainty and waiting states precise;
4. prevent accidental publication/finalization/deletion;
5. support keyboard-heavy repetitive work;
6. use natural Japanese labels and Japanese date/score conventions;
7. keep destructive/system complexity out of ordinary teacher paths.

Default UI language is Japanese. English localization may exist for technician/support screens, but Japanese is release-blocking.

## 2. Information architecture

Primary navigation:

1. **ダッシュボード** — Dashboard
2. **採点待ち・確認** — Queue and review
3. **テスト実施** — Test sessions and uploads
4. **テストひな形** — Blank tests/templates
5. **生徒** — Roster and individual progress
6. **帳票** — Reports/exports
7. **管理** — Administration, visible by role

Persistent header:

- one `Ooki Grader` product label; school identity belongs in reports and
  administration rather than a permanent card below the product name;
- an environment badge only when the environment is not production;
- host connectivity;
- queue summary;
- storage warning when relevant;
- current staff menu/session;
- no API/model branding in ordinary grading screens.

## 3. Global interaction patterns

### 3.1 Status language

Use specific Japanese labels:

| Internal state | Primary label | Supporting text |
|---|---|---|
| `uploading` | アップロード中 | Closing the browser can interrupt only the current unsent chunk. |
| `preprocessing` | 画像を準備中 | Orientation, page order, and answer areas are being checked locally. |
| `template_planning` | PDFを分割しています | Page ranges are being planned locally from the selected test type. |
| `template_generating` | テンプレートを生成しています | Shows completed units against the deterministic total. |
| `template_rotating` | ページの向きを補正しています | A derived copy is being rotated locally; the original is unchanged. |
| `template_final_check` | 最終確認を準備しています | All units succeeded; grade evidence is being reconciled before deterministic names are computed. |
| `awaiting_ai` | AI処理待ち | The paper is safe on the host and waiting to be submitted. |
| `legacy_gemini_batch_running` | 旧一括処理を確認中 | Administrator-only recovery status for historical jobs; never a new-work choice. |
| `openrouter_queued` | OpenRouter採点待ち | Requests are being processed in the host queue. |
| `budget_blocked` | AI利用上限で保留 | Local data is safe; an administrator must adjust the limit. |
| `needs_name_review` | 生徒名の確認が必要 | No student has been assigned automatically. |
| `needs_grade_review` | 採点の確認が必要 | One or more answers require a teacher. |
| `ready_to_finalize` | 確定できます | Required checks are complete. |
| `finalized` | 確定済み | Included in progress and reports. |
| `scan_deleted` | 画像削除済み | Grade data remains; scan deleted by retention. |

Do not use “AI is thinking,” “magic,” or an indefinite spinner without a durable status.

### 3.2 Saving

- Simple forms use explicit `保存`.
- Template creation is settings-first: test type and subject are required before
  the upload control appears; `その他` also requires answer style. Upload then
  creates a visible, host-owned deterministic plan before generation.
- Large template editing autosaves a local draft to the server after a short idle delay and shows `保存済み` with time.
- Generated template review defaults to unresolved exceptions. Safe proposals
  can be confirmed together; the full question list remains available on
  demand.
- Leaving with uncommitted client changes prompts once.
- A stale edit shows a conflict comparison and never overwrites another teacher automatically.
- Actions with background work return immediately to a durable status page.

### 3.3 Confirmation levels

- No confirmation: reversible navigation/filter changes.
- Lightweight confirmation: close session, retry job, regenerate report.
- Typed/re-authenticated confirmation: retention-policy reduction, restore, permanent student erasure, API key replacement if policy requires.
- Reason required: grade override, reopen, void, student reassignment after finalization.

### 3.4 Notifications

- Inline messages next to the affected field are primary.
- Toasts are only supplemental and never the sole error.
- Persistent system warnings use banners with owner/action.
- Completed long jobs update via SSE and appear in an activity tray.
- Browser notifications are off by default and contain no student names.

### 3.5 Search, filters, sorting, and pages

Student, template, test-session, and report lists share one discoverable
pattern:

- one debounced tolerant search field;
- a relevant filter panel populated from bounded server facets, not merely the
  visible page;
- a separate allowlisted sort field and ascending/descending control;
- an `現在の絞り込み` summary and one `絞り込みをすべて解除` action;
- exact/approximate result count, page-size selector (25/50/100/200), and
  keyboard-accessible previous/next controls;
- loading skeleton, retryable error, and filter-aware empty state.

Search, filters, sorting, page size, current page, and cursor are reflected in
the URL and survive reload. Changing any membership or sort criterion returns
to page one and clears stale selection. Unknown/tampered URL values are removed
before a request. The host remains authoritative and returns a typed inline
error for an invalid or stale cursor.

## 4. Dashboard

### 4.1 Teacher dashboard

Cards:

- papers needing name confirmation;
- questions/papers needing grade review;
- ready to finalize;
- durable AI work in progress/delayed;
- today's finalized papers.

Lists:

- recent open test sessions;
- recent activity;
- system warning summary if teacher action is relevant.

Primary action: `テスト答案をアップロード`.

### 4.2 Administrator additions

- scan quota gauge: managed used / 150 GiB, warning/target/hard markers;
- physical free disk;
- last successful metadata backup;
- AI daily/monthly estimated spend vs budget;
- provider/model health;
- failed/reconcile-required jobs;
- certificate expiry.

The quota gauge separates managed scans from templates/reports/logs to explain why Windows disk use may differ.

## 5. Student roster UX

### 5.1 List

Columns:

- student number;
- name;
- kana;
- grade/class/course;
- active state;
- last finalized test date.

Controls:

- tolerant search;
- active/inactive filter;
- grade/class/course filters;
- student-number, name, or update-time sorting in either direction;
- `生徒を追加`;
- `CSVから取り込む`.

Potential alias collisions display a warning icon but do not expose another student's private notes.

### 5.2 Student detail

Tabs:

- **基本情報**
- **別名・表記**
- **学習推移**
- **テスト結果**
- **変更履歴** (role-dependent)

The progress tab defaults to the last three months, with explicit inclusive date fields and presets: 1 month, 3 months, 6 months, school year, all.

### 5.3 CSV import wizard

Steps:

1. select file and encoding auto-detection;
2. map columns;
3. preview normalization/duplicates;
4. choose create/update/skip strategy;
5. validate;
6. apply once;
7. show summary and downloadable errors.

The Apply button states exact counts, e.g. `128名を追加、14名を更新`.

## 6. Template creation and editor

### 6.1 Settings-first deterministic creation

The new creation route is a four-step workflow. Test settings appear before
every file picker and drag-and-drop surface.

1. **テスト設定:** require `試験タイプ` and `教科`. Test type choices are
   `HOP`, `STEP`, `クラス分けテスト`, and `その他`; subjects are `算数`,
   `国語`, `理科`, and `社会`. Only `その他` reveals a required `問題形式`
   choice of `通常` or `穴埋め`.
2. **アップロード:** after settings are valid, accept one PDF and show its
   verified page count. HOP accepts any positive count. STEP blocks immediately
   unless the count is divisible by six. Class-placement and Other keep the
   whole PDF. Grade is not a required intake field.
3. **作成予定:** show the immutable local plan. HOP says
   `1ページごとに分割し、N件のテンプレートを生成します。`; STEP says
   `2ページごとに分割し、3件を1セットとして -1 / -2 / -3 を付けます。`;
   unsplit types say `PDF全体から1件のテンプレートを生成します。` The plan
   is informational, not an editable page-boundary tool.
4. **生成:** queue the durable batch and navigate to progress, then final check.

The teacher selection is authoritative. The UI does not show test-type
auto-detection, source-role routing, an initial required grade, orientation,
split, cover-page, variation, Batch/economy/priority, or legacy generation-mode
controls. Changing type after upload invalidates the current plan and requires
replanning before generation. The old upload-first branches are removed rather
than hidden behind a mode toggle.

HOP creates one unrelated candidate per page. STEP creates one unrelated
candidate per two pages and fixed variations `-1`, `-2`, `-3` inside each
six-page set. Suffixes reset for the next set and cannot be edited. The UI never
suggests that three STEP variations share questions, answers, publication, or
results.

### 6.1.1 Progress and final check

The durable progress page uses concrete Japanese states such as
`PDFを分割しています`, `テンプレート 3 / 12 を生成しています`,
`ページの向きを補正しています`, `補正後のテンプレートを生成しています`, and
`最終確認を準備しています`. It never displays a fake AI classification phase.
Closing the browser does not discard the uploaded source or queued work.

When every unit succeeds, the final-check page shows the selected settings,
source filename, page/unit count, and for each unit:

- page or page range, STEP set and fixed suffix;
- AI-transcribed printed title as provenance/reference and the final name;
- filename grade, paper grade, resolved grade, and evidence conflict;
- extracted question count and unit status;
- local orientation-correction summary;
- blocking warnings and current row version.

Grade evidence must be resolved first. Missing grade uses the message
`学年がファイル名またはテスト用紙から確認できませんでした。学年を選択してください。`
with `1年生` through `6年生`; conflicting filename/paper evidence remains
visible until the teacher explicitly chooses. A safe missing-grade bulk action
may apply one selected grade to unresolved units with row-version checks.

After grade resolution, the host displays these exact final names:

- HOP: `{subject}{grade}年HOP{unitSequence}` (for example,
  `理科6年HOP1`);
- STEP: `{subject}{grade}年STEPセット{set}-{variation}` (for example,
  `理科6年STEPセット2-1`);
- ClassPlacement: `{subject}{grade}年クラス分けテスト` (for example,
  `理科6年クラス分けテスト`).

Those known-type names are read-only. The AI-transcribed printed title remains
visible only to explain what the extraction saw and does not alter a final
name. Only Other exposes an editable final-name field, initially proposed from
the printed title when safe.

`確認してテンプレートを作成` remains disabled while a unit failed, a grade is
unresolved, an Other name is missing or duplicated, a blocking warning exists,
or a row-version conflict requires reload. Confirmation is
all-or-nothing and idempotent: it creates independent draft templates, then
hands each to the existing editor. A unit that exhausted its one orientation
retry cannot be retried into a third automatic AI call; the teacher must correct
or re-upload the source.

### 6.2 Editor layout

On 1440 px or wider:

```text
┌──────────────────────────────────────────────────────────────┐
│ Template title | v3 draft | saved | Validate | Publish       │
├─────────────┬──────────────────────────┬─────────────────────┤
│ Questions   │ Complete source page     │ Selected question   │
│ Q1          │ visual reference only    │ text/type/points     │
│ Q2 warning  │ no boxes or coordinates  │ answers/rubric       │
│ Q3          │                          │ Kanji checkbox       │
└─────────────┴──────────────────────────┴─────────────────────┘
```

At narrower supported widths, the properties pane becomes a drawer. The editor is not optimized for phones.

### 6.3 Page preview

- the complete blank page is a read-only visual reference;
- page navigation and fit-to-width are available;
- no overlays, handles, coordinates, privacy masks, or crop controls appear;
- selecting a logical question does not require mapping it to pixels.

### 6.4 Question pane

Fields:

- label/order;
- question text;
- type;
- maximum points;
- grading mode;
- canonical answer;
- accepted equivalents;
- partial-credit rubric;
- `完答`;
- `順不同`;
- `漢字必須`;
- `常に先生の確認を必要とする`;
- teacher-only note.

Supporting copy:

- `完答`: `一部だけ正しい場合も0点にします。読取不明は確認待ちにします。`
- `順不同`: `「、」「／」「；」「・」または改行で区切った全項目を、重複も含めて照合します。`

When `漢字必須` is checked and the canonical answer contains Kanji, show:

> ひらがな・カタカナだけの同じ読みは不正解になります。個別に許可する読みは「漢字必須の例外（読み）」へ1行に1つ追加してください。

When it is unchecked:

> 漢字でなくても、登録した読み・採点基準に一致すれば正解にできます。

### 6.5 Template archive and restore

Each template card exposes `アーカイブ` for non-archived templates and
`復元` in the archived filter. The confirmation MUST state that published
versions, existing test sessions, submissions, grading results, and audit
history remain. Archived templates disappear from the normal list, are
read-only, and cannot be selected for a new session. This is intentionally not
worded as permanent erasure. If automatic draft extraction is still active,
the archive action explains that the teacher must wait for it to finish.

Closed test sessions expose `アーカイブ`, with copy stating that every answer
must first be finalized or voided and that uploads, ordered-scan batches, and
grading work must be finished. A readiness conflict remains actionable rather
than partially archiving the session. An archived session is excluded from
review/finalize queues, is read-only, and does not show the reopen action.
Students use deactivate/reactivate and staff accounts use disable/re-enable;
records referenced by grades or audit history do not receive destructive delete
controls.

### 6.6 Proposal review

AI-proposed fields have:

- proposal badge;
- confidence/warning in teacher-friendly wording;
- `採用`, `編集して採用`, `無視`;
- no raw model probability where it could imply calibrated certainty.

The initial question list highlights unresolved exceptions: low-confidence,
incomplete, conflicting, unsupported, or explicitly always-review items. A
revision-protected `すべての問題を確認` action acknowledges every complete
proposal atomically and reports every structural/global item it skipped; it
does not publish. A descriptive question is not exceptional merely because of
its type. Teacher verification remains available per question. The full
question list is one click away, and publish validation lists blocking issues
and navigates to each.

The main question form exposes one `採点方法` selector rather than requiring a
teacher to coordinate raw question-type and grading-mode fields. Its default is
`AIで判定（おすすめ）`; the other presets are exact/registered-variant,
numeric, choice, and teacher grading. Raw type/mode controls remain under
`詳細設定` for exceptional imported combinations. Loading, copying, or
importing an existing explicit choice does not silently rewrite it.

The default partial-credit increment is 1 point. `先生が必ず確認` is off by
default and can be enabled per question. A clear, valid, sufficiently confident
AI judgment can therefore proceed without opening that question individually;
the teacher still finalizes the paper as a whole. One primary
`すべての問題を確認` action confirms every complete proposal, reports the
confirmed/skipped counts, and navigates to the first structurally incomplete
question. It does not hide a missing answer, rubric, source, or global template
inconsistency.

An answer transcribed from a visible model answer uses a distinct
`模範解答から読取` badge and records its source page. An independently solved
proposal uses `AI提案`. If an independent solution and visible model answer
differ, both appear side by side under `解答の矛盾を確認` and publication is
blocked until the teacher selects or enters the authoritative answer.

### 6.7 Start reception

The final dialog shows:

- canonical test name, subject, grade, category, and course from the template;
- test date and optional target class;
- version number;
- page/question count;
- total points;
- unresolved non-blocking warnings;
- count of Kanji-required questions;
- count always requiring review;
- statement that starting reception fixes the version and opens the upload
  screen immediately.

Button: `受付を開始`.

The UI does not ask for an administration/session name, course, or processing
priority. For a draft, the action is an atomic make-immutable-and-open
operation. For an already immutable version, the same dialog creates another
open administration without republishing. On success it navigates directly to
the reception/upload page. Teacher-facing labels use `確定済み`/`利用中`
rather than exposing the internal `published` state.

## 7. Test session and upload UX

### 7.1 Session creation

Select an immutable template/version and enter only the test date plus an
optional target class. The screen displays the canonical template name,
subject, grade, category, and course as read-only metadata. It does not request
a session name, duplicate course, or priority. Creation opens reception in the
same idempotent request; there is no intermediate draft-session step in the
normal teacher workflow.

The server-owned current task profile is either an exact-current
`capability_passed` Gemini revision or a separately evaluated advanced
revision. One durable queue is used, and the teacher is not shown economy,
expedite, Batch, or provider-routing controls.

### 7.2 Upload board

The open session page is an ordered one-page PDF board. It reads the expected
pages per answer from the selected published template: HOP shows singletons,
each separately registered STEP variation/session shows pairs, and
class-placement/Other draws groups of the complete published page count up to
the supported 50-page ceiling. The UI never combines a six-page STEP source set
or multiple registered STEP variations into one student's answer.

Before upload, the board:

- natural-sorts scanner filenames as a convenience only;
- shows an explicit one-based read order and answer/page boundaries;
- labels page 1 as the page from which the student name is read;
- allows move, remove, and add operations;
- blocks freezing while the final answer group is incomplete; and
- makes `この順番でページを送信` the explicit order confirmation.

After freezing, parallel transfers retain their immutable client ID and input
ordinal. A browser reload restores the server manifest and can finalize a fully
uploaded draft without the original browser `File` objects. Failed local
transfers can resume with stable idempotency. `needsReview`, `failed`, and
expired batches are cancelled before the UI starts a replacement batch, so
staged scan references are released.

The board also supports:

- large drag/drop zone;
- multi-file selection;
- per-file progress, speed, resume state;
- host-returned local template-page role, duplicate, and order warnings;
- batch summary counts by state;
- “start queued processing now” for authorized users, with provider-specific wording;
- closing-session control.

Uploading 30 files does not open 30 dialogs. Problems collect in a filterable table.

The UI states the operational limitation directly: under the approved ordered
scan workflow, page 2 and later belong to the immediately preceding page 1. A
later page from another student cannot be detected if it is otherwise in the
correct template position and carries no identifier.

A many-page paper remains one answer in the teacher workflow. The host grades
its consecutive page chunks as durable bounded requests and exposes one combined
draft only after every chunk succeeds. If two chunks claim the same question,
the review queue shows a cross-chunk conflict with zero proposed points; it does
not silently choose the later response. Provider-size failure is shown as a
local/configuration action and never as a partially graded paper.

### 7.3 Duplicate flow

Exact duplicate:

> This file has already been uploaded to this session.

Options depend on context:

- open existing submission;
- link additional source only if valid;
- upload as another attempt (teacher role);
- cancel.

Possible visual duplicate goes to review and does not assert certainty.

### 7.4 Scan quality review

Show page thumbnails next to expected blank-page thumbnails and:

- detected orientation/page mapping;
- missing/extra/repeated pages;
- blur/cutoff warnings;
- actions: rotate mapping, reorder, exclude extra, replace source, retry.

Preprocessing never offers an image edit that could erase answer strokes.

## 8. Student-name review

The normal AI request has already graded the visible answers while reading the
page-1 identity field. This screen is still a separate teacher decision: it
assigns the locally matched roster record and never asks the provider to choose
a student. A completed grade run remains staged until this decision, then opens
without another initial-grading call.

Layout:

- complete first answer page, with additional pages available when needed;
- AI transcription;
- ranked roster candidates with name, kana, student number, class, and non-sensitive context;
- score evidence labels: exact number, close spelling, expected roster;
- search entire active roster;
- `この生徒に割り当てる`;
- `判読できない`;
- `生徒の答案ではない`.

Keyboard:

- `1`–`5` select candidate;
- arrows move;
- Enter confirms after visible focus;
- `/` focuses search;
- skip goes to next without assignment.

The screen never says “99% sure” unless the number is actually calibrated and approved for staff display. Prefer `自動割り当て基準を満たした` or `確認が必要`.

## 9. Grade review workspace

### 9.1 Review modes

- **Question-first:** review the same question across many students; efficient calibration.
- **Paper-first:** review all flagged questions for one submission and finalize.

Both use the same revision/locking semantics.

### 9.2 Question-first layout

Persistent top:

- test/session/question;
- question text;
- expected answers, Kanji policy, rubric;
- maximum points.

Each review card:

- complete answer page;
- transcription;
- proposed points/outcome;
- specific reason/warning;
- quick score buttons;
- transcription correction;
- optional note.

Names may be revealed through a role-appropriate control, but question-first default reduces reviewer bias.

### 9.3 Paper-first layout

Header:

- student/assignment state;
- test title/date;
- score summary;
- scan availability/quality;
- current grading run/provenance summary.

Question row:

- number and text;
- expected answer;
- complete answer page and transcription;
- points/outcome;
- confidence status in plain language;
- Kanji-rule badge;
- review/override action.

Filters: flagged only, incorrect/partial, all.

Paper-first is a dedicated route for exactly one submission. The left evidence
area displays the original or locally assembled multipage PDF, including a
two-page STEP answer as one document. Choosing a question navigates to its exact
evidence page. If the browser cannot display the PDF, the teacher can switch to
one selected normalized page with a lazy thumbnail rail; the UI never eagerly
loads dozens of full-resolution pages. After scan retention, the structured
grades remain visible with a clear evidence-unavailable banner.

The result editor changes transcription, outcome (`correct`, `partial`,
`incorrect`, `blank`, or `unreadable`), and points under the current question's
increment/complete-answer rules. Every save appends a revision. Finalized and
archived workspaces are read-only until the existing reopen workflow applies.

`未確認N問を一括確認` opens a count-and-acknowledgment dialog for the current
submission only. It preserves the shown points, outcome, and transcription,
marks the exact unresolved snapshot as reviewed, and leaves finalization as a
separate explicit action. Any concurrent result change aborts the full bulk
operation and asks the teacher to reload; it never confirms only the currently
loaded list page or silently includes newly arrived results.

### 9.4 Override

Quick reason codes:

- accepted equivalent;
- transcription corrected;
- partial credit;
- rubric corrected;
- scan/reading issue;
- teacher judgment;
- other (note required).

Changing points updates a provisional total immediately, but the server confirms and becomes authoritative. A stale revision shows that another teacher changed it and offers reload.

### 9.5 Finalization

Before finalization, a checklist shows:

- student assigned;
- no duplicate conflict;
- all required reviews complete;
- score arithmetic valid;
- scan warnings acknowledged where required.

Finalization button is disabled with navigable reasons, never only greyed out.

## 10. Results and progress

The report list includes only current finalized results by default. It supports
student/test search, inclusive date range, student, template, subject,
category, course, and class filters, plus test-date, finalized-time, student,
or test-title sorting in either direction. Each row has an accessible checkbox;
the header selects only the visible page. A separate `現在の条件に一致する全件`
choice is always confirmed by a server preview and is not simulated by checking
only loaded pages.

### 10.1 Result detail

Top:

- student;
- date/test;
- earned/possible/percentage;
- finalized/reopened status;
- report download.

Questions display even after scan deletion because text/answer
transcription/results are structured data. Retention removes every ordered
source page, the assembled scan, normalized pages, thumbnails, and grading
image evidence together; it keeps page ordinal/hash lineage and structured
grading history. No stale thumbnail or crop link remains. The crop area changes
to:

> 保存期間または容量上限により、答案画像は2026年7月27日に削除されました。採点結果は保持されています。

### 10.2 Progress graph

Controls:

- start/end date;
- presets;
- subject/category/course/template;
- include/exclude voided/superseded (default excluded; elevated role).

Graph:

- x-axis local test date;
- y-axis 0–100%;
- points connected chronologically only when more than one;
- hover/focus tooltip with title, date, score;
- clicking opens result;
- no artificial smoothing or forecasts;
- table below with the same data;
- partial/blank breakdown shown as supplementary bars/table, not conflated with score line.

### 10.3 Misleading-comparison guard

Different tests may have different difficulty. The UI labels the chart `得点率の推移` and does not claim learning causation. When filters mix subjects/categories, show a subtle notice.

## 11. PDF export UX

From a finalized result:

1. select `結果PDFを作成`;
2. preview included fields (no scan by default);
3. enqueue render;
4. show `作成中`;
5. offer download when verified;
6. after grade change, label old report `旧版` and offer regeneration.

Suggested filename:

```text
2026-07-27_漢字確認テスト4_大木花子_結果.pdf
```

The HTTP layer sanitizes and supplies a fallback ASCII filename; physical store never uses this display filename.

### 11.1 Bulk student result PDFs

The report list exposes `生徒別結果PDFを一括出力` to teachers and
administrators. The action supports:

1. checked rows on the current/visited pages; or
2. every result matching the current filters, independent of pagination.

Before any artifact is created, a modal calls the server preview and states an
exact sentence such as `24名・87件の確定結果を出力します`. It also explains
that the output is one canonical PDF per student/test plus `manifest.csv`, not
one recomputed cumulative transcript. Empty, duplicate, unassigned, reopened,
voided, stale, or over-limit selections keep the modal open with a corrective
message.

After confirmation the modal/status panel shows durable processed/total counts
and a determinate progress bar. Navigating away does not cancel the job. A
verified job offers one ZIP download; a failed or superseded job offers
`件数確認からやり直す`, not blind replay. The UI clears selected IDs whenever
membership filters change, never places student names in a toast or browser
notification, and reminds staff to remove downloaded ZIP/PDF files from shared
PCs after authorized use.

## 12. Administration UX

### 12.1 AI configuration

The normal **Official Gemini** flow is one modal. It accepts a new/replacement
key and the primary action is `接続を確認して有効化` (`確認中…` while pending).
The explanation states that the candidate is tested before save and is
encrypted/persisted only on full success. The server result reports
capabilities, never the secret:

- authentication;
- model available;
- structured output;
- image input;
- usage metadata and representative image task;
- last latency;
- configuration warnings.

After success, `利用中のAI機能` shows four read-only rows: ひな形の作成、氏名
の読み取り、答案のAI採点、採点結果の再確認. Each displays `利用できます`;
if the current connection cannot support it, it displays `APIキーを再設定`.
The normal UI contains no evaluation record, pilot approval, profile activation,
or rollback buttons. A failed/ambiguous replacement clearly says the previous
working key and four-feature configuration were preserved and leaves the modal
open for correction. A successful manual `接続を確認` also repairs stale
current Gemini profiles; startup reconciliation after prompt/schema/hash bumps
needs no ordinary administrator action.

Timeout, concurrency, pricing, budgets, and usage are folded under details.
**OpenRouter** remains an optional advanced/manual card. The administrator saves
the connection first and then explicitly selects `再確認`; it does not use the
Gemini one-step action. The card retains masked key replacement, exact model
slug, required-parameter/routing capability test, evaluation evidence, and
explicit profile activation/rollback.
Backward-compatible technical endpoints remain available but are not presented as the
normal Gemini setup.

The current screen exposes no Batch, economy, priority, expedite, automatic
provider-failover, or automatic finalization control. New work always uses the
normal durable queue. Capability success makes AI drafts available; the screen
continues to state that teachers compare source pages and explicitly review,
publish, assign, and finalize.

The screen explicitly states:

> Geminiがひな形作成と採点を補助します。AIの結果は、公開・確定する前に先生が確認できます。

### 12.2 Storage

Display:

- managed scans / 150 GiB;
- originals vs derivatives;
- templates/reports/logs;
- physical volume used/free;
- next cleanup;
- oldest retained scan;
- last deletion counts/reason;
- temporary/quarantine sizes.

Manual cleanup is an enqueue action, not a “delete random files” browser.

### 12.3 Jobs and provider dispatch

Business and technical views are separated:

- normal queue view: actionable paper/test status;
- admin diagnostic view: job type, attempt, operation ID, sanitized error, retry/reconcile controls.

Ambiguous direct Gemini batch creation shows a red, non-dismissable warning and never a generic Retry button. OpenRouter work is shown as individually queued/running/retrying; it is never labeled as a discounted batch.

### 12.4 Backup

- last successful and verified backup;
- destination accessibility;
- next schedule;
- restore drill age;
- start backup;
- maintenance-only restore wizard.

No screen implies scans are backed up when policy excludes them.

## 13. Accessibility and keyboard requirements

- All fields have programmatic Japanese labels and error association.
- Template creation and grading require no canvas-region or numeric-coordinate controls.
- Review actions are reachable without pointer.
- Shortcuts do not activate while typing in text fields.
- Color/status always includes icon/text.
- Zoom up to 200% preserves core workflows without horizontal page scrolling outside the canvas editor.
- Modal focus is trapped/restored; Escape does not discard an unsaved destructive action silently.
- Graph points are keyboard focusable and table-equivalent.
- Page/document language is `ja`.
- Motion honors `prefers-reduced-motion`.

## 14. Supported viewport/browser behavior

Primary:

- Edge/Chrome current and previous major on Windows 11;
- 1280×720 minimum;
- 1440×900 recommended;
- touch is optional; mouse/keyboard fully supported.

At widths below 1024, roster/status/result views remain usable, but dense
template and grading review show a recommendation to use a larger screen.
Phone layouts are not a v1 acceptance target.

## 15. UX analytics without student content

Local-only product telemetry MAY record:

- screen/action identifiers;
- duration/count;
- error/status codes;
- browser/app version;
- anonymous staff role category.

It MUST NOT record names, answers, question text, image pixels, report content, search queries, or form values. No third-party analytics script is loaded.

## 16. UX acceptance walkthroughs

A teacher usability test must complete:

1. import a 30-row roster CSV with one duplicate;
2. select HOP and a subject before upload, verify one-template-per-page preview,
   resolve a missing grade in final check, and correct an AI answer;
3. select STEP before upload, understand a six-page/three-template plan and
   fixed `-1`/`-2`/`-3` suffixes, and recognize why a five-page PDF is blocked;
4. change one Kanji policy and publish;
5. open a session and upload five scans from a peer;
6. resolve an ambiguous similar-name match;
7. correct a mistranscribed answer and award partial credit;
8. finalize a paper;
9. filter three months of progress;
10. export and open the Japanese PDF;
11. understand why an old scan is unavailable without support.

Target: trained teacher completes the normal flow without technician help; all mistakes are recoverable and no critical action is ambiguous.
