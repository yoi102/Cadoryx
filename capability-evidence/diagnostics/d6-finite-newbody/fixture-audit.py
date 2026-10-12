from pathlib import Path
import datetime, hashlib, json, zipfile

root = Path('C:/Users/yoiri/source/repos/Cadoryx')
task = root / 'artifacts/d6-window-fixtures-2'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref = lambda p: {'path': str(p), 'sha256': sha(p)}
build_path = root / 'artifacts/d6-finite-newbody-draft-4/build-both-1-verification.json'
build = read(build_path)
payload = task / 'bin/Release/net10.0'
assert read(task / 'build-release-1-exit.json')['exitCode'] == 0
log = (task / 'build-release-1.log').read_text(encoding='utf-8-sig')
assert '0 Warning(s)' in log and '0 Error(s)' in log
assert (task / 'build-release-1-stderr.log').stat().st_size == 0
terminal = read(task / 'runtime-1-exit.json')
assert terminal['exitCode'] == 0 and not terminal['actualWpfWindowsStarted']
assert sha(payload / 'FixtureWriter.dll') == terminal['dllSha256']
assert (task / 'runtime-1-stderr.log').stat().st_size == 0
for filename in ('FixtureWriter.cs', 'FixtureWriter.csproj'):
    assert sha(task / filename) == sha(root / 'capability-evidence/diagnostics/d6-finite-newbody' / filename)
expected = {r['path']: r['sha256'] for r in build['configurations']['Release']['testPayload']}
application = []
for filename in payload.glob('*.dll'):
    if filename.name == 'FixtureWriter.dll':
        continue
    assert filename.name in expected and sha(filename) == expected[filename.name]
    application.append(ref(filename))
assert {r['name']: sha(payload / r['name']) for r in build['sdk13']} == {r['name']: r['sha256'] for r in build['sdk13']}
result_path = task / 'fixtures-1/result.json'
result = read(result_path)
assert len(result['rows']) == 6 and result['assetsRemaining'] == 0
assert not result['desktopInputStarted'] and not result['actualWpfWindowAcceptance']
assert {(r['kind'], r['negative']) for r in result['rows']} == {(k, n) for k in ('plane', 'sphere', 'cylinder') for n in (False, True)}
assert len(result['native']) == 62
assert {r['name']: r['sha256'] for r in result['native']} == {r['name']: r['sha256'] for r in build['native62']}
for row in result['native']:
    assert Path(row['path']).parent.resolve() == (payload / 'occt').resolve()
    assert sha(Path(row['path'])) == row['sha256']
archives = []
for row in result['rows']:
    path = Path(row['path'])
    assert sha(path) == row['sha256'] and row['savedReopened'] and row['originalBodiesPreserved'] and row['seedAssetNotAuthored']
    with zipfile.ZipFile(path) as z:
        manifest = json.loads(z.read('manifest.json'))
        assert manifest['applicationVersion'] == '0.7.11' and manifest['documentId']['value'] == row['documentId']['Value']
        assert 'cadoryx.finite-surface-new-body.1' in manifest['requiredCapabilities']
        formats = {s['kind']: s['schemaVersion'] for s in manifest['sections']}
        assert formats['document'] == 60 and formats['features'] == 43
        for section in manifest['sections']:
            data = z.read(section['path'])
            assert len(data) == section['length'] and hashlib.sha256(data).hexdigest() == section['sha256']
        archives.append({'fixture': row, 'manifest': manifest})
failed = root / 'artifacts/d6-window-fixtures-1'
assert read(failed / 'build-release-1-exit.json')['exitCode'] == 1
assert 'CS1503' in (failed / 'build-release-1.log').read_text(encoding='utf-8-sig')
out = root / 'capability-evidence/AF_PENDING_D6_WINDOW_FIXTURES_VERIFICATION.json'
assert not out.exists()
out.write_text(json.dumps({'recordedAt': datetime.datetime.now(datetime.timezone.utc).isoformat(),
 'state': 'PASS_SIX_D6_FIXTURES_CREATED_EXACT_REPLAY_REOPEN_NATIVE62_ACTUAL_WINDOWS_NOT_RUN',
 'build': ref(build_path), 'fixtureBuild': ref(task / 'build-release-1-exit.json'),
 'fixtureBuildLog': ref(task / 'build-release-1.log'), 'fixtureRuntime': ref(task / 'runtime-1-exit.json'),
 'fixtureResult': ref(result_path), 'source': [ref(task / f) for f in ('FixtureWriter.cs', 'FixtureWriter.csproj')],
 'copiedCandidateDlls': application, 'native62': result['native'], 'archives': archives,
 'firstFixtureBuildFailure': ref(failed / 'build-release-1.log'),
 'firstFixtureSource': ref(failed / 'FixtureWriter.cs'),
 'firstFailureCorrection': 'Only changed the storage constructor to named surfaceExtrusionResolver; no product code or runtime guard changed. Added structural profile comparison for reloaded records before the successful build.',
 'assetsRemaining': 0, 'actualWpfWindowAcceptance': False, 'desktopInputStarted': False,
 'full6619': 'QUEUED_NOT_RUN', 'productionApplied': False, 'cleanup': False, 'AF': 'ACTIVE', 'G': 'USER_DEFERRED'
}, indent=2)+'\n', encoding='utf-8')
print('PASS six exact archives, full section hashes, native62, candidate copied DLLs, original failed build retained; WPF acceptance separate.')
