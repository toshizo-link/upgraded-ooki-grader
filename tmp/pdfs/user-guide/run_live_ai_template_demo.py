from __future__ import annotations

import argparse
import time

from seed_demo import ApiClient, FIXTURES


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Attach the fictional Japanese exam and run live Gemini template extraction."
    )
    parser.add_argument("--template-id", required=True)
    parser.add_argument("--version-id", required=True)
    parser.add_argument(
        "--recover-job-id",
        help="Cancel a retry-waiting demo job and queue it immediately.",
    )
    args = parser.parse_args()

    client = ApiClient()
    client.request(
        "POST",
        "/auth/login",
        {"username": "guide-admin", "password": "GuideDemo!2026"},
    )
    # The browser treats localhost as a secure context. urllib does not send a
    # Secure cookie over the loopback HTTP URL used by this isolated demo.
    for cookie in client.cookies:
        if cookie.name == "__Host-OokiSession" and cookie.domain == "127.0.0.1":
            cookie.secure = False

    detail_path = (
        f"/templates/{args.template_id}/versions/{args.version_id}"
    )
    if args.recover_job_id:
        client.request(
            "POST",
            f"/admin/jobs/{args.recover_job_id}:cancel",
            {},
        )
        client.request(
            "POST",
            f"/admin/jobs/{args.recover_job_id}:retry",
            {},
        )

    detail = client.request("GET", detail_path)
    if not detail["sources"]:
        upload = client.upload(
            FIXTURES / "asia-check-test-blank.pdf",
            "templateSource",
        )
        client.request(
            "POST",
            f"{detail_path}/sources",
            {
                "uploadId": upload["uploadId"],
                "sourceRole": "blankTest",
                "displayName": "アジア州 確認テスト（空欄）",
            },
        )

    if not detail["questions"] and detail.get("state") == "draft":
        client.request(
            "POST",
            f"{detail_path}:generateDraft",
            {"priority": "expedite"},
        )

    for _ in range(90):
        detail = client.request("GET", detail_path)
        state = detail.get("state")
        questions = detail.get("questions", [])
        print(f"state={state} questions={len(questions)}", flush=True)
        if state in {"draft", "validating"} and questions:
            return
        if state in {"failed", "blocked", "cancelled"}:
            raise RuntimeError(f"Template extraction stopped with state={state}")
        time.sleep(2)

    raise TimeoutError("Gemini template extraction did not finish within 180 seconds.")


if __name__ == "__main__":
    main()
