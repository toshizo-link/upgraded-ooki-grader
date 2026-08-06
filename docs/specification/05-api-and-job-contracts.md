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

Default page size is 50, maximum 200. Cursor encodes stable sort key and ID and is integrity-protected. Search strings are max 200 Unicode characters.

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
| `POST` | `/templates/{templateId}/versions` | create empty/clone draft |
| `GET/PATCH` | `/templates/{templateId}/versions/{versionId}` | draft detail/update defaults |
| `POST` | `/templates/{templateId}/versions/{versionId}/sources` | attach upload with `blankTest`, `containsModelAnswers`, `containsNonModelAnswers`, or `separateAnswerKey` role |
| `POST` | `/templates/{templateId}/versions/{versionId}:generateDraft` | enqueue AI draft |
| `GET` | `/templates/source-match?uploadIds=...&sourceRoles=...` | find an exact published source-set-and-role match before creating a redundant draft |
| `POST` | `/templates/{templateId}/versions/{versionId}/questions:verifyProposals` | atomically verify the non-blocking generated proposals and return skipped issues |
| `GET` | `/templates/{templateId}/versions/{versionId}/generation` | proposal/status |
| `POST` | `/templates/{templateId}/versions/{versionId}:acceptProposal` | copy selected proposal fields |
| `GET/POST` | `/templates/{templateId}/versions/{versionId}/questions` | list/add |
| `GET/PATCH/DELETE` | `/templates/{templateId}/versions/{versionId}/questions/{questionId}` | edit/remove draft question |
| `POST` | `/templates/{templateId}/versions/{versionId}/questions:reorder` | reorder |
| `GET/PUT` | `/templates/{templateId}/versions/{versionId}/regions` | region set |
| `POST` | `/templates/{templateId}/versions/{versionId}:validate` | validation report |
| `POST` | `/templates/{templateId}/versions/{versionId}:publish` | immutable publish |
| `POST` | `/templates/{templateId}:retire` | prevent new sessions |

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
| `GET/POST` | `/test-sessions` | list/create |
| `GET/PATCH` | `/test-sessions/{sessionId}` | detail/edit draft/open |
| `PUT` | `/test-sessions/{sessionId}/roster` | replace expected roster with revision |
| `POST` | `/test-sessions/{sessionId}:open` | allow uploads |
| `POST` | `/test-sessions/{sessionId}:close` | stop normal uploads |
| `POST` | `/test-sessions/{sessionId}:archive` | archive UI |
| `GET` | `/test-sessions/{sessionId}/summary` | counts/status/cost |

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
| `POST` | `/submissions/{submissionId}/results/{resultId}:override` | append teacher revision |
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
| `POST` | `/admin/ai-connections` | create connection/secret |
| `PUT` | `/admin/ai-connections/{id}` | replace key/settings |
| `POST` | `/admin/ai-connections/{id}:test` | synthetic capability probe |
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

The API never offers “show API key.” `GET /admin/settings/ai` returns:

```json
{
  "connections": [
    {
      "id": "01JCONNECTION...",
      "provider": "geminiDirect",
      "configured": true,
      "keyFingerprint": "sha256:7fa1…91c2",
      "lastCapabilityProbe": {"state": "passed", "checkedAt": "2026-07-27T03:15:22Z"}
    },
    {
      "id": "01JCONNECTION2...",
      "provider": "openRouter",
      "configured": true,
      "keyFingerprint": "sha256:02b4…3ae8",
      "lastCapabilityProbe": {"state": "passed", "checkedAt": "2026-07-27T03:17:10Z"}
    }
  ],
  "activeProfiles": {
    "templateExtraction": "01JPROFILE...",
    "nameTranscription": "01JPROFILE2...",
    "initialGrading": "01JPROFILE3...",
    "adjudication": "01JPROFILE4..."
  }
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
| `PrepareNameRequest` | `submission:{id}:name:{cropHash}:{schema}` | AI request |
| `MatchStudentLocally` | `submission:{id}:match:{recognitionId}:{rosterRevision}` | candidates/disposition |
| `PrepareGradingRequest` | `submission:{id}:grade:{manifestHash}` | AI request |
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
- Japanese Unicode round trips;
- pagination stability under concurrent inserts;
- deleted scan `410` behavior;
- file range and safe filename behavior;
- SSE replay and authorization;
- problem responses contain no secrets/paths;
- job duplicate delivery and lease expiry;
- all sample payloads in this specification or checked-in fixtures.
