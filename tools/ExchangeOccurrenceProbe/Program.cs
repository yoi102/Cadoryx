using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using OcctSharp;

if(args.Length<2)throw new ArgumentException("make new-directory | inspect new-output.json input.xbf|input.step [...]");
static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
static void Require(bool condition,string message){if(!condition)throw new InvalidDataException(message);}
static double[] Matrix(TopLocLocation location)
{
    using var transform=location.ToTransform();
    return Enumerable.Range(1,3).SelectMany(row=>Enumerable.Range(1,4).Select(column=>transform.Value(row,column))).ToArray();
}
static object Observe(XdeDocument document,string path)
{
    var groups=new Dictionary<string,int>();var graph=new List<object>();var leaves=new List<object>();
    int Group(string entry){if(!groups.TryGetValue(entry,out int group)){group=groups.Count;groups.Add(entry,group);}return group;}
    foreach(var root in document.GetFreeShapes())
    {
        using var rootLocation=root.Location;
        graph.Add(new{entry=root.Entry,path=new[]{root.Entry},name=root.Name,definitionName=root.Name,depth=0,assembly=root.IsAssembly,definitionGroup=Group(root.Entry),worldMatrix3x4=Matrix(rootLocation)});
        var occurrences=root.GetOccurrences(recursive:true);
        try
        {
            Require(graph.Count+occurrences.Count<=4096,"Native occurrence observation budget exceeded");
            foreach(var item in occurrences)
            {
                using var world=item.GetWorldLocation();
                graph.Add(new{entry=item.OccurrenceLabel.Entry,path=item.Path.ToArray(),name=item.OccurrenceLabel.Name,
                    definitionName=item.ReferredLabel.Name,depth=item.Depth,assembly=item.IsAssembly,definitionGroup=Group(item.ReferredLabel.Entry),worldMatrix3x4=Matrix(world)});
                if(item.IsAssembly)continue;
                using var shape=item.GetLocatedShape();var faces=shape.GetFaces();
                try
                {
                    Require(faces.Length<=250000,"Native face observation budget exceeded");
                    var styles=item.ReferredLabel.GetPresentationStyles();
                    try
                    {
                        var styleMeasurements=new List<object>();
                        foreach(var style in styles)
                        {
                            var styledFaces=style.Shape.GetFaces();
                            try
                            {
                                var measured=new List<object>();
                                foreach(var styledFace in styledFaces)
                                {using var located=world.Locate(styledFace);measured.Add(new{bounds=located.GetBoundingBox(),properties=located.InspectAdaptiveProperties(InspectionPropertyKind.Area,1e-10).Properties});}
                                styleMeasurements.Add(new{style.SurfaceColor,style.CurveColor,style.MaterialColor,style.EffectiveColor,measuredFaces=measured});
                            }
                            finally{foreach(var styledFace in styledFaces)styledFace.Dispose();}
                        }
                        leaves.Add(new{path=item.Path.ToArray(),definitionGroup=Group(item.ReferredLabel.Entry),valid=shape.IsValid,
                            solids=shape.CountSubShapes(ShapeKind.Solid),faces=faces.Length,bounds=shape.GetBoundingBox(),
                            area=faces.Sum(f=>f.InspectAdaptiveProperties(InspectionPropertyKind.Area,1e-10).Properties.Mass),
                            volume=shape.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10).Properties.Mass,
                            definitionPresentation=styleMeasurements,
                            measuredFaces=faces.Select((face,index)=>new{observedSlot=index+1,bounds=face.GetBoundingBox(),properties=face.InspectAdaptiveProperties(InspectionPropertyKind.Area,1e-10).Properties}).ToArray()});
                    }
                    finally{foreach(var style in styles)style.Dispose();}
                }
                finally{foreach(var face in faces)face.Dispose();}
            }
        }
        finally{foreach(var item in occurrences)item.Dispose();}
    }
    var dimensions=new List<object>();var tolerances=new List<object>();var datums=new List<object>();
    foreach(var item in document.GetDimensions())
    {
        using var copy=item.GetSnapshot();var d=copy.Definition;
        dimensions.Add(new{copy.Entry,type=d.Type.ToString(),values=d.Values,d.SemanticName,d.Description,d.FirstPoint,d.SecondPoint,
            copy.FirstShapeEntries,copy.SecondShapeEntries,hasAuxiliaryPath=d.Path is not null,hasPresentation=d.Presentation is not null});
    }
    foreach(var item in document.GetGeometricTolerances())
    {
        using var copy=item.GetSnapshot();var d=copy.Definition;
        tolerances.Add(new{copy.Entry,type=d.Type.ToString(),d.Value,d.SemanticName,d.Modifiers,copy.ShapeEntries,copy.DatumEntries,hasPresentation=d.Presentation is not null});
    }
    foreach(var item in document.GetDatums())
    {
        using var copy=item.GetSnapshot();var d=copy.Definition;
        datums.Add(new{copy.Entry,d.Name,d.Identification,d.SemanticName,d.IsDatumTarget,copy.ShapeEntries,copy.ToleranceEntries});
    }
    return new{path,sha256=Hash(path),graph,leaves,pmi=new{dimensions,tolerances,datums,
        unitStatus="NotMeasured: public XdeDocument unit owner projection still requires explicit consumer qualification; values are not assigned an invented unit."},
        basis="Native same-document owned snapshots; per-leg labels/slots are not an exchange identity mapping"};
}
if(args[0]=="make")
{
    string directory=Path.GetFullPath(args[1]);Require(!Directory.Exists(directory),"Preserve earlier fixture directory");Directory.CreateDirectory(directory);
    using var document=XdeDocument.Create();using var box=ShapeFactory.CreateBox(2,3,4);using var cylinder=ShapeFactory.CreateCylinder(1,6);
    using(var edit=document.BeginTransaction("Nested shared external interchange source"))
    {
        var part=document.AddShape(box,"Bracket");var pin=document.AddShape(cylinder,"Pin");
        part.Color=new(.1,.3,.7);pin.Color=new(.7,.3,.1);part.AddLayer("Manufacturing");pin.AddLayer("Hardware");
        using var repair=new RepairDocumentSession(document,part);
        var faceLabels=new List<XdeLabel>();var endFaces=new Dictionary<int,XdeLabel>();int face=0;
        foreach(var slot in repair.Source.Topology.Where(t=>t.Kind==ShapeKind.Face))
        {
            var label=repair.GetOrCreateSubshapeLabel(slot.Selection);
            label.SetPresentationColor(XdeColorChannel.Surface,new((face+1)/7.0,.2,1-(face+1)/7.0));label.AddLayer("Bracket face "+face++);faceLabels.Add(label);
            using var actual=repair.Source.CopySubshape(slot.Selection);var bounds=actual.GetBoundingBox();
            if(Math.Abs(bounds.Maximum.X-bounds.Minimum.X)<1e-5)
            {
                int end=Math.Abs(bounds.Minimum.X)<1e-5?0:Math.Abs(bounds.Minimum.X-2)<1e-5?2:-1;
                Require(end>=0&&!endFaces.ContainsKey(end),"Source end-plane measurements are ambiguous");endFaces.Add(end,label);
            }
        }
        Require(faceLabels.Count==6&&endFaces.Count==2,"Source bracket face/end-plane inventory changed");
        var module=document.AddAssembly("Shared module");var root=document.AddAssembly("Root assembly");
        void Add(XdeLabel parent,XdeLabel definition,string name,double x,double y,double angle)
        {using var transform=GpTrsf.Create(x,y,0,0,0,1,angle);using var location=TopLocLocation.FromTransform(transform);document.AddComponent(parent,definition,location).Name=name;}
        Add(module,part,"Bracket slot",0,0,0);Add(module,pin,"Pin slot",12,0,.2);
        Add(root,module,"Left module",-40,0,.35);Add(root,module,"Right module",50,30,-.6);Add(root,part,"Spare bracket",0,70,1.1);
        var datum=document.CreateDatum(new XdeDatumDefinition{Name="A",Identification="A",SemanticName="Bracket support",Position=1},[endFaces[0]]);
        document.CreateDimension(new XdeDimensionDefinition(XCAFDimTolObjectsDimensionType.XCAFDimTolObjects_DimensionType_Location_LinearDistance,[2])
            {SemanticName="Bracket nominal source length",Description="Between measured x=0/x=2 source planes; unit declaration observed separately",FirstPoint=new(0,1.5,2),SecondPoint=new(2,1.5,2)},[endFaces[0]],[endFaces[2]]);
        document.CreateGeometricTolerance(new XdeGeomToleranceDefinition{Type=XCAFDimTolObjectsGeomToleranceType.XCAFDimTolObjects_GeomToleranceType_Flatness,Value=.05,SemanticName="Bracket flatness"},[endFaces[0]],[datum]);
        Require(edit.Commit(),"Source transaction failed");
    }
    string source=Path.Combine(directory,"source.xbf"),step=Path.Combine(directory,"source.step");
    document.Save(source);document.WriteStep(step,new XdeStepWriteOptions(WriteGdt:true,Schema:XdeStepSchema.Ap242));
    using var native=XdeDocument.Open(source);using var exported=XdeDocument.ReadStep(step,new XdeStepReadOptions(ReadGdt:true));
    var roots=native.GetFreeShapes();Require(roots.Length==1&&roots[0].IsAssembly,"Source must have one actual assembly root");
    var instances=roots[0].GetOccurrences(recursive:true);
    try
    {
        Require(instances.Count==7&&instances.Count(i=>i.IsAssembly)==2,"Source nested occurrence inventory changed");
        var reuse=instances.Where(i=>!i.IsAssembly).GroupBy(i=>i.ReferredLabel.Entry).Select(g=>g.Count()).Order().ToArray();
        Require(reuse.SequenceEqual(new[]{2,3}),"Source actual shared part partition changed");
        foreach(var instance in instances.Where(i=>!i.IsAssembly))
        {using var placed=instance.GetLocatedShape();Require(placed.IsValid&&placed.CountSubShapes(ShapeKind.Solid)==1,"Source instance is not a valid single solid");}
    }
    finally{foreach(var instance in instances)instance.Dispose();}
    Require(native.GetDimensions().Length==1&&native.GetGeometricTolerances().Length==1&&native.GetDatums().Length==1,"Source persisted PMI inventory changed");
    var records=new[]{Observe(native,source),Observe(exported,step)};
    Write(Path.Combine(directory,"native-observation.json"),records);
}
else if(args[0]=="inspect")
{
    Require(args.Length>=3,"Input files required");var records=new List<object>();
    foreach(var input in args.Skip(2))
    {string path=Path.GetFullPath(input);using var document=Path.GetExtension(path).Equals(".xbf",StringComparison.OrdinalIgnoreCase)?XdeDocument.Open(path):XdeDocument.ReadStep(path,new XdeStepReadOptions(ReadGdt:true));records.Add(Observe(document,path));}
    Write(Path.GetFullPath(args[1]),records);
}
else throw new ArgumentException("Unknown probe action");
static void Write(string output,object records)
{
    var native=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m=>Path.GetDirectoryName(m.FileName)?.EndsWith(Path.DirectorySeparatorChar+"occt",StringComparison.OrdinalIgnoreCase)==true)
        .Select(m=>new{path=m.FileName,sha256=Hash(m.FileName)}).OrderBy(m=>m.path).ToArray();
    using var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write);JsonSerializer.Serialize(file,new{runtime=OcctRuntime.Info,nativeModules=native,records},new JsonSerializerOptions{WriteIndented=true});
}
