"""Qualify a harness-only taskbar entry; never change frozen product inputs."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib, json, re, sys

ROOT = Path(__file__).resolve().parents[2]
mode, iteration = sys.argv[1], int(sys.argv[2])
assert mode in ('prepare', 'audit', 'check') and 1 <= iteration <= 100
STAGE = ROOT / f'artifacts/af-final87-{iteration}'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref = lambda p: dict(path=p.relative_to(ROOT).as_posix(), sha256=sha(p))
freeze = read(STAGE / 'freeze.json'); prep = read(STAGE / 'followup-preparation.json')
assert prep['freeze']['sha256'] == sha(STAGE / 'freeze.json')
for row in prep['hostSources']: assert sha(ROOT / row['path']) == row['sha256']
original = Path(freeze['workspace']) / 'tools/CadFiniteSurfaceWindowProbe'
folder = STAGE / 'targetable-window-host1'
source = (original / 'WindowHost.cs').read_bytes()
needle = b'            var editor=host.Editor;'
assert source.count(needle) == 1
delta = b'            host.ShowInTaskbar=true; // Harness-only Computer Use modal discovery.\n'
expected = source.replace(needle, delta + needle)
if mode == 'prepare':
    assert not folder.exists(); folder.mkdir()
    (folder / 'WindowHost.cs').write_bytes(expected)
    (folder / 'WindowHost.csproj').write_bytes((original / 'WindowHost.csproj').read_bytes())
    print('Isolated host prepared; only modal taskbar visibility added.'); sys.exit(0)
assert (folder / 'WindowHost.cs').read_bytes() == expected
assert (folder / 'WindowHost.csproj').read_bytes() == (original / 'WindowHost.csproj').read_bytes()
proof_path = STAGE / 'targetable-window-host1-verification.json'
if mode == 'check':
    proof = read(proof_path); assert proof['freeze'] == ref(STAGE / 'freeze.json')
    for row in proof['sources']: assert sha(ROOT / row['path']) == row['sha256']
    for host in proof['hosts']:
        for row in host['payload']: assert sha(Path(host['output']) / row['path']) == row['sha256']
    print('Targetable modal host and frozen application payload verified.'); sys.exit(0)
built = read(STAGE / 'targetable-window-host1-build.json'); assert len(built['stages']) == 2
assert {s['configuration'] for s in built['stages']} == {'Release', 'Debug'}
hosts = []
for stage in built['stages']:
    assert stage['exitCode'] == 0
    config = next(c for c in freeze['configurations'] if c['configuration'] == stage['configuration'])
    app, output = Path(config['candidate']), Path(stage['output']); assert Path(stage['candidate']) == app
    log = Path(stage['log']).read_text(encoding='utf-8-sig')
    assert not re.search(r'\b(?:warning|error)\s+(?:[A-Z]+\d+|:)', log, re.I)
    for row in config['appPayload']: assert sha(app / row['path']) == row['sha256']
    production = list(app.glob('Cadoryx.*.dll')); assert len(production) == 19
    for path in production: assert sha(output / path.name) == sha(path)
    assert {p.name: sha(p) for p in output.glob('OcctSharp*.dll')} == {r['name']: r['sha256'] for r in freeze['managed13']}
    assert {p.name: sha(p) for p in (output / 'occt').glob('*.dll')} == {r['name']: r['sha256'] for r in freeze['native62']}
    for path in app.glob('*/Cadoryx.Lang.resources.dll'): assert sha(output / path.parent.name / path.name) == sha(path)
    hosts.append(dict(stage, label='d6-window36', payload=[dict(path=p.relative_to(output).as_posix(), sha256=sha(p)) for p in sorted(output.rglob('*')) if p.is_file()]))
record = dict(recordedAt=datetime.now(timezone.utc).isoformat(), state='TARGETABLE_MODAL_HOST_TWO_CONFIG_BUILD0_EXACT_PRODUCT_PAYLOAD_RUNTIME_PENDING',
              freeze=ref(STAGE / 'freeze.json'), originalHostSources=[ref(original / n) for n in ('WindowHost.cs', 'WindowHost.csproj')],
              sources=[ref(folder / n) for n in ('WindowHost.cs', 'WindowHost.csproj')], hosts=hosts,
              soleSourceDelta='host.ShowInTaskbar=true', productChanged=False, assertionsChanged=False, hostInjectsInput=False,
              readinessSeconds=30, D6DeadlineSeconds=300, D6MatrixCount=36, AFComplete=False)
assert not proof_path.exists(); proof_path.write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
print('Two targetable modal hosts qualified; app19/SDK13/native62 unchanged; actual UI pending.')
