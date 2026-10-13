from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json
import shutil

ROOT = Path(__file__).resolve().parents[2]
TOOL = Path(__file__).resolve().parent
DRAFT = ROOT / 'artifacts/inspection-document-id-draft-1'
WORK = DRAFT / 'workspace'
def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest().upper()
def ref(p): return {'path': p.relative_to(ROOT).as_posix(), 'sha256': sha(p)}

assert not WORK.exists()
parent_path = ROOT / 'artifacts/af1082-d7-local87cu1-1/freeze.json'
parent = read(parent_path)
WORK.mkdir(parents=True)
for row in parent['sourcePayload']:
    original = Path(parent['workspace']) / row['path']
    assert sha(original) == row['sha256']
    target = WORK / row['path']; target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(original, target)
source = WORK / 'Cadoryx.ViewModels/Toolboxes/CadAgentDocumentTools.cs'
before = source.read_text(encoding='utf-8-sig')
after = before.replace('if (document is null || document.IsDetached) throw new InvalidOperationException("No active document.");',
    'if (document is null || document.IsDetached) throw new InvalidOperationException("No active document.");\n            if (document.IsClosingRequested || document.Session.IsClosing) throw new InvalidOperationException("The document is closing or closed.");')
needle = 'return CadAuthoringInspection.Read(document.Session, area, id, partId, offset, limit, cancellationToken);'
assert after.count(needle) == 1
after = after.replace(needle, 'var inspected = CadAuthoringInspection.Read(document.Session, area, id, partId, offset, limit, cancellationToken);\n            return CadInspectionDocumentIdentityFeedback.Read(snapshot, area, id, partId, offset, limit) ?? inspected;')
needle = 'return JsonSerializer.Serialize(result, InspectJsonOptions);'
assert after.count(needle) == 1
after = after.replace(needle, 'return CadInspectionDocumentIdentityFeedback.Read(snapshot, area, id, partId, offset, limit) ?? JsonSerializer.Serialize(result, InspectJsonOptions);')
assert before != after
source.write_text(after, encoding='utf-8')
helper = WORK / 'Cadoryx.ViewModels/Toolboxes/CadInspectionDocumentIdentityFeedback.cs'
shutil.copyfile(TOOL / helper.name, helper)
rows = [ref(p) for p in sorted(TOOL.iterdir()) if p.is_file()]
record = {'recordedAt': datetime.now(timezone.utc).isoformat(), 'parent': ref(parent_path),
          'baseInputs': len(parent['sourcePayload']), 'workspace': str(WORK),
          'changedSource': ref(source), 'addedHelper': ref(helper), 'toolSource': rows,
          'nativeRuntimeStarted': False, 'whole6991RegressionChanged': False,
          'originalSchemaOrToolCountChanged': False, 'productionApplied': False, 'AFComplete': False}
(DRAFT / 'preparation.json').write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'copiedInputs': len(parent['sourcePayload']), 'modifiedSource': 1, 'addedHelper': 1, 'nativeRuntimeStarted': False}))
