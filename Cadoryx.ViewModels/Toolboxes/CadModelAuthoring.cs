using System.Collections.Immutable;
using System.Text.Json;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using static Cadoryx.ViewModels.Toolboxes.CadAgentParameterTools;
using static Cadoryx.ViewModels.Toolboxes.CadDatumAuthoring;

namespace Cadoryx.ViewModels.Toolboxes;

internal static partial class CadModelAuthoring
{
    internal static async Task<string> ApplyAsync(CadDocumentViewModel document,DocumentCommandExpectation expected,JsonElement args,bool identity,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var action=RequiredString(args,"action",32);
        string[] fields=action switch{"create"=>["partId","name","authorJson"],"inspect_contours"=>["partId","authorJson","offset","limit"],"inspect_copy_history" or "edit" or "reselect"=>["id","revision","authorJson"],
            "set_assembly"=>["id","path","name","authorJson"],"detach_assembly"=>["id"],
            "detach_profile" or "detach_datum"=>["id","revision"],_=>throw new ArgumentException("Unknown model action.")};
        RequireFields(args,["action",..identity?new[]{"documentId","stateId"}:[],..fields]);
        if(document.IsDetached||document.IsClosingRequested||document.Session.IsClosing||action is not ("inspect_contours" or "inspect_copy_history")&&document.IsReadOnly)
            throw new InvalidOperationException("Authoring requires an editable document at the history end.");
        expected.Validate(document.Session.Snapshot);var before=document.Session.Snapshot;
        if(action=="inspect_contours")return await InspectContoursAsync(document,expected,args,token);
        if(action=="inspect_copy_history")return await InspectCopyHistoryAsync(document,expected,args,token);
        if(action is "set_assembly" or "detach_assembly")return await ApplyAssemblyAsync(document,expected,args,action,token);
        using var author=action is "create" or "edit" or "reselect"?JsonDocument.Parse(RequiredString(args,"authorJson",32768)):null;
        await document.Session.ExecuteAsync(new ModelCommand(args.Clone(),action,author?.RootElement.Clone()),token,expected);
        var after=document.Session.Snapshot;var changed=after.StateId!=before.StateId;
        var features=after.Features.Keys.Except(before.Features.Keys).OrderBy(id=>id.Value).ToArray();
        var bodyIds=after.Bodies.Keys.Except(before.Bodies.Keys).OrderBy(id=>id.Value).Select(id=>id.ToString()).ToArray();
        var id=action=="create"?features.SingleOrDefault():new FeatureId(RequiredGuid(args,"id"));
        var feature=after.Features.GetValueOrDefault(id);
        return JsonSerializer.Serialize(new{success=true,action,changed,id=feature?.Id.ToString(),revision=feature?.Result.Revision.ToString(),
            documentId=after.Id.ToString(),stateId=after.StateId.ToString(),createdFeatureIds=features.Select(f=>f.ToString()).ToArray(),
            createdBodyIds=bodyIds,createdPartIds=after.Definitions.Keys.Except(before.Definitions.Keys).Select(p=>p.ToString()).ToArray(),
            createdCopyIds=CopyIds(feature?.Recipe).Except(action=="create"?[]:CopyIds(before.Features[id].Recipe)).ToArray(),
            createdChannelIds=ChannelIds(feature?.Recipe).Except(action=="create"?[]:ChannelIds(before.Features[id].Recipe)).ToArray(),
            createdProgramIds=ProgramIds(feature?.Recipe).Except(action=="create"?[]:ProgramIds(before.Features[id].Recipe)).ToArray()},CadAuthoringInspection.JsonOptions);
    }
    private static async Task<string> InspectCopyHistoryAsync(CadDocumentViewModel document,DocumentCommandExpectation expected,JsonElement args,CancellationToken token)
    {
        using var capture=document.Session.Capture();var snapshot=capture.Snapshot;expected.Validate(snapshot);
        var target=snapshot.Features.GetValueOrDefault(new(RequiredGuid(args,"id")))??throw new ArgumentException("History target is missing.");
        if(target.IsStale||target.IsSuppressed||target.Result.Revision.Value!=RequiredGuid(args,"revision"))throw new ArgumentException("History target revision changed.");
        using var input=JsonDocument.Parse(RequiredString(args,"authorJson",32768));var value=input.RootElement;
        RequireFields(value,["source","repetitionId","copyId"]);
        var sourceValue=value.GetProperty("source");var kind=EnumValue<HistoryShapeKind>(sourceValue,"kind");
        if(kind is not (HistoryShapeKind.Face or HistoryShapeKind.Edge))throw new ArgumentException("Choose an exact source face or edge.");
        var source=CadModelJson.Selection(snapshot,target.PartId,sourceValue,kind);
        var repetition=new FeatureId(RequiredGuid(value,"repetitionId"));var copyId=RequiredGuid(value,"copyId");
        var resolver=document.GeometryKernel as IRepetitionTopologyHistoryResolver??throw new NotSupportedException("Kernel has no proven copy-history resolver.");
        var result=await resolver.TraceCopyAsync(snapshot,source,target.Id,repetition,copyId,document.Session.Assets,token);
        token.ThrowIfCancellationRequested();expected.Validate(document.Session.Snapshot);
        if(document.IsDetached||document.IsClosingRequested||document.Session.IsClosing)throw new InvalidOperationException("Document closed during copy inspection.");
        return JsonSerializer.Serialize(new{success=true,action="inspect_copy_history",changed=false,documentId=snapshot.Id.ToString(),stateId=snapshot.StateId.ToString(),
            id=target.Id.ToString(),revision=target.Result.Revision.ToString(),repetitionId=repetition.ToString(),copyId,source=CadDatumJson.Selection(source),
            status=result.Status.ToString(),result.CandidateCount,result.Diagnostic,result.PathLength,result.CompletedSteps,stoppedAt=result.StoppedAt?.ToString(),
            target=result.Target is {} located?new{revision=located.Revision.ToString(),asset=located.Asset.ToString(),index=located.FullTopologyIndex,kind=located.Kind.ToString(),located.AdapterVersion}:null,
            selection=result.VerifiedSelection is {} selection?CadDatumJson.Selection(selection):null,
            contract="Explicit copy choice plus replayed native path. Splits, shared targets, deleted/unmapped slots and multiple document paths stop; no model edit or automatic rebinding."},CadAuthoringInspection.JsonOptions);
    }
    private sealed class ModelCommand(JsonElement args,string action,JsonElement? author):ICadDocumentCommand
    {
        public string Name=>"Model "+action;
        public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
        {
            var doc=context.Snapshot;FeatureDefinition? old=null;
            if(action!="create")
            {
                old=doc.Features.GetValueOrDefault(new(RequiredGuid(args,"id")))??throw new ArgumentException("Exact feature is missing.");
                if(old.Result.Revision.Value!=RequiredGuid(args,"revision"))throw new StaleDocumentException();
                if(old.IsSuppressed||old.IsStale&&action!="reselect")throw new ArgumentException("Restore or explicitly reselect a blocked feature before authoring.");
            }
            var part=old?.PartId??new DefinitionId(RequiredGuid(args,"partId"));
            if(doc.Definitions.GetValueOrDefault(part) is not PartDefinition||doc.ExternalParts.ContainsKey(part))throw new ArgumentException("Select an exact local part.");
            if(action.StartsWith("detach_",StringComparison.Ordinal))return await DetachAsync(context,old!,action,token);
            var authored=RequiredString(author!.Value,"type",32)=="contour_local"?
                await ParseContoursAsync(context,part,author.Value,old,token):RequiredString(author.Value,"type",32)=="planar_push_pull"?
                await ParsePlanarFaceEditAsync(context,part,author.Value,token):Parse(doc,part,author.Value,old);
            if(old is not null)
            {
                if(old.Recipe.GetType()!=authored.Recipe.GetType()||!old.Inputs.SequenceEqual(authored.Inputs))
                    throw new ArgumentException("An edit must preserve feature type, ordered input identities and consumption semantics.");
                if(old.SketchSource is not null&&authored.ProfileSource is null)throw new ArgumentException("Use detach_profile explicitly before replacing a linked profile with independent geometry.");
                GuardAssociations(old.Recipe,authored.Recipe);
                if(action=="reselect")
                {
                    if(authored.Recipe is ContourLocalRecipe contour)return await new ReselectContourLocalCommand(old.Id,contour).PrepareAsync(context,token);
                    if(authored.Recipe is ShellRecipe or DraftRecipe or PlanarPushPullRecipe or DefeatureRecipe)return await new ReselectFaceFeatureCommand(old.Id,authored.Recipe).PrepareAsync(context,token);
                    if(authored.Recipe is not ExtrudeRecipe{Options:{LimitFace:not null}})throw new ArgumentException("Explicit reselection supports shell/draft faces and exact extrusion limit faces.");
                }
                var proposed=old with{Recipe=authored.Recipe,SketchSource=authored.ProfileSource};
                if(SameAuthor(doc,old,proposed))return new(doc);
                return await new RecomputeCommand(old.Id,authored.Recipe,authored.ProfileSource).PrepareAsync(context,token);
            }
            var name=RequiredString(args,"name",64);ICadDocumentCommand command=authored.Recipe switch
            {
                ExtrudeRecipe e=>new AddExtrusionCommand(e,name,part,ConsumedBody(doc,authored.Inputs),authored.ProfileSource),
                RevolveRecipe or SweepRecipe or LoftRecipe=>new AddBodyCommand(authored.Recipe,name,part,sketchSource:authored.ProfileSource),
                BooleanRecipe b=>new BooleanCommand(b.Operation,authored.Inputs.Select(id=>DisplayedBody(doc,id))),
                HoleRecipe h=>new AddHoleCommand(DisplayedBody(doc,authored.Inputs.Single()),h,name),
                RepetitionRecipe r=>new AddRepetitionCommand(DisplayedBody(doc,authored.Inputs.Single()),r,name),
                HolePatternRecipe p=>new AddHolePatternCommand(DisplayedBody(doc,p.Seed),p,name),
                ExtrusionPatternRecipe p=>new AddExtrusionPatternCommand(DisplayedBody(doc,p.Seed),p,name),
                ShellRecipe or DraftRecipe or PlanarPushPullRecipe or DefeatureRecipe=>new AddFaceFeatureCommand(DisplayedBody(doc,authored.Inputs.Single()),authored.Recipe,name),
                SolidOffsetRecipe r=>new AddSolidOffsetCommand(DisplayedBody(doc,authored.Inputs.Single()),r,name),
                ContourLocalRecipe r=>new AddContourLocalCommand(DisplayedBody(doc,authored.Inputs.Single()),r,name),
                _=>throw new ArgumentException("Unsupported model creation recipe.")
            };
            var prepared=await command.PrepareAsync(context,token);
            if(authored.Recipe is not BooleanRecipe)return prepared;
            try
            {
                var next=prepared.Snapshot;var feature=next.Features.Values.Single(f=>!doc.Features.ContainsKey(f.Id));
                feature=feature with{Name=name,OutputMetadata=feature.OutputMetadata! with{Name=name}};
                next=next with{Features=next.Features.SetItem(feature.Id,feature)};
                if(next.Bodies.TryGetValue(feature.OutputBodyId,out var body))next=next with{Bodies=next.Bodies.SetItem(body.Id,body with{Name=name})};
                return new(next,[prepared]);
            }
            catch{prepared.Dispose();throw;}
        }
    }
    private static BodyId? ConsumedBody(DocumentSnapshot doc,ImmutableArray<FeatureId> inputs)=>inputs.IsEmpty?null:DisplayedBody(doc,inputs.Single());
    private static BodyId DisplayedBody(DocumentSnapshot doc,FeatureId id)
    {
        var f=doc.Features[id];
        if(!doc.Bodies.TryGetValue(f.OutputBodyId,out var body)||body.Producer!=id||body.Geometry!=f.Result)
            throw new ArgumentException("Creation consumes the exact current displayed input; an already consumed historical result is not a target.");return body.Id;
    }
    private sealed record Authored(GeometryRecipe Recipe,ImmutableArray<FeatureId> Inputs,SketchProfileReference? ProfileSource=null);
    private static Authored Parse(DocumentSnapshot doc,DefinitionId part,JsonElement args,FeatureDefinition? old)
    {
        var type=RequiredString(args,"type",32);var fields=AuthorFields(type);RequireFields(args,["type",..fields]);
        FeatureDefinition Input()=>CadModelJson.Input(doc,part,args.GetProperty("input"));
        var input=type is "hole" or "repetition" or "hole_pattern" or "extrusion_pattern" or "shell" or "draft" or "solid_offset" or "defeature"?Input():null;
        var inputs=input is null?ImmutableArray<FeatureId>.Empty:[input.Id];GeometryRecipe recipe;SketchProfileReference? source=null;
        switch(type)
        {
            case "extrude":
            {
                var operation=EnumValue<ExtrusionOperation>(args,"operation",ExtrusionOperation.NewBody);
                if(operation!=ExtrusionOperation.NewBody){input=Input();inputs=[input.Id];}
                else if(args.TryGetProperty("input",out _))throw new ArgumentException("NewBody cannot carry a hidden target input.");
                var termination=EnumValue<ExtrusionTermination>(args,"termination",ExtrusionTermination.Distance);
                var limit=ReadPlane(doc,part,args,"limit",(old?.Recipe as ExtrudeRecipe)?.Options?.LimitPlane);
                ExactTopologySelection? face=args.TryGetProperty("limitFace",out var f)?CadModelJson.Selection(doc,part,f):null;
                if(termination==ExtrusionTermination.Plane&&limit is null||termination!=ExtrusionTermination.Plane&&limit is not null||
                    (termination is ExtrusionTermination.PlanarFace or ExtrusionTermination.FiniteSurface)!=(face is not null))
                    throw new ArgumentException("Termination requires its explicit matching plane/face source and no hidden companion.");
                var support=args.TryGetProperty("supportFace",out var start)?CadModelJson.Selection(doc,part,start):null;
                ExtrusionSurfaceBranch? branch=args.TryGetProperty("surfaceBranch",out _)?EnumValue<ExtrusionSurfaceBranch>(args,"surfaceBranch"):null;
                var options=new ExtrusionOptions(EnumValue<ExtrusionSide>(args,"side",ExtrusionSide.Positive),Number(args,"backward",0),operation,input?.Result,
                    termination,limit?.Frame,limit?.Id,limit?.Revision,face,face is null?null:doc.Features[face.FeatureId].Result,support,branch);
                var section=Section(doc,part,args.GetProperty("section"),(old?.Recipe as ExtrudeRecipe)?.Placement);source=section.Source;
                recipe=new ExtrudeRecipe(section.Profile,Number(args,"distance",0),section.Placement,options);break;
            }
            case "revolve":
            {
                var section=Section(doc,part,args.GetProperty("section"),(old?.Recipe as RevolveRecipe)?.Placement);source=section.Source;
                var axis=args.TryGetProperty("axis",out var a)?Axis(doc,part,a):null;
                recipe=new RevolveRecipe(section.Profile,Number(args,"angle"),section.Placement){Axis=axis};break;
            }
            case "boolean":
                var ordered=CadModelJson.Array(args,"inputs",2,50).Select(a=>CadModelJson.Input(doc,part,a)).ToArray();
                if(ordered.Select(f=>f.Id).Distinct().Count()!=ordered.Length)throw new ArgumentException("Boolean inputs must be distinct and ordered.");
                inputs=ordered.Select(f=>f.Id).ToImmutableArray();recipe=new BooleanRecipe(EnumValue<BooleanOperation>(args,"operation"),ordered.Select(f=>f.Result).ToImmutableArray());
                if(old?.Recipe is BooleanRecipe b&&b.Operation!=((BooleanRecipe)recipe).Operation)throw new ArgumentException("Changing Boolean operation requires a new feature.");break;
            case "hole":
            {
                var plane=ReadPlane(doc,part,args,"plane",(old?.Recipe as HoleRecipe)?.Frame)??throw new ArgumentException("Hole requires an entry plane.");
                MetricClearanceSpec? standard=args.TryGetProperty("standard",out var s)?CadModelJson.Read<MetricClearanceSpec>(s):null;
                MetricThreadSpec? thread=args.TryGetProperty("thread",out var t)?CadModelJson.Read<MetricThreadSpec>(t):null;
                recipe=new HoleRecipe(input!.Result,plane.Frame,CadModelJson.Read<Point2d>(args.GetProperty("position")),Number(args,"diameter"),
                    CadModelJson.Bool(args,"through",true),Number(args,"depth",0),EnumValue<HoleHead>(args,"head",HoleHead.Plain),Number(args,"headDiameter",0),
                    Number(args,"headDepth",0),Number(args,"headAngle",0),standard,plane.Id,plane.Revision,thread);break;
            }
            case "repetition":recipe=Layout(doc,part,input!.Result,args.GetProperty("layout"),old?.Recipe as RepetitionRecipe);break;
            case "hole_pattern":
                var hole=input!.Recipe as HoleRecipe??throw new ArgumentException("Replay seed must be a current hole feature.");
                recipe=new HolePatternRecipe(input.Id,hole,Layout(doc,part,input.Result,args.GetProperty("layout"),(old?.Recipe as HolePatternRecipe)?.Layout));break;
            case "extrusion_pattern":
                var extrusion=input!.Recipe as ExtrudeRecipe??throw new ArgumentException("Replay seed must be a current add/cut extrusion feature.");
                recipe=new ExtrusionPatternRecipe(input.Id,extrusion,Layout(doc,part,input.Result,args.GetProperty("layout"),(old?.Recipe as ExtrusionPatternRecipe)?.Layout));break;
            case "sweep":
                recipe=new SweepRecipe(Section(doc,part,args.GetProperty("section"),(old?.Recipe as SweepRecipe)?.Section.Placement),
                    Path(doc,part,args.GetProperty("path"),(old?.Recipe as SweepRecipe)?.Path.Placement),
                    EnumValue<GuidedOutput>(args,"output",GuidedOutput.Solid),EnumValue<SweepCorner>(args,"corner",SweepCorner.Transformed));break;
            case "loft":recipe=Loft(doc,part,args,old?.Recipe as LoftRecipe);break;
            case "shell":recipe=new ShellRecipe(input!.Result,CadModelJson.Array(args,"faces",1).Select(a=>CadModelJson.Selection(doc,part,a)).ToImmutableArray(),
                Number(args,"thickness"),CadModelJson.Bool(args,"outward"));break;
            case "solid_offset":recipe=new SolidOffsetRecipe(input!.Result,Number(args,"distance"));break;
            case "defeature":recipe=new DefeatureRecipe(input!.Result,CadModelJson.Array(args,"faces",1,256).Select(a=>CadModelJson.Selection(doc,part,a)).ToImmutableArray());break;
            case "draft":
                var previous=old?.Recipe as DraftRecipe;
                recipe=new DraftRecipe(input!.Result,CadModelJson.Array(args,"programs",1).Select(p=>
                {
                    RequireFields(p,["id","face","angle","plane","pull","propagation"]);
                    var id=p.TryGetProperty("id",out _)?RequiredGuid(p,"id"):old is null?Guid.NewGuid():throw new ArgumentException("Edited draft programs require explicit IDs.");
                    var plane=ReadPlane(doc,part,p,"plane",previous?.Programs.FirstOrDefault(x=>x.Id==id)?.NeutralPlane)??throw new ArgumentException("Draft needs a neutral plane.");
                    return new DraftFaceProgram(id,CadModelJson.Selection(doc,part,p.GetProperty("face")),Number(p,"angle"),plane.Frame,Vector(p,"pull"),
                        EnumValue<FaceDraftPropagation>(p,"propagation",FaceDraftPropagation.SelectedFaceOnly),plane.Id,plane.Revision);
                }).ToImmutableArray());break;
            default:throw new ArgumentException("Unsupported model author type.");
        }
        recipe.Validate();return new(recipe,inputs,source);
    }
    internal static string[] AuthorFields(string type)=>type switch
    {
        "extrude"=>["section","distance","operation","side","backward","termination","input","limit","limitFace","supportFace","surfaceBranch"],
        "revolve"=>["section","angle","axis"],"boolean"=>["operation","inputs"],
        "hole"=>["input","plane","position","diameter","through","depth","head","headDiameter","headDepth","headAngle","standard","thread"],
        "repetition" or "hole_pattern" or "extrusion_pattern"=>["input","layout"],
        "sweep"=>["section","path","output","corner"],"loft"=>["stations","channels","output","ruled"],
        "contour_local"=>["input","operation","programs"],"solid_offset"=>["input","distance"],"shell"=>["input","faces","thickness","outward"],"draft"=>["input","programs"],
        "planar_push_pull"=>["input","face","distance","normal"],"defeature"=>["input","faces"],_=>throw new ArgumentException("Unknown model author type.")
    };
    private static async Task<Authored> ParsePlanarFaceEditAsync(DocumentCommandContext context,DefinitionId part,JsonElement args,CancellationToken token)
    {
        RequireFields(args,["type",..AuthorFields("planar_push_pull")]);var doc=context.Snapshot;var input=CadModelJson.Input(doc,part,args.GetProperty("input"));
        var face=CadModelJson.Selection(doc,part,args.GetProperty("face"));
        if(face.FeatureId!=input.Id)throw new ArgumentException("Planar face must belong to the exact input feature.");
        if(context.Kernel is not IFaceEditResolver resolver)throw new NotSupportedException("Kernel has no native face-edit resolver.");
        var normal=await resolver.InspectPlanarFaceAsync(doc,face,context.Assets,token);
        if(args.TryGetProperty("normal",out _)&&(Vector(args,"normal")-normal).Length>1e-10)throw new ArgumentException("Explicit normal differs from the confirmed native outward direction.");
        var recipe=new PlanarPushPullRecipe(input.Result,face,Number(args,"distance"),normal);recipe.Validate();return new(recipe,[input.Id]);
    }
    private sealed record PlaneInput(RigidTransform3d Frame,Guid? Id=null,Guid? Revision=null);
    private static PlaneInput? ReadPlane(DocumentSnapshot doc,DefinitionId part,JsonElement args,string field,RigidTransform3d? previous)
    {
        if(!args.TryGetProperty(field,out var value))return null;
        if(value.TryGetProperty("datumId",out _))
        {
            RequireFields(value,["datumId","revision"]);var id=RequiredGuid(value,"datumId");
            var plane=doc.Datums.Planes.GetValueOrDefault(id)??throw new ArgumentException("Exact plane datum is missing.");
            if(plane.PartId!=part||plane.IsStale||plane.Revision!=RequiredGuid(value,"revision"))throw new ArgumentException("Plane datum is foreign, stale or changed.");
            return new(plane.Frame,id,plane.Revision);
        }
        return new(Frame(value,previous));
    }
    private static AuthoringSection Section(DocumentSnapshot doc,DefinitionId part,JsonElement args,RigidTransform3d? previous)
    {
        if(args.TryGetProperty("source",out var s))
        {
            RequireFields(args,["source"]);var source=CadModelJson.Read<SketchProfileReference>(s);
            var e=(ExtrudeRecipe)source.Resolve(doc,part,new ExtrudeRecipe(new([]),1,RigidTransform3d.Identity));
            return new(e.Profile,e.Placement){Source=source};
        }
        RequireFields(args,["profile","frame"]);var profile=CadModelJson.Read<SketchProfile>(args.GetProperty("profile"));profile.Validate();
        return new(profile,Frame(args.GetProperty("frame"),previous));
    }
    private static SweepPath Path(DocumentSnapshot doc,DefinitionId part,JsonElement args,RigidTransform3d? previous)
    {
        if(args.TryGetProperty("source",out var s)){RequireFields(args,["source"]);return CadModelJson.Read<SketchPathReference>(s).Resolve(doc,part);}
        RequireFields(args,["curves","frame"]);var path=new SweepPath(CadModelJson.Read<ImmutableArray<SketchBoundaryCurve>>(args.GetProperty("curves")),Frame(args.GetProperty("frame"),previous));path.Validate();return path;
    }
    private static RevolveAxisReference Axis(DocumentSnapshot doc,DefinitionId part,JsonElement args)
    {
        if(args.TryGetProperty("datumId",out _))
        {
            RequireFields(args,["datumId","revision"]);var axis=DatumConstructions.Axis(doc,part,RequiredGuid(args,"datumId"));
            if(axis.IsStale||axis.Revision!=RequiredGuid(args,"revision"))throw new ArgumentException("Axis datum is stale or changed.");
            return new(axis.Frame.Translation,axis.Frame.Rotation.Rotate(Vector3d.UnitZ),axis.Id,axis.Revision);
        }
        RequireFields(args,["point","direction"]);var result=new RevolveAxisReference(Vector(args,"point"),Vector(args,"direction"));result.Validate();return result;
    }
    private static RepetitionRecipe Layout(DocumentSnapshot doc,DefinitionId part,GeometryAssetRef source,JsonElement args,RepetitionRecipe? old)
    {
        RequireFields(args,["kind","frame","datumId","datumRevision","count","step","copyIds","output","includeSource","fullCircle","stylePolicy"]);
        var kind=EnumValue<RepetitionKind>(args,"kind");var count=Integer(args,"count",null,1,256);Guid? datum=null;Guid? revision=null;RigidTransform3d frame;
        if(args.TryGetProperty("datumId",out _))
        {
            if(args.TryGetProperty("frame",out _))throw new ArgumentException("Associated layouts cannot carry an independent frame cache.");
            datum=RequiredGuid(args,"datumId");revision=RequiredGuid(args,"datumRevision");
            if(DatumConstructions.Revision(doc.Datums,datum.Value)!=revision)throw new ArgumentException("Layout datum revision changed.");
            frame=DatumConstructions.InputFrame(doc,part,datum.Value,kind!=RepetitionKind.Mirror);
        }
        else
        {
            if(args.TryGetProperty("datumRevision",out _))throw new ArgumentException("Hidden layout datum revision.");
            frame=Frame(args.GetProperty("frame"),old?.Frame);
        }
        var ids=args.TryGetProperty("copyIds",out var c)?CadModelJson.Read<ImmutableArray<Guid>>(c):
            old is null?Enumerable.Range(0,count).Select(_=>Guid.NewGuid()).ToImmutableArray():old.Resize(count).CopyIds;
        var result=new RepetitionRecipe(source,kind,frame,count,Number(args,"step",0),ids,
            EnumValue<RepetitionOutput>(args,"output",RepetitionOutput.Compound),CadModelJson.Bool(args,"includeSource",true),CadModelJson.Bool(args,"fullCircle"),datum,revision,
            EnumValue<RepetitionStylePolicy>(args,"stylePolicy",old?.StylePolicy??RepetitionStylePolicy.VerifiedInheritance));
        result.Validate();return result;
    }
    private static LoftRecipe Loft(DocumentSnapshot doc,DefinitionId part,JsonElement args,LoftRecipe? old)
    {
        var stationArgs=CadModelJson.Array(args,"stations",2,128);var stations=stationArgs.Select((s,i)=>Frame(s,old is not null&&i<old.Stations.Length?old.Stations[i]:null)).ToImmutableArray();
        var channels=CadModelJson.Array(args,"channels",1,64).Select(c=>
        {
            RequireFields(c,["id","parent","sections"]);var id=c.TryGetProperty("id",out _)?RequiredGuid(c,"id"):old is null?Guid.NewGuid():throw new ArgumentException("Edited loft channels need explicit IDs.");
            var previous=old?.Channels.FirstOrDefault(x=>x.Id==id);var sections=CadModelJson.Array(c,"sections",stations.Length,stations.Length)
                .Select((s,i)=>Section(doc,part,s,previous is not null&&i<old!.Stations.Length?old.Stations[i]:null)).ToArray();
            if(sections.Where((s,i)=>s.Placement!=stations[i]).Any())throw new ArgumentException("Loft section frames must match their explicit ordered stations.");
            return new LoftChannel(id,c.TryGetProperty("parent",out var parent)&&parent.ValueKind!=JsonValueKind.Null?RequiredGuid(c,"parent"):null,sections.Select(s=>s.Profile).ToImmutableArray())
                {Sources=sections.Select(s=>s.Source).ToImmutableArray()};
        }).ToImmutableArray();
        return new(stations,channels,EnumValue<GuidedOutput>(args,"output",GuidedOutput.Solid),CadModelJson.Bool(args,"ruled"));
    }
}
