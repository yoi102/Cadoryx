"""Collect actual FreeCAD object/link/placement/face observations and a STEP return.

This is an external observation, not a topology identity matcher. Native shape
coordinates and global object placements remain separate, especially for links.
"""
from pathlib import Path
import hashlib, json, math, sys, traceback

input_path=Path(sys.argv[1]).resolve()
output=Path(sys.argv[2]).resolve()
assert input_path.is_file() and input_path.suffix.lower() in ('.step','.stp')
output.mkdir(parents=True,exist_ok=False)
sha=lambda p:hashlib.sha256(Path(p).read_bytes()).hexdigest().upper()
report=dict(schemaVersion=1,observationCompleted=False,input=str(input_path),inputSha256=sha(input_path),
    basis='Actual FreeCAD ImportGui object graph, link targets, object global placement and GUI ShapeAppearance; no guessed node/face correspondence',
    geometryCoordinates='Raw object Shape coordinates. Global object placements are recorded separately; no inferred flattened world shape for links.',
    limitations=['Raw object graph includes reader wrappers and definition objects; object totals are not occurrence totals',
        'Compound and leaf geometry can overlap; scalar properties are not summed across objects',
        'No persistent topology or automatic reference rebinding',
        'PMI semantic transfer is not established by object/property names or lack of visible annotations',
        'Offscreen GUI observation does not prove physical display or pointer interaction'])
documents=[]
try:
    import FreeCAD as App
    import FreeCADGui as Gui
    import Part
    Gui.showMainWindow()
    import ImportGui
    report.update(software='FreeCAD',version=App.Version(),occt=Part.OCC_VERSION,executable=sys.executable,
        executableSha256=sha(sys.executable),module=App.__file__,moduleSha256=sha(App.__file__))

    def finite(values):
        result=[float(v) for v in values]
        if not all(math.isfinite(v) for v in result):raise ValueError('Nonfinite external observation')
        return result

    def matrix(placement):
        m=placement.toMatrix()
        return finite(getattr(m,'A'+str(row)+str(column)) for row in range(1,4) for column in range(1,5))

    def bounds(shape):
        b=shape.BoundBox
        return finite([b.XMin,b.YMin,b.ZMin,b.XMax,b.YMax,b.ZMax])

    def rgb(material):
        return finite(material.DiffuseColor)

    def collect(document):
        objects=list(document.Objects)
        if not 1<=len(objects)<=4096:raise ValueError('External object observation budget exceeded or empty document')
        known={o.Name for o in objects}
        if len(known)!=len(objects):raise ValueError('Duplicate external object identity')
        rows=[];total_faces=0
        for obj in objects:
            row=dict(name=obj.Name,label=obj.Label,typeId=obj.TypeId,properties=sorted(obj.PropertiesList),
                inList=[o.Name for o in obj.InList],outList=[o.Name for o in obj.OutList])
            if 'Group' in obj.PropertiesList:
                row['groupChildren']=[child.Name for child in obj.Group]
                if any(name not in known for name in row['groupChildren']):raise ValueError('Foreign group child')
            if obj.isDerivedFrom('App::Link'):
                target=obj.getLinkedObject(False)
                if target is None:raise ValueError('Unresolved external link')
                row['linkTarget']=dict(document=target.Document.Name,name=target.Name,label=target.Label,typeId=target.TypeId)
                row['linkTransform']=bool(obj.LinkTransform) if 'LinkTransform' in obj.PropertiesList else None
            if 'Placement' in obj.PropertiesList:
                row['localPlacement3x4']=matrix(obj.Placement)
                row['globalObjectPlacement3x4']=matrix(obj.getGlobalPlacement())
            view=obj.ViewObject
            row['view']=dict(visible=bool(view.Visibility),properties=sorted(view.PropertiesList))
            if 'Shape' in obj.PropertiesList and not obj.Shape.isNull():
                shape=obj.Shape;faces=list(shape.Faces);total_faces+=len(faces)
                if total_faces>250000:raise ValueError('External face observation budget exceeded')
                row['shape']=dict(valid=bool(shape.isValid()),solids=len(shape.Solids),faces=len(faces),edges=len(shape.Edges),
                    volume=finite([shape.Volume])[0],area=finite([shape.Area])[0],bounds=bounds(shape),placement3x4=matrix(shape.Placement))
                materials=list(view.ShapeAppearance) if 'ShapeAppearance' in view.PropertiesList else []
                row['shapeAppearance']=dict(materialCount=len(materials),faceCount=len(faces),colors=[rgb(m) for m in materials],
                    facePairing='Same-object face order' if len(materials)==len(faces) else 'NotComparable; raw material list retained')
                row['faces']=[dict(observedSlot=i+1,area=finite([face.Area])[0],center=finite(face.CenterOfMass),bounds=bounds(face),
                    srgb=rgb(materials[i]) if len(materials)==len(faces) else None) for i,face in enumerate(faces)]
            rows.append(row)
        return dict(document=document.Name,roots=[o.Name for o in document.RootObjects],objects=rows,
            pmi=dict(status='NotMeasured',note='No semantic PMI reader inventory has been proved for this FreeCAD transfer; raw object and property names are not proof.'))

    first=App.newDocument('CadoryxOccurrenceExternal');documents.append(first.Name)
    ImportGui.insert(str(input_path),first.Name);first.recompute()
    report['imported']=collect(first)
    exported=output/'freecad-return.step'
    roots=list(first.RootObjects)
    if not roots:raise ValueError('No actual external root objects to export')
    ImportGui.export(roots,str(exported))
    if not exported.is_file() or exported.stat().st_size==0:raise ValueError('External writer produced no STEP file')
    report.update(returned=str(exported),returnedBytes=exported.stat().st_size,returnedSha256=sha(exported),exportedRoots=[o.Name for o in roots])
    second=App.newDocument('CadoryxOccurrenceExternalReturn');documents.append(second.Name)
    ImportGui.insert(str(exported),second.Name);second.recompute()
    report['reopenedReturn']=collect(second)
    report['observationCompleted']=True
except Exception as error:
    report.update(error=str(error),trace=traceback.format_exc())
finally:
    if 'App' in globals():
        for name in reversed(documents):App.closeDocument(name)
    (output/'observation.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print('FREECAD_OCCURRENCE_OBSERVATION completed='+str(report['observationCompleted']))
sys.exit(0 if report['observationCompleted'] else 1)
