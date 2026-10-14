"""Audit one actual complete configuration without opening the both-full gate."""
from pathlib import Path
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
iteration, configuration = int(sys.argv[1]), sys.argv[2]
assert 1 <= iteration <= 100 and configuration in ('Release', 'Debug')
stage = ROOT / f'artifacts/af-final87-{iteration}'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref = lambda p: dict(path=p.relative_to(ROOT).as_posix(), sha256=sha(p))
freeze = read(stage / 'freeze.json')
config = next(c for c in freeze['configurations'] if c['configuration'] == configuration)
outcome = stage / f'full-{configuration.lower()}-1'
terminal = read(outcome / 'exit.json')
assert terminal['configuration'] == configuration and terminal['exitCode'] == 0
assert terminal['actualNativeBinding'] and not terminal['STEP24Retried']
assert freeze['expectedExecuted'] == freeze['expectedTotal'] - 24 and len(freeze['deferredNames']) == 24
for row in freeze['candidateSource']:
    assert sha(Path(freeze['workspace']) / row['path']) == row['sha256']
for row in config['payload']:
    assert sha(Path(config['testDirectory']) / row['path']) == row['sha256']
tree = ET.parse(outcome / 'runtime.trx').getroot()
ns = {'t': tree.tag.split('}')[0].strip('{')}
rows = tree.findall('.//t:UnitTestResult', ns)
assert len(rows) == freeze['expectedExecuted'] and all(r.attrib['outcome'] == 'Passed' for r in rows)
assert len({r.attrib['testId'] for r in rows}) == len(rows)
expected = Counter(config['discoveredNames'])
assert sum(expected.values()) == freeze['expectedTotal']
for name in freeze['deferredNames']:
    assert expected[name] == 1
    del expected[name]
assert Counter(r.attrib['testName'] for r in rows) == expected
binding = read(outcome / 'loaded-native.json')
assert len(binding['native']) == 62 and {r['name']: r['sha256'] for r in binding['native']} == {r['name']: r['sha256'] for r in freeze['native62']}
for row in binding['native']:
    assert Path(row['path']).parent == Path(config['testDirectory']) / 'occt' and sha(Path(row['path'])) == row['sha256']
record = dict(recordedAt=datetime.now(timezone.utc).isoformat(), state='FINAL87_SINGLE_FULL_CONFIGURATION_VERIFIED_BOTH_GATE_SEPARATE',
              configuration=configuration, freeze=ref(stage / 'freeze.json'), version=freeze['version'],
              discovered=freeze['expectedTotal'], passed=len(rows), failed=0, skipped=0, exactUserDeferredNotExecuted=24,
              terminal=ref(outcome / 'exit.json'), trx=ref(outcome / 'runtime.trx'), actualNative62=ref(outcome / 'loaded-native.json'),
              namesAndMultiplicityEqualDiscoveryMinusExact24=True, bothConfigurationsProved=False,
              modelOrWindowAccepted=False, productionApplied=False, AFComplete=False)
out = ROOT / f'capability-evidence/diagnostics/af-final87/candidate{iteration}-full-{configuration.lower()}-verification.json'
assert not out.exists()
out.write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
print(f'{configuration}: actual {len(rows)} passed; exact discovery minus24 and native62 verified; both-full gate remains separate.')
