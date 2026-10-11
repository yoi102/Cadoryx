using System.Collections.Immutable;
using System.Text.Json;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Sketching;

namespace Cadoryx.ViewModels.Toolboxes;

internal static partial class CadModelAuthoring
{
    internal static Dictionary<string,object> Author(DocumentSnapshot doc,FeatureDefinition feature)
    {
        var recipe=feature.Recipe;var result=new Dictionary<string,object>();
        void Add(string name,object? value){if(value is not null)result[name]=value;}
        object Input()=>CadModelJson.Input(doc.Features[feature.Inputs.Single()]);
        switch(recipe)
        {
            case ExtrudeRecipe e:
                result["type"]="extrude";Add("section",Section(e.Profile,e.Placement,feature.SketchSource));Add("distance",e.Distance);
                var options=e.Options??new();Add("operation",options.Operation.ToString());Add("side",options.Side.ToString());Add("backward",options.Backward);Add("termination",options.Termination.ToString());
                if(!feature.Inputs.IsEmpty)Add("input",Input());
                if(options.LimitPlane is {} plane)Add("limit",Plane(plane,options.Datum,options.DatumRevision));
                if(options.LimitFace is {} face)Add("limitFace",CadDatumJson.Selection(face));
                if(options.SupportFace is {} support)Add("supportFace",CadDatumJson.Selection(support));
                Add("surfaceBranch",options.SurfaceBranch?.ToString());break;
            case RevolveRecipe r:
                result["type"]="revolve";Add("section",Section(r.Profile,r.Placement,feature.SketchSource));Add("angle",r.AngleRadians);
                if(r.Axis is {} axis)Add("axis",axis.Datum is {} id?new{datumId=id,revision=axis.DatumRevision}:new{point=CadDatumJson.Vector(axis.Point),direction=CadDatumJson.Vector(axis.Direction)});break;
            case BooleanRecipe b:
                result["type"]="boolean";Add("operation",b.Operation.ToString());Add("inputs",feature.Inputs.Select(id=>CadModelJson.Input(doc.Features[id])).ToArray());break;
            case HoleRecipe h:
                result["type"]="hole";Add("input",Input());Add("plane",Plane(h.Frame,h.Datum,h.DatumRevision));Add("position",CadModelJson.Write(h.Position));Add("diameter",h.Diameter);
                Add("through",h.Through);Add("depth",h.Depth);Add("head",h.Head.ToString());Add("headDiameter",h.HeadDiameter);Add("headDepth",h.HeadDepth);Add("headAngle",h.HeadAngle);
                if(h.Standard is {} standard)Add("standard",CadModelJson.Write(standard));
                if(h.Thread is {} thread)Add("thread",CadModelJson.Write(thread));break;
            case RepetitionRecipe r:result["type"]="repetition";Add("input",Input());Add("layout",Layout(r));break;
            case HolePatternRecipe p:result["type"]="hole_pattern";Add("input",Input());Add("layout",Layout(p.Layout));break;
            case ExtrusionPatternRecipe p:result["type"]="extrusion_pattern";Add("input",Input());Add("layout",Layout(p.Layout));break;
            case SweepRecipe s:
                result["type"]="sweep";Add("section",Section(s.Section.Profile,s.Section.Placement,s.Section.Source));
                Add("path",s.Path.Source is {} path?new{source=CadModelJson.Write(path)}:new{curves=CadModelJson.Write(s.Path.Curves),frame=CadDatumJson.Frame(s.Path.Placement)});
                Add("output",s.Output.ToString());Add("corner",s.Corner.ToString());break;
            case LoftRecipe l:
                result["type"]="loft";Add("stations",l.Stations.Select(CadDatumJson.Frame).ToArray());
                Add("channels",l.Channels.Select(c=>new{id=c.Id,parent=c.Parent,sections=c.Profiles.Select((p,i)=>Section(p,l.Stations[i],c.Sources.IsEmpty?null:c.Sources[i])).ToArray()}).ToArray());
                Add("output",l.Output.ToString());Add("ruled",l.Ruled);break;
            case ShellRecipe s:result["type"]="shell";Add("input",Input());Add("faces",s.ClosingFaces.Select(CadDatumJson.Selection).ToArray());Add("thickness",s.Thickness);Add("outward",s.Outward);break;
            case SolidOffsetRecipe o:result["type"]="solid_offset";Add("input",Input());Add("distance",o.Distance);break;
            case PlanarPushPullRecipe p:result["type"]="planar_push_pull";Add("input",Input());Add("face",CadDatumJson.Selection(p.Face));Add("distance",p.Distance);Add("normal",CadDatumJson.Vector(p.OutwardNormal));break;
            case DefeatureRecipe d:result["type"]="defeature";Add("input",Input());Add("faces",d.Faces.Select(CadDatumJson.Selection).ToArray());break;
            case ContourLocalRecipe c:
                result["type"]="contour_local";Add("input",Input());Add("operation",c.Operation.ToString());Add("programs",c.Programs.Select(ContourAuthor).ToArray());break;
            case DraftRecipe d:
                result["type"]="draft";Add("input",Input());Add("programs",d.Programs.Select(p=>new{id=p.Id,face=CadDatumJson.Selection(p.Face),angle=p.Angle,
                    plane=Plane(p.NeutralPlane,p.Datum,p.DatumRevision),pull=CadDatumJson.Vector(p.PullDirection),propagation=p.Propagation.ToString()}).ToArray());break;
            default:throw new NotSupportedException("Recipe has no common-model author API: "+recipe.GetType().Name);
        }
        return result;
    }
    private static object Plane(RigidTransform3d frame,Guid? id,Guid? revision)=>id is {} datum?new{datumId=datum,revision}:CadDatumJson.Frame(frame);
    private static object Section(SketchProfile profile,RigidTransform3d frame,SketchProfileReference? source)=>source is not null?
        new{source=CadModelJson.Write(source)}:new{profile=CadModelJson.Write(profile),frame=CadDatumJson.Frame(frame)};
    private static Dictionary<string,object> Layout(RepetitionRecipe r)
    {
        var result=new Dictionary<string,object>{["kind"]=r.Kind.ToString(),["count"]=r.Count,["step"]=r.Step,["copyIds"]=r.CopyIds,
            ["output"]=r.Output.ToString(),["includeSource"]=r.IncludeSource,["fullCircle"]=r.FullCircle,["stylePolicy"]=r.StylePolicy.ToString()};
        if(r.Datum is {} id){result["datumId"]=id;result["datumRevision"]=r.DatumRevision!.Value;}else result["frame"]=CadDatumJson.Frame(r.Frame);return result;
    }
    private static bool SameAuthor(DocumentSnapshot doc,FeatureDefinition a,FeatureDefinition b)=>
        JsonSerializer.Serialize(Author(doc,a),CadAuthoringInspection.JsonOptions)==JsonSerializer.Serialize(Author(doc,b),CadAuthoringInspection.JsonOptions);
    private static void GuardAssociations(GeometryRecipe old,GeometryRecipe next)
    {
        void Datum(Guid? before,Guid? after){if(before is not null&&before!=after)throw new ArgumentException("Use detach_datum explicitly before changing an associated datum source.");}
        void Profile(SketchProfileReference? before,SketchProfileReference? after)
        {if(before is not null&&after is null)throw new ArgumentException("Use detach_profile explicitly before removing a linked source.");}
        switch(old,next)
        {
            case (ExtrudeRecipe a,ExtrudeRecipe b):Datum(a.Options?.Datum,b.Options?.Datum);break;
            case (RevolveRecipe a,RevolveRecipe b):Datum(a.Axis?.Datum,b.Axis?.Datum);break;
            case (HoleRecipe a,HoleRecipe b):Datum(a.Datum,b.Datum);break;
            case (RepetitionRecipe a,RepetitionRecipe b):Datum(a.Datum,b.Datum);break;
            case (HolePatternRecipe a,HolePatternRecipe b):Datum(a.Layout.Datum,b.Layout.Datum);break;
            case (ExtrusionPatternRecipe a,ExtrusionPatternRecipe b):Datum(a.Layout.Datum,b.Layout.Datum);break;
            case (DraftRecipe a,DraftRecipe b):
                foreach(var p in a.Programs.Where(p=>p.Datum is not null))
                {var replacement=b.Programs.FirstOrDefault(q=>q.Id==p.Id)??throw new ArgumentException("Detach an associated draft program before removing it.");Datum(p.Datum,replacement.Datum);}break;
            case (SweepRecipe a,SweepRecipe b):Profile(a.Section.Source,b.Section.Source);
                if(a.Path.Source is not null&&b.Path.Source is null)throw new ArgumentException("Use detach_profile before removing a linked sweep path.");break;
            case (LoftRecipe a,LoftRecipe b):
                foreach(var c in a.Channels.Where(c=>c.Sources.Any(s=>s is not null)))
                {
                    var replacement=b.Channels.FirstOrDefault(d=>d.Id==c.Id)??throw new ArgumentException("Detach linked loft channels before removing them.");
                    if(replacement.Sources.Length<c.Sources.Length)throw new ArgumentException("Detach linked loft stations before removing them.");
                    for(int i=0;i<c.Sources.Length;i++)Profile(c.Sources[i],replacement.Sources[i]);
                }break;
        }
    }
    private static async Task<PreparedDocumentEdit> DetachAsync(DocumentCommandContext context,FeatureDefinition feature,string action,CancellationToken token)
    {
        var recipe=feature.Recipe;var source=feature.SketchSource;
        if(action=="detach_profile")
        {
            source=null;
            recipe=recipe switch
            {
                ExtrudeRecipe or RevolveRecipe=>recipe,
                SweepRecipe s=>s with{Section=s.Section with{Source=null},Path=s.Path with{Source=null}},
                LoftRecipe l=>l with{Channels=l.Channels.Select(c=>c with{Sources=[]}).ToImmutableArray()},
                _=>throw new ArgumentException("Feature has no profile/path association.")
            };
        }
        else recipe=recipe switch
        {
            ExtrudeRecipe e=>e with{Options=e.Options is {} o?o with{Datum=null,DatumRevision=null}:null},
            RevolveRecipe r=>r with{Axis=r.Axis is {} a?a with{Datum=null,DatumRevision=null}:null},
            HoleRecipe h=>h with{Datum=null,DatumRevision=null},RepetitionRecipe r=>r with{Datum=null,DatumRevision=null},
            HolePatternRecipe p=>p with{Layout=p.Layout with{Datum=null,DatumRevision=null}},
            ExtrusionPatternRecipe p=>p with{Layout=p.Layout with{Datum=null,DatumRevision=null}},
            DraftRecipe d=>d with{Programs=d.Programs.Select(p=>p with{Datum=null,DatumRevision=null}).ToImmutableArray()},
            _=>throw new ArgumentException("Feature has no datum association.")
        };
        var replacement=feature with{Recipe=recipe,SketchSource=source};
        if(SameAuthor(context.Snapshot,feature,replacement))return new(context.Snapshot);
        var staged=context.Snapshot with{Features=context.Snapshot.Features.SetItem(feature.Id,replacement)};
        return await new RecomputeCommand(feature.Id,recipe).PrepareAsync(context with{Snapshot=staged},token);
    }
    private static IEnumerable<Guid> CopyIds(GeometryRecipe? recipe)=>recipe switch{RepetitionRecipe r=>r.CopyIds,HolePatternRecipe p=>p.Layout.CopyIds,ExtrusionPatternRecipe p=>p.Layout.CopyIds,_=>[]};
    private static IEnumerable<Guid> ChannelIds(GeometryRecipe? recipe)=>recipe is LoftRecipe l?l.Channels.Select(c=>c.Id):[];
    private static IEnumerable<Guid> ProgramIds(GeometryRecipe? recipe)=>recipe switch{DraftRecipe d=>d.Programs.Select(p=>p.Id),ContourLocalRecipe c=>c.Programs.Select(p=>p.Id),_=>[]};

    internal static object InspectRecipe(DocumentSnapshot doc,FeatureDefinition f)
    {
        Dictionary<string,object>? author=null;string? diagnostic=null;
        try{author=Author(doc,f);}catch(NotSupportedException e){diagnostic=e.Message;}
        var available=author is not null&&!f.IsStale&&!f.IsSuppressed&&!FeatureSuppression.Blocked(doc).Contains(f.Id);
        return new{id=f.Id.ToString(),partId=f.PartId.ToString(),f.Name,revision=f.Result.Revision.ToString(),recipe=f.Recipe.GetType().Name,
            f.IsStale,f.IsSuppressed,available,coordinateSpace="part_local",inputs=f.Inputs.Select(id=>CadModelJson.Input(doc.Features[id])).ToArray(),
            outputBodyId=f.OutputBodyId.ToString(),displayed=doc.Bodies.ContainsKey(f.OutputBodyId),author,diagnostic,
            topologyHistory=f.TopologyHistory is {} history?new{history.SchemaVersion,history.AdapterVersion,entries=history.Entries.Length,
                copies=history.Entries.Where(e=>e.CopyId.HasValue).Select(e=>e.CopyId).Distinct().Count(),
                ambiguousPaths=history.Entries.Count(e=>e.PathAmbiguous),deleted=history.Entries.Count(e=>e.Evolution==TopologyEvolution.Deleted),
                unmapped=history.Entries.Count(e=>e.Evolution==TopologyEvolution.Unmapped)}:null,
            repetitionPresentation=f.RepetitionPresentation,
            copyHistoryContract=f.Recipe is RepetitionRecipe?"cad_model inspect_copy_history: id/revision=exact target, authorJson={source: exact selection from inspect_sources, repetitionId: chosen repetition feature, copyId: explicit layout.copyIds entry}. Native replay required; shared/split/multiple paths stop. History diagnostics do not edit bindings. layout.stylePolicy=VerifiedInheritance preserves proved subshape presentation and rejects incompatible contributors; WholeBody explicitly uses only root presentation.":null,
            edit=available?new{action="edit",documentId=doc.Id.ToString(),stateId=doc.StateId.ToString(),id=f.Id.ToString(),revision=f.Result.Revision.ToString(),authorJson=JsonSerializer.Serialize(author,CadAuthoringInspection.JsonOptions)}:null,
            reselectTemplate=f.IsStale&&f.Recipe is ShellRecipe or DraftRecipe or PlanarPushPullRecipe or DefeatureRecipe or ContourLocalRecipe or ExtrudeRecipe{Options:{LimitFace:not null}}?
                new{action="reselect",documentId=doc.Id.ToString(),stateId=doc.StateId.ToString(),id=f.Id.ToString(),revision=f.Result.Revision.ToString(),authorJson=JsonSerializer.Serialize(author,CadAuthoringInspection.JsonOptions)}:null};
    }
    internal static object InspectSketch(DocumentSnapshot doc,CadSketch s,CancellationToken token=default)=>new{id=s.Id.ToString(),partId=s.PartId.ToString(),s.Name,s.Revision,
        coordinateSpace="part_local",frame=CadDatumJson.Frame(s.Plane),stale=SketchNativeProjectionGeometry.HasStaleInputs(doc,s.Id),
        points=CadModelJson.Write(s.Points),lines=CadModelJson.Write(s.Lines),circles=CadModelJson.Write(s.Circles),arcs=CadModelJson.Write(s.Arcs),
        beziers=CadModelJson.Write(s.Beziers),splines=CadModelJson.Write(s.Splines),ellipses=CadModelJson.Write(s.Ellipses),freeforms=CadModelJson.Write(s.Freeforms),
        regionAuthors=s.RegionOffsets.Select(g=>new{g.Id,g.Distance,g.Join,g.IsFrozen,g.FrozenReason,g.Algorithm,
            sources=g.Sources.Select(l=>l.Select(v=>new{entity=v.Entity.ToString(),v.Kind,v.Reversed})),
            loops=g.Loops.Select(l=>new{l.Depth,l.Parent,l.InputTraversalReversed,outputs=l.Outputs.Select(o=>new{curve=o.Curve.ToString(),
                endpoints=new{start=o.Endpoints.Start.ToString(),end=o.Endpoints.End.ToString(),o.Endpoints.StartEquation,o.Endpoints.EndEquation},o.Role,o.SourceSlots,o.SourceTraversalReversed}),
                joints=l.Joints.Select(j=>new{j.PreviousSourceSlot,j.NextSourceSlot,previousSource=j.PreviousSource.ToString(),nextSource=j.NextSource.ToString(),
                    j.Kind,j.PreviousParameter,j.NextParameter,j.PreviousTraversalReversed,j.NextTraversalReversed})})}),
        regionSourceContract="cad_edit reselect_region_sources / SKETCH_REGION_SOURCES: sketchId/revision, id=exact group GUID, sources=explicit ordered arrays for every loop and source slot. Copy regionAuthors identities. Exact complete replay must prove unchanged loop containment/output roles; curve families/directions may change. Retained region drivers/references reject structure changes. Original source drivers keep their own identity. This correspondence is not native edge/face history.",
        constraints=s.Constraints.Select(c=>CadSketchContactAuthoring.Inspect(s,c)).ToArray(),
        constraintState=new{readOnly=true,iterations=0,evaluation="current_actual_parameters",localLinearizationOnly=true,
            analysis=new ManagedSketchConstraintSolver().Analyze(s,cancellationToken:token)},
        finiteContactContract="cad_edit set_sketch_finite_contact/delete_sketch_finite_contact: exact sketch id/revision, curveA/curveB, direction=SameDirection|OppositeDirection, continuity=G1|G2, constraintId/enabled. parameterMode=Fixed|SolveA|SolveB|SolveBoth; omitted edit mode inherits. Each fixed side requires interior parameterA/B; each automatic side requires seedA/B, lowerA/B, upperA/B and forbids read-only parameterA/B. Seeds and actual solved parameters stay separate. Strict open intervals, copied numeric geometry and curve identities are retained. constraintState evaluates current actual parameters without solving; local rank does not prove global uniqueness.",
        offsetContract="cad_edit offset_sketch_loop/offset_sketch_regions: exact documentId/stateId, sketchId/revision, seeds (loop exactly one), finite nonzero distanceMm, join=Miter|Round|Bevel (default Miter). Seeds identify isolated line/arc loops or circles; selected containment alternates material/holes. Returns independent new boundary IDs and hierarchy; no hidden offset recipe or automatic rebinding. SKETCH_OFFSET uses the same authors with captured terminal document/state.",
        curvatureContract="cad_edit set_sketch_curvature: id=sketchId, revision=exact revision, curveA/curveB=explicit line/arc/quadratic Bezier/clamped cubic IDs with exactly one shared endpoint ID. Tangent and signed inward curvature sum must agree. enabled optional; constraintId edits existing G2 only. delete_sketch_curvature requires constraintId. No point merging, no parameter-speed equality.",
        contactContract="cad_edit set_sketch_contact: id=sketchId, revision=sketch revision, curveA/curveB=explicit entities, branch=LineLeft|LineRight|External|AContainsB|BContainsA, enabled optional. Omit constraintId to create; provide existing circular-contact ID to edit. delete_sketch_contact requires constraintId. Line side is relative to Start->End regardless of operand order. Finite contact verified after solve; no point merging.",
        selectionContract="Select explicit entity GUIDs in authored order; directed paths also require reverse flags. No inferred regions or hole/channel correspondence."};
}
