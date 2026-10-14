"""Prove the exact new report cases against candidate5 before freezing the next candidate."""
from pathlib import Path
from collections import Counter
from datetime import datetime, timezone
import hashlib, json, sys

ROOT=Path(__file__).resolve().parents[2]
iteration=int(sys.argv[1]) if len(sys.argv)==2 else 6
assert 6<=iteration<=100
STAGE=ROOT/f'artifacts/af-final87-{iteration}-root'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref=lambda p:dict(path=p.relative_to(ROOT).as_posix(),sha256=sha(p))
prior=ROOT/'artifacts/af-final87-5/freeze.json'
freeze=json.loads(prior.read_text(encoding='utf-8-sig'))
discoveries=[];payloads=[];delta=None
for config in ('Release','Debug'):
    discovery=STAGE/f'discovery-{config}.log'
    actual=Counter(line.strip() for line in discovery.read_text(encoding='utf-8-sig').splitlines() if line.strip().startswith('Cadoryx.Tests.'))
    before=Counter(next(c for c in freeze['configurations'] if c['configuration']==config)['discoveredNames'])
    assert before<=actual
    added=actual-before
    assert sum(added.values())==10 and all(n.startswith('Cadoryx.Tests.ExchangeOccurrenceSemanticTests.') for n in added)
    assert actual==before+added and sum(actual.values())==7208
    if delta is None:delta=added
    else:assert delta==added
    discoveries.append(ref(discovery))
    payloads.append(ref(ROOT/f'Cadoryx.Tests/bin/{config}/net10.0/Cadoryx.Tests.dll'))
out=STAGE/'additional-discovery.json';assert not out.exists()
out.write_text(json.dumps(dict(recordedAt=datetime.now(timezone.utc).isoformat(),priorFreeze=ref(prior),
    expectedTotal=7208,additionalNames=dict(delta),rootDiscovery=discoveries,rootTestPayload=payloads,
    additionalProjects=['tools/ExchangeSemanticTests/ExchangeSemanticTests.csproj'],originalNamesAndMultiplicityPreserved=True,
    strictSTEP24Retried=False,AFComplete=False),indent=2)+'\n',encoding='utf-8')
print('Both actual7208; exactly10 report cases added; all candidate5 names/multiplicities preserved.')
