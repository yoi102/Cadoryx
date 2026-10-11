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
    private static async Task<Authored> ParseContoursAsync(DocumentCommandContext context,DefinitionId part,JsonElement args,FeatureDefinition? old,CancellationToken token,bool requireIds=false)
    {
        RequireFields(args,["type","input","operation","programs"]);
        if(RequiredString(args,"type",32)!="contour_local")throw new ArgumentException("Contour inspection requires contour_local author input.");
        var doc=context.Snapshot;var input=CadModelJson.Input(doc,part,args.GetProperty("input"));var operation=EnumValue<LocalFeatureOperation>(args,"operation");
        if(old?.Recipe is ContourLocalRecipe previous&&previous.Operation!=operation)throw new ArgumentException("Changing contour operation requires a new feature.");
        var resolver=context.Kernel as IContourLocalResolver??throw new NotSupportedException("Kernel has no exact contour resolver.");
        var programs=ImmutableArray.CreateBuilder<ContourLocalProgram>();
        foreach(var value in CadModelJson.Array(args,"programs",1,256))
        {
            RequireFields(value,["id","seed","radius","supportFace","distance","secondDistance"]);
            var id=value.TryGetProperty("id",out _)?RequiredGuid(value,"id"):old is null&&!requireIds?Guid.NewGuid():throw new ArgumentException("Edited and inspected contour programs require explicit stable IDs.");
            var seed=CadModelJson.Selection(doc,part,value.GetProperty("seed"),HistoryShapeKind.Edge);
            if(seed.FeatureId!=input.Id)throw new ArgumentException("All contour seeds must belong to the exact input.");
            ExactTopologySelection? support=null;ContourRadiusLaw? radius=null;double? distance=null,second=null;
            if(operation==LocalFeatureOperation.Fillet)
            {
                if(value.TryGetProperty("supportFace",out _)||value.TryGetProperty("distance",out _)||value.TryGetProperty("secondDistance",out _))throw new ArgumentException("Fillet forbids chamfer companions.");
                radius=ReadContourAuthorLaw(value.GetProperty("radius"));
            }
            else
            {
                if(value.TryGetProperty("radius",out _))throw new ArgumentException("Chamfer forbids a radius law.");
                support=CadModelJson.Selection(doc,part,value.GetProperty("supportFace"));distance=Number(value,"distance");
                if(value.TryGetProperty("secondDistance",out _))second=Number(value,"secondDistance");
            }
            var chain=await resolver.DiscoverContourAsync(doc,seed,operation,support,context.Assets,token);
            programs.Add(new(id,seed,chain,radius,support,distance,second));
        }
        var recipe=new ContourLocalRecipe(input.Result,operation,programs.ToImmutable());recipe.Validate();return new(recipe,[input.Id]);
    }
    private static ContourRadiusLaw ReadContourAuthorLaw(JsonElement value)
    {
        var kind=RequiredString(value,"kind",16);var fields=kind switch
        {
            "constant"=>new[]{"value"},"linear"=>["first","last"],"smooth"=>["first","last","firstDerivative","lastDerivative"],
            "sampled"=>["samples"],"spline"=>["poles","knots","multiplicities","degree"],_=>throw new ArgumentException("Unknown contour law kind.")
        };
        RequireFields(value,["kind",..fields]);
        ContourRadiusLaw law=kind switch
        {
            "constant"=>new ConstantContourRadius(Number(value,"value")),"linear"=>new LinearContourRadius(Number(value,"first"),Number(value,"last")),
            "smooth"=>new SmoothContourRadius(Number(value,"first"),Number(value,"last"),Number(value,"firstDerivative",0),Number(value,"lastDerivative",0)),
            "sampled"=>new SampledContourRadius(CadModelJson.Read<ImmutableArray<ContourRadiusSample>>(value.GetProperty("samples"))),
            _=>new SplineContourRadius(CadModelJson.Read<ImmutableArray<ContourRadiusPole>>(value.GetProperty("poles")),
                CadModelJson.Read<ImmutableArray<double>>(value.GetProperty("knots")),CadModelJson.Read<ImmutableArray<int>>(value.GetProperty("multiplicities")),Integer(value,"degree",null,1,8))
        };
        law.Validate();return law;
    }
    private static object ContourAuthor(ContourLocalProgram p)
    {
        var result=new Dictionary<string,object>{["id"]=p.Id,["seed"]=CadDatumJson.Selection(p.Seed)};
        if(p.Radius is {} law)result["radius"]=CadModelJson.Write<ContourRadiusLaw>(law);
        if(p.SupportFace is {} face)result["supportFace"]=CadDatumJson.Selection(face);
        if(p.Distance is {} distance)result["distance"]=distance;if(p.SecondDistance is {} second)result["secondDistance"]=second;
        return result;
    }
    private static async Task<string> InspectContoursAsync(CadDocumentViewModel document,DocumentCommandExpectation expected,JsonElement args,CancellationToken token)
    {
        using var capture=document.Session.Capture();var snapshot=capture.Snapshot;expected.Validate(snapshot);
        var offset=Integer(args,"offset",0,0,100000);var limit=Integer(args,"limit",20,1,50);var part=new DefinitionId(RequiredGuid(args,"partId"));
        using var author=JsonDocument.Parse(RequiredString(args,"authorJson",32768));
        var parsed=await ParseContoursAsync(new(snapshot,document.Session.Generation,document.Session.Assets,document.GeometryKernel),part,author.RootElement,null,token,true);
        var recipe=(ContourLocalRecipe)parsed.Recipe;var edges=recipe.Programs.SelectMany(p=>p.Chain.Members.Select(m=>new{programId=p.Id,member=m})).ToArray();
        var rows=edges.Skip(offset).Take(limit).ToList();
        string Serialize()=>JsonSerializer.Serialize(new{success=true,action="inspect_contours",documentId=snapshot.Id.ToString(),stateId=snapshot.StateId.ToString(),
            input=CadModelJson.Input(snapshot.Features[recipe.Programs[0].Seed.FeatureId]),operation=recipe.Operation,offset,limit,total=edges.Length,returned=rows.Count,
            nextOffset=offset+rows.Count,hasMore=(long)offset+rows.Count<edges.Length,
            contours=recipe.Programs.Where(p=>rows.Any(r=>r.programId==p.Id)).Select(p=>new{p.Id,seedIndex=p.Seed.FullTopologyIndex,supportFaceIndex=p.SupportFace?.FullTopologyIndex,
                edgeCount=p.Chain.Members.Length,p.Chain.FirstVertex,p.Chain.LastVertex,p.Chain.IsClosed,p.Chain.IsClosedAndTangent,p.Chain.Length}),items=rows,
            contract="Read-only native discovery. Author input supplies exact seeds and laws; no chain caches are accepted. Parameter fields retain program/sample/pole IDs. Use explicit reselect after source changes."},CadAuthoringInspection.JsonOptions);
        string json=Serialize();while(json.Length>5500&&rows.Count>1){rows.RemoveAt(rows.Count-1);json=Serialize();}
        if(json.Length>5500)throw new NotSupportedException("Contour inspection exceeds its page budget.");token.ThrowIfCancellationRequested();expected.Validate(document.Session.Snapshot);return json;
    }
}
