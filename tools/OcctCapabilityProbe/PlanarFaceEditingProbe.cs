using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using OcctSharp;

internal static class PlanarFaceEditingProbe
{
    private static readonly FeatureModelingOptions Options=new(){FuzzyTolerance=1e-7,NonDestructive=true,RepairInputs=false,UnifyResult=true};
    internal static int Run(string[] args)
    {
        string folder=Path.GetFullPath(args[0]);Directory.CreateDirectory(folder);var results=new List<object>();bool passed=true;
        void Probe(string name,Func<object> run)
        {var clock=Stopwatch.StartNew();try{results.Add(new{name,passed=true,detail=run(),elapsedMs=clock.ElapsedMilliseconds});}catch(Exception error){passed=false;results.Add(new{name,passed=false,error=error.ToString(),elapsedMs=clock.ElapsedMilliseconds});}}
        foreach(bool rotated in new[]{false,true})foreach(double distance in new[]{2d,-2d})Probe($"box-{(rotated?"rotated":"axis")}-{distance}",()=>
        {
            using var box=ShapeFactory.CreateBox(10,12,15);using var original=RepairSnapshot.Create(box);
            using var top=original.CopySubshape(Select(original,s=>Math.Abs(s.GetBoundingBox().Minimum.Z-15)<1e-6));
            double a=rotated?.37:0;var transform=new ShapeTransform(0,0,0,1,0,0,a);
            using var source=box.Transformed(transform);using var profile=top.Transformed(transform);using var before=RepairSnapshot.Create(source);
            var vector=new GpXyz(0,-Math.Sin(a)*distance,Math.Cos(a)*distance);
            using var result=distance>0?FeatureModeling.AddBoss(source,profile,vector,Options):FeatureModeling.CutPocket(source,profile,vector,Options);
            Check(result);Near(Volume(result.RequireShape()),1800+120*distance);using(var immediate=RepairSnapshot.Create(source))Require(before.Fingerprint==immediate.Fingerprint,"Source mutated before STEP export: "+before.Fingerprint+" -> "+immediate.Fingerprint);Roundtrip(result.RequireShape(),folder,$"box-{rotated}-{distance}");
            using var after=RepairSnapshot.Create(source);Require(before.Fingerprint==after.Fingerprint,"Source mutated after STEP export: "+before.Fingerprint+" -> "+after.Fingerprint);
            return new{volume=Volume(result.RequireShape()),history=result.History.Count,modified=result.Modified.Count,generated=result.Generated.Count,sourceUnchanged=true};
        });
        foreach(double distance in new[]{2d,-2d})Probe($"perforated-finite-face-{distance}",()=>
        {
            using var box=ShapeFactory.CreateBox(10,12,15);using var cylinder=ShapeFactory.CreateCylinder(2,17);using var tool=cylinder.Transformed(ShapeTransform.CreateTranslation(5,6,-1));using var source=box.Cut(tool);using var before=RepairSnapshot.Create(source);
            using var top=before.CopySubshape(Select(before,s=>Math.Abs(s.GetBoundingBox().Minimum.Z-15)<1e-6));
            var wires=top.GetSubShapes(ShapeKind.Wire);try{Require(wires.Length==2,"Fixture must retain the outer and inner native wire.");}finally{foreach(var wire in wires)wire.Dispose();}
            using var result=distance>0?FeatureModeling.AddBoss(source,top,new(0,0,distance),Options):FeatureModeling.CutPocket(source,top,new(0,0,distance),Options);
            Check(result);Near(Volume(result.RequireShape()),(120-4*Math.PI)*(15+distance));Roundtrip(result.RequireShape(),folder,$"perforated-{distance}");
            using var after=RepairSnapshot.Create(source);Require(before.Fingerprint==after.Fingerprint,"Perforated source mutated.");
            return new{volume=Volume(result.RequireShape()),innerDomainPreserved=true,history=result.History.Count};
        });
        foreach(bool copied in new[]{false,true})Probe($"defeature-through-bore-{(copied?"independent-copy-rejected":"exact-native-member")}",()=>
        {
            using var box=ShapeFactory.CreateBox(10,12,15);using var cylinder=ShapeFactory.CreateCylinder(2,17);using var tool=cylinder.Transformed(ShapeTransform.CreateTranslation(5,6,-1));using var source=box.Cut(tool);using var before=RepairSnapshot.Create(source);
            Shape[] faces=source.GetSubShapes(ShapeKind.Face);
            try
            {
                using var copy=before.CopySubshape(Select(before,s=>s.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder));
                int selected=Select(before,s=>s.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder).Index;
                var member=faces.Single(s=>RepairSnapshot.FindTopologyIndex(source,s)==selected);
                using var result=FeatureModeling.Defeature(source,[copied?copy:member],Options);
                if(copied){Require(!result.Diagnostics.Succeeded,"An independent repaired copy was mistaken for source membership.");using var unchanged=RepairSnapshot.Create(source);Require(before.Fingerprint==unchanged.Fingerprint,"Rejected defeature mutated source.");return new{rejected=true,diagnostics=result.Diagnostics};}
                Check(result);Near(Volume(result.RequireShape()),1800);Roundtrip(result.RequireShape(),folder,"defeature-"+copied);
                using var after=RepairSnapshot.Create(source);Require(before.Fingerprint==after.Fingerprint,"Defeature source mutated.");
                return new{volume=Volume(result.RequireShape()),history=result.History.Count,deleted=result.DeletedSourceIndices.Count,copySelection=copied};
            }
            finally{foreach(var face in faces)face.Dispose();}
        });
        Probe("negative-full-removal-is-not-an-accepted-solid",()=>
        {
            using var source=ShapeFactory.CreateBox(10,12,15);using var before=RepairSnapshot.Create(source);using var top=before.CopySubshape(Select(before,s=>Math.Abs(s.GetBoundingBox().Minimum.Z-15)<1e-6));
            using var result=FeatureModeling.CutPocket(source,top,new(0,0,-20),Options);
            bool rejected=false;try{Check(result);}catch(InvalidOperationException){rejected=true;}Require(rejected,"Empty solid was accepted.");
            using var after=RepairSnapshot.Create(source);Require(before.Fingerprint==after.Fingerprint,"Rejected source mutated.");return new{rejected=true,diagnostics=result.Diagnostics};
        });
        Probe("owned-derived-face-tool-has-complete-full-map-source-history",()=>
        {
            using var box=ShapeFactory.CreateBox(10,12,15);using var source=RepairSnapshot.Create(box);
            using var top=source.CopySubshape(Select(source,s=>Math.Abs(s.GetBoundingBox().Minimum.Z-15)<1e-6));
            using var vector=GpVec.Create(0,0,-2);using var prism=top.Extrude(vector);using var tool=RepairSnapshot.Create(prism);
            using var result=BooleanHistoryModeling.Build(TopologyBooleanOperation.Cut,[source,tool]);var shape=result.RequireShape();
            Require(shape.GetTopologySummary().UniqueCounts.SolidCount==1&&shape.GetValidationReport(true,true).IsValid&&shape.GetSelfInterferenceReport().IsClear,"Owned face Boolean invalid.");Near(Volume(shape),1560);
            var rows=result.History.Where(h=>h.Source?.ArgumentIndex==0).ToArray();
            foreach(var item in source.Topology.Where(t=>t.Kind is ShapeKind.Face or ShapeKind.Edge or ShapeKind.Vertex))
                Require(rows.Any(h=>h.Source is {} s&&s.PlanId==result.PlanId&&s.TopologyIndex==item.Selection.Index&&s.Kind==item.Kind),"Full-map native source slot missing.");
            Roundtrip(shape,folder,"owned-full-map");using var again=RepairSnapshot.Create(box);Require(source.Fingerprint==again.Fingerprint,"Owned source mutated.");
            return new{volume=Volume(shape),sourceHistoryRows=rows.Length,fullMapVerified=true,derivedToolArguments=1};
        });
        var loaded=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m=>Path.GetDirectoryName(m.FileName)?.EndsWith(Path.DirectorySeparatorChar+"occt",StringComparison.OrdinalIgnoreCase)==true)
            .Select(m=>new{path=m.FileName,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(m.FileName)))}).OrderBy(m=>m.path).ToArray();
        File.WriteAllText(Path.Combine(folder,"result.json"),JsonSerializer.Serialize(new{passed,total=results.Count,results,nativeModules=loaded,
            scope="Locked SDK prerequisite for finite planar face extrusion and defeaturing. No general face move, application, UI or physical-input claim."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PLANAR_FACE_EDITING_PROBE_COMPLETE passed="+passed);return passed?0:1;
    }
    private static RepairSelection Select(RepairSnapshot source,Func<Shape,bool> predicate)
    {foreach(var t in source.Topology.Where(t=>t.Kind==ShapeKind.Face)){using var face=source.CopySubshape(t.Selection);if(predicate(face))return t.Selection;}throw new InvalidOperationException("Explicit fixture face missing.");}
    private static void Check(FeatureOperationResult result)
    {Require(result.Diagnostics.Succeeded,result.Diagnostics.StageMessage);var shape=result.RequireShape();Require(shape.GetTopologySummary().UniqueCounts.SolidCount==1&&shape.GetValidationReport(true,true).IsValid&&shape.GetSelfInterferenceReport().IsClear,"Result is not one valid, interference-free solid.");}
    private static double Volume(Shape shape)=>Math.Abs(shape.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10).Properties.Mass);
    private static void Near(double value,double expected)=>Require(Math.Abs(value-expected)<1e-4,$"Expected {expected:R}, actual {value:R}.");
    private static void Roundtrip(Shape shape,string folder,string name)
    {string path=Path.Combine(folder,name+".step"),brep=Path.Combine(folder,name+".brep");ShapeExchange.WriteBrep(shape,brep);using var independent=ShapeExchange.ReadBrep(brep);ShapeExchange.WriteStep(independent,path);using var reopened=ShapeExchange.ReadStep(path);Require(reopened.GetValidationReport(true,true).IsValid,"STEP reimport invalid.");Near(Volume(reopened),Volume(shape));}
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
