using Cadoryx.Kernel.Occt;
using OcctSharp;

internal static class ManufacturingSizeFeatureProbe
{
    internal static IReadOnlyList<object> Verify(string output)
    {
        var results=new List<object>();using var box=ShapeFactory.CreateBox(12,10,15);using var map=RepairSnapshot.Create(box);
        var origin=new GpXyz(5,6,15);var axis=new GpXyz(0,0,-1);
        using var shaft=ShapeFactory.CreateCylinder(1,15);
        using var through=LocalFeatures.Hole(map,new(LocalHoleMode.ThroughAll,origin,axis,1));
        using var blind=LocalFeatures.Hole(map,new(LocalHoleMode.Blind,origin,axis,1,Last:6));
        using var recess=LocalFeatures.Hole(map,new(LocalHoleMode.Blind,origin,axis,2,Last:2));using var head=RepairSnapshot.Create(recess.RequireShape());
        using var counterbore=LocalFeatures.Hole(head,new(LocalHoleMode.Blind,origin,axis,1,Last:6));
        using var cone=ShapeFactory.CreateCone(2,1,1);using var transform=GpTrsf.Create(5,6,15,1,0,0,Math.PI);
        using var tool=cone.Transformed(transform);using var sink=box.Cut(tool);using var sinkMap=RepairSnapshot.Create(sink);
        using var countersink=LocalFeatures.Hole(sinkMap,new(LocalHoleMode.Blind,origin,axis,1,Last:6));
        List<NativeHoleInspector.CylinderEvidence> Inspect(Shape shape)
        {
            using var snapshot=RepairSnapshot.Create(shape);var values=new List<NativeHoleInspector.CylinderEvidence>();
            foreach(var face in snapshot.Topology.Where(t=>t.Kind==ShapeKind.Face))
            {
                using var sub=snapshot.CopySubshape(face.Selection);
                if(sub.GetFaceSurfaceSnapshot().SurfaceType!=SurfaceGeometryType.Cylinder)continue;
                try{values.Add(NativeHoleInspector.InspectCylinder(shape,face.Selection.Index));}catch(InvalidOperationException){}
            }
            using var after=RepairSnapshot.Create(shape);if(snapshot.Fingerprint!=after.Fingerprint)throw new InvalidOperationException("Size probe mutated the original solid.");
            return values;
        }
        void Verify(string name,Shape shape,bool internalWall,params (double Diameter,double Length)[] expected)
        {
            var values=Inspect(shape).OrderBy(v=>v.Diameter).ToArray();
            if(values.Length!=expected.Length)throw new InvalidOperationException($"{name}: expected {expected.Length} complete size walls, got {values.Length}.");
            for(int i=0;i<values.Length;i++)
            {
                var v=values[i];
                if(v.Internal!=internalWall||Math.Abs(v.Diameter-expected[i].Diameter)>1e-6||Math.Abs(v.Length-expected[i].Length)>1e-6||
                    v.Faces.Length!=3||v.RingEdges.Length<2||v.RingEdges.Distinct().Count()!=v.RingEdges.Length||Math.Abs(v.Direction.Length-1)>1e-8)
                    throw new InvalidOperationException(name+": size dimensions, type or original ring identities differ.");
            }
            results.Add(new{name,accepted=values.Length>0,evidence=values});
        }
        Verify("outer-shaft",shaft,false,(2,15));Verify("through-bore",through.RequireShape(),true,(2,15));
        Verify("blind-bore",blind.RequireShape(),true,(2,6));Verify("counterbore-two-size-walls",counterbore.RequireShape(),true,(2,4),(4,2));
        Verify("countersink-main-size-wall",countersink.RequireShape(),true,(2,5));
        using var moved=shaft.Transformed(ShapeTransform.CreateTranslationAndRotationZ(31,42,53,37));Verify("placed-shaft",moved,false,(2,15));
        var placed=Inspect(moved).Single();if(Math.Abs(placed.Origin.X-31)>1e-6||Math.Abs(placed.Origin.Y-42)>1e-6)throw new InvalidOperationException("Placed size axis origin differs.");
        string shaftStep=Path.ChangeExtension(output,"shaft.step");ShapeExchange.WriteStep(shaft,shaftStep);
        using var importedShaft=ShapeExchange.ReadStep(shaftStep);Verify("step-outer-shaft",importedShaft,false,(2,15));
        string holeStep=Path.ChangeExtension(output,"size-hole.step");ShapeExchange.WriteStep(countersink.RequireShape(),holeStep);
        using var importedHole=ShapeExchange.ReadStep(holeStep);Verify("step-countersink-size",importedHole,true,(2,5));
        using var inner=ShapeFactory.CreateCylinder(1,5);using var cavityTool=inner.Transformed(ShapeTransform.CreateTranslation(5,6,5));
        using var cavity=box.Cut(cavityTool);Verify("sealed-cavity-size-is-not-hole-proof",cavity,true,(2,5));
        using var side=ShapeFactory.CreateBox(7,10,15);using var sideTool=side.Transformed(ShapeTransform.CreateTranslation(5,0,0));
        using var partial=through.RequireShape().Cut(sideTool);Verify("partial-cut-wall-rejected",partial,true);
        using var fill=shaft.Transformed(ShapeTransform.CreateTranslation(5,6,0));using var filled=through.RequireShape().Fuse(fill);Verify("filled-bore-has-no-size-wall",filled,true);
        using var shaftMap=RepairSnapshot.Create(shaft);int index=shaftMap.Topology.Single(t=>
        {if(t.Kind!=ShapeKind.Face)return false;using var sub=shaftMap.CopySubshape(t.Selection);return sub.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder;}).Selection.Index;
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        try{NativeHoleInspector.InspectCylinder(shaft,index,cancel.Token);throw new InvalidOperationException("Canceled size inspection was accepted.");}
        catch(OperationCanceledException){results.Add(new{name="canceled-size-inspection",accepted=false});}
        try{NativeHoleInspector.InspectCylinder(shaft,-1);throw new ArgumentException("Foreign size selection was accepted.");}
        catch(InvalidOperationException){results.Add(new{name="invalid-original-size-index",accepted=false});}
        return results;
    }
}
