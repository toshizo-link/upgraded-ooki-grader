from __future__ import annotations

import argparse
import base64
import hashlib
import json
import mimetypes
import os
import re
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


BASE_URL = "https://openrouter.ai/api/v1"
DEFAULT_MODEL = "deepseek/deepseek-v4-flash"
KEY_PATTERN = re.compile(r"sk-or-v1-[A-Za-z0-9_-]+")
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


def sanitized(value: Any, secret: str) -> Any:
    if isinstance(value, dict):
        return {key: sanitized(item, secret) for key, item in value.items()}
    if isinstance(value, list):
        return [sanitized(item, secret) for item in value]
    if isinstance(value, str):
        cleaned = value.replace(secret, "[redacted]") if secret else value
        return KEY_PATTERN.sub("[redacted]", cleaned)
    return value


def request_json(
    method: str,
    url: str,
    *,
    api_key: str | None = None,
    payload: dict[str, Any] | None = None,
    timeout_seconds: int = 180,
) -> dict[str, Any]:
    body = None if payload is None else json.dumps(payload).encode("utf-8")
    headers = {"Accept": "application/json"}
    if body is not None:
        headers["Content-Type"] = "application/json"
    if api_key:
        headers["Authorization"] = f"Bearer {api_key}"
        headers["X-OpenRouter-Title"] = "Ooki Grader accuracy evaluation"
    request = urllib.request.Request(url, data=body, headers=headers, method=method)
    started = time.monotonic()
    try:
        with urllib.request.urlopen(request, timeout=timeout_seconds) as response:
            raw = response.read(2_000_000)
            status = response.status
    except urllib.error.HTTPError as error:
        raw = error.read(2_000_000)
        status = error.code
    elapsed_ms = round((time.monotonic() - started) * 1000)
    try:
        parsed: Any = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        parsed = {"unparsed": raw[:500].decode("utf-8", errors="replace")}
    return {
        "status": status,
        "elapsedMs": elapsed_ms,
        "body": sanitized(parsed, api_key or ""),
    }


def response_summary(result: dict[str, Any]) -> dict[str, Any]:
    body = result.get("body") if isinstance(result.get("body"), dict) else {}
    choice = None
    choices = body.get("choices") if isinstance(body, dict) else None
    if isinstance(choices, list) and choices:
        choice = choices[0]
    message = choice.get("message") if isinstance(choice, dict) else None
    content = message.get("content") if isinstance(message, dict) else None
    error = body.get("error") if isinstance(body, dict) else None
    return {
        "status": result["status"],
        "elapsedMs": result["elapsedMs"],
        "requestId": body.get("id") if isinstance(body, dict) else None,
        "actualModel": body.get("model") if isinstance(body, dict) else None,
        "provider": body.get("provider") if isinstance(body, dict) else None,
        "finishReason": choice.get("finish_reason") if isinstance(choice, dict) else None,
        "content": content if isinstance(content, str) else None,
        "usage": body.get("usage") if isinstance(body, dict) else None,
        "error": error,
    }


def schema(name: str, properties: dict[str, Any], required: list[str]) -> dict[str, Any]:
    return {
        "type": "json_schema",
        "json_schema": {
            "name": name,
            "strict": True,
            "schema": {
                "type": "object",
                "properties": properties,
                "required": required,
                "additionalProperties": False,
            },
        },
    }


def provider_preferences() -> dict[str, Any]:
    return {
        "require_parameters": True,
        "data_collection": "deny",
        "zdr": True,
    }


def parse_structured_content(content: str | None) -> dict[str, Any] | None:
    if not content:
        return None
    candidate = content.strip()
    if candidate.startswith("```"):
        candidate = re.sub(r"^```(?:json)?\s*", "", candidate)
        candidate = re.sub(r"\s*```$", "", candidate)
    try:
        parsed = json.loads(candidate)
    except json.JSONDecodeError:
        return None
    return parsed if isinstance(parsed, dict) else None


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Safely probe OpenRouter DeepSeek text and image eligibility."
    )
    parser.add_argument("--image", type=Path, required=True)
    parser.add_argument("--model", default=DEFAULT_MODEL)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    api_key = os.environ.get("OPENROUTER_API_KEY", "").strip()
    if not api_key:
        raise SystemExit("OPENROUTER_API_KEY is required in the process environment.")
    if not args.image.is_file():
        raise SystemExit(f"Image does not exist: {args.image}")

    models_result = request_json("GET", f"{BASE_URL}/models")
    model_records = (
        models_result["body"].get("data", [])
        if isinstance(models_result.get("body"), dict)
        else []
    )
    matches = [
        item
        for item in model_records
        if isinstance(item, dict)
        and (
            item.get("id") == args.model
            or item.get("canonical_slug") == args.model
        )
    ]
    model_record = matches[0] if matches else None
    architecture = model_record.get("architecture", {}) if model_record else {}
    supported_parameters = model_record.get("supported_parameters", []) if model_record else []

    common = {
        "model": args.model,
        "provider": provider_preferences(),
        "reasoning": {"effort": "high", "exclude": True},
        "stream": False,
    }
    text_payload = {
        **common,
        "messages": [
            {
                "role": "user",
                "content": "Return status=ok and number=4. Output only the requested JSON.",
            }
        ],
        "response_format": schema(
            "ooki_openrouter_text_probe",
            {
                "status": {"type": "string", "enum": ["ok"]},
                "number": {"type": "integer", "enum": [4]},
            },
            ["status", "number"],
        ),
        "max_tokens": 200,
    }
    text_raw = request_json(
        "POST",
        f"{BASE_URL}/chat/completions",
        api_key=api_key,
        payload=text_payload,
    )
    text_probe = response_summary(text_raw)
    text_parsed = parse_structured_content(text_probe.get("content"))
    text_probe["schemaPassed"] = text_parsed == {"status": "ok", "number": 4}
    text_probe.pop("content", None)

    mime_type = mimetypes.guess_type(args.image.name)[0] or "image/png"
    data_url = (
        f"data:{mime_type};base64,"
        + base64.b64encode(args.image.read_bytes()).decode("ascii")
    )
    image_payload = {
        **common,
        "messages": [
            {
                "role": "user",
                "content": [
                    {
                        "type": "text",
                        "text": (
                            "この日本語理科プリントの手書き穴埋め解答を、紙面の上から順に"
                            "11個すべて転記してください。説明は不要です。"
                        ),
                    },
                    {"type": "image_url", "image_url": {"url": data_url}},
                ],
            }
        ],
        "response_format": schema(
            "ooki_fill_blank_probe",
            {
                "answers": {
                    "type": "array",
                    "minItems": 11,
                    "maxItems": 11,
                    "items": {"type": "string"},
                }
            },
            ["answers"],
        ),
        "max_tokens": 1200,
    }
    image_raw = request_json(
        "POST",
        f"{BASE_URL}/chat/completions",
        api_key=api_key,
        payload=image_payload,
    )
    image_probe = response_summary(image_raw)
    image_parsed = parse_structured_content(image_probe.get("content"))
    actual_answers = image_parsed.get("answers") if image_parsed else None
    image_probe["answers"] = actual_answers
    image_probe["exactAnswerCount"] = (
        sum(
            str(actual).strip() == expected
            for actual, expected in zip(actual_answers, EXPECTED_ANSWERS)
        )
        if isinstance(actual_answers, list)
        else 0
    )
    image_probe["allAnswersExact"] = actual_answers == EXPECTED_ANSWERS
    image_probe.pop("content", None)

    evidence = {
        "schema": "ooki-openrouter-deepseek-capability/v1",
        "createdAt": datetime.now(timezone.utc).isoformat(),
        "requestedModel": args.model,
        "modelMetadata": {
            "found": model_record is not None,
            "id": model_record.get("id") if model_record else None,
            "canonicalSlug": model_record.get("canonical_slug") if model_record else None,
            "inputModalities": architecture.get("input_modalities", []),
            "outputModalities": architecture.get("output_modalities", []),
            "contextLength": model_record.get("context_length") if model_record else None,
            "pricing": model_record.get("pricing") if model_record else None,
            "supportsResponseFormat": "response_format" in supported_parameters,
            "supportsStructuredOutputs": "structured_outputs" in supported_parameters,
        },
        "privacyRouting": {
            "dataCollection": "deny",
            "zeroDataRetentionRequired": True,
            "providerParameterSupportRequired": True,
        },
        "sourceImage": {
            "name": args.image.name,
            "bytes": args.image.stat().st_size,
            "sha256": hashlib.sha256(args.image.read_bytes()).hexdigest(),
        },
        "textStructuredProbe": text_probe,
        "actualImageProbe": image_probe,
        "eligibleForCurrentOokiVisualWorkflow": (
            "image" in architecture.get("input_modalities", [])
            and image_probe.get("allAnswersExact") is True
        ),
        "secretPersisted": False,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(sanitized(evidence, api_key), ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    print(
        json.dumps(
            {
                "model": args.model,
                "inputModalities": evidence["modelMetadata"]["inputModalities"],
                "textStatus": text_probe["status"],
                "textSchemaPassed": text_probe["schemaPassed"],
                "imageStatus": image_probe["status"],
                "imageExactAnswerCount": image_probe["exactAnswerCount"],
                "eligible": evidence["eligibleForCurrentOokiVisualWorkflow"],
                "output": str(args.output),
            },
            ensure_ascii=False,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
