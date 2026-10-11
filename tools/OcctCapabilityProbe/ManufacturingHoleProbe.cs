using System.Text.Json;
using OcctSharp;

internal static class ManufacturingHoleProbe
{
    private readonly record struct V(double X,double Y,double Z)
    {
        internal static V Point(GpPoint p)=>new(p.X,p.Y,p.Z);
        internal double Dot(V b)=>X*b.X+Y*b.Y+Z*b.Z;
        internal V Cross(V b)=>new(Y*b.Z-Z*b.Y,Z*b.X-X*b.Z,X*b.Y-Y*b.X);
        public static V operator +(V a,V b)=>new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
        public static V operator -(V a,V b)=>new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
        public static V operator *(V a,double b)=>new(a.X*b,a.Y*b,a.Z*b);
        internal double Length=>Math.Sqrt(Dot(this));
    }
    private static (V Center,double Radius) Circle(Shape edge)
    {
        var range=edge.GetEdgeCurveSnapshot();double span=range.LastParameter-range.FirstParameter;
        var p=V.Point(edge.EvaluateEdge(range.FirstParameter+span*.2).Point);
        var q=V.Point(edge.EvaluateEdge(range.FirstParameter+span*.5).Point);
        var r=V.Point(edge.EvaluateEdge(range.FirstParameter+span*.8).Point);
        var a=q-p;var b=r-p;var cross=a.Cross(b);double square=cross.Dot(cross);
        if(square<1e-14)throw new InvalidOperationException("Degenerate exact circle.");
        var center=p+(b.Cross(cross)*a.Dot(a)+cross.Cross(a)*b.Dot(b))*(1/(2*square));
        return(center,(p-center).Length);
    }
    private sealed record End(int Edge,int Neighbor,string Role,double Radius,V Center);
    private sealed record Bore(int Face,bool Inward,bool FullDomain,double Radius,double ShaftLength,IReadOnlyList<End> Ends);
    private sealed record Inspection(string Name,string Fingerprint,IReadOnlyList<Bore> Cylinders);
    private static Inspection Inspect(string name,Shape shape)
    {
        if(!shape.IsValid||shape.GetTopologySummary().UniqueCounts.SolidCount!=1||shape.InspectProperties(InspectionPropertyKind.Volume).Mass<=0)
            throw new InvalidOperationException("Hole recognition requires one positively oriented valid solid.");
        using var before=RepairSnapshot.Create(shape);using var adjacency=shape.GetTopologyAdjacency(ShapeKind.Edge,ShapeKind.Face);
        var faces=adjacency.Ancestors.Select(f=>RepairSnapshot.FindTopologyIndex(shape,f)).ToArray();
        var edges=adjacency.Items.Select(e=>RepairSnapshot.FindTopologyIndex(shape,e)).ToArray();
        if(faces.Any(i=>i<0)||edges.Any(i=>i<0)||faces.Distinct().Count()!=faces.Length||edges.Distinct().Count()!=edges.Length)
            throw new InvalidOperationException("Owning adjacency values did not preserve original topology identity.");
        var bores=new List<Bore>();
        for(int f=0;f<faces.Length;f++)
        {
            var face=adjacency.Ancestors[f];var surface=face.GetFaceSurfaceSnapshot();if(surface.SurfaceType!=SurfaceGeometryType.Cylinder)continue;
            var indices=adjacency.GetItemIndices(f);var circles=indices.Where(i=>adjacency.Items[i].GetEdgeCurveSnapshot().CurveType==CurveGeometryType.Circle).ToArray();
            double u=surface.FirstUParameter+(surface.LastUParameter-surface.FirstUParameter)/2,v=surface.FirstVParameter+(surface.LastVParameter-surface.FirstVParameter)/2;
            // For a true cylinder, three native points on the same iso circle determine its exact center.
            using var iso=SurfaceModeling.CreateIsoEdge(face,SurfaceIsoDirection.ConstantV,v,new(surface.FirstUParameter,surface.LastUParameter));
            var (center,radius)=Circle(iso);var at=face.EvaluateFace(u,v);
            bool inward=(V.Point(at.Point)-center).Dot(V.Point(at.Normal))<-.99*radius;
            bool full=Math.Abs(surface.LastUParameter-surface.FirstUParameter-Math.Tau)<1e-7&&circles.Length==2;
            var ends=new List<End>();
            foreach(int e in circles)
            {
                var edge=adjacency.Items[e];var range=edge.GetEdgeCurveSnapshot();
                if(Math.Abs(range.LastParameter-range.FirstParameter-Math.Tau)>1e-7){full=false;continue;}
                var circle=Circle(edge);var neighbors=adjacency.GetAncestorIndices(e).ToArray().Where(n=>n!=f).ToArray();
                if(neighbors.Length!=1){full=false;continue;}
                int other=neighbors[0];var neighbor=adjacency.Ancestors[other];string role;
                if(neighbor.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Plane)
                {
                    var solutions=SurfaceModeling.ProjectPoint(neighbor,new(circle.Center.X,circle.Center.Y,circle.Center.Z),limitToFace:false);
                    if(solutions.Count!=1||solutions[0].Distance>1e-6)throw new InvalidOperationException("Planar boundary center has no unique exact projection.");
                    role=solutions[0].DomainState switch
                    {SurfaceDomainState.Outside=>"aperture",SurfaceDomainState.Inside=>"cap",_=>"unproved"};
                }
                else role=neighbor.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cone?"cone-transition":"unproved";
                ends.Add(new(edges[e],faces[other],role,circle.Radius,circle.Center));
            }
            foreach(int e in indices.Except(circles))
                if(adjacency.Items[e].GetEdgeCurveSnapshot().CurveType!=CurveGeometryType.Line||
                    adjacency.GetAncestorIndices(e).ToArray().Any(n=>n!=f))full=false;
            double length=ends.Count==2?(ends[1].Center-ends[0].Center).Length:0;
            bores.Add(new(faces[f],inward,full,radius,length,ends));
        }
        using var after=RepairSnapshot.Create(shape);if(before.Fingerprint!=after.Fingerprint)throw new InvalidOperationException("Hole inspection modified the original BRep.");
        return new(name,before.Fingerprint,bores);
    }
    private static void Require(bool condition,string error){if(!condition)throw new InvalidOperationException(error);}
    internal static int Run(string[] args)
    {
        if(args.Length!=1||File.Exists(args[0]))throw new ArgumentException("Expected one fresh result path.");
        var result=new List<Inspection>();
        try
        {
            using var box=ShapeFactory.CreateBox(12,10,15);using var snapshot=RepairSnapshot.Create(box);
            var origin=new GpXyz(5,6,15);var direction=new GpXyz(0,0,-1);
            using var through=LocalFeatures.Hole(snapshot,new(LocalHoleMode.ThroughAll,origin,direction,1));
            using var blind=LocalFeatures.Hole(snapshot,new(LocalHoleMode.Blind,origin,direction,1,Last:6));
            using var head=LocalFeatures.Hole(snapshot,new(LocalHoleMode.Blind,origin,direction,2,Last:2));
            using var headSnapshot=RepairSnapshot.Create(head.RequireShape());using var counterbore=LocalFeatures.Hole(headSnapshot,new(LocalHoleMode.ThroughAll,origin,direction,1));
            using var cylinder=ShapeFactory.CreateCylinder(1,15);
            foreach(var (name,shape) in new[]{("through",through.RequireShape()),("blind",blind.RequireShape()),("counterbore",counterbore.RequireShape()),("outer-cylinder",cylinder)})
                result.Add(Inspect(name,shape));
            var t=result.Single(r=>r.Name=="through").Cylinders.Single();
            Require(t.Inward&&t.FullDomain&&t.Ends.Count(e=>e.Role=="aperture")==2&&Math.Abs(t.ShaftLength-15)<1e-6,"Through bore evidence differs.");
            var b=result.Single(r=>r.Name=="blind").Cylinders.Single();
            Require(b.Inward&&b.FullDomain&&b.Ends.Count(e=>e.Role=="aperture")==1&&b.Ends.Count(e=>e.Role=="cap")==1&&Math.Abs(b.ShaftLength-6)<1e-6,"Blind bore evidence differs.");
            Require(!result.Single(r=>r.Name=="outer-cylinder").Cylinders.Single().Inward,"Outer cylindrical material was misidentified as a bore.");
            Require(result.Single(r=>r.Name=="counterbore").Cylinders.Count==2&&result.Single(r=>r.Name=="counterbore").Cylinders.All(c=>c.Inward&&c.FullDomain),"Counterbore passage topology unavailable.");
            string step=Path.ChangeExtension(args[0],".step");ShapeExchange.WriteStep(through.RequireShape(),step);
            using var imported=ShapeExchange.ReadStep(step);var importedEvidence=Inspect("imported-step-through",imported);result.Add(importedEvidence);
            Require(importedEvidence.Cylinders.Single() is {Inward:true,FullDomain:true},"Original STEP bore not proved.");
            using var cutter=ShapeFactory.CreateCylinder(1,5);using var internalTool=cutter.Transformed(ShapeTransform.CreateTranslation(5,6,5));
            using var cavity=box.Cut(internalTool);var sealedEvidence=Inspect("sealed-cavity",cavity);result.Add(sealedEvidence);
            Require(sealedEvidence.Cylinders.Single().Ends.All(e=>e.Role=="cap"),"Sealed internal cavity was misidentified as an open hole.");
            using var trim=ShapeFactory.CreateBox(7,10,15);using var trimTool=trim.Transformed(ShapeTransform.CreateTranslation(5,0,0));
            using var trimmed=through.RequireShape().Cut(trimTool);var trimmedEvidence=Inspect("later-cut-open-side",trimmed);result.Add(trimmedEvidence);
            Require(trimmedEvidence.Cylinders.All(c=>!c.FullDomain),"Partially removed wall was accepted as an intact bore.");
            using var fill=cylinder.Transformed(ShapeTransform.CreateTranslation(5,6,0));using var filled=through.RequireShape().Fuse(fill);
            var filledEvidence=Inspect("later-filled-hole",filled);result.Add(filledEvidence);Require(!filledEvidence.Cylinders.Any(c=>c.Inward),"Historical hole dimensions survived filled topology.");
            var composition=ManufacturingHoleCompositionProbe.Verify(args[0]);
            var sizes=ManufacturingSizeFeatureProbe.Verify(args[0]);
            string nativeDirectory=Path.Combine(AppContext.BaseDirectory,"occt");
            var names=Directory.EnumerateFiles(nativeDirectory,"*.dll").Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var loaded=System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>().Where(m=>names.Contains(m.ModuleName)).ToArray();
            Require(loaded.All(m=>string.Equals(Path.GetDirectoryName(m.FileName),nativeDirectory,StringComparison.OrdinalIgnoreCase)),"Native module loaded outside the NuGet consumer output.");
            Require(loaded.Any(m=>m.ModuleName.Equals("OcctSharp.Native.dll",StringComparison.OrdinalIgnoreCase)),"Native bridge provenance not proved.");
            var nativeModules=loaded.Select(m=>new{name=m.ModuleName,path=m.FileName,sha256=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(m.FileName)))}).ToArray();
            File.WriteAllText(args[0],JsonSerializer.Serialize(new{passed=true,runtime=OcctRuntime.Info,nativeModules,scope="Original inward holes and complete inner/outer cylindrical size walls; distinct contracts, no cross-revision rebinding",results=result,composition,sizes},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception error){File.WriteAllText(args[0],JsonSerializer.Serialize(new{passed=false,error=error.ToString(),results=result},new JsonSerializerOptions{WriteIndented=true}));return 1;}
    }
}
