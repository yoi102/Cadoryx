"""Observe bounded STEP face presentation using real FreeCAD GUI view providers.

Face measurements pair this separated planar fixture for interoperability only.
They do not establish persistent topology identity or authorize any CAD rebinding.
"""
import hashlib,json,sys,traceback
from pathlib import Path
import FreeCAD as App
import FreeCADGui as Gui
Gui.showMainWindow()
import ImportGui,Part

inspection=Path(sys.argv[1]).resolve()
output=Path(sys.argv[2]).resolve()
output.mkdir(parents=True,exist_ok=False)
records=json.loads(inspection.read_text(encoding='utf-8'))['records']
native=next(r for r in records if r['path'].lower().endswith('.xbf'))
step=next(r for r in records if r['path'].lower().endswith('.step'))
file=Path(step['path']);document=None
def sha(path):return hashlib.sha256(Path(path).read_bytes()).hexdigest()
def point(p):return [p[k] for k in ['X','Y','Z']]
def srgb(x):return 12.92*x if x<=.0031308 else 1.055*x**(1/2.4)-.055
def close(a,b,tolerance):return len(a)==len(b) and max(abs(x-y) for x,y in zip(a,b))<=tolerance
def face_bounds(face):
 b=face.BoundBox
 return [b.XMin,b.YMin,b.ZMin,b.XMax,b.YMax,b.ZMax]
def expected_bounds(bounds):return point(bounds['Minimum'])+point(bounds['Maximum'])
report=dict(passed=False,software='FreeCAD',version=App.Version(),occt=Part.OCC_VERSION,
 executable=sys.executable,executableSha256=sha(sys.executable),module=App.__file__,moduleSha256=sha(App.__file__),
 inspection=str(inspection),inspectionSha256=sha(inspection),input=str(file),inputSha256=sha(file),
 basis='Actual ImportGui STEP transfer and ShapeAppearance of native GUI view providers; unique complete bounded planar face measurement/color pairs only',
 limitations=['No persistent topology identity or automatic rebinding across exchange',
  'No external PMI, physical material or PBR fidelity claim',
  'Per-face hidden state and per-face layers are separately observed; surface-color success does not prove their preservation',
  'Offscreen GUI view-provider inspection does not prove desktop pointer input or visible OpenGL rendering'])
try:
 assert sha(file)==step['sha256'].lower()
 expected=[s for b in native['bodies'] for s in b['slots'] if s['kind']=='Face']
 assert len(expected)==24 and len(native['bodies'])==1
 document=App.newDocument('CadoryxMulticopyPresentation')
 ImportGui.insert(str(file),document.Name);document.recompute()
 features=[o for o in document.Objects if o.isDerivedFrom('Part::Feature')]
 assert len(features)==1
 feature=features[0];shape=feature.Shape;appearances=feature.ViewObject.ShapeAppearance
 assert shape.isValid() and len(shape.Solids)==4 and len(shape.Faces)==24 and len(appearances)==24
 assert abs(shape.Volume-96)<=1e-6
 used=set();pairs=[]
 for number,(face,material) in enumerate(zip(shape.Faces,appearances)):
  center=list(face.CenterOfMass);bounds=face_bounds(face)
  matching=[s for s in expected if close(point(s['surface']['center']),center,1e-5) and abs(s['surface']['area']-face.Area)<=1e-5
    and close(expected_bounds(s['surface']['bounds']),bounds,1e-5)]
  assert len(matching)==1,(number,center,len(matching))
  original=matching[0];assert original['index'] not in used;used.add(original['index'])
  color=original['colors']['Surface'];reference=[srgb(color[k]) for k in ['Red','Green','Blue']]
  actual=list(material.DiffuseColor)
  assert close(reference,actual[:3],2e-6),(number,reference,actual)
  pairs.append(dict(face=number+1,nativeMeasuredSlot=original['index'],center=center,area=face.Area,bounds=bounds,
    expectedSrgb=reference,actualSrgb=actual,colorDelta=max(abs(a-b) for a,b in zip(reference,actual[:3]))))
 assert len(used)==24
 groups=[p for p in feature.InList if p.TypeId=='App::Part']
 assert len(groups)==1 and feature.Label==step['bodies'][0]['name']
 root=next(h for h in step['hierarchy'] if h['parent']=='')
 assert groups[0].Label==root['name']
 returned=output/'freecad-styled-return.step'
 ImportGui.export([groups[0]],str(returned))
 assert returned.exists() and returned.stat().st_size>0
 report.update(passed=True,facePairs=pairs,uniqueColors=len({tuple(round(v,5) for v in p['actualSrgb'][:3]) for p in pairs}),
   geometry=dict(valid=shape.isValid(),solids=len(shape.Solids),faces=len(shape.Faces),volume=shape.Volume),
   hierarchy=[dict(name=o.Name,label=o.Label,type=o.TypeId,parents=[p.Name for p in o.InList]) for o in [groups[0],feature]],
   returned=str(returned),returnedSha256=sha(returned),
   nativeHiddenFaces=sum(not s['colors']['IsVisible'] for s in expected),freecadBodyVisible=feature.ViewObject.Visibility,
   faceLayerProperties=[p for p in feature.PropertiesList+feature.ViewObject.PropertiesList if 'layer' in p.lower()])
except Exception as error:
 report.update(error=str(error),trace=traceback.format_exc())
finally:
 if document is not None:App.closeDocument(document.Name)
 (output/'result.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
 print('FREECAD_MULTICOPY_PRESENTATION passed='+str(report['passed']))
sys.exit(0 if report['passed'] else 1)
