"""Preserve the original material observer, renderer cases and picker fixtures."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json
import shutil
import sys

ROOT = Path(__file__).resolve().parents[2]
iteration = int(sys.argv[1])
assert 1 <= iteration <= 100
STAGE = ROOT / f'artifacts/af-final87-{iteration}'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref = lambda p: dict(path=p.relative_to(ROOT).as_posix(), sha256=sha(p))
freeze = read(STAGE / 'freeze.json')
app = Path(next(c for c in freeze['configurations'] if c['configuration'] == 'Release')['candidate'])
base = ROOT / 'artifacts/af-sdk30-history-identity-draft-5/material82-1'
base_prep = read(base / 'preparation-1.json')
original_program = Path(base_prep['originalProgram']['path'])
assert sha(original_program) == base_prep['originalProgram']['sha256']
prior = (base / 'Program.cs').read_text(encoding='utf-8-sig')
assert prior.replace('8.0.1-preview.31.cadoryx.generated-defeature-history.30', '8.0.1-preview.31.cadoryx.curve-inspection.26').replace('Expected SDK30.', 'Expected SDK26.').replace('unified SDK30 viewport', 'revision9 SDK26 viewport') == original_program.read_text(encoding='utf-8-sig')
program = prior.replace('8.0.1-preview.31.cadoryx.generated-defeature-history.30', freeze['version'])
program = program.replace('Expected SDK30.', 'Expected final87.').replace('unified SDK30 viewport', 'final87 viewport')
assert program.replace(freeze['version'], '8.0.1-preview.31.cadoryx.generated-defeature-history.30').replace('Expected final87.', 'Expected SDK30.').replace('final87 viewport', 'unified SDK30 viewport') == prior
directory = STAGE / 'material82'
assert not directory.exists()
directory.mkdir()
(directory / 'Program.cs').write_text(program, encoding='utf-8')
shutil.copyfile(base / 'Directory.Build.props', directory / 'Directory.Build.props')
# Absolute references consume the qualified application; no product or SDK
# implementation is compiled by this auxiliary renderer acceptance project.
(directory / 'MaterialAuthoringConsumer.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
 <PropertyGroup><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
 <ItemGroup>
  <Compile Include="Program.cs" />
  <Reference Include="{app.as_posix()}/*.dll"><Private>true</Private></Reference>
  <Content Include="{app.as_posix()}/occt/*.*"><Link>occt/%(Filename)%(Extension)</Link><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></Content>
  <None Update="expected.json" CopyToOutputDirectory="PreserveNewest" />
 </ItemGroup>
</Project>
''', encoding='utf-8')
names = [r['name'] for r in read(base / 'expected.json')['managed']]
assert len(names) == len(set(names)) == 30
managed = [dict(name=n, sha256=sha(app / n)) for n in names]
expected = dict(build=ref(STAGE / 'freeze.json'), sourceCount=len(freeze['rootSource']), managed=managed, native=freeze['native62'])
(directory / 'expected.json').write_text(json.dumps(expected, indent=2) + '\n', encoding='utf-8')
observer_proof = read(ROOT / 'artifacts/a5-sdk27-material-desktop-observer-1/build-4-exit.json')
observer = []
for row in observer_proof['source']:
    original = Path(row['path'])
    current = ROOT / 'tools/CadMaterialDesktopProbe' / original.name
    assert sha(original) == sha(current) == row['sha256']
    observer.append(dict(original=ref(original), current=ref(current), byteIdentical=True))
fixtures = read(ROOT / 'artifacts/a5-sdk27-material-desktop-observer-1/fixtures-1/preparation-1.json')
for key in ('image', 'invalidImage'):
    assert sha(Path(fixtures[key]['path'])) == fixtures[key]['sha256']
assert fixtures['width'] == 64 and fixtures['height'] == 32
fixture_dir = STAGE / 'material-fixtures'
assert not fixture_dir.exists()
fixture_dir.mkdir()
current_fixtures = {}
for key in ('image', 'invalidImage'):
    original = Path(fixtures[key]['path'])
    current = fixture_dir / original.name
    shutil.copyfile(original, current)
    current_fixtures[key] = ref(current)
record = dict(recordedAt=datetime.now(timezone.utc).isoformat(), state='FINAL87_ORIGINAL_MATERIAL82_AND_READ_ONLY_UI_OBSERVER_PREPARED_NOT_RUNTIME',
              freeze=ref(STAGE / 'freeze.json'), originalProgram=ref(original_program), priorProgram=ref(base / 'Program.cs'),
              projectedProgram=ref(directory / 'Program.cs'), programChangesOnlyRuntimeIdentity=True,
              sourcePayload=[ref(p) for p in sorted(directory.iterdir()) if p.is_file()], observer=observer,
              expectedManaged30=managed, native62=freeze['native62'], original82=base_prep['original82'],
              originalImageReview=ref(ROOT / 'artifacts/a5-material-authoring-native-consumer-1/images-1-verification.json'),
              pickerFixtures=current_fixtures, rgbaPixelSha256=fixtures['rgbaPixelSha256'], originalPlanned=82, originalFrames=346,
              originalObserverReadySeconds=60, originalDesktopMinutes=30, cultures=[2052, 1033, 1041], themes=['dark', 'light'],
              realPickerAndProductionActionsRequired=True, desktopInputStarted=False, runtimeStarted=False, AFComplete=False)
out = STAGE / 'material-preparation.json'
assert not out.exists()
out.write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
print('Original material82 cases and exact observer/fixtures prepared for final87; runtime NOT RUN.')
