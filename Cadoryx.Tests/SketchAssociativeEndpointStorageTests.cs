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

public sealed class SketchAssociativeEndpointStorageTests
{
    public static IEnumerable<object[]> Contacts()=>from kind in Enumerable.Range(1,6) from curvature in new[]{false,true}
        from json in new[]{false,true} select new object[]{kind,curvature,json};
    [Theory][MemberData(nameof(Contacts))]
    public async Task MainFileAttachesEndpointAuthorsAndG1G2RelationsAtomicallyAndKeepsAllIds(int kind,bool curvature,bool json)
    {
        using var files=new TestFiles();var(doc,part)=SketchCommandTests.Document();var source=SketchAssociativeEndpointTests.Pair(kind,true,true,curvature,true) with{PartId=part};
        var result=await new ManagedSketchConstraintSolver().SolveAsync(source);Assert.True(result.Succeeded,result.Diagnostic);var sketch=result.Solution!;
        var snapshot=doc with{Sketches=doc.Sketches.Add(sketch.Id,sketch)};var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();var file=files.PathFor("endpoints.cadoryx");
        await storage.SaveAsync(snapshot,assets,file);var manifest=FormatEvolutionTests.Manifest(file);
        Assert.Equal("0.7.8",manifest.ApplicationVersion);Assert.Equal(60,manifest.Sections.Single(s=>s.Kind=="document").SchemaVersion);
        Assert.Equal(4,manifest.Sections.Single(s=>s.Kind=="sketch-expressions").SchemaVersion);Assert.Equal(22,manifest.Sections.Single(s=>s.Kind=="sketches").SchemaVersion);
        if(json)SketchExpressionSectionTests.Rewrite(file,"sketch-expressions",_=>SketchExpressionSectionStorage.EncodeJson([sketch]),"json",3);
        using var loaded=await storage.LoadAsync(file,assets);var actual=loaded.Snapshot.Sketches[sketch.Id];
        Assert.True(SketchExpressionEntities.SameTable(sketch,actual));Assert.Equal(sketch.Points.ToArray(),actual.Points.ToArray());
        Assert.Equal(sketch.Constraints.ToArray(),actual.Constraints.ToArray());SketchContactSolutionVerification.Validate(source,actual);SketchAssociativeEndpoints.RequireSolved(actual);
        var saved=files.PathFor("saved.cadoryx");await storage.SaveAsync(loaded.Snapshot,assets,saved);using var reopened=await storage.LoadAsync(saved,assets);
        Assert.True(SketchExpressionEntities.SameTable(actual,reopened.Snapshot.Sketches[sketch.Id]));Assert.Empty(reopened.Diagnostics);Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task RealPreviousContactTablesMigrateWithNoInventedEndpointIdentities(bool json)
    {
        using var files=new TestFiles();var(doc,part)=SketchCommandTests.Document();var source=SketchExpressionContactTests.Pair(3,false,FiniteContactContinuity.Curvature,FiniteContactParameterMode.SolveBoth) with{PartId=part};
        var result=await new ManagedSketchConstraintSolver().SolveAsync(source);Assert.True(result.Succeeded,result.Diagnostic);source=result.Solution!;
        var storage=new CadDocumentStorage();var assets=new MemoryAssetStore();var file=files.PathFor("previous.cadoryx");await storage.SaveAsync(doc with{Sketches=doc.Sketches.Add(source.Id,source)},assets,file);
        string baseline=SketchLegacyPointFixture.GeometryHash(file);
        SketchExpressionSectionTests.Rewrite(file,"sketch-expressions",bytes=>
        {
            var current=MessagePackSerializer.Deserialize<PackSketchExpressionSection>(bytes);
            var old=new PackLegacySketchExpressionContactSection(json?2:3,current.Sketches.Select(t=>new PackLegacySketchExpressionContactTable(t.Sketch,
                t.Curves.Select(c=>new PackLegacySketchExpressionEntity(c.Id,c.Geometry,c.Source,c.IsConstruction)).ToArray(),t.Contacts!)).ToArray());
            return json?JsonSerializer.SerializeToUtf8Bytes(old,CadJson.Options):MessagePackSerializer.Serialize(old);
        },json?"json":"messagepack",json?2:3);
        SketchLegacyPointFixture.Prepare(file);SketchExpressionSectionTests.Rewrite(file,"document",b=>b,version:55);
        using var loaded=await storage.LoadAsync(file,assets);var actual=loaded.Snapshot.Sketches[source.Id];SketchLegacyPointFixture.SamePoints(source,actual);Assert.True(SketchExpressionEntities.SameTable(source,actual));
        Assert.All(actual.ExpressionCurves,c=>Assert.Null(c.Endpoints));Assert.Equal(source.Constraints.ToArray(),actual.Constraints.ToArray());
        var saved=files.PathFor("migrated.cadoryx");await storage.SaveAsync(loaded.Snapshot,assets,saved);
        Assert.Equal(baseline,SketchLegacyPointFixture.GeometryHash(saved));Assert.Equal(0,assets.Count);
    }
    public static IEnumerable<object[]> Invalid()=>from mutation in new[]{"missing","point","same-point","equation","equation-collision","source-cycle","joint","disabled-joint","document","section","both","fields"}
        from json in new[]{false,true} select new object[]{mutation,json};
    [Theory][MemberData(nameof(Invalid))]
    public async Task MalformedEndpointAndDisabledRelationshipAuthorsCannotPublishPartialState(string mutation,bool json)
    {
        using var files=new TestFiles();var(doc,part)=SketchCommandTests.Document();var source=SketchAssociativeEndpointTests.Pair(3,false,false,true,true) with{PartId=part};
        var storage=new CadDocumentStorage();var assets=new MemoryAssetStore();var file=files.PathFor("bad.cadoryx");await storage.SaveAsync(doc with{Sketches=doc.Sketches.Add(source.Id,source)},assets,file);
        SketchExpressionSectionTests.Rewrite(file,"sketch-expressions",bytes=>
        {
            var value=MessagePackSerializer.Deserialize<PackSketchExpressionSection>(bytes);var table=value.Sketches[0];var c=table.Curves[0];var e=c.Endpoints!;
            if(mutation=="missing")c=c with{Endpoints=null};if(mutation=="point")e=e with{Start=Guid.NewGuid()};
            if(mutation=="same-point")e=e with{End=e.Start};if(mutation=="equation")e=e with{EndEquation=e.StartEquation};
            if(mutation=="equation-collision")e=e with{StartEquation=source.Constraints[0].Id.Value};
            if(mutation=="source-cycle")c=c with{Source=c.Source! with{Entity=c.Id,Kind=8}};
            if(mutation is "joint" or "disabled-joint")
            {
                var entry=table.Contacts![0];var constraint=entry.Constraint with{Targets=[entry.Constraint.Targets[0],entry.Constraint.Targets[1],source.Beziers[^1].End.Value],Enabled=mutation!="disabled-joint"};
                table=table with{Contacts=[entry with{Constraint=constraint}]};
            }
            if(mutation!="missing")c=c with{Endpoints=e};table=table with{Curves=[c]};value=value with{Sketches=[table],SchemaVersion=json?3:4};
            var encoded=json?JsonSerializer.SerializeToUtf8Bytes(value,CadJson.Options):MessagePackSerializer.Serialize(value);
            if(mutation=="fields")
            {
                if(!json)return [0x93,..encoded.AsSpan(1).ToArray(),0];
                var node=JsonNode.Parse(encoded)!;node["sketches"]![0]!["curves"]![0]!["endpoints"]!.AsObject().Remove("startEquation");return JsonSerializer.SerializeToUtf8Bytes(node);
            }
            return encoded;
        },json?"json":"messagepack",json?3:4);
        if(mutation is "document" or "both")SketchExpressionSectionTests.Rewrite(file,"document",b=>b,version:55);
        if(mutation is "section" or "both")SketchExpressionSectionTests.Rewrite(file,"sketch-expressions",b=>b,version:json?2:3);
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(file,assets));Assert.Equal(0,assets.Count);source.Validate();
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task LinkedCircleProfileParameterAndExactSolidRecomputeUndoSaveAndRecoverTogether(bool revolve)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var d=new SketchDraft(CadSketch.Create(part,"Associated circle",RigidTransform3d.Identity));d.AddCircle(new(8,1),2,construction:true);
            d.Change(s=>s with{Constraints=s.Constraints.Add(new FixPointConstraint(SketchConstraintId.New(),s.Circles[0].Center,new(8,1)))});
            var id=d.AddExactOffset(d.Value.Circles[0].Id,.03);d.SetExpressionConstruction(id,false);
            await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));var s=session.Snapshot.Sketches[d.Value.Id];var endpoint=s.ExpressionCurves.Single().Endpoints!;
            var profile=SketchProfileBuilder.Mixed(s,[id]);GeometryRecipe recipe=revolve?new RevolveRecipe(profile,Math.Tau,s.Plane):new ExtrudeRecipe(profile,3,s.Plane);
            await session.ExecuteAsync(new AddBodyCommand(recipe,"Associated solid",part,sketchSource:SketchProfileReference.CreateMixed(s,[id])));
            var target=ParameterTarget.SketchCurve(s.Id,id,"OffsetDistance",0);var parameter=new CadParameter(Guid.NewGuid(),"distance",ParameterDimension.Length,"0.03 mm");
            var book=new ParameterBook([parameter],[new(Guid.NewGuid(),parameter.Id,target)]);await session.ExecuteAsync(new SetParametersCommand(book));
            var before=session.Snapshot;var feature=before.Features.Values.Single();var body=before.Bodies.Values.Single();
            await session.ExecuteAsync(new SetParametersCommand(book with{Parameters=[parameter with{Expression="0.08 mm"}]}));var after=session.Snapshot;var current=after.Sketches[s.Id];
            Assert.Equal(endpoint,current.ExpressionCurves.Single().Endpoints);SketchAssociativeEndpoints.RequireSolved(current);
            Assert.Equal(2,current.Circles[0].Radius);Assert.Equal(new(8,1),current.Points.Single(p=>p.Id==current.Circles[0].Center).Position);
            double expected=revolve?2*Math.PI*Math.PI*8*2.08*2.08:Math.PI*2.08*2.08*3;
            Assert.True(Math.Abs(after.Bodies[body.Id].Geometry.VolumeMm3-expected)<=1e-7,
                $"Actual={after.Bodies[body.Id].Geometry.VolumeMm3:R}; expected={expected:R}; radius={current.Circles[0].Radius:R}; center={current.Points.Single(p=>p.Id==current.Circles[0].Center).Position}; expression={current.ExpressionCurves.Single().Geometry}; endpoint={current.Points.Single(p=>p.Id==endpoint.Start).Position}");
            Assert.NotEqual(before.Features[feature.Id].Result.AssetId,after.Features[feature.Id].Result.AssetId);Assert.Equal(current.Revision,after.Features[feature.Id].SketchSource!.Revision);
            Assert.Contains(SketchMixedLoops.Find(current),loop=>loop.SequenceEqual(new[]{id}));
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
            var storage=new CadDocumentStorage();await session.SaveAsync(storage,files.PathFor("linked.cadoryx"));using var loaded=await storage.LoadAsync(files.PathFor("linked.cadoryx"),assets);
            Assert.True(SketchExpressionEntities.SameTable(current,loaded.Snapshot.Sketches[s.Id]));Assert.Equal(after.Bodies[body.Id],loaded.Snapshot.Bodies[body.Id]);
            RecoveryKey key;using(var writer=new CadRecoveryStore(files.PathFor("recovery"),storage)){var rid=Guid.NewGuid();key=new(writer.RunId,rid);await writer.WriteAsync(rid,after,assets,null);}
            using var reader=new CadRecoveryStore(files.PathFor("recovery"),storage);using var recovered=await reader.OpenAsync(key,assets);
            Assert.True(SketchExpressionEntities.SameTable(current,recovered.Document.Snapshot.Sketches[s.Id]));Assert.Equal(after.StateId,recovered.Document.Snapshot.StateId);
        }
        Assert.Equal(0,assets.Count);
    }
}
