"""Apply only the audited D7 and exact development NuGet changes to the root."""
from pathlib import Path
from datetime import datetime, timezone
import argparse
import hashlib
import json
import runpy
import tempfile

ROOT = Path(__file__).resolve().parents[2]
DRAFT = ROOT / 'artifacts/af1082-f5-d7-local87cu1-1'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
def ref(p): return {'path': p.relative_to(ROOT).as_posix(), 'sha256': sha(p)}
def verify_reference(reference, expected=None):
    path = ROOT / reference['path']
    assert sha(path) == reference['sha256']
    if expected is not None: assert path.resolve() == expected.resolve()
def replace_owned_file(target, data, expected_digest=None):
    assert target.resolve().is_relative_to(ROOT.resolve())
    def check_current():
        if expected_digest is None: assert not target.exists(), f'Preserve new file: {target}'
        else: assert sha(target) == expected_digest, f'File changed before replacement: {target}'
    check_current()
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(prefix='.' + target.name + '-', suffix='.cadoryx-integration.tmp',
                                         dir=target.parent, delete=False) as stream:
            temporary = Path(stream.name)
            stream.write(data)
        check_current()
        temporary.replace(target)
    finally:
        if temporary is not None and temporary.exists(): temporary.unlink()

parser = argparse.ArgumentParser()
parser.add_argument('--apply', action='store_true')
parser.add_argument('--with-ui-culture', action='store_true')
args = parser.parse_args()
prep, freeze = read(DRAFT / 'preparation.json'), read(DRAFT / 'freeze.json')
assert freeze['version'] == '8.0.1-preview.87.cu1'
original = {r['path']: r['sha256'] for r in prep['originalSource']}
current = {r['path']: r['sha256'] for r in freeze['sourcePayload']}
assert len(original) == 1196 and len(current) == 1200
code = set(prep['candidateOverlays'].values()) | {prep['candidateBranchTest']}
assert len(code) == 5
settings = {'Directory.Build.props', 'NuGet.Config', 'Cadoryx.wpf/runtime-baseline.json'}
changes = [n for n in current if current[n] != original.get(n)]
locks = {n for n in changes if n.endswith('/packages.lock.json')}
assert len(locks) == 30 and len(changes) == 38
assert set(changes) == code | settings | locks
root_expected, effective = dict(original), dict(current)
ui_qualification = None
if args.with_ui_culture:
    import importlib.util
    spec = importlib.util.spec_from_file_location('ui_followups', Path(__file__).with_name('qualify-ui-followups.py'))
    ui = importlib.util.module_from_spec(spec); spec.loader.exec_module(ui)
    ui_qualification, ui_qualification_path = ui.verified_followups()
    source = ui_qualification['source']
    name = source['path']
    assert name == 'Cadoryx.wpf/Views/ViewportPane.GraphicsRecovery.cs'
    assert current[name] == original[name]
    root_expected[name] = effective[name] = source['sha256']
for name, digest in original.items():
    assert sha(ROOT / name) == root_expected[name], f'Root input changed; preserve it: {name}'
for name, digest in current.items():
    source = Path(freeze['workspace']) / name
    assert sha(source) == digest, f'Frozen candidate changed: {name}'
    if name not in original: assert not (ROOT / name).exists(), f'Preserve new root file: {name}'
for name in changes:
    assert (ROOT / name).resolve().is_relative_to(ROOT.resolve())
for name in locks:
    before, after = read(ROOT / name), read(Path(freeze['workspace']) / name)
    assert before['version'] == after['version']
    assert before['dependencies'].keys() == after['dependencies'].keys()
    for framework, packages in before['dependencies'].items():
        restored = after['dependencies'][framework]
        assert packages.keys() == restored.keys()
        for key, value in packages.items():
            actual = restored[key]
            if key.startswith('OcctSharp'):
                assert actual['resolved'] == freeze['version']
                if 'requested' in actual:
                    assert actual['requested'] == f"[{freeze['version']}, {freeze['version']}]"
            elif value.get('type') == 'Project':
                old_project, new_project = dict(value), dict(actual)
                old_dependencies = old_project.pop('dependencies', {})
                new_dependencies = new_project.pop('dependencies', {})
                assert old_project == new_project and old_dependencies.keys() == new_dependencies.keys()
                for dependency, constraint in old_dependencies.items():
                    if dependency.lower().startswith('occtsharp'):
                        assert new_dependencies[dependency] == f"[{freeze['version']}, {freeze['version']}]"
                    else: assert new_dependencies[dependency] == constraint, (name, key, dependency)
            else: assert value == actual, (name, framework, key)
sdk = read(ROOT / 'artifacts/local-sdk87cu1-received-1/receipt.json')
assert sdk['version'] == freeze['version'] and len(sdk['packages']) == 14
feed = Path(sdk['immutableReceivedFeed'])
for row in sdk['packages']: assert sha(feed / row['name']) == row['sha256']
if not args.apply:
    print(json.dumps({'root1196InputsUnchanged': not args.with_ui_culture,
                      'rootOnlyProvenUiCultureDelta': args.with_ui_culture, 'candidate1200InputsMatched': True,
                      'codeFiles': sorted(code), 'SDKLocks': len(locks), 'changes': len(changes),
                      'nonSDKDependenciesChanged': False, 'productionApplied': False}))
else:
    runpy.run_path(str(Path(__file__).with_name('require-full-gate.py')))
    gates = [ROOT / 'capability-evidence/diagnostics/af1082-f5-d7-local87cu1/full-verification.json',
             DRAFT / ('recovery24/runtime-3-verification.json' if args.with_ui_culture else 'recovery24/runtime-1-verification.json'),
             DRAFT / ('main-status36/runtime-5-verification.json' if args.with_ui_culture else 'main-status36/runtime-1-verification.json'),
             ROOT / 'capability-evidence/diagnostics/af1082-f5-d7-local87cu1/provider-verification.json']
    if args.with_ui_culture:
        runpy.run_path(str(Path(__file__).with_name('require-followup-evidence.py')))
        gates += [ui_qualification_path,
                  ROOT / 'capability-evidence/diagnostics/af1082-f5-d7-local87cu1/recovery24-ui-whole-image-review.json']
        review = read(gates[-1])
        verify_reference(review['audit'], gates[1])
        assert review['wholeImages'] == 52 and review['uniqueWholeImages'] == len(review['reviewedWholeImages']) == 2
        for image in review['reviewedWholeImages']: verify_reference(image)
    recovery, main, provider = (read(p) for p in gates[1:4])
    assert recovery['state'] == 'PASS_LOCAL87CU1_CURRENT_F5_D7_ORIGINAL_WPF24_NATIVE62_HISTORY_SAVE48_IMAGES52_PENDING_DIRECT_REVIEW'
    assert main['state'] == 'PASS_LOCAL87CU1_CURRENT_F5_D7_MAIN_STATUS_RECOVERY36_NATIVE62_144_FULL_IMAGES_PENDING_DIRECT_REVIEW'
    assert provider['state'] == 'CURRENT_F5_D7_ORIGINAL_PROVIDER_OUTCOMES_ACTUALLY_VERIFIED'
    application_path = ROOT / ui_qualification['application']['path'] if args.with_ui_culture else DRAFT / 'freeze.json'
    verify_reference(recovery['semanticBuild'], application_path)
    verify_reference(main['application'], application_path)
    verify_reference(provider['parent'], DRAFT / 'freeze.json')
    for proof in (recovery, main): verify_reference(proof['fullBoth7117'], gates[0])
    verify_reference(provider['fullGate'], gates[0])
    assert len(recovery['cases']) == 24 and len(recovery['images']) == 52 and len(recovery['saves']) == 48
    assert main['passed'] == 36 and main['failed'] == 0 and len(main['frames']) == 144
    assert {r['scenario'] for r in provider['scenarios']} == {'drawing', 'workspace'}
    for proof in (recovery, main):
        assert not proof['productionApplied'] and not proof['AFComplete']
        for key in ('terminal', 'result', 'binding'):
            verify_reference(proof[key])
    for case in provider['scenarios']:
        assert case['actualNative62Matched'] and case['assetsAfterClose'] == 0
        for key in ('terminal', 'result', 'binding', 'toolCalls'):
            verify_reference(case[key])
    out = ROOT / 'artifacts/af1082-f5-d7-root-integration-1'
    assert not out.exists(), 'Preserve existing integration and inspect its state'
    out.mkdir()
    before_bytes = {n: (ROOT / n).read_bytes() if n in original else None for n in changes}
    source_bytes = {n: (Path(freeze['workspace']) / n).read_bytes() for n in changes}
    applied = []
    try:
        for name in changes:
            target = ROOT / name
            replace_owned_file(target, source_bytes[name], original.get(name))
            applied.append(name)
            assert sha(target) == current[name]
        for name, digest in effective.items(): assert sha(ROOT / name) == digest, name
    except Exception:
        # Restore only files that still contain this transaction's exact bytes.
        for name in reversed(applied):
            target = ROOT / name
            if sha(target) != current[name]: continue
            if before_bytes[name] is None: target.unlink()
            else: replace_owned_file(target, before_bytes[name], current[name])
        raise
    replace_owned_file(out / 'source-receipt.json', (json.dumps({
        'recordedAt': datetime.now(timezone.utc).isoformat(),
        'state': 'D7_AND_EXACT_LOCAL87CU1_SOURCE_APPLIED_ROOT_BUILD_AND_RUNTIME_BINDING_PENDING',
        'candidate': ref(DRAFT / 'freeze.json'), 'prerequisites': [ref(p) for p in gates],
        'rootSource': [{'path': n, 'sha256': effective[n]} for n in effective],
        'existingProvenUiCultureDeltaPreserved': args.with_ui_culture,
        'changedPaths': changes, 'sourceApplied': True, 'rootBuildProven': False,
        'desktopAcceptanceProven': False, 'providerAllPassed': provider['allPassed'],
        'AFComplete': False, 'G': 'USER_DEFERRED', 'cleanup': False}, indent=2) + '\n').encode('utf-8'))
    print('Applied exact D7 source and development NuGet changes; root build/runtime and remaining acceptance are unproven.')
