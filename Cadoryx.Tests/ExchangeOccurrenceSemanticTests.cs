using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Cadoryx.Db;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Xunit;

namespace Cadoryx.Tests;

// Metadata/report fixtures only; these tests do not execute a CAD reader or prove geometry fidelity.
public sealed class ExchangeOccurrenceSemanticTests
{
    [Fact]
    public void EqualReuseCountsCannotHideAChangedOccurrenceSharingPartition()
    {
        var first=Model([0,1,0,1]);var second=Model([0,0,1,1]);
        var rows=ExchangeMeasuredReport.Compare(first,second,"STEP",new());
        Assert.Equal("Same",At(rows,"PartDefinitionSharing").Status);
        Assert.Equal("Same",At(rows,"HierarchyAndNames").Status);
        Assert.Equal("Different",At(rows,"OccurrenceDefinitionSharing").Status);
    }

    [Theory,InlineData(false),InlineData(true)]
    public void ScalarBoundsAndMassCannotHideAChangedInstancePlacement(bool rotation)
    {
        var first=Model([0,1,0,1]);var changed=first.Occurrences[2] with{WorldTransform=rotation
            ?new(Vector3d.Zero,Quaterniond.FromAxisAngle(Vector3d.UnitZ,.25))
            :new(new(.01,0,0),Quaterniond.Identity)};
        var second=first with{Occurrences=first.Occurrences.SetItem(2,changed)};
        var rows=ExchangeMeasuredReport.Compare(first,second,"STEP",new());
        Assert.Equal("WithinTolerance",At(rows,"WorldBoundsMm").Status);
        Assert.Equal("WithinTolerance",At(rows,"SolidVolumeMm3").Status);
        Assert.Equal("Different",At(rows,rotation?"OccurrenceOrientations":"OccurrenceTranslationsMm").Status);
    }

    [Fact]
    public void QuaternionSignIsTheSameObservedOrientation()
    {
        var first=Model([0,1,0,1]);var q=Quaterniond.FromAxisAngle(new(0,1,0),.6);
        first=first with{Occurrences=first.Occurrences.SetItem(0,first.Occurrences[0] with{WorldTransform=new(new(1,2,3),q)})};
        var second=first with{Occurrences=first.Occurrences.SetItem(0,first.Occurrences[0] with{WorldTransform=new(new(1,2,3),new(-q.X,-q.Y,-q.Z,-q.W))})};
        var row=At(ExchangeMeasuredReport.Compare(first,second,"STEP",new()),"OccurrenceOrientations");
        Assert.Equal("WithinTolerance",row.Status);Assert.Equal(0,row.MaximumDifference);
    }

    [Theory,InlineData("name"),InlineData("depth"),InlineData("kind"),InlineData("missing")]
    public void AChangedHierarchyCannotProduceGuessedPlacementOrSharingCorrespondence(string fault)
    {
        var first=Model([0,1,0,1]);var item=first.Occurrences[1];
        var second=first with{Occurrences=fault=="missing"?first.Occurrences.RemoveAt(1):first.Occurrences.SetItem(1,fault switch
        {"name"=>item with{Name="other"},"depth"=>item with{Depth=2},_=>item with{Assembly=true,BodyCount=0}})};
        var rows=ExchangeMeasuredReport.Compare(first,second,"STEP",new());
        foreach(var aspect in new[]{"OccurrenceDefinitionSharing","OccurrenceTranslationsMm","OccurrenceOrientations"})
            Assert.Equal("NotComparable",At(rows,aspect).Status);
        Assert.Equal("NotMeasured",At(rows,"PMI").Status);
    }

    [Theory,InlineData(1),InlineData(2)]
    public async Task OldAndNewArchivesVerifyWithTheirOwnComparisonContract(int schema)
    {
        var model=Model([0,1,0,1]);var rows=ExchangeMeasuredReport.Compare(model,model,"STEP",new());
        if(schema==1)rows=rows.Where(r=>!r.Aspect.StartsWith("Occurrence",StringComparison.Ordinal)).ToImmutableArray();
        string folder=Path.Combine(Path.GetTempPath(),"CadoryxSemanticReport-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try
        {
            string file=Path.Combine(folder,"report.zip");byte[] payload="hash-only report fixture; no geometry claim"u8.ToArray();
            var options=new DeliveryOptions(DeliveryOutput.ExchangeAudit);
            var format=new ExchangeMeasuredFormat("STEP","export.step",payload.Length,Convert.ToHexStringLower(SHA256.HashData(payload)),model,[],[],rows);
            var report=new ExchangeMeasuredReport(schema,Guid.NewGuid().ToString(),Guid.NewGuid().ToString(),DateTimeOffset.UtcNow,
                "metadata fixture",ExchangeMeasuredReport.LocalBasis,options,[new string('a',64)],model,[format],["External CAD not run"]);
            using(var archive=ZipFile.Open(file,ZipArchiveMode.Create))
            {
                using(var output=archive.CreateEntry("export.step").Open())output.Write(payload);
                using var json=new StreamWriter(archive.CreateEntry("report.json").Open());json.Write(JsonSerializer.Serialize(report,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));
            }
            Assert.Equal(schema,(await ExchangeMeasurementService.VerifyAsync(file)).SchemaVersion);
            // An added/dropped semantic row cannot be disguised as either report version.
            using(var archive=ZipFile.Open(file,ZipArchiveMode.Update))
            {
                var entry=archive.GetEntry("report.json")!;using var input=new StreamReader(entry.Open());var text=await input.ReadToEndAsync();input.Dispose();entry.Delete();
                var node=System.Text.Json.Nodes.JsonNode.Parse(text)!;node["schemaVersion"]=schema==1?2:1;
                using var output=new StreamWriter(archive.CreateEntry("report.json").Open());output.Write(node.ToJsonString());
            }
            await Assert.ThrowsAsync<InvalidDataException>(()=>ExchangeMeasurementService.VerifyAsync(file));
        }
        finally{Directory.Delete(folder,true);}
    }

    private static ExchangeComparison At(ImmutableArray<ExchangeComparison> rows,string aspect)=>Assert.Single(rows,r=>r.Aspect==aspect);
    private static ExchangeModelObservation Model(int[] groups)=>new(4,2,[2,2],new(Vector3d.Zero,new(1,1,1)),24,4,4,0,24,48,
        [0xff86acc5u],[],[0xff86acc5u],[..groups.Select((group,index)=>new ExchangeOccurrenceObservation(index,1,"instance"+index,"Part",false,group,RigidTransform3d.Identity,1))]);
}
