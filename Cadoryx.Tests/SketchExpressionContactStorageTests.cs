using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using MessagePack;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchExpressionContactStorageTests
{
    private static void SameConstraints(ImmutableArray<SketchConstraint> expected,ImmutableArray<SketchConstraint> actual)
    {
        Assert.Equal(expected.Length,actual.Length);
        for(int i=0;i<expected.Length;i++)Assert.Equal(expected[i],actual[i]);
    }
    public static IEnumerable<object[]> Modes()=>from mode in Enum.GetValues<FiniteContactParameterMode>()
        from continuity in Enum.GetValues<FiniteContactContinuity>() from json in new[]{false,true} select new object[]{mode,continuity,json};
    [Theory][MemberData(nameof(Modes))]
    public async Task AtomicJsonAndMessagePackAttachmentPreservesConstraintOrderSourcesAndAutomaticAuthors(FiniteContactParameterMode mode,FiniteContactContinuity continuity,bool json)
    {
        using var files=new TestFiles();var(doc,part)=SketchCommandTests.Document();var source=SketchExpressionContactTests.Pair(3,true,continuity,mode) with{PartId=part};
        var result=await new ManagedSketchConstraintSolver().SolveAsync(source);Assert.True(result.Succeeded,result.Diagnostic);var sketch=result.Solution!;
        var snapshot=doc with{Sketches=doc.Sketches.Add(sketch.Id,sketch)};var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();var file=files.PathFor("contacts.cadoryx");
        await storage.SaveAsync(snapshot,assets,file);var manifest=FormatEvolutionTests.Manifest(file);
        Assert.Equal("0.7.8",manifest.ApplicationVersion);Assert.Equal(60,manifest.Sections.Single(s=>s.Kind=="document").SchemaVersion);
        Assert.Equal(22,manifest.Sections.Single(s=>s.Kind=="sketches").SchemaVersion);Assert.Equal(4,manifest.Sections.Single(s=>s.Kind=="sketch-expressions").SchemaVersion);
        Assert.Contains("cadoryx.exact-expression-contacts.1",manifest.RequiredCapabilities);Assert.Equal(18,manifest.Sections.Length);
        PackSketches? basic=null;FormatEvolutionTests.RewriteSection(file,"sketches",bytes=>{basic=MessagePackSerializer.Deserialize<PackSketches>(bytes);return bytes;});
        Assert.DoesNotContain(basic!.Sketches.Single().Constraints,c=>c.Kind.Contains("parametric"));
        if(json)SketchExpressionSectionTests.Rewrite(file,"sketch-expressions",_=>SketchExpressionSectionStorage.EncodeJson([sketch]),"json",3);
        using(var loaded=await storage.LoadAsync(file,assets))
        {
            var actual=loaded.Snapshot.Sketches[sketch.Id];SameConstraints(sketch.Constraints,actual.Constraints);Assert.True(SketchExpressionEntities.SameTable(sketch,actual));
            Assert.Equal(snapshot.StateId,loaded.Snapshot.StateId);SketchContactSolutionVerification.Validate(source,actual);
            SketchExpressionContactTests.CheckNative(actual,actual.Constraints.OfType<FiniteParametricContactConstraint>().Single());
            var saved=files.PathFor("resaved.cadoryx");await storage.SaveAsync(loaded.Snapshot,assets,saved);using var reopened=await storage.LoadAsync(saved,assets);
            Assert.Empty(reopened.Diagnostics);SameConstraints(actual.Constraints,reopened.Snapshot.Sketches[sketch.Id].Constraints);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task RealLegacyExpressionTablesMigrateWithoutInventingContactsOrChangingBaseSketchBytes(bool json)
    {
        using var files=new TestFiles();var(doc,part)=SketchCommandTests.Document();var(d,id)=SketchExpressionEntityTests.Source(part,3);d.AddExactOffset(id,.04);
        var snapshot=doc with{Sketches=doc.Sketches.Add(d.Value.Id,d.Value)};var storage=new CadDocumentStorage();var assets=new MemoryAssetStore();var file=files.PathFor("legacy.cadoryx");
        await storage.SaveAsync(snapshot,assets,file);var baseline=SketchLegacyPointFixture.GeometryHash(file);
        SketchExpressionSectionTests.Rewrite(file,"sketch-expressions",bytes=>
        {
            var current=MessagePackSerializer.Deserialize<PackSketchExpressionSection>(bytes);
            var old=new PackLegacySketchExpressionSection(json?1:2,current.Sketches.Select(t=>new PackLegacySketchExpressionTable(t.Sketch,
                t.Curves.Select(c=>new PackLegacySketchExpressionEntity(c.Id,c.Geometry,c.Source,c.IsConstruction)).ToArray())).ToArray());
            return json?JsonSerializer.SerializeToUtf8Bytes(old,CadJson.Options):MessagePackSerializer.Serialize(old);
        },json?"json":"messagepack",json?1:2);
        SketchLegacyPointFixture.Prepare(file);SketchExpressionSectionTests.Rewrite(file,"document",b=>b,version:54);
        using var loaded=await storage.LoadAsync(file,assets);var actual=loaded.Snapshot.Sketches[d.Value.Id];SketchLegacyPointFixture.SamePoints(d.Value,actual);Assert.True(SketchExpressionEntities.SameTable(d.Value,actual));
        SameConstraints(d.Value.Constraints,actual.Constraints);Assert.Empty(actual.Constraints.OfType<FiniteParametricContactConstraint>());
        var saved=files.PathFor("migrated.cadoryx");await storage.SaveAsync(loaded.Snapshot,assets,saved);
        Assert.Equal(baseline,SketchLegacyPointFixture.GeometryHash(saved));Assert.Equal(0,assets.Count);
    }
    public static IEnumerable<object[]> Malformed()=>from mutation in new[]{"owner","duplicate-position","out-of-range","foreign-target","ordinary-targets","kind","missing-table",
        "mode","seed","hidden-fixed","duplicate-id","values","target-count","tail","fields","source"} from json in new[]{false,true} select new object[]{mutation,json};
    [Theory][MemberData(nameof(Malformed))]
    public async Task InvalidOrDisabledContactsCannotPublishAnyPartOfTheCombinedTables(string mutation,bool json)
    {
        using var files=new TestFiles();var(doc,part)=SketchCommandTests.Document();var source=SketchExpressionContactTests.Pair(3,false,FiniteContactContinuity.Curvature,FiniteContactParameterMode.SolveA) with{PartId=part};
        source=source with{Constraints=source.Constraints.Select(c=>c is FiniteParametricContactConstraint?c with{IsEnabled=false}:c).ToImmutableArray()};
        var snapshot=doc with{Sketches=doc.Sketches.Add(source.Id,source)};var storage=new CadDocumentStorage();var assets=new MemoryAssetStore();var file=files.PathFor("malformed.cadoryx");await storage.SaveAsync(snapshot,assets,file);
        SketchExpressionSectionTests.Rewrite(file,"sketch-expressions",bytes=>
        {
            var pack=MessagePackSerializer.Deserialize<PackSketchExpressionSection>(bytes);var table=pack.Sketches[0];var entry=table.Contacts![0];var c=entry.Constraint;var values=(double[])c.Values.Clone();
            if(mutation=="owner")table=table with{Sketch=Guid.NewGuid()};
            if(mutation=="out-of-range")entry=entry with{Position=9999};
            if(mutation=="foreign-target")c=c with{Targets=[c.Targets[0],Guid.NewGuid()]};
            if(mutation=="ordinary-targets")c=c with{Targets=[source.Beziers[0].Id.Value,source.Beziers[1].Id.Value]};
            if(mutation=="kind")c=c with{Kind="fix-point"};
            if(mutation=="mode")values[4]=1;if(mutation=="seed")values[5]=0;if(mutation=="hidden-fixed")values[8]=.3;
            if(mutation=="values")values=values[..^1];if(mutation=="target-count")c=c with{Targets=c.Targets[..^1]};
            if(mutation=="duplicate-id")c=c with{Id=source.Constraints.First(v=>v is FixPointConstraint).Id.Value};
            if(mutation=="source")table=table with{Curves=[table.Curves[0] with{Source=table.Curves[0].Source! with{Entity=Guid.NewGuid()}}]};
            entry=entry with{Constraint=c with{Values=values}};table=table with{Contacts=mutation=="missing-table"?null:mutation=="duplicate-position"?[entry,entry]:[entry]};
            pack=pack with{Sketches=[table],SchemaVersion=json?3:4};
            var encoded=json?JsonSerializer.SerializeToUtf8Bytes(pack,CadJson.Options):MessagePackSerializer.Serialize(pack);
            if(mutation=="tail")return [..encoded,(byte)'0'];
            if(mutation=="fields")
            {
                if(!json)return [0x93,..encoded.AsSpan(1).ToArray(),0];
                var node=JsonNode.Parse(encoded)!;node["sketches"]![0]!["contacts"]![0]!["constraint"]!["hidden"]=true;return JsonSerializer.SerializeToUtf8Bytes(node);
            }
            return encoded;
        },json?"json":"messagepack",json?3:4);
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(file,assets));Assert.Equal(0,assets.Count);snapshot.Validate();
    }
    [Theory][InlineData("position")][InlineData("constraint")][InlineData("id")][InlineData("kind")][InlineData("targets")][InlineData("values")][InlineData("enabled")]
    public void JsonContactFieldsAreRequiredEvenWhenFalseOrEmpty(string field)
    {
        var source=SketchExpressionContactTests.Pair(3,false,FiniteContactContinuity.Tangent,FiniteContactParameterMode.Fixed);
        var node=JsonNode.Parse(SketchExpressionSectionStorage.EncodeJson([source]))!;var contact=node["sketches"]![0]!["contacts"]![0]!;
        if(field is "position" or "constraint")contact.AsObject().Remove(field);else contact["constraint"]!.AsObject().Remove(field);
        var basic=source with{ExpressionCurves=[],Constraints=source.Constraints.Where(c=>!SketchFiniteParametricContact.UsesExpression(source,c)).ToImmutableArray()};
        Assert.Throws<InvalidDataException>(()=>SketchExpressionSectionStorage.AttachJson(JsonSerializer.SerializeToUtf8Bytes(node),ImmutableDictionary<SketchId,CadSketch>.Empty.Add(basic.Id,basic)));
        Assert.Empty(basic.ExpressionCurves);
    }
    [Theory][InlineData("document")][InlineData("section")][InlineData("both")]
    public async Task EachLegacyDescriptorIndependentlyRejectsExpressionContacts(string descriptor)
    {
        using var files=new TestFiles();var(doc,part)=SketchCommandTests.Document();var source=SketchExpressionContactTests.Pair(3,false,FiniteContactContinuity.Tangent,FiniteContactParameterMode.Fixed) with{PartId=part};
        var storage=new CadDocumentStorage();var assets=new MemoryAssetStore();var file=files.PathFor("downgrade.cadoryx");await storage.SaveAsync(doc with{Sketches=doc.Sketches.Add(source.Id,source)},assets,file);
        FormatEvolutionTests.RewriteManifest(file,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="document"&&descriptor is "document" or "both"?s with{SchemaVersion=54}:
            s.Kind=="sketch-expressions"&&descriptor is "section" or "both"?s with{SchemaVersion=2}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(file,assets));Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(1)][InlineData(3)][InlineData(5)][InlineData(6)]
    public async Task CommittedOffsetEditsRecomputeContactAndUndoRedoSaveAndRecoverAtomically(int kind)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var source=SketchExpressionContactTests.Pair(kind,false,FiniteContactContinuity.Curvature,FiniteContactParameterMode.Fixed,false) with{PartId=part};
            await session.ExecuteAsync(new UpsertSketchCommand(source,new ManagedSketchConstraintSolver()));var before=session.Snapshot;var original=before.Sketches[source.Id];var curve=original.ExpressionCurves.Single();
            var draft=new SketchDraft(original);draft.EditExpression(curve.Id,curve.Geometry with{SignedOffset=curve.Geometry.SignedOffset+.01});
            await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,new ManagedSketchConstraintSolver()));var after=session.Snapshot;var current=after.Sketches[source.Id];
            Assert.Equal(curve.Id,current.ExpressionCurves.Single().Id);Assert.False(curve.Geometry.SameValues(current.ExpressionCurves.Single().Geometry));
            Assert.Equal(original.Constraints.OfType<FiniteParametricContactConstraint>().Single(),current.Constraints.OfType<FiniteParametricContactConstraint>().Single());
            SketchExpressionContactTests.CheckNative(current,current.Constraints.OfType<FiniteParametricContactConstraint>().Single());
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
            var storage=new CadDocumentStorage();RecoveryKey key;
            using(var writer=new CadRecoveryStore(files.PathFor("recovery"),storage)){var id=Guid.NewGuid();key=new(writer.RunId,id);await writer.WriteAsync(id,after,assets,null);}
            using var reader=new CadRecoveryStore(files.PathFor("recovery"),storage);using var recovered=await reader.OpenAsync(key,assets);
            Assert.Equal(after.StateId,recovered.Document.Snapshot.StateId);SameConstraints(current.Constraints,recovered.Document.Snapshot.Sketches[current.Id].Constraints);
            Assert.True(SketchExpressionEntities.SameTable(current,recovered.Document.Snapshot.Sketches[current.Id]));
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task MultipleExpressionToExpressionContactsPreserveInterleavedDisabledAndBasicRelations(bool json)
    {
        using var files=new TestFiles();var(doc,part)=SketchCommandTests.Document();var source=SketchExpressionContactTests.Pair(3,false,FiniteContactContinuity.Curvature,FiniteContactParameterMode.Fixed) with{PartId=part};
        var draft=new SketchDraft(source);var copy=draft.AddExactOffset(source.ExpressionCurves.Single().Id,0);var a=source.ExpressionCurves.Single().Id;
        var first=new FiniteParametricContactConstraint(SketchConstraintId.New(),a,copy,.37,.37,FiniteContactTangentBranch.SameDirection){Continuity=FiniteContactContinuity.Curvature};
        var second=first with{Id=SketchConstraintId.New(),IsEnabled=false};
        source=draft.Value with{Constraints=draft.Value.Constraints.Insert(0,first).Add(second)};
        var result=await new ManagedSketchConstraintSolver().SolveAsync(source);Assert.True(result.Succeeded,result.Diagnostic);source=result.Solution!;
        var storage=new CadDocumentStorage();var assets=new MemoryAssetStore();var file=files.PathFor("multiple.cadoryx");await storage.SaveAsync(doc with{Sketches=doc.Sketches.Add(source.Id,source)},assets,file);
        if(json)SketchExpressionSectionTests.Rewrite(file,"sketch-expressions",_=>SketchExpressionSectionStorage.EncodeJson([source]),"json",3);
        using var loaded=await storage.LoadAsync(file,assets);var actual=loaded.Snapshot.Sketches[source.Id];SameConstraints(source.Constraints,actual.Constraints);
        SketchContactSolutionVerification.Validate(source,actual);Assert.True(SketchExpressionEntities.SameTable(source,actual));Assert.Equal(0,assets.Count);
    }
}
