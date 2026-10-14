"""Audit original material UI workflow using its actual fresh process identities."""
import copy,datetime,hashlib,json,struct,subprocess,sys,zipfile
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
iteration=int(sys.argv[1]);assert 1<=iteration<=100
STAGE=ROOT/f'artifacts/af-final87-{iteration}'
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
ref=lambda p:dict(path=str(p.resolve()),sha256=sha(p))
for name in ('require-followup-runtime.py','audit-material-build.py','audit-material82.py'):
 subprocess.run([sys.executable,str(Path(__file__).with_name(name)),str(iteration)],check=True)
freeze=read(STAGE/'freeze.json');material_prep=read(STAGE/'material-preparation.json');built=read(STAGE/'material-build-verification.json')
assert material_prep['freeze']['sha256']==sha(STAGE/'freeze.json')==built['freeze']['sha256']
config=next(c for c in freeze['configurations'] if c['configuration']=='Release')
app=dict(candidate=config['candidate'],native=freeze['native62'])
for row in config['appPayload']:assert sha(Path(app['candidate'])/row['path'])==row['sha256']
observer=next(r for r in built['builds'] if r['label']=='material-observer')
observer_path=Path(observer['output'])/'MaterialDesktopObserver.dll'
observer_sha=next(r['sha256'] for r in observer['payload'] if r['path']=='MaterialDesktopObserver.dll')
for row in observer['payload']:assert sha(Path(observer['output'])/row['path'])==row['sha256']
directory=STAGE/'material-desktop1';terminal_path=STAGE/'material-desktop1-exit.json'
start=read(STAGE/'material-desktop1-start.json');pid=start['pid']
assert start['freezeSHA256']==sha(STAGE/'freeze.json') and start['originalReadySeconds']==60 and start['originalDeadlineMinutes']==30
terminal = read(terminal_path)
assert terminal['state'] == 'PROCESS_EXIT0_OBSERVATIONS_RECORDED_PENDING_INDEPENDENT_MANUAL_UI_ASSERTIONS'
assert terminal['exitCode'] == 0 and terminal['pid'] == pid and terminal['elapsedMs'] < 30*60*1000
assert Path(terminal['application']).resolve() == Path(app['candidate']).resolve()
assert Path(terminal['observer']) == observer_path and sha(observer_path) == observer_sha
assert not list(directory.glob('observation-failure-*')) and not (directory/'observer-failure.json').exists()
binding_path = Path(terminal['actualNative'])
binding = read(binding_path)
assert binding['processId'] == pid and len(binding['native']) == 62
assert {r['name']:r['sha256'] for r in binding['native']} == {r['name']:r['sha256'] for r in app['native']}
for row in binding['native']:
    assert Path(row['path']).resolve().parent == (Path(app['candidate'])/'occt').resolve()
    assert sha(Path(row['path'])) == row['sha256']
markers = {}
observed = {}
for p in sorted(directory.glob('sky-*.json')):
    stage = read(p)
    path = Path(stage['observationPath'])
    assert path.resolve().parent == directory.resolve()
    observation = read(path)['observation']
    assert observation['pid'] == pid
    markers[stage['stage']] = dict(marker=ref(p), observation=ref(path))
    observed[p.name] = observation
def at(prefix):
    candidates = [o for n,o in observed.items() if n.startswith('sky-'+prefix+'-')]
    assert len(candidates) == 1, prefix
    return candidates[0]
def unchanged(a,b):
    for k in ('activeSession','documentId','stateId','materials','bodies','assetsCount','assetsBytes','isDirty'):
        assert a[k] == b[k], k
first, cancel, invalid, draft, add, edited, noop, apply = [at(s) for s in ('01','02','03','04','05','06','07','08')]
unchanged(first,cancel)
assert first['materialDraft'] == cancel['materialDraft']
unchanged(cancel,invalid)
expected = copy.deepcopy(cancel['materialDraft']); expected['status'] = '请选择单张 PNG 或 JPEG 图像。'
assert invalid['materialDraft'] == expected
assert expected['status'] in read(directory/'sky-03-invalid-png-rejected.json')['accessibility']['tree']
unchanged(invalid,draft)
assert draft['materials'] == [] and draft['assetsCount'] == 0
fixture_path = STAGE/'material-preparation.json'
fixture = dict(rgbaPixelSha256=material_prep['rgbaPixelSha256'], **{k:dict(path=str((ROOT/v['path']).resolve()),sha256=v['sha256']) for k,v in material_prep['pickerFixtures'].items()})
assert draft['materialDraft']['pendingPixels'] == dict(width=64,height=32,bytes=8192,rgbaSha256=fixture['rgbaPixelSha256'])
for k in ('image','invalidImage'):
    assert ref(Path(fixture[k]['path'])) == fixture[k]
assert add['stateId'] != first['stateId'] and add['assetsCount'] == 1 and len(add['materials']) == 1
assert add['materialDraft']['pendingPixels'] is None
material_id = add['materials'][0]['Id']['Value']
assert material_id and material_id != first['documentId']['Value']
texture_id = '5d31648c3eb9a8fb291a0277ee23594f466d77863429693dbe5463898c97df2a'
assert add['materials'][0]['Id']['Value'] == material_id
assert add['materials'][0]['Appearance']['Texture']['AssetId']['Sha256'] == texture_id
unchanged(add,edited)
assert edited['materialDraft']['appearance']['Front']['Roughness'] == .28
assert edited['materials'][0]['Appearance']['Front']['Roughness'] == .5
assert noop == edited  # Original offscreen input did not apply; preserved as NO-OP.
assert apply['stateId'] != edited['stateId'] and apply['assetsCount'] == 1
assert apply['materials'][0]['Appearance']['Front']['Roughness'] == .28
assert apply['materials'][0]['Appearance']['Back']['Roughness'] == .5
assert apply['materials'][0]['DensityKgPerMm3'] == 7.85e-6
created, assigned, visible, undo, redo, saved, closed, reopened = [at(s) for s in ('09','10','11','12','13','14','15','17')]
assert len(created['bodies']) == 1 and created['bodies'][0]['materialId'] is None
assert assigned['bodies'][0]['materialId']['Value'] == material_id
unchanged(assigned,visible)
assert undo['bodies'] == created['bodies'] and undo['stateId'] == created['stateId']
assert redo['bodies'] == assigned['bodies'] and redo['stateId'] == assigned['stateId']
assert saved['bodies'] == reopened['bodies'] == assigned['bodies']
assert saved['materials'] == reopened['materials'] == apply['materials']
assert saved['documentId'] == reopened['documentId'] == first['documentId']
assert saved['stateId'] == reopened['stateId'] == assigned['stateId']
assert not saved['isDirty'] and not reopened['isDirty'] and saved['activeSession'] != reopened['activeSession']
assert saved['assetsCount'] == reopened['assetsCount'] == 2
geometry = assigned['bodies'][0]['geometry']
assert geometry['VolumeMm3'] == 24000 and geometry['Revision']['Value'] == created['bodies'][0]['geometry']['Revision']['Value']
for n in ('09','10','12','13','14','17'):
    assert at(n)['bodies'][0]['geometry'] == geometry
assert closed['activeSession'] is None and closed['observedSessions'][0]['isClosing']
assert closed['observedSessions'][0]['assetsCount'] == closed['observedSessions'][0]['assetsBytes'] == 0
themes = []
for culture, dark, top, bottom, picker, cancelled in (
    (2052,True,'01','03','01','02'),(2052,False,'18','19','20','21'),
    (1033,False,'22','23','24b','25'),(1033,True,'26','27','28','29'),
    (1041,True,'30','31','32','33'),(1041,False,'34','35','36','37')):
    for index in (top,bottom,picker,cancelled):
        o=at(index);assert o['culture']==culture and o['general']['IsDarkTheme']==dark
    unchanged(at(picker),at(cancelled))
    assert at(top)['materialDraft'] == at(cancelled)['materialDraft']
    assert not at(cancelled)['materialDraft']['isBusy']
    if top!='01': assert at(picker)['materialDraft']['isBusy']
    if top!='01':
        unchanged(reopened,at(cancelled))
        assert at(cancelled)['materialDraft']['appearance']['Texture']['AssetId']['Sha256']==texture_id
    themes.append(dict(culture=culture,dark=dark,top=top,bottom=bottom,picker=picker,cancelled=cancelled,
                       visualReview='PENDING_SEPARATE_ACTUAL_SCREENSHOT_REVIEW'))
last=at('39b')
assert last['activeSession'] is None and len(last['observedSessions'])==2
assert all(s['isClosing'] and s['assetsCount']==s['assetsBytes']==0 for s in last['observedSessions'])

# Independent bounded MessagePack reader for the existing array/string/bin/numeric storage keys.
def unpack(data):
    offset=0
    def take(n):
        nonlocal offset
        result=data[offset:offset+n];assert len(result)==n;offset+=n;return result
    def value():
        c=take(1)[0]
        if c<=0x7f:return c
        if c>=0xe0:return c-256
        if 0x90<=c<=0x9f:return [value() for _ in range(c-0x90)]
        if 0xa0<=c<=0xbf:return take(c-0xa0).decode('utf-8')
        if c==0xc0:return None
        if c in (0xc2,0xc3):return c==0xc3
        numbers={0xca:('>f',4),0xcb:('>d',8),0xcc:('>B',1),0xcd:('>H',2),0xce:('>I',4),0xcf:('>Q',8),0xd0:('>b',1),0xd1:('>h',2),0xd2:('>i',4),0xd3:('>q',8)}
        if c in numbers:
            fmt,n=numbers[c];return struct.unpack(fmt,take(n))[0]
        sizes={0xc4:1,0xc5:2,0xc6:4,0xd9:1,0xda:2,0xdb:4,0xdc:2,0xdd:4}
        assert c in sizes,hex(c)
        n=int.from_bytes(take(sizes[c]),'big')
        if c in (0xdc,0xdd):return [value() for _ in range(n)]
        raw=take(n);return raw.decode('utf-8') if c in (0xd9,0xda,0xdb) else raw
    result=value();assert offset==len(data);return result
saved_path=directory/'actual-ui-material-1.cadoryx'
with zipfile.ZipFile(saved_path) as archive:
    manifest=json.loads(archive.read('manifest.json'))
    assert manifest['documentId']['value']==saved['documentId']['Value']
    assert manifest['stateId']['value']==saved['stateId']['Value']
    assert len(manifest['assets'])==2 and len(manifest['sections'])==19
    for row in manifest['sections']+manifest['assets']:
        content=archive.read(row['path']);assert len(content)==row['length'] and hashlib.sha256(content).hexdigest()==row['sha256']
    texture=archive.read('assets/'+texture_id+'.bin')
    assert texture[:8]==b'CDXRGBA1' and struct.unpack('<4i',texture[8:24])==(1,64,32,0)
    assert len(texture)==8216 and hashlib.sha256(texture[24:]).hexdigest().upper()==fixture['rgbaPixelSha256']
    presentation=unpack(archive.read('sections/presentation.msgpack'))
    assert presentation[1]==[[material_id,'A5 UI Checker',7.85e-6]]
    appearances=unpack(archive.read('sections/material-appearances.msgpack'))
    assert len(appearances[0])==1 and appearances[0][0][0]==material_id
    stored_appearance=json.loads(appearances[0][0][1])
    assert stored_appearance['front']['roughness']==.28 and stored_appearance['back']['roughness']==.5
    assert stored_appearance['texture']['assetId']['sha256']==texture_id
out=ROOT/f'capability-evidence/diagnostics/af-final87/candidate{iteration}-material-desktop-verification.json'
assert not out.exists()
record=dict(recordedAt=datetime.datetime.now(datetime.timezone.utc).isoformat(),
 state='FINAL87_ACTUAL_MATERIAL_UI_STATE_AND_FILE_WORKFLOW_VERIFIED_VISUAL_REVIEW_SEPARATE',
 freeze=ref(STAGE/'freeze.json'),preparation=ref(fixture_path),start=ref(STAGE/'material-desktop1-start.json'),terminal=ref(terminal_path),binding=ref(binding_path),
 actualPid=pid,actualMaterialId=material_id,actualGeometryRevision=geometry['Revision']['Value'],markers=markers,themes=themes,saved=ref(saved_path),
 assetCountAfterBothClosed=0,physicalDensity=7.85e-6,textureRgbaMatched=True,
 originalOffscreenApplyFailure='PRESERVED_IN_PRIOR_CAPTURE_NOT_REQUIRED_TO_RECREATE',
 priorMaterialUi=ref(ROOT/'artifacts/a5-sdk27-material-desktop-3-verification.json'),
 directCurrentVisualReview='PENDING_SEPARATE_ACTUAL_SCREENSHOTS_AND_DENSITY_DISPLAY',
 productionApplied=False,physicalGpuLoss=False,AFComplete=False)
out.write_text(json.dumps(record,indent=2)+'\n',encoding='utf-8')
print('Actual material picker/draft/add/apply/assign/undo/redo/save/reopen/six themes and two closed sessions verified; current visual review remains separate.')
