using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cadoryx.Db;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using MessagePack;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchExpressionSectionTests
{
    public static IEnumerable<object[]> Cases()=>from kind in Enumerable.Range(0,8) from json in new[]{false,true} select new object[]{kind,json};
    [Theory][MemberData(nameof(Cases))]
    public async Task MainDocumentJsonAndMessagePackSectionsPreserveCompleteAuthorsAndDependencies(int kind,bool json)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var(d,source)=SketchExpressionEntityTests.Source(part,kind);
        var a=d.AddExactOffset(source,.075);d.EditExpression(a,d.Value.ExpressionCurves.Single(e=>e.Id==a).Geometry.Reverse().Trim(.1,.9));
        d.AddExactOffset(a,-.025);var frozen=d.AddExactOffset(source,.1);d.FreezeExpression(frozen);
        var doc=initial with{Sketches=initial.Sketches.Add(d.Value.Id,d.Value)};var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();var path=files.PathFor("expressions.cadoryx");
        await storage.SaveAsync(doc,assets,path);var manifest=FormatEvolutionTests.Manifest(path);
        Assert.Equal("0.7.8",manifest.ApplicationVersion);Assert.Equal(18,manifest.Sections.Length);Assert.Contains("cadoryx.exact-sketch-expressions.1",manifest.RequiredCapabilities);
        Assert.Equal(60,manifest.Sections.Single(e=>e.Kind=="document").SchemaVersion);Assert.Equal(22,manifest.Sections.Single(e=>e.Kind=="sketches").SchemaVersion);
        if(json)Rewrite(path,"sketch-expressions",_=>SketchExpressionSectionStorage.EncodeJson(doc.Sketches.Values),encoding:"json",version:3);
        using(var loaded=await storage.LoadAsync(path,assets))
        {
            var actual=loaded.Snapshot.Sketches[d.Value.Id];Assert.True(SketchExpressionEntities.SameTable(d.Value,actual));Assert.Equal(d.Value.Plane,actual.Plane);
            Assert.Equal(doc.Id,loaded.Snapshot.Id);Assert.Equal(doc.StateId,loaded.Snapshot.StateId);Assert.Equal(d.Value.Revision,actual.Revision);
            Assert.Equal(json,loaded.Diagnostics.Any(x=>x.Code=="IO.MIGRATED"));
            foreach(var curve in d.Value.ExpressionCurves)
                foreach(double t in new[]{0d,.13,.5,.91,1})
                {
                    var expected=curve.Geometry.At(t);var observed=actual.ExpressionCurves.Single(e=>e.Id==curve.Id).Geometry.At(t);
                    Assert.InRange(double.Hypot(expected.X-observed.X,expected.Y-observed.Y),0,1e-9);
                }
            await storage.SaveAsync(loaded.Snapshot,assets,files.PathFor("resaved.cadoryx"));using var reopened=await storage.LoadAsync(files.PathFor("resaved.cadoryx"),assets);
            Assert.Empty(reopened.Diagnostics);Assert.True(SketchExpressionEntities.SameTable(actual,reopened.Snapshot.Sketches[actual.Id]));
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("owner")][InlineData("duplicate")][InlineData("entity")][InlineData("source")][InlineData("path")][InlineData("basis")]
    [InlineData("future")][InlineData("non-construction")][InlineData("tail")][InlineData("fields")][InlineData("downgrade")][InlineData("missing")]
    public async Task MalformedMainSectionCannotPublishPartialAuthorState(string mutation)
    {
        using var files=new TestFiles();var(doc,part)=SketchCommandTests.Document();var(d,source)=SketchExpressionEntityTests.Source(part,0);d.AddExactOffset(source,.2);
        doc=doc with{Sketches=doc.Sketches.Add(d.Value.Id,d.Value)};var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();var path=files.PathFor("malformed.cadoryx");await storage.SaveAsync(doc,assets,path);
        if(mutation=="downgrade")Rewrite(path,"document",b=>b,version:52);
        else if(mutation=="missing")Rewrite(path,"sketch-expressions",_=>null);
        else Rewrite(path,"sketch-expressions",bytes=>
        {
            if(mutation=="tail")return [..bytes,0];if(mutation=="fields")return [0x93,..bytes.AsSpan(1).ToArray(),0];
            var packed=MessagePackSerializer.Deserialize<PackSketchExpressionSection>(bytes);var table=packed.Sketches[0];var c=table.Curves[0];
            if(mutation=="owner")table=table with{Sketch=Guid.NewGuid()};
            if(mutation=="entity")c=c with{Id=source.Value};
            if(mutation=="source")c=c with{Source=c.Source! with{Entity=Guid.NewGuid()}};
            if(mutation=="path")c=c with{Source=c.Source! with{BasisDepth=0}};
            if(mutation=="basis")c=c with{Source=c.Source! with{Geometry=c.Source!.Geometry with{Nodes=[c.Source.Geometry.Nodes[0] with{LastParameter=.5}]}}};
            if(mutation=="future")packed=packed with{SchemaVersion=5};
            if(mutation=="non-construction")c=c with{IsConstruction=false};
            table=table with{Curves=[c]};packed=packed with{Sketches=mutation=="duplicate"?[table,table]:[table]};return MessagePackSerializer.Serialize(packed);
        });
        if(mutation=="missing")await Assert.ThrowsAsync<NotSupportedException>(()=>storage.LoadAsync(path,assets));
        else await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));
        Assert.Equal(doc.Settings,await storage.ReadSettingsAsync(path));Assert.Equal(0,assets.Count);doc.Validate();
    }
    [Theory][InlineData("unknown")][InlineData("missing")][InlineData("source-field")][InlineData("node-direction")][InlineData("node-distance")]
    public void JsonRejectsUnknownAndMissingRequiredEntityEvidence(string mutation)
    {
        var(d,source)=SketchExpressionEntityTests.Source(DefinitionId.New(),0);d.AddExactOffset(source,.2);
        var node=JsonNode.Parse(SketchExpressionSectionStorage.EncodeJson([d.Value]))!;var entity=node["sketches"]![0]!["curves"]![0]!;
        if(mutation=="unknown")entity["ignored"]=1;else if(mutation=="missing")entity.AsObject().Remove("source");
        else if(mutation=="node-direction")entity["geometry"]!["nodes"]![1]!.AsObject().Remove("reversed");
        else if(mutation=="node-distance")entity["geometry"]!["nodes"]![1]!.AsObject().Remove("signedOffset");else entity["source"]!["ignored"]=1;
        var empty=d.Value with{ExpressionCurves=[]};Assert.Throws<InvalidDataException>(()=>SketchExpressionSectionStorage.AttachJson(JsonSerializer.SerializeToUtf8Bytes(node),ImmutableDictionary<SketchId,CadSketch>.Empty.Add(empty.Id,empty)));
    }
    [Fact] public async Task Document52MigrationAddsOnlyAnEmptyExpressionRegistryAndRetainsOldSketchBytes()
    {
        using var files=new TestFiles();var(doc,part)=SketchCommandTests.Document();var(d,_)=SketchExpressionEntityTests.Source(part,6);doc=doc with{Sketches=doc.Sketches.Add(d.Value.Id,d.Value)};
        var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();var path=files.PathFor("legacy52.cadoryx");await storage.SaveAsync(doc,assets,path);
        var oldSketchHash=SketchLegacyPointFixture.GeometryHash(path);
        SketchLegacyPointFixture.Prepare(path);Rewrite(path,"document",b=>b,version:52);Rewrite(path,"sketch-expressions",_=>null);
        using(var loaded=await storage.LoadAsync(path,assets))
        {
            Assert.Equal(doc.Id,loaded.Snapshot.Id);Assert.Equal(doc.StateId,loaded.Snapshot.StateId);Assert.Empty(loaded.Snapshot.Sketches[d.Value.Id].ExpressionCurves);
            SketchLegacyPointFixture.SamePoints(d.Value,loaded.Snapshot.Sketches[d.Value.Id]);Assert.True(d.Value.Freeforms.Single().Geometry.SameValues(loaded.Snapshot.Sketches[d.Value.Id].Freeforms.Single().Geometry));
            Assert.Contains(loaded.Diagnostics,x=>x.Code=="IO.MIGRATED");var saved=files.PathFor("migrated.cadoryx");await storage.SaveAsync(loaded.Snapshot,assets,saved);
            Assert.Equal(oldSketchHash,SketchLegacyPointFixture.GeometryHash(saved));
            using var reopened=await storage.LoadAsync(saved,assets);Assert.Empty(reopened.Diagnostics);Assert.Equal(18,FormatEvolutionTests.Manifest(saved).Sections.Length);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public void CyclicEntitySourcesAreRejectedBeforeAnyTableIsAttached()
    {
        var(d,_)=SketchExpressionEntityTests.Source(DefinitionId.New(),0);var basis=SketchCurveExpressionGeometry.FromBoundary(new(new(0,0),new(8,4)));
        var a=SketchEntityId.New();var b=SketchEntityId.New();var c1=new SketchExpressionCurve(a,basis.Offset(.1),new(b,SketchExpressionSourceKind.Expression,basis,1));
        var c2=new SketchExpressionCurve(b,basis.Offset(.2),new(a,SketchExpressionSourceKind.Expression,basis,1));
        Assert.Throws<CadValidationException>(()=>(d.Value with{ExpressionCurves=[c1,c2]}).Validate());
        var packedGeometry=JsonSerializer.Deserialize<PackSketchCurveExpression>(SketchCurveExpressionStorage.EncodeJson(basis),CadJson.Options)!;
        var empty=d.Value;var table=new PackSketchExpressionTable(empty.Id.Value,
            [new(a.Value,JsonSerializer.Deserialize<PackSketchCurveExpression>(SketchCurveExpressionStorage.EncodeJson(c1.Geometry),CadJson.Options)!,new(b.Value,8,packedGeometry,1,false),true),
             new(b.Value,JsonSerializer.Deserialize<PackSketchCurveExpression>(SketchCurveExpressionStorage.EncodeJson(c2.Geometry),CadJson.Options)!,new(a.Value,8,packedGeometry,1,false),true)]);
        var registry=ImmutableDictionary<SketchId,CadSketch>.Empty.Add(empty.Id,empty);
        Assert.Throws<InvalidDataException>(()=>SketchExpressionSectionStorage.AttachMessagePack(MessagePackSerializer.Serialize(new PackSketchExpressionSection(4,[table with{Contacts=[]}])),registry));
        Assert.Empty(registry[empty.Id].ExpressionCurves);
    }
    [Fact] public async Task LegacyFixtureEmulationCannotRemoveNonemptyExpressionAuthors()
    {
        using var files=new TestFiles();var(doc,part)=SketchCommandTests.Document();var(d,source)=SketchExpressionEntityTests.Source(part,0);d.AddExactOffset(source,.2);
        doc=doc with{Sketches=doc.Sketches.Add(d.Value.Id,d.Value)};var storage=new CadDocumentStorage();var assets=new MemoryAssetStore();var file=files.PathFor("downgrade.cadoryx");
        await storage.SaveAsync(doc,assets,file);FormatEvolutionTests.RewriteManifest(file,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="document"?s with{SchemaVersion=52}:s).ToImmutableArray()});
        Assert.Contains(FormatEvolutionTests.Manifest(file).Sections,s=>s.Kind=="sketch-expressions");
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(file,assets));Assert.Equal(0,assets.Count);
    }
    internal static void Rewrite(string path,string kind,Func<byte[],byte[]?> change,string? encoding=null,int? version=null)
    {
        using var zip=ZipFile.Open(path,ZipArchiveMode.Update);CadManifest manifest;
        using(var input=zip.GetEntry("manifest.json")!.Open())manifest=JsonSerializer.Deserialize<CadManifest>(input,CadJson.Options)!;
        var section=manifest.Sections.Single(s=>s.Kind==kind);byte[] original;using(var input=zip.GetEntry(section.Path)!.Open())using(var buffer=new MemoryStream()){input.CopyTo(buffer);original=buffer.ToArray();}
        var bytes=change(original);zip.GetEntry(section.Path)!.Delete();
        if(bytes is null)manifest=manifest with{Sections=manifest.Sections.Remove(section)};
        else
        {
            using(var output=zip.CreateEntry(section.Path).Open())output.Write(bytes);
            manifest=manifest with{Sections=manifest.Sections.Replace(section,section with{Length=bytes.Length,Sha256=Convert.ToHexStringLower(SHA256.HashData(bytes)),Encoding=encoding??section.Encoding,SchemaVersion=version??section.SchemaVersion})};
        }
        zip.GetEntry("manifest.json")!.Delete();using var stream=zip.CreateEntry("manifest.json").Open();JsonSerializer.Serialize(stream,manifest,CadJson.Options);
    }
}
