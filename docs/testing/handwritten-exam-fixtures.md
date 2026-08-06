# Handwritten exam and Japanese worksheet smoke-test fixtures

No public fixture located so far combines all production characteristics:
Japanese middle-school questions, writable answer areas interwoven with the
question sheet, real student handwriting, an authoritative answer key, and
teacher scores under a commercial-use-compatible licence. Testing therefore
uses two public datasets for separate concerns. Neither replaces the
school-approved golden set.

## Japanese handwriting

The Japanese handwriting fixtures come from:

> Maeda, Koki and Okazaki, Naoaki (2026), “JaWildText: A Benchmark
> for Vision-Language Models on Japanese Scene Text Understanding”.

Dataset: <https://huggingface.co/datasets/llm-jp/jawildtext>

Pinned revision:
`627ca7ea7c224ffe1accff8737991fc2240784fa`

Licence: [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0)

The dataset card describes `handwriting_ocr` as page-level Japanese
handwriting transcription and explicitly includes both annotations and images
under Apache-2.0. Do not use it to identify writers or infer personal
attributes.

Run from the repository root:

```text
node tools/fetch-japanese-handwriting-fixtures.mjs
node tools/fetch-handwritten-exam-fixtures.mjs
OOKI_EXTERNAL_FIXTURE_ROOT="$PWD" dotnet test \
  tests/OokiGrader.Preprocessing.Tests \
  --filter ExternalHandwritingFixtureSmokeTests
```

The opt-in smoke test sends all five pinned files through the real local
preprocessing pipeline and verifies their source hashes, PDF page counts,
normalized pages, thumbnails, manifests, and exact/perceptual fingerprints.
It is skipped in ordinary test runs because the licensed fixture bytes are not
stored in Git.

An explicit live Gemini smoke run is also available. Supply the key only as a
process environment variable; never put it in a command-line argument, source
file, or test output:

```text
OOKI_GEMINI_API_KEY="..." \
OOKI_EXTERNAL_FIXTURE_ROOT="$PWD" \
dotnet test tests/OokiGrader.ProviderContract.Tests \
  --filter \
  "FullyQualifiedName~ExactModelPasses|FullyQualifiedName~ExactModelTranscribes"
```

Those two opt-in tests probe the exact configured model and transcribe one
pinned public Japanese handwriting image. A third live test can verify that an
interwoven worksheet yields distinct logical questions and non-authoritative
AI answer proposals without coordinates, but it sends that
worksheet image to Gemini. Run it only when the school or image owner has
explicitly authorized that disclosure:

```text
OOKI_GEMINI_API_KEY="..." \
OOKI_INTERLEAVED_LAYOUT_IMAGE="/absolute/path/to/authorized-layout.png" \
dotnet test tests/OokiGrader.ProviderContract.Tests \
  --filter ExactModelExtractsInterwovenQuestionsWithoutCoordinates
```

Live tests consume provider quota and are never part of the ordinary
deterministic suite.

### Basic live grading-accuracy evaluation

The opt-in evaluator compares live Gemini grading proposals with the pinned
item labels and writes sanitized JSON evidence. It also applies the real local
grading-response validator so raw model coverage is not confused with results
the application can safely persist.

Render the three scored PDFs to evaluation PNGs, then run the evaluator with
the credential supplied only through standard input or a short-lived process
environment variable:

```text
mkdir -p output/accuracy/eval-media
pdftoppm -png -r 120 tmp/handwritten-exam-fixtures/Student_18.pdf \
  output/accuracy/eval-media/Student_18-page
pdftoppm -png -r 120 tmp/handwritten-exam-fixtures/Student_19.pdf \
  output/accuracy/eval-media/Student_19-page
pdftoppm -png -r 120 tmp/handwritten-exam-fixtures/Student_26.pdf \
  output/accuracy/eval-media/Student_26-page

OOKI_GEMINI_API_KEY="..." \
OOKI_EXTERNAL_FIXTURE_ROOT="$PWD" \
OOKI_GRADING_EVAL_MEDIA_DIR="$PWD/output/accuracy/eval-media" \
OOKI_GRADING_EVAL_MODEL_ID="gemini-3.5-flash-lite" \
OOKI_GRADING_EVAL_THINKING_LEVEL="MINIMAL" \
OOKI_GRADING_EVAL_OUTPUT="$PWD/output/accuracy/gemini-3.5-flash-lite-model-comparison-run-1.json" \
dotnet test tests/OokiGrader.ProviderContract.Tests \
  --filter ExactModelProducesBasicGradingAccuracyEvidence
```

`OOKI_GRADING_EVAL_MODEL_ID` defaults to `gemini-3.5-flash-lite`. The default
evidence filename includes the selected model ID, but comparison campaigns
should always set an explicit run-specific output path so repetitions cannot
overwrite each other. `OOKI_GRADING_EVAL_THINKING_LEVEL` accepts `MINIMAL`,
`LOW`, `MEDIUM`, or `HIGH`; use only levels supported by the selected model.
Gemini 3.1 Pro requires at least `LOW` and has no Gemini API free tier.

This evaluator is exploratory: the English source-data inconsistency and small
number of scored short answers prevent it from approving a production profile.
Repeat runs may measure stability, but do not increase the independent sample
count. Keep the prompt, schema, pipeline version, fixture hashes, media hashes,
and thinking level fixed when attributing a difference to the model.

Evidence schema v3 records both the provider's raw `Predictions` and the
application validator's `SystemPredictions`. Use the latter to verify local
deterministic recomputation and review-safe quarantine; retain the raw values to
measure model behavior. A bad point increment on one known question must be
reported as an item-level reconciliation reason rather than aborting the
evaluation. Identity, duplicate-ID, and irreconcilable coverage errors remain
response-wide failures.

The downloader retrieves fresh row metadata from the public dataset server,
validates immutable row identity, and verifies:

- `0051_01_2_2_1_h.jpg` — paper-based black-ink Japanese technical writing,
  425,311 bytes, SHA-256
  `0239932e51aad04001834ae953541434f07232c2c71ad4bcc0bd3358e6d68aa1`;
- `0128_45_1_2_3_h.jpg` — paper-based red-ink Japanese technical writing,
  1,511,207 bytes, SHA-256
  `32336f4bf9c16db8734d204181a31cbb2d26a927087e16601efe5a8b9c040d2c`;
- the corresponding polygon transcriptions, with separate pinned SHA-256
  values in the downloader.

The images cover real Japanese handwriting, page perspective, shadows,
different ink colours, mathematical notation, and long-form transcription.
They do not contain printed exam questions or per-question scores.

## Interleaved Japanese worksheet layout

The target layout is a single Japanese question sheet containing printed
questions, maps/tables, and multiple writable answer areas on the same page.
The representative public worksheet supplied during implementation is:

<https://startoo.co/workbook/137794/>

That page provides free printable blank and model-answer PDFs, but does not
state an open redistribution licence. It is therefore a visual reference only:
it is not committed, fetched by repository tooling, or included in release
packages.

The automated `TemplateExtractionJobWorkerTests` regression uses an original
synthetic response with the same structural property. It verifies that every
logical question retains an independent answer rectangle, including cells in
a shared grid and long response areas located far from their printed
questions. The extraction prompt explicitly prohibits merging an answer row,
grid, nearby map, or table into one crop.

## English scored answer sheets

The scored-answer smoke tests use a small subset of the public dataset:

> K P, Dinesh (2026), “A Dataset of Digitized Student Examination Papers,
> Answer Keys, and Manual Evaluations for Automated Grading Research”,
> Mendeley Data, V1, DOI `10.17632/sf3kvjwknt.1`.

Source: <https://data.mendeley.com/datasets/sf3kvjwknt/1>

Licence: [Creative Commons Attribution 4.0 International](https://creativecommons.org/licenses/by/4.0/)

The source states that names and institutional identifiers were replaced and
visually redacted. Do not attempt to identify writers from handwriting. These
files are for local engineering tests only and are not included in release
packages.

### Reproducible subset

Run from the repository root:

```text
node tools/fetch-handwritten-exam-fixtures.mjs
```

The downloader retrieves and verifies:

- `Student_18.pdf` — 2 pages, SHA-256
  `68622bdd43848e17b487ab47a531eaaff578b1b29e9f9239fa90c59d0075c034`;
- `Student_19.pdf` — 4 pages, SHA-256
  `b49444fb96457a21b3a02c45ca2f8d885e34ff0e15a22debfda93dc2d2b3b854`;
- `Student_26.pdf` — 2 pages, SHA-256
  `d92dfd9886e1363f99f2ce282ff86fc5796cf9e71c9a830367b52caad686bd96`;
- the question text, answer key, and anonymized item-level teacher marks.

The files are written to `tmp/handwritten-exam-fixtures` and ignored by Git.
The downloader pins expected byte lengths and SHA-256 values and writes files
atomically.

## Intended use and release boundary

This English university data-science exam is useful for PDF admission,
rasterization, page-quality, handwriting, Batch/direct-provider, and
teacher-review smoke tests. It is not representative of Japanese cram-school
material. The JaWildText subset is useful for Japanese handwriting admission,
preprocessing, and transcription checks, but is not an exam grading corpus.
The interleaved worksheet regression covers geometry but not real student
writing.

All downloaded files are written below `tmp/`, ignored by Git, and excluded
from release packaging. Production quality must be evaluated separately on a
school-approved, versioned Japanese golden set that combines the real cram
school layout with anonymized student answers, authoritative rubrics, and
teacher adjudication.
