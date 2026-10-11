using System.Collections.Immutable;
using System.Text.Json;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using static Cadoryx.ViewModels.Toolboxes.CadAgentParameterTools;
using static Cadoryx.ViewModels.Toolboxes.CadDatumAuthoring;

namespace Cadoryx.ViewModels.Toolboxes;

internal static partial class CadModelAuthoring
{
    private static async Task<string> ApplyAssemblyAsync(CadDocumentViewModel document,DocumentCommandExpectation expected,JsonElement args,string action,CancellationToken token)
    {
        var before=document.Session.Snapshot;
        Guid id=args.TryGetProperty("id",out _)?RequiredGuid(args,"id"):action=="set_assembly"?Guid.NewGuid():throw new ArgumentException("Exact assembly pattern ID is required.");
        await document.Session.ExecuteAsync(new AssemblyModelCommand(args.Clone(),action,id),token,expected);var after=document.Session.Snapshot;
        var pattern=after.AssemblyPatterns.GetValueOrDefault(id);
        var oldSlots=before.Definitions.Values.OfType<AssemblyDefinition>().SelectMany(a=>a.Children).Select(s=>s.Id).ToHashSet();
        return JsonSerializer.Serialize(new{success=true,action,id,changed=after.StateId!=before.StateId,documentId=after.Id.ToString(),stateId=after.StateId.ToString(),
            createdPatternIds=after.AssemblyPatterns.Keys.Except(before.AssemblyPatterns.Keys).ToArray(),
            createdDefinitionIds=after.Definitions.Keys.Except(before.Definitions.Keys).Select(d=>d.ToString()).ToArray(),
            createdFeatureIds=after.Features.Keys.Except(before.Features.Keys).Select(f=>f.ToString()).ToArray(),
            createdBodyIds=after.Bodies.Keys.Except(before.Bodies.Keys).Select(b=>b.ToString()).ToArray(),
            createdSlotIds=after.Definitions.Values.OfType<AssemblyDefinition>().SelectMany(a=>a.Children).Select(s=>s.Id).Except(oldSlots).Select(s=>s.ToString()).ToArray(),
            copySlots=pattern?.CopySlots.Select(s=>s.ToString()).ToArray()??[],coordinateSpace="parent_local"},CadAuthoringInspection.JsonOptions);
    }
    private sealed class AssemblyModelCommand(JsonElement args,string action,Guid id):ICadDocumentCommand
    {
        public string Name=>"Assembly model "+action;
        public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
        {
            var doc=context.Snapshot;doc.AssemblyPatterns.TryGetValue(id,out var old);
            if(args.TryGetProperty("id",out _)&&old is null)throw new ArgumentException("Exact assembly pattern is missing.");
            if(action=="detach_assembly")return await AssemblyRepetitionCommands.Detach(id).PrepareAsync(context,token);
            var pathText=RequiredString(args,"path",8192);var occurrence=doc.EnumerateOccurrences().SingleOrDefault(o=>o.Path.ToString()==pathText)??throw new ArgumentException("Exact source occurrence path is missing.");
            var placement=OccurrencePlacement.Resolve(doc,occurrence.Path);
            if(!placement.CanMoveIndependently)throw new ArgumentException("Select a source under an independent parent assembly.");
            if(old is not null&&(old.Owner!=placement.OwnerId||old.Source!=placement.Slot.Id))throw new ArgumentException("An existing assembly pattern must retain its exact owner/source occurrence.");
            using var parsed=JsonDocument.Parse(RequiredString(args,"authorJson",32768));var author=parsed.RootElement;
            RequireFields(author,["type","kind","frame","datumId","datumRevision","count","step","copySlots","fullCircle"]);
            if(RequiredString(author,"type",32)!="assembly_pattern")throw new ArgumentException("Expected an assembly_pattern author.");
            var kind=EnumValue<RepetitionKind>(author,"kind");var count=Integer(author,"count",null,2,256);Guid? datum=null;Guid? revision=null;RigidTransform3d frame;
            if(author.TryGetProperty("datumId",out _))
            {
                if(author.TryGetProperty("frame",out _))throw new ArgumentException("Associated assembly layout cannot carry a frame cache.");
                datum=RequiredGuid(author,"datumId");revision=RequiredGuid(author,"datumRevision");
                if(DatumConstructions.Revision(doc.Datums,datum.Value)!=revision)throw new ArgumentException("Assembly layout datum revision changed.");
                frame=placement.Slot.LocalTransform*DatumConstructions.InputFrame(doc,placement.Slot.DefinitionId,datum.Value,kind!=RepetitionKind.Mirror);
            }
            else
            {
                if(author.TryGetProperty("datumRevision",out _))throw new ArgumentException("Hidden assembly datum revision.");
                frame=Frame(author.GetProperty("frame"),old?.Frame);
            }
            if(old?.Datum is not null&&old.Datum!=datum)throw new ArgumentException("Detach the assembly layout before replacing its associated datum.");
            var slots=author.TryGetProperty("copySlots",out var copies)?CadModelJson.Read<ImmutableArray<ComponentSlotId>>(copies):
                old is null?Enumerable.Range(0,count-1).Select(_=>ComponentSlotId.New()).ToImmutableArray():old.Resize(count).CopySlots;
            var pattern=new AssemblyRepetition(id,placement.OwnerId,placement.Slot.Id,RequiredString(args,"name",64),kind,frame,count,Number(author,"step",0),slots,
                CadModelJson.Bool(author,"fullCircle"),datum,revision){Mirrors=old?.Mirrors??[]};pattern.Validate();
            if(old is not null&&JsonSerializer.Serialize(AssemblyAuthor(old),CadAuthoringInspection.JsonOptions)==JsonSerializer.Serialize(AssemblyAuthor(pattern),CadAuthoringInspection.JsonOptions)&&old.Name==pattern.Name)
                return new(doc);
            return await AssemblyRepetitionCommands.Set(occurrence.Path,pattern).PrepareAsync(context,token);
        }
    }
    private static Dictionary<string,object> AssemblyAuthor(AssemblyRepetition pattern)
    {
        var result=new Dictionary<string,object>{["type"]="assembly_pattern",["kind"]=pattern.Kind.ToString(),["count"]=pattern.Count,["step"]=pattern.Step,
            ["copySlots"]=pattern.CopySlots.Select(s=>s.ToString()).ToArray(),["fullCircle"]=pattern.FullCircle};
        if(pattern.Datum is {} datum){result["datumId"]=datum;result["datumRevision"]=pattern.DatumRevision!.Value;}else result["frame"]=CadDatumJson.Frame(pattern.Frame);return result;
    }
    internal static object InspectAssembly(DocumentSnapshot doc,AssemblyRepetition p)
    {
        var sources=doc.EnumerateOccurrences().Where(o=>!o.Path.Slots.IsEmpty&&OccurrencePlacement.Resolve(doc,o.Path) is {} placement&&placement.OwnerId==p.Owner&&placement.Slot.Id==p.Source).ToArray();
        var source=sources.Length==1?sources[0]:null;
        var author=AssemblyAuthor(p);
        return new{p.Id,p.Name,ownerId=p.Owner.ToString(),sourceSlot=p.Source.ToString(),path=source?.Path.ToString(),paths=sources.Select(o=>o.Path.ToString()).ToArray(),coordinateSpace="parent_local",author,
            mirroredDefinitions=p.Mirrors.Select(m=>new{source=m.Source.ToString(),target=m.Target.ToString(),bodyIds=m.Bodies.Select(b=>b.Target.ToString()).ToArray(),slotIds=m.Slots.Select(s=>s.Target.ToString()).ToArray()}).ToArray(),
            edit=source is null?null:new{action="set_assembly",documentId=doc.Id.ToString(),stateId=doc.StateId.ToString(),id=p.Id,path=source.Path.ToString(),name=p.Name,
                authorJson=JsonSerializer.Serialize(author,CadAuthoringInspection.JsonOptions)}};
    }
}
