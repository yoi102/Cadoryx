using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Rendering;
using Cadoryx.IO;
using Cadoryx.Commands;
using Cadoryx.Editor;
using OcctSharp;
using Xunit;

namespace Cadoryx.Tests;

public sealed class ExchangeFixtureTests
{
    public static string Fixture(string name)=>Path.Combine(AppContext.BaseDirectory,"Fixtures","Exchange",name);

    [Theory]
    [InlineData("inch","step",25.4)][InlineData("meter","step",1000)]
    [InlineData("inch","iges",25.4)][InlineData("meter","iges",1000)]
    public async Task FixedSourceUnitsConvertExactlyOnce(string unit,string extension,double scale)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using(var loaded=await kernel.ImportAsync(Fixture($"unit-{unit}.{extension}"),assets))
        {
            var body=Assert.Single(loaded.Snapshot.Bodies.Values);var bounds=body.Geometry.Bounds;
            Assert.Equal(0xFFFF0000u,body.Appearance.Argb);
            Assert.Equal(0,bounds.Min.X,4);Assert.Equal(scale,bounds.Max.X,4);
            Assert.Equal(2*scale,bounds.Max.Y,4);Assert.Equal(3*scale,bounds.Max.Z,4);
            using var shape=OcctGeometryBridge.ReadShape(body.Geometry,assets);
            Assert.True(shape.IsValid);
            var faces=shape.GetFaces();
            try{Assert.Equal(22*scale*scale,faces.Sum(f=>f.InspectProperties(InspectionPropertyKind.Area).Mass),3);}
            finally{foreach(var face in faces)face.Dispose();}
            if(extension=="step")Assert.Equal(6*scale*scale*scale,body.Geometry.VolumeMm3,2);
            else
            {
                // IGESCAF's surface export is a closed collection of faces, not a topological solid.
                Assert.Equal(BodyKind.Sheet,body.Geometry.Kind);Assert.Equal(0,body.Geometry.VolumeMm3);
                Assert.Equal(6,faces.Length);
            }
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact]
    public async Task FixedRotatedAssemblyPreservesDefinitionSharingAndFaceStyles()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using(var loaded=await kernel.ImportAsync(Fixture("rotated-colors.step"),assets))
        {
            var doc=loaded.Snapshot;var body=Assert.Single(doc.Bodies.Values);
            Assert.Equal(6000,body.Geometry.VolumeMm3,5);Assert.Equal(0xFFFF0000u,body.Appearance.Argb);
            var instances=doc.EnumerateOccurrences().Where(o=>doc.Definitions[o.DefinitionId] is PartDefinition).OrderByDescending(o=>o.WorldTransform.Translation.X).ToArray();
            Assert.Equal(2,instances.Length);Assert.Equal(instances[0].DefinitionId,instances[1].DefinitionId);
            Assert.NotEqual(instances[0].Path,instances[1].Path);
            Near(new(100,5,0),instances[0].WorldTransform.Apply(Vector3d.Zero));
            Near(new(0,0,-1),instances[0].WorldTransform.Rotation.Rotate(new(1,0,0)));
            Near(new(-5,200,0),instances[1].WorldTransform.Apply(Vector3d.Zero));
            Near(new(-3,200,-1),instances[1].WorldTransform.Apply(new(1,0,-2)));
            var source=Assert.IsType<XdeSourceRef>(body.Geometry.Source);
            using var context=OcctGeometryBridge.ReadContext(source.ContextAssetId,assets);
            var styles=context.GetLabel(source.DefinitionEntry).GetPresentationStyles();
            try
            {
                var colors=styles.Where(s=>s.EffectiveColor is not null).Select(s=>OcctGeometryBridge.ToArgb(s.EffectiveColor!.Value)).ToArray();
                Assert.Contains(0xFFFF0000u,colors);Assert.Contains(0xFF00FF00u,colors);Assert.Contains(0xFF0000FFu,colors);
            }
            finally{foreach(var style in styles)style.Dispose();}
        }
        Assert.Equal(0,assets.Count);
    }
    private static void Near(Vector3d expected,Vector3d actual)
    {Assert.Equal(expected.X,actual.X,6);Assert.Equal(expected.Y,actual.Y,6);Assert.Equal(expected.Z,actual.Z,6);}

    [Theory][InlineData("step")][InlineData("iges")]
    public async Task FixedRotationSurvivesExchangeExport(string extension)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using(var source=await kernel.ImportAsync(Fixture("rotated-colors.step"),assets))
        {
            string path=files.PathFor("roundtrip."+extension);
            var report=await kernel.ExportAsync(source.Snapshot,assets,path,new CadExportOptions());
            Assert.Contains(report.Diagnostics,d=>d.Code=="EXPORT.METADATA");
            if(extension=="iges")Assert.Contains(report.Diagnostics,d=>d.Code=="EXPORT.IGES_SURFACES");
            using var result=await kernel.ImportAsync(path,assets);
            var points=new List<Vector3d>();
            foreach(var item in CadScene.FromDocument(result.Snapshot).Items)
            {
                var b=item.Geometry.Bounds;
                foreach(double x in new[]{b.Min.X,b.Max.X})foreach(double y in new[]{b.Min.Y,b.Max.Y})foreach(double z in new[]{b.Min.Z,b.Max.Z})
                    points.Add(item.WorldTransform.Apply(new(x,y,z)));
                Assert.Equal(0xFFFF0000u,item.Argb);
            }
            Near(new(-35,5,-10),new(points.Min(p=>p.X),points.Min(p=>p.Y),points.Min(p=>p.Z)));
            Near(new(100,200,0),new(points.Max(p=>p.X),points.Max(p=>p.Y),points.Max(p=>p.Z)));
        }
        Assert.Equal(0,assets.Count);
    }

    [Theory][InlineData("inch","step",25.4)][InlineData("meter","step",1000)]
    [InlineData("inch","iges",25.4)][InlineData("meter","iges",1000)]
    public async Task SourceUnitsRemainMillimetersAfterExport(string unit,string extension,double scale)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using(var source=await kernel.ImportAsync(Fixture($"unit-{unit}.{extension}"),assets))
        {
            string path=files.PathFor("millimeters."+extension);await kernel.ExportAsync(source.Snapshot,assets,path);
            using var loaded=await kernel.ImportAsync(path,assets);
            var bounds=Assert.Single(loaded.Snapshot.Bodies.Values).Geometry.Bounds;
            Assert.Equal(scale,bounds.Max.X,4);Assert.Equal(2*scale,bounds.Max.Y,4);Assert.Equal(3*scale,bounds.Max.Z,4);
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact]
    public async Task SourceAppearanceSurvivesSaveAndExplicitOverrideUndo()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage();
        using(var source=await kernel.ImportAsync(Fixture("rotated-colors.step"),assets))
        await using(var session=new CadDocumentSession(source.Snapshot,assets,kernel,new InlineSessionDispatcher()))
        {
            Assert.All(CadScene.FromDocument(session.Snapshot).Items,i=>Assert.True(i.PreserveSourceStyles));
            var body=session.Snapshot.Bodies.Values.Single();
            await session.ExecuteAsync(DocumentEdits.SetAppearance(body.Id,new(0xFFFFFF00)));
            Assert.All(CadScene.FromDocument(session.Snapshot).Items,i=>Assert.False(i.PreserveSourceStyles));
            await session.UndoAsync();
            await session.SaveAsync(storage,files.PathFor("source.cadoryx"));
            using var restored=await storage.LoadAsync(files.PathFor("source.cadoryx"),assets);
            Assert.All(CadScene.FromDocument(restored.Snapshot).Items,i=>Assert.True(i.PreserveSourceStyles));
            Assert.Equal(body,restored.Snapshot.Bodies[body.Id]);
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact]
    public void OldAppearanceArrayDefaultsToExplicitColor()
    {
        // Fixed old two-key MessagePack array: [0xFFFF0000, false].
        byte[] old=[0x92,0xce,0xff,0xff,0,0,0xc2];
        var value=MessagePack.MessagePackSerializer.Deserialize<PackAppearance>(old,
            MessagePack.MessagePackSerializerOptions.Standard.WithSecurity(MessagePack.MessagePackSecurity.UntrustedData));
        Assert.Equal(0xFFFF0000u,value.Argb);Assert.False(value.ByLayer);Assert.False(value.PreserveSourceStyles);
    }
}
