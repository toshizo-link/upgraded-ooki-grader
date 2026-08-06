# Ooki Grader specification index

**Status:** Baseline design with teacher-first simplification  
**Specification version:** 1.0  
**Last verified:** 2026-08-05  
**Primary deployment:** One Japanese cram-school site, Windows 11 host, trusted school LAN  
**Primary users:** School administrators, teachers, and scan operators

## Reading order

1. [Vision, scope, and success criteria](00-vision-and-scope.md)
2. [Product requirements and acceptance criteria](01-product-requirements.md)
3. [System architecture](02-system-architecture.md)
4. [Domain model, storage, and retention](03-data-storage-and-retention.md)
5. [AI, recognition, and grading design](04-ai-and-grading-design.md)
6. [API and background-job contracts](05-api-and-job-contracts.md)
7. [UX and interaction specification](06-ux-specification.md)
8. [Security, privacy, and compliance](07-security-privacy-and-compliance.md)
9. [Windows deployment and operations](08-deployment-and-operations.md)
10. [Testing, quality, and observability](09-testing-quality-and-observability.md)
11. [Development plan and work breakdown](10-development-plan.md)
12. [Decisions, risks, assumptions, and glossary](11-decisions-risks-and-glossary.md)
13. [Coordinate-free teacher workflow](12-coordinate-free-teacher-workflow.md)

## Normative language

The terms **MUST**, **MUST NOT**, **SHOULD**, **SHOULD NOT**, and **MAY** express requirement strength:

- **MUST/MUST NOT:** required for production acceptance;
- **SHOULD/SHOULD NOT:** expected unless a documented architecture decision explains an exception;
- **MAY:** optional or future-compatible behavior.

## Source-of-truth rules

- Product behavior and acceptance criteria are authoritative in `01-product-requirements.md`.
- Architecture and component boundaries are authoritative in `02-system-architecture.md`.
- Data definitions and deletion semantics are authoritative in `03-data-storage-and-retention.md`.
- AI confidence, grading, and provider behavior are authoritative in `04-ai-and-grading-design.md`.
- Endpoint details are authoritative in `05-api-and-job-contracts.md`.
- If documents conflict, resolve the conflict by updating the relevant authoritative document and recording the decision in `11-decisions-risks-and-glossary.md`.
- The adopted coordinate-free workflow in
  `12-coordinate-free-teacher-workflow.md` supersedes earlier references to
  teacher-edited regions, crops, privacy masking, or full-page fallback.
- The 2026-08-05 teacher-first simplification routes all new work through one
  normal queued request path. Gemini is the default; an administrator may add
  an OpenRouter connection, but only an image-capable, structured-output model
  with approved accuracy evidence can be activated. Batch/economy/priority
  controls and teacher-facing routing choices described in older chapters are
  legacy architecture background. Silent cross-provider failover is disabled.
  The current UI shows the uploaded source beside the AI draft and requires
  teacher confirmation before publishing or finalizing.
- A release MUST use one tagged, internally consistent specification version.

## External facts verified for this design

The following facts affect architecture and cost. They are not guarantees by Ooki Grader and MUST be reverified before implementation and release:

- Google lists `gemini-3.5-flash-lite` as a stable GA model supporting text, image, PDF, structured output, and the Batch API. Its listed limits are 1,048,576 input tokens and 65,536 output tokens. [Official model page](https://ai.google.dev/gemini-api/docs/models/gemini-3.5-flash-lite)
- The Batch API is asynchronous, targets completion within 24 hours, is available only through `generateContent`, supports inline requests under 20 MB or JSONL input files up to 2 GB, and is priced at 50% of equivalent standard inference. Batch creation is not idempotent. [Official Batch API guide](https://ai.google.dev/gemini-api/docs/batch-api)
- As verified on 2026-07-27, paid Gemini 3.5 Flash-Lite batch pricing is USD $0.15 per million text/image/video/audio input tokens and $1.25 per million output tokens, including thinking tokens. Pricing is configuration data, not a constant embedded in grading logic. [Official pricing page](https://ai.google.dev/gemini-api/docs/pricing)
- Gemini document understanding accepts PDFs up to 50 MB or 1,000 pages. Scans MUST be normalized and split below provider limits before upload. [Official document-processing guide](https://ai.google.dev/gemini-api/docs/document-processing)
- Gemini Files API objects are provider-side, temporary working copies: the current documented limit is 20 GB per project, 2 GB per file, and automatic deletion after 48 hours. Ooki Grader will additionally request deletion immediately after use. [Official Files API guide](https://ai.google.dev/gemini-api/docs/files)
- Google states that paid-service prompts and responses are not used to improve its products, while unpaid services may use submitted content and explicitly warn against submitting personal information. Production MUST use a billing-enabled paid project. [Gemini API terms](https://ai.google.dev/gemini-api/terms) and [zero-data-retention guide](https://ai.google.dev/gemini-api/docs/zdr)
- Google requires production clients to keep API keys server-side. Ooki Grader therefore stores the key only on the host and proxies all AI work through the host service. [Official API key guide](https://ai.google.dev/gemini-api/docs/api-key)
- The current Gemini terms require API users to be at least 18 and prohibit API clients directed toward, or likely accessed by, people under 18. Ooki Grader is therefore a **staff-only application**; there is no student login or student-facing portal in this scope. [Gemini API terms](https://ai.google.dev/gemini-api/terms)
- OpenRouter exposes an OpenAI-compatible API, multimodal image/PDF input, strict JSON-schema structured outputs on compatible models, model/provider routing, and response usage/cost information. These capabilities vary by model and routed endpoint, so Ooki Grader requires a live image and structured-output probe before activation. [Official quickstart](https://openrouter.ai/docs/quickstart), [multimodal guide](https://openrouter.ai/docs/guides/overview/multimodal/overview), [structured-output guide](https://openrouter.ai/docs/guides/features/structured-outputs), and [usage accounting](https://openrouter.ai/docs/cookbook/administration/usage-accounting)
- No general asynchronous discounted chat-completions batch endpoint was found in OpenRouter's official API documentation as of 2026-07-27. Ooki Grader therefore uses its own durable standard-request queue for OpenRouter and does not claim a provider batch discount. Gemini Batch remains documented external capability, but the current teacher workflow does not expose or use Batch/priority controls for new work.
- .NET 10 is an active LTS release supported through November 2028, and Microsoft documents hosting ASP.NET Core directly as a Windows Service without IIS. [Official .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) and [Windows Service hosting guide](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/windows-service?view=aspnetcore-10.0)
- Student names, handwriting, test answers, scores, and linked scan images are personal data. The operator must review the current Japanese Act on the Protection of Personal Information and applicable guidance before production. [Personal Information Protection Commission general guidelines](https://www.ppc.go.jp/personalinfo/legal/guidelines_tsusoku/)

## Product baseline in one paragraph

One Windows 11 host runs an ASP.NET Core service and owns an embedded SQLite database plus an NTFS-managed file store. Every staff computer uses a browser over HTTPS on the school LAN. Teachers add one or more sources—blank questions, a paper containing model answers, a completed non-model paper for which AI must solve independently, and/or a separate model-answer sheet—and usually accept the automatically proposed source classification. The original page stays visible beside the generated question-and-answer draft; safe proposals can be confirmed together and only exceptions need individual correction. After one explicit publish action, teachers start answer intake, upload completed papers, and let the active evaluated AI profile recognize names and propose grading; Gemini is the checked-in default, while an approved OpenRouter model may be selected explicitly. Supplied model answers remain authoritative and writing on a non-model paper is never silently treated as an answer key. Every AI result remains reviewable and finalization stays teacher-controlled. Scores remain durable records, while original test scans and scan-derived artifacts are removed after three calendar months or earlier when the managed quota requires it.
