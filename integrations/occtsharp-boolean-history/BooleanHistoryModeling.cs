using OcctSharp.Interop;

namespace OcctSharp;

/// <summary>Boolean operations with exact, per-input topology history.</summary>
public enum TopologyBooleanOperation
{
    /// <summary>Union of the argument and tool groups.</summary>
    Fuse,
    /// <summary>Subtract the tool group from the argument group.</summary>
    Cut,
    /// <summary>Intersection of the argument and tool groups.</summary>
    Common
}

/// <summary>Captures face, edge and vertex relations from one non-destructive Boolean build.
/// Indices name the supplied snapshots and the returned exact result, not persistent names.</summary>
public static class BooleanHistoryModeling
{
    /// <summary>Builds with input slots [0, argumentCount) as arguments and the remainder as tools.
    /// The caller retains its snapshots; the returned result and history shapes own their lifetime.
    /// Deleted/unmapped relations have no target. Split and shared targets are retained without choosing one.
    /// Inputs and results may be disposed independently after the call. The default has one base and N tools.</summary>
    public static unsafe LocalFeatureResult Build(TopologyBooleanOperation operation,
        IReadOnlyList<RepairSnapshot> inputs, int argumentCount = 1)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        if (inputs.Count is < 2 or > 256) throw new ArgumentException("Expected two to 256 snapshots.", nameof(inputs));
        if (argumentCount < 1 || argumentCount >= inputs.Count) throw new ArgumentOutOfRangeException(nameof(argumentCount));
        var shapes = new Shape[inputs.Count];
        for (int i = 0; i < shapes.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(inputs[i]);
            shapes[i] = inputs[i].Shape;
        }
        // WithInputs holds SafeHandle references throughout native copying/building.
        return AuthoringBridge.WithInputs(shapes, (p, count) =>
        {
            NativeError.ThrowIfFailed(NativeMethods.BooleanTopologyHistory(p, count, argumentCount, (int)operation, out nint result),
                "boolean_topology_history");
            return LocalFeatureBridge.Read(Guid.NewGuid(), result);
        });
    }
}
