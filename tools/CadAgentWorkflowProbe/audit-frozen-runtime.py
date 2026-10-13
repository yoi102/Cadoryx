"""Audit existing provider receipts without changing prompts or rerunning a model."""
from pathlib import Path
from datetime import datetime, timezone
import argparse
import hashlib
import json

ROOT = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument("run_root", type=Path)
parser.add_argument("output", type=Path)
args = parser.parse_args()
folder = args.run_root.resolve()
target = args.output.resolve()
assert not target.exists(), "Preserve existing audit evidence."

def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()

def ref(path):
    return {"path": path.relative_to(ROOT).as_posix(), "sha256": sha(path)}

freeze_path = folder / "provider-runtime-freeze.json"
freeze = read(freeze_path)
candidate = Path(freeze["candidate"])
assert freeze["originalContext"] == 8192
assert freeze["originalToolRounds"] == 16
assert freeze["originalPerTurnMinutes"] == 4
assert freeze["parentKnownSTEP24FailuresPreserved"]
assert freeze["model"]["quantization"]["name"] == "Q1_0"
assert freeze["model"]["contextLength"] == 8192
for row in freeze["payload"]:
    assert sha(candidate / row["path"]) == row["sha256"]
for row in freeze["originalPromptAndAssertionSources"]:
    assert sha(ROOT / row["path"]) == row["sha256"]
parent_path = folder / "freeze.json"
parent = read(parent_path)
for row in parent["sourcePayload"]:
    assert sha(Path(parent["workspace"]) / row["path"]) == row["sha256"]
native = {r["name"]: r["sha256"] for r in freeze["native62"]}
assert len(native) == 62
terminal_path = folder / "provider-both-exit.json"
terminal = read(terminal_path)
checks = []
for scene in freeze["scenarioOrder"]:
    name = "lmstudio-" + scene + "-1"
    output = folder / name
    result_path = output / "result.json"
    result = read(result_path)
    exit_path = folder / (name + "-exit.json")
    end = read(exit_path)
    start = read(folder / (name + "-start.json"))
    binding_path = folder / (name + "-loaded-native.json")
    binding = read(binding_path)
    row = next(r for r in terminal["scenarios"] if r["scenario"] == scene)
    assert sha(exit_path) == row["sha256"]
    assert end["exitCode"] == row["exitCode"] == (0 if result["passed"] else 1)
    assert end["scenario"] == result["scenario"] == scene
    assert end["model"] == result["model"] == freeze["model"]["identifier"]
    assert end["endpoint"] == result["endpoint"] == freeze["endpoint"]
    assert end["contextLength"] == 8192 and end["maxToolRounds"] == 16
    assert end["originalPerTurnMinutes"] == 4
    assert start["pid"] == binding["processId"]
    assert Path(start["candidate"]) == Path(end["candidate"]) == candidate
    assert result["actualProductionTools"] and result["assetsAfterClose"] == 0
    assert result["physicalPointer"] == "NOT RUN"
    assert len(binding["native"]) == 62
    assert {r["name"] for r in binding["native"]} == set(native)
    for loaded in binding["native"]:
        assert Path(loaded["path"]).parent == candidate / "occt"
        assert sha(Path(loaded["path"])) == loaded["sha256"] == native[loaded["name"]]
    calls_path = output / "tool-calls.json"
    calls = read(calls_path)
    failures = []
    for index, call in enumerate(calls):
        receipt = json.loads(call["result"])
        if receipt.get("success") is False:
            failures.append({"ordinal": index, "tool": call["Name"],
                             "arguments": json.loads(call["ArgumentsJson"]), "receipt": receipt})
    saved = []
    if scene == "workspace":
        saved_path = output / "workspace-provider-saved.cadoryx"
        assert sha(saved_path).lower() == result["savedSha256"].lower()
        saved.append(ref(saved_path))
    checks.append({"scenario": scene, "passed": result["passed"],
                   "completedTurns": len(result["turns"]), "requiredTurns": 8,
                   "failure": result.get("failure"), "assetsAfterClose": 0,
                   "actualNative62Matched": True, "calls": len(calls),
                   "failedReceipts": failures, "saved": saved,
                   "result": ref(result_path), "terminal": ref(exit_path),
                   "binding": ref(binding_path), "toolCalls": ref(calls_path)})
assert [r["scenario"] for r in checks] == ["drawing", "workspace"]
record = {"recordedAt": datetime.now(timezone.utc).isoformat(),
          "state": "AF1081_LOCAL86_ORIGINAL_PROVIDER_FAILURES_INDEPENDENTLY_VERIFIED",
          "freeze": ref(freeze_path), "parent": ref(parent_path), "suite": ref(terminal_path),
          "scenarios": checks, "originalSourcesMatched": len(parent["sourcePayload"]),
          "payloadMatched": len(freeze["payload"]), "originalPromptAndAssertionsChanged": False,
          "original8192Context16ToolRounds4MinutesPreserved": True,
          "productionApplied": False, "modelAcceptancePassed": all(c["passed"] for c in checks),
          "AFComplete": False, "G": "USER_DEFERRED", "cleanup": False}
target.parent.mkdir(parents=True, exist_ok=True)
target.write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
print(json.dumps({"scenarios": [{k: c[k] for k in ["scenario", "passed", "completedTurns", "failure"]} for c in checks],
                  "originalSourcesMatched": len(parent["sourcePayload"]), "actualNative62Matched": True}))
