using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.CommandLine;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchRegionJointAuthorStorageTests
{
    private static readonly OcctGeometryKernel Kernel=new();
    private static CadSketch Rectangle(SketchRegionAuthorJoin join)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Recorded region",RigidTransform3d.Identity));d.AddRectangle(new(0,0),new(8,4));
        return SketchRegionOffsetAuthoring.Create(d.Value,[d.Value.Lines[0].Id],.2,join,Kernel).Sketch;
    }
    private static CadSketch Capsule(DefinitionId? part=null)
    {
        var d=new SketchDraft(CadSketch.Create(part??DefinitionId.New(),"Recorded endpoint chart",RigidTransform3d.Identity));
        d.AddLine(new(0,-2),new(8,-2));var first=d.Value.Lines[0];
        d.AddArc(new(8,-2),new(10,0),new(8,2),first.End);var arc=d.Value.Arcs[0];
        d.AddLine(new(8,2),new(0,2),arc.End);var last=d.Value.Lines[^1];
        d.AddArc(new(0,2),new(-2,0),new(0,-2),last.End,existingEnd:first.Start);
        return SketchRegionOffsetAuthoring.Create(d.Value,[first.Id],.1,SketchRegionAuthorJoin.Miter,Kernel).Sketch;
    }
    [Theory][InlineData(SketchRegionAuthorJoin.Miter,false)][InlineData(SketchRegionAuthorJoin.Round,false)]
    [InlineData(SketchRegionAuthorJoin.Bevel,false)][InlineData(SketchRegionAuthorJoin.Miter,true)]
    [InlineData(SketchRegionAuthorJoin.Round,true)][InlineData(SketchRegionAuthorJoin.Bevel,true)]
    public void CurrentJsonAndMessagePackRetainCompleteCreatingOperationConnections(SketchRegionAuthorJoin join,bool json)
    {
        var sketch=Rectangle(join);var group=Assert.Single(sketch.RegionOffsets);Assert.Equal(SketchRegionJointAuthors.CurrentAlgorithm,group.Algorithm);
        Assert.Equal(4,group.Loops[0].Joints.Length);var registry=ImmutableDictionary<SketchId,CadSketch>.Empty.Add(sketch.Id,sketch with{RegionOffsets=[]});
        var bytes=json?SketchRegionSectionStorage.EncodeJson([sketch]):SketchRegionSectionStorage.EncodeMessagePack([sketch]);
        var result=json?SketchRegionSectionStorage.AttachJson(bytes,registry):SketchRegionSectionStorage.AttachMessagePack(bytes,registry);
        Assert.True(SketchRegionAuthorModel.SameTable(sketch,result[sketch.Id]));Kernel.ConfirmExpressionRegions(result[sketch.Id]);
        Assert.Equal(group.Loops[0].Joints.AsEnumerable(),result[sketch.Id].RegionOffsets[0].Loops[0].Joints);
    }
    [Theory][InlineData("empty")][InlineData("miter")][InlineData("round")][InlineData("bevel")][InlineData("tangent-capsule")][InlineData("circle")]
    public async Task ActualOldWriterFilesMigrateWithoutInventingAnyConnectionAndKeepAllAuthorIds(string label)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage(regionVerifier:Kernel);
        string path=Fixture(label,"cadoryx");var manifest=FormatEvolutionTests.Manifest(path);
        Assert.Equal("0.7.7",manifest.ApplicationVersion);Assert.Equal(59,manifest.Sections.Single(s=>s.Kind=="document").SchemaVersion);
        Assert.Equal(2,manifest.Sections.Single(s=>s.Kind=="sketch-regions").SchemaVersion);
        using var old=await storage.LoadAsync(path,assets);var sketch=Assert.Single(old.Snapshot.Sketches.Values);
        var legacy=JsonSerializer.Deserialize<LegacyPackSketchRegionSection>(File.ReadAllBytes(Fixture(label,"json")),CadJson.Options)!;
        Assert.Equal(1,legacy.Version);Assert.Contains(old.Diagnostics,d=>d.Code=="IO.MIGRATED");
        if(label=="empty")Assert.Empty(sketch.RegionOffsets);
        else
        {
            var expected=Assert.Single(legacy.Sketches).Groups.Single();var actual=Assert.Single(sketch.RegionOffsets);
            Assert.Equal(expected.Id,actual.Id);Assert.Equal(SketchRegionJointAuthors.LegacyAlgorithm,actual.Algorithm);
            Assert.All(actual.Loops,l=>{Assert.False(l.Joints.IsDefault);Assert.Empty(l.Joints);});
            Assert.Equal(expected.Loops.SelectMany(l=>l.Outputs).Select(o=>o.Curve),actual.Loops.SelectMany(l=>l.Outputs).Select(o=>o.Curve.Value));
            Assert.Equal(expected.Loops.SelectMany(l=>l.Outputs).Select(o=>o.Endpoints.Start),actual.Loops.SelectMany(l=>l.Outputs).Select(o=>o.Endpoints.Start.Value));
            Assert.Equal(expected.Loops.SelectMany(l=>l.Outputs).Select(o=>o.Endpoints.StartEquation),actual.Loops.SelectMany(l=>l.Outputs).Select(o=>o.Endpoints.StartEquation.Value));
        }
        string current=files.PathFor(label+"-current.cadoryx");await storage.SaveAsync(old.Snapshot,assets,current);
        var saved=FormatEvolutionTests.Manifest(current);Assert.Equal("0.7.8",saved.ApplicationVersion);
        Assert.Equal(60,saved.Sections.Single(s=>s.Kind=="document").SchemaVersion);Assert.Equal(3,saved.Sections.Single(s=>s.Kind=="sketch-regions").SchemaVersion);
        using(var reopened=await storage.LoadAsync(current,assets))Assert.True(SketchRegionAuthorModel.SameTable(sketch,reopened.Snapshot.Sketches[sketch.Id]));
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("empty",true)][InlineData("miter",true)][InlineData("round",true)][InlineData("bevel",true)]
    [InlineData("tangent-capsule",true)][InlineData("circle",true)][InlineData("empty",false)][InlineData("miter",false)]
    [InlineData("round",false)][InlineData("bevel",false)][InlineData("tangent-capsule",false)][InlineData("circle",false)]
    public async Task RealLegacyJsonAndMessagePackUseExplicitSectionMigrationAndRetainOldSemantics(string label,bool json)
    {
        var assets=new MemoryAssetStore();using var loaded=await new CadDocumentStorage(regionVerifier:Kernel).LoadAsync(Fixture(label,"cadoryx"),assets);
        var input=new SectionPayload(new("sketch-regions",json?1:2,json?"json":"messagepack"),File.ReadAllBytes(Fixture(label,json?"json":"messagePack")));
        var target=CadSectionMigrationRegistry.CurrentFormats["sketch-regions"];
        var migrated=new CadSectionMigrationRegistry().Migrate([input],new Dictionary<string,SectionFormat>{{target.Kind,target}})[target.Kind];
        var bases=loaded.Snapshot.Sketches.ToImmutableDictionary(p=>p.Key,p=>p.Value with{RegionOffsets=[]});
        var attached=SketchRegionSectionStorage.AttachMessagePack(migrated.Bytes,bases);
        foreach(var sketch in loaded.Snapshot.Sketches.Values)
        {Assert.True(SketchRegionAuthorModel.SameTable(sketch,attached[sketch.Id]));Kernel.ConfirmExpressionRegions(attached[sketch.Id]);}
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("slots")][InlineData("source")][InlineData("kind")][InlineData("parameter")][InlineData("traversal")]
    [InlineData("order")][InlineData("missing")][InlineData("default")][InlineData("legacy")]
    public void ForgedConnectionTablesFailLocalOwnershipValidation(string fault)
    {
        var sketch=Rectangle(SketchRegionAuthorJoin.Miter);var group=sketch.RegionOffsets[0];var loop=group.Loops[0];var joints=loop.Joints;var joint=joints[0];
        if(fault=="slots")joint=joint with{NextSourceSlot=joint.PreviousSourceSlot};
        if(fault=="source")joint=joint with{PreviousSource=SketchEntityId.New()};
        if(fault=="kind")joint=joint with{Kind=(SketchRegionJointKind)99};
        if(fault=="parameter")joint=joint with{PreviousParameter=joint.PreviousParameter+.01};
        if(fault=="traversal")joint=joint with{NextTraversalReversed=!joint.NextTraversalReversed};
        joints=joints.SetItem(0,joint);
        if(fault=="order")joints=joints.Reverse().ToImmutableArray();if(fault=="missing")joints=joints.RemoveAt(0);if(fault=="default")joints=default;
        if(fault=="legacy")group=group with{Algorithm=SketchRegionJointAuthors.LegacyAlgorithm};
        sketch=sketch with{RegionOffsets=[group with{Loops=[loop with{Joints=joints}]}]};Assert.Throws<CadValidationException>(sketch.Validate);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void IdenticalEndpointCoordinatesCannotForgeAnIntersectionBranchAndRetainedC1RejectsARealBranchChange(bool reference)
    {
        var(doc,part)=SketchCommandTests.Document();var before=Capsule(part);var group=before.RegionOffsets[0];var loop=group.Loops[0];
        var forged=before with{RegionOffsets=[group with{Loops=[loop with{Joints=loop.Joints.SetItem(0,loop.Joints[0] with{Kind=SketchRegionJointKind.NativeIntersection})}]}]};
        forged.Validate();Assert.Throws<CadValidationException>(()=>Kernel.ConfirmExpressionRegions(forged));
        // A real source edit changes the two right-hand corner branches while
        // all curve, source, endpoint and group identities remain unchanged.
        var middle=before.Arcs[0].Middle;
        var frozen=before with{Points=before.Points.Select(p=>p.Id==middle?p with{Position=new(p.Position.X+.05,p.Position.Y)}:p).ToImmutableArray(),
            RegionOffsets=[group with{IsFrozen=true,FrozenReason="Explicit source change"}]};frozen.Validate();
        var after=SketchRegionSourceReselection.Apply(frozen,group.Id,group.Sources.Select(l=>l.Select(s=>s.Entity).ToImmutableArray()).ToImmutableArray(),Kernel);
        after.Validate();Kernel.ConfirmExpressionRegions(after);
        Assert.False(SketchRegionJointAuthors.SameStructure(loop.Joints,after.RegionOffsets[0].Loops[0].Joints));
        Assert.Equal(before.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)),after.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)));
        Assert.Equal(group.Sources[0].Select(s=>s.Entity),after.RegionOffsets[0].Sources[0].Select(s=>s.Entity));
        var target=ParameterTarget.SketchRegion(before.Id,group.Id);var parameter=new CadParameter(Guid.NewGuid(),"Distance",ParameterDimension.Length,reference?"":"0.1 mm",reference?target:null);
        var book=new ParameterBook([parameter],reference?[]:[new(Guid.NewGuid(),parameter.Id,target)]);
        var a=doc with{Sketches=doc.Sketches.Add(before.Id,before),Parameters=book};var b=a with{Sketches=a.Sketches.SetItem(before.Id,after)};
        a.Validate();b.Validate();Assert.Throws<CadValidationException>(()=>SketchRegionParameterTargets.GuardStructure(a,b));
        var detached=b with{Parameters=ParameterBook.Empty};SketchRegionParameterTargets.GuardStructure(a,detached);
    }
    [Fact] public async Task SubToleranceParameterForgeryChangesSameValuesAndFailsIndependentSaveReplayAtomically()
    {
        using var files=new TestFiles();var(doc,part)=SketchCommandTests.Document();var sketch=Rectangle(SketchRegionAuthorJoin.Miter) with{PartId=part};
        doc=doc with{Sketches=doc.Sketches.Add(sketch.Id,sketch)};var group=sketch.RegionOffsets[0];var loop=group.Loops[0];var joint=loop.Joints[0];
        var changed=group with{Loops=[loop with{Joints=loop.Joints.SetItem(0,joint with{PreviousParameter=joint.PreviousParameter+1e-10})}]};
        Assert.True(SketchRegionJointAuthors.SameStructure(loop.Joints,changed.Loops[0].Joints));Assert.False(SketchRegionAuthorModel.SameValues(group,changed));
        var forged=sketch with{RegionOffsets=[changed]};forged.Validate();Assert.Throws<CadValidationException>(()=>Kernel.ConfirmExpressionRegions(forged));
        var assets=new MemoryAssetStore();var storage=new CadDocumentStorage(regionVerifier:Kernel);string path=files.PathFor("original.cadoryx");
        await storage.SaveAsync(doc,assets,path);var original=File.ReadAllBytes(path);
        await Assert.ThrowsAsync<CadValidationException>(()=>storage.SaveAsync(doc with{Sketches=doc.Sketches.SetItem(sketch.Id,forged)},assets,path));
        Assert.Equal(original,File.ReadAllBytes(path));Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("missing")][InlineData("null")][InlineData("legacy")][InlineData("kind")][InlineData("oversized")]
    public void CurrentJsonRequiresItsCompleteConnectionFieldAndRejectsMasqueradedOwnership(string fault)
    {
        var sketch=Rectangle(SketchRegionAuthorJoin.Round);var json=JsonNode.Parse(SketchRegionSectionStorage.EncodeJson([sketch]))!;
        var group=json["sketches"]![0]!["groups"]!;var owner=group[0]!;var loop=owner["loops"]![0]!;
        if(fault=="missing")loop.AsObject().Remove("joints");if(fault=="null")loop["joints"]=null;
        if(fault=="legacy")owner["algorithm"]=SketchRegionJointAuthors.LegacyAlgorithm;
        if(fault=="kind")loop["joints"]![0]!["kind"]=99;
        if(fault=="oversized"){var row=loop["joints"]![0]!.DeepClone();loop["joints"]=new JsonArray(Enumerable.Range(0,257).Select(_=>row.DeepClone()).ToArray());}
        var registry=ImmutableDictionary<SketchId,CadSketch>.Empty.Add(sketch.Id,sketch with{RegionOffsets=[]});
        Assert.Throws<InvalidDataException>(()=>SketchRegionSectionStorage.AttachJson(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()),registry));Assert.Empty(registry[sketch.Id].RegionOffsets);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task NewConnectionAuthorsCannotMasqueradeAsDocument59OrOldRegionSection(bool section)
    {
        using var files=new TestFiles();var(doc,part)=SketchCommandTests.Document();var sketch=Capsule(part);doc=doc with{Sketches=doc.Sketches.Add(sketch.Id,sketch)};
        var assets=new MemoryAssetStore();var storage=new CadDocumentStorage(regionVerifier:Kernel);string path=files.PathFor("new.cadoryx");await storage.SaveAsync(doc,assets,path);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind==(section?"sketch-regions":"document")?s with{SchemaVersion=section?2:59}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("new-field")][InlineData("new-algorithm")]
    public void OldJsonCannotCarryNewConnectionSemanticsUnderItsLegacyVersion(string fault)
    {
        var bytes=File.ReadAllBytes(Fixture("miter","json"));var json=JsonNode.Parse(bytes)!;var group=json["sketches"]![0]!["groups"]![0]!;
        if(fault=="new-field")group["loops"]![0]!["joints"]=new JsonArray();else group["algorithm"]=SketchRegionJointAuthors.CurrentAlgorithm;
        var input=new SectionPayload(new("sketch-regions",1,"json"),System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()));var target=CadSectionMigrationRegistry.CurrentFormats["sketch-regions"];
        Assert.Throws<InvalidDataException>(()=>new CadSectionMigrationRegistry().Migrate([input],new Dictionary<string,SectionFormat>{{target.Kind,target}}));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ProductionInspectionReturnsRecordedConnectionsAndKeepsLegacyUnknownExplicit(bool legacy)
    {
        var assets=new MemoryAssetStore();DocumentSnapshot doc;CadSketch sketch;
        if(legacy)
        {using var loaded=await new CadDocumentStorage(regionVerifier:Kernel).LoadAsync(Fixture("tangent-capsule","cadoryx"),assets);doc=loaded.Snapshot;sketch=doc.Sketches.Values.Single();}
        else{var pair=SketchCommandTests.Document();sketch=Capsule(pair.Item2);doc=pair.Item1 with{Sketches=pair.Item1.Sketches.Add(sketch.Id,sketch)};}
        await using var session=new CadDocumentSession(doc,assets,Kernel,new InlineSessionDispatcher());var document=new CadDocumentViewModel(session,Kernel,new CadMessageLog());
        try
        {
            var tools=new CadAgentToolset(new CadCommandLineService(),document);
            using var read=JsonDocument.Parse(await tools.ExecuteAsync(new("branch-read","cad_inspect",JsonSerializer.Serialize(new{area="sketch_geometry",id=sketch.Id.Value})),default));
            var group=read.RootElement.GetProperty("items")[0].GetProperty("regionAuthors")[0];var expected=sketch.RegionOffsets[0];
            Assert.Equal(expected.Algorithm,group.GetProperty("algorithm").GetString());var joints=group.GetProperty("loops")[0].GetProperty("joints");
            Assert.Equal(legacy?0:4,joints.GetArrayLength());
            for(int i=0;i<joints.GetArrayLength();i++)
            {
                var actual=joints[i];var joint=expected.Loops[0].Joints[i];
                Assert.Equal(joint.PreviousSource.Value,actual.GetProperty("previousSource").GetGuid());Assert.Equal(joint.NextSource.Value,actual.GetProperty("nextSource").GetGuid());
                Assert.Equal(joint.Kind,actual.GetProperty("kind").Deserialize<SketchRegionJointKind>(CadJson.Options));
                Assert.Equal(joint.PreviousParameter,actual.GetProperty("previousParameter").GetDouble());Assert.Equal(joint.NextParameter,actual.GetProperty("nextParameter").GetDouble());
            }
            Assert.Same(doc,session.Snapshot);Assert.False(session.CanUndo);
        }
        finally{document.Detach();}
        Assert.Equal(0,assets.Count);
    }
    internal static string Fixture(string label,string category)
    {
        string root=Path.Combine(AppContext.BaseDirectory,"Fixtures","Storage","RegionConnectionsLegacy");
        using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"manifest.json")));
        var row=manifest.RootElement.GetProperty("cases").EnumerateArray().Single(v=>v.GetProperty("label").GetString()==label).GetProperty(category);
        string path=Path.Combine(root,row.GetProperty("file").GetString()!);Assert.Equal(row.GetProperty("sha256").GetString(),Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));return path;
    }
}
