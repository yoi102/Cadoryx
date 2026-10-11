"""Validate native/STEP/FreeCAD observations without treating measurements as topology identity."""
import hashlib,json,sys
from pathlib import Path

native_path,external_path,returned_path,output=map(Path,sys.argv[1:5])
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
native=read(native_path);external=read(external_path);returned=read(returned_path)
assert external['passed'] and external['inspectionSha256']==sha(native_path)
source,step=native['records'];back=returned['records'][0]
assert source['path'].lower().endswith('.xbf') and step['path'].lower().endswith('.step')
for record in [source,step,back]:assert sha(record['path'])==record['sha256'].lower()
assert external['inputSha256']==step['sha256'].lower() and external['returnedSha256']==back['sha256'].lower()
assert native['runtime']['ManagedVersion']=='8.0.1-preview.31.cadoryx.curve-inspection.10'
assert returned['runtime']==native['runtime']
def point(p):return [p[k] for k in ['X','Y','Z']]
def near(a,b,tolerance):return len(a)==len(b) and max(abs(x-y) for x,y in zip(a,b))<=tolerance
def faces(record):
 assert len(record['bodies'])==1
 body=record['bodies'][0]
 assert body['valid'] and body['solids']==4 and body['faces']==24 and abs(body['volume']-96)<=1e-6
 return [s for s in body['slots'] if s['kind']=='Face']
def same_measure(a,b):
 x,y=a['surface'],b['surface']
 return abs(x['area']-y['area'])<=1e-5 and near(point(x['center']),point(y['center']),1e-5) and all(
  near(point(x['bounds'][k]),point(y['bounds'][k]),1e-5) for k in ['Minimum','Maximum'])
def color(c):return [c[k] for k in ['Red','Green','Blue','Alpha']]
source_faces=faces(source);pairs=[]
for target,channels,layers in [(step,['Surface','Curve'],True),(back,['Surface'],False)]:
 used=set();target_faces=faces(target)
 for face in target_faces:
  matches=[s for s in source_faces if same_measure(s,face)]
  assert len(matches)==1
  original=matches[0];assert original['index'] not in used;used.add(original['index'])
  for channel in channels:
   actual=face['colors'][channel] if layers else face['effectiveSurface']
   assert near(color(original['colors'][channel]),color(actual),1e-7),(target['path'],face['index'],channel)
  if layers:assert sorted(original['layers'])==sorted(face['layers'])
 assert len(used)==24
 pairs.append(dict(path=target['path'],facePairs=24,channelsVerified=channels,layersVerified=layers,
   hiddenFaces=sum(not f['colors']['IsVisible'] for f in target_faces),
   layeredFaces=sum(bool(f['layers']) for f in target_faces),visualMaterialFaces=sum(f['visualMaterial'] is not None for f in target_faces)))
assert len(external['facePairs'])==24 and external['uniqueColors']==6
assert len(step['hierarchy'])==2 and step['hierarchy'][0]['assembly'] and step['hierarchy'][1]['reference']
report=dict(passed=True,nativeInspectionSha256=sha(native_path),externalResultSha256=sha(external_path),returnInspectionSha256=sha(returned_path),
   software='FreeCAD '+'.'.join(external['version'][:3]),externalOcct=external['occt'],surfaceColors=6,facePairsPerLeg=24,
   nativeHiddenFaces=sum(not f['colors']['IsVisible'] for f in source_faces),nativeLayeredFaces=sum(bool(f['layers']) for f in source_faces),
   nativeVisualMaterialFaces=sum(f['visualMaterial'] is not None for f in source_faces),
   nativePhysicalMaterial=source['bodies'][0]['slots'][0]['material'],roundtrips=pairs,
   topologyCounts=[dict(path=r['path'],counts={k:sum(s['kind']==k for s in r['bodies'][0]['slots'])
      for k in sorted({s['kind'] for s in r['bodies'][0]['slots']})}) for r in [source,step,back]],
   scope='Bounded four-solid separated planar fixture: exact native presentation persisted; STEP surface/curve channels and per-face layers; actual external GUI surface colors and hierarchy; external STEP return surface colors',
   losses=['STEP readback loses four hidden-face flags, per-face PBR and physical material in this fixture',
     'FreeCAD provides no face-layer property; returned STEP face layers are observed separately',
     'FreeCAD return includes additional edge/vertex compounds; full native topology graph differs and is not an exact geometry-equivalence pass',
     'No persistent topology, source PMI or all-reader interoperability claim',
     'Offscreen view-provider inspection does not prove visible OpenGL output or pointer interaction'])
output.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print('MULTICOPY_EXTERNAL_PRESENTATION PASS: 24 complete face/color pairs per leg, six colors, native and external hierarchy observed.')
