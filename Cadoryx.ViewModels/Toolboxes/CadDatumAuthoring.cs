using System.Text.Json;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using static Cadoryx.ViewModels.Toolboxes.CadAgentParameterTools;

namespace Cadoryx.ViewModels.Toolboxes;

internal static class CadDatumAuthoring
{
    internal static string[] Fields(string action)=>action switch
    {
        "set_plane" or "set_axis" or "set_point"=>["id","revision","partId","name","author"],
        "delete" or "detach"=>["id","revision"],
        "bind_sketch" or "unbind_sketch"=>["sketchId","sketchRevision","datumId","datumRevision"],
        "bind_workplane" or "unbind_workplane"=>["datumId","datumRevision","path"],
        "inspect_sources"=>["featureId","revision","offset","limit"],
        _=>throw new ArgumentException("Unknown datum action.")
    };

    internal static async Task<string> ApplyAsync(CadDocumentViewModel document,DocumentCommandExpectation expected,
        JsonElement args,bool includesIdentity,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var action=RequiredString(args,"action",32);
        if(action=="contract"){RequireFields(args,["action"]);return CadAgentDatumTools.Contract();}
        RequireFields(args,["action",..includesIdentity?new[]{"documentId","stateId"}:[],..Fields(action)]);
        Guard(document,expected);
        if(action=="inspect_sources")return await InspectSourcesAsync(document,expected,args,token);
        if(document.IsReadOnly)throw new InvalidOperationException("Return to the history end of an editable document before authoring datums.");
        var before=document.Session.Snapshot;
        var create=action is "set_plane" or "set_axis" or "set_point"&&!args.TryGetProperty("id",out _);
        Guid? resultId=action is "set_plane" or "set_axis" or "set_point" or "delete" or "detach"?
            create?Guid.NewGuid():RequiredGuid(args,"id"):null;
        if(create&&args.TryGetProperty("revision",out _))throw new ArgumentException("A new datum cannot carry an old revision.");
        await document.Session.ExecuteAsync(new SetDatumsCommand((context,ct)=>BuildAsync(context,args,action,resultId,create,ct)),token,expected);
        var after=document.Session.Snapshot;var changed=after.StateId!=before.StateId;
        return JsonSerializer.Serialize(new{success=true,action,id=resultId?.ToString("D"),changed,
            createdIds=create&&changed?new[]{resultId!.Value.ToString("D")}:[],
            revision=resultId is {} id&&action is not "delete"?DatumConstructions.Revision(after.Datums,id).ToString("D"):null,
            documentId=after.Id.ToString(),stateId=after.StateId.ToString()},CadAuthoringInspection.JsonOptions);
    }

    private static void Guard(CadDocumentViewModel document,DocumentCommandExpectation expected)
    {
        if(document.IsDetached||document.IsClosingRequested||document.Session.IsClosing)throw new InvalidOperationException("The active document is closing or detached.");
        expected.Validate(document.Session.Snapshot);
    }
    private static async Task<string> InspectSourcesAsync(CadDocumentViewModel document,DocumentCommandExpectation expected,JsonElement args,CancellationToken token)
    {
        using var capture=document.Session.Capture();var snapshot=capture.Snapshot;expected.Validate(snapshot);
        var id=new FeatureId(RequiredGuid(args,"featureId"));var revision=new GeometryRevisionId(RequiredGuid(args,"revision"));
        var offset=Integer(args,"offset",0,0,100000);var limit=Integer(args,"limit",50,1,50);
        if(document.GeometryKernel is not IDatumGeometryResolver resolver)throw new NotSupportedException("Kernel has no exact datum source inspector.");
        var page=await resolver.InspectDatumSourcesAsync(snapshot,id,revision,document.Session.Assets,offset,limit,token);
        token.ThrowIfCancellationRequested();Guard(document,expected);
        return JsonSerializer.Serialize(new{success=true,action="inspect_sources",documentId=snapshot.Id.ToString(),stateId=snapshot.StateId.ToString(),
            featureId=id.ToString(),partId=snapshot.Features[id].PartId.ToString(),revision=revision.ToString(),offset,limit,total=page.Total,
            hasMore=(long)offset+limit<page.Total,coordinateSpace="part_local",units=new{length="mm",angle="rad"},
            items=page.Items.Select(slot=>new{slot.Index,slot.ShapeKind,selection=slot.Selection is {} s?CadDatumJson.Selection(s):null,
                geometry=slot.Geometry is {} g?new{kind=g.Kind.ToString(),point=CadDatumJson.Vector(g.Point),direction=CadDatumJson.Vector(g.Direction),radius=g.Radius}:null,
                slot.Diagnostic}).ToArray()},CadAuthoringInspection.JsonOptions);
    }

    private static async Task<DatumBook> BuildAsync(DocumentCommandContext context,JsonElement args,string action,Guid? resultId,bool create,CancellationToken token)
    {
        var doc=context.Snapshot;var book=doc.Datums;
        if(action is "set_plane" or "set_axis" or "set_point")
        {
            var id=resultId!.Value;var part=new DefinitionId(RequiredGuid(args,"partId"));var name=RequiredString(args,"name",64);
            if(doc.Definitions.GetValueOrDefault(part) is not PartDefinition||doc.ExternalParts.ContainsKey(part))throw new ArgumentException("A local part definition is required.");
            if(!create)
            {
                CheckRevision(book,id,RequiredGuid(args,"revision"));
                var owner=book.Planes.TryGetValue(id,out var p)?p.PartId:book.Axes.TryGetValue(id,out var a)?a.PartId:book.Points[id].PartId;
                if(owner!=part)throw new ArgumentException("Datum ownership cannot change. Create a new datum explicitly.");
            }
            var author=args.GetProperty("author");
            if(action=="set_plane")
            {
                if(!create&&!book.Planes.ContainsKey(id))throw new ArgumentException("Exact ID is not a plane datum.");
                var kind=EnumValue<DatumPlaneKind>(author,"kind");
                RequireFields(author,["kind","primary",..kind==DatumPlaneKind.Midplane?new[]{"secondary"}:new[]{"value"},..kind==DatumPlaneKind.Angle?new[]{"hinge","hingeAxis"}:[]]);
                var previous=book.Planes.GetValueOrDefault(id);
                var primary=await PlaneSource(context,part,author.GetProperty("primary"),token,previous?.Primary);
                var secondary=kind==DatumPlaneKind.Midplane?await PlaneSource(context,part,author.GetProperty("secondary"),token,previous?.Secondary):null;
                var plane=new CadDatumPlane(id,part,name,kind,primary,secondary,kind==DatumPlaneKind.Midplane?0:Number(author,"value",0),
                    kind==DatumPlaneKind.Angle?EnumValue<DatumHinge>(author,"hinge",DatumHinge.U):DatumHinge.U){HingeAxis=OptionalId(author,"hingeAxis")};
                if(book.Planes.TryGetValue(id,out var old))plane=plane with{Revision=old.Revision,Frame=old.Frame,SourceRevisions=old.SourceRevisions,IsStale=old.IsStale};
                plane.Validate();return old==plane?book:book with{Planes=book.Planes.SetItem(id,plane)};
            }
            if(action=="set_axis")
            {
                if(!create&&!book.Axes.ContainsKey(id))throw new ArgumentException("Exact ID is not an axis datum.");
                var kind=EnumValue<DatumAxisKind>(author,"kind");
                string[] extra=kind switch{DatumAxisKind.Basis=>["basis"],DatumAxisKind.TwoPoints or DatumAxisKind.PlaneIntersection=>["first","second"],
                    DatumAxisKind.PlaneNormal=>["first"],DatumAxisKind.Analytic=>["selection"],DatumAxisKind.Independent=>["direction"],_=>[]};
                RequireFields(author,["kind","origin","reverse","offset",..extra]);
                var axis=new CadDatumAxis(id,part,name,kind,OptionalId(author,"first"),OptionalId(author,"second"),
                    kind==DatumAxisKind.Analytic?await Exact(context,part,author.GetProperty("selection"),token):null,
                    kind==DatumAxisKind.Basis?EnumValue<DatumBasisAxis>(author,"basis",DatumBasisAxis.Z):DatumBasisAxis.Z,
                    Boolean(author,"reverse",false),Vector(author,"origin",Vector3d.Zero),kind==DatumAxisKind.Independent?Vector(author,"direction"):Vector3d.Zero,Number(author,"offset",0));
                if(book.Axes.TryGetValue(id,out var old))axis=axis with{Revision=old.Revision,Frame=old.Frame,SourceRevisions=old.SourceRevisions,IsStale=old.IsStale};
                axis.Validate();return old==axis?book:book with{Axes=book.Axes.SetItem(id,axis)};
            }
            if(!create&&!book.Points.ContainsKey(id))throw new ArgumentException("Exact ID is not a point datum.");
            var pointKind=EnumValue<DatumPointKind>(author,"kind");
            string[] pointFields=pointKind switch{DatumPointKind.Independent=>["value"],DatumPointKind.Offset=>["value","first"],
                DatumPointKind.Midpoint or DatumPointKind.AxisPlane=>["first","second"],DatumPointKind.ThreePlanes=>["first","second","third"],DatumPointKind.CircleCenter=>["selection"],_=>[]};
            RequireFields(author,["kind",..pointFields]);
            var point=new CadDatumPoint(id,part,name,pointKind,pointKind is DatumPointKind.Independent or DatumPointKind.Offset?Vector(author,"value"):Vector3d.Zero,
                OptionalId(author,"first"),OptionalId(author,"second"),OptionalId(author,"third"),
                pointKind==DatumPointKind.CircleCenter?await Exact(context,part,author.GetProperty("selection"),token):null);
            if(book.Points.TryGetValue(id,out var oldPoint))point=point with{Revision=oldPoint.Revision,Position=oldPoint.Position,SourceRevisions=oldPoint.SourceRevisions,IsStale=oldPoint.IsStale};
            point.Validate();return oldPoint==point?book:book with{Points=book.Points.SetItem(id,point)};
        }
        if(action is "delete" or "detach")
        {
            var id=resultId!.Value;CheckRevision(book,id,RequiredGuid(args,"revision"));
            if(action=="delete")
            {
                if(book.SketchBindings.Values.Any(b=>b.Datum==id)||book.WorkPlane?.Datum==id||
                    doc.Parameters.Bindings.Any(b=>b.Target.Kind==ParameterTargetKind.Datum&&b.Target.Owner==id)||
                    doc.Parameters.Parameters.Any(p=>p.Reference is {Kind:ParameterTargetKind.Datum} t&&t.Owner==id))
                    throw new ArgumentException("Datum has sketch, workplane or parameter dependents. Explicitly detach them before deleting.");
                var next=book with{Planes=book.Planes.Remove(id),Axes=book.Axes.Remove(id),Points=book.Points.Remove(id)};
                _=DatumPlanes.Order(doc with{Datums=next});return next;
            }
            if(book.Planes.TryGetValue(id,out var plane))
            {
                var next=plane with{Kind=DatumPlaneKind.Offset,Primary=DatumPlaneSource.Independent(plane.Frame),Secondary=null,Value=0,Hinge=DatumHinge.U,HingeAxis=null};
                return next==plane?book:book with{Planes=book.Planes.SetItem(id,next)};
            }
            if(book.Axes.TryGetValue(id,out var axis))
            {
                var next=axis with{Kind=DatumAxisKind.Independent,First=null,Second=null,Exact=null,Basis=DatumBasisAxis.Z,Reverse=false,
                    Origin=axis.Frame.Translation,Direction=axis.Frame.Rotation.Rotate(Vector3d.UnitZ),Offset=0};
                return next==axis?book:book with{Axes=book.Axes.SetItem(id,next)};
            }
            var point=book.Points[id];var detached=point with{Kind=DatumPointKind.Independent,First=null,Second=null,Third=null,Exact=null,Value=point.Position};
            return detached==point?book:book with{Points=book.Points.SetItem(id,detached)};
        }
        var datum=RequiredGuid(args,"datumId");CheckRevision(book,datum,RequiredGuid(args,"datumRevision"));
        if(!book.Planes.TryGetValue(datum,out var target))throw new ArgumentException("Association requires an exact plane datum.");
        if(action is "bind_sketch" or "unbind_sketch")
        {
            var sketchId=new SketchId(RequiredGuid(args,"sketchId"));
            var sketch=doc.Sketches.GetValueOrDefault(sketchId)??throw new ArgumentException("Exact sketch ID is missing.");
            if(sketch.Revision!=RequiredGuid(args,"sketchRevision"))throw new StaleDocumentException();
            if(sketch.PartId!=target.PartId)throw new ArgumentException("Sketch and plane must belong to the same part.");
            book.SketchBindings.TryGetValue(sketchId,out var binding);
            if(action=="unbind_sketch")
            {
                if(binding?.Datum!=datum)throw new ArgumentException("Exact sketch datum association is missing or changed.");
                return book with{SketchBindings=book.SketchBindings.Remove(sketchId)};
            }
            if(target.IsStale)throw new DatumSourceUnavailableException();
            if(binding is not null&&binding.Datum!=datum)throw new ArgumentException("Sketch already has another plane. Explicitly unbind first.");
            return binding?.Revision==target.Revision?book:book with{SketchBindings=book.SketchBindings.SetItem(sketchId,new(sketchId,datum,target.Revision))};
        }
        var pathText=RequiredString(args,"path",8192);
        var occurrence=doc.EnumerateOccurrences().SingleOrDefault(o=>o.Path.ToString()==pathText&&o.DefinitionId==target.PartId)??
            throw new ArgumentException("Exact workplane occurrence path is missing or belongs to another definition.");
        if(action=="unbind_workplane")
        {
            if(book.WorkPlane is not {} existing||existing.Datum!=datum||!existing.Occurrence.Equals(occurrence.Path))
                throw new ArgumentException("Exact workplane association is missing or changed.");
            return book with{WorkPlane=null};
        }
        if(target.IsStale)throw new DatumSourceUnavailableException();
        if(book.WorkPlane is {} current&&(current.Datum!=datum||!current.Occurrence.Equals(occurrence.Path)))
            throw new ArgumentException("Workplane already has another association. Explicitly unbind first.");
        var workplane=new WorkPlaneDatumBinding(datum,occurrence.Path,target.Revision,occurrence.WorldTransform);
        return workplane==book.WorkPlane?book:book with{WorkPlane=workplane};
    }

    private static void CheckRevision(DatumBook book,Guid id,Guid revision)
    {
        if(!book.Planes.ContainsKey(id)&&!book.Axes.ContainsKey(id)&&!book.Points.ContainsKey(id))throw new ArgumentException("Exact datum ID is missing.");
        if(DatumConstructions.Revision(book,id)!=revision)throw new StaleDocumentException();
    }
    private static async Task<DatumPlaneSource> PlaneSource(DocumentCommandContext context,DefinitionId owner,JsonElement args,CancellationToken token,DatumPlaneSource? previous=null)
    {
        var kind=EnumValue<DatumSourceKind>(args,"kind");
        string[] extra=kind switch{DatumSourceKind.Basis=>["basis"],DatumSourceKind.Datum=>["datum"],DatumSourceKind.BoxFace=>["feature","boundary"],
            DatumSourceKind.ExactFace=>["selection","frame"],DatumSourceKind.Independent=>["frame"],DatumSourceKind.AxisNormal=>["datum","point"],_=>[]};
        RequireFields(args,["kind",..extra]);
        var source=kind switch
        {
            DatumSourceKind.Basis=>DatumPlaneSource.Base(EnumValue<DocumentWorkPlaneKind>(args,"basis")),
            DatumSourceKind.Datum=>DatumPlaneSource.Named(RequiredGuid(args,"datum")),
            DatumSourceKind.BoxFace=>DatumPlaneSource.Box(new(RequiredGuid(args,"feature")),EnumValue<BoxBoundary>(args,"boundary")),
            DatumSourceKind.Independent=>DatumPlaneSource.Independent(Frame(args.GetProperty("frame"),previous?.AuthoredFrame)),
            DatumSourceKind.AxisNormal=>DatumPlaneSource.Normal(RequiredGuid(args,"datum"),OptionalId(args,"point")),
            DatumSourceKind.ExactFace=>DatumPlaneSource.Face(await Exact(context,owner,args.GetProperty("selection"),token),Frame(args.GetProperty("frame"),previous?.AuthoredFrame)),
            _=>throw new ArgumentException("Unknown plane source.")
        };
        source.Validate();return source;
    }
    private static async Task<ExactDatumGeometry> Exact(DocumentCommandContext context,DefinitionId part,JsonElement args,CancellationToken token)
    {
        RequireFields(args,["featureId","revision","asset","fingerprint","index","kind","adapterVersion"]);
        var featureId=new FeatureId(RequiredGuid(args,"featureId"));
        if(!context.Snapshot.Features.TryGetValue(featureId,out var feature)||feature.PartId!=part)throw new ArgumentException("Exact topology belongs to a missing or foreign feature.");
        var selection=new ExactTopologySelection(context.Snapshot.Id,featureId,new(RequiredGuid(args,"revision")),new(RequiredString(args,"asset",64)),
            RequiredString(args,"fingerprint",64),Integer(args,"index",null,0,100000),EnumValue<HistoryShapeKind>(args,"kind"),RequiredString(args,"adapterVersion",128));
        selection.Validate();
        if(context.Kernel is not IDatumGeometryResolver resolver)throw new NotSupportedException("Kernel has no exact datum geometry resolver.");
        return await resolver.ResolveDatumGeometryAsync(context.Snapshot,selection,context.Assets,token).ConfigureAwait(false);
    }
    internal static RigidTransform3d Frame(JsonElement args,RigidTransform3d? previous=null)
    {
        RequireFields(args,["origin","normal","u"]);var origin=Vector(args,"origin");var normal=Vector(args,"normal");var u=Vector(args,"u");
        // Exact copies of emitted authored axes preserve the original quaternion bits. No
        // geometric similarity or tolerance is used to identify a different source or edit.
        if(previous is {} frame&&origin==frame.Translation&&normal==frame.Rotation.Rotate(Vector3d.UnitZ)&&u==frame.Rotation.Rotate(new(1,0,0)))return frame;
        return DatumConstructions.Frame(origin,normal,u);
    }
    internal static Vector3d Vector(JsonElement args,string name,Vector3d? fallback=null)
    {
        if(!args.TryGetProperty(name,out var value))return fallback??throw new ArgumentException("Missing "+name+".");
        if(value.ValueKind!=JsonValueKind.Array||value.GetArrayLength()!=3)throw new ArgumentException(name+" requires exactly three numbers.");
        var parts=value.EnumerateArray().Select(v=>v.ValueKind==JsonValueKind.Number&&v.TryGetDouble(out var n)&&double.IsFinite(n)&&Math.Abs(n)<=1000000?n:
            throw new ArgumentException("Invalid "+name+" coordinate.")).ToArray();return new(parts[0],parts[1],parts[2]);
    }
    internal static T EnumValue<T>(JsonElement args,string name,T? fallback=null) where T:struct,Enum
    {
        if(!args.TryGetProperty(name,out _))return fallback??throw new ArgumentException("Missing "+name+".");
        var text=RequiredString(args,name,32);return Enum.GetNames<T>().Contains(text,StringComparer.Ordinal)?Enum.Parse<T>(text):throw new ArgumentException("Unknown "+name+".");
    }
    private static Guid? OptionalId(JsonElement args,string name)=>args.TryGetProperty(name,out _)?RequiredGuid(args,name):null;
    internal static double Number(JsonElement args,string name,double? fallback=null)
    {
        if(!args.TryGetProperty(name,out var v))return fallback??throw new ArgumentException("Missing "+name+".");
        return v.ValueKind==JsonValueKind.Number&&v.TryGetDouble(out var n)&&double.IsFinite(n)&&Math.Abs(n)<=1000000?n:throw new ArgumentException("Invalid "+name+" number.");
    }
    internal static int Integer(JsonElement args,string name,int? fallback,int minimum,int maximum)
    {
        if(!args.TryGetProperty(name,out var v))return fallback??throw new ArgumentException("Missing "+name+".");
        return v.ValueKind==JsonValueKind.Number&&v.TryGetInt32(out var n)&&n>=minimum&&n<=maximum?n:throw new ArgumentException("Invalid "+name+" integer.");
    }
    private static bool Boolean(JsonElement args,string name,bool fallback)
    {
        if(!args.TryGetProperty(name,out var v))return fallback;
        return v.ValueKind is JsonValueKind.True or JsonValueKind.False?v.GetBoolean():throw new ArgumentException("Invalid "+name+" boolean.");
    }
}
