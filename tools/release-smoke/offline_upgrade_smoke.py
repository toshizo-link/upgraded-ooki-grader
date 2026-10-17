"""Verify an isolated published-binary upgrade without any provider calls."""
import argparse
import json
from pathlib import Path
import sqlite3

from published_upgrade_smoke import Host, digest, seed, wait_json, write_json


def snapshot(host, ids):
    routes = {
        "teacher": "/api/v1/auth/me",
        "student": f"/api/v1/students/{ids['studentId']}",
        "template": f"/api/v1/templates/{ids['templateId']}",
        "version": f"/api/v1/templates/{ids['templateId']}/versions/{ids['versionId']}",
        "session": f"/api/v1/test-sessions/{ids['sessionId']}",
        "submission": f"/api/v1/submissions/{ids['submissionId']}",
        "result": f"/api/v1/results/{ids['submissionId']}",
        "export": f"/api/v1/exports/{ids['exportId']}",
        "schoolManager": "/api/v1/admin/school-manager",
    }
    entities = {name: host.json("GET", route) for name, route in routes.items()}
    entities["teacher"].pop("expiresAt", None)
    entities["teacher"].pop("sessionExpiresAt", None)
    return {
        "entities": entities,
        "originalPdf": digest(host.api("GET", f"/api/v1/submissions/{ids['submissionId']}/original-pdf").content),
        "resultPdf": digest(host.api("GET", f"/api/v1/exports/{ids['exportId']}/file").content),
        "protectedFiles": {str(p.relative_to(host.work / "data")): digest(p.read_bytes())
                           for p in (host.work / "data").rglob("*.secret")},
    }


def assert_retained(before, after):
    """New response fields may be added, but existing values must be retained."""
    if isinstance(before, dict):
        for key, value in before.items():
            assert key in after, f"Missing retained field: {key}"
            assert_retained(value, after[key])
    elif isinstance(before, list):
        assert len(before) == len(after)
        for old, new in zip(before, after):
            assert_retained(old, new)
    else:
        assert before == after, "An existing value changed during upgrade"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--old-package", type=Path, required=True)
    parser.add_argument("--new-package", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True)
    args = parser.parse_args()
    work = args.work.resolve()
    assert work.name.startswith("isolated-upgrade-")
    assert "programdata" not in str(work).lower()
    assert not work.exists(), "Use a new isolated directory for each test"
    work.mkdir(parents=True)
    with Host(args.old_package, work, "old-offline") as host:
        ids = seed(host, None)
        before = snapshot(host, ids)
    checks = {}
    with Host(args.new_package, work, "new-offline") as host:
        host.login()
        after = snapshot(host, ids)
        assert_retained(before, after)
        checks["existingRecordsAndPdfHashesRetained"] = True
        sid = ids["sessionId"]
        session = host.json("GET", f"/api/v1/test-sessions/{sid}")
        assert session.get("maximumMistakesToPass") is None
        host.json("PUT", f"/api/v1/test-sessions/{sid}/pass-criteria", {
            "maximumMistakesToPass": 4, "revision": session["revision"],
        })
        result = host.json("GET", f"/api/v1/results/{ids['submissionId']}")
        assert result["maximumMistakesToPass"] == 4
        assert result["mistakeCount"] == 0
        assert result["passFailStatus"] == "pass"
        checks["passCriterionAppliedToExistingResult"] = True
        export = host.json("POST", f"/api/v1/results/{ids['submissionId']}/exports", {})
        export = wait_json(host, f"/api/v1/exports/{export['id']}", lambda x: x["state"] == "verified")
        report = host.api("GET", f"/api/v1/exports/{export['id']}/file").content
        assert report.startswith(b"%PDF-") and digest(report) != before["resultPdf"]
        (work / "result-with-pass-criteria.pdf").write_bytes(report)
        checks["updatedResultPdfVerified"] = True
        assert host.json("GET", "/api/v1/admin/school-manager")["enabled"] is False
    with Host(args.new_package, work, "restart-offline") as host:
        host.login()
        assert host.json("GET", f"/api/v1/test-sessions/{ids['sessionId']}")["maximumMistakesToPass"] == 4
        checks["criterionPersistsAfterRestart"] = True
    with sqlite3.connect(f"file:{(work / 'data/ooki-grader.db').as_posix()}?mode=ro", uri=True) as db:
        assert db.execute("PRAGMA integrity_check").fetchone()[0] == "ok"
        assert not db.execute("PRAGMA foreign_key_check").fetchall()
        checks["databaseIntegrity"] = True
    evidence = {"state": "passed", "checks": checks,
                "scope": "Published host upgrade and restart with synthetic data; no AI calls, live sending, installed service, or elevated updater"}
    write_json(work / "offline-upgrade-evidence.json", evidence)
    print(json.dumps(evidence))


if __name__ == "__main__":
    main()
