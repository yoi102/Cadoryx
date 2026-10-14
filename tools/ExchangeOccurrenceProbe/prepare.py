"""Freeze the compiled independent consumer; preparation is not CAD acceptance."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib, json

ROOT=Path(__file__).resolve().parents[2];STAGE=ROOT/'artifacts/f4-occurrence-external-1'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref=lambda p:dict(path=p.relative_to(ROOT).as_posix(),sha256=sha(p))
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
freeze=read(ROOT/'artifacts/af-final87-8/freeze.json')
consumer=STAGE/'consumer';published=STAGE/'publish2'
for name in ('Program.cs','ExchangeOccurrenceProbe.csproj'):
    assert sha(ROOT/'tools/ExchangeOccurrenceProbe'/name)==sha(consumer/name)
project=(consumer/'ExchangeOccurrenceProbe.csproj').read_text(encoding='utf-8-sig')
assert 'ProjectReference' not in project and 'OcctSharp.Native.win-x64' not in project
locks=read(consumer/'packages.lock.json')
for framework,dependencies in locks['dependencies'].items():
    for name,item in dependencies.items():
        if name.startswith('OcctSharp') and item.get('type')!='Project':assert item['resolved']=='8.0.1-preview.87'
assert {p.name:sha(p) for p in published.glob('OcctSharp*.dll')}=={r['name']:r['sha256'] for r in freeze['managed13']}
assert {p.name:sha(p) for p in (published/'occt').glob('*.dll')}=={r['name']:r['sha256'] for r in freeze['native62']}
log=STAGE/'publish2.log';text=log.read_text(encoding='utf-8-sig')
assert ' -> ' in text and 'warning ' not in text.lower() and 'error ' not in text.lower()
payload=[ref(p) for p in sorted(published.rglob('*')) if p.is_file()]
record=dict(recordedAt=datetime.now(timezone.utc).isoformat(),state='F4_NESTED_SHARED_PMI_SOURCE_CONSUMER_COMPILED_AND_FROZEN_NOT_RUNTIME',
    applicationFreeze=ref(ROOT/'artifacts/af-final87-8/freeze.json'),sdk='8.0.1-preview.87',consumerPublished=str(published),
    source=[ref(ROOT/'tools/ExchangeOccurrenceProbe'/n) for n in ('Program.cs','ExchangeOccurrenceProbe.csproj','freecad_observe.py')],
    compileLog=ref(log),lock=ref(consumer/'packages.lock.json'),payload=payload,
    sourceInventoryRequired=dict(assemblyRoots=1,assemblyInstances=2,partInstances=5,partReuseCounts=[2,3],sourceBoxFaces=6,dimensions=1,geometricTolerances=1,datums=1),
    runtimeStarted=False,externalCADStarted=False,semanticUnitReadNotYetQualified=True,
    noGuessedTopologyOrNodeMapping=True,fullGeometryEquivalence=False,AFComplete=False)
out=STAGE/'preparation.json';assert not out.exists()
out.write_text(json.dumps(record,indent=2)+'\n',encoding='utf-8')
print('F4 source consumer compiled;13 managed/62 native package bytes exact; native/external runtime NOT RUN.')
