using OcctSharp.Interop;

namespace OcctSharp.Runtime.Tests;

#pragma warning disable CA1861
public sealed class BooleanTopologyHistoryTests
{
    private static Shape Translate(Shape source, double x, double y, double z)
    {
        using var transform = GpTrsf.Create(x, y, z);
        return source.Transformed(transform);
    }

    [Theory]
    [InlineData(TopologyBooleanOperation.Cut, 4800)]
    [InlineData(TopologyBooleanOperation.Fuse, 6208)]
    [InlineData(TopologyBooleanOperation.Common, 1200)]
    public void ExactHistoryCoversBothInputsAndKeepsInputGeometry(TopologyBooleanOperation operation, double volume)
    {
        using var box = ShapeFactory.CreateBox(10, 20, 30);
        using var tool = ShapeFactory.CreateBox(2, 22, 32);
        using var placed = Translate(tool, 4, -1, -1);
        using var first = RepairSnapshot.Create(box); using var second = RepairSnapshot.Create(placed);
        using var result = BooleanHistoryModeling.Build(operation, new[] { first, second });
        Assert.InRange(Math.Abs(result.RequireShape().InspectProperties(InspectionPropertyKind.Volume).Mass), volume - 1e-6, volume + 1e-6);
        Assert.False(result.Diagnostics.HasComposedHistory);
        Assert.True(result.Diagnostics.GroupSupport.HasFlag(LocalFeatureGroupSupport.Evolution));
        Assert.Equal(LocalFeatureOperation.Boolean, result.Diagnostics.Operation);
        Assert.Equal(first.Fingerprint, RepairSnapshot.ComputeFingerprint(first.Shape));
        Assert.Equal(second.Fingerprint, RepairSnapshot.ComputeFingerprint(second.Shape));
        using var final = RepairSnapshot.Create(result.RequireShape());
        var inputs = new[] { first, second };
        for (int argument = 0; argument < inputs.Length; argument++)
            foreach (var item in inputs[argument].Topology.Where(t => t.Kind is ShapeKind.Face or ShapeKind.Edge or ShapeKind.Vertex))
                Assert.Contains(result.History, h => h.Source is { } s && s.ArgumentIndex == argument
                    && s.TopologyIndex == item.Selection.Index && s.Kind == item.Kind && s.PlanId == result.PlanId);
        Assert.All(result.History, h =>
        {
            Assert.NotNull(h.Source);
            if (h.ResultTopologyIndex is { } target)
            {
                Assert.InRange(target, 0, final.Topology.Count - 1); Assert.NotNull(h.Shape);
                // Select directly in the exact returned graph. RepairSnapshot makes
                // a deep copy whose p-curve representation is a separate contract.
                NativeError.ThrowIfFailed(NativeMethods.LocalFeatureSourceSubshape(result.RequireShape().Handle, target, out nint raw), "test_final_slot");
                using var exact = ShapeFactory.FromNativeHandle(raw, "test_final_slot");
                Assert.Equal(RepairSnapshot.ComputeFingerprint(exact), RepairSnapshot.ComputeFingerprint(h.Shape!));
            }
            else Assert.True(h.Kind is LocalFeatureHistoryKind.Deleted or LocalFeatureHistoryKind.Unmapped);
        });
        if (operation == TopologyBooleanOperation.Cut)
        {
            Assert.Contains(result.History, h => h.Kind == LocalFeatureHistoryKind.Deleted);
            Assert.Contains(result.History, h => h.Kind == LocalFeatureHistoryKind.Unchanged);
            Assert.Contains(result.History.Where(h => h.Source!.Value.ArgumentIndex == 0 && h.Source.Value.Kind == ShapeKind.Face
                && h.Kind == LocalFeatureHistoryKind.Modified).GroupBy(h => h.Source),
                g => g.Select(h => h.ResultTopologyIndex).Distinct().Count() > 1);
        }
    }

    [Fact]
    public void CopiedOutputsOutliveSourcesAndRejectDisposedInputs()
    {
        using var box = ShapeFactory.CreateBox(10, 20, 30);
        using var first = RepairSnapshot.Create(box); using var second = RepairSnapshot.Create(box);
        using var result = BooleanHistoryModeling.Build(TopologyBooleanOperation.Common, new[] { first, second });
        first.Dispose(); second.Dispose(); box.Dispose();
        Assert.InRange(Math.Abs(result.RequireShape().InspectProperties(InspectionPropertyKind.Volume).Mass), 5999.99999, 6000.00001);
        // Coincident independent sources genuinely share final topology, without fabricated relations.
        Assert.Contains(result.History.Where(h => h.ResultTopologyIndex.HasValue).GroupBy(h => h.ResultTopologyIndex),
            g => g.Select(h => h.Source!.Value.ArgumentIndex).Distinct().Count() == 2);
        foreach (var h in result.History.Where(h => h.Shape is not null)) Assert.True(h.Shape!.IsValid);
        Assert.Throws<ObjectDisposedException>(() => BooleanHistoryModeling.Build(TopologyBooleanOperation.Cut, new[] { first, second }));
        result.Dispose(); result.Dispose();
        Assert.Throws<ObjectDisposedException>(() => result.RequireShape());
    }

    [Fact]
    public void InvalidOperationAndInputGroupsRejectBeforeBuilding()
    {
        using var box = ShapeFactory.CreateBox(1, 1, 1); using var source = RepairSnapshot.Create(box);
        Assert.Throws<ArgumentOutOfRangeException>(() => BooleanHistoryModeling.Build((TopologyBooleanOperation)99, new[] { source, source }));
        Assert.Throws<ArgumentException>(() => BooleanHistoryModeling.Build(TopologyBooleanOperation.Cut, new[] { source }));
        Assert.Throws<ArgumentOutOfRangeException>(() => BooleanHistoryModeling.Build(TopologyBooleanOperation.Cut, new[] { source, source }, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BooleanHistoryModeling.Build(TopologyBooleanOperation.Cut, new[] { source, source }, 2));
    }
}
#pragma warning restore CA1861
