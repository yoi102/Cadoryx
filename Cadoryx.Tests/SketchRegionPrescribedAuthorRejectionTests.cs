using Cadoryx.Db;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchRegionPrescribedAuthorRejectionTests
{
    [Theory]
    [InlineData("arc-g1",false)] [InlineData("spline-g1",false)] [InlineData("spline-g2",false)]
    [InlineData("arc-g1",true)] [InlineData("spline-g1",true)] [InlineData("spline-g2",true)]
    public async Task ExplicitCoordinateConflictOrBrokenEndpointRelationCannotPublishAChangedRegion(string kind,bool duplicateConflict)
    {
        var kernel=new OcctGeometryKernel();
        var original=SketchRegionSingleLoopMixedNativeTests.NativeMixedBoundary(kind,false,.1,SketchRegionAuthorJoin.Miter);
        var ids=original.Lines.SelectMany(l=>new[]{l.Start,l.End}).Concat(original.Arcs.SelectMany(a=>new[]{a.Start,a.Middle,a.End}))
            .Concat(original.Splines.SelectMany(s=>s.Controls)).ToHashSet();
        var changed=kind=="arc-g1"?original.Arcs[0].Middle:original.Splines[0].Controls[1];
        var authored=original with{Constraints=original.Constraints.AddRange(original.Points.Where(p=>ids.Contains(p.Id))
            .Select(p=>new FixPointConstraint(SketchConstraintId.New(),p.Id,
                !duplicateConflict&&p.Id==changed?new(p.Position.X,p.Position.Y+.5):p.Position)))};
        if(duplicateConflict)
        {
            var p=original.Points.Single(p=>p.Id==changed);
            authored=authored with{Constraints=authored.Constraints.Add(new FixPointConstraint(SketchConstraintId.New(),changed,new(p.Position.X,p.Position.Y+.5)))};
        }
        var before=authored;authored.Validate();
        var report=await new ManagedSketchConstraintSolver(kernel).SolveAsync(authored);
        Assert.False(report.Succeeded);Assert.Null(report.Solution);
        Assert.Same(before,authored);Assert.Equal(original.RegionOffsets,authored.RegionOffsets);
        Assert.Equal(original.Points,authored.Points);Assert.Equal(original.ExpressionCurves,authored.ExpressionCurves);
        kernel.ConfirmExpressionRegions(original);
    }
}
