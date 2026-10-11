using System.Collections.Immutable;

namespace Cadoryx.Db;

public enum DatumSourceKind { Basis=0, Datum=1, BoxFace=2, ExactFace=3, Independent=4, AxisNormal=5 }
public enum DatumPlaneKind { Offset=0, Midplane=1, Angle=2 }
public enum DatumHinge { U=0, V=1 }
/// <summary>Part-local sources. Box boundaries identify the authored primitive, never a result face index.</summary>
public sealed record DatumPlaneSource(DatumSourceKind Kind,DocumentWorkPlaneKind Basis=DocumentWorkPlaneKind.XY,
    Guid? Datum=null,FeatureId? Feature=null,BoxBoundary? Boundary=null,
    ExactDatumGeometry? Exact=null,RigidTransform3d? AuthoredFrame=null,Guid? Point=null)
{
    public static DatumPlaneSource Base(DocumentWorkPlaneKind kind)=>new(DatumSourceKind.Basis,kind);
    public static DatumPlaneSource Named(Guid id)=>new(DatumSourceKind.Datum,Datum:id);
    public static DatumPlaneSource Box(FeatureId feature,BoxBoundary boundary)=>new(DatumSourceKind.BoxFace,Feature:feature,Boundary:boundary);
    public static DatumPlaneSource Face(ExactDatumGeometry exact,RigidTransform3d authored)=>new(DatumSourceKind.ExactFace,Exact:exact,AuthoredFrame:authored);
    public static DatumPlaneSource Independent(RigidTransform3d frame)=>new(DatumSourceKind.Independent,AuthoredFrame:frame);
    public static DatumPlaneSource Normal(Guid axis,Guid? point=null)=>new(DatumSourceKind.AxisNormal,Datum:axis,Point:point);
    public void Validate()
    {
        if(!Enum.IsDefined(Kind)||!Enum.IsDefined(Basis)||Basis==DocumentWorkPlaneKind.Custom||
            Kind==DatumSourceKind.Basis&&(Datum is not null||Feature is not null||Boundary is not null)||
            Kind==DatumSourceKind.Datum&&(Datum is null||Datum==Guid.Empty||Feature is not null||Boundary is not null||Basis!=DocumentWorkPlaneKind.XY)||
            Kind==DatumSourceKind.BoxFace&&(Datum is not null||Feature is null||Feature.Value.Value==Guid.Empty||Boundary is null||!Enum.IsDefined(Boundary.Value)||Basis!=DocumentWorkPlaneKind.XY)||
            Kind is DatumSourceKind.ExactFace or DatumSourceKind.Independent&&(Datum is not null||Feature is not null||Boundary is not null||Basis!=DocumentWorkPlaneKind.XY)||
            Kind==DatumSourceKind.AxisNormal&&(Datum is null||Datum==Guid.Empty||Feature is not null||Boundary is not null||Basis!=DocumentWorkPlaneKind.XY)||
            (Kind==DatumSourceKind.ExactFace)!=(Exact is not null)||
            (Kind is DatumSourceKind.ExactFace or DatumSourceKind.Independent)!=(AuthoredFrame is not null)||
            Kind!=DatumSourceKind.AxisNormal&&Point is not null||Point==Guid.Empty)
            throw new CadValidationException("Invalid datum plane source identity.");
        Exact?.Validate();AuthoredFrame?.Validate();
        if(Exact is not null&&Exact.Kind!=ExactDatumKind.Plane)throw new CadValidationException("Selected source is not an analytic plane.");
    }
}
public sealed record CadDatumPlane(Guid Id,DefinitionId PartId,string Name,DatumPlaneKind Kind,DatumPlaneSource Primary,
    DatumPlaneSource? Secondary=null,double Value=0,DatumHinge Hinge=DatumHinge.U)
{
    public Guid Revision {get;init;}=Guid.NewGuid();
    public RigidTransform3d Frame {get;init;}=RigidTransform3d.Identity;
    public ImmutableArray<Guid> SourceRevisions {get;init;}=[];
    public bool IsStale {get;init;}
    public Guid? HingeAxis {get;init;}
    public void Validate()
    {
        if(Id==Guid.Empty||Revision==Guid.Empty||!Enum.IsDefined(Kind)||!Enum.IsDefined(Hinge)||SourceRevisions.IsDefault||SourceRevisions.Length>5||SourceRevisions.Any(r=>r==Guid.Empty)||HingeAxis==Guid.Empty||Kind!=DatumPlaneKind.Angle&&HingeAxis is not null)
            throw new CadValidationException("Invalid datum plane identity or kind.");
        CadGuard.Id(PartId);CadGuard.Name(Name);CadGuard.Finite(Value);Frame.Validate();Primary.Validate();Secondary?.Validate();
        if((Kind==DatumPlaneKind.Midplane)!=(Secondary is not null)||Kind==DatumPlaneKind.Midplane&&Value!=0||
            Kind!=DatumPlaneKind.Angle&&Hinge!=DatumHinge.U||Math.Abs(Value)>(Kind==DatumPlaneKind.Angle?20*Math.PI:1_000_000))
            throw new CadValidationException("Datum offset, midpoint or angle contract is invalid.");
    }
}
public sealed record SketchDatumBinding(SketchId Sketch,Guid Datum,Guid Revision);
public sealed record WorkPlaneDatumBinding(Guid Datum,OccurrencePath Occurrence,Guid Revision,RigidTransform3d OccurrenceTransform);
public sealed record DatumBook(ImmutableDictionary<Guid,CadDatumPlane> Planes,
    ImmutableDictionary<SketchId,SketchDatumBinding> SketchBindings,WorkPlaneDatumBinding? WorkPlane=null)
{
    public static DatumBook Empty {get;}=new(ImmutableDictionary<Guid,CadDatumPlane>.Empty,ImmutableDictionary<SketchId,SketchDatumBinding>.Empty);
    public ImmutableDictionary<Guid,CadDatumAxis> Axes {get;init;}=ImmutableDictionary<Guid,CadDatumAxis>.Empty;
    public ImmutableDictionary<Guid,CadDatumPoint> Points {get;init;}=ImmutableDictionary<Guid,CadDatumPoint>.Empty;
    public bool IsEmpty=>Planes.Count==0&&Axes.Count==0&&Points.Count==0&&SketchBindings.Count==0&&WorkPlane is null;
}

public static class DatumPlanes
{
    public static (RigidTransform3d Frame,ImmutableArray<Guid> Revisions) Resolve(DocumentSnapshot doc,CadDatumPlane plane,bool frozen=false)
    {
        plane.Validate();var (first,firstRevision)=Source(doc,plane.PartId,plane.Primary,frozen);
        var revisions=firstRevision is {} r?ImmutableArray.Create(r):ImmutableArray<Guid>.Empty;
        if(plane.Primary.Point is {} primaryPoint)revisions=revisions.Add(DatumConstructions.Point(doc,plane.PartId,primaryPoint,frozen).Revision);
        var normal=first.Rotation.Rotate(Vector3d.UnitZ);RigidTransform3d frame;
        switch(plane.Kind)
        {
            case DatumPlaneKind.Offset:frame=first with{Translation=first.Translation+normal*plane.Value};break;
            case DatumPlaneKind.Angle:
                if(plane.HingeAxis is {} axisId)
                {
                    var axis=DatumConstructions.Axis(doc,plane.PartId,axisId,frozen);var direction=axis.Frame.Rotation.Rotate(Vector3d.UnitZ);
                    if(Math.Abs(direction.Dot(normal))>1e-8||Math.Abs((axis.Frame.Translation-first.Translation).Dot(normal))>1e-7)
                        throw new CadValidationException("Angular datum hinge must lie in its source plane.");
                    var rotate=Quaterniond.FromAxisAngle(direction,plane.Value);
                    frame=new(axis.Frame.Translation+rotate.Rotate(first.Translation-axis.Frame.Translation),rotate*first.Rotation);revisions=revisions.Add(axis.Revision);break;
                }
                var hinge=plane.Hinge==DatumHinge.U?new Vector3d(1,0,0):new Vector3d(0,1,0);
                frame=first with{Rotation=first.Rotation*Quaterniond.FromAxisAngle(hinge,plane.Value)};break;
            case DatumPlaneKind.Midplane:
                var (second,secondRevision)=Source(doc,plane.PartId,plane.Secondary!,frozen);
                if(Math.Abs(Math.Abs(normal.Dot(second.Rotation.Rotate(Vector3d.UnitZ)))-1)>1e-10)
                    throw new CadValidationException("Midplane sources must be parallel or antiparallel.");
                frame=first with{Translation=first.Translation+normal*((second.Translation-first.Translation).Dot(normal)/2)};
                if(secondRevision is {} sr)revisions=revisions.Add(sr);break;
            default:throw new CadValidationException("Unknown datum plane kind.");
        }
        if(plane.Secondary?.Point is {} secondaryPoint)revisions=revisions.Add(DatumConstructions.Point(doc,plane.PartId,secondaryPoint,frozen).Revision);
        frame.Validate();new DocumentWorkPlaneSettings(DocumentWorkPlaneKind.Custom){CustomOrigin=frame.Translation,CustomRotation=frame.Rotation}.Validate();
        return(frame,revisions);
    }
    private static (RigidTransform3d,Guid?) Source(DocumentSnapshot doc,DefinitionId owner,DatumPlaneSource source,bool frozen)
    {
        source.Validate();
        if(source.Kind==DatumSourceKind.Independent)return(source.AuthoredFrame!.Value,null);
        if(source.Kind==DatumSourceKind.ExactFace)
        {
            var exact=source.Exact!;if(!exact.IsCurrent(doc,owner)&&!frozen)throw new DatumSourceUnavailableException();
            var authored=source.AuthoredFrame!.Value;var normal=exact.Direction;
            if(Math.Abs(normal.Dot(authored.Rotation.Rotate(Vector3d.UnitZ)))<1e-8)throw new CadValidationException("Authored plane normal does not define a side of its exact source.");
            if(normal.Dot(authored.Rotation.Rotate(Vector3d.UnitZ))<0)normal=normal*-1;
            var origin=authored.Translation+normal*(exact.Point-authored.Translation).Dot(normal);
            return(DatumConstructions.Frame(origin,normal,authored.Rotation.Rotate(new(1,0,0))),exact.Selection.Revision.Value);
        }
        if(source.Kind==DatumSourceKind.AxisNormal)
        {
            var axis=DatumConstructions.Axis(doc,owner,source.Datum!.Value,frozen);
            var origin=source.Point is {} id?DatumConstructions.Point(doc,owner,id,frozen).Position:axis.Frame.Translation;
            return(axis.Frame with{Translation=origin},axis.Revision);
        }
        if(source.Kind==DatumSourceKind.Basis){var basis=new DocumentWorkPlaneSettings(source.Basis);return(new(basis.Origin,basis.Rotation),null);}
        if(source.Kind==DatumSourceKind.Datum)
        {
            if(!doc.Datums.Planes.TryGetValue(source.Datum!.Value,out var datum)||datum.PartId!=owner)
                throw new CadValidationException("Datum source is missing or belongs to another part. Reselect or detach explicitly.");
            if(datum.IsStale&&!frozen)throw new DatumSourceUnavailableException();return(datum.Frame,datum.Revision);
        }
        if(!doc.Features.TryGetValue(source.Feature!.Value,out var feature)||feature.PartId!=owner||feature.Recipe is not BoxRecipe box)
            throw new CadValidationException("Semantic face requires the current authored Box feature in the same part. Reselect or detach explicitly.");
        if((feature.IsStale||feature.IsSuppressed)&&!frozen)throw new DatumSourceUnavailableException();
        box.Validate();
        var local=source.Boundary switch
        {
            BoxBoundary.XMin=>new RigidTransform3d(new(0,0,0),Quaterniond.FromAxisAngle(new(0,1,0),-Math.PI/2)),
            BoxBoundary.XMax=>new RigidTransform3d(new(box.X,0,0),Quaterniond.FromAxisAngle(new(0,1,0),Math.PI/2)),
            BoxBoundary.YMin=>new RigidTransform3d(new(0,0,0),Quaterniond.FromAxisAngle(new(1,0,0),Math.PI/2)),
            BoxBoundary.YMax=>new RigidTransform3d(new(0,box.Y,0),Quaterniond.FromAxisAngle(new(1,0,0),-Math.PI/2)),
            BoxBoundary.ZMin=>new RigidTransform3d(new(0,0,0),Quaterniond.FromAxisAngle(new(1,0,0),Math.PI)),
            BoxBoundary.ZMax=>new RigidTransform3d(new(0,0,box.Z),Quaterniond.Identity),
            _=>throw new CadValidationException("Invalid semantic Box boundary.")
        };
        return(box.Placement*local,feature.Result.Revision.Value);
    }
    public static bool SameFrame(RigidTransform3d a,RigidTransform3d b)=>
        (a.Translation-b.Translation).Length<=1e-9&&
        (a.Rotation.Rotate(new(1,0,0))-b.Rotation.Rotate(new(1,0,0))).Length<=1e-10&&
        (a.Rotation.Rotate(Vector3d.UnitZ)-b.Rotation.Rotate(Vector3d.UnitZ)).Length<=1e-10;
    public static CadOccurrence Occurrence(DocumentSnapshot doc,WorkPlaneDatumBinding binding)
    {
        var datum=doc.Datums.Planes.GetValueOrDefault(binding.Datum)??throw new CadValidationException("Workplane datum is missing.");
        return doc.EnumerateOccurrences().SingleOrDefault(o=>o.Path.Equals(binding.Occurrence)&&o.DefinitionId==datum.PartId)
            ??throw new CadValidationException("The explicitly selected workplane occurrence is missing or has changed ownership.");
    }
    public static ImmutableArray<Guid> Order(DocumentSnapshot doc)
    {
        var nodes=DependencyNodes(doc);
        var levels=new Dictionary<(char,Guid),int>();var active=new List<(char,Guid)>();var order=ImmutableArray.CreateBuilder<Guid>();
        int Visit((char,Guid) key)
        {
            if(levels.TryGetValue(key,out int known))return known;if(active.Count>=128)throw new CadValidationException("Model dependency depth exceeds 128.");
            if(active.Contains(key))throw new CadValidationException("Model dependency cycle: "+string.Join(" -> ",active.Append(key)));
            if(!nodes.TryGetValue(key,out var deps))throw new CadValidationException("Model dependency source is missing.");
            active.Add(key);int level=1;foreach(var dep in deps)level=Math.Max(level,1+Visit(dep));active.RemoveAt(active.Count-1);
            if(level>128)throw new CadValidationException("Model dependency depth exceeds 128.");
            levels.Add(key,level);if(key.Item1=='D')order.Add(key.Item2);return level;
        }
        foreach(var key in nodes.Keys)Visit(key);return order.ToImmutable();
    }
    /// <summary>Feature prerequisites include source features reached through datums and sketches.</summary>
    public static IReadOnlyDictionary<FeatureId,ImmutableHashSet<FeatureId>> FeatureDependencies(DocumentSnapshot doc)
    {
        _=Order(doc);var nodes=DependencyNodes(doc);
        return doc.Features.Keys.ToDictionary(id=>id,id=>
        {
            var visited=new HashSet<(char,Guid)>();var features=ImmutableHashSet.CreateBuilder<FeatureId>();
            void Visit((char,Guid) key)
            {
                if(!visited.Add(key))return;
                foreach(var dep in nodes[key]){if(dep.Item1=='F')features.Add(new(dep.Item2));Visit(dep);}
            }
            Visit(('F',id.Value));return features.ToImmutable();
        });
    }
    internal static Dictionary<(char,Guid),List<(char,Guid)>> DependencyNodes(DocumentSnapshot doc)
    {
        if(doc.Datums is null||doc.Datums.Planes is null||doc.Datums.Axes is null||doc.Datums.Points is null||doc.Datums.SketchBindings is null||doc.Datums.Planes.Count+doc.Datums.Axes.Count+doc.Datums.Points.Count>256||doc.Datums.SketchBindings.Count>4096)
            throw new CadValidationException("Invalid or oversized datum book.");
        var nodes=new Dictionary<(char,Guid),List<(char,Guid)>>();
        List<(char,Guid)> Node(char kind,Guid id)=>nodes.GetValueOrDefault((kind,id))??(nodes[(kind,id)]=[]);
        void Reference(Guid id,DefinitionId owner,char type)
        {
            bool valid=type switch{'P'=>doc.Datums.Planes.TryGetValue(id,out var p)&&p.PartId==owner,'A'=>doc.Datums.Axes.TryGetValue(id,out var a)&&a.PartId==owner,
                'T'=>doc.Datums.Points.TryGetValue(id,out var t)&&t.PartId==owner,_=>false};
            if(!valid)throw new CadValidationException("Datum reference has the wrong type, is missing or belongs to another part.");
        }
        foreach(var f in doc.Features.Values)
        {var deps=Node('F',f.Id.Value);deps.AddRange(f.Inputs.Select(id=>('F',id.Value)));deps.AddRange(GuidedAuthoringSources.Sketches(f).Select(id=>('S',id.Value)));
            if(f.Recipe is RepetitionRecipe {Datum: {} datum} repetition){Reference(datum,f.PartId,doc.Datums.Axes.ContainsKey(datum)&&repetition.Kind!=RepetitionKind.Mirror?'A':'P');deps.Add(('D',datum));}
            if(f.Recipe is HolePatternRecipe {Layout:{Datum:{} patternDatum} layout}){Reference(patternDatum,f.PartId,doc.Datums.Axes.ContainsKey(patternDatum)&&layout.Kind!=RepetitionKind.Mirror?'A':'P');deps.Add(('D',patternDatum));}
            if(f.Recipe is ExtrusionPatternRecipe {Layout:{Datum:{} replayDatum} extrusionLayout}){Reference(replayDatum,f.PartId,doc.Datums.Axes.ContainsKey(replayDatum)&&extrusionLayout.Kind!=RepetitionKind.Mirror?'A':'P');deps.Add(('D',replayDatum));}
            if(f.Recipe is HoleRecipe {Datum: {} holeDatum}){Reference(holeDatum,f.PartId,'P');deps.Add(('D',holeDatum));}
            if(f.Recipe is RevolveRecipe {Axis:{Datum:{} revolveDatum}}){Reference(revolveDatum,f.PartId,'A');deps.Add(('D',revolveDatum));}
            if(f.Recipe is DraftRecipe draft)foreach(var program in draft.Programs.Where(p=>p.Datum is not null)){Reference(program.Datum!.Value,f.PartId,'P');deps.Add(('D',program.Datum.Value));}
            if(f.Recipe is ExtrudeRecipe {Options: {Datum: {} extrusionDatum}}){Reference(extrusionDatum,f.PartId,'P');deps.Add(('D',extrusionDatum));}
            if(f.Recipe is ExtrudeRecipe {Options: {LimitFace: {} face}})deps.Add(('F',face.FeatureId.Value));}
        foreach(var s in doc.Sketches.Values){var deps=Node('S',s.Id.Value);deps.AddRange(s.Projections.Select(p=>('S',p.SourceSketch.Value)));
            deps.AddRange(s.NativeProjections.Where(p=>doc.Features.ContainsKey(p.Selection.FeatureId)).Select(p=>('F',p.Selection.FeatureId.Value)));}
        var names=new HashSet<(DefinitionId,string)>();
        var datumIds=new HashSet<Guid>();
        void Owner(Guid key,Guid id,DefinitionId owner,string name)
        {if(key!=id||!datumIds.Add(id)||!doc.Definitions.TryGetValue(owner,out var part)||part is not PartDefinition||!names.Add((owner,name.ToUpperInvariant())))throw new CadValidationException("Datum key, owner, identity or name is invalid/duplicated.");}
        foreach(var (id,p) in doc.Datums.Planes)
        {
            p.Validate();Owner(id,p.Id,p.PartId,p.Name);
            var deps=Node('D',id);
            if(p.HingeAxis is {} hinge){Reference(hinge,p.PartId,'A');deps.Add(('D',hinge));}
            foreach(var source in new[]{p.Primary,p.Secondary}.OfType<DatumPlaneSource>())
            {
                if(source.Kind is DatumSourceKind.Datum or DatumSourceKind.AxisNormal){Reference(source.Datum!.Value,p.PartId,source.Kind==DatumSourceKind.Datum?'P':'A');deps.Add(('D',source.Datum.Value));}
                if(source.Point is {} point){Reference(point,p.PartId,'T');deps.Add(('D',point));}
                if(source.Kind==DatumSourceKind.BoxFace)deps.Add(('F',source.Feature!.Value.Value));
                if(source.Exact is {} exact){_=exact.IsCurrent(doc,p.PartId);deps.Add(('F',exact.Selection.FeatureId.Value));}
            }
        }
        foreach(var(id,a) in doc.Datums.Axes)
        {a.Validate();Owner(id,a.Id,a.PartId,a.Name);var deps=Node('D',id);if(a.First is {} first){Reference(first,a.PartId,a.Kind==DatumAxisKind.TwoPoints?'T':'P');deps.Add(('D',first));}if(a.Second is {} second){Reference(second,a.PartId,a.Kind==DatumAxisKind.TwoPoints?'T':'P');deps.Add(('D',second));}if(a.Exact is {} exact){_=exact.IsCurrent(doc,a.PartId);deps.Add(('F',exact.Selection.FeatureId.Value));}}
        foreach(var(id,p) in doc.Datums.Points)
        {p.Validate();Owner(id,p.Id,p.PartId,p.Name);var deps=Node('D',id);if(p.First is {} first){Reference(first,p.PartId,p.Kind==DatumPointKind.AxisPlane?'A':p.Kind==DatumPointKind.ThreePlanes?'P':'T');deps.Add(('D',first));}
            if(p.Second is {} second){Reference(second,p.PartId,p.Kind is DatumPointKind.AxisPlane or DatumPointKind.ThreePlanes?'P':'T');deps.Add(('D',second));}
            if(p.Third is {} third){Reference(third,p.PartId,'P');deps.Add(('D',third));}if(p.Exact is {} exact){_=exact.IsCurrent(doc,p.PartId);deps.Add(('F',exact.Selection.FeatureId.Value));}}
        foreach(var (id,b) in doc.Datums.SketchBindings)
        {
            if(id!=b.Sketch||b.Revision==Guid.Empty||!doc.Sketches.TryGetValue(id,out var sketch)||!doc.Datums.Planes.TryGetValue(b.Datum,out var plane)||sketch.PartId!=plane.PartId)
                throw new CadValidationException("Sketch datum binding has invalid identity or ownership.");
            Node('S',id.Value).Add(('D',b.Datum));
        }
        return nodes;
    }
    public static bool HasStaleInput(DocumentSnapshot doc,FeatureId id)
    {
        if(doc.Datums.IsEmpty)return false;
        var nodes=DependencyNodes(doc);var visited=new HashSet<(char,Guid)>();
        bool Visit((char,Guid) key)=>visited.Add(key)&&(key.Item1=='D'&&DatumConstructions.IsStale(doc.Datums,key.Item2)||nodes[key].Any(Visit));
        return Visit(('F',id.Value));
    }
    public static void ValidateApplied(DocumentSnapshot doc)
    {
        if(doc.Datums is {IsEmpty:true})return;
        foreach(var id in Order(doc))
        {
            try
            {
                if(doc.Datums.Planes.TryGetValue(id,out var plane))
                {var (frame,revisions)=Resolve(doc,plane);if(plane.IsStale||!SameFrame(frame,plane.Frame)||!revisions.SequenceEqual(plane.SourceRevisions))throw new CadValidationException("Datum plane cache is stale.");}
                else if(doc.Datums.Axes.TryGetValue(id,out var axis))
                {var (frame,revisions)=DatumConstructions.Resolve(doc,axis);if(axis.IsStale||!SameFrame(frame,axis.Frame)||!revisions.SequenceEqual(axis.SourceRevisions))throw new CadValidationException("Datum axis cache is stale.");}
                else
                {var point=doc.Datums.Points[id];var(position,revisions)=DatumConstructions.Resolve(doc,point);if(point.IsStale||(position-point.Position).Length>1e-9||!revisions.SequenceEqual(point.SourceRevisions))throw new CadValidationException("Datum point cache is stale.");}
            }
            catch(DatumSourceUnavailableException)
            {
                if(!DatumConstructions.IsStale(doc.Datums,id))throw new CadValidationException("Changed datum source must be explicitly frozen.");
                if(doc.Datums.Planes.TryGetValue(id,out var p))
                {var(frame,revisions)=Resolve(doc,p,true);if(!SameFrame(frame,p.Frame)||!revisions.SequenceEqual(p.SourceRevisions))throw new CadValidationException("Frozen plane cache differs from its retained source evidence.");}
                else if(doc.Datums.Axes.TryGetValue(id,out var a))
                {var(frame,revisions)=DatumConstructions.Resolve(doc,a,true);if(!SameFrame(frame,a.Frame)||!revisions.SequenceEqual(a.SourceRevisions))throw new CadValidationException("Frozen axis cache differs from its retained source evidence.");}
                else
                {var point=doc.Datums.Points[id];var(position,revisions)=DatumConstructions.Resolve(doc,point,true);if((position-point.Position).Length>1e-9||!revisions.SequenceEqual(point.SourceRevisions))throw new CadValidationException("Frozen point cache differs from its retained source evidence.");}
            }
        }
        foreach(var b in doc.Datums.SketchBindings.Values)
        {var p=doc.Datums.Planes[b.Datum];if(b.Revision!=p.Revision||!SameFrame(doc.Sketches[b.Sketch].Plane,p.Frame))throw new CadValidationException("Sketch datum cache is stale.");}
        if(doc.Datums.WorkPlane is {} w)
        {
            w.OccurrenceTransform.Validate();var occurrence=Occurrence(doc,w);var p=doc.Datums.Planes[w.Datum];var wp=doc.Settings.WorkPlane;
            if(!p.IsStale&&(w.Revision!=p.Revision||w.OccurrenceTransform!=occurrence.WorldTransform||wp.Kind!=DocumentWorkPlaneKind.Custom||wp.OffsetMm!=0||!SameFrame(new(wp.Origin,wp.Rotation),occurrence.WorldTransform*p.Frame)))
                throw new CadValidationException("Workplane datum cache is stale.");
        }
    }
}
