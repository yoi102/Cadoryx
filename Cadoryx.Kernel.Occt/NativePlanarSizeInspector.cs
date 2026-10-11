using OcctSharp;

namespace Cadoryx.Kernel.Occt;

// This first contract proves the external thickness of an entire straight
// polygonal prism. An arbitrary pair of plane classifications is insufficient.
internal static class NativePlanarSizeInspector
{
    internal readonly record struct Point(double X,double Y,double Z)
    {
        internal static Point From(GpPoint p)=>new(p.X,p.Y,p.Z);
        internal double Dot(Point b)=>X*b.X+Y*b.Y+Z*b.Z;
        internal Point Cross(Point b)=>new(Y*b.Z-Z*b.Y,Z*b.X-X*b.Z,X*b.Y-Y*b.X);
        internal double Length=>Math.Sqrt(Dot(this));
        public static Point operator +(Point a,Point b)=>new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
        public static Point operator -(Point a,Point b)=>new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
        public static Point operator *(Point a,double b)=>new(a.X*b,a.Y*b,a.Z*b);
    }
    internal sealed record Evidence(int FirstFace,int SecondFace,Point Origin,Point Direction,double Width,
        int[] Faces,int[] FirstEdges,int[] SecondEdges,int[] ConnectingEdges)
    {
        public bool Internal {get;init;}
        public int[] PrismCapFaces {get;init;}=[];
        public int[] PrismBoundaryEdges {get;init;}=[];
    }
    private static void Require(bool condition,string message)
    {if(!condition)throw new InvalidOperationException(message);}
    private static (Point Point,Point Normal) Plane(Shape face)
    {
        var surface=face.GetFaceSurfaceSnapshot();
        Require(surface.SurfaceType is SurfaceGeometryType.Plane or SurfaceGeometryType.SurfaceOfExtrusion&&double.IsFinite(surface.FirstUParameter)&&
            double.IsFinite(surface.LastUParameter)&&double.IsFinite(surface.FirstVParameter)&&double.IsFinite(surface.LastVParameter)&&
            surface.LastUParameter>surface.FirstUParameter&&surface.LastVParameter>surface.FirstVParameter,
            $"A finite nondegenerate original plane is required: {surface.SurfaceType}, U={surface.FirstUParameter:R}..{surface.LastUParameter:R}, V={surface.FirstVParameter:R}..{surface.LastVParameter:R}.");
        if(surface.SurfaceType==SurfaceGeometryType.SurfaceOfExtrusion)
        {
            // The native type proves that all constant-V curves share one translated
            // basis. Its exact line type proves the whole surface is planar; sampled
            // normals or four coplanar boundary points alone would not prove this.
            using var basis=SurfaceModeling.CreateIsoEdge(face,SurfaceIsoDirection.ConstantV,
                (surface.FirstVParameter+surface.LastVParameter)/2,new(surface.FirstUParameter,surface.LastUParameter));
            using var generator=SurfaceModeling.CreateIsoEdge(face,SurfaceIsoDirection.ConstantU,
                (surface.FirstUParameter+surface.LastUParameter)/2,new(surface.FirstVParameter,surface.LastVParameter));
            Require(basis.GetEdgeCurveSnapshot().CurveType==CurveGeometryType.Line&&generator.GetEdgeCurveSnapshot().CurveType==CurveGeometryType.Line,
                "An extrusion surface requires one exact linear basis and linear generator.");
        }
        var value=face.EvaluateFace((surface.FirstUParameter+surface.LastUParameter)/2,(surface.FirstVParameter+surface.LastVParameter)/2);
        var normal=Point.From(value.Normal);Require(Math.Abs(normal.Length-1)<1e-8,"Plane has no unit outward normal.");
        return(Point.From(value.Point),normal);
    }
    internal static Evidence InspectPrism(Shape shape,int firstFace,int secondFace,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        Require(shape.IsValid&&shape.GetTopologySummary().UniqueCounts.SolidCount==1&&
            shape.InspectProperties(InspectionPropertyKind.Volume).Mass>0,"One valid positive solid is required.");
        using var original=RepairSnapshot.Create(shape);
        Require(original.Topology.Count<=4096&&firstFace>=0&&secondFace>=0&&firstFace!=secondFace&&
            firstFace<original.Topology.Count&&secondFace<original.Topology.Count&&
            original.Topology[firstFace].Kind==ShapeKind.Face&&original.Topology[secondFace].Kind==ShapeKind.Face,"Select two distinct original faces.");
        using var adjacency=shape.GetTopologyAdjacency(ShapeKind.Edge,ShapeKind.Face);
        Require(adjacency.Ancestors.Count is >=5 and <=66&&adjacency.Items.Count<=256,"Polygonal prism evidence budget exceeded.");
        var faceIds=adjacency.Ancestors.Select(f=>RepairSnapshot.FindTopologyIndex(shape,f)).ToArray();
        var edgeIds=adjacency.Items.Select(e=>RepairSnapshot.FindTopologyIndex(shape,e)).ToArray();
        Require(faceIds.All(i=>i>=0)&&faceIds.Distinct().Count()==faceIds.Length&&edgeIds.All(i=>i>=0)&&
            edgeIds.Distinct().Count()==edgeIds.Length,"Original face/edge identity is not unique.");
        int a=Array.IndexOf(faceIds,firstFace),b=Array.IndexOf(faceIds,secondFace);Require(a>=0&&b>=0,"Selected caps are outside the original adjacency.");
        var first=Plane(adjacency.Ancestors[a]);var second=Plane(adjacency.Ancestors[b]);
        Require(first.Normal.Dot(second.Normal)<-1+1e-10,"Size faces must have opposite outward normals.");
        var direction=first.Normal*-1;double width=(second.Point-first.Point).Dot(direction);
        Require(double.IsFinite(width)&&width is >1e-6 and <=1e6,"Size faces do not enclose a positive finite material thickness.");
        var firstEdges=adjacency.GetItemIndices(a).ToHashSet();var secondEdges=adjacency.GetItemIndices(b).ToHashSet();
        Require(firstEdges.Count>=3&&firstEdges.Count==secondEdges.Count&&!firstEdges.Overlaps(secondEdges),"Prism caps have inconsistent original boundary budgets.");
        var connectors=new HashSet<int>();var used=new HashSet<int>();
        for(int f=0;f<faceIds.Length;f++)
        {
            token.ThrowIfCancellationRequested();if(f==a||f==b)continue;
            var side=Plane(adjacency.Ancestors[f]);Require(Math.Abs(side.Normal.Dot(direction))<1e-10,"A tapered or stepped wall cannot prove constant prism thickness.");
            var edges=adjacency.GetItemIndices(f).Distinct().ToArray();
            Require(edges.Length==4&&edges.Count(firstEdges.Contains)==1&&edges.Count(secondEdges.Contains)==1,"Each prism side must join both selected finite caps exactly once.");
            foreach(int e in edges)
            {
                used.Add(e);var owners=adjacency.GetAncestorIndices(e).ToArray().Distinct().ToArray();
                Require(owners.Length==2&&owners.Contains(f),"Prism boundary is open or branched.");
                var edge=adjacency.Items[e];var range=edge.GetEdgeCurveSnapshot();
                Require(range.CurveType==CurveGeometryType.Line&&double.IsFinite(range.FirstParameter)&&double.IsFinite(range.LastParameter)&&
                    range.LastParameter>range.FirstParameter,"This contract accepts only finite straight polygonal boundaries.");
                var p=Point.From(edge.EvaluateEdge(range.FirstParameter).Point);var q=Point.From(edge.EvaluateEdge(range.LastParameter).Point);
                Require((q-p).Length>1e-6,"Degenerate prism edge.");
                if(firstEdges.Contains(e)||secondEdges.Contains(e))
                {
                    int cap=firstEdges.Contains(e)?a:b;var origin=cap==a?first.Point:second.Point;
                    Require(owners.Contains(cap)&&Math.Abs((p-origin).Dot(direction))<1e-6&&Math.Abs((q-origin).Dot(direction))<1e-6,
                        "Cap boundary does not belong to its selected original plane.");
                }
                else
                {
                    Require(!owners.Contains(a)&&!owners.Contains(b),"A connecting edge was misclassified.");
                    double along=(q-p).Dot(direction);
                    Require(Math.Abs(Math.Abs(along)-width)<1e-6&&((q-p)-direction*along).Length<1e-6,
                        "The original side edge does not join equal translated cap positions.");
                    double lo=Math.Min((p-first.Point).Dot(direction),(q-first.Point).Dot(direction));
                    double hi=Math.Max((p-first.Point).Dot(direction),(q-first.Point).Dot(direction));
                    Require(Math.Abs(lo)<1e-6&&Math.Abs(hi-width)<1e-6,"Connecting edge endpoints are outside the selected caps.");connectors.Add(e);
                }
            }
        }
        Require(used.Count==edgeIds.Length&&connectors.Count==firstEdges.Count,"The entire finite prism boundary is not covered.");
        double area=adjacency.Ancestors[a].InspectProperties(InspectionPropertyKind.Area).Mass;
        double oppositeArea=adjacency.Ancestors[b].InspectProperties(InspectionPropertyKind.Area).Mass;
        double volume=shape.InspectProperties(InspectionPropertyKind.Volume).Mass;
        Require(double.IsFinite(area)&&area>1e-12&&Math.Abs(area-oppositeArea)<Math.Max(1e-9,area*1e-9)&&
            Math.Abs(volume-area*width)<Math.Max(1e-9,volume*1e-9),"Full cap areas or solid volume contradict the original prism boundary.");
        using var unchanged=RepairSnapshot.Create(shape);Require(unchanged.Fingerprint==original.Fingerprint,"Size inspection changed the original BRep.");
        token.ThrowIfCancellationRequested();
        return new(firstFace,secondFace,first.Point+direction*(width/2),direction,width,faceIds.Order().ToArray(),
            firstEdges.Select(e=>edgeIds[e]).Order().ToArray(),secondEdges.Select(e=>edgeIds[e]).Order().ToArray(),connectors.Select(e=>edgeIds[e]).Order().ToArray())
            {PrismCapFaces=[firstFace,secondFace],PrismBoundaryEdges=firstEdges.Concat(secondEdges).Select(e=>edgeIds[e]).Order().ToArray()};
    }

    // A uniform rectangular void in a fully certified polygonal prism. The
    // entire open rectangle is proved free of material by all original cap
    // boundaries plus an analytic even-odd classification, not sampled rays.
    internal static Evidence InspectInternalWidth(Shape shape,int firstFace,int secondFace,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        using var original=RepairSnapshot.Create(shape);
        Require(shape.IsValid&&shape.GetTopologySummary().UniqueCounts.SolidCount==1&&original.Topology.Count<=4096,
            "One valid bounded solid is required for an internal width.");
        using var adjacency=shape.GetTopologyAdjacency(ShapeKind.Edge,ShapeKind.Face);
        Require(adjacency.Ancestors.Count is >=5 and <=66&&adjacency.Items.Count<=256,"Internal width evidence budget exceeded.");
        var faceIds=adjacency.Ancestors.Select(f=>RepairSnapshot.FindTopologyIndex(shape,f)).ToArray();
        var edgeIds=adjacency.Items.Select(e=>RepairSnapshot.FindTopologyIndex(shape,e)).ToArray();
        int a=Array.IndexOf(faceIds,firstFace),b=Array.IndexOf(faceIds,secondFace);
        Require(a>=0&&b>=0&&a!=b,"Select two distinct original slot walls.");
        var first=Plane(adjacency.Ancestors[a]);var second=Plane(adjacency.Ancestors[b]);
        Require(first.Normal.Dot(second.Normal)<-1+1e-10,"Slot walls must have opposite outward normals.");
        var direction=first.Normal;double width=(second.Point-first.Point).Dot(direction);
        Require(double.IsFinite(width)&&width is >1e-6 and <=1e6,"The selected walls do not face a positive internal gap.");
        var firstEdges=adjacency.GetItemIndices(a).Distinct().ToArray();var secondEdges=adjacency.GetItemIndices(b).Distinct().ToArray();
        Require(firstEdges.Length==4&&secondEdges.Length==4,"This internal-width contract requires two entire rectangular walls.");
        HashSet<int> Neighbors(int[] edges,int self)=>edges.SelectMany(e=>adjacency.GetAncestorIndices(e).ToArray()).Where(f=>f!=self).ToHashSet();
        var candidates=Neighbors(firstEdges,a).Intersect(Neighbors(secondEdges,b)).Order().ToArray();
        Require(candidates.Length is >=2 and <=4,"The walls have no complete common prism boundaries.");
        var prisms=new List<Evidence>();
        for(int i=0;i<candidates.Length;i++)for(int j=i+1;j<candidates.Length;j++)
        {
            token.ThrowIfCancellationRequested();
            try{prisms.Add(InspectPrism(shape,faceIds[candidates[i]],faceIds[candidates[j]],token));}
            catch(InvalidOperationException){ /* This candidate pair has no complete prism proof. */ }
        }
        Require(prisms.Count==1,"The selected walls require one unique complete prism domain.");
        var prism=prisms[0];int cap=Array.IndexOf(faceIds,prism.FirstFace);
        var capEdges=adjacency.GetItemIndices(cap).Distinct().ToArray();
        var ae=firstEdges.Intersect(capEdges).ToArray();var be=secondEdges.Intersect(capEdges).ToArray();
        Require(ae.Length==1&&be.Length==1&&Math.Abs(prism.Direction.Dot(direction))<1e-10,
            "The selected walls do not each have one common complete cap edge.");
        (Point P,Point Q) Ends(int e)
        {
            var edge=adjacency.Items[e];var range=edge.GetEdgeCurveSnapshot();
            Require(range.CurveType==CurveGeometryType.Line,"A slot domain requires exact straight boundary edges.");
            return(Point.From(edge.EvaluateEdge(range.FirstParameter).Point),Point.From(edge.EvaluateEdge(range.LastParameter).Point));
        }
        var p=Ends(ae[0]);var q=Ends(be[0]);double length=(p.Q-p.P).Length;
        Require(length>1e-6&&length<=1e6,"Slot wall has no finite common length.");
        var tangent=(p.Q-p.P)*(1/length);
        Require(Math.Abs(tangent.Dot(direction))<1e-10&&Math.Abs(tangent.Dot(prism.Direction))<1e-10,
            "The slot cap edge is not perpendicular to its two domain directions.");
        Point offset=direction*width;
        Require(((q.P-p.P-offset).Length<1e-6&&(q.Q-p.Q-offset).Length<1e-6)||
            ((q.Q-p.P-offset).Length<1e-6&&(q.P-p.Q-offset).Length<1e-6),
            "The two complete slot wall domains do not coincide by translation.");
        // Clip each remaining original segment against the *open* gap. Any
        // material boundary inside it disproves a uniform rectangular void.
        const double eps=1e-7;
        bool CrossesInterior(double x0,double y0,double x1,double y1)
        {
            double lo=0,hi=1;
            bool Clip(double p0,double p1,double min,double max)
            {
                double delta=p1-p0;
                if(Math.Abs(delta)<1e-12)return p0>min&&p0<max;
                double enter=(min-p0)/delta,leave=(max-p0)/delta;
                if(enter>leave)(enter,leave)=(leave,enter);
                lo=Math.Max(lo,enter);hi=Math.Min(hi,leave);return lo<hi;
            }
            return Clip(x0,x1,eps,width-eps)&&Clip(y0,y1,eps,length-eps)&&lo<hi;
        }
        bool material=false;double midX=width/2,midY=length/2;
        foreach(int e in capEdges)
        {
            token.ThrowIfCancellationRequested();var ends=Ends(e);
            double x0=(ends.P-p.P).Dot(direction),x1=(ends.Q-p.P).Dot(direction);
            double y0=(ends.P-p.P).Dot(tangent),y1=(ends.Q-p.P).Dot(tangent);
            if(e!=ae[0]&&e!=be[0])Require(!CrossesInterior(x0,y0,x1,y1),"A material boundary enters the claimed entire slot gap.");
            if((y0>midY)!=(y1>midY)&&midX<x0+(x1-x0)*(midY-y0)/(y1-y0))material=!material;
        }
        Require(!material,"The entire claimed gap is on the material side of the cap domain.");
        using var unchanged=RepairSnapshot.Create(shape);Require(unchanged.Fingerprint==original.Fingerprint,"Internal width inspection changed the original BRep.");
        token.ThrowIfCancellationRequested();
        return new(firstFace,secondFace,p.P+direction*midX+tangent*midY+prism.Direction*(prism.Width/2),direction,width,prism.Faces,
            firstEdges.Select(e=>edgeIds[e]).Order().ToArray(),secondEdges.Select(e=>edgeIds[e]).Order().ToArray(),prism.ConnectingEdges)
            {Internal=true,PrismCapFaces=prism.PrismCapFaces,PrismBoundaryEdges=prism.PrismBoundaryEdges};
    }
}

