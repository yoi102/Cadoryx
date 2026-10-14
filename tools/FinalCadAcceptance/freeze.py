"""Bind the two builds, exact locks, discovery and provider to final87."""
from pathlib import Path
from datetime import datetime, timezone
from collections import Counter
import json, hashlib, sys
iteration=int(sys.argv[1]) if len(sys.argv)==2 else 1
assert 1<=iteration<=100
ROOT = Path(__file__).resolve().parents[2]; STAGE = ROOT/f'artifacts/af-final87-{iteration}'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
files = lambda p: [dict(path=f.relative_to(p).as_posix(),sha256=sha(f)) for f in sorted(p.rglob('*')) if f.is_file()]
suffix='-2' if iteration==1 else ''
prep = read(STAGE/('preparation2.json' if iteration==1 else 'preparation.json')); sdk = read(STAGE/'sdk-reception.json'); work = Path(prep['workspace'])
build = read(STAGE/f'build-exit{suffix}.json')
assert all(r['exitCode']==0 for r in build['stages'])
for row in build['stages']:
    if 'build' in row['stage']:
        log=Path(row['log']).read_text(encoding='utf-8-sig')
        assert 'Build succeeded.' in log and '0 Warning(s)' in log and '0 Error(s)' in log
locks, changes = [], []
selected_locks={str(Path(p).parent/'packages.lock.json').replace('\\','/') for p in prep['projects']}
for row in prep['rootSource']:
    source = ROOT/row['path']; candidate=work/row['path']
    assert sha(source)==row['sha256']
    if row['path'] in selected_locks:
        before, after=read(source),read(candidate)
        for target, dependencies in before['dependencies'].items():
            assert set(dependencies)==set(after['dependencies'][target])
            for name, item in dependencies.items():
                updated=after['dependencies'][target][name]
                if name.startswith('OcctSharp') and item.get('type')!='Project':
                    assert updated['resolved']==sdk['version']
                elif item!=updated:
                    # Project dependency maps may change only SDK version strings.
                    assert json.dumps(item,sort_keys=True).replace('8.0.1-preview.87.cu1',sdk['version'])==json.dumps(updated,sort_keys=True)
        locks.append(dict(path=row['path'],sha256=sha(candidate)))
    if sha(candidate)!=row['sha256']:
        assert row['path'] in prep['candidateChanges'] or row['path'].endswith('packages.lock.json')
        changes.append(row['path'])
old=read(ROOT/'artifacts/af1082-f5-d7-local87cu1-1/freeze.json')
managed={r['name']:r['sha256'] for r in sdk['managed13']}; native={r['name']:r['sha256'] for r in sdk['native62']}
configurations=[]
for config in ('Release','Debug'):
    app=work/f'Cadoryx.wpf/bin/{config}/net10.0-windows/win-x64'; test=work/f'Cadoryx.Tests/bin/{config}/net10.0'
    for folder in (app,test):
        assert {p.name:sha(p) for p in folder.glob('OcctSharp*.dll')}==managed
        assert {p.name:sha(p) for p in (folder/'occt').glob('*.dll')}==native
    names=[line.strip() for line in (STAGE/f'discovery-{config.lower()}{suffix}.log').read_text(encoding='utf-8-sig').splitlines() if line.strip().startswith('Cadoryx.Tests.')]
    assert len(names)==prep['expectedTotal']
    prior=Counter(next(c for c in old['configurations'] if c['configuration']==config)['discoveredNames'])
    current=Counter(names); assert prior<=current
    added=current-prior
    assert sum(added.values())==prep['expectedTotal']-7117
    assert sum(v for n,v in added.items() if n.startswith('Cadoryx.Tests.LmStudioCompletionIntegrityTests.'))==40
    assert sum(v for n,v in added.items() if n.startswith('Cadoryx.Tests.AgentEmptyResponseRecoveryTests.ToolboxDisplaysLocalizedTruncation'))==3
    assert sum(v for n,v in added.items() if n.startswith('Cadoryx.Tests.AgentNativeSelectionRetentionTests.'))==(32 if iteration==1 else 34)
    deferred=read(ROOT/'capability-evidence/diagnostics/af1081-local86/step24-user-deferral.json')['exactTestNames']
    assert all(current[n]==1 for n in deferred) and len(deferred)==24
    configurations.append(dict(configuration=config,candidate=str(app),appPayload=files(app),testDirectory=str(test),payload=files(test),discoveredNames=names))
assert Counter(configurations[0]['discoveredNames'])==Counter(configurations[1]['discoveredNames'])
provider=work/'tools/CadAgentWorkflowProbeRawTransport/bin/Release/net10.0'
app=Path(configurations[0]['candidate'])
shared=[dict(name=p.name,sha256=sha(p)) for p in provider.glob('Cadoryx.*.dll')]
assert len(shared)==16 and all(sha(app/r['name'])==r['sha256'] for r in shared)
assert {p.name:sha(p) for p in provider.glob('OcctSharp*.dll')}==managed
assert {p.name:sha(p) for p in (provider/'occt').glob('*.dll')}==native
record=dict(recordedAt=datetime.now(timezone.utc).isoformat(),state=f"FINAL87_CURRENT{prep['expectedTotal']}_BOTH_BUILD0_DISCOVERY_AND_PAYLOAD_FROZEN_NOT_RUNTIME",
    workspace=str(work),version=sdk['version'],nativeAbi=sdk['nativeAbi'],bridgeVersion=sdk['bridgeVersion'],
    rootSource=prep['rootSource'],candidateSource=[dict(path=r['path'],sha256=sha(work/r['path'])) for r in prep['rootSource']],
    allowedChanges=changes,locks=locks,configurations=configurations,native62=sdk['native62'],managed13=sdk['managed13'],
    provider=str(provider),providerPayload=files(provider),sharedProvider16=shared,
    expectedTotal=prep['expectedTotal'],expectedExecuted=prep['expectedExecuted'],deferredNames=deferred,original7117NamesAndMultiplicityPreserved=True,
    productionApplied=False,actualRuntimeStarted=False,publicNuget=False,AFComplete=False)
out=STAGE/'freeze.json'; assert not out.exists(); out.write_text(json.dumps(record,indent=2)+'\n',encoding='utf-8')
print(f"Both configurations build0/discover{prep['expectedTotal']}; original7117 intact; SDK13/native62/provider16 exact; next run{prep['expectedExecuted']}, deferred24 excluded.")
