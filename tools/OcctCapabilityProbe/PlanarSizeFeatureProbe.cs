using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Cadoryx.Kernel.Occt;
using OcctSharp;

internal static class PlanarSizeFeatureProbe
{
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static int[] Caps(Shape shape,NativePlanarSizeInspector.Point axis)
    {
        using var map=RepairSnapshot.Create(shape);
        return map.Topology.Where(t=>
        {
            if(t.Kind!=ShapeKind.Face)return false;using var face=map.CopySubshape(t.Selection);var s=face.GetFaceSurfaceSnapshot();
            if(s.SurfaceType!=SurfaceGeometryType.Plane)return false;
            var at=face.EvaluateFace((s.FirstUParameter+s.LastUParameter)/2,(s.FirstVParameter+s.LastVParameter)/2);
            return Math.Abs(NativePlanarSizeInspector.Point.From(at.Normal).Dot(axis))>1-1e-8;
        }).Select(t=>t.Selection.Index).ToArray();
    }
    private static NativePlanarSizeInspector.Point Rotate(NativePlanarSizeInspector.Point point,bool rotated)
    {
        if(!rotated)return point;double c=Math.Cos(.6),s=Math.Sin(.6);var axis=new NativePlanarSizeInspector.Point(1,2,3)*(1/Math.Sqrt(14));
        return point*c+axis.Cross(point)*s+axis*(axis.Dot(point)*(1-c));
    }
    private static int[] SlotWalls(Shape shape,NativePlanarSizeInspector.Point direction)
    {
        using var map=RepairSnapshot.Create(shape);
        var faces=new List<(int Id,double Position)>();
        foreach(var item in map.Topology.Where(t=>t.Kind==ShapeKind.Face))
        {
            using var face=map.CopySubshape(item.Selection);var surface=face.GetFaceSurfaceSnapshot();
            if(surface.SurfaceType is not (SurfaceGeometryType.Plane or SurfaceGeometryType.SurfaceOfExtrusion))continue;
            var at=face.EvaluateFace((surface.FirstUParameter+surface.LastUParameter)/2,(surface.FirstVParameter+surface.LastVParameter)/2);
            if(Math.Abs(NativePlanarSizeInspector.Point.From(at.Normal).Dot(direction))>1-1e-8)
                faces.Add((item.Selection.Index,NativePlanarSizeInspector.Point.From(at.Point).Dot(direction)));
        }
        Require(faces.Count==4,"The slot fixture must have four explicit walls along its known size direction.");
        return faces.OrderBy(f=>f.Position).Skip(1).Take(2).Select(f=>f.Id).ToArray();
    }
    private static Shape Slot(bool closed,bool mismatch=false)
    {
        if(closed)
        {
            using var blank=ShapeFactory.CreateBox(20,12,4);using var cutter=ShapeFactory.CreateBox(6,8,6);
            using var move=GpTrsf.Create(7,2,-1);using var tool=cutter.Transformed(move);return blank.Cut(tool);
        }
        using var wire=ShapeFactory.CreatePolygonWire([new(0,0,0),new(20,0,0),new(20,12,0),new(13,12,0),
            new(13,mismatch?6:5,0),new(7,5,0),new(7,12,0),new(0,12,0)],true);
        using var face=ShapeFactory.CreatePlanarFace(wire);using var vector=GpVec.Create(0,0,4);return face.Extrude(vector);
    }
    internal static int Run(string[] args)
    {
        string path=Path.GetFullPath(args[0]);string folder=Path.Combine(Path.GetDirectoryName(path)!,"planar-size-fixtures");Directory.CreateDirectory(folder);
        var results=new List<object>();
        try
        {
            foreach(string profile in new[]{"rectangle","triangle","concave"})foreach(bool rotated in new[]{false,true})foreach(bool imported in new[]{false,true})
            {
                GpPoint[] points=profile switch
                {
                    "rectangle"=>[new(0,0,0),new(20,0,0),new(20,12,0),new(0,12,0)],
                    "triangle"=>[new(0,0,0),new(20,0,0),new(0,12,0)],
                    _=>[new(0,0,0),new(20,0,0),new(20,5,0),new(8,5,0),new(8,12,0),new(0,12,0)]
                };
                using var wire=ShapeFactory.CreatePolygonWire(points,true);using var face=ShapeFactory.CreatePlanarFace(wire);using var vector=GpVec.Create(0,0,4);
                using var prism=face.Extrude(vector);using var transform=GpTrsf.Create(30,-20,10,1,2,3,rotated?.6:0);
                using var moved=prism.Transformed(transform);Shape shape;string name=$"{profile}-{(rotated?"rotated":"aligned")}-{(imported?"step":"authored")}";
                if(imported){string step=Path.Combine(folder,name+".step");ShapeExchange.WriteStep(moved,step);shape=ShapeExchange.ReadStep(step);}else shape=moved.Clone();
                using(shape)
                {
                    double c=rotated?Math.Cos(.6):1,s=rotated?Math.Sin(.6):0;
                    var axis=new NativePlanarSizeInspector.Point(2*s/Math.Sqrt(14)+3*(1-c)/14,-s/Math.Sqrt(14)+6*(1-c)/14,c+9*(1-c)/14);
                    var caps=Caps(shape,axis);Require(caps.Length==2,"Fixture does not identify exactly two caps.");
                    using var before=RepairSnapshot.Create(shape);var evidence=NativePlanarSizeInspector.InspectPrism(shape,caps[0],caps[1]);
                    Require(Math.Abs(evidence.Width-4)<1e-6,"Original prism thickness differs from its fixture.");
                    var reverse=NativePlanarSizeInspector.InspectPrism(shape,caps[1],caps[0]);
                    Require(Math.Abs(reverse.Width-evidence.Width)<1e-6&&reverse.Direction.Dot(evidence.Direction)<-1+1e-10,
                        "Reversing the two explicit selections changed the proven thickness.");
                    using var after=RepairSnapshot.Create(shape);Require(before.Fingerprint==after.Fingerprint,"Size inspection changed the original shape.");
                    results.Add(new{name,passed=true,width=evidence.Width,faces=evidence.Faces.Length,capEdges=evidence.FirstEdges.Length,
                        connectingEdges=evidence.ConnectingEdges.Length,evidence,originalFingerprint=before.Fingerprint,sourceUnchanged=true});
                }
            }
            foreach(bool closed in new[]{false,true})foreach(bool rotated in new[]{false,true})foreach(bool imported in new[]{false,true})
            {
                using var slot=Slot(closed);using var transform=GpTrsf.Create(30,-20,10,1,2,3,rotated?.6:0);using var moved=slot.Transformed(transform);
                string name=$"{(closed?"closed-slot":"open-slot")}-{(rotated?"rotated":"aligned")}-{(imported?"step":"authored")}";
                Shape shape;if(imported){string step=Path.Combine(folder,name+".step");ShapeExchange.WriteStep(moved,step);shape=ShapeExchange.ReadStep(step);}else shape=moved.Clone();
                using(shape)
                {
                    var walls=SlotWalls(shape,Rotate(new(1,0,0),rotated));using var before=RepairSnapshot.Create(shape);
                    var evidence=NativePlanarSizeInspector.InspectInternalWidth(shape,walls[0],walls[1]);
                    var reverse=NativePlanarSizeInspector.InspectInternalWidth(shape,walls[1],walls[0]);
                    Require(evidence.Internal&&Math.Abs(evidence.Width-6)<1e-6&&Math.Abs(reverse.Width-6)<1e-6&&
                        evidence.Direction.Dot(reverse.Direction)<-1+1e-10,"Reversed complete slot proof differs from the fixture.");
                    using var after=RepairSnapshot.Create(shape);Require(before.Fingerprint==after.Fingerprint,"Slot inspection changed the source.");
                    results.Add(new{name,passed=true,width=evidence.Width,evidence,sourceUnchanged=true});
                }
            }
            using(var slot=Slot(false))
            {
                var walls=SlotWalls(slot,new(1,0,0));var caps=Caps(slot,new(0,0,1));
                foreach(var sample in new[]{(Name:"internal-same-face",First:walls[0],Second:walls[0]),
                    (Name:"internal-nonparallel",First:walls[0],Second:caps[0]),(Name:"internal-exterior-material",First:caps[0],Second:caps[1])})
                {
                    bool rejected=false;try{NativePlanarSizeInspector.InspectInternalWidth(slot,sample.First,sample.Second);}catch(InvalidOperationException){rejected=true;}
                    Require(rejected,"Invalid internal wall pair was accepted.");results.Add(new{name=sample.Name,passed=true,rejected=true});
                }
            }
            using(var uneven=Slot(false,true))
            {
                var walls=SlotWalls(uneven,new(1,0,0));bool rejected=false;
                try{NativePlanarSizeInspector.InspectInternalWidth(uneven,walls[0],walls[1]);}catch(InvalidOperationException){rejected=true;}
                Require(rejected,"Mismatched complete finite wall domains were accepted.");results.Add(new{name="internal-mismatched-domains",passed=true,rejected=true});
            }
            using(var box=ShapeFactory.CreateBox(20,12,4))
            using(var map=RepairSnapshot.Create(box))
            {
                var caps=Caps(box,new(0,0,1));var side=map.Topology.First(t=>t.Kind==ShapeKind.Face&&!caps.Contains(t.Selection.Index)).Selection.Index;
                foreach(var sample in new[]{(Name:"same-face",First:caps[0],Second:caps[0]),(Name:"nonparallel",First:caps[0],Second:side),
                    (Name:"out-of-range",First:caps[0],Second:99999)})
                {
                    bool rejected=false;try{NativePlanarSizeInspector.InspectPrism(box,sample.First,sample.Second);}catch(InvalidOperationException){rejected=true;}
                    Require(rejected,"Invalid explicit face pair was accepted.");results.Add(new{name=sample.Name,passed=true,rejected=true});
                }
                using var other=ShapeFactory.CreateBox(2,2,2);using var compound=ShapeFactory.CreateCompound([box,other]);
                bool compoundRejected=false;try{NativePlanarSizeInspector.InspectPrism(compound,caps[0],caps[1]);}catch(InvalidOperationException){compoundRejected=true;}
                Require(compoundRejected,"Two solids were accepted as one size feature.");results.Add(new{name="multiple-solids",passed=true,rejected=true});
            }
            using(var cylinder=ShapeFactory.CreateCylinder(5,4))
            {
                var caps=Caps(cylinder,new(0,0,1));Require(caps.Length==2,"Cylinder fixture caps missing.");bool rejected=false;
                try{NativePlanarSizeInspector.InspectPrism(cylinder,caps[0],caps[1]);}catch(InvalidOperationException){rejected=true;}
                Require(rejected,"Curved-side geometry escaped the declared polygonal prism contract.");results.Add(new{name="curved-side-outside-first-contract",passed=true,rejected=true});
            }
            string moduleRoot=Path.GetFullPath(args[1]);var names=Directory.GetFiles(moduleRoot,"*.dll").Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var modules=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m=>names.Contains(m.ModuleName)).ToArray();
            Require(modules.Any(m=>m.ModuleName.Equals("OcctSharp.Native.dll",StringComparison.OrdinalIgnoreCase)),"Native bridge is not loaded.");
            foreach(var module in modules)Require(Path.GetDirectoryName(module.FileName)==moduleRoot,"Unexpected native module path.");
            var nativeModules=modules.Select(m=>new{name=m.ModuleName,path=m.FileName,sha256=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(m.FileName)))});
            File.WriteAllText(path,JsonSerializer.Serialize(new{passed=true,scope="F3h complete polygonal prism external thickness and rectangular internal width prerequisites; application integration remains pending",results,nativeModules},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception error){File.WriteAllText(path,JsonSerializer.Serialize(new{passed=false,error=error.ToString(),results},new JsonSerializerOptions{WriteIndented=true}));return 1;}
    }
}
