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
| `awaiting_ai` | AI処理待ち | The paper is safe on the host and waiting to be submitted. |
| `gemini_batch_running` | Gemini一括採点中 | Completion may take up to 24 hours. |
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
- Template creation is upload-first: selecting or dropping files immediately
  starts upload, exact-match detection, and—after a short visible source-role
  override window—economy draft generation.
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

## 4. Dashboard

### 4.1 Teacher dashboard

Cards:

- papers needing name confirmation;
- questions/papers needing grade review;
- ready to finalize;
- Gemini batches and OpenRouter queued work in progress/delayed;
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
- class/course filter;
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

### 6.1 Upload-first creation

The default flow is one upload surface, not a multi-step form:

1. the teacher drops the question paper and any answer material together;
2. upload starts immediately and filename evidence proposes title, subject,
   and each source role;
3. a five-second visible override window lets the teacher correct only a wrong
   source-role proposal;
4. exact `(content hash, source role)` pairs are checked against published
   template source sets;
5. if an exact match exists, the UI recommends opening that immutable version
   instead of creating a duplicate;
6. the host creates and attaches the draft, then starts the configured economy
   extraction profile automatically;
7. the editor opens immediately and polls durable generation status.

Title, subject, category, grade/course, and default points remain available
under `テスト名・教科などを指定する（省略可）`. Values explicitly entered by a
teacher are preserved. Filename guesses are submitted as replaceable
placeholders when AI generation is available, allowing high-confidence printed
headers to supply better metadata.

Each source shows one editable select with `問題のみ（未記入）`,
`模範解答入り`, `記入済み答案（AIが正答を作成）`, or
`別紙の模範解答`. The third choice means that the writing already present is
not a correct-answer source: AI reads the printed questions and solves them
independently. Its supporting text says
`記入されている解答は正答として使いません`. A plain file named `解答用紙`
is never promoted to an authoritative answer key automatically. If a filled
paper cannot be classified confidently, the UI defaults to a non-authoritative
choice and asks for one quick confirmation rather than trusting its answers.
The teacher can switch to manual editing before draft generation, and an
unavailable AI profile falls back to the same editor without discarding the
uploaded files.

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
- `漢字以外の解答を許可する`;
- `常に先生の確認を必要とする`;
- teacher-only note.

When the Kanji checkbox is unchecked and canonical answer contains Kanji, show:

> ひらがな・カタカナだけの同じ読みは不正解になります。個別に許可する表記は「解答の別表記」に追加してください。

When it is checked:

> 漢字でなくても、登録した読み・採点基準に一致すれば正解にできます。

### 6.5 Proposal review

AI-proposed fields have:

- proposal badge;
- confidence/warning in teacher-friendly wording;
- `採用`, `編集して採用`, `無視`;
- no raw model probability where it could imply calibrated certainty.

The initial question list contains only unresolved exceptions: low-confidence,
incomplete, conflicting, subjective, unsupported, or otherwise always-review
items. A revision-protected `安全な提案を一括確認` action verifies all remaining
high-confidence objective proposals atomically and reports anything skipped;
it does not publish. Teacher verification remains available per question.
The full question list is one click away, and publish validation lists blocking
issues and navigates to each.

An answer extracted from an authoritative source uses a distinct
`模範解答から読取` badge and records its source document/page. An independently
solved proposal uses `AI提案`. A proposal derived from
`記入済み答案（AIが正答を作成）` also displays
`記入済み解答は正答に使用していません` so the authority rule is visible. If an
independent solution and a supplied model answer differ, both appear side by
side under `解答の矛盾を確認` and publication is blocked until the teacher
selects or enters the authoritative answer.

### 6.6 Publish

The final dialog shows:

- version number;
- page/question count;
- total points;
- unresolved non-blocking warnings;
- count of Kanji-required questions;
- count always requiring review;
- statement that publishing is immutable.

Button: `この版を公開`.

## 7. Test session and upload UX

### 7.1 Session creation

Select:

- published template/version;
- test date;
- class/course;
- expected roster (recommended);
- economy/expedite;
- session name.

The priority control explains cost and delay. Economy is preselected.

### 7.2 Upload board

The open session page supports:

- large drag/drop zone;
- multi-file selection;
- per-file progress, speed, resume state;
- detected page count;
- duplicates/warnings;
- batch summary counts by state;
- “start queued processing now” for authorized users, with provider-specific wording;
- closing-session control.

Uploading 30 files does not open 30 dialogs. Problems collect in a filterable table.

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

### 10.1 Result detail

Top:

- student;
- date/test;
- earned/possible/percentage;
- finalized/reopened status;
- report download.

Questions display even after scan deletion because text/answer transcription/results are structured data. Crop area changes to:

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

## 12. Administration UX

### 12.1 AI configuration

Connection cards:

- **Official Gemini:** masked key replacement, capability test, current quota/error state;
- **OpenRouter:** optional masked key replacement, exact model slug, credit/capability test;
- daily/monthly warn/hard budgets and optional estimated JPY conversion.

Task-profile cards:

- grading-key generation;
- name transcription;
- initial grading;
- ambiguous-answer adjudication;
- selected connection/model;
- last accuracy evaluation, teacher correction rate, median latency, and measured cost;
- activate/rollback.

Test output reports capabilities, not secret:

- authentication;
- model available;
- structured output;
- image input;
- OpenRouter required-parameter/routing support when selected;
- last latency;
- configuration warnings.

The current screen exposes no Batch, economy, priority, expedite, or automatic
provider-failover controls. New work always uses the normal durable queue and
the explicitly active evaluated profile.

The screen explicitly states:

> API keys remain on this host. Only profiles that passed the Ooki accuracy check can be activated.

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
2. create a template from a blank PDF and correct an AI answer;
3. change one Kanji policy and publish;
4. open a session and upload five scans from a peer;
5. resolve an ambiguous similar-name match;
6. correct a mistranscribed answer and award partial credit;
7. finalize a paper;
8. filter three months of progress;
9. export and open the Japanese PDF;
10. understand why an old scan is unavailable without support.

Target: trained teacher completes the normal flow without technician help; all mistakes are recoverable and no critical action is ambiguous.
