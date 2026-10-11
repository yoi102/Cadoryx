using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Rendering.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using MessagePack;
using OcctSharp;
using Xunit;

namespace Cadoryx.Tests;

public sealed class RevolveAxisTests
{
    private static RevolveAxisReference Reference(CadDatumAxis axis)=>new(axis.Frame.Translation,axis.Frame.Rotation.Rotate(Vector3d.UnitZ),axis.Id,axis.Revision);
    private static double TorusVolume(double major,double minor=1)=>2*Math.PI*Math.PI*major*minor*minor;
    [Theory][InlineData(false)][InlineData(true)]
    public async Task PartLocalTranslatedTiltedAxisRevolvesExactCircleAndDirectHandle(bool reverse)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        var placement=new RigidTransform3d(new(7,11,13),Quaterniond.FromEulerDegrees(20,35,17));
        var direction=new Vector3d(1,0,1).Normalized()*(reverse?-1:1);var point=new Vector3d(2,0,-3);
        var recipe=new RevolveRecipe(SketchProfile.FromCircle(new(9,-10),1),Math.PI/2,placement)
        {Axis=new(placement.Apply(point),placement.Rotation.Rotate(direction))};
        double radius=Math.Sqrt(98);recipe.Validate();
        using(var result=await kernel.EvaluateAsync(recipe,assets))
        {
            Assert.Equal(BodyKind.Solid,result.Geometry.Kind);Assert.Equal(TorusVolume(radius)/4,result.Geometry.VolumeMm3,5);
            using var shape=OcctGeometryBridge.ReadShape(result.Geometry,assets);Assert.True(shape.GetValidationReport(true,true).IsValid);Assert.True(shape.GetSelfInterferenceReport().IsClear);
            var handle=Assert.Single(SolidDimensionHandles.Describe(recipe,RigidTransform3d.Identity));
            var local=placement.Inverse().Apply(handle.WorldPoint);var line=recipe.Axis.InMeridian(placement);
            var delta=local-line.Point;var radial=delta-line.Direction*delta.Dot(line.Direction);
            Assert.InRange(radial.Length,radius-.01-1, radius+1.01);Assert.InRange(Math.Abs(handle.WorldAxis.Dot(placement.Rotation.Rotate(line.Direction))),0,1e-9);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task CustomAxisAcceptsEitherRadialSideAndDirectionControlsPartialSweep(bool reverse)
    {
        var assets=new MemoryAssetStore();var direction=Vector3d.UnitZ*(reverse?-1:1);
        var recipe=new RevolveRecipe(SketchProfile.FromCircle(new(-4,0),1),Math.PI,RigidTransform3d.Identity){Axis=new(new(2,0,0),direction)};
        using(var result=await new OcctGeometryKernel().EvaluateAsync(recipe,assets))
        {
            Assert.Equal(TorusVolume(6)/2,result.Geometry.VolumeMm3,5);
            if(reverse)Assert.InRange(result.Geometry.Bounds.Min.Y,-1e-5,1e-5);
            else Assert.InRange(result.Geometry.Bounds.Max.Y,-1e-5,1e-5);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public void CustomAxisRejectsCrossingArcExtremaTangentCircleNonCoplanarAndMalformedLines()
    {
        static void Reject(RevolveAxisReference axis,SketchProfile? profile=null)=>Assert.Throws<CadValidationException>(()=>
            new RevolveRecipe(profile??SketchProfile.FromCircle(new(10,0),1),Math.PI,RigidTransform3d.Identity){Axis=axis}.Validate());
        Reject(new(new(0,1,0),Vector3d.UnitZ));Reject(new(Vector3d.Zero,new(0,1,0)));Reject(new(Vector3d.Zero,new(0,0,2)));
        Reject(new(Vector3d.Zero,Vector3d.UnitZ,Guid.NewGuid()));Reject(new(Vector3d.Zero,Vector3d.UnitZ,Guid.Empty,Guid.NewGuid()));
        Reject(new(new(2,0,0),Vector3d.UnitZ),SketchProfile.FromCircle(new(3,0),1));
        Reject(new(new(2,0,0),Vector3d.UnitZ),new([new(1,0),new(3,0),new(3,4),new(1,4)]));
        Point2d At(double degrees)=>new(3+2*Math.Cos(degrees*Math.PI/180),2*Math.Sin(degrees*Math.PI/180));
        Reject(new(new(2,0,0),Vector3d.UnitZ),new([]){Arc=new(At(100),At(260),At(280))});
        // A boundary may terminate on the axis on either side, including when its first point is on the axis.
        new RevolveRecipe(new([new(2,0),new(0,0),new(0,3),new(2,3)]),Math.Tau,RigidTransform3d.Identity)
            {Axis=new(new(2,0,0),Vector3d.UnitZ)}.Validate();
    }
    [Fact] public async Task NamedAxisParameterAtomicallyUpdatesRevolveDrawingUndoReopenAndFurtherEdit()
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var axis=new CadDatumAxis(Guid.NewGuid(),part,"Spin",DatumAxisKind.Independent,Origin:new(2,0,0),Direction:Vector3d.UnitZ);
            await session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with{Axes=DatumBook.Empty.Axes.Add(axis.Id,axis)}));axis=session.Snapshot.Datums.Axes[axis.Id];
            await session.ExecuteAsync(new AddBodyCommand(new RevolveRecipe(SketchProfile.FromCircle(new(10,0),1),Math.Tau,RigidTransform3d.Identity){Axis=Reference(axis)},"Ring",part));
            var feature=session.Snapshot.Features.Values.Single();var sheet=TechnicalDrawingSheet.A4Landscape("Axis drawing");
            await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Front",DrawingViewKind.Front,new(100,100),1));
            var before=session.Snapshot;var driver=new CadParameter(Guid.NewGuid(),"offset",ParameterDimension.Length,"4 mm");
            await session.ExecuteAsync(new SetParametersCommand(new([driver],[new(Guid.NewGuid(),driver.Id,ParameterTarget.Datum(axis.Id,"X"))])));
            var after=session.Snapshot;Assert.Equal(TorusVolume(6),after.Features[feature.Id].Result.VolumeMm3,5);Assert.NotEqual(before.Features[feature.Id].Result.AssetId,after.Features[feature.Id].Result.AssetId);
            Assert.All(after.DrawingSheets.Values.SelectMany(s=>s.Views),v=>{Assert.Null(v.StaleReason);Assert.Equal(after.Features[feature.Id].Result.Revision,v.Sources.Single().Revision);});after.Validate();
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
            var path=files.PathFor("axis.cadoryx");var storage=new CadDocumentStorage(datumResolver:kernel);await session.SaveAsync(storage,path);
            using(var loaded=await storage.LoadAsync(path,assets))
            await using(var reopened=new CadDocumentSession(loaded.Snapshot,assets,kernel,new InlineSessionDispatcher()))
            {
                Assert.Equal(((RevolveRecipe)after.Features[feature.Id].Recipe).Axis,((RevolveRecipe)loaded.Snapshot.Features[feature.Id].Recipe).Axis);
                var book=reopened.Snapshot.Parameters with{Parameters=reopened.Snapshot.Parameters.Parameters.Select(p=>p with{Expression="5 mm"}).ToImmutableArray()};
                await reopened.ExecuteAsync(new SetParametersCommand(book));Assert.Equal(TorusVolume(5),reopened.Snapshot.Features[feature.Id].Result.VolumeMm3,5);reopened.Snapshot.Validate();
                var invalid=book with{Parameters=book.Parameters.Select(p=>p with{Expression="10 mm"}).ToImmutableArray()};var accepted=reopened.Snapshot;int count=assets.Count;
                await Assert.ThrowsAnyAsync<Exception>(()=>reopened.ExecuteAsync(new SetParametersCommand(invalid)));Assert.Same(accepted,reopened.Snapshot);Assert.Equal(count,assets.Count);
            }
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("circle")][InlineData("arc")][InlineData("mixed")][InlineData("polygon")]
    public async Task CustomAxisCompanionRoundTripsAllAnalyticalProfileKindsAndRejectsOldSchema(string kind)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        var profile=kind switch
        {
            "circle"=>SketchProfile.FromCircle(new(-4,0),1),
            "arc"=>new SketchProfile([]){Arc=new(new(-4,-1),new(-3,0),new(-4,1))},
            "mixed"=>new SketchProfile([]){BoundaryCurves=[new(new(-4,-1),new(-4,1),new(-3,0)),new(new(-4,1),new(-5,0)),new(new(-5,0),new(-4,-1))]},
            _=>new SketchProfile([new(-4,-1),new(-3,-1),new(-3,1),new(-4,1)])
        };
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new RevolveRecipe(profile,Math.PI,RigidTransform3d.Identity){Axis=new(new(2,0,0),Vector3d.UnitZ)},"Ring",part));
            var path=files.PathFor(kind+".cadoryx");var storage=new CadDocumentStorage();await session.SaveAsync(storage,path);int count=assets.Count;
            using(var loaded=await storage.LoadAsync(path,assets))
            {
                loaded.Snapshot.Validate();var recipe=(RevolveRecipe)loaded.Snapshot.Features.Values.Single().Recipe;
                using var result=await kernel.EvaluateAsync(recipe,assets);Assert.Equal(session.Snapshot.Features.Values.Single().Result.VolumeMm3,result.Geometry.VolumeMm3,5);
            }
            Assert.Equal(count,assets.Count);var manifest=FormatEvolutionTests.Manifest(path);Assert.Equal(CadSectionMigrationRegistry.CurrentFormats["features"].Version,manifest.Sections.Single(s=>s.Kind=="features").SchemaVersion);Assert.Contains("cadoryx.revolve-axis.1",manifest.RequiredCapabilities);
            FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=27}:s).ToImmutableArray()});
            await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(count,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task ProductionEditorPreservesFrozenCircleAndAxisDetachCancelConfirmAndDirection()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var axis=new CadDatumAxis(Guid.NewGuid(),part,"Spin",DatumAxisKind.Independent,Origin:new(2,0,0),Direction:Vector3d.UnitZ,Reverse:true);
            await session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with{Axes=DatumBook.Empty.Axes.Add(axis.Id,axis)}));axis=session.Snapshot.Datums.Axes[axis.Id];
            var recipe=new RevolveRecipe(SketchProfile.FromCircle(new(-4,0),1),Math.PI,RigidTransform3d.Identity){Axis=Reference(axis)};
            await session.ExecuteAsync(new AddBodyCommand(recipe,"Ring",part));var feature=session.Snapshot.Features.Values.Single();var baseline=session.Snapshot;
            var vm=new CadDocumentViewModel(session,kernel,new CadMessageLog());
            try
            {
                vm.EditFeature(feature.Id);Assert.Equal(axis.Id,vm.SelectedRevolveAxis?.Id);Assert.False(vm.CanEditProfilePoints);Assert.True(vm.CanDetachRevolveAxis);
                await vm.PreviewCommand.ExecuteAsync(null);Assert.True(vm.HasPreview,vm.ToolStatus);vm.DetachRevolveAxisCommand.Execute(null);Assert.False(vm.HasPreview);Assert.Null(vm.SelectedRevolveAxis?.Id);
                await vm.PreviewCommand.ExecuteAsync(null);Assert.True(vm.HasPreview,vm.ToolStatus);vm.CancelCommand.Execute(null);Assert.Same(baseline,session.Snapshot);
                await vm.PreviewCommand.ExecuteAsync(null);await vm.ConfirmCommand.ExecuteAsync(null);var after=session.Snapshot;
                var detached=(RevolveRecipe)after.Features[feature.Id].Recipe;Assert.Null(detached.Axis!.Datum);Assert.Equal(recipe.Axis!.Direction,detached.Axis.Direction);Assert.Equal(recipe.Profile.Circle,detached.Profile.Circle);Assert.Equal(feature.Result.VolumeMm3,after.Features[feature.Id].Result.VolumeMm3,5);
                await session.UndoAsync();Assert.Same(baseline,session.Snapshot);await session.RedoAsync();vm.EditFeature(feature.Id);
                await vm.PreviewCommand.ExecuteAsync(null);Assert.True(vm.HasPreview,vm.ToolStatus);Assert.Equal(detached.Axis,((RevolveRecipe)vm.SolidHandleRecipe()!).Axis);
                vm.CancelCommand.Execute(null);vm.UseLocalRevolveAxisCommand.Execute(null);await vm.PreviewCommand.ExecuteAsync(null);Assert.False(vm.HasPreview);Assert.Same(after,session.Snapshot);
                vm.StartTool("Revolve");Assert.Null(vm.SelectedRevolveAxis?.Id);Assert.True(vm.CanEditProfilePoints);await vm.PreviewCommand.ExecuteAsync(null);Assert.True(vm.HasPreview,vm.ToolStatus);vm.CancelCommand.Execute(null);
            }
            finally{vm.Detach();}
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task ForeignAxisAndTamperedCacheCannotEnterDocumentOrBypassDrivenEdit()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var axis=new CadDatumAxis(Guid.NewGuid(),part,"Spin",DatumAxisKind.Basis);
            await session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with{Axes=DatumBook.Empty.Axes.Add(axis.Id,axis)}));axis=session.Snapshot.Datums.Axes[axis.Id];
            var recipe=new RevolveRecipe(SketchProfile.FromCircle(new(10,0),1),Math.Tau,RigidTransform3d.Identity){Axis=Reference(axis)};
            await session.ExecuteAsync(new AddBodyCommand(recipe,"Ring",part));var feature=session.Snapshot.Features.Values.Single();var before=session.Snapshot;
            var tampered=recipe with{Axis=recipe.Axis! with{Point=new(2,0,0)}};
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new RecomputeCommand(feature.Id,tampered)));Assert.Same(before,session.Snapshot);
            var altered=before with{Features=before.Features.SetItem(feature.Id,feature with{Recipe=tampered})};Assert.Throws<CadValidationException>(altered.Validate);
            var foreign=recipe with{Axis=recipe.Axis! with{Datum=Guid.NewGuid()}};
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new AddBodyCommand(foreign,"Foreign",part)));Assert.Same(before,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task ChangedExactCylinderSourceFreezesRevolveAndExplicitReselectionRestoresSameIdentity()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(2,6,RigidTransform3d.Identity),"Source",part));var source=session.Snapshot.Features.Values.Single();
            async Task<ExactDatumGeometry> Pick()
            {
                var current=session.Snapshot.Features[source.Id];using var shape=OcctGeometryBridge.ReadShape(current.Result,assets);using var snapshot=RepairSnapshot.Create(shape);
                var item=snapshot.Topology.Single(t=>{if(t.Kind!=ShapeKind.Face)return false;using var face=snapshot.CopySubshape(t.Selection);return face.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder;});
                return await kernel.ResolveDatumGeometryAsync(session.Snapshot,new(session.Snapshot.Id,current.Id,current.Result.Revision,current.Result.AssetId,snapshot.Fingerprint,item.Selection.Index,HistoryShapeKind.Face,OcctGeometryKernel.HistoryAdapterVersion),assets);
            }
            var axis=new CadDatumAxis(Guid.NewGuid(),part,"Cylinder axis",DatumAxisKind.Analytic,Exact:await Pick());
            await session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with{Axes=DatumBook.Empty.Axes.Add(axis.Id,axis)}));axis=session.Snapshot.Datums.Axes[axis.Id];
            await session.ExecuteAsync(new AddBodyCommand(new RevolveRecipe(SketchProfile.FromCircle(new(8,0),1),Math.Tau,RigidTransform3d.Identity){Axis=Reference(axis)},"Ring",part));var ring=session.Snapshot.Features.Values.Single(f=>f.Recipe is RevolveRecipe);var original=session.Snapshot;
            await session.ExecuteAsync(new RecomputeCommand(source.Id,new CylinderRecipe(2,9,RigidTransform3d.Identity)));var frozen=session.Snapshot;
            Assert.True(frozen.Datums.Axes[axis.Id].IsStale);Assert.True(frozen.Features[ring.Id].IsStale);Assert.Equal(ring.Result,frozen.Features[ring.Id].Result);frozen.Validate();
            var frozenRecipe=(RevolveRecipe)frozen.Features[ring.Id].Recipe;
            Assert.Throws<CadValidationException>((frozen with{Features=frozen.Features.SetItem(ring.Id,frozen.Features[ring.Id] with{Recipe=frozenRecipe with{Axis=frozenRecipe.Axis! with{Point=new(2,0,0)}}})}).Validate);
            await session.ExecuteAsync(new SetDatumsCommand(frozen.Datums with{Axes=frozen.Datums.Axes.SetItem(axis.Id,frozen.Datums.Axes[axis.Id] with{Exact=await Pick()})}));
            var restored=session.Snapshot;Assert.False(restored.Datums.Axes[axis.Id].IsStale);Assert.False(restored.Features[ring.Id].IsStale);Assert.Equal(ring.Id,restored.Features[ring.Id].Id);Assert.Equal(ring.Result.VolumeMm3,restored.Features[ring.Id].Result.VolumeMm3,5);restored.Validate();
            await session.UndoAsync();Assert.Same(frozen,session.Snapshot);await session.UndoAsync();Assert.Same(original,session.Snapshot);await session.RedoAsync();await session.RedoAsync();Assert.Same(restored,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("foreign-kind")][InlineData("bad-direction")][InlineData("bad-cache")]
    public async Task InvalidPersistedAxisCompanionFailsWithoutRetainingAssets(string mutation)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var axis=new CadDatumAxis(Guid.NewGuid(),part,"Spin",DatumAxisKind.Basis);await session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with{Axes=DatumBook.Empty.Axes.Add(axis.Id,axis)}));axis=session.Snapshot.Datums.Axes[axis.Id];
            await session.ExecuteAsync(new AddBodyCommand(new RevolveRecipe(SketchProfile.FromCircle(new(10,0),1),Math.Tau,RigidTransform3d.Identity){Axis=Reference(axis)},"Ring",part));
            var path=files.PathFor("invalid.cadoryx");var storage=new CadDocumentStorage();await session.SaveAsync(storage,path);int count=assets.Count;
            FormatEvolutionTests.RewriteSection(path,"features",bytes=>
            {
                var pack=MessagePackSerializer.Deserialize<PackFeaturesV3>(bytes,MessagePackSerializerOptions.Standard);var f=pack.Features[0];var r=f.Recipe;
                r=mutation switch
                {
                    "foreign-kind"=>r with{Kind="box",Numbers=[1,2,3],Profile=[]},
                    "bad-direction"=>r with{RevolveAxis=r.RevolveAxis! with{Direction=[0,0,2]}},
                    _=>r with{RevolveAxis=r.RevolveAxis! with{Point=[2,0,0]}}
                };
                return MessagePackSerializer.Serialize(pack with{Features=[f with{Recipe=r}]},MessagePackSerializerOptions.Standard);
            });
            await Assert.ThrowsAnyAsync<Exception>(()=>storage.LoadAsync(path,assets));Assert.Equal(count,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }
}
