using System.Collections.Immutable;
using System.Security.Cryptography;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using MessagePack;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

public sealed partial class PlanarFaceEditingTests
{
    [Theory][InlineData(2,false,false)][InlineData(-2,false,false)][InlineData(2,true,false)][InlineData(-2,true,false)]
    [InlineData(2,false,true)][InlineData(-2,false,true)]
    public async Task SignedFiniteDomainRetainsHolesNativeDirectionAndIndependentSource(double distance,bool rotated,bool bore)
    {
        await using var f=await Fixture.Create(rotated:rotated,bores:bore?1:0);var recipe=await f.Push(distance);var before=f.Session.Snapshot;
        var fingerprint=f.Fingerprint();int count=f.Assets.Count;
        using(var preview=await f.Kernel.EvaluateAsync(recipe,f.Assets))
        {
            Assert.Equal((120-(bore?4*Math.PI:0))*(15+distance),preview.Geometry.VolumeMm3,4);
            Assert.True(preview.TopologyHistory is not null,string.Join("; ",preview.Diagnostics.Select(d=>d.Message))+(preview.TopologyHistory is null?RoundtripDiagnostic(f,recipe):""));Assert.Equal(1,preview.TopologyHistory!.ArgumentCount);
            Assert.Equal(fingerprint,preview.TopologyHistory.SourceFingerprint);
            using var shape=OcctGeometryBridge.ReadShape(preview.Geometry,f.Assets);Assert.True(shape.GetValidationReport(true,true).IsValid);Assert.True(shape.GetSelfInterferenceReport().IsClear);
            if(bore){var faces=shape.GetFaces();try{Assert.Single(faces,face=>face.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder);}finally{foreach(var face in faces)face.Dispose();}}
        }
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);Assert.Equal(fingerprint,f.Fingerprint());
        await f.Apply(recipe);var after=f.Session.Snapshot;after.Validate();Assert.Single(after.Bodies);Assert.Equal(f.Source.Result,after.Features[f.Source.Id].Result);
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
    }
    [Theory][InlineData(1)][InlineData(2)]
    public async Task SelectedNativeBoreFacesAreAllRemovedAndHealedToOneValidSolid(int bores)
    {
        await using var f=await Fixture.Create(bores:bores);var faces=f.SelectMany(s=>s.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder);
        Assert.Equal(bores,faces.Length);var before=f.Session.Snapshot;string fingerprint=f.Fingerprint();
        await f.Apply(new DefeatureRecipe(f.Source.Result,faces));var result=f.Edited;Assert.Equal(1800,result.Result.VolumeMm3,4);Assert.NotNull(result.TopologyHistory);
        using(var shape=OcctGeometryBridge.ReadShape(result.Result,f.Assets)){Assert.True(shape.GetValidationReport(true,true).IsValid);Assert.True(shape.GetSelfInterferenceReport().IsClear);Assert.Equal(1,shape.GetTopologySummary().UniqueCounts.SolidCount);}
        Assert.Equal(fingerprint,f.Fingerprint());var trace=await f.Kernel.TraceExactAsync(f.Session.Snapshot,faces[0],result.Id,f.Assets);Assert.Equal(HistoryResolutionStatus.Deleted,trace.Status);Assert.Null(trace.Target);
        var after=f.Session.Snapshot;await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
    }
    [Fact]public async Task BlindBoreRequiresWallAndFloorAsOneExplicitRemovalSet()
    {
        await using var f=await Fixture.Create(bores:1,blind:true);
        var faces=f.SelectMany(s=>s.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder||Math.Abs(s.GetBoundingBox().Minimum.Z-5)<1e-5&&Math.Abs(s.GetBoundingBox().Maximum.Z-5)<1e-5);
        Assert.Equal(2,faces.Length);await f.Apply(new DefeatureRecipe(f.Source.Result,faces));Assert.Equal(1800,f.Edited.Result.VolumeMm3,4);
    }
    [Theory][InlineData(0)][InlineData(1e-7)][InlineData(double.NaN)][InlineData(double.PositiveInfinity)][InlineData(100001)][InlineData(-20)]
    public async Task InvalidDistanceOrCompleteRemovalIsAtomic(double distance)
    {
        await using var f=await Fixture.Create();var recipe=await f.Push(2);await f.Reject(recipe with{Distance=distance});
    }
    [Theory][InlineData("normal")][InlineData("opposite")][InlineData("fingerprint")][InlineData("revision")][InlineData("foreign")][InlineData("kind")][InlineData("index")]
    public async Task ForgedFaceOrDirectionCannotPublishOrLeak(string mutation)
    {
        await using var f=await Fixture.Create();var recipe=await f.Push(2);var face=recipe.Face;
        recipe=mutation switch
        {
            "normal"=>recipe with{OutwardNormal=new(1,0,0)},"opposite"=>recipe with{OutwardNormal=recipe.OutwardNormal*-1},
            "fingerprint"=>recipe with{Face=face with{Fingerprint=new string('0',64)}},"revision"=>recipe with{Face=face with{Revision=GeometryRevisionId.New()}},
            "foreign"=>recipe with{Face=face with{DocumentId=DocumentId.New()}},"kind"=>recipe with{Face=face with{Kind=HistoryShapeKind.Edge}},
            _=>recipe with{Face=face with{FullTopologyIndex=100000}}
        };
        await f.Reject(recipe);
    }
    [Fact]public async Task CurvedFaceIsRejectedByBothDiscoveryAndRecipeConfirmation()
    {
        await using var f=await Fixture.Create(bores:1);var wall=f.SelectMany(s=>s.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder).Single();
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Kernel.InspectPlanarFaceAsync(f.Session.Snapshot,wall,f.Assets));
        await f.Reject(new PlanarPushPullRecipe(f.Source.Result,wall,2,Vector3d.UnitZ));
    }
    [Theory][InlineData("empty")][InlineData("duplicate")][InlineData("outer-face")][InlineData("partial-set")]
    public async Task InvalidUnhealableOrPartlyIgnoredRemovalLeavesExactSourceAndHistoryIntact(string mode)
    {
        await using var f=await Fixture.Create(bores:1);var wall=f.SelectMany(s=>s.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder).Single();var top=f.Top();
        var faces=mode switch{"empty"=>ImmutableArray<ExactTopologySelection>.Empty,"duplicate"=>[wall,wall],"outer-face"=>[top],_=>ImmutableArray.Create(wall,top)};
        await f.Reject(new DefeatureRecipe(f.Source.Result,faces));
    }
    [Fact]public async Task UnchangedOriginalBottomHasReplayedNativeHistoryAfterPushPullAndReopen()
    {
        using var files=new TestFiles();await using var f=await Fixture.Create();var bottom=f.SelectMany(s=>Math.Abs(s.GetBoundingBox().Maximum.Z)<1e-5).Single();
        await f.Apply(await f.Push(-2));var target=f.Edited.Id;var h=f.Edited.TopologyHistory!;
        Assert.Equal(f.Source.Result.Revision,h.SourceRevision);Assert.All(h.Entries,e=>Assert.Equal(0,e.SourceArgument));
        var trace=await f.Kernel.TraceExactAsync(f.Session.Snapshot,bottom,target,f.Assets);Assert.Equal(HistoryResolutionStatus.Resolved,trace.Status);Assert.NotNull(trace.Target);
        var path=files.PathFor("history.cadoryx");var storage=new CadDocumentStorage(faceEditResolver:f.Kernel);await f.Session.SaveAsync(storage,path);
        using var loaded=await storage.LoadAsync(path,f.Assets);Assert.Equal(trace,await f.Kernel.TraceExactAsync(loaded.Snapshot,bottom,target,f.Assets));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task UpstreamChangeFreezesAuthoredFacesUntilExplicitCurrentReselection(bool removal)
    {
        using var files=new TestFiles();await using var f=await Fixture.Create(bores:removal?1:0);
        GeometryRecipe recipe=removal?new DefeatureRecipe(f.Source.Result,f.SelectMany(s=>s.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder)):await f.Push(2);
        await f.Apply(recipe);var feature=f.Edited;var reference=f.SelectMany(s=>Math.Abs(s.GetBoundingBox().Maximum.Z)<1e-5).First();
        await f.Session.ExecuteAsync(new RecomputeCommand(f.Source.Id,removal?f.Source.Recipe:new BoxRecipe(11,12,15,RigidTransform3d.Identity)));
        // An imported source's identical recipe does not change identity, so removal uses an independently stored replacement.
        if(removal)
        {
            using var original=OcctGeometryBridge.ReadShape(f.Source.Result,f.Assets);using var moved=original.Transformed(ShapeTransform.CreateTranslation(1,0,0));using var stored=OcctGeometryBridge.StoreShape(moved,f.Assets);
            await f.Session.ExecuteAsync(new RecomputeCommand(f.Source.Id,new ImportedRecipe(stored.Geometry)));
        }
        var frozen=f.Session.Snapshot;Assert.True(frozen.Features[feature.Id].IsStale);Assert.Equal(feature.Result,frozen.Features[feature.Id].Result);
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new ReselectFaceFeatureCommand(feature.Id,recipe)));Assert.Same(frozen,f.Session.Snapshot);
        var path=files.PathFor("frozen.cadoryx");var storage=new CadDocumentStorage(faceEditResolver:f.Kernel);await f.Session.SaveAsync(storage,path);
        using(var loaded=await storage.LoadAsync(path,f.Assets)){Assert.True(loaded.Snapshot.Features[feature.Id].IsStale);Assert.Equal(FaceFeatureRecipes.Source(recipe),FaceFeatureRecipes.Source(loaded.Snapshot.Features[feature.Id].Recipe));}
        var current=f.CurrentSource;recipe=removal?new DefeatureRecipe(current.Result,f.SelectMany(s=>s.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder)):await f.Push(2);
        await f.Session.ExecuteAsync(new ReselectFaceFeatureCommand(feature.Id,recipe));Assert.False(f.Session.Snapshot.Features[feature.Id].IsStale);
        Assert.Equal(removal?1800:2244,f.Session.Snapshot.Features[feature.Id].Result.VolumeMm3,4);await f.Session.UndoAsync();Assert.Same(frozen,f.Session.Snapshot);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task RoundtripRetainsExactFaceProgramsAndRequiresNativeResolver(bool removal)
    {
        using var files=new TestFiles();await using var f=await Fixture.Create(bores:removal?1:0);GeometryRecipe recipe=removal?new DefeatureRecipe(f.Source.Result,f.SelectMany(s=>s.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder)):await f.Push(-2);
        await f.Apply(recipe);var path=files.PathFor("face.cadoryx");var storage=new CadDocumentStorage(faceEditResolver:f.Kernel);await f.Session.SaveAsync(storage,path);int count=f.Assets.Count;
        using(var loaded=await storage.LoadAsync(path,f.Assets)){loaded.Snapshot.Validate();Assert.Equal(FaceFeatureRecipes.Faces(recipe).ToArray(),FaceFeatureRecipes.Faces(loaded.Snapshot.Features[f.Edited.Id].Recipe).ToArray());}
        Assert.Equal(count,f.Assets.Count);await Assert.ThrowsAsync<NotSupportedException>(()=>new CadDocumentStorage().LoadAsync(path,f.Assets));Assert.Equal(count,f.Assets.Count);
        Assert.Contains("cadoryx.face-editing.1",FormatEvolutionTests.Manifest(path).RequiredCapabilities);
    }
    [Theory][InlineData("legacy")][InlineData("kind")][InlineData("companion")][InlineData("normal")][InlineData("fingerprint")][InlineData("distance")][InlineData("two-faces")]
    public async Task TamperedStoredFaceProofOrFamilyRejectsBeforeAssetPublication(string mutation)
    {
        using var files=new TestFiles();await using var f=await Fixture.Create();await f.Apply(await f.Push(2));var path=files.PathFor("bad.cadoryx");var storage=new CadDocumentStorage(faceEditResolver:f.Kernel);await f.Session.SaveAsync(storage,path);int count=f.Assets.Count;
        if(mutation=="legacy")FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=37}:s).ToImmutableArray()});
        else FormatEvolutionTests.RewriteSection(path,"features",bytes=>
        {
            var data=MessagePackSerializer.Deserialize<PackFeaturesV3>(bytes);
            return MessagePackSerializer.Serialize(data with{Features=data.Features.Select(feature=>
            {
                if(feature.Recipe.FaceEdit is not {} edit)return feature;var r=feature.Recipe;
                r=mutation switch{"kind"=>r with{Kind="defeature"},"companion"=>r with{FaceFeature=new()},"normal"=>r with{FaceEdit=edit with{Normal=[1,0,0]}},
                    "fingerprint"=>r with{FaceEdit=edit with{Faces=[edit.Faces[0] with{Fingerprint=new string('0',64)}]}},"distance"=>r with{FaceEdit=edit with{Distance=double.NaN}},
                    _=>r with{FaceEdit=edit with{Faces=[edit.Faces[0],edit.Faces[0]]}}};return feature with{Recipe=r};
            }).ToArray()});
        });
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,f.Assets));Assert.Equal(count,f.Assets.Count);
    }
    [Fact]public async Task ValidPreviousSchemasMigrateThroughFixedIntermediateVersions()
    {
        using var files=new TestFiles();await using var f=await Fixture.Create();var path=files.PathFor("old.cadoryx");var storage=new CadDocumentStorage();await f.Session.SaveAsync(storage,path);
        // Emulate the actual legacy four-field point payload before declaring its document version.
        SketchLegacyPointFixture.Prepare(path);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind is "document" or "features"?s with{SchemaVersion=s.Kind=="document"?41:37}:s).ToImmutableArray()});
        using var loaded=await storage.LoadAsync(path,f.Assets);loaded.Snapshot.Validate();Assert.Equal(1800,loaded.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3,4);
    }
    private static string RoundtripDiagnostic(Fixture f,PlanarPushPullRecipe recipe)
    {
        using var files=new TestFiles();using var shape=OcctGeometryBridge.ReadShape(recipe.Source,f.Assets);using var source=RepairSnapshot.Create(shape);var faces=shape.GetFaces();
        try
        {
            var face=faces.Single(s=>RepairSnapshot.FindTopologyIndex(shape,s)==recipe.Face.FullTopologyIndex);var d=recipe.OutwardNormal*recipe.Distance;
            using var vector=GpVec.Create(d.X,d.Y,d.Z);using var prism=face.Extrude(vector);using var tool=RepairSnapshot.Create(prism);
            using var operation=BooleanHistoryModeling.Build(recipe.Distance>0?TopologyBooleanOperation.Fuse:TopologyBooleanOperation.Cut,[source,tool]);
            using var stored=OcctGeometryBridge.StoreShape(operation.RequireShape(),f.Assets);using var reopened=OcctGeometryBridge.ReadShape(stored.Geometry,f.Assets);
            using var live=RepairSnapshot.Create(operation.RequireShape());using var restored=RepairSnapshot.Create(reopened);
            string Canonical(Shape item,string name){var path=files.PathFor(name+".brep");ShapeExchange.WriteBrep(item,path);for(int i=0;i<4;i++){using var normalized=ShapeExchange.ReadBrep(path);ShapeExchange.WriteBrep(normalized,path);}return File.ReadAllText(path);}
            using var a=live.CopySubshape(live.Select(0));using var b=restored.CopySubshape(restored.Select(0));var x=Canonical(a,"live").Split('\n');var y=Canonical(b,"restored").Split('\n');
            return "\nCanonical differences:\n"+string.Join("\n",Enumerable.Range(0,Math.Max(x.Length,y.Length)).Where(i=>x.ElementAtOrDefault(i)!=y.ElementAtOrDefault(i)).Select(i=>$"{i+1}: {x.ElementAtOrDefault(i)} => {y.ElementAtOrDefault(i)}").Take(30));
        }
        finally{foreach(var face in faces)face.Dispose();}
    }
    private sealed class Fixture:IAsyncDisposable
    {
        internal MemoryAssetStore Assets{get;}=new();internal OcctGeometryKernel Kernel{get;}=new();internal CadDocumentSession Session{get;}
        internal FeatureDefinition Source{get;private set;}=null!;internal FeatureDefinition CurrentSource=>Session.Snapshot.Features[Source.Id];
        internal CadDocumentViewModel Document{get;}
        internal FeatureDefinition Edited=>Session.Snapshot.Features.Values.Single(p=>p.Recipe is PlanarPushPullRecipe or DefeatureRecipe);
        private readonly bool rotated;
        private Fixture(bool rotated){this.rotated=rotated;Session=new(DocumentSnapshot.Create("Planar faces"),Assets,Kernel,new InlineSessionDispatcher());Document=new(Session,Kernel,new CadMessageLog());}
        internal static async Task<Fixture> Create(bool rotated=false,int bores=0,bool blind=false)
        {
            var f=new Fixture(rotated);
            if(!rotated&&bores==0)await f.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Box"));
            else
            {
                using var box=ShapeFactory.CreateBox(10,12,15);Shape shape=box.Clone();
                try
                {
                    for(int i=0;i<bores;i++){using var cylinder=ShapeFactory.CreateCylinder(2,blind?11:17);using var tool=cylinder.Transformed(ShapeTransform.CreateTranslation(bores==1?5:i==0?2.5:7.5,6,blind?5:-1));var next=shape.Cut(tool);shape.Dispose();shape=next;}
                    if(rotated){var next=shape.Transformed(new ShapeTransform(0,0,0,1,0,0,.37));shape.Dispose();shape=next;}
                    using var stored=OcctGeometryBridge.StoreShape(shape,f.Assets);await f.Session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),"Source"));
                }
                finally{shape.Dispose();}
            }
            f.Source=f.Session.Snapshot.Features.Values.Single();return f;
        }
        internal string Fingerprint(){using var shape=OcctGeometryBridge.ReadShape(Source.Result,Assets);using var map=RepairSnapshot.Create(shape);return map.Fingerprint;}
        internal ImmutableArray<ExactTopologySelection> SelectMany(Func<Shape,bool> predicate,FeatureDefinition? feature=null,ShapeKind kind=ShapeKind.Face)
        {
            var source=feature??CurrentSource;using var shape=OcctGeometryBridge.ReadShape(source.Result,Assets);using var map=RepairSnapshot.Create(shape);var selected=ImmutableArray.CreateBuilder<ExactTopologySelection>();
            foreach(var item in map.Topology.Where(t=>t.Kind==kind)){using var sub=map.CopySubshape(item.Selection);if(predicate(sub))selected.Add(new(Session.Snapshot.Id,source.Id,source.Result.Revision,source.Result.AssetId,map.Fingerprint,item.Selection.Index,kind==ShapeKind.Edge?HistoryShapeKind.Edge:HistoryShapeKind.Face,OcctGeometryKernel.HistoryAdapterVersion));}
            return selected.ToImmutable();
        }
        internal ExactTopologySelection Top()=>SelectMany(s=>
        {
            var surface=s.GetFaceSurfaceSnapshot();if(surface.SurfaceType!=SurfaceGeometryType.Plane)return false;
            var p=s.EvaluateFace((surface.FirstUParameter+surface.LastUParameter)/2,(surface.FirstVParameter+surface.LastVParameter)/2);
            return Math.Abs(p.Normal.Z-(rotated?Math.Cos(.37):1))<1e-6&&Math.Abs(p.Normal.Y-(rotated?-Math.Sin(.37):0))<1e-6;
        }).Single();
        internal async Task<PlanarPushPullRecipe> Push(double distance){var face=Top();return new(CurrentSource.Result,face,distance,await Kernel.InspectPlanarFaceAsync(Session.Snapshot,face,Assets));}
        internal Task Apply(GeometryRecipe recipe)=>Session.ExecuteAsync(new AddFaceFeatureCommand(CurrentSource.OutputBodyId,recipe,"Face edit"));
        internal async Task Reject(GeometryRecipe recipe)
        {
            var before=Session.Snapshot;int count=Assets.Count;var history=Session.HistoryUsage;string fingerprint=Fingerprint();
            await Assert.ThrowsAnyAsync<Exception>(()=>Apply(recipe));Assert.Same(before,Session.Snapshot);Assert.Equal(count,Assets.Count);Assert.Equal(history,Session.HistoryUsage);Assert.Equal(fingerprint,Fingerprint());
        }
        public async ValueTask DisposeAsync(){Document.Detach();await Session.DisposeAsync();Assert.Equal(0,Assets.Count);}
    }
}
