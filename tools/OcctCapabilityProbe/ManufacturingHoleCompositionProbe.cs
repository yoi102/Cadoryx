using Cadoryx.Kernel.Occt;
using OcctSharp;

internal static class ManufacturingHoleCompositionProbe
{
    internal static IReadOnlyList<object> Verify(string output)
    {
        var results=new List<object>();
        using var box=ShapeFactory.CreateBox(12,10,15);using var snapshot=RepairSnapshot.Create(box);
        var origin=new GpXyz(5,6,15);var axis=new GpXyz(0,0,-1);
        using var through=LocalFeatures.Hole(snapshot,new(LocalHoleMode.ThroughAll,origin,axis,1));
        using var blind=LocalFeatures.Hole(snapshot,new(LocalHoleMode.Blind,origin,axis,1,Last:6));
        using var counter=LocalFeatures.Hole(snapshot,new(LocalHoleMode.Blind,origin,axis,2,Last:2));
        using var head=RepairSnapshot.Create(counter.RequireShape());
        using var counterThrough=LocalFeatures.Hole(head,new(LocalHoleMode.ThroughAll,origin,axis,1));
        using var counterBlind=LocalFeatures.Hole(head,new(LocalHoleMode.Blind,origin,axis,1,Last:6));
        using var cone=ShapeFactory.CreateCone(2,1,1);
        using var rotation=GpTrsf.Create(5,6,15,1,0,0,Math.PI);
        using var tool=cone.Transformed(rotation);using var sink=box.Cut(tool);using var sinkMap=RepairSnapshot.Create(sink);
        using var sinkThrough=LocalFeatures.Hole(sinkMap,new(LocalHoleMode.ThroughAll,origin,axis,1));
        using var sinkBlind=LocalFeatures.Hole(sinkMap,new(LocalHoleMode.Blind,origin,axis,1,Last:6));
        void Verify(string name,Shape shape,bool through,int recess,double depth)
        {
            var values=InspectAll(shape,out var errors);
            if(values.Count!=1)throw new InvalidOperationException($"{name}: expected exactly one cylindrical shaft proof, got {values.Count}. {string.Join(" | ",errors)}");
            var value=values[0];
            if(value.Through!=through||value.Head!=recess||Math.Abs(value.Depth-depth)>1e-6||Math.Abs(value.Diameter-2)>1e-6||
                recess!=0&&(Math.Abs(value.HeadDiameter-4)>1e-6||Math.Abs(value.HeadDepth-(recess==1?2:1))>1e-6)||
                recess==2&&Math.Abs(value.HeadAngle-Math.PI/2)>1e-6)
                throw new InvalidOperationException($"{name}: dimensions or full hole composition differ.");
            results.Add(new{name,accepted=true,evidence=value});
        }
        Verify("plain-through",through.RequireShape(),true,0,15);Verify("plain-blind",blind.RequireShape(),false,0,6);
        Verify("counterbore-through-total-depth",counterThrough.RequireShape(),true,1,15);
        Verify("counterbore-blind-total-depth",counterBlind.RequireShape(),false,1,6);
        Verify("countersink-through-total-depth",sinkThrough.RequireShape(),true,2,15);
        Verify("countersink-blind-total-depth",sinkBlind.RequireShape(),false,2,6);
        using var moved=counterBlind.RequireShape().Transformed(ShapeTransform.CreateTranslationAndRotationZ(31,42,53,37));
        Verify("placed-counterbore",moved,false,1,6);
        string step=Path.ChangeExtension(output,"composition.step");ShapeExchange.WriteStep(sinkBlind.RequireShape(),step);
        using var imported=ShapeExchange.ReadStep(step);Verify("step-countersink",imported,false,2,6);
        void Reject(string name,Shape shape)
        {if(InspectAll(shape,out _).Count!=0)throw new InvalidOperationException(name+": unsupported/removed hole was accepted.");results.Add(new{name,accepted=false});}
        using var cylinder=ShapeFactory.CreateCylinder(1,15);Reject("outside-cylinder",cylinder);
        using var tube=ShapeFactory.CreateCylinder(5,15);using var tubeMap=RepairSnapshot.Create(tube);
        using var tubeThrough=LocalFeatures.Hole(tubeMap,new(LocalHoleMode.ThroughAll,new(0,0,15),axis,1));
        using var tubeBlind=LocalFeatures.Hole(tubeMap,new(LocalHoleMode.Blind,new(0,0,15),axis,1,Last:6));
        Verify("cylindrical-part-through-annulus",tubeThrough.RequireShape(),true,0,15);
        Verify("cylindrical-part-blind-annulus",tubeBlind.RequireShape(),false,0,6);
        using var oneHole=RepairSnapshot.Create(tubeThrough.RequireShape());
        using var secondHole=LocalFeatures.Hole(oneHole,new(LocalHoleMode.ThroughAll,new(3,0,15),axis,.5));
        var twoHoles=InspectAll(secondHole.RequireShape(),out var twoErrors);
        if(twoHoles.Count!=2||!twoHoles.All(p=>p.Through&&p.Head==0&&Math.Abs(p.Depth-15)<1e-6)||
           twoHoles.Select(p=>p.Diameter).Order().Zip(new double[]{1,2}).Any(p=>Math.Abs(p.First-p.Second)>1e-6))
            throw new InvalidOperationException("Multiple cylindrical-part apertures were mistaken for a recess: "+string.Join(" | ",twoErrors));
        results.Add(new{name="cylindrical-part-multiple-apertures",accepted=true,holes=twoHoles});
        using var closed=ShapeFactory.CreateCylinder(1,5);using var cavityTool=closed.Transformed(ShapeTransform.CreateTranslation(5,6,5));
        using var cavity=box.Cut(cavityTool);Reject("sealed-cavity",cavity);
        using var side=ShapeFactory.CreateBox(7,10,15);using var sideTool=side.Transformed(ShapeTransform.CreateTranslation(5,0,0));
        using var partial=through.RequireShape().Cut(sideTool);Reject("cut-away-half-wall",partial);
        using var fill=cylinder.Transformed(ShapeTransform.CreateTranslation(5,6,0));using var filled=through.RequireShape().Fuse(fill);Reject("filled-hole",filled);
        return results;
    }
    private static List<NativeHoleInspector.Evidence> InspectAll(Shape shape,out List<string> errors)
    {
        using var map=RepairSnapshot.Create(shape);var values=new List<NativeHoleInspector.Evidence>();errors=[];
        foreach(var item in map.Topology.Where(t=>t.Kind==ShapeKind.Face))
        {
            using var face=map.CopySubshape(item.Selection);
            if(face.GetFaceSurfaceSnapshot().SurfaceType!=SurfaceGeometryType.Cylinder)continue;
            try{values.Add(NativeHoleInspector.Inspect(shape,item.Selection.Index));}
            catch(InvalidOperationException error){errors.Add($"Face {item.Selection.Index}: {error.Message}");}
        }
        return values;
    }
}
