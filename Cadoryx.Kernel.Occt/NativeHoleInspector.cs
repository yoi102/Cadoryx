using OcctSharp;

namespace Cadoryx.Kernel.Occt;

// No Cadoryx values or handles escape this inspector. The NuGet-only consumer compiles
// this same source to verify the original topology/adjacency contract independently.
internal static class NativeHoleInspector
{
    internal readonly record struct Point(double X,double Y,double Z)
    {
        internal static Point From(GpPoint p)=>new(p.X,p.Y,p.Z);
        internal double Dot(Point b)=>X*b.X+Y*b.Y+Z*b.Z;
        internal Point Cross(Point b)=>new(Y*b.Z-Z*b.Y,Z*b.X-X*b.Z,X*b.Y-Y*b.X);
        public static Point operator +(Point a,Point b)=>new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
        public static Point operator -(Point a,Point b)=>new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
        public static Point operator *(Point a,double b)=>new(a.X*b,a.Y*b,a.Z*b);
        internal double Length=>Math.Sqrt(Dot(this));
    }
    internal sealed record Evidence(int ShaftFace,int[] Faces,Point Entry,Point Direction,
        double Diameter,bool Through,double Depth,int Head,double HeadDiameter,double HeadDepth,double HeadAngle);
    internal sealed record CylinderEvidence(int Face,int[] Faces,int[] RingEdges,Point Origin,Point Direction,
        double Diameter,double Length,bool Internal);
    private sealed record InspectionResult(Evidence? Hole=null,CylinderEvidence? Cylinder=null);
    private sealed record Circle(Point Center,double Radius,Point Normal,Point Start,double Span);
    private sealed record End(int[] Edges,int Neighbor,Circle Circle,string Role);
    private sealed record Wall(int Face,int Index,bool Cone,End[] Ends,bool Internal);
    private static void Require(bool value,string message)
    {if(!value)throw new InvalidOperationException(message);}
    private const double Tol=1e-6;
    private static Circle ExactCircle(Shape edge)
    {
        var range=edge.GetEdgeCurveSnapshot();
        Require(range.CurveType==CurveGeometryType.Circle&&double.IsFinite(range.FirstParameter)&&
            double.IsFinite(range.LastParameter)&&range.LastParameter-range.FirstParameter is >1e-5&&
            range.LastParameter-range.FirstParameter<=Math.Tau+1e-7,"Invalid analytic circular boundary.");
        double span=range.LastParameter-range.FirstParameter;
        var p=Point.From(edge.EvaluateEdge(range.FirstParameter+span*.2).Point);
        var a=Point.From(edge.EvaluateEdge(range.FirstParameter+span*.5).Point)-p;
        var b=Point.From(edge.EvaluateEdge(range.FirstParameter+span*.8).Point)-p;
        var cross=a.Cross(b);double square=cross.Dot(cross);
        Require(double.IsFinite(square)&&square>1e-14,"Degenerate circular boundary.");
        var center=p+(b.Cross(cross)*a.Dot(a)+cross.Cross(a)*b.Dot(b))*(1/(2*square));
        double radius=(p-center).Length;
        Require(double.IsFinite(radius)&&radius>1e-6&&radius<=500000,"Invalid bore radius.");
        return new(center,radius,cross*(1/Math.Sqrt(square)),Point.From(edge.EvaluateEdge(range.FirstParameter).Point),span);
    }
    internal static Evidence Inspect(Shape shape,int selectedIndex,CancellationToken token=default)
        =>InspectCore(shape,selectedIndex,token,cylinderOnly:false).Hole!;
    internal static CylinderEvidence InspectCylinder(Shape shape,int selectedIndex,CancellationToken token=default)
        =>InspectCore(shape,selectedIndex,token,cylinderOnly:true).Cylinder!;
    private static InspectionResult InspectCore(Shape shape,int selectedIndex,CancellationToken token,bool cylinderOnly)
    {
        token.ThrowIfCancellationRequested();
        Require(shape.IsValid&&shape.GetTopologySummary().UniqueCounts.SolidCount==1&&
            shape.InspectProperties(InspectionPropertyKind.Volume).Mass>0,
            "Manufacturing inspection requires exactly one valid positively oriented solid.");
        using var original=RepairSnapshot.Create(shape);
        Require(original.Topology.Count<=100000,"Hole inspection topology budget exceeded.");
        using var map=shape.GetTopologyAdjacency(ShapeKind.Edge,ShapeKind.Face);
        using var vertices=shape.GetTopologyAdjacency(ShapeKind.Vertex,ShapeKind.Edge);
        Require(map.Ancestors.Count<=4096&&vertices.Ancestors.Count<=8192,"Manufacturing hole adjacency budget exceeded.");
        Dictionary<int,int>? vertexEdgeIndices=null;
        var indexCache=new Dictionary<int,int>();
        int Index(int f)
        {
            if(indexCache.TryGetValue(f,out int cached))return cached;
            token.ThrowIfCancellationRequested();int value=RepairSnapshot.FindTopologyIndex(shape,map.Ancestors[f]);
            Require(value>=0,"Adjacency lost original face identity.");indexCache.Add(f,value);return value;
        }
        int selected=-1;
        for(int f=0;f<map.Ancestors.Count;f++)if(Index(f)==selectedIndex)
        {Require(selected<0,"Ambiguous original face identity.");selected=f;}
        Require(selected>=0,"Selected cylindrical wall is not in the original solid.");
        Circle Ring(int[] edges)
        {
            var circles=edges.Select(e=>ExactCircle(map.Items[e])).ToArray();var first=circles[0];
            var x=(first.Start-first.Center)*(1/first.Radius);var y=first.Normal.Cross(x);
            var intervals=new List<(double Start,double End)>();var degree=new Dictionary<int,int>();
            foreach(int e in edges)
            {
                token.ThrowIfCancellationRequested();int originalEdge=RepairSnapshot.FindTopologyIndex(shape,map.Items[e]);
                Require(originalEdge>=0,"Ring edge lost original topology identity.");
                if(vertexEdgeIndices is null)
                {
                    vertexEdgeIndices=[];
                    for(int i=0;i<vertices.Ancestors.Count;i++)
                    {
                        token.ThrowIfCancellationRequested();int identity=RepairSnapshot.FindTopologyIndex(shape,vertices.Ancestors[i]);
                        Require(identity>=0&&vertexEdgeIndices.TryAdd(identity,i),"Ambiguous original edge/vertex identity.");
                    }
                }
                Require(vertexEdgeIndices.TryGetValue(originalEdge,out int owner),"Ring edge has no original vertex adjacency.");
                var ends=vertices.GetItemIndices(owner).ToArray();
                Require(ends.Length==(edges.Length==1?1:2),"Circular ring endpoints do not form an original closed chain.");
                foreach(int vtx in ends)degree[vtx]=degree.GetValueOrDefault(vtx)+1;
            }
            Require(degree.Values.All(d=>d==(edges.Length==1?1:2)),"Circular ring endpoints are open or branched.");
            foreach(var circle in circles)
            {
                Require((circle.Center-first.Center).Length<Tol&&Math.Abs(circle.Radius-first.Radius)<Tol&&
                    Math.Abs(circle.Normal.Dot(first.Normal))>1-1e-8,"Circular ring pieces differ in plane or radius.");
                var radial=circle.Start-first.Center;
                double start=Math.Atan2(radial.Dot(y),radial.Dot(x));
                if(circle.Normal.Dot(first.Normal)<0)start-=circle.Span;
                start=(start%Math.Tau+Math.Tau)%Math.Tau;if(start>Math.Tau-1e-7)start=0;
                double end=start+circle.Span;
                intervals.Add((start,Math.Min(end,Math.Tau)));if(end>Math.Tau+1e-7)intervals.Add((0,end-Math.Tau));
            }
            double covered=0;
            foreach(var interval in intervals.OrderBy(i=>i.Start))
            {Require(Math.Abs(interval.Start-covered)<1e-7,"Circular ring has a gap or overlapping arc.");covered=interval.End;}
            Require(Math.Abs(covered-Math.Tau)<1e-7,"Circular boundary does not cover one full revolution.");
            return first;
        }
        var wallCache=new Dictionary<int,Wall>();
        Wall ReadWall(int f,bool allowCone,bool allowOuter=false)
        {
            token.ThrowIfCancellationRequested();
            if(wallCache.TryGetValue(f,out var cached))
            {Require(allowCone||!cached.Cone,"Select the cylindrical shaft, not the conical recess.");
                if(!cylinderOnly&&!allowOuter)Require(cached.Internal,"The selected surface is outer material, not an inward bore wall.");return cached;}
            var face=map.Ancestors[f];var surface=face.GetFaceSurfaceSnapshot();
            bool cone=surface.SurfaceType==SurfaceGeometryType.Cone;
            Require(surface.SurfaceType==SurfaceGeometryType.Cylinder||allowCone&&cone,"Select a complete cylindrical bore wall.");
            Require(double.IsFinite(surface.FirstUParameter)&&double.IsFinite(surface.LastUParameter)&&
                Math.Abs(surface.LastUParameter-surface.FirstUParameter-Math.Tau)<1e-7,
                "A partially cut wall cannot supply a complete manufacturing hole.");
            var items=map.GetItemIndices(f).ToArray();Require(items.Length is >=2 and <=16,"Unsupported cylindrical/conical boundary budget.");
            var ends=new List<End>();var circular=new Dictionary<int,List<int>>();
            foreach(int e in items)
            {
                var edge=map.Items[e];var type=edge.GetEdgeCurveSnapshot().CurveType;
                var neighbors=map.GetAncestorIndices(e).ToArray().Distinct().ToArray();
                Require(neighbors.Contains(f),"Broken original edge/face adjacency.");
                if(type==CurveGeometryType.Line)
                {Require(neighbors.Length==1,"An open-side line cannot be treated as a periodic seam.");continue;}
                Require(neighbors.Length==2,"Circular boundary needs exactly one distinct adjacent face.");
                int n=neighbors.Single(x=>x!=f);if(!circular.TryGetValue(n,out var group))circular.Add(n,group=[]);group.Add(e);
            }
            foreach(var pair in circular)
            {
                int n=pair.Key;var edgeIds=pair.Value.Order().ToArray();var circle=Ring(edgeIds);
                var neighbor=map.Ancestors[n];string role="transition";
                if(neighbor.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Plane)
                {
                    var projections=SurfaceModeling.ProjectPoint(neighbor,new(circle.Center.X,circle.Center.Y,circle.Center.Z),limitToFace:false);
                    Require(projections.Count==1&&projections[0].Distance<Tol,"No unique planar boundary center projection.");
                    role=projections[0].DomainState switch
                    {SurfaceDomainState.Outside=>"aperture",SurfaceDomainState.Inside=>"cap",_=>"unproved"};
                }
                ends.Add(new(edgeIds,n,circle,role));
            }
            Require(ends.Count==2,"The wall needs two complete circular ends.");
            var axis=ends[1].Circle.Center-ends[0].Circle.Center;
            Require(axis.Length>1e-6&&axis.Length<=1e6,"Invalid finite wall depth.");axis*=1/axis.Length;
            double v=(surface.FirstVParameter+surface.LastVParameter)/2;
            using var iso=SurfaceModeling.CreateIsoEdge(face,SurfaceIsoDirection.ConstantV,v,
                new(surface.FirstUParameter,surface.LastUParameter));
            var mid=ExactCircle(iso);
            Require((mid.Center-ends[0].Circle.Center).Cross(axis).Length<Tol,"Wall centers do not share one exact axis.");
            bool? inward=null;
            for(int j=1;j<=3;j++)
            {
                var at=face.EvaluateFace(surface.FirstUParameter+(surface.LastUParameter-surface.FirstUParameter)*j/4,v);
                var radial=Point.From(at.Point)-mid.Center;
                double dot=radial.Dot(Point.From(at.Normal));
                Require(double.IsFinite(dot)&&Math.Abs(dot)>(cone?1e-5:.99)*mid.Radius&&
                    (inward is null||inward==(dot<0)),"Cylinder/cone wall orientation is inconsistent.");
                inward=dot<0;
                if(!cylinderOnly&&!allowOuter)Require(inward.Value,"The selected surface is outer material, not an inward bore wall.");
            }
            if(!cone)Require(Math.Abs(ends[0].Circle.Radius-ends[1].Circle.Radius)<Tol&&
                Math.Abs(mid.Radius-ends[0].Circle.Radius)<Tol,"Cylinder radius is inconsistent.");
            var wall=new Wall(f,Index(f),cone,ends.ToArray(),inward!.Value);wallCache.Add(f,wall);return wall;
        }
        var shaft=ReadWall(selected,false);
        if(cylinderOnly)
        {
            var a=shaft.Ends[0].Circle.Center;var delta=shaft.Ends[1].Circle.Center-a;double length=delta.Length;
            var faces=new[]{Index(selected),Index(shaft.Ends[0].Neighbor),Index(shaft.Ends[1].Neighbor)}.Distinct().Order().ToArray();
            Require(faces.Length==3,"A finite size wall requires two distinct original end neighbors.");
            var ringEdges=shaft.Ends.SelectMany(e=>e.Edges).Select(e=>RepairSnapshot.FindTopologyIndex(shape,map.Items[e])).Order().ToArray();
            Require(ringEdges.All(e=>e>=0)&&ringEdges.Distinct().Count()==ringEdges.Length,"Size ring identities are invalid.");
            using var checkedShape=RepairSnapshot.Create(shape);Require(checkedShape.Fingerprint==original.Fingerprint,"Size inspection changed the BRep.");
            token.ThrowIfCancellationRequested();
            return new(Cylinder:new(selectedIndex,faces,ringEdges,a,delta*(1/length),shaft.Ends[0].Circle.Radius*2,length,shaft.Internal));
        }
        var used=new HashSet<int>{selected};int head=0;double headDiameter=0,headDepth=0,headAngle=0;
        End? entry=null,bottom=null;
        foreach(var end in shaft.Ends)
        {
            var neighbor=map.Ancestors[end.Neighbor];var type=neighbor.GetFaceSurfaceSnapshot().SurfaceType;
            Wall? recess=null;End? shoulderEnd=null;
            if(type==SurfaceGeometryType.Cone)
            {
                recess=ReadWall(end.Neighbor,true);
                shoulderEnd=recess.Ends.SingleOrDefault(e=>e.Edges.SequenceEqual(end.Edges)&&e.Neighbor==selected);
                Require(shoulderEnd is not null,"Conical recess lacks the exact shared shaft edge.");
                head=2;
            }
            else if(type==SurfaceGeometryType.Plane)
            {
                // A planar annulus with exactly these two original circular edges is a shoulder.
                // Similar centers/radii on unrelated faces never establish this relationship.
                var planeEdges=map.GetItemIndices(end.Neighbor).ToArray();
                if(neighbor.GetTopologySummary().UniqueCounts.WireCount==2&&planeEdges.Length is >=2 and <=16&&
                    planeEdges.All(e=>map.Items[e].GetEdgeCurveSnapshot().CurveType==CurveGeometryType.Circle))
                {
                    var otherEdges=planeEdges.Except(end.Edges).ToArray();Require(otherEdges.Length>0,"Unsupported planar circular boundary.");
                    var adjacent=otherEdges.SelectMany(e=>map.GetAncestorIndices(e).ToArray()).Where(f=>f!=end.Neighbor).Distinct().ToArray();
                    Require(adjacent.Length==1,"Ambiguous recess shoulder adjacency.");
                    var candidate=ReadWall(adjacent[0],false,allowOuter:true);
                    // The two circular bounds of a tube's end face join the bore and the
                    // exterior cylinder. An outward wall is not a counterbore recess.
                    if(candidate.Internal)
                    {
                        recess=candidate;
                        shoulderEnd=recess.Ends.SingleOrDefault(e=>e.Edges.SequenceEqual(otherEdges.Order())&&e.Neighbor==end.Neighbor);
                        Require(shoulderEnd is not null,"Counterbore lacks its original shoulder edge.");
                        used.Add(end.Neighbor);head=1;
                    }
                }
            }
            if(recess is not null)
            {
                Require(entry is null&&recess.Face!=selected,"Multiple recesses require a separately supported hole definition.");
                used.Add(recess.Face);var opening=recess.Ends.Single(e=>e!=shoulderEnd);
                Require(opening.Role=="aperture"&&map.Ancestors[opening.Neighbor].GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Plane,
                    "Recess must open on a proved bounded planar aperture.");
                Require(opening.Circle.Radius>shaft.Ends[0].Circle.Radius+Tol,
                    "Select the smaller cylindrical shaft, not the counterbore wall.");
                var shaftAxis=shaft.Ends[1].Circle.Center-shaft.Ends[0].Circle.Center;shaftAxis*=1/shaftAxis.Length;
                var offset=opening.Circle.Center-end.Circle.Center;
                Require(offset.Length>1e-6&&offset.Cross(shaftAxis).Length<Tol&&
                    (shoulderEnd!.Circle.Center-end.Circle.Center).Length<Tol,
                    "Head and shaft are not coaxial at the exact shared shoulder.");
                var other=shaft.Ends.Single(e=>e!=end);
                Require(offset.Dot(other.Circle.Center-end.Circle.Center)<-1e-12,"Recess overlaps the shaft interior.");
                if(recess.Cone)Require(Math.Abs(shoulderEnd!.Circle.Radius-end.Circle.Radius)<Tol,
                    "Conical recess radius differs at the original shared edge.");
                headDiameter=opening.Circle.Radius*2;headDepth=offset.Length;
                if(recess.Cone)headAngle=2*Math.Atan((opening.Circle.Radius-end.Circle.Radius)/headDepth);
                entry=opening;bottom=other;
            }
        }
        if(entry is null)
        {
            Require(shaft.Ends.All(e=>e.Role is "aperture" or "cap")&&shaft.Ends.Any(e=>e.Role=="aperture"),
                "Closed cavities or unsupported transitions are not manufacturing holes.");
            // A plain through hole has two equally valid ends. Choose the original surface's
            // first finite V end; the persisted direction remains evidence of this same BRep.
            entry=shaft.Ends.First(e=>e.Role=="aperture");bottom=shaft.Ends.Single(e=>e!=entry);
        }
        Require(bottom!.Role is "aperture" or "cap","Unproved hole bottom.");
        used.Add(entry.Neighbor);used.Add(bottom.Neighbor);
        var direction=bottom.Circle.Center-entry.Circle.Center;double depth=direction.Length;direction*=1/depth;
        Require(double.IsFinite(depth)&&depth is >1e-6 and <=1e6,"Invalid complete hole depth.");
        using var after=RepairSnapshot.Create(shape);Require(after.Fingerprint==original.Fingerprint,"Hole inspection changed the BRep.");
        token.ThrowIfCancellationRequested();
        return new(Hole:new(selectedIndex,used.Select(Index).Order().ToArray(),entry.Circle.Center,direction,
            shaft.Ends[0].Circle.Radius*2,bottom.Role=="aperture",depth,head,headDiameter,headDepth,headAngle));
    }
}
