using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using OcctSharp;
using Xunit;

namespace Cadoryx.Tests;

public sealed class ImportedMomentInspectionTests
{
    [Theory]
    [InlineData("minipc3-many-faces-solid.brep",5277.5345493580207,1e-5)]
    [InlineData("minipc3-difficult-trim-solid.brep",223.84908567032869,1e-7)]
    public async Task OriginalDetailedImportedSolidKeepsIndependentMassThroughStorageAndHistory(string fixture,double reference,double tolerance)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();using var files=new TestFiles();
        var (initial,part)=SketchCommandTests.Document();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            using(var source=ShapeExchange.ReadBrep(ExchangeFixtureTests.Fixture(fixture)))
            using(var before=RepairSnapshot.Create(source))
            using(var stored=OcctGeometryBridge.StoreShape(source,assets))
            {
                Assert.InRange(Math.Abs(stored.Geometry.VolumeMm3-reference),0,tolerance);
                await session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),fixture,part));
                using var after=RepairSnapshot.Create(source);Assert.Equal(before.Fingerprint,after.Fingerprint);
            }
            var feature=Assert.Single(session.Snapshot.Features.Values);
            await session.ExecuteAsync(new RecomputeCommand(feature.Id,feature.Recipe));
            var storage=new CadDocumentStorage();string path=files.PathFor("detailed.cadoryx");
            await session.SaveAsync(storage,path);
            using var restored=await storage.LoadAsync(path,assets);
            Assert.InRange(Math.Abs(Assert.Single(restored.Snapshot.Features.Values).Result.VolumeMm3-reference),0,tolerance);
            await session.UndoAsync();await session.RedoAsync();session.Snapshot.Validate();
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact]
    public async Task OriginalImportedSmallSolidKeepsAccurateMassThroughProductionStoreRecomputeAndReopen()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();using var files=new TestFiles();
        var (initial,part)=SketchCommandTests.Document();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            using(var source=ShapeExchange.ReadBrep(ExchangeFixtureTests.Fixture("minipc3-moment-cancellation-solid.brep")))
            using(var before=RepairSnapshot.Create(source))
            using(var stored=OcctGeometryBridge.StoreShape(source,assets))
            {
                Assert.InRange(Math.Abs(stored.Geometry.VolumeMm3-.18952421132937064),0,1e-8);
                await session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),"Original imported solid",part));
                using var after=RepairSnapshot.Create(source);Assert.Equal(before.Fingerprint,after.Fingerprint);
            }
            var feature=Assert.Single(session.Snapshot.Features.Values);
            Assert.InRange(Math.Abs(feature.Result.VolumeMm3-.18952421132937064),0,1e-8);
            await session.ExecuteAsync(new RecomputeCommand(feature.Id,feature.Recipe));
            var storage=new CadDocumentStorage();string path=files.PathFor("original.cadoryx");
            await session.SaveAsync(storage,path);
            using var restored=await storage.LoadAsync(path,assets);
            var result=Assert.Single(restored.Snapshot.Features.Values).Result;
            Assert.InRange(Math.Abs(result.VolumeMm3-.18952421132937064),0,1e-8);
            using var shape=OcctGeometryBridge.ReadShape(result,assets);
            Assert.True(shape.IsValid);
            Assert.InRange(Math.Abs(shape.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10).Properties.Mass-.18952421132937064),0,1e-9);
            await session.UndoAsync();await session.RedoAsync();session.Snapshot.Validate();
        }
        Assert.Equal(0,assets.Count);
    }
}
