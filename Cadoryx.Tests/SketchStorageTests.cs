using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Cadoryx.Db;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Sketching;
using MessagePack;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchStorageTests
{
    [Fact] public async Task EveryConstraintTypeAndConstructionFlagRoundtripsWithoutSolving()
    {
        using var files=new TestFiles();var (doc,part)=SketchCommandTests.Document();var s=SketchTestData.Rectangle(part);
        var p=s.Points;var l=s.Lines;var a=new SketchCircle(SketchEntityId.New(),p[0].Id,5,true);var b=new SketchCircle(SketchEntityId.New(),p[1].Id,7);
        s=s with{Points=p.SetItem(0,p[0] with{IsConstruction=true}),Circles=[a,b],Constraints=[
            ..s.Constraints,new CoincidentConstraint(SketchConstraintId.New(),p[0].Id,p[2].Id,false),new DistanceConstraint(SketchConstraintId.New(),p[0].Id,p[2].Id,12,false),
            new LengthConstraint(SketchConstraintId.New(),l[0].Id,40,false),new ParallelConstraint(SketchConstraintId.New(),l[0].Id,l[2].Id,false),
            new PerpendicularConstraint(SketchConstraintId.New(),l[0].Id,l[1].Id,false),new EqualLengthConstraint(SketchConstraintId.New(),l[0].Id,l[2].Id,false),
            new RadiusConstraint(SketchConstraintId.New(),a.Id,5,false),new EqualRadiusConstraint(SketchConstraintId.New(),a.Id,b.Id,false)]};
        doc=doc with{Sketches=doc.Sketches.Add(s.Id,s)};var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();string path=files.PathFor("sketch.cadoryx");
        await storage.SaveAsync(doc,assets,path);using(var loaded=await storage.LoadAsync(path,assets))
        {
            Assert.Equal(doc.Id,loaded.Snapshot.Id);Assert.Equal(doc.StateId,loaded.Snapshot.StateId);Equal(s,loaded.Snapshot.Sketches[s.Id]);
            var manifest=FormatEvolutionTests.Manifest(path);Assert.Equal(CadSectionMigrationRegistry.CurrentFormats.Count,manifest.Sections.Length);Assert.Contains("cadoryx.sketches.1",manifest.RequiredCapabilities);
            Assert.Equal(4,manifest.Sections.Single(e=>e.Kind=="document").SchemaVersion);Assert.Equal(2,manifest.Sections.Single(e=>e.Kind=="sketches").SchemaVersion);
            Assert.Empty(loaded.Diagnostics);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task FrozenFiveSectionFileGainsAnEmptyRegistryWithoutChangingItsGeometry()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();string legacy=Path.Combine(AppContext.BaseDirectory,"Fixtures","Storage","m3v-colored.cadoryx");
        Assert.Equal("d8913e5fef1e4e55da67690bdb8741d6377dcafe7fcee4a4625e49e5aed9228d",Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(legacy))));
        using(var loaded=await storage.LoadAsync(legacy,assets))
        {
            Assert.Empty(loaded.Snapshot.Sketches);Assert.Contains(loaded.Diagnostics,d=>d.Code=="IO.MIGRATED");Assert.DoesNotContain(loaded.Diagnostics,d=>d.Code=="IO.LEGACY_ASSET_FORMAT");
            await storage.SaveAsync(loaded.Snapshot,assets,files.PathFor("current.cadoryx"));using var current=await storage.LoadAsync(files.PathFor("current.cadoryx"),assets);
            Assert.Equal(loaded.Snapshot.Id,current.Snapshot.Id);Assert.Equal(loaded.Snapshot.StateId,current.Snapshot.StateId);
            Assert.Equal(loaded.Snapshot.Bodies.Values.Single(),current.Snapshot.Bodies.Values.Single());Assert.Empty(current.Snapshot.Sketches);Assert.Empty(current.Diagnostics);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("future")][InlineData("missing")][InlineData("arguments")][InlineData("duplicate")][InlineData("owner")][InlineData("tail")]
    public async Task MalformedSketchSectionFailsAndSettingsRemainIndependent(string mutation)
    {
        using var files=new TestFiles();var (doc,part)=SketchCommandTests.Document();var sketch=SketchTestData.Rectangle(part);doc=doc with{Sketches=doc.Sketches.Add(sketch.Id,sketch)};
        var storage=new CadDocumentStorage();var assets=new MemoryAssetStore();string path=files.PathFor("bad.cadoryx");await storage.SaveAsync(doc,assets,path);
        using(var zip=ZipFile.Open(path,ZipArchiveMode.Update))
        {
            CadManifest manifest;using(var input=zip.GetEntry("manifest.json")!.Open())manifest=JsonSerializer.Deserialize<CadManifest>(input,CadJson.Options)!;
            var section=manifest.Sections.Single(s=>s.Kind=="sketches");PackSketches data;
            using(var input=zip.GetEntry(section.Path)!.Open())data=MessagePackSerializer.Deserialize<PackSketches>(input);
            zip.GetEntry(section.Path)!.Delete();
            if(mutation=="missing")manifest=manifest with{Sections=manifest.Sections.Remove(section)};
            else
            {
                var s=data.Sketches[0];
                if(mutation=="future")s.Constraints[0]=s.Constraints[0] with{Kind="future.constraint"};
                if(mutation=="arguments")s.Constraints[0]=s.Constraints[0] with{Targets=[]};
                if(mutation=="duplicate")data=data with{Sketches=[s,s]};
                if(mutation=="owner")data=data with{Sketches=[s with{Part=Guid.NewGuid()}]};
                byte[] bytes=MessagePackSerializer.Serialize(data);if(mutation=="tail")bytes=[..bytes,0];
                using(var output=zip.CreateEntry(section.Path).Open())output.Write(bytes);
                manifest=manifest with{Sections=manifest.Sections.Replace(section,section with{Length=bytes.Length,Sha256=Convert.ToHexStringLower(SHA256.HashData(bytes))})};
            }
            zip.GetEntry("manifest.json")!.Delete();using var m=zip.CreateEntry("manifest.json").Open();JsonSerializer.Serialize(m,manifest,CadJson.Options);
        }
        Assert.Equal(doc.Settings,await storage.ReadSettingsAsync(path));
        if(mutation is "future" or "missing")await Assert.ThrowsAsync<NotSupportedException>(()=>storage.LoadAsync(path,assets));
        else await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task RecoveryCarriesSolvedSketchIdentityCoordinatesAndConstraints()
    {
        using var files=new TestFiles();var (doc,part)=SketchCommandTests.Document();var solved=(await new ManagedSketchConstraintSolver().SolveAsync(SketchTestData.Rectangle(part))).Solution!;
        doc=doc with{Sketches=doc.Sketches.Add(solved.Id,solved)};var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();RecoveryKey key;
        using(var store=new CadRecoveryStore(files.PathFor("recovery"),storage))
        {var sessionId=Guid.NewGuid();key=new(store.RunId,sessionId);await store.WriteAsync(sessionId,doc,assets,null);}
        using var reader=new CadRecoveryStore(files.PathFor("recovery"),storage);using var recovered=await reader.OpenAsync(key,assets);
        Equal(solved,recovered.Document.Snapshot.Sketches[solved.Id]);Assert.Equal(doc.StateId,recovered.Document.Snapshot.StateId);
    }
    internal static void Equal(CadSketch a,CadSketch b)
    {
        Assert.Equal(a.Id,b.Id);Assert.Equal(a.PartId,b.PartId);Assert.Equal(a.Name,b.Name);Assert.Equal(a.Plane,b.Plane);
        Assert.Equal(a.Points.ToArray(),b.Points.ToArray());Assert.Equal(a.Lines.ToArray(),b.Lines.ToArray());Assert.Equal(a.Circles.ToArray(),b.Circles.ToArray());Assert.Equal(a.Constraints.ToArray(),b.Constraints.ToArray());
    }
}
