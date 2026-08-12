# API and background-job contracts

## 1. API scope

The API is an internal application contract between the browser bundle shipped with Ooki Grader and the host service. It is not a public integration API in v1, but it is versioned and documented to make testing and future integrations safe.

Base path:

```text
https://<configured-host>/api/v1
```

OpenAPI 3.1 is generated in CI. Production exposes the document only to administrators or disables it by configuration. The generated TypeScript client is built from the exact OpenAPI artifact and checked for drift.

## 2. Common conventions

### 2.1 Media and encoding

- JSON: `application/json; charset=utf-8`
- Errors: `application/problem+json`
- Chunk bytes: `application/offset+octet-stream`
- Downloads: verified content MIME
- JSON property naming: `camelCase`
- IDs: canonical 26-character ULID strings, opaque to clients
- Times: RFC 3339 UTC with `Z`
- Local test date: `YYYY-MM-DD`
- Score: integer `pointsMilli`, where `1000 = 1 point`
- Byte sizes: integer bytes

### 2.2 Correlation

Every response includes `X-Correlation-Id`. A valid caller-supplied ID may be accepted, otherwise the server generates one. Correlation IDs are safe random identifiers and contain no names.

### 2.3 Authentication

The browser uses a server-issued opaque session cookie:

```text
__Host-OokiSession=<opaque>; Secure; HttpOnly; SameSite=Strict; Path=/
```

The database stores only a hash. Mutating requests also require `X-CSRF-Token`, obtained from `/auth/csrf`, and an allowed `Origin`.

No bearer token is stored in `localStorage` or `sessionStorage`. Gemini/OpenRouter provider keys are never returned by an API.

### 2.4 Optimistic concurrency

Mutable resources return:

```text
ETag: "rev-7"
```

Updates require:

```text
If-Match: "rev-7"
```

Missing precondition returns `428`; stale revision returns `412` with current resource metadata. Append-only actions such as overrides use source revision in the body and still validate concurrency.

### 2.5 Idempotency

Creation/action endpoints marked idempotent require:

```text
Idempotency-Key: <UUID-or-ULID>
```

The server stores `(user, route, key, canonical request hash, response)` for 24 hours:

- same key and same hash returns original response;
- same key and different hash returns `409 IDEMPOTENCY_KEY_REUSED`;
- key never becomes the direct Gemini Batch API idempotency mechanism.

### 2.6 Pagination and filtering

Lists return cursor pagination:

```json
{
  "items": [],
  "nextCursor": "opaque-or-null",
  "totalApproximate": 120
}
```

Default page size is 50, maximum 200. Supported page sizes in the staff UI are
25, 50, 100, and 200. `sort` is an endpoint-specific allowlisted field; a
leading `-` means descending. Every effective order ends with the immutable ID
as a deterministic tie-breaker. The integrity-protected cursor binds the route,
normalized search terms, filters, visibility scope, sort, and last stable key,
so it cannot be replayed against a different query. Search strings are at most
200 Unicode characters and 20 normalized terms. Invalid filters, date ranges,
sorts, page cursors, or overlong values return `400 LIST_QUERY_INVALID` (an
invalid/rebound cursor may use the cursor-specific typed problem).
Supplying `pageSize`/legacy `limit` below 1 or above 200 is invalid rather than
silently clamped.

Major list endpoints accept `includeFacets=true`. The optional `facets` object
contains at most 200 sorted `{value,label,count}` values per relevant field,
computed from the complete authorized corpus rather than only the returned
page. Current clients may fall back to values on the page when talking to an
older host, but MUST treat returned facets as authoritative.

### 2.7 Problem response

```json
{
  "type": "https://ooki-grader.local/problems/scan-quality",
  "title": "The scan needs attention",
  "status": 422,
  "code": "SCAN_PAGE_MISSING",
  "detail": "Expected page 2 was not found.",
  "instance": "/api/v1/submissions/01J.../preprocess",
  "correlationId": "01J...",
  "errors": [
    {"field": "pages", "code": "EXPECTED_PAGE_MISSING", "message": "Page 2 is missing."}
  ]
}
```

`detail` never exposes stack traces, filesystem paths, prompts, keys, SQL, or provider response bodies.

### 2.8 Status codes

- `200` read/action complete;
- `201` resource created;
- `202` durable asynchronous work accepted;
- `204` action complete/no body;
- `206` file range response;
- `400` malformed request;
- `401` unauthenticated;
- `403` authenticated but unauthorized/CSRF/origin;
- `404` absent or intentionally concealed resource;
- `409` lifecycle/duplicate/idempotency conflict;
- `410` authorized resource existed but payload was deleted by retention;
- `412` stale `If-Match`;
- `413` request/file too large;
- `415` unsupported/invalid media;
- `422` semantic validation;
- `423` maintenance/read-only lock;
- `428` missing precondition;
- `429` throttled;
- `500` unexpected error;
- `502/503/504` dependency unavailable where a synchronous health/test action requires it.

## 3. Endpoint inventory

### 3.1 Bootstrap and authentication

| Method | Path | Role | Purpose |
|---|---|---|---|
| `GET` | `/bootstrap/status` | host-local only | bootstrap state, no secret |
| `POST` | `/bootstrap/complete` | host-local + bootstrap token | create first admin |
| `POST` | `/auth/login` | anonymous | create staff session |
| `POST` | `/auth/logout` | signed in | revoke current session |
| `GET` | `/auth/me` | signed in | current user/roles/session expiry |
| `GET` | `/auth/csrf` | signed in | issue/rotate CSRF token |
| `POST` | `/auth/change-password` | signed in | change own password |

Login response does not return the session token in JSON.

### 3.2 Staff

| Method | Path | Role |
|---|---|---|
| `GET/POST` | `/staff` | administrator |
| `GET/PATCH` | `/staff/{staffId}` | administrator |
| `POST` | `/staff/{staffId}:disable` | administrator |
| `POST` | `/staff/{staffId}:enable` | administrator |
| `POST` | `/staff/{staffId}:resetPassword` | administrator |
| `GET` | `/roles` | administrator |

At least one enabled administrator must remain. An administrator cannot remove their own last-admin grant.

### 3.3 Students and roster import

| Method | Path | Purpose |
|---|---|---|
| `GET/POST` | `/students` | search/create |
| `GET/PATCH` | `/students/{studentId}` | detail/update |
| `POST` | `/students/{studentId}:deactivate` | preserve history, stop matching |
| `POST` | `/students/{studentId}:reactivate` | re-enable |
| `GET/POST` | `/students/{studentId}/aliases` | list/create alias |
| `DELETE` | `/students/{studentId}/aliases/{aliasId}` | remove alias |
| `POST` | `/roster-imports` | upload CSV to staging |
| `GET` | `/roster-imports/{importId}` | preview/errors |
| `POST` | `/roster-imports/{importId}:apply` | transactional apply |
| `GET` | `/roster-imports/{importId}/errors.csv` | row errors |

Create example:

```json
{
  "studentNumber": "S-1042",
  "familyName": "大木",
  "givenName": "花子",
  "familyNameKana": "オオキ",
  "givenNameKana": "ハナコ",
  "displayName": "大木 花子",
  "gradeLabel": "中学2年",
  "course": "英語A"
}
```

### 3.4 Test templates and versions

| Method | Path | Purpose |
|---|---|---|
| `GET/POST` | `/templates` | list/create metadata |
| `GET/PATCH` | `/templates/{templateId}` | detail/edit metadata |
| `DELETE` | `/templates/{templateId}` | revision-protected soft archive; versions/history remain |
| `POST` | `/templates/{templateId}:restore` | restore archived template to `active` or `draft` |
| `POST` | `/templates/{templateId}/versions` | create empty/clone draft |
| `GET/PATCH` | `/templates/{templateId}/versions/{versionId}` | draft detail/update defaults |
| `POST` | `/templates/{templateId}/versions/{versionId}/sources` | attach upload with `blankTest`, `containsModelAnswers`, `containsNonModelAnswers`, or `separateAnswerKey` role |
| `POST` | `/templates/{templateId}/versions/{versionId}:generateDraft` | enqueue AI draft |
| `GET` | `/templates/source-match?uploadIds=...&sourceRoles=...` | find an exact published source-set-and-role match before creating a redundant draft |
| `POST` | `/templates/{templateId}/versions/{versionId}/questions:verifyProposals` | atomically verify proposals; `selectionMode: "all"` acknowledges complete reviewable proposals while returning every structural/global skip, and legacy `allNonBlocking` remains supported |
| `GET` | `/templates/{templateId}/versions/{versionId}/generation` | proposal/status |
| `POST` | `/templates/{templateId}/versions/{versionId}:acceptProposal` | copy selected proposal fields |
| `GET/POST` | `/templates/{templateId}/versions/{versionId}/questions` | list/add |
| `GET/PATCH/DELETE` | `/templates/{templateId}/versions/{versionId}/questions/{questionId}` | edit/remove draft question |
| `POST` | `/templates/{templateId}/versions/{versionId}/questions:reorder` | reorder |
| `GET/PUT` | `/templates/{templateId}/versions/{versionId}/regions` | region set |
| `POST` | `/templates/{templateId}/versions/{versionId}:validate` | validation report |
| `POST` | `/templates/{templateId}/versions/{versionId}:publish` | normal `受付を開始` contract: atomically make a valid draft immutable and create/open its canonical first test session; accepts revision plus test date/optional class and returns `testSession` |

`DELETE /templates/{templateId}` requires `If-Match: "rev-N"` on the first
archive and returns `204` with the new ETag. A repeated archive is idempotent.
Restore accepts the ETag or `{ "revision": N }` and returns the restored
template summary. The normal list excludes archived templates; use
`?state=archived` to retrieve the recoverable archive. Archive returns `409`
with `TEMPLATE_EXTRACTION_IN_PROGRESS` while any child version is generating.

`uploadIds` and `sourceRoles` contain the same number of comma-separated
values in the same order. Reuse identity includes both the stored file object
and its normalized source role; the same bytes classified once as a model
answer and once as non-model work are not an exact match.

Question update:

```json
{
  "displayLabel": "問3",
  "questionText": "次の語を漢字で書きなさい。",
  "questionType": "exact_short_text",
  "gradingMode": "transcribe_then_rules",
  "maxPointsMilli": 2000,
  "allowNonKanji": false,
  "requiresCompleteAnswer": true,
  "answerOrderInsensitive": false,
  "acceptedAnswers": [
    {"text": "大木", "variantType": "canonical"}
  ],
  "answerRegion": {
    "pageNumber": 1,
    "xMillionths": 610000,
    "yMillionths": 245000,
    "widthMillionths": 280000,
    "heightMillionths": 75000
  },
  "requiresReviewAlways": false
}
```

The editor labels `allowNonKanji: false` positively as `漢字必須`. `完答`
maps to `requiresCompleteAnswer`; `順不同` maps to
`answerOrderInsensitive`. Legacy clients that omit the two new fields receive
`false` defaults. Order-insensitive values use explicit list separators; the
server preserves duplicate component counts. The dedicated
`漢字必須の例外（読み）` rows use answer `variantType: "explicitException"`
(stored as `phonetic_exception`); ordinary `accepted` variants do not bypass
the Kanji requirement.

Attach-source metadata example:

```json
{
  "uploadId": "01JUPLOAD...",
  "sourceRole": "containsModelAnswers",
  "displayName": "模範解答記入済み答案"
}
```

For a completed paper whose answers are not a model answer:

```json
{
  "uploadId": "01JUPLOAD...",
  "sourceRole": "containsNonModelAnswers",
  "displayName": "記入済み答案（AIが正答を作成）"
}
```

The UI select maps directly to `sourceRole`; the API does not persist a second
redundant model-answer boolean. For `containsNonModelAnswers`, visible written
answers are non-authoritative and extraction must independently solve the
printed questions.

Published-version `PATCH` returns `409 TEMPLATE_VERSION_IMMUTABLE`.

### 3.5 Test sessions

| Method | Path | Purpose |
|---|---|---|
| `GET/POST` | `/test-sessions` | list/create another administration from an immutable version; normal request supplies version, date, optional class, and `openImmediately: true`, while canonical display metadata comes from the template snapshot |
| `GET/PATCH` | `/test-sessions/{sessionId}` | detail/edit draft/open |
| `PUT` | `/test-sessions/{sessionId}/roster` | replace expected roster with revision |
| `POST` | `/test-sessions/{sessionId}:open` | allow uploads |
| `POST` | `/test-sessions/{sessionId}:close` | stop normal uploads |
| `POST` | `/test-sessions/{sessionId}:archive` | archive a closed, terminal-work session |
| `GET` | `/test-sessions/{sessionId}/summary` | counts/status/cost |

Session archive returns a typed `409` until every submission is finalized or
voided and no related upload, duplicate-pending upload, ordered-scan batch, or
grading job remains nonterminal. After success, mutation endpoints return
`TEST_SESSION_ARCHIVED_READ_ONLY`; historical detail/summary/result reads remain
available and archived work is excluded from review/finalization queues.

### 3.6 Resumable uploads

#### Create

`POST /uploads` with idempotency key:

```json
{
  "purpose": "completedTest",
  "testSessionId": "01JSESSION...",
  "fileName": "scan-2026-07-27.pdf",
  "declaredMimeType": "application/pdf",
  "length": 18231422,
  "expectedSha256": "optional-lowercase-hex"
}
```

Response:

```json
{
  "uploadId": "01JUPLOAD...",
  "state": "uploading",
  "offset": 0,
  "maxChunkBytes": 8388608,
  "expiresAt": "2026-07-28T05:00:00Z",
  "chunkUrl": "/api/v1/uploads/01JUPLOAD.../content"
}
```

#### Resume/status

`HEAD /uploads/{uploadId}/content`

Headers:

```text
Upload-Offset: 8388608
Upload-Length: 18231422
Upload-Expires: ...
```

#### Append

`PATCH /uploads/{uploadId}/content`

Required headers:

```text
Content-Type: application/offset+octet-stream
Upload-Offset: 8388608
Content-Length: <1..8388608>
```

The server locks one upload during append, validates exact offset, streams with a per-chunk timeout, updates durable offset after flush, and returns the next offset. A mismatch returns `409 UPLOAD_OFFSET_MISMATCH` with current offset.

#### Finalize/cancel

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/uploads/{uploadId}:finalize` | verify full length/hash/signature; create source/submission; enqueue |
| `DELETE` | `/uploads/{uploadId}` | cancel and remove temporary bytes |
| `GET` | `/uploads/{uploadId}` | status |

Finalize returns:

```json
{
  "uploadId": "01JUPLOAD...",
  "state": "completed",
  "submissionId": "01JSUB...",
  "jobId": "01JJOB...",
  "statusUrl": "/api/v1/submissions/01JSUB..."
}
```

#### Ordered one-page scan batches

The session detail exposes `expectedSubmissionPageCount`. Clients freeze the
complete one-page manifest before starting parallel uploads:

| Selected published test | `expectedSubmissionPageCount` |
|---|---:|
| HOP | 1 |
| STEP `-1`, `-2`, or `-3` variation/session | 2 |
| Class placement | complete published template count, 1–50 |
| Other | complete published template count, 1–50 |

A registered STEP variation is an independent test/session; clients must not
submit the original six-page source set as one answer. The ordered manifest is
the ownership authority. It must use unique, gap-free one-based ordinals, may
contain at most 1,000 items, and its item count must be an exact multiple of the
expected submission page count.

```http
POST /test-sessions/{sessionId}/ordered-scan-batches
Idempotency-Key: ...
```

```json
{
  "items": [
    {"clientItemId":"uuid-1","fileName":"SCAN_0001.pdf","inputOrdinal":1},
    {"clientItemId":"uuid-2","fileName":"SCAN_0002.pdf","inputOrdinal":2}
  ]
}
```

Each upload uses purpose `completedTestPage` and repeats the immutable binding:

```json
{
  "purpose": "completedTestPage",
  "testSessionId": "01JSESSION...",
  "orderedScanBatchId": "01JBATCH...",
  "inputOrdinal": 1,
  "clientItemId": "uuid-1",
  "fileName": "SCAN_0001.pdf",
  "declaredMimeType": "application/pdf",
  "length": 842113
}
```

The host returns `orderedScanItemId` instead of creating a submission at upload
finalization. After every item is durable, the client uses the latest row
version:

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/ordered-scan-batches/{batchId}` | manifest, items, groups, issues, submissions |
| `POST` | `/ordered-scan-batches/{batchId}:finalize` | freeze/queue local classification and assembly |
| `POST` | `/ordered-scan-batches/{batchId}:cancel` | release staged pages before assembly |

Finalize/cancel bodies are `{ "expectedRowVersion": 7 }`. Batch statuses are
`draft`, `processing`, `completed`, `needsReview`, `failed`, `cancelled`, and
`expired`. Input and group ordinals are one-based; submission page numbers are
one-based. Replaying the same batch/manifest is idempotent and never creates a
second logical submission.

Every `completedTestPage` upload must verify as exactly one PDF page and must
match the batch's immutable session, item ID, ordinal, filename, length, and
declared type. During `ordered_scan.assemble`, the host—not an external
provider—matches each input against published template pages and requires an
unambiguous role in exact page order. Missing, repeated, foreign, ambiguous, or
out-of-order roles move the batch to review without creating a logical
submission. Valid groups retain source ordinal/page/hash lineage, and ordered
submission analysis reads identity only from logical page 1 in its first chunk.
Legacy/name-only fallback also receives logical page 1 only.

The API cannot authenticate the student identity of page 2 or later from order
alone. If a different student's later page has no identifier and occupies the
correct template role, structural validation succeeds; the school must enforce
consecutive per-student scanning or use a separate manual/identified workflow.

### 3.7 Submissions, assignment, and grading

| Method | Path | Role/purpose |
|---|---|---|
| `GET` | `/submissions` | filtered queue/list |
| `GET` | `/submissions/{submissionId}` | detail/status |
| `GET` | `/submissions/{submissionId}/pages` | page metadata |
| `GET` | `/submissions/{submissionId}/pages/{pageId}/thumbnail` | authorized image |
| `GET` | `/submissions/{submissionId}/questions/{questionId}/crop` | authorized answer crop |
| `POST` | `/submissions/{submissionId}:retryPreprocess` | retry after correction |
| `GET` | `/submissions/{submissionId}/name-candidates` | ranked candidates |
| `POST` | `/submissions/{submissionId}:assignStudent` | manual assign |
| `POST` | `/submissions/{submissionId}:markUnidentified` | explicit status |
| `POST` | `/submissions/{submissionId}:queueGrading` | enqueue/requeue |
| `POST` | `/submissions/{submissionId}:changePriority` | economy/expedite |
| `GET` | `/submissions/{submissionId}/grading-runs` | provenance/history |
| `GET` | `/submissions/{submissionId}/grading-runs/{runId}` | question results |
| `GET` | `/submissions/{submissionId}/grading-workspace` | one submission's complete current run, pages, metadata, and exact unresolved snapshot |
| `GET` | `/submissions/{submissionId}/original-pdf` | authorized original/assembled multipage PDF with range support |
| `GET` | `/submissions/{submissionId}/pages/{pageId}/thumbnail` | submission-scoped lazy thumbnail |
| `POST` | `/submissions/{submissionId}/results/{resultId}:override` | append teacher revision |
| `POST` | `/submissions/{submissionId}/results:confirm-unresolved` | preserve values and resolve the exact current unresolved set; does not finalize |
| `POST` | `/submissions/{submissionId}:finalize` | finalize current run |
| `POST` | `/submissions/{submissionId}:reopen` | reason required |
| `POST` | `/submissions/{submissionId}:void` | reason required |
| `POST` | `/submissions/{submissionId}:requestRegrade` | explicit new run |

Manual assignment:

```json
{
  "studentId": "01JSTUDENT...",
  "sourceRevision": 12,
  "reasonCode": "teacher_confirmed_handwriting",
  "note": ""
}
```

Override:

```json
{
  "sourceResultRevision": 1,
  "awardedPointsMilli": 1000,
  "outcome": "correct",
  "transcriptionCorrection": "大木",
  "reasonCode": "accepted_equivalent",
  "note": "The teacher confirmed this spelling."
}
```

### 3.8 Review queues

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/review/name` | name review queue |
| `GET` | `/review/grading` | question/submission review queue |
| `POST` | `/review/items/{itemId}:claim` | optional short reviewer lease |
| `POST` | `/review/items/{itemId}:release` | release |
| `GET` | `/review/counts` | dashboard badges |

Claiming prevents accidental simultaneous edits but never prevents an administrator from resolving an abandoned claim. Final mutations still use revisions.

### 3.9 Results, progress, and exports

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/students/{studentId}/results` | paginated finalized results |
| `GET` | `/students/{studentId}/progress` | graph/table series |
| `GET` | `/results/{submissionId}` | current finalized detail |
| `POST` | `/results/{submissionId}/exports` | enqueue result PDF |
| `GET` | `/exports/{exportId}` | status/provenance |
| `GET` | `/exports/{exportId}/file` | secure PDF download |
| `POST` | `/exports/{exportId}:regenerate` | new revision if allowed |
| `POST` | `/transcript-exports:preview` | resolve checked rows or all current report matches and return exact counts/fingerprint |
| `POST` | `/transcript-exports` | enqueue a verified ZIP of canonical per-result PDFs; teacher/admin only |
| `GET` | `/transcript-exports/{exportId}` | bulk-package state, progress, counts, and safe error |
| `GET` | `/transcript-exports/{exportId}/file` | secure verified ZIP download |

Bulk preview accepts exactly one selector. Explicit selection uses distinct
submission IDs:

```json
{
  "selector": {
    "submissionIds": ["01J...", "01K..."]
  }
}
```

All-matches selection uses the same membership fields as the finalized
`GET /submissions` report list:

```json
{
  "selector": {
    "filter": {
      "search": "大木 国語",
      "from": "2026-04-01",
      "to": "2026-08-10",
      "studentId": null,
      "templateId": "01J...",
      "subject": "国語",
      "category": "漢字",
      "course": "本科",
      "class": "A組",
      "sort": "-testDate"
    }
  }
}
```

Preview returns a server-normalized selector, `studentCount`, `resultCount`,
`sourceFingerprint`, and the enforced student/result/archive-size limits.
Creation repeats that selector and fingerprint and supplies the normal
`Idempotency-Key` request header. The export stores that key and a request
fingerprint in the same transaction as the export/job; uniqueness is scoped to
the creating staff user. The same key plus identical request recovers the same
export even if the HTTP replay record was not written, while the same key with
different input is rejected. A changed source returns
`412 BULK_EXPORT_SOURCE_SNAPSHOT_STALE`; an empty, duplicate, ineligible, or
over-limit selection returns a typed 422 problem. In all-matches mode, if even
one matched finalized row is unassigned or otherwise unsafe to export, the
whole request fails atomically with
`422 BULK_EXPORT_FILTER_HAS_NON_EXPORTABLE_RESULTS` and a non-PII count; the
server must not silently create a partial archive. Status is one of `queued`,
`rendering`, `verified`, `failed`, or `superseded`, with
`progressBasisPoints`, processed/total counts, provenance hashes/versions, safe
error fields, timestamps, and a `fileUrl` only when verified. ZIP downloads use
`application/zip`, private/no-store caching, `nosniff`, an SHA-256 ETag, and
range requests.

Create uses the low-cardinality site policy `bulk-transcript-export-create`:
burst 3, refill 1 request/minute, no queue. The create transaction additionally
permits at most 2 active (`queued`/`rendering`) exports for one staff actor and
4 for the site. A cap returns `429 BULK_EXPORT_ACTIVE_LIMIT_REACHED`,
`retryable: true`, and `Retry-After: 60`. Preview, status, and download use the
normal authenticated `search` limiter. Status revalidates a verified source
snapshot before returning `fileUrl`; drift durably changes the export to
`superseded`, so a stale archive is never advertised as downloadable.

Progress query:

```text
GET /students/{id}/progress?from=2026-04-01&to=2026-07-31&subject=国語&category=漢字
```

Response:

```json
{
  "student": {"id": "01J...", "displayName": "大木 花子"},
  "range": {"from": "2026-04-01", "to": "2026-07-31", "timeZone": "Asia/Tokyo"},
  "series": [
    {
      "submissionId": "01J...",
      "testDate": "2026-07-27",
      "testTitle": "漢字確認テスト 4",
      "earnedPointsMilli": 18000,
      "possiblePointsMilli": 20000,
      "percentageBasisPoints": 9000,
      "correct": 17,
      "partial": 2,
      "incorrect": 1,
      "blank": 0,
      "resultRevision": 3
    }
  ]
}
```

`percentageBasisPoints = 10000` means 100.00%.

### 3.10 Administration and operations

| Method | Path | Role/purpose |
|---|---|---|
| `GET/PATCH` | `/admin/settings/site` | administrator |
| `GET` | `/admin/ai-connections` | masked Gemini/OpenRouter connections |
| `POST` | `/admin/ai-connections` | create connection/secret; Gemini optionally tests-and-enables before commit |
| `PUT` | `/admin/ai-connections/{id}` | replace key/settings; Gemini optionally tests-and-enables before commit |
| `POST` | `/admin/ai-connections/{id}:test` | synthetic capability probe; successful Gemini test self-heals current profiles |
| `GET/POST` | `/admin/ai-task-profiles` | list/create per-task profiles |
| `PATCH` | `/admin/ai-task-profiles/{id}` | update draft profile |
| `POST` | `/admin/ai-task-profiles/{id}:validate` | capability + accuracy fixture run |
| `POST` | `/admin/ai-task-profiles/{id}:activate` | activate approved revision |
| `GET/POST` | `/admin/settings/budgets` | budget policy |
| `GET` | `/admin/usage` | measured/estimated cost |
| `GET` | `/admin/health` | component health |
| `GET` | `/admin/jobs` | durable job queue |
| `POST` | `/admin/jobs/{jobId}:retry` | safe retry |
| `POST` | `/admin/jobs/{jobId}:cancel` | cancel queued work |
| `GET` | `/admin/ai-batches` | direct Gemini batch ledger |
| `POST` | `/admin/ai-batches/{id}:reconcile` | force direct Gemini read-only remote reconcile |
| `GET` | `/admin/ai-dispatch-groups` | Gemini batch/OpenRouter queued group view |
| `GET` | `/admin/storage` | physical/logical/quota metrics |
| `POST` | `/admin/retention:run` | enqueue cleanup |
| `GET` | `/admin/deletions` | manifests/history |
| `POST` | `/admin/backups` | enqueue backup |
| `GET` | `/admin/backups` | status/history |
| `POST` | `/admin/maintenance:enter` | mutation gate |
| `POST` | `/admin/maintenance:exit` | leave after checks |
| `GET` | `/admin/audit-events` | filtered audit |
| `POST` | `/admin/diagnostic-bundles` | redacted support bundle |

Gemini `POST`/`PUT` accepts optional `testAndEnable`; the normal Web flow sends
`true`. That request is candidate-first: the supplied key must pass
authentication, exact-model, image-input, strict-structured-output,
usage-metadata, and representative image-task checks before any secret,
connection revision, or profile pointer is persisted. On full success the same
transaction encrypts/persists the key and activates the exact current
`templateExtraction`, `nameTranscription`, `initialGrading`, and `adjudication`
profiles with `approval_state=capability_passed`. Failure, timeout, cancellation,
or an ambiguous replace result returns a typed error and leaves the former key,
connection, and active profiles byte-for-byte/revision-for-revision unchanged.

The existing `:test` route uses the stored Gemini key and the same full probe;
success repairs missing or stale exact-current Gemini profiles atomically.
Startup performs the same active-profile reconciliation after checked-in
prompt/schema/configuration-hash changes. Jobs already queued remain pinned to
their recorded immutable profile revision. `testAndEnable` is not the normal
OpenRouter contract. OpenRouter is saved first, then explicitly rechecked by the
administrator; OpenRouter and legacy callers keep the advanced profile create,
validate, approve, activate, and rollback routes in this table.

The API never offers “show API key.” `GET /admin/ai-connections` returns a
paged masked connection collection, for example:

```json
{
  "items": [
    {
      "id": "01JCONNECTION...",
      "provider": "geminiDirect",
      "configured": true,
      "keyFingerprint": "sha256:7fa1…91c2",
      "lastCapabilityProbe": {
        "state": "passed",
        "checkedAt": "2026-07-27T03:15:22Z",
        "imageInput": true,
        "structuredOutput": true
      }
    }
  ],
  "nextCursor": null,
  "totalApproximate": 1
}
```

`GET /admin/ai-task-profiles` separately returns the paged profile collection.
The Web derives the four read-only current-task states from active entries such
as:

```json
{
  "taskType": "templateExtraction",
  "approvalState": "capability_passed",
  "active": true,
  "stale": false,
  "connectionId": "01JCONNECTION..."
}
```

### 3.11 File route behavior

- Authorization is checked for every request and owner object.
- File references are opaque IDs, not paths.
- `Content-Disposition` uses a server-generated safe Japanese filename plus RFC 5987 encoding.
- `Cache-Control: private, no-store` for scans and reports.
- `X-Content-Type-Options: nosniff`.
- Range requests are allowed for PDF viewing.
- Scan-deleted authorized requests return `410` with deletion date/reason; unauthorized users still receive `404`.
- Thumbnail responses may have short private in-memory browser caching only if security review approves; default no-store.

## 4. SSE status stream

`GET /events` returns `text/event-stream`. Events contain IDs and non-sensitive status summaries:

```text
id: 01JEVENT...
event: submission.status
data: {"submissionId":"01JSUB...","state":"needsGradeReview","revision":14}
```

Event types:

- `submission.status`;
- `upload.status`;
- `job.status`;
- `review.counts`;
- `export.status`;
- `system.storageWarning`;
- `system.providerWarning`;
- `session.summaryChanged`.

Rules:

- authorization filters events;
- names/answers never appear in event payload;
- `Last-Event-ID` supports bounded replay;
- client falls back to polling with exponential interval;
- heartbeat every 20 seconds;
- connections close at session expiry.

## 5. Background-job contract

### 5.1 Common handler contract

Every handler:

1. receives entity IDs and expected versions;
2. loads current state;
3. returns success if the desired durable outcome already exists with matching input hash;
4. checks cancellation and dependency/budget gates;
5. creates file intents before filesystem changes;
6. performs bounded work with timeout/cancellation;
7. commits output and outbox events transactionally;
8. records sanitized metrics;
9. classifies errors as transient, permanent, blocked, or manual;
10. never logs raw student content.

### 5.2 Job catalog

| Job type | Deduplication key | Main output |
|---|---|---|
| `ValidateUpload` | `upload:{id}:final` | verified source file/submission |
| `PreprocessTemplate` | `template-version:{id}:preprocess:{sourceHash}` | normalized blank pages |
| `GenerateTemplateProposal` | `template-version:{id}:generate:{inputHash}:{promptVersion}` | proposal |
| `PreprocessSubmission` | `submission:{id}:preprocess:{inputHash}:{pipeline}` | pages/quality/crops |
| `ordered_scan.assemble` | `ordered-scan:{batchId}:assemble` | classified groups, source lineage, logical submissions |
| `PrepareSubmissionAnalysis` | `submission:{id}:gemini-analyze:{manifestHash}:{profile}:{revision}:{promptHash}` plus deterministic chunk manifest | chunk 1 identity + grading, later grading-only chunks |
| `PrepareNameRequest` | `submission:{id}:name:{pageManifestHash}:{schema}` | fallback/legacy page-1 name request |
| `MatchStudentLocally` | `submission:{id}:match:{recognitionId}:{rosterRevision}` | candidates/disposition |
| `PrepareGradingRequest` | `submission:{id}:grade:{manifestHash}` plus deterministic chunk manifest | one durable AI request per consecutive page chunk |
| `AssembleAiBatch` | compatibility key + assembly epoch | prepared batch |
| `SubmitAiBatch` | `ai-batch:{id}:submitEpoch` | provider operation ID |
| `ReconcileAiBatch` | `ai-batch:{id}:operation` | terminal/next poll |
| `DispatchOpenRouterRequest` | `ai-request:{id}:inputHash:{profile}` | validated response/usage |
| `ApplyAiResponse` | `ai-request:{id}:responseHash` | proposal/run/result |
| `DeleteProviderFiles` | `ai-batch:{id}:providerCleanup` | confirmed cleanup ledger |
| `RunDeterministicGrading` | `grading-run:{id}:rules:{policy}` | question results |
| `RecomputeResultTotal` | `grading-run:{id}:resultRevision` | exact total |
| `RecomputeProgress` | `student:{id}:sourceRevision` | progress projection |
| `RenderResultPdf` | `export:{id}:sourceHash:{renderer}` | PDF file |
| `RunRetention` | time-bucket/reason | deletion manifests |
| `ReconcileFileIntents` | singleton | repaired file/database boundary |
| `CleanupTemporaryFiles` | hourly bucket | abandoned temp deletion |
| `CreateBackup` | scheduled backup ID | verified backup |
| `VerifyBackup` | backup manifest hash | verification state |
| `DatabaseIntegrityCheck` | maintenance window | check report |

Initial grading uses pipeline `gemini-submission-analysis-page-chunks-v5` and
schema `submission_analysis_v2`. Chunk 1 returns the page-1 identity component
and grading observations; later chunks require `identity=null`. Identity and
grading components are validated and persisted independently. A chunk
contains at most 32 page media parts and raw bytes no greater than the smallest
of the configured media cap, 12 MiB, and the dynamic base64-aware allowance
under the Gemini client's 18 MiB complete serialized-request limit. The dynamic
calculation reserves one MiB for the JSON envelope after counting UTF-8 system
instruction, user instruction, and schema bytes. More than 300 questions,
system/user instructions above 20,000/100,000 characters, exhausted serialized
overhead, or one page above the effective allowance fails locally before
provider media I/O.

Each chunk is keyed by its immutable manifest hash. Redelivery, partial retry,
and the retained direct/legacy dispatch continuations reuse terminal chunks and
settle usage once. Only after every chunk succeeds does the apply path create
one grading run. A question observed by exactly one chunk uses that evidence;
missing observations from other chunks are neutral; multiple observations
produce `ai_chunk_observation_conflict` and mandatory manual review.

If all chunks finish before student confirmation, the host stores one
non-current `awaiting_identity` run. It cannot appear in grade/finalization
queues. Assigning a student or explicitly marking the paper unidentified
activates that same run and any parked adjudication work without another
initial-grading request. Marking it as a non-student sample discards the staged
run and cancels pending apply/recheck work while retaining request usage ledgers.

The grading-workspace bulk confirmation request carries submission revision,
run ID, result-source revision, and every unresolved result/revision pair (at
most 300). The server requires that set to equal the current unresolved set and
commits append-only teacher-confirmation revisions in one transaction. Any
changed, missing, finalized, voided, or archived item rejects the whole action
with no partial writes. A semantic replay is harmless. This operation is
separate from submission finalization.

`RunRetention` includes every live managed-scan reference owned by the selected
submission: ordered one-page sources, the assembled PDF, normalized pages,
thumbnails, and grading artifacts. Manifest finalization clears live source and
answer-evidence references, removes derived page/artifact records, and sets
`scan_deleted`. Immutable source ordinals/hashes and structured grading runs,
results, revisions, totals, audits, and reports remain. A content-addressed
object shared by a non-expired reference is not physically deleted.

### 5.3 Retry policy

Default transient retry:

```text
attempt 1: 30 seconds
attempt 2: 2 minutes
attempt 3: 10 minutes
attempt 4: 30 minutes
attempt 5+: 2 hours, capped and jittered
```

Each type defines maximum attempts/deadline. Provider `Retry-After` takes precedence when longer. Batch ambiguous-create errors use reconciliation instead of this generic retry.

### 5.4 Job visibility

Teachers see business status, not implementation names. Administrators see:

- job type/entity;
- state/progress;
- created/started/next attempt;
- attempt count;
- bounded error code;
- dependency/budget block;
- correlation/causation;
- safe retry/cancel availability.

No UI action may retry a non-idempotent direct Gemini batch create without the special reconciliation workflow. Individual OpenRouter requests follow their own idempotent-local-request/duplicate-cost safeguards.

## 6. Rate and abuse controls

Default per-user/IP limits:

- login: 5 failures/15 minutes plus progressive delay;
- upload session create: 30/hour;
- concurrent uploads: 3/user, 10/host;
- search/list: 120/minute;
- export create: 30/hour;
- AI priority change/retry: 20/hour;
- SSE: 3 connections/session.

Large bodies are streamed and bounded before JSON parsing where applicable. Rate limits are configurable but cannot disable login protection.

## 7. Compatibility and deprecation

- Breaking changes create `/api/v2`; additive fields are allowed in v1.
- Clients ignore unknown response properties.
- Server rejects unknown enum/input properties where ambiguity could alter grades.
- Frontend and host are shipped together and exchange a build compatibility header.
- A stale frontend receives `409 CLIENT_VERSION_MISMATCH` and reload instruction.
- Stored job payloads are schema-versioned; upgrades include upconverters or drain requirements.
- Provider DTOs never become browser/domain contracts.

## 8. Contract testing

CI MUST test:

- OpenAPI schema generation and TypeScript client compilation;
- every role against every endpoint;
- CSRF/origin/session/lockout behavior;
- ETag and idempotency semantics;
- upload interruption/resume/hash/offset/expiry;
- ordered one-page manifests for HOP 1, independent STEP 2, and
  class-placement/Other 1–50, including role/order/duplicate failures and
  idempotent assembly;
- multi-chunk initial grading at the serialized provider boundary, partial
  replay, last-page evidence, and cross-chunk conflict review;
- Japanese Unicode round trips;
- pagination stability under concurrent inserts;
- deleted scan `410` behavior;
- retention of structured grades and ordered ordinal/hash lineage after all
  source, assembled, normalized, thumbnail, and grading-image references are
  released;
- file range and safe filename behavior;
- SSE replay and authorization;
- problem responses contain no secrets/paths;
- job duplicate delivery and lease expiry;
- all sample payloads in this specification or checked-in fixtures.
