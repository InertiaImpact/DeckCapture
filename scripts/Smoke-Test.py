"""Exercise an isolated, running DeckCapture instance using only synthetic local I/O.

Start it with scripts/Start-DeckCapture.ps1 -StateDirectory .local/smoke-state,
then run python scripts/Smoke-Test.py. Never use production operational state.
The script retains all generated media and restores the original configuration.
"""
import argparse
import json
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--base-url", default="http://127.0.0.1:5278")
args = parser.parse_args()
base = args.base_url.rstrip("/") + "/api"
if not base.startswith(("http://127.0.0.1:", "http://localhost:")):
    raise SystemExit("Use a loopback HTTP test instance.")

def get(path):
    with urllib.request.urlopen(base + path, timeout=10) as response:
        return json.load(response)

def command(path, payload, key=None, method="POST", expected=200):
    request = urllib.request.Request(base + path, data=json.dumps(payload).encode(), method=method,
        headers={"Content-Type": "application/json", "Idempotency-Key": key or uuid.uuid4().hex})
    try:
        with urllib.request.urlopen(request, timeout=50) as response:
            assert response.status == expected
            return json.load(response)
    except urllib.error.HTTPError as error:
        value = json.load(error)
        assert error.code == expected, (error.code, value)
        return value

state = get("/state")
original = state["configuration"]
state_root = Path(state["system"]["stateDirectory"]).resolve()
assert ".local" in state_root.parts, "Use an isolated state directory under .local."
assert not original["notifications"]["enabled"], "SMTP must be disabled in the test instance."
assert all(c["sourceKind"] == "Synthetic" for c in original["channels"])
assert all(Path(d["root"]).resolve().is_relative_to(state_root) for d in original["destinations"])
assert Path(original["cacheRoot"]).resolve().is_relative_to(state_root)
assert not any(j["state"] in ["Prepared", "Waiting", "Recording", "Finalizing", "Validating"] for j in state["recordings"])
configuration = json.loads(json.dumps(original))
configuration["capture"]["offlineQcEnabled"] = True
configuration["transfer"]["scheduleEnabled"] = False
configuration["metadataFields"]["tapeId"] = {"visible": False, "required": False}
configuration["metadataFields"]["operator"] = {"visible": True, "required": False}
try:
    command("/configuration", configuration, method="PUT")
    payload = {"channelId": configuration["channels"][0]["id"], "fileName": "smoke_" + uuid.uuid4().hex[:12],
        "durationSeconds": 5, "destinationId": configuration["destinations"][0]["id"],
        "relativeDirectory": "http-smoke", "metadata": {"operator": "Synthetic test", "tapeId": "hidden-value"}}
    key = uuid.uuid4().hex
    job = command("/recordings/prepare", payload, key)
    assert "tapeId" not in job["metadata"]
    assert command("/recordings/prepare", payload, key)["id"] == job["id"]
    command("/recordings/prepare", dict(payload, fileName="different"), key, expected=409)
    route = "/recordings/" + job["id"]
    command(route + "/start", {"revision": 0}, expected=409)
    command(route + "/start", {"revision": job["revision"]})
    deadline = time.monotonic() + 90
    while time.monotonic() < deadline:
        current = get("/state")
        recording = next(j for j in current["recordings"] if j["id"] == job["id"])
        if recording["state"] in ["Complete", "NeedsReview", "Failed"]:
            break
        time.sleep(.5)
    assert recording["state"] == "Complete", (recording["state"], recording["error"])
    while time.monotonic() < deadline:
        current = get("/state")
        delivery = next(d for d in current["deliveries"] if d["recordingId"] == job["id"])
        if delivery["state"] in ["Delivered", "Held", "Failed"]:
            break
        time.sleep(.5)
    assert delivery["state"] == "Delivered", (delivery["state"], delivery["error"])
    qc = get(route + "/qc")["summary"]
    assert qc["analysis"]["decodedFrames"] > 100
    assert all(qc["analysis"]["availableFilters"].values()), "This acceptance smoke requires all documented offline QC filters."
    assert qc["analysis"]["signalStatsFrames"] == qc["analysis"]["decodedFrames"], "Signalstats must cover the complete decoded recording."
    assert Path(delivery["destinationPath"], "COMPLETE.json").is_file()
    evidence = {"recordingId": recording["id"], "state": recording["state"], "seconds": recording["recordedSeconds"],
        "delivery": delivery["state"], "qcDecodedFrames": qc["analysis"]["decodedFrames"],
        "idempotency": "passed", "revisionConflict": "passed", "hiddenMetadata": "omitted"}
    target = state_root / "http-smoke-result.json"
    target.write_text(json.dumps(evidence, indent=2), encoding="utf-8")
    print(json.dumps(evidence, indent=2))
finally:
    command("/configuration", original, method="PUT")
