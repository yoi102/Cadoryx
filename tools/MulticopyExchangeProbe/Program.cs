using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using OcctSharp;

if(args.Length<2)throw new ArgumentException("Usage: MulticopyExchangeProbe output.json input.xbf|input.step [...]");
var records=new List<object>();
foreach(string input in args.Skip(1))
{
    string path=Path.GetFullPath(input);using var document=Path.GetExtension(path)==".xbf"?XdeDocument.Open(path):XdeDocument.ReadStep(path);
    var roots=document.GetFreeShapes();var hierarchy=new List<object>();var definitions=new HashSet<string>();var bodies=new List<object>();
    void Visit(XdeLabel label,string parent,bool occurrence=false)
    {
        var definition=occurrence?label.ReferredShape:label;
        using var placement=label.Location;
        hierarchy.Add(new{entry=label.Entry,parent,name=label.Name,reference=occurrence,definition=definition.Entry,assembly=definition.IsAssembly,identityPlacement=placement.IsIdentity});
        if(!definitions.Add(definition.Entry))return;
        if(definition.IsAssembly){foreach(var child in definition.GetComponents())Visit(child,definition.Entry,true);return;}
        using var shape=definition.Shape;using var members=new RepairDocumentSession(document,definition);var slots=new List<object>();
        using(var edit=document.BeginTransaction("Read independent native member presentation"))
        {
            var labels=members.Source.Topology.ToDictionary(t=>t.Selection.Index,t=>t.Selection.Index==0?definition:members.GetOrCreateSubshapeLabel(t.Selection));
            var containers=new Dictionary<int,(Shape Shape,XdeColor Color)>();
            try
            {
                foreach(var item in members.Source.Topology.Where(t=>t.Kind is ShapeKind.Compound or ShapeKind.CompSolid or ShapeKind.Solid or ShapeKind.Shell))
                {
                    var colors=labels[item.Selection.Index].GetPresentationColors();
                    if((colors.Surface??colors.General) is {} color)containers.Add(item.Selection.Index,(labels[item.Selection.Index].Shape,color));
                }
            foreach(var item in members.Source.Topology)
            {
                var metadata=labels[item.Selection.Index];var colors=metadata.GetPresentationColors();
                object? surface=null;XdeColor? effectiveSurface=null;
                if(item.Kind==ShapeKind.Face)
                {
                    using var face=members.Source.CopySubshape(item.Selection);
                    var properties=face.InspectAdaptiveProperties(InspectionPropertyKind.Area,1e-10).Properties;
                    surface=new{area=properties.Mass,center=properties.CenterOfMass,bounds=face.GetBoundingBox()};
                    effectiveSurface=colors.Surface??colors.General;
                    if(effectiveSurface is null)
                    {
                        using var actualMember=metadata.Shape;
                        var owners=containers.Where(c=>RepairSnapshot.FindTopologyIndex(c.Value.Shape,actualMember)>=0).ToArray();
                        // All actual native owners are considered, including shared ancestry.
                        // Only an unambiguous effective color is reported; no topology rebinding.
                        var closest=owners.Where(c=>!owners.Any(other=>other.Key!=c.Key&&RepairSnapshot.FindTopologyIndex(c.Value.Shape,other.Value.Shape)>=0)).ToArray();
                        var inherited=closest.Select(c=>c.Value.Color).Distinct().ToArray();
                        if(inherited.Length>1)throw new InvalidOperationException("Conflicting native inherited surface color owners.");
                        effectiveSurface=inherited.Length==0?null:inherited[0];
                    }
                }
                slots.Add(new{index=item.Selection.Index,kind=item.Kind.ToString(),parent=item.ParentIndex,colors,
                    layers=metadata.Layers,material=metadata.Material,visualMaterial=metadata.VisualMaterial,surface,effectiveSurface});
            }
            }
            finally{foreach(var container in containers.Values)container.Shape.Dispose();}
        }
        bodies.Add(new{entry=definition.Entry,name=definition.Name,valid=shape.IsValid,solids=shape.CountSubShapes(ShapeKind.Solid),faces=shape.FaceCount,
            volume=shape.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10).Properties.Mass,slots});
    }
    foreach(var root in roots)Visit(root,"");
    records.Add(new{path,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),hierarchy,bodies});
}
var native=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m=>Path.GetDirectoryName(m.FileName)?.EndsWith(Path.DirectorySeparatorChar+"occt",StringComparison.OrdinalIgnoreCase)==true)
    .Select(m=>new{path=m.FileName,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(m.FileName)))}).OrderBy(m=>m.path).ToArray();
File.WriteAllText(Path.GetFullPath(args[0]),JsonSerializer.Serialize(new{runtime=OcctRuntime.Info,nativeModules=native,records},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine("Independent native presentation inspection complete: "+records.Count+" files.");
