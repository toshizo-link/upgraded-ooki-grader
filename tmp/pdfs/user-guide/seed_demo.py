from __future__ import annotations

import http.cookiejar
import json
import mimetypes
import os
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path
from typing import Any


BASE_URL = os.environ.get(
    "OOKI_GUIDE_API_URL",
    "http://127.0.0.1:7047/api/v1",
)
ORIGIN = os.environ.get("OOKI_GUIDE_ORIGIN", "http://localhost:5173")
FIXTURES = Path(__file__).resolve().parent / "fixtures"
STATE_PATH = Path(
    os.environ.get(
        "OOKI_GUIDE_STATE_PATH",
        str(Path(__file__).resolve().parent / "demo-state.json"),
    )
)


class ApiClient:
    def __init__(self) -> None:
        self.cookies = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(
            urllib.request.HTTPCookieProcessor(self.cookies)
        )
        self.csrf_token: str | None = None

    def request(
        self,
        method: str,
        path: str,
        payload: Any = ...,
        *,
        raw: bytes | None = None,
        content_type: str | None = None,
        extra_headers: dict[str, str] | None = None,
    ) -> Any:
        url = path if path.startswith("http") else f"{BASE_URL}{path}"
        headers = {
            "Accept": "application/json",
            "Origin": ORIGIN,
        }
        body: bytes | None = None
        if raw is not None:
            body = raw
            if content_type:
                headers["Content-Type"] = content_type
        elif payload is not ...:
            body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
            headers["Content-Type"] = "application/json; charset=utf-8"

        if method not in {"GET", "HEAD", "OPTIONS"} and not path.startswith("/auth/"):
            if not self.csrf_token:
                csrf_response = self.request("GET", "/auth/csrf")
                self.csrf_token = csrf_response.get("csrfToken") or csrf_response.get(
                    "token"
                )
                if not self.csrf_token:
                    raise RuntimeError(
                        "The host did not return a recognizable CSRF token."
                    )
            headers["X-CSRF-Token"] = self.csrf_token
            if method != "PATCH" or content_type != "application/offset+octet-stream":
                headers["Idempotency-Key"] = str(uuid.uuid4())
        if extra_headers:
            headers.update(extra_headers)

        request = urllib.request.Request(url, data=body, method=method, headers=headers)
        try:
            with self.opener.open(request, timeout=30) as response:
                data = response.read()
                if not data:
                    return None
                return json.loads(data.decode("utf-8"))
        except urllib.error.HTTPError as error:
            error_body = error.read().decode("utf-8", errors="replace")
            raise RuntimeError(
                f"{method} {path} failed with HTTP {error.code}: {error_body}"
            ) from error

    def upload(self, path: Path, purpose: str, test_session_id: str | None = None) -> dict:
        data = path.read_bytes()
        mime = mimetypes.guess_type(path.name)[0] or "application/octet-stream"
        created = self.request(
            "POST",
            "/uploads",
            {
                "purpose": purpose,
                "testSessionId": test_session_id,
                "fileName": path.name,
                "declaredMimeType": mime,
                "length": len(data),
            },
        )
        chunk_url = created["chunkUrl"]
        if chunk_url.startswith("/"):
            api_root = BASE_URL.removesuffix("/api/v1")
            chunk_url = f"{api_root}{chunk_url}"
        self.request(
            "PATCH",
            chunk_url,
            raw=data,
            content_type="application/offset+octet-stream",
            extra_headers={"Upload-Offset": "0"},
        )
        return self.request(
            "POST",
            f"/uploads/{created['uploadId']}:finalize",
            payload=...,
        )


def region(x: float, y_from_bottom: float, width: float, height: float) -> dict:
    page_width = 210.0
    page_height = 297.0
    return {
        "pageNumber": 1,
        "xMillionths": round(x / page_width * 1_000_000),
        "yMillionths": round(
            (page_height - (y_from_bottom + height)) / page_height * 1_000_000
        ),
        "widthMillionths": round(width / page_width * 1_000_000),
        "heightMillionths": round(height / page_height * 1_000_000),
        "rotationDegrees": 0,
    }


def question(
    label: str,
    order: int,
    text: str,
    maximum: int,
    canonical: str,
    question_region: dict,
    answer_region: dict,
    *,
    question_type: str = "exact_short_text",
    grading_mode: str = "transcribe_then_rules",
    always_review: bool = False,
    teacher_note: str | None = None,
) -> dict:
    return {
        "displayLabel": label,
        "order": order,
        "questionText": text,
        "questionType": question_type,
        "gradingMode": grading_mode,
        "maxPointsMilli": maximum,
        "pointIncrementMilli": 1000,
        "allowNonKanji": False,
        "canonicalAnswer": canonical,
        "answerProvenance": "teacher_entered",
        "questionRegion": question_region,
        "answerRegion": answer_region,
        "requiresReviewAlways": always_review,
        "teacherVerified": True,
        "teacherNote": teacher_note,
    }


def main() -> None:
    client = ApiClient()
    client.request(
        "POST",
        "/auth/login",
        {"username": "guide-admin", "password": "GuideDemo!2026"},
    )
    if STATE_PATH.exists():
        print(STATE_PATH.read_text(encoding="utf-8"))
        return

    students = [
        {
            "studentNumber": "S-001",
            "familyName": "桜井",
            "givenName": "花子",
            "familyNameKana": "サクライ",
            "givenNameKana": "ハナコ",
            "displayName": "桜井 花子",
            "gradeLabel": "中学1年",
            "course": "標準コース",
            "schoolClass": "1年A組",
            "notes": "ユーザーガイド用の架空データ",
        },
        {
            "studentNumber": "S-002",
            "familyName": "田中",
            "givenName": "悠太",
            "familyNameKana": "タナカ",
            "givenNameKana": "ユウタ",
            "displayName": "田中 悠太",
            "gradeLabel": "中学1年",
            "course": "標準コース",
            "schoolClass": "1年A組",
            "notes": "ユーザーガイド用の架空データ",
        },
        {
            "studentNumber": "S-003",
            "familyName": "鈴木",
            "givenName": "美咲",
            "familyNameKana": "スズキ",
            "givenNameKana": "ミサキ",
            "displayName": "鈴木 美咲",
            "gradeLabel": "中学1年",
            "course": "標準コース",
            "schoolClass": "1年A組",
            "notes": "ユーザーガイド用の架空データ",
        },
        {
            "studentNumber": "S-004",
            "familyName": "佐々木",
            "givenName": "蓮",
            "familyNameKana": "ササキ",
            "givenNameKana": "レン",
            "displayName": "佐々木 蓮",
            "gradeLabel": "中学1年",
            "course": "標準コース",
            "schoolClass": "1年B組",
            "notes": "ユーザーガイド用の架空データ",
        },
    ]
    current_students = client.request("GET", "/students?pageSize=200")
    by_student_number = {
        item["studentNumber"]: item for item in current_students["items"]
    }
    created_students = []
    for student in students:
        created_students.append(
            by_student_number.get(student["studentNumber"])
            or client.request("POST", "/students", student)
        )

    template_title = "中1社会 アジア州 確認テスト"
    current_templates = client.request("GET", "/templates?pageSize=200")
    template = next(
        (
            item
            for item in current_templates["items"]
            if item["title"] == template_title
        ),
        None,
    )
    if template is None:
        template = client.request(
            "POST",
            "/templates",
            {
                "title": template_title,
                "subject": "社会",
                "gradeLabel": "中学1年",
                "course": "標準コース",
                "category": "地理",
                "notes": "解答欄が問題用紙内に配置されたテストの例",
                "defaultPointsMilli": 10000,
            },
        )
    template_id = template["id"]
    version_id = template.get("activeVersionId")
    if not version_id:
        version = client.request(
            "POST",
            f"/templates/{template_id}/versions",
            {
                "targetTotalPointsMilli": 50000,
                "defaultPointsMilli": 10000,
                "defaultAllowNonKanji": False,
            },
        )
        version_id = version.get("id") or version["version"]["id"]

    detail = client.request(
        "GET", f"/templates/{template_id}/versions/{version_id}"
    )
    if not detail["sources"]:
        blank_upload = client.upload(
            FIXTURES / "asia-check-test-blank.pdf", "templateSource"
        )
        client.request(
            "POST",
            f"/templates/{template_id}/versions/{version_id}/sources",
            {
                "uploadId": blank_upload["uploadId"],
                "sourceRole": "blankTest",
                "displayName": "アジア州 確認テスト（空欄）",
            },
        )

    questions = [
        question(
            "1",
            1,
            "日本の首都を漢字で書きなさい。",
            8000,
            "東京",
            region(15, 227, 181, 8),
            region(20, 214, 176, 12),
        ),
        question(
            "2",
            2,
            "ASEAN（アセアン）を日本語で何というか。",
            10000,
            "東南アジア諸国連合",
            region(15, 202, 181, 8),
            region(20, 189, 176, 12),
        ),
        question(
            "3",
            3,
            "インドで最も多くの人が信仰している宗教を書きなさい。",
            8000,
            "ヒンドゥー教",
            region(15, 177, 181, 8),
            region(20, 164, 176, 12),
        ),
        question(
            "4",
            4,
            "東南アジアの気候として最も適切なものを選びなさい。",
            8000,
            "イ",
            region(15, 144, 181, 16),
            region(20, 131, 176, 12),
            question_type="multiple_choice",
            grading_mode="deterministic",
        ),
        question(
            "5",
            5,
            "資料から読み取れる輸出品の変化を、「工業化」という言葉を使って具体的に説明しなさい。",
            16000,
            "工業化が進み、天然ゴム中心から機械類中心へ変化した。",
            region(15, 70, 181, 54),
            region(20, 38, 176, 30),
            question_type="semantic_short_text",
            always_review=True,
            teacher_note="要点が同じなら表現の違いを認める。",
        ),
    ]
    existing_labels = {item["displayLabel"] for item in detail["questions"]}
    for item in questions:
        if item["displayLabel"] not in existing_labels:
            client.request(
                "POST",
                f"/templates/{template_id}/versions/{version_id}/questions",
                item,
            )

    detail = client.request(
        "GET", f"/templates/{template_id}/versions/{version_id}"
    )
    validation = client.request(
        "POST", f"/templates/{template_id}/versions/{version_id}:validate"
    )
    if not validation["valid"]:
        raise RuntimeError(
            "Template validation failed: "
            + json.dumps(validation, ensure_ascii=False, indent=2)
        )
    if detail["state"] == "draft":
        client.request(
            "POST",
            f"/templates/{template_id}/versions/{version_id}:publish",
            {"revision": detail["revision"]},
        )

    session = client.request(
        "POST",
        "/test-sessions",
        {
            "templateVersionId": version_id,
            "testDate": "2026-07-28",
            "sessionName": "7月28日 中1社会 アジア州",
            "classLabel": "1年A組",
            "course": "標準コース",
            "priority": "economy",
        },
    )
    session_id = session["id"]
    student_ids = [item["id"] for item in created_students[:3]]
    client.request(
        "PUT",
        f"/test-sessions/{session_id}/roster",
        {"studentIds": student_ids},
    )
    client.request("POST", f"/test-sessions/{session_id}:open")

    completed_uploads = []
    for filename in ["asia-check-test-hanako.pdf", "asia-check-test-yuta.pdf"]:
        completed_uploads.append(
            client.upload(
                FIXTURES / filename,
                "completedTest",
                test_session_id=session_id,
            )
        )

    state = {
        "students": created_students,
        "template": template,
        "templateId": template_id,
        "versionId": version_id,
        "session": session,
        "sessionId": session_id,
        "uploads": completed_uploads,
    }
    STATE_PATH.write_text(
        json.dumps(state, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(json.dumps(state, ensure_ascii=False, indent=2))

    # Give the in-process background worker a moment to index the uploaded scans.
    time.sleep(2)


if __name__ == "__main__":
    main()
