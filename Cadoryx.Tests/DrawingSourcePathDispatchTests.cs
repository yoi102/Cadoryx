using System.Text.Json;
using System.Text.Json.Nodes;
using Cadoryx.CommandLine;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;

namespace Cadoryx.Tests;

public sealed class DrawingSourcePathDispatchTests
{
    [Fact]
    public async Task ValidFullPathReachesOriginalInspectorAndDoesNotConvertItsBodyFailureIntoPathGuidance()
    {
        await using var f=new Fixture();var before=f.Session.Snapshot;var request=f.Request();
        using var rejected=await f.Query(request);var r=rejected.RootElement;
        Assert.False(r.GetProperty("success").GetBoolean());Assert.Equal(RejectingTopologyKernel.BodyFailure,r.GetProperty("message").GetString());
        Assert.False(r.TryGetProperty("sourceReads",out _));Assert.False(r.TryGetProperty("rejection",out _));
        Assert.Equal(1,f.Kernel.TopologyReads);Assert.Same(before,f.Kernel.ReceivedSnapshot);Assert.Equal(f.Path,f.Kernel.ReceivedPath);
        Assert.Equal(request["bodyId"]!.GetValue<string>(),f.Kernel.ReceivedBody.ToString());Assert.Equal(request["revision"]!.GetValue<string>(),f.Kernel.ReceivedRevision.ToString());
        f.Unchanged(before);
    }
    [Theory][InlineData("bodyId")][InlineData("revision")]
    public async Task ValidFullPathKeepsBodyAndRevisionGuardsBeforeInspectorWithoutInventingReplacementSources(string identity)
    {
        await using var f=new Fixture();var before=f.Session.Snapshot;var request=f.Request();request[identity]=Guid.Empty.ToString("D");
        using var rejected=await f.Query(request);Assert.False(rejected.RootElement.GetProperty("success").GetBoolean());
        Assert.False(rejected.RootElement.TryGetProperty("sourceReads",out _));Assert.Equal(0,f.Kernel.TopologyReads);f.Unchanged(before);
    }
    private sealed class Fixture:IAsyncDisposable
    {
        internal RejectingTopologyKernel Kernel{get;}=new();internal MemoryAssetStore Assets{get;}=new();internal CadDocumentSession Session{get;}internal OccurrencePath Path{get;}
        private CadDocumentViewModel Document{get;}private CadAgentToolset Tools{get;}
        internal Fixture()
        {
            var snapshot=DocumentSnapshot.Create("Exact dispatch");var part=DefinitionId.New();var slot=ComponentSlotId.New();var root=(AssemblyDefinition)snapshot.Definitions[snapshot.RootAssemblyId];
            snapshot=snapshot with{Definitions=snapshot.Definitions.Add(part,new PartDefinition(part,"Empty part",[],[])).SetItem(root.Id,root with{Children=[new ComponentSlot(slot,part,"Exact instance",RigidTransform3d.Identity)]})};
            snapshot.Validate();Path=new OccurrencePath(snapshot.Id,[slot]);Session=new(snapshot,Assets,Kernel,new InlineSessionDispatcher());Document=new(Session,Kernel,new CadMessageLog());Tools=new(new CadCommandLineService(),Document);
        }
        internal JsonObject Request()=>new(){["action"]="inspect_sources",["documentId"]=Session.Snapshot.Id.ToString(),["stateId"]=Session.Snapshot.StateId.ToString(),["path"]=Path.ToString(),["bodyId"]=Guid.NewGuid().ToString("D"),["revision"]=Guid.NewGuid().ToString("D")};
        internal async Task<JsonDocument> Query(JsonObject request)=>JsonDocument.Parse(await Tools.ExecuteAsync(new("exact","cad_drawing",request.ToJsonString()),default));
        internal void Unchanged(DocumentSnapshot before){Assert.Same(before,Session.Snapshot);Assert.False(Session.CanUndo);Assert.False(Session.CanRedo);Assert.Equal(0,Assets.Count);Assert.Equal(0,Kernel.GeometryCalls);}
        public async ValueTask DisposeAsync(){Document.Detach();await Session.DisposeAsync();Assert.Equal(0,Assets.Count);Assert.Equal(0,Kernel.GeometryCalls);}
    }
    private sealed class RejectingTopologyKernel:IGeometryKernel,IDrawingTopologyResolver
    {
        internal const string BodyFailure="Original inspector: exact source body is missing.";
        internal int TopologyReads{get;private set;}internal int GeometryCalls{get;private set;}internal DocumentSnapshot? ReceivedSnapshot{get;private set;}internal OccurrencePath? ReceivedPath{get;private set;}internal BodyId ReceivedBody{get;private set;}internal GeometryRevisionId ReceivedRevision{get;private set;}
        public string Version=>"Managed dispatch fixture";
        public Task<DrawingTopologyPage> InspectDrawingTopologyAsync(DocumentSnapshot document,OccurrencePath path,BodyId body,GeometryRevisionId revision,IAssetStore assets,int offset,int limit,CancellationToken token=default)
        {TopologyReads++;ReceivedSnapshot=document;ReceivedPath=path;ReceivedBody=body;ReceivedRevision=revision;throw new ArgumentException(BodyFailure);}
        public Task<DrawingTopologyReference> ResolveDrawingTopologyAsync(DocumentSnapshot document,OccurrencePath path,BodyId body,int index,string fingerprint,IAssetStore assets,CancellationToken token=default)=>throw Unexpected();
        private Exception Unexpected(){GeometryCalls++;return new InvalidOperationException("Managed read rejection must not change geometry.");}
        public bool Supports(GeometryRecipe recipe)=>throw Unexpected();
        public Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken token=default)=>throw Unexpected();
        public Task<LoadedDocument> ImportAsync(string path,IAssetStore assets,CancellationToken token=default)=>throw Unexpected();
        public Task ExportAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CancellationToken token=default)=>throw Unexpected();
        public Task<CadExportReport> ExportAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CadExportOptions options,CancellationToken token=default)=>throw Unexpected();
    }
}
