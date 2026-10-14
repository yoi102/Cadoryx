"""Qualify isolated followers of the audited recovery-culture-only WPF delta."""
from datetime import datetime, timezone
from pathlib import Path
import hashlib
import json
import runpy
import shutil

ROOT = Path(__file__).resolve().parents[2]
DRAFT = ROOT / 'artifacts/af1082-f5-d7-local87cu1-1'
UI = ROOT / 'artifacts/af1082-f5-d7-ui-culture-1'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref = lambda p: dict(path=p.relative_to(ROOT).as_posix(), sha256=sha(p))

def check(reference):
    path = ROOT / reference['path']
    assert sha(path) == reference['sha256']
    return path

def verified_followups():
    proof_path = UI / 'followups-verification1.json'
    proof = read(proof_path)
    assert proof['state'] == 'QUALIFIED_WPF_CULTURE_ONLY_FOLLOWERS_ORIGINAL_CORE_AND_ASSERTIONS_RETAINED_NOT_RUNTIME'
    for key in ('application', 'base', 'main36', 'main36DirectReview', 'originalHosts', 'source', 'originalPerformance'):
        check(proof[key])
    for row in proof['builds']: check(row)
    app = read(check(proof['application']))
    base = read(check(proof['base']))
    hosts = read(check(proof['originalHosts']))
    assert proof['source1200OnlyRecoveryCultureDiffers'] and proof['providerShared16ByteIdentical']
    assert proof['originalRecoveryAndWindowHostAssertionsUnchanged']
    assert proof['performance']['candidate'] == app['candidate']
    assert proof['performance']['payload'] == app['payload']
    assert {r['label'] for r in proof['followers']} == {'recovery24', 'd6-window36'}
    for follower in proof['followers']:
        assert set(follower['changedFiles']) == {'Cadoryx.wpf.dll', 'Cadoryx.wpf.pdb'}
        original = next(r for r in hosts['configurations'] if r['label'] == follower['label'] and r['configuration'] == 'Release')
        assert follower['originalHost'] == original['output']
        before = {r['path']: r['sha256'] for r in original['payload']}
        after = {r['path']: r['sha256'] for r in follower['payload']}
        assert before.keys() == after.keys()
        assert {n for n in before if before[n] != after[n]} == set(follower['changedFiles'])
        for row in follower['payload']: assert sha(Path(follower['output']) / row['path']) == row['sha256']
    for row in app['payload']: assert sha(Path(app['candidate']) / row['path']) == row['sha256']
    for row in base['sourcePayload']:
        assert sha(Path(base['workspace']) / row['path']) == row['sha256']
        expected = proof['source']['sha256'] if row['path'] == proof['source']['path'] else row['sha256']
        assert sha(UI / 'workspace' / row['path']) == expected
    return proof, proof_path

def qualify():
    runpy.run_path(str(Path(__file__).with_name('require-full-gate.py')))
    freeze_path = DRAFT / 'freeze.json'
    freeze = read(freeze_path)
    ui_path = UI / 'application-verification2.json'
    ui = read(ui_path)
    assert ui['state'] == 'CURRENT_UI_RECOVERY_CULTURE_ONLY_DELTA_BUILD0_ORIGINAL_CORE18_SDK13_NATIVE62_IDENTICAL'
    assert check(ui['baseFreeze']) == freeze_path
    check(ui['baseFullBoth7117'])
    change = 'Cadoryx.wpf/Views/ViewportPane.GraphicsRecovery.cs'
    assert check(ui['sourceChange']) == ROOT / change
    assert set(ui['changedApplicationFiles']) == {'Cadoryx.wpf.dll', 'Cadoryx.wpf.pdb'}
    assert ui['core18Sdk13Native62AndWorkerBytesUnchanged']
    original = next(c for c in freeze['configurations'] if c['configuration'] == 'Release')
    before = {r['path']: r['sha256'] for r in original['appPayload']}
    after = {r['path']: r['sha256'] for r in ui['payload']}
    assert before.keys() == after.keys()
    assert {n for n in before if before[n] != after[n]} == set(ui['changedApplicationFiles'])
    app = Path(ui['candidate'])
    for row in ui['payload']: assert sha(app / row['path']) == row['sha256']
    for row in freeze['sourcePayload']:
        assert sha(Path(freeze['workspace']) / row['path']) == row['sha256']
        revised = UI / 'workspace' / row['path']
        expected = ui['sourceChange']['sha256'] if row['path'] == change else row['sha256']
        assert sha(revised) == expected
        if row['path'] == change: assert row['sha256'] == ui['originalSourceHash']
    logs = [ROOT / ui['compileLog']['path'], UI / 'debug-build1.log',
            UI / 'root-release-build.log', UI / 'root-debug-build1.log']
    assert check(ui['compileLog']) == logs[0]
    for log in logs:
        text = log.read_text(encoding='utf-8-sig')
        assert 'Build succeeded.' in text and '0 Warning(s)' in text and '0 Error(s)' in text
    main_path = DRAFT / 'main-status36/runtime-5-verification.json'
    main = read(main_path)
    assert check(main['application']) == ui_path
    assert main['passed'] == 36 and main['failed'] == 0 and len(main['frames']) == 144
    for key in ('fixture', 'fullBoth7117', 'terminal', 'result', 'binding'): check(main[key])
    review_path = ROOT / 'capability-evidence/diagnostics/af1082-f5-d7-local87cu1/main-status36-whole-image-review.json'
    review = read(review_path)
    assert check(review['audit']) == main_path and check(review['application']) == ui_path
    assert review['state'] == 'CURRENT_UI_CULTURE_MAIN36_ALL42_UNIQUE_FULL_FRAMES_DIRECTLY_REVIEWED'
    assert review['wholeImages'] == 144 and review['uniqueWholeImages'] == 42 and review['fullShells'] == 36
    assert {r['image']['sha256'] for r in review['reviewedWholeImages']} == {r['image']['sha256'] for r in main['frames']}
    for row in main['frames']: check(row['image'])
    hosts_path = ROOT / 'capability-evidence/diagnostics/af1082-f5-d7-local87cu1/hosts-verification.json'
    hosts = read(hosts_path)
    assert check(hosts['freeze']) == freeze_path
    assert hosts['D6ReadinessSeconds'] == 30 and hosts['D6ObservationSeconds'] == 300
    for row in hosts['windowObserverSources']: assert sha(ROOT / row['path']) == row['sha256']
    followers = []
    for label in ('recovery24', 'd6-window36'):
        base = next(r for r in hosts['configurations'] if r['configuration'] == 'Release' and r['label'] == label)
        source, dest = Path(base['output']), UI / 'hosts' / (label + '-release-1')
        for row in base['payload']: assert sha(source / row['path']) == row['sha256']
        assert not dest.exists(), 'Preserve existing qualified follower; do not overwrite it.'
        shutil.copytree(source, dest)
        for name in ui['changedApplicationFiles']: shutil.copyfile(app / name, dest / name)
        payload = []
        assert {p.relative_to(dest).as_posix() for p in dest.rglob('*') if p.is_file()} == {r['path'] for r in base['payload']}
        for row in base['payload']:
            digest = sha(dest / row['path'])
            assert digest == (after[row['path']] if row['path'] in ui['changedApplicationFiles'] else row['sha256'])
            payload.append(dict(path=row['path'], sha256=digest))
        followers.append(dict(label=label, output=str(dest), payload=payload,
                              originalHost=str(source), changedFiles=ui['changedApplicationFiles']))
    provider = Path(freeze['provider'])
    shared = [r for r in freeze['providerPayload'] if r['path'].startswith('Cadoryx.') and r['path'].endswith('.dll')]
    assert len(shared) == 16
    for row in freeze['providerPayload']: assert sha(provider / row['path']) == row['sha256']
    for row in shared: assert after[row['path']] == row['sha256']
    perf_path = DRAFT / 'performance-freeze.json'
    perf = read(perf_path)
    assert perf['originalDirectRuns'] == perf['productionOpenRuns'] == 2
    assert perf['originalLifecycleCyclesPerRun'] == 20 and perf['targetMs'] == 6482
    assert perf['directTimeoutSeconds'] == 1800 and perf['productionOpenTimeoutSeconds'] == 900
    release = next(c for c in perf['configurations'] if c['configuration'] == 'Release')
    assert Path(release['candidate']) == Path(original['candidate'])
    assert {r['path']: r['sha256'] for r in release['payload']} == before
    performance = dict(release, candidate=str(app), payload=ui['payload'])
    out = UI / 'followups-verification1.json'
    assert not out.exists()
    out.write_text(json.dumps(dict(recordedAt=datetime.now(timezone.utc).isoformat(),
        state='QUALIFIED_WPF_CULTURE_ONLY_FOLLOWERS_ORIGINAL_CORE_AND_ASSERTIONS_RETAINED_NOT_RUNTIME',
        application=ref(ui_path), base=ref(freeze_path), main36=ref(main_path), main36DirectReview=ref(review_path),
        originalHosts=ref(hosts_path), source=ui['sourceChange'], builds=[ref(log) for log in logs],
        source1200OnlyRecoveryCultureDiffers=True, originalRecoveryAndWindowHostAssertionsUnchanged=True,
        followers=followers, providerShared16ByteIdentical=True, providerCandidate=str(provider),
        originalPerformance=ref(perf_path), performance=performance,
        fullSuiteRerun=False, physicalScanout=False, productionApplied=False, AFComplete=False),
        indent=2)+'\n', encoding='utf-8')
    print('Qualified isolated recovery24/D6 hosts, unchanged provider16, and exact final UI performance payload; runtimes remain unproven.')

if __name__ == '__main__': qualify()
