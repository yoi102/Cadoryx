using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

public sealed class MirrorStyleTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ReflectedFaceStylesFollowExactGeometryThroughNestedMirrorsSaveAndStep(bool nested)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        var input=files.PathFor("two-face-colors.step");CreateSource(input);
        using(var imported=await kernel.ImportAsync(input,assets))
        await using(var session=new CadDocumentSession(imported.Snapshot,assets,kernel,new InlineSessionDispatcher()))
        {
            var original=session.Snapshot.Bodies.Values.Single();CheckSource(original.Geometry,assets,2);
            var root=new OccurrencePath(session.Snapshot.Id,[]);var source=session.Snapshot.EnumerateOccurrences().Single(o=>session.Snapshot.Definitions[o.DefinitionId] is PartDefinition).Path;
            if(nested)
            {
                var owner=DefinitionId.New();var slot=ComponentSlotId.New();
                await session.ExecuteAsync(AssemblyOccurrenceCommands.CreateAssembly(root,owner,slot,"Nested source",RigidTransform3d.Translate(8,4,3)));
                var parent=root.Append(slot);await session.ExecuteAsync(AssemblyOccurrenceCommands.Reparent(source,parent));source=parent.Append(source.Slots.Last());
                var inner=new AssemblyRepetition(Guid.NewGuid(),owner,source.Slots.Last(),"Inner",RepetitionKind.Mirror,RigidTransform3d.Identity,2,0,[ComponentSlotId.New()]);
                await session.ExecuteAsync(AssemblyRepetitionCommands.Set(source,inner));source=parent;
            }
            var placement=OccurrencePlacement.Resolve(session.Snapshot,source);
            var mirror=new AssemblyRepetition(Guid.NewGuid(),placement.OwnerId,placement.Slot.Id,"Styled reflection",RepetitionKind.Mirror,
                new(new(17,-3,9),Quaterniond.FromAxisAngle(new(2,1,3),.431)),2,0,[ComponentSlotId.New()]);
            var before=session.Snapshot;await session.ExecuteAsync(AssemblyRepetitionCommands.Set(source,mirror));var after=session.Snapshot;
            var reflected=after.AssemblyPatterns[mirror.Id].Mirrors.Single(m=>m.Source==original.PartId).Bodies.Single().Target;
            Assert.True(after.Bodies[reflected].Appearance.PreserveSourceStyles);CheckSource(after.Bodies[reflected].Geometry,assets,-2);
            if(nested)
            {
                var inner=after.AssemblyPatterns.Values.Single(p=>p.Id!=mirror.Id);var innerPart=inner.Mirrors.Single().Target;
                var twice=after.AssemblyPatterns[mirror.Id].Mirrors.Single(m=>m.Source==innerPart).Bodies.Single().Target;
                CheckSource(after.Bodies[twice].Geometry,assets,2);
                using var actual=OcctGeometryBridge.ReadShape(after.Bodies[twice].Geometry,assets);
                using var expected=OcctGeometryBridge.ReadShape(original.Geometry,assets);
                using var extra=actual.Cut(expected);using var missing=expected.Cut(actual);
                Assert.Equal(0,extra.GetTopologySummary().UniqueCounts.SolidCount);Assert.Equal(0,missing.GetTopologySummary().UniqueCounts.SolidCount);
            }
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
            var saved=files.PathFor("colored-mirror.cadoryx");await session.SaveAsync(new CadDocumentStorage(datumResolver:kernel),saved);
            using(var loaded=await new CadDocumentStorage(datumResolver:kernel).LoadAsync(saved,assets))CheckSource(loaded.Snapshot.Bodies[reflected].Geometry,assets,-2);
            var output=files.PathFor("colored-mirror.step");await kernel.ExportAsync(after,assets,output);
            using var reopened=XdeDocument.ReadStep(output);var occurrences=reopened.GetFreeShapes().SelectMany(l=>l.GetOccurrences()).ToArray();
            try
            {
                var leaves=occurrences.Where(o=>!o.ReferredLabel.IsAssembly).ToArray();
                Assert.Equal(nested?4:2,leaves.Length);
                foreach(var occurrence in leaves)
                {
                    var styles=occurrence.ReferredLabel.GetPresentationStyles();
                    try
                    {
                        Assert.Single(styles,s=>IsRed(s.EffectiveColor));Assert.Single(styles,s=>IsGreen(s.EffectiveColor));
                        using var shape=occurrence.GetLocatedShape();Assert.Equal(24,shape.InspectProperties(InspectionPropertyKind.Volume).Mass,7);Assert.True(shape.IsValid);
                    }
                    finally{foreach(var style in styles)style.Dispose();}
                }
            }
            finally{foreach(var occurrence in occurrences)occurrence.Dispose();}
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task CancelledStyledCandidateReleasesBothGeometryAndContextWithoutChangingDocument()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        var input=files.PathFor("source.step");CreateSource(input);
        using(var imported=await kernel.ImportAsync(input,assets))
        await using(var session=new CadDocumentSession(imported.Snapshot,assets,kernel,new InlineSessionDispatcher()))
        {
            var before=session.Snapshot;var count=assets.Count;var body=before.Bodies.Values.Single();
            using(var candidate=await kernel.EvaluateAsync(new RepetitionRecipe(body.Geometry,RepetitionKind.Mirror,RigidTransform3d.Identity,1,0,[Guid.NewGuid()],IncludeSource:false),assets))
            {CheckSource(candidate.Geometry,assets,-2);Assert.True(assets.Count>count);Assert.Same(before,session.Snapshot);}
            Assert.Equal(count,assets.Count);
            using var cancellation=new CancellationTokenSource();cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>kernel.EvaluateAsync(new RepetitionRecipe(body.Geometry,RepetitionKind.Mirror,RigidTransform3d.Identity,1,0,[Guid.NewGuid()],IncludeSource:false),assets,cancellation.Token));
            Assert.Equal(count,assets.Count);Assert.Same(before,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }

    internal static void CreateSource(string path)
    {
        using var document=XdeDocument.Create();using var box=ShapeFactory.CreateBox(4,3,2);
        using(var edit=document.BeginTransaction("Two independently colored faces"))
        {
            var part=document.AddShape(box,"Colored source");part.Color=new(.2,.4,.8);part.SetLayer("Manufacturing");
            using var repair=new RepairDocumentSession(document,part);
            foreach(var face in repair.Source.Topology.Where(t=>t.Kind==ShapeKind.Face))
            {
                using var shape=repair.Source.CopySubshape(face.Selection);var bounds=shape.GetBoundingBox();
                if(Math.Abs(bounds.Maximum.Z-bounds.Minimum.Z)<1e-6&&Math.Abs(bounds.Maximum.Z-2)<1e-6)
                    repair.GetOrCreateSubshapeLabel(face.Selection).Color=new(1,0,0);
                if(Math.Abs(bounds.Maximum.X-bounds.Minimum.X)<1e-6&&Math.Abs(bounds.Maximum.X-4)<1e-6)
                    repair.GetOrCreateSubshapeLabel(face.Selection).Color=new(0,1,0);
            }
            edit.Commit();
        }
        document.WriteStep(path);
    }
    private static bool IsRed(XdeColor? color)=>color is {} c&&c.Red>.99&&c.Green<.01&&c.Blue<.01;
    private static bool IsGreen(XdeColor? color)=>color is {} c&&c.Green>.99&&c.Red<.01&&c.Blue<.01;
    private static void CheckSource(GeometryAssetRef geometry,IAssetStore assets,double redZ)
    {
        var source=Assert.IsType<XdeSourceRef>(geometry.Source);using var document=OcctGeometryBridge.ReadContext(source.ContextAssetId,assets,source.Format);
        var label=document.GetLabel(source.DefinitionEntry);var styles=label.GetPresentationStyles();
        try
        {
            var red=Assert.Single(styles,s=>IsRed(s.EffectiveColor));Assert.Equal(1,red.Shape.FaceCount);
            Assert.Equal(redZ,red.Shape.InspectProperties(InspectionPropertyKind.Area).CenterOfMass.Z,7);
            var green=Assert.Single(styles,s=>IsGreen(s.EffectiveColor));Assert.Equal(1,green.Shape.FaceCount);
            Assert.Equal(4,green.Shape.InspectProperties(InspectionPropertyKind.Area).CenterOfMass.X,7);
            using var exact=OcctGeometryBridge.ReadShape(geometry,assets);using var styled=label.Shape;
            Assert.True(exact.IsValid);Assert.True(exact.GetSelfInterferenceReport().IsClear);
            using var extra=styled.Cut(exact);using var missing=exact.Cut(styled);
            Assert.Equal(0,extra.GetTopologySummary().UniqueCounts.SolidCount);Assert.Equal(0,missing.GetTopologySummary().UniqueCounts.SolidCount);
        }
        finally{foreach(var style in styles)style.Dispose();}
    }
}
