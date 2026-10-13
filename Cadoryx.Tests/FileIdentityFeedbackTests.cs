using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cadoryx.CommandLine;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;

namespace Cadoryx.Tests;

[Collection("ModelAgentNativeGates")]
public sealed class FileIdentityFeedbackTests
{
    [Theory]
    [InlineData("documentId","missing")][InlineData("documentId","null")][InlineData("documentId","empty")][InlineData("documentId","numeric")][InlineData("documentId","malformed")]
    [InlineData("stateId","missing")][InlineData("stateId","null")][InlineData("stateId","empty")][InlineData("stateId","numeric")][InlineData("stateId","malformed")]
    public async Task MissingIdentityReportsAReadOnlySourceWithoutWritingAndTheCallerMustExplicitlyRetry(string field,string fault)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage();
        var writer=new CountingFiles(new DocumentFileService(storage,kernel,new DocumentDeliveryService(storage,kernel)));
        string path=files.PathFor("identity-save.cadoryx");await File.WriteAllTextAsync(path,"ORIGINAL");
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Identity feedback"),assets,kernel,new InlineSessionDispatcher()))
        {
            var document=new CadDocumentViewModel(session,kernel,new CadMessageLog(),storage,fileService:writer);
            var tools=new CadAgentToolset(new CadCommandLineService(),document);
            try
            {
                await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(4,3,2,RigidTransform3d.Identity),"Box"));
                var before=session.Snapshot;var generation=session.Generation;
                var request=new JsonObject{["action"]="save",["documentId"]=before.Id.ToString(),["stateId"]=before.StateId.ToString(),["path"]=path,["overwrite"]=true};
                switch(fault)
                {
                    case "missing":request.Remove(field);break;case "null":request[field]=null;break;
                    case "empty":request[field]=Guid.Empty.ToString();break;case "numeric":request[field]=123;break;
                    case "malformed":request[field]="not-a-guid";break;
                }
                string rejected=request.ToJsonString();using var result=JsonDocument.Parse(await tools.ExecuteAsync(new("missing-file-id","cad_file",rejected),default));
                var root=result.RootElement;Assert.False(root.GetProperty("success").GetBoolean());
                Assert.Equal("Missing or invalid "+field+".",root.GetProperty("message").GetString());
                Assert.Equal(field,Assert.Single(root.GetProperty("missingOrInvalid").EnumerateArray()).GetString());
                var source=root.GetProperty("identitySource");Assert.Equal("cad_status",source.GetProperty("toolName").GetString());
                Assert.Empty(source.GetProperty("arguments").EnumerateObject());Assert.False(root.TryGetProperty("documentId",out _));Assert.False(root.TryGetProperty("stateId",out _));
                Assert.False(root.TryGetProperty("nextRequest",out _));Assert.Equal(0,writer.Calls);Assert.Equal("ORIGINAL",await File.ReadAllTextAsync(path));
                Assert.Same(before,session.Snapshot);Assert.Equal(generation,session.Generation);Assert.True(session.IsDirty);Assert.Equal(rejected,request.ToJsonString());
                using var status=JsonDocument.Parse(await tools.ExecuteAsync(new("identity-read",source.GetProperty("toolName").GetString()!,source.GetProperty("arguments").GetRawText()),default));
                // The caller explicitly chooses to retry after reading real current
                // identities; feedback did not supply a filled save mutation.
                request["documentId"]=status.RootElement.GetProperty("documentId").GetString();request["stateId"]=status.RootElement.GetProperty("stateId").GetString();
                using var saved=JsonDocument.Parse(await tools.ExecuteAsync(new("explicit-retry","cad_file",request.ToJsonString()),default));
                Assert.True(saved.RootElement.GetProperty("success").GetBoolean(),saved.RootElement.GetRawText());Assert.Equal(1,writer.Calls);
                var receipt=saved.RootElement.GetProperty("receipt");
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))),receipt.GetProperty("sha256").GetString());
                Assert.Equal(new FileInfo(path).Length,receipt.GetProperty("bytes").GetInt64());Assert.Equal(before.StateId.ToString(),receipt.GetProperty("stateId").GetString());
                Assert.Same(before,session.Snapshot);Assert.False(session.IsDirty);
                var fresh=new MemoryAssetStore();
                using(var reopened=await storage.LoadAsync(path,fresh))
                {Assert.Equal(before.Id,reopened.Snapshot.Id);Assert.Equal(before.StateId,reopened.Snapshot.StateId);Assert.InRange(Math.Abs(Assert.Single(reopened.Snapshot.Bodies.Values).Geometry.VolumeMm3-24),0,1e-7);}
                Assert.Equal(0,fresh.Count);
            }
            finally{document.Detach();}
        }
        Assert.Equal(0,assets.Count);
    }

    [Theory][InlineData("documentId")][InlineData("stateId")]
    public async Task WellFormedForeignOrStaleIdentityStillRejectsWithoutAnAutomaticRepair(string field)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage();
        var writer=new CountingFiles(new DocumentFileService(storage,kernel,new DocumentDeliveryService(storage,kernel)));
        string path=files.PathFor("retained-file.cadoryx");await File.WriteAllTextAsync(path,"ORIGINAL");
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Stale save feedback"),assets,kernel,new InlineSessionDispatcher()))
        {
            var document=new CadDocumentViewModel(session,kernel,new CadMessageLog(),storage,fileService:writer);var tools=new CadAgentToolset(new CadCommandLineService(),document);
            try
            {
                await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(4,3,2,RigidTransform3d.Identity),"Box"));var previous=session.Snapshot;
                await session.ExecuteAsync(new EditDocumentCommand("Rename",snapshot=>snapshot with{Name="Changed"}));var before=session.Snapshot;
                var request=new JsonObject{["action"]="save",["documentId"]=before.Id.ToString(),["stateId"]=before.StateId.ToString(),["path"]=path,["overwrite"]=true};
                request[field]=field=="documentId"?DocumentSnapshot.Create("Other document").Id.ToString():previous.StateId.ToString();
                string raw=request.ToJsonString();using var result=JsonDocument.Parse(await tools.ExecuteAsync(new("stale-file","cad_file",raw),default));
                Assert.False(result.RootElement.GetProperty("success").GetBoolean());Assert.False(result.RootElement.TryGetProperty("missingOrInvalid",out _));
                Assert.Equal(0,writer.Calls);Assert.Equal("ORIGINAL",await File.ReadAllTextAsync(path));Assert.Equal(raw,request.ToJsonString());
                Assert.Same(before,session.Snapshot);Assert.True(session.IsDirty);Assert.Equal(1,assets.Count);
            }
            finally{document.Detach();}
        }
        Assert.Equal(0,assets.Count);
    }

    private sealed class CountingFiles(IDocumentFileService actual):IDocumentFileService
    {
        public int Calls{get;private set;}
        public Task<DocumentFileReceipt> WriteAsync(DocumentSnapshot snapshot,IAssetStore assets,DocumentFileRequest request,CancellationToken token=default)
        {Calls++;return actual.WriteAsync(snapshot,assets,request,token);}
    }
}
