# Coordinate-free teacher workflow

**Status:** Adopted product decision  
**Effective:** 2026-07-28  
**Supersedes:** Any earlier requirement that teachers define, verify, or repair
question, answer, name, or student-number regions.

## Decision

Ooki Grader MUST optimize for the following teacher workflow:

1. Add the question paper and, when available, its model answer or answer key;
   for each file, confirm only whether it is unfilled, authoritative, filled
   but non-authoritative, or a separate answer key.
2. Let AI create the logical question, answer, point, and grading-policy draft.
3. Review only incomplete, low-confidence, conflicting, subjective, or otherwise
   exceptional proposals; publish the template.
4. Upload completed papers in bulk.
5. Let AI read the student's name and every answer from the complete normalized
   pages, grade them, and independently recheck ambiguous answers.
6. Assign an unresolved name, correct an exceptional grade, and finalize.

Teachers MUST NOT draw rectangles, enter coordinates, tune crop margins, mask
identity areas, or map answer cells to questions. The application MUST NOT make
publishing contingent on region completeness.

## AI input and matching

- Template extraction receives the complete teacher-supplied source pages.
- For `contains_non_model_answers`, template extraction ignores visible written
  responses as answer authority and independently solves the printed
  questions. Those answers use `ai_proposed` provenance unless an uploaded
  authoritative source supplies the matched answer.
- Name transcription receives every complete normalized page and is instructed
  to locate only printed name/student-number fields and ignore answers.
- Initial grading receives every complete normalized page plus the logical
  question list and teacher-approved answers/rubrics.
- Adjudication receives every complete normalized page for the paper plus the
  one question to recheck.
- Questions are matched by printed label, question text, visual reading order,
  and whole-page context.
- Structured outputs contain logical content and confidence, not geometry.

## Local preprocessing

Local processing still performs bounded decoding, orientation normalization,
page ordering, alignment/quality checks, thumbnails, hashes, duplicate
detection, and retention accounting. It stores normalized full pages and does
not generate name or answer crops.

## Privacy and provider disclosure

Full pages can contain a student's name and answers. The UI and administrator
setup MUST state this clearly. Production requires a school-controlled,
billing-enabled provider account and the provider/privacy review described in
the security specification. Data minimization is achieved through:

- task-scoped prompts and fields;
- no roster disclosure to the vision model;
- bounded media and output sizes;
- short-lived provider processing;
- local storage and retention controls;
- audit, budgets, and explicit approved profiles.

Identity redaction or coordinate-based privacy cropping is not part of the
teacher workflow.

## Compatibility

Existing database columns and API fields for regions remain nullable and
readable so old installations can upgrade without destructive migration. New
template extraction and submission processing leave them empty. Legacy
artifact endpoints may remain temporarily for old records, but no active
workflow may depend on them.

## Acceptance criteria

- A Japanese sheet with questions, maps/tables, and writable areas interwoven
  on the same page can become a publishable template without any coordinates.
- Submission preprocessing persists normalized pages and zero submission crop
  artifacts.
- Name, grading, and adjudication requests use complete pages.
- The template editor contains no coordinate or rectangle controls.
- Review screens show the complete answer page rather than a crop.
- The Japanese user guide teaches only this coordinate-free workflow.
