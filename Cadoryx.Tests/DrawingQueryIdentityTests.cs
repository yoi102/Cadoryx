using System.Text.Json;
using Cadoryx.AI.Contracts;
using Cadoryx.CommandLine;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;

namespace Cadoryx.Tests;

[Collection("ModelAgentNativeGates")]
public sealed class DrawingQueryIdentityTests
{
    [Theory]
    [InlineData("drawings")][InlineData("drawing_views")][InlineData("drawing_dimensions")]
    public async Task DocumentIdentityIsNotSilentlyAcceptedAsAnAbsentDrawingObject(string area)
    {
        await using var f=await Fixture.Create();
        var request=new Dictionary<string,object>{["area"]=area,["id"]=f.Session.Snapshot.Id.ToString()};
        if(area!="drawings")request["sheetId"]=f.Sheet.ToString();
        using var result=await f.Query(request);Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("not documentId",result.RootElement.GetProperty("message").GetString());
    }

    [Theory][InlineData("drawing_views")][InlineData("drawing_dimensions")]
    public async Task DocumentIdentityIsNotSilentlyAcceptedAsAMissingSheetOwner(string area)
    {
        await using var f=await Fixture.Create();
        using var result=await f.Query(new{area,sheetId=f.Session.Snapshot.Id.ToString()});
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("not documentId",result.RootElement.GetProperty("message").GetString());
        Assert.Contains("area=drawings",result.RootElement.GetProperty("message").GetString());
    }

    [Theory][InlineData("drawing_views")][InlineData("drawing_dimensions")]
    public async Task MissingOwnerExplainsTheReadOnlySheetDiscoveryRoute(string area)
    {
        await using var f=await Fixture.Create();using var result=await f.Query(new{area});
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("sheetId",result.RootElement.GetProperty("message").GetString());
        Assert.Contains("area=drawings",result.RootElement.GetProperty("message").GetString());
    }

    [Theory][InlineData("drawings")][InlineData("drawing_views")][InlineData("drawing_dimensions")]
    public async Task OrdinaryAbsentObjectStillReturnsAnHonestEmptyPage(string area)
    {
        await using var f=await Fixture.Create();
        var request=new Dictionary<string,object>{["area"]=area,["id"]=Guid.NewGuid().ToString()};
        if(area!="drawings")request["sheetId"]=f.Sheet.ToString();
        using var result=await f.Query(request);Assert.True(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(0,result.RootElement.GetProperty("total").GetInt32());Assert.Empty(result.RootElement.GetProperty("items").EnumerateArray());
    }

    [Theory][InlineData("sheet")][InlineData("view")][InlineData("dimension")]
    public async Task ActualTypedObjectsRemainQueryableWhenTheirGuidEqualsTheDocumentGuid(string role)
    {
        await using var f=await Fixture.Create(role);string area=role=="sheet"?"drawings":role=="view"?"drawing_views":"drawing_dimensions";
        var request=new Dictionary<string,object>{["area"]=area,["id"]=f.Session.Snapshot.Id.ToString()};
        if(role!="sheet")request["sheetId"]=f.Sheet.ToString();
        using var result=await f.Query(request);Assert.True(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(1,result.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(f.Session.Snapshot.Id.ToString(),result.RootElement.GetProperty("items")[0].GetProperty("id").GetString());
    }

    [Theory][InlineData("extrude")][InlineData("hole")]
    public async Task ModelContractKeepsItsExistingTypeNameId(string type)
    {
        await using var f=await Fixture.Create();using var result=await f.Query(new{area="model_contract",id=type});
        Assert.True(result.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task DiscoveryReturnsActualOwnerAndChildIdsWithoutSelectingOrChangingTheDocument()
    {
        await using var f=await Fixture.Create();using var sheets=await f.Query(new{area="drawings",limit=1});
        Assert.Equal(f.Sheet.ToString(),sheets.RootElement.GetProperty("items")[0].GetProperty("id").GetString());
        using var views=await f.Query(new{area="drawing_views",sheetId=f.Sheet.ToString(),limit=1});
        Assert.Equal(f.View.ToString(),views.RootElement.GetProperty("items")[0].GetProperty("id").GetString());
        using var dimensions=await f.Query(new{area="drawing_dimensions",sheetId=f.Sheet.ToString(),limit=1});
        Assert.Equal(f.Dimension.ToString(),dimensions.RootElement.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Empty(f.Document.Selection.Items);
    }

    private sealed class Fixture:IAsyncDisposable
    {
        internal MemoryAssetStore Assets{get;}=new();internal OcctGeometryKernel Kernel{get;}=new();
        internal CadDocumentSession Session{get;}internal CadDocumentViewModel Document{get;}internal CadAgentToolset Tools{get;}
        internal Guid Sheet{get;private set;}internal Guid View{get;private set;}internal Guid Dimension{get;private set;}
        private Fixture()
        {
            Session=new(DocumentSnapshot.Create("Query identities"),Assets,Kernel,new InlineSessionDispatcher());
            Document=new(Session,Kernel,new CadMessageLog());Tools=new(new CadCommandLineService(),Document);
        }
        internal static async Task<Fixture> Create(string? collision=null)
        {
            var f=new Fixture();
            try
            {
                await f.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,3,4,RigidTransform3d.Identity),"Box"));
                var doc=f.Session.Snapshot;var body=doc.Bodies.Values.Single();var path=doc.EnumerateOccurrences().Single(x=>x.DefinitionId==body.PartId).Path;
                var source=new DrawingSource(path,body.Id,body.Producer,body.Geometry.Revision,body.Geometry.AssetId,RigidTransform3d.Identity);
                f.View=collision=="view"?doc.Id.Value:Guid.NewGuid();f.Dimension=collision=="dimension"?doc.Id.Value:Guid.NewGuid();
                var view=new TechnicalDrawingView(f.View,"Front",DrawingViewKind.Front,null,new(100,90),1,[source],[],"Projection pending");
                // Query fixture only: stale native annotation is never treated as
                // manufacturing/topology proof and is not exported or refreshed.
                var datum=new AssemblyDatumReference(path,body.PartId,body.Id,body.Producer,body.Geometry.Revision,body.Geometry.AssetId,
                    new string('A',64),1,AssemblyDatumGeometry.PlaneFace,Vector3d.Zero,Vector3d.UnitZ,0);
                var dimension=new TechnicalDrawingDimension(f.Dimension,f.View,DrawingMeasureKind.Length,datum,datum,new(20,40),2,StaleReason:"Needs refresh");
                var sheet=TechnicalDrawingSheet.A4Landscape("Sheet") with{Views=[view],Dimensions=[dimension]};
                if(collision=="sheet")sheet=sheet with{Id=doc.Id.Value};f.Sheet=sheet.Id;
                await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));return f;
            }
            catch{await f.DisposeAsync();throw;}
        }
        internal async Task<JsonDocument> Query(object request)
        {
            var before=Session.Snapshot;var generation=Session.Generation;var count=Assets.Count;var undo=Session.CanUndo;
            var result=JsonDocument.Parse(await Tools.ExecuteAsync(new(Guid.NewGuid().ToString(),"cad_inspect",JsonSerializer.Serialize(request)),default));
            Assert.Same(before,Session.Snapshot);Assert.Equal(generation,Session.Generation);Assert.Equal(count,Assets.Count);Assert.Equal(undo,Session.CanUndo);
            return result;
        }
        public async ValueTask DisposeAsync(){Document.Detach();await Session.DisposeAsync();Assert.Equal(0,Assets.Count);}
    }
}
