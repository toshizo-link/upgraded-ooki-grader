"""Isolated Windows published-binary upgrade smoke; never controls a service.

Use prepare with a 0.9.14 package, then finish with 0.9.16 against the same
explicit private work directory. OOKI_GEMINI_API_KEY is read from process memory
only. Evidence contains synthetic records, safe status codes, and file hashes.
This exercises application startup/migration, not the elevated updater UI.
"""
from __future__ import annotations

import argparse
import hashlib
import http.cookiejar
import json
import os
from pathlib import Path
import secrets
import socket
import sqlite3
import subprocess
import time
import uuid
import urllib.error
import urllib.request

from reportlab.pdfgen import canvas


USERNAME = "isolated-upgrade-teacher"
PASSWORD = "Smoke-Only-Account-2026!"
EXPECTED_MODEL = "gemini-3.8-flash"


class HttpResponse:
    def __init__(self, response):
        self.status_code = response.code
        self.headers = response.headers
        self.content = response.read()
        self.ok = 200 <= self.status_code < 300

    def json(self):
        return json.loads(self.content)


class HttpSession:
    def __init__(self):
        self.opener = urllib.request.build_opener(
            urllib.request.ProxyHandler({}),
            urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()),
        )

    def request(self, method, url, json=None, data=None, headers=None, timeout=10):
        headers = dict(headers or {})
        if json is not None:
            data = globals()["json"].dumps(json).encode("utf-8")
            headers["Content-Type"] = "application/json"
        request = urllib.request.Request(url, data=data, headers=headers, method=method)
        try:
            with self.opener.open(request, timeout=timeout) as response:
                return HttpResponse(response)
        except urllib.error.HTTPError as response:
            return HttpResponse(response)

    def get(self, url, timeout):
        return self.request("GET", url, timeout=timeout)

    def close(self):
        pass


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def write_json(path: Path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")


class Host:
    def __init__(self, package: Path, work: Path, label: str):
        self.package = package.resolve()
        self.work = work.resolve()
        self.label = label
        self.process = None
        self.log = None
        self.http = HttpSession()

    def __enter__(self):
        executable = self.package / "OokiGrader.Host.exe"
        assert executable.is_file(), "Published host executable is missing"
        with socket.socket() as s:
            s.bind(("127.0.0.1", 0))
            port = s.getsockname()[1]
        self.base = f"http://127.0.0.1:{port}"
        self.token = secrets.token_urlsafe(32)
        data = self.work / "data"
        env = dict(os.environ)
        # Provider key is used only by the test API client, never the host env.
        env.pop("OOKI_GEMINI_API_KEY", None)
        env.update({
            "ASPNETCORE_ENVIRONMENT": "Development",
            "ASPNETCORE_URLS": self.base,
            "AllowedHosts": "localhost;127.0.0.1",
            "Data__Root": str(data),
            "Data__ObjectStore": str(data / "objects"),
            "Data__Incoming": str(data / "incoming"),
            "Data__Reports": str(data / "reports"),
            "Security__AllowedOrigin": self.base,
            "Security__RequireSecureCookies": "false",
            "Security__BootstrapToken": self.token,
            "Backup__Enabled": "false",
            "Backup__DestinationRoot": "",
            "Backup__DestinationEncryptionConfirmed": "false",
            "Features__Ai.GeminiDirect": "true",
            "Features__Ai.OpenRouter": "false",
            "Features__Ai.TemplateGeneration": "true",
            "Features__Grading.Semantic": "false",
            "SchoolManagerAutomation__Enabled": "false",
        })
        self.log = (self.work / f"{self.label}-host.log").open("wb")
        self.process = subprocess.Popen(
            [str(executable), "--contentRoot", str(self.package)],
            cwd=self.package, env=env, stdout=self.log, stderr=subprocess.STDOUT,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        try:
            deadline = time.monotonic() + 95
            while time.monotonic() < deadline:
                if self.process.poll() is not None:
                    raise RuntimeError(f"{self.label} host exited before startup")
                try:
                    response = self.http.get(self.base + "/api/v1/bootstrap/status", timeout=2)
                    if response.status_code == 200:
                        return self
                except (urllib.error.URLError, TimeoutError, OSError):
                    pass
                time.sleep(.5)
            raise RuntimeError(f"{self.label} startup timed out")
        except BaseException:
            self.__exit__(None, None, None)
            raise

    def __exit__(self, *unused):
        self.http.close()
        if self.process is not None and self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=20)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=10)
        if self.log:
            self.log.close()

    def api(self, method, route, body=None, *, raw=None, extra=None, tolerate=()):
        headers = {"Origin": self.base, "Idempotency-Key": str(uuid.uuid4())}
        if getattr(self, "csrf", None):
            headers["X-CSRF-Token"] = self.csrf
        headers.update(extra or {})
        response = self.http.request(
            method, self.base + route, json=body if raw is None else None,
            data=raw, headers=headers, timeout=330,
        )
        if not response.ok and response.status_code not in tolerate:
            try:
                problem = response.json()
                safe = {k: problem.get(k) for k in ("code", "title", "detail")}
            except ValueError:
                safe = {"code": "non_json_response"}
            raise RuntimeError(f"{method} {route}: HTTP {response.status_code} {safe}")
        return response

    def json(self, method, route, body=None, **kwargs):
        response = self.api(method, route, body, **kwargs)
        return response.json() if response.content else None

    def login(self):
        self.api("POST", "/api/v1/auth/login", {"username": USERNAME, "password": PASSWORD})
        self.csrf = self.json("GET", "/api/v1/auth/csrf")["token"]

    def upload(self, pdf: Path, purpose: str, session_id=None):
        data = pdf.read_bytes()
        created = self.json("POST", "/api/v1/uploads", {
            "purpose": purpose, "testSessionId": session_id,
            "fileName": pdf.name, "declaredMimeType": "application/pdf",
            "length": len(data), "expectedSha256": digest(data),
        })
        self.api("PATCH", created["chunkUrl"], raw=data, extra={
            "Content-Type": "application/offset+octet-stream", "Upload-Offset": "0",
        })
        return self.json("POST", f"/api/v1/uploads/{created['uploadId']}:finalize", {})


def wait_json(host, route, predicate, timeout=70):
    deadline = time.monotonic() + timeout
    last = None
    while time.monotonic() < deadline:
        response = host.api("GET", route, tolerate=(409,))
        last = response.json()
        if response.ok and predicate(last):
            return last
        time.sleep(.6)
    raise RuntimeError(f"Timed out: {route}; safe final state {last.get('state') if isinstance(last,dict) else None}")


def fixture_pdf(path: Path):
    pdf = canvas.Canvas(str(path), pagesize=(595,842))
    pdf.setTitle("Synthetic upgrade retention fixture")
    pdf.drawString(55, 785, "SYNTHETIC UPGRADE TEST - NOT A REAL STUDENT")
    pdf.drawString(55, 753, "Student: SMOKE-001 | Class: T00 | Date: 2026-09-30")
    pdf.drawString(55, 691, "Question 1: 1 + 1 = ____")
    pdf.drawString(55, 658, "Answer: 2")
    pdf.save()


def seed(host: Host, key: str):
    status = host.json("GET", "/api/v1/bootstrap/status")
    assert not status["completed"], "Prepare requires a fresh isolated data directory"
    host.api("POST", "/api/v1/bootstrap/complete", {
        "token": host.token, "username": USERNAME, "displayName": "検証専用教員",
        "password": PASSWORD, "schoolName": "更新検証専用校",
    })
    host.login()
    student = host.json("POST", "/api/v1/students", {
        "studentNumber":"SMOKE-001", "familyName":"検証", "givenName":"生徒",
        "familyNameKana":"ケンショウ", "givenNameKana":"セイト", "displayName":"検証 生徒",
        "gradeLabel":"小6", "course":"理科", "schoolClass":"T00", "notes":"合成データ",
    })
    template = host.json("POST", "/api/v1/templates", {
        "title":"更新検証 1問", "subject":"理科", "gradeLabel":"小6", "category":"その他",
        "course":"理科", "notes":"合成教材", "defaultPointsMilli":1000,
    })
    tid = template["id"]
    version = host.json("POST", f"/api/v1/templates/{tid}/versions", {})
    vid = version["id"]
    pdf = host.work / "synthetic-original.pdf"
    fixture_pdf(pdf)
    source_upload = host.upload(pdf, "templateSource")
    source = host.json("POST", f"/api/v1/templates/{tid}/versions/{vid}/sources", {
        "uploadId":source_upload["uploadId"], "sourceRole":"containsModelAnswers",
        "displayName":"合成教材と模範解答",
    })
    question = host.json("POST", f"/api/v1/templates/{tid}/versions/{vid}/questions", {
        "displayLabel":"1", "order":1, "questionText":"1 + 1 = ?", "questionType":"exact_short_text",
        "gradingMode":"manual", "maxPointsMilli":1000, "pointIncrementMilli":1000,
        "allowNonKanji":True, "canonicalAnswer":"2", "answerProvenance":"teacher_entered",
        "teacherVerified":True, "requiresReviewAlways":True,
        "acceptedAnswers":[{"text":"2","variantType":"canonical","provenance":"teacher_entered","teacherVerified":True}],
    })
    current = host.api("GET", f"/api/v1/templates/{tid}/versions/{vid}")
    published = host.json("POST", f"/api/v1/templates/{tid}/versions/{vid}:publish", {
        "revision":current.json()["revision"], "testDate":"2026-09-30", "classLabel":"T00",
    }, extra={"If-Match":current.headers["ETag"]})
    session = published.get("testSession") or published.get("session")
    sid = session["id"] if session else published.get("testSessionId")
    if not sid:
        session = host.json("POST", "/api/v1/test-sessions", {
            "title":"更新検証 1問", "templateVersionId":vid, "testDate":"2026-09-30",
            "gradeLabel":"小6", "course":"理科", "openImmediately":True,
        })
        sid = session["id"]
    submission_upload = host.upload(pdf, "completedTest", sid)
    sub_id = submission_upload["submissionId"]
    wait_json(host, f"/api/v1/submissions/{sub_id}", lambda x:x.get("processingState") != "preprocessing")
    current = host.api("GET", f"/api/v1/submissions/{sub_id}")
    host.json("POST", f"/api/v1/submissions/{sub_id}:assignStudent", {
        "studentId":student["id"], "sourceRevision":current.json()["revision"],
        "reasonCode":"teacher_confirmed", "note":"合成答案の更新保持確認",
    }, extra={"If-Match":current.headers["ETag"]})
    workspace = wait_json(host, f"/api/v1/submissions/{sub_id}/grading-workspace", lambda x:bool(x.get("results")))
    result = workspace["results"][0]
    host.json("POST", f"/api/v1/submissions/{sub_id}/results/{result['resultId']}:override", {
        "sourceResultRevision":result["sourceResultRevision"], "awardedPointsMilli":1000, "outcome":"correct",
        "transcriptionCorrection":"2", "reasonCode":"teacher_confirmed", "note":"合成答案の正答",
    })
    current = host.api("GET", f"/api/v1/submissions/{sub_id}")
    finalized = host.json("POST", f"/api/v1/submissions/{sub_id}:finalize", {
        "sourceRevision":current.json()["revision"],
    }, extra={"If-Match":current.headers["ETag"]})
    export = host.json("POST", f"/api/v1/results/{sub_id}/exports", {})
    export = wait_json(host, f"/api/v1/exports/{export['id']}", lambda x:x["state"] == "verified")
    connection = host.json("POST", "/api/v1/admin/ai-connections", {
        "provider":"geminiDirect", "modelId":"gemini-3.5-flash-lite", "apiKey":key,
        "timeoutSeconds":120, "concurrencyLimit":1, "testAndEnable":False,
    })
    host.json("PUT", "/api/v1/admin/school-manager", {
        "baseUrl":"https://fsm.flens.jp/", "username":"synthetic-disabled-upgrade-test",
        "password":"Synthetic-Only-Not-A-Real-Credential!", "enabled":False, "dryRun":True,
    })
    return {"studentId":student["id"],"templateId":tid,"versionId":vid,"sessionId":sid,
            "submissionId":sub_id,"exportId":export["id"],"connectionId":connection["id"],
            "originalFixtureSha256":digest(pdf.read_bytes())}


def snapshot(host: Host, ids):
    routes = {
        "teacher":"/api/v1/auth/me",
        "student":f"/api/v1/students/{ids['studentId']}",
        "template":f"/api/v1/templates/{ids['templateId']}",
        "version":f"/api/v1/templates/{ids['templateId']}/versions/{ids['versionId']}",
        "session":f"/api/v1/test-sessions/{ids['sessionId']}",
        "submission":f"/api/v1/submissions/{ids['submissionId']}",
        "result":f"/api/v1/results/{ids['submissionId']}",
        "export":f"/api/v1/exports/{ids['exportId']}",
        "schoolManagerSettings":"/api/v1/admin/school-manager",
        "schoolManagerDeliveries":"/api/v1/admin/school-manager/deliveries",
    }
    entities = {name:host.json("GET",route) for name,route in routes.items()}
    # A re-login creates a new session expiration, without changing the user.
    entities["teacher"].pop("expiresAt",None)
    entities["teacher"].pop("sessionExpiresAt",None)
    original = host.api("GET",f"/api/v1/submissions/{ids['submissionId']}/original-pdf").content
    report = host.api("GET",f"/api/v1/exports/{ids['exportId']}/file").content
    assert digest(original) == ids["originalFixtureSha256"]
    connection = next(c for c in host.json("GET","/api/v1/admin/ai-connections")["items"] if c["id"]==ids["connectionId"])
    profiles = host.json("GET","/api/v1/admin/ai-task-profiles")
    file_hashes = {str(p.relative_to(host.work/"data")):digest(p.read_bytes()) for p in (host.work/"data").rglob("*.secret")}
    db = sqlite3.connect(f"file:{(host.work/'data/ooki-grader.db').as_posix()}?mode=ro",uri=True)
    db.row_factory = sqlite3.Row
    credential_metadata = dict(db.execute(
        "SELECT key_fingerprint, secret_reference, credential_revision FROM ai_connection WHERE id=?",
        (ids["connectionId"],),
    ).fetchone())
    db.close()
    return {"entities":entities,"originalPdfSha256":digest(original),"reportPdfSha256":digest(report),
            "connection":connection,"profiles":profiles,"protectedCredentialFileHashes":file_hashes,
            "credentialMetadata":credential_metadata}


def generate_client_template(host: Host, client_pdf: Path):
    """Run the production batch/unit worker against the client's supplied PDF."""
    source = host.upload(client_pdf.resolve(), "templateSource")
    batch = host.json("POST", "/api/v1/template-generation-batches", {
        "sourceId":source["uploadId"], "expectedSourceRowVersion":source["rowVersion"],
        "testType":"other", "subject":"理科", "answerStyle":"fillBlank",
    })
    batch = host.json("POST", f"/api/v1/template-generation-batches/{batch['id']}/generate", {
        "expectedRowVersion":batch["rowVersion"],
    })
    final = wait_json(host, f"/api/v1/template-generation-batches/{batch['id']}",
                      lambda x:x["status"] in ("needsFinalCheck","completed","failed","cancelled"),timeout=430)
    write_json(host.work/"client-template-batch-result.json",final)
    units = final["units"]
    db = sqlite3.connect(f"file:{(host.work/'data/ooki-grader.db').as_posix()}?mode=ro",uri=True)
    rows = db.execute("SELECT extraction_draft_json FROM template_generation_unit WHERE batch_id=? ORDER BY sequence", (batch["id"],)).fetchall()
    db.close()
    drafts = [json.loads(row[0]) for row in rows if row[0]]
    issues = []
    for draft in drafts:
        issues.extend(draft.get("reviewIssues",[]))
        for page in draft.get("pages",[]):
            for question in page.get("questions",[]):
                issues.extend(question.get("reviewIssues",[]))
    write_json(host.work/"client-template-validated-drafts.json",drafts)
    summary = {
        "status":final["status"],"sourcePdfSha256":digest(client_pdf.read_bytes()),
        "sourcePages":final["sourcePageCount"],"units":len(units),
        "questionCount":sum(u["questionCount"] for u in units),
        "failedUnits":final["failedUnitCount"],"lastErrorCode":final.get("lastErrorCode"),
        "allUnitsExtracted":bool(units) and all(u["status"] in ("extracted","confirmed") for u in units),
        "blockingExtractionReviewIssues":sum(bool(i.get("blocking")) for i in issues),
        "persistedDraftQuestionCount":sum(len(p["questions"]) for d in drafts for p in d["pages"]),
    }
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("phase",choices=("prepare","finish"))
    parser.add_argument("--package",type=Path,required=True)
    parser.add_argument("--work",type=Path,required=True)
    parser.add_argument("--recheck",action="store_true",help="Run one real capability recheck if startup probe is blocked")
    parser.add_argument("--client-pdf",type=Path,help="After retention checks, run the production other/fillBlank template worker")
    parser.add_argument("--expected-question-count",type=int,help="Require exact persisted question count from independently counted answer slots")
    args = parser.parse_args()
    work = args.work.resolve()
    assert work != Path(os.environ.get("ProgramData","C:/ProgramData")).resolve()
    assert "programdata" not in str(work).lower(), "Real ProgramData is outside scope"
    assert work.name.startswith("isolated-upgrade-"), "Use a clearly named isolated test directory"
    work.mkdir(parents=True,exist_ok=True)
    state = work/"state.json"
    if args.phase == "prepare":
        assert not state.exists(), "Fresh prepare work directory required"
        key = os.environ.get("OOKI_GEMINI_API_KEY")
        assert key, "Provide authorized provider key in OOKI_GEMINI_API_KEY (process environment only)"
        with Host(args.package,work,"old-0.9.14") as host:
            ids = seed(host,key)
            before = snapshot(host,ids)
            write_json(state,{"ids":ids,"before":before})
        print(json.dumps({"phase":"prepare","state":"passed","syntheticFinalizedResults":1,"originalAndReportPdfStored":True}))
    else:
        data = json.loads(state.read_text(encoding="utf-8"))
        with Host(args.package,work,"new-0.9.16") as host:
            host.login()
            after = snapshot(host,data["ids"])
            startup_probe = after["connection"].get("lastCapabilityProbe")
            recheck = None
            if args.recheck and after["connection"].get("state") != "active":
                response = host.api("POST",f"/api/v1/admin/ai-connections/{data['ids']['connectionId']}:test",{})
                recheck = response.json()
                after = snapshot(host,data["ids"])
            before = data["before"]
            before["entities"]["teacher"].pop("sessionExpiresAt",None)
            checks = {
                "syntheticEntitiesUnchanged":before["entities"] == after["entities"],
                "originalPdfUnchanged":before["originalPdfSha256"] == after["originalPdfSha256"],
                "resultPdfUnchanged":before["reportPdfSha256"] == after["reportPdfSha256"],
                "encryptedKeyFilesUnchanged":before["protectedCredentialFileHashes"] == after["protectedCredentialFileHashes"],
                "keyFingerprintUnchanged":bool(before["credentialMetadata"]["key_fingerprint"]) and before["credentialMetadata"]["key_fingerprint"] == after["credentialMetadata"]["key_fingerprint"],
                "credentialReferenceAndRevisionUnchanged":before["credentialMetadata"] == after["credentialMetadata"],
                "modelAutomaticallySelected":after["connection"]["modelId"] == EXPECTED_MODEL,
            }
            profiles = after["profiles"].get("items",[])
            checks["fourProfilesSelectModel"] = len(profiles)==4 and all(p["modelId"]==EXPECTED_MODEL for p in profiles)
            db = sqlite3.connect(f"file:{(work/'data/ooki-grader.db').as_posix()}?mode=ro",uri=True)
            migration_count = db.execute("SELECT COUNT(*) FROM audit_event WHERE event_type='ai.upgrade.gemini38.0_9_16'").fetchone()[0]
            db.close()
            checks["migrationRecordedOnce"] = migration_count == 1
            evidence = {"state":"passed" if all(checks.values()) else "failed", "checks":checks,"after":after,
                        "startupProbe":startup_probe,"manualRecheck":recheck,
                        "scope":"Published host startup transition; no installed service or elevated updater was used"}
            write_json(work/"upgrade-result.json",evidence)
            assert all(checks.values()), checks
            if args.client_pdf:
                assert after["connection"].get("state") == "active", "Live template generation requires successful capability probe"
                client_generation = generate_client_template(host,args.client_pdf)
                evidence["clientTemplateGeneration"] = client_generation
                write_json(work/"upgrade-result.json",evidence)
                assert client_generation["allUnitsExtracted"] and client_generation["questionCount"] > 0, client_generation
                assert client_generation["blockingExtractionReviewIssues"] == 0, client_generation
                if args.expected_question_count is not None:
                    assert client_generation["questionCount"] == args.expected_question_count, client_generation
                    assert client_generation["persistedDraftQuestionCount"] == args.expected_question_count, client_generation
        # A second new-binary startup must not repeat the one-time migration.
        with Host(args.package,work,"new-restart-0.9.16") as host:
            host.login()
            again = snapshot(host,data["ids"])
            assert again["connection"]["modelId"]==EXPECTED_MODEL
        db=sqlite3.connect(f"file:{(work/'data/ooki-grader.db').as_posix()}?mode=ro",uri=True)
        count=db.execute("SELECT COUNT(*) FROM audit_event WHERE event_type='ai.upgrade.gemini38.0_9_16'").fetchone()[0]
        db.close()
        assert count==1
        evidence["checks"]["secondStartupDoesNotRepeatMigration"]=True
        write_json(work/"upgrade-result.json",evidence)
        print(json.dumps({"phase":"finish","state":"passed","checks":evidence["checks"],
                          "capabilityProbe":after["connection"].get("lastCapabilityProbe"),
                          "connectionState":after["connection"].get("state"),
                          "activeProfiles":sum(bool(p.get("active")) for p in profiles),"profiles":len(profiles),
                          "clientTemplateGeneration":evidence.get("clientTemplateGeneration")}))


if __name__ == "__main__":
    main()
