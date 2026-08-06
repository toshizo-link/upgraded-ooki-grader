from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tmp" / "pdfs" / "user-guide"))

from seed_demo import ApiClient  # noqa: E402


EXPECTED_LABELS = [
    "①",
    "②",
    "③",
    "④",
    "③（2回目）",
    "⑤",
    "⑥",
    "⑦",
    "⑧",
    "⑧（2回目）",
    "⑧（3回目）",
]
EXPECTED_ANSWERS = [
    "光",
    "太陽",
    "光源",
    "月",
    "光源",
    "かげ",
    "直進",
    "上下左右",
    "反射",
    "反射",
    "反射",
]
NON_MODEL_CANONICAL_OPTIONS = [
    {answer} for answer in EXPECTED_ANSWERS
]
# In non-model mode the handwriting is deliberately not answer evidence. 影 is
# therefore an equally correct independently-solved canonical form, provided
# the grader also retains the worksheet's kana form as an accepted answer.
NON_MODEL_CANONICAL_OPTIONS[5] = {"かげ", "影"}
NON_MODEL_REQUIRED_ACCEPTED_FORMS = [
    {answer} for answer in EXPECTED_ANSWERS
]
NON_MODEL_REQUIRED_ACCEPTED_FORMS[5] = {"かげ", "影"}
EXPECTED_ORDERS = list(range(len(EXPECTED_LABELS)))
# The final two blanks legitimately repeat 反射 elsewhere in the same sentence.
# Every other visible occurrence would expose the answer that should have been
# removed from the handwritten slot. ⑤ also guards the common Kanji rewrite.
ALLOWED_VISIBLE_ANSWER_OCCURRENCES = [
    {"光": 0},
    {"太陽": 0},
    {"光源": 0},
    {"月": 0},
    {"光源": 0},
    {"かげ": 0, "影": 0},
    {"直進": 0},
    {"上下左右": 0},
    {"反射": 0},
    {"反射": 1},
    {"反射": 1},
]
ROLE_CASES = {
    "non_model": ("containsNonModelAnswers", "ai_proposed"),
    "model": ("containsModelAnswers", "provided_model_answer"),
}
ALLOWED_SAFETY_REVIEW_CODES = {
    "question.additional_placeholders_redacted",
}
HEADER_PATTERN = re.compile(
    r"(?:氏名|名前|学籍番号|得点欄|採点欄|先生印|4乙)"
)
BRACKET_PATTERN = re.compile(r"[［\[]([^］\]]*)[］\]]")
CRITICAL_OCR_PATTERN = re.compile(
    r"(?:は(?:い|じ)(?:か)?(?:は?ね)?返|といいう)"
)


def login() -> ApiClient:
    client = ApiClient()
    client.request(
        "POST",
        "/auth/login",
        {"username": "guide-admin", "password": "GuideDemo!2026"},
    )
    for cookie in client.cookies:
        if cookie.name == "__Host-OokiSession" and cookie.domain == "127.0.0.1":
            cookie.secure = False
    return client


def create_run(
    client: ApiClient,
    upload_id: str,
    mode: str,
    run_number: int,
) -> tuple[str, str]:
    source_role, _ = ROLE_CASES[mode]
    template = client.request(
        "POST",
        "/templates",
        {
            "title": f"実画像穴埋め精度検証_{mode}_{run_number}",
            "subject": "理科",
            "category": "光・穴埋め",
            "gradeLabel": "小学生",
            "course": "精度検証（公開禁止）",
            "defaultPointsMilli": 1_000,
        },
    )
    template_id = template["id"]
    version = client.request(
        "POST",
        f"/templates/{template_id}/versions",
        {"sourceVersionId": None},
    )
    version_id = version["id"]
    version_path = f"/templates/{template_id}/versions/{version_id}"
    client.request(
        "POST",
        f"{version_path}/sources",
        {
            "uploadId": upload_id,
            "sourceRole": source_role,
            "displayName": "実データ_理科_光_記入済み答案.png",
        },
    )
    client.request(
        "POST",
        f"{version_path}:generateDraft",
        {"priority": "expedite"},
    )
    return template_id, version_id


def wait_for_generation(
    client: ApiClient,
    template_id: str,
    version_id: str,
    timeout_seconds: int,
) -> dict[str, Any]:
    generation_path = (
        f"/templates/{template_id}/versions/{version_id}/generation"
    )
    deadline = time.monotonic() + timeout_seconds
    latest: dict[str, Any] = {}
    while time.monotonic() < deadline:
        latest = client.request("GET", generation_path)
        state = latest.get("state")
        if state in {"completed", "failed", "blocked", "cancelled"}:
            return latest
        time.sleep(2)
    raise TimeoutError(
        f"Template generation did not finish in {timeout_seconds}s; "
        f"last state={latest.get('state')}"
    )


def evaluate_run(
    client: ApiClient,
    mode: str,
    run_number: int,
    template_id: str,
    version_id: str,
    generation: dict[str, Any],
) -> dict[str, Any]:
    version_path = f"/templates/{template_id}/versions/{version_id}"
    detail = client.request("GET", version_path)
    questions = sorted(detail.get("questions", []), key=lambda item: item["order"])
    orders = [question["order"] for question in questions]
    labels = [question["displayLabel"] for question in questions]
    answers = [question.get("canonicalAnswer") for question in questions]
    accepted_answer_sets = [
        {
            answer.get("text")
            for answer in question.get("acceptedAnswers", [])
            if answer.get("text")
        }
        for question in questions
    ]
    provenances = [question.get("answerProvenance") for question in questions]
    placeholder_counts = [
        question["questionText"].count("［　］") for question in questions
    ]
    bracket_counts = [
        len(BRACKET_PATTERN.findall(question["questionText"]))
        for question in questions
    ]
    nonblank_brackets = [
        {
            "label": question["displayLabel"],
            "values": [
                value
                for value in BRACKET_PATTERN.findall(question["questionText"])
                if value.replace("　", "").strip()
            ],
        }
        for question in questions
    ]
    nonblank_brackets = [item for item in nonblank_brackets if item["values"]]
    visible_answer_leakage = []
    for index, question in enumerate(questions):
        if index >= len(ALLOWED_VISIBLE_ANSWER_OCCURRENCES):
            break
        for token, allowed_count in ALLOWED_VISIBLE_ANSWER_OCCURRENCES[index].items():
            actual_count = question["questionText"].count(token)
            if actual_count > allowed_count:
                visible_answer_leakage.append(
                    {
                        "label": question["displayLabel"],
                        "token": token,
                        "actualCount": actual_count,
                        "allowedCount": allowed_count,
                    }
                )
    header_extractions = [
        question["displayLabel"]
        for question in questions
        if HEADER_PATTERN.search(
            question["displayLabel"] + " " + question["questionText"]
        )
    ]
    critical_ocr_issues = [
        question["displayLabel"]
        for question in questions
        if CRITICAL_OCR_PATTERN.search(question["questionText"])
    ]
    expected_provenance = ROLE_CASES[mode][1]
    review_issues = detail.get("reviewIssues", [])
    permanent_review_ids = {
        question["id"]
        for question in questions
        if question.get("requiresReviewAlways")
    }
    review_codes_by_question: dict[str, set[str]] = {}
    for issue in review_issues:
        question_id = issue.get("questionId")
        code = issue.get("code")
        if question_id and code:
            review_codes_by_question.setdefault(question_id, set()).add(code)
    allowed_safety_review_ids = {
        question_id
        for question_id in permanent_review_ids
        if review_codes_by_question.get(question_id)
        and review_codes_by_question[question_id].issubset(
            ALLOWED_SAFETY_REVIEW_CODES
        )
    }

    bulk_result: dict[str, Any] | None = None
    if questions:
        bulk_result = client.request(
            "POST",
            f"{version_path}/questions:verifyProposals",
            {
                "selectionMode": "allNonBlocking",
                "revision": detail["revision"],
            },
        )
    post_verify_detail = client.request("GET", version_path)

    bulk_questions = (bulk_result or {}).get("questions", [])
    bulk_unverified_ids = {
        question["id"]
        for question in bulk_questions
        if not question.get("teacherVerified")
    }
    bulk_confirmation_mismatches = []
    for question in bulk_questions:
        question_id = question["id"]
        expected_verified = question_id not in permanent_review_ids
        question_verified = bool(question.get("teacherVerified"))
        answer_states = [
            bool(answer.get("teacherVerified"))
            for answer in question.get("acceptedAnswers", [])
        ]
        if (
            question_verified != expected_verified
            or any(state != expected_verified for state in answer_states)
        ):
            bulk_confirmation_mismatches.append(
                {
                    "questionId": question_id,
                    "label": question.get("displayLabel"),
                    "expectedVerified": expected_verified,
                    "questionVerified": question_verified,
                    "answerVerifiedStates": answer_states,
                }
            )
    bulk_verified_count = (bulk_result or {}).get("verifiedQuestionCount", 0)
    bulk_skipped_count = (bulk_result or {}).get("skippedQuestionCount", 0)
    bulk_coverage = bool(bulk_result) and (
        bulk_verified_count + bulk_skipped_count == len(questions)
    )
    safe_review_routing = (
        bulk_unverified_ids == permanent_review_ids
        and not bulk_confirmation_mismatches
        and permanent_review_ids.issubset(allowed_safety_review_ids)
        and len(permanent_review_ids) <= 1
        and bulk_skipped_count == len(permanent_review_ids)
    )
    draft_remains_unpublished = (
        post_verify_detail.get("state") == "draft"
        and post_verify_detail.get("contentHash") is None
        and post_verify_detail.get("publishedAt") is None
    )
    if mode == "model":
        answer_correctness = answers == EXPECTED_ANSWERS
    else:
        answer_correctness = (
            len(answers) == len(NON_MODEL_CANONICAL_OPTIONS)
            and all(
                answer in allowed
                for answer, allowed in zip(
                    answers,
                    NON_MODEL_CANONICAL_OPTIONS,
                    strict=True,
                )
            )
            and all(
                required.issubset(actual)
                for required, actual in zip(
                    NON_MODEL_REQUIRED_ACCEPTED_FORMS,
                    accepted_answer_sets,
                    strict=True,
                )
            )
        )

    checks = {
        "generationCompleted": generation.get("state") == "completed",
        "questionCount": len(questions) == 11,
        "questionOrder": orders == EXPECTED_ORDERS,
        "printedLabels": labels == EXPECTED_LABELS,
        "answerCorrectness": answer_correctness,
        "onePlaceholderPerQuestion": (
            placeholder_counts == [1] * 11
            and bracket_counts == [1] * 11
        ),
        "filledAnswerLeakageAbsent": (
            not nonblank_brackets and not visible_answer_leakage
        ),
        "administrativeFieldsExcluded": not header_extractions,
        "criticalOcrIssueAbsent": not critical_ocr_issues,
        "answerProvenance": provenances == [expected_provenance] * 11,
        "bulkCoverage": bulk_coverage,
        "unsafeBulkConfirmationAbsent": safe_review_routing,
        "draftRemainsUnpublished": draft_remains_unpublished,
    }
    return {
        "mode": mode,
        "runNumber": run_number,
        "templateId": template_id,
        "versionId": version_id,
        "generation": generation,
        "questionCount": len(questions),
        "orders": orders,
        "labels": labels,
        "answers": answers,
        "acceptedAnswerSets": [sorted(values) for values in accepted_answer_sets],
        "provenances": provenances,
        "placeholderCounts": placeholder_counts,
        "bracketCounts": bracket_counts,
        "nonblankBracketContent": nonblank_brackets,
        "visibleAnswerLeakage": visible_answer_leakage,
        "headerExtractions": header_extractions,
        "criticalOcrIssues": critical_ocr_issues,
        "requiresReviewAlwaysCount": sum(
            bool(question.get("requiresReviewAlways")) for question in questions
        ),
        "blockingWarnings": detail.get("blockingWarnings", []),
        "reviewIssues": review_issues,
        "safetyHeldQuestionIds": sorted(permanent_review_ids),
        "bulkVerification": bulk_result,
        "bulkConfirmationMismatches": bulk_confirmation_mismatches,
        "postVerificationState": {
            "state": post_verify_detail.get("state"),
            "contentHash": post_verify_detail.get("contentHash"),
            "publishedAt": post_verify_detail.get("publishedAt"),
        },
        "checks": checks,
        "passed": all(checks.values()),
        "questions": [
            {
                "label": question["displayLabel"],
                "text": question["questionText"],
                "answer": question.get("canonicalAnswer"),
                "provenance": question.get("answerProvenance"),
                "requiresReviewAlways": question.get("requiresReviewAlways"),
            }
            for question in questions
        ],
    }


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Run the live Japanese fill-blank template accuracy gate."
    )
    parser.add_argument("--image", type=Path, required=True)
    parser.add_argument("--runs-per-mode", type=int, default=1)
    parser.add_argument("--timeout-seconds", type=int, default=300)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if args.runs_per_mode < 1:
        raise ValueError("--runs-per-mode must be at least 1")
    image = args.image.resolve()
    if not image.is_file():
        raise FileNotFoundError(image)

    client = login()
    upload = client.upload(image, "templateSource")
    profiles = client.request("GET", "/admin/ai-task-profiles")["items"]
    template_profile = next(
        profile
        for profile in profiles
        if profile["taskType"] == "templateExtraction" and profile["active"]
    )
    evidence: dict[str, Any] = {
        "generatedAt": datetime.now(timezone.utc).isoformat(),
        "input": {
            "name": image.name,
            "sha256": hashlib.sha256(image.read_bytes()).hexdigest(),
            "bytes": image.stat().st_size,
            "uploadId": upload["uploadId"],
        },
        "profile": {
            "modelId": template_profile["modelId"],
            "promptVersion": template_profile["promptVersion"],
            "schemaVersion": template_profile["schemaVersion"],
            "processingStrategy": template_profile["processingStrategy"],
            "thinkingLevel": template_profile.get("thinkingLevel"),
            "mediaResolution": template_profile.get("mediaResolution"),
        },
        "expected": {
            "questionCount": 11,
            "labels": EXPECTED_LABELS,
            "modelAnswersExact": EXPECTED_ANSWERS,
            "nonModelCanonicalOptions": [
                sorted(values) for values in NON_MODEL_CANONICAL_OPTIONS
            ],
            "nonModelRequiredAcceptedForms": [
                sorted(values)
                for values in NON_MODEL_REQUIRED_ACCEPTED_FORMS
            ],
        },
        "runs": [],
    }
    for mode in ROLE_CASES:
        for run_number in range(1, args.runs_per_mode + 1):
            started = time.monotonic()
            template_id, version_id = create_run(
                client,
                upload["uploadId"],
                mode,
                run_number,
            )
            generation = wait_for_generation(
                client,
                template_id,
                version_id,
                args.timeout_seconds,
            )
            run = evaluate_run(
                client,
                mode,
                run_number,
                template_id,
                version_id,
                generation,
            )
            run["elapsedSeconds"] = round(time.monotonic() - started, 2)
            evidence["runs"].append(run)
            print(
                f"{mode} run {run_number}: "
                f"questions={run['questionCount']} passed={run['passed']}",
                flush=True,
            )

    evidence["summary"] = {
        "runCount": len(evidence["runs"]),
        "passedRunCount": sum(run["passed"] for run in evidence["runs"]),
        "allPassed": all(run["passed"] for run in evidence["runs"]),
    }
    rendered = json.dumps(evidence, ensure_ascii=False, indent=2) + "\n"
    if args.output:
        output = args.output.resolve()
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(rendered, encoding="utf-8")
        print(f"evidence={output}")
    else:
        print(rendered)


if __name__ == "__main__":
    main()
