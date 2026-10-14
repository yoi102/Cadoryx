"""Audit current MiniPC direct and production-open results independently."""
from datetime import datetime, timezone
from pathlib import Path
import importlib.util
import json
import math
import runpy
import struct

spec = importlib.util.spec_from_file_location('ui_followups', Path(__file__).with_name('qualify-ui-followups.py'))
ui = importlib.util.module_from_spec(spec); spec.loader.exec_module(ui)
ROOT, DRAFT, read, sha, ref = ui.ROOT, ui.DRAFT, ui.read, ui.sha, ui.ref
runpy.run_path(str(Path(__file__).with_name('require-followup-evidence.py')))
qualified, qualification_path = ui.verified_followups()
freeze = read(DRAFT / 'performance-freeze.json')
candidate = Path(qualified['performance']['candidate'])
expected_native = {r['name']: r['sha256'] for r in qualified['performance']['native']}
assert len(expected_native) == 62
source = Path(freeze['input']['path'])
source_hash = 'F3773A7F37BD0CB8E717E4BE6F1070A72E5C6E7B2F36F26F9501183DF8C4C68E'
image_hash = '2E8022953A2D1D2118E6FBC4DDC8A964D7EEC1ACA8CA6B2AF055131595E63852'
assert sha(source) == source_hash and source.stat().st_size == 88258425
assert freeze['originalDirectRuns'] == freeze['productionOpenRuns'] == 2
assert freeze['originalLifecycleCyclesPerRun'] == 20 and freeze['targetMs'] == 6482
assert freeze['directTimeoutSeconds'] == 1800 and freeze['productionOpenTimeoutSeconds'] == 900

def timing(value):
    assert isinstance(value, (int, float)) and math.isfinite(value) and value >= 0
    return value

def native(path):
    binding = read(path)
    rows = binding['files']
    assert len(rows) == len({r['name'] for r in rows}) == 62
    assert {r['name']: r['sha256'] for r in rows} == expected_native
    for row in rows:
        module = Path(row['path'])
        assert module.parent == candidate / 'occt' and sha(module) == row['sha256']
    return binding['pid']

direct_folder = ROOT / 'artifacts/af1082-f5-d7-direct-1'
open_folder = ROOT / 'artifacts/af1082-f5-d7-production-open-1'
direct_summary, open_summary = (read(f / 'summary.json') for f in (direct_folder, open_folder))
assert direct_summary['state'] == 'CORRECTNESS_PASS_PERFORMANCE_AS_RECORDED'
assert len(direct_summary['runs']) == len(open_summary['runs']) == 2
direct, opened, pids = [], [], []
for number in (1, 2):
    folder = direct_folder / f'run-{number}'
    assert (folder / 'result.txt').read_text(encoding='utf-8-sig').startswith('PASS:')
    assert (folder / 'bindings.log').stat().st_size == 0
    report_path = folder / 'user-model-1-report.json'; report = read(report_path)
    assert report['sourceSha256'].upper() == source_hash and report['sourceBytes'] == 88258425
    assert report['visibleInstances'] == 4964 and len(report['lifecycle']) == 20
    for cycle in report['lifecycle']:
        assert all(cycle[key] == 1185 for key in ('primaryGeometry', 'secondaryGeometry', 'primarySources', 'secondarySources'))
        assert cycle['primaryXdeContexts'] == cycle['secondaryXdeContexts'] == 1
    pids.append(native(folder / 'loaded-native.json'))
    submitted, first = timing(report['firstSubmitMs']), timing(report['firstVisibleSubmitMs'])
    assert first <= submitted
    row = direct_summary['runs'][number - 1]
    assert row['firstSubmitMs'] == submitted and row['targetMs'] == 6482
    assert row['targetMet'] == (submitted <= 6482)
    direct.append(dict(run=number, firstCompleteSubmitMs=submitted, firstVisibleSubmitMs=first,
        inputServiceP95Ms=timing(report['inputServiceP95Ms']), targetMs=6482, targetMet=submitted <= 6482,
        cycles=20, report=ref(report_path), actualNative62=ref(folder / 'loaded-native.json'),
        result=ref(folder / 'result.txt'), bindings=ref(folder / 'bindings.log')))
    folder = open_folder / f'run-{number}'
    assert (folder / 'result.txt').read_text(encoding='utf-8-sig').startswith('PASS:')
    report_path = folder / 'report.json'; report = read(report_path)
    assert report['sourceSha256'].upper() == source_hash
    assert report['visibleInstances'] == report['scene']['VisibleInstances'] == 4964
    assert report['bodies'] == report['nativeGeometry'] == report['nativeSources'] == 1185
    assert report['xdeContexts'] == 1
    pids.append(native(folder / 'loaded-native.json'))
    png = folder / 'full-scene.png'; data = png.read_bytes()
    assert data[:8] == b'\x89PNG\r\n\x1a\n' and struct.unpack('>II', data[16:24]) == (788, 411)
    assert sha(png) == open_summary['runs'][number - 1]['screenshotSha256'].upper() == image_hash
    opened.append(dict(run=number, firstVisibleFromClickMs=timing(report['firstVisibleFromClickMs']),
        fullSceneFromClickMs=timing(report['fullSceneFromClickMs']), sceneCompleteMs=timing(report['scene']['CompleteMs']),
        workerKernelImportMs=timing(report['import']['Worker']['KernelImportMs']), report=ref(report_path),
        actualNative62=ref(folder / 'loaded-native.json'), completeAcceptedPng=ref(png), result=ref(folder / 'result.txt')))
assert len(set(pids)) == 4, 'Four independent application processes required.'
met = all(r['targetMet'] for r in direct)
out = ROOT / 'capability-evidence/diagnostics/af1082-f5-d7-local87cu1/performance-verification.json'
assert not out.exists()
record = dict(recordedAt=datetime.now(timezone.utc).isoformat(),
    state='CURRENT_FINAL_UI_DIRECT_AND_PRODUCTION_CORRECTNESS_PASS_PERFORMANCE_AS_MEASURED',
    qualification=ref(qualification_path), application=qualified['application'],
    originalPerformance=qualified['originalPerformance'], sourceInput=dict(path=str(source), sha256=sha(source)),
    sourceCount=1200, sourceOnlyProvenWpfCultureDiffers=True, directCorrectness='PASS',
    directTarget6482Ms='PASS' if met else 'FAIL', direct=direct, productionOpenCorrectness='PASS',
    productionOpen=opened, actualFourProcessIds=pids, directSummary=ref(direct_folder / 'summary.json'),
    openSummary=ref(open_folder / 'summary.json'), sdkCpuPause=ref(DRAFT / 'sdk-performance-pause.json'),
    wholePngReferenceUnchanged=True, cacheCondition=direct_summary['cacheCondition'],
    physicalDisplayFpsOrLatency='NOT_MEASURED', originalSTEP24='DEFERRED_FAILURES',
    materialAndD6WindowAcceptance='SEPARATE_PENDING', providerAllPassed=False,
    A5='NOT_FULLY_ACCEPTED', AFComplete=False, G='USER_DEFERRED')
out.write_text(json.dumps(record, indent=2)+'\n',encoding='utf-8')
print(json.dumps(dict(directTarget6482Ms=record['directTarget6482Ms'], direct=direct,
                     productionOpen=opened), ensure_ascii=False))
