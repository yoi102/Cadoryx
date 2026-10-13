from pathlib import Path
from datetime import datetime, timezone
import hashlib, json, shutil, xml.etree.ElementTree as ET

root = Path('C:/Users/yoiri/source/repos/Cadoryx')
draft = root / 'artifacts/af1076-root-full-1'
dest = root / 'capability-evidence/diagnostics/af1076-root-full'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
freeze = read(draft / 'freeze.json')
config = next(c for c in freeze['configurations'] if c['configuration'] == 'Release')
binary = Path(config['testDirectory'])
for p in freeze['sourcePayload']: assert sha(root / p['path']) == p['sha256']
for p in config['payload']: assert sha(binary / p['path']) == p['sha256']
folder = draft / 'full6652-release-1'
exit = read(folder / 'exit.json')
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
tree = ET.parse(folder / 'runtime.trx')
counts = tree.find('.//t:Counters', ns).attrib
results = tree.findall('.//t:UnitTestResult', ns)
assert len(results) == int(counts['total']) == 6652
assert int(counts['passed']) == 6628 and int(counts['failed']) == 24 and int(counts['notExecuted']) == 0
parent = read(root / 'capability-evidence/diagnostics/public-nuget-migration/public85-final1073-full-both-verification.json')
old = next(c for c in parent['configurations'] if c['configuration'] == 'Release')
oldTrx = Path(old['trx']['path'])
assert sha(oldTrx) == old['trx']['sha256']
oldResults = ET.parse(oldTrx).findall('.//t:UnitTestResult', ns)
names = {r.attrib['testName'] for r in results}
oldNames = {r.attrib['testName'] for r in oldResults}
assert len(names) == 6652 and len(oldNames) == 6619 and oldNames <= names
oldFailures = {r.attrib['testName'] for r in oldResults if r.attrib['outcome'] == 'Failed'}
failures = {r.attrib['testName'] for r in results if r.attrib['outcome'] == 'Failed'}
assert failures == oldFailures and len(failures) == 24 and exit['exitCode'] == 1
binding = read(folder / 'loaded-native.json')
expected = {r['name']: r['sha256'] for r in freeze['native62']}
assert len(binding['native']) == 62 and {r['name'] for r in binding['native']} == expected.keys()
for p in binding['native']:
    assert Path(p['path']).parent == binary / 'occt'
    assert sha(Path(p['path'])) == p['sha256'] == expected[p['name']]
dest.mkdir(parents=True, exist_ok=True)
refs = []
for name in ('runtime.trx', 'loaded-native.json', 'exit.json', 'stdout.log', 'stderr.log'):
    output = dest / ('release-' + name)
    shutil.copyfile(folder / name, output)
    refs.append({'path': output.relative_to(root).as_posix(), 'sha256': sha(output)})
for name in ('original-coordinator-terminal.json',):
    shutil.copyfile(draft / name, dest / name)
report = {'recordedAt': datetime.now(timezone.utc).isoformat(), 'state': 'AF1076_ROOT_RELEASE_FULL6652_VERIFIED_6628_PASS_24_ORIGINAL_FAILURES',
          'total': 6652, 'passed': 6628, 'failed': 24, 'skipped': 0, 'parent6619NamesRetained': True,
          'added33Names': sorted(names - oldNames), 'failures': sorted(failures), 'newFailureNames': [],
          'actualNative62Matched': True, 'originalStrictSTEPGateChanged': False, 'allPassed': False,
          'Debug': 'RESUMED_WITH_SAME_FROZEN_SOURCE_AND_BINARIES_NOT_TERMINAL', 'references': refs,
          'AFComplete': False, 'cleanup': False, 'G': 'USER_DEFERRED'}
output = dest / 'release-verification.json'
assert not output.exists()
output.write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps({'total': 6652, 'passed': 6628, 'failed': 24, 'newFailures': 0, 'native62': True}))
