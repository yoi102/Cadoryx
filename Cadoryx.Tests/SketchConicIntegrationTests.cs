using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchConicIntegrationTests
{
    [Fact] public async Task AutoNestConicRegionsChoosesTheNearestParentAndCreatesExactSolid()
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var draft=new SketchDraft(CadSketch.Create(part,"Nested conics",RigidTransform3d.Identity));
            foreach(double radius in new[]{10d,7,4,2})draft.AddEllipse(new(new(20,0),new(radius,0),new(0,radius/2),.3,Math.Tau));
            await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,new ManagedSketchConstraintSolver()));var sketch=session.Snapshot.Sketches[draft.Value.Id];
            var editor=new CadDocumentViewModel(session,kernel,new CadMessageLog());
            try
            {
                editor.SelectedTargetPart=part;editor.SelectedSketchId=sketch.Id;editor.StartSketchFeature("Extrude");
                editor.SelectedSketchProfile=editor.SketchProfiles.Single(p=>p.Source.MixedBoundaryIds.SequenceEqual([sketch.Ellipses[0].Id]));
                editor.SizeZ=3;editor.AutoNestSketchRegionsCommand.Execute(null);
                Assert.Equal(string.Format(Cadoryx.Lang.Strings.Strings.NestedRegionsCount,3),editor.AutoNestStatus);
                await editor.PreviewCommand.ExecuteAsync(null);Assert.NotNull(editor.PreviewScene);await editor.ConfirmCommand.ExecuteAsync(null);
                var feature=Assert.Single(session.Snapshot.Features.Values);var hole=Assert.Single(feature.SketchSource!.Regions);Assert.Equal(sketch.Ellipses[1].Id,Assert.Single(hole.MixedCurveIds));
                var island=Assert.Single(hole.Children);Assert.Equal(sketch.Ellipses[2].Id,Assert.Single(island.MixedCurveIds));Assert.Equal(sketch.Ellipses[3].Id,Assert.Single(Assert.Single(island.Children).MixedCurveIds));
                Assert.InRange(Math.Abs(feature.Result.VolumeMm3-(100-49+16-4)*Math.PI/2*3),0,1e-5);
            }
            finally{editor.Detach();}
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task CachedConicCoefficientsCannotChangeBehindUnchangedBoundaryEndpoints()
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var draft=new SketchDraft(CadSketch.Create(part,"Conic",RigidTransform3d.Identity));draft.AddEllipse(new(new(20,0),new(5,0),new(0,2),.3,Math.Tau));
        await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,new ManagedSketchConstraintSolver()));var sketch=session.Snapshot.Sketches[draft.Value.Id];var source=SketchProfileReference.CreateMixed(sketch,[sketch.Ellipses[0].Id]);
        var recipe=(ExtrudeRecipe)source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,sketch.Plane));await session.ExecuteAsync(new AddBodyCommand(recipe,"Conic",part,sketchSource:source));
        var feature=Assert.Single(session.Snapshot.Features.Values);var boundary=recipe.Profile.BoundaryCurves;
        var forged=boundary.SetItem(0,boundary[0] with{Ellipse=boundary[0].Ellipse!.Value with{Sine=new(0,9)}});
        Assert.False(SketchMixedProfile.SameLoop(boundary,forged));
        var changed=feature with{Recipe=recipe with{Profile=recipe.Profile with{BoundaryCurves=forged}}};
        Assert.Throws<CadValidationException>(()=>source.ValidateCache(session.Snapshot,changed));
    }
}
