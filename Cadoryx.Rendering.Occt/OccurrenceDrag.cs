using Cadoryx.Db;
using Cadoryx.Rendering;

namespace Cadoryx.Rendering.Occt;

/// <summary>Converts a world-space handle drag into the selected slot's parent-local placement.</summary>
public static class OccurrenceDrag
{
    public static RigidTransform3d MoveLocal(RigidTransform3d parentWorld,
        RigidTransform3d originalLocal,Vector3d worldDelta)
    {
        parentWorld.Validate();originalLocal.Validate();worldDelta.Validate();
        var delta=parentWorld.Rotation.Inverse().Rotate(worldDelta);
        var result=originalLocal with{Translation=originalLocal.Translation+delta};
        result.Validate();return result;
    }

    public static bool Contains(OccurrencePath parent,OccurrencePath candidate)=>
        parent.DocumentId==candidate.DocumentId&&candidate.Slots.Length>=parent.Slots.Length&&
        parent.Slots.SequenceEqual(candidate.Slots.Take(parent.Slots.Length));

    public static bool CanMove(DocumentSnapshot snapshot,OccurrencePath path)
    {
        if(!OccurrencePlacement.Resolve(snapshot,path).CanMoveIndependently)return false;
        return !snapshot.AssemblyConstraints.Values.Any(c=>c.IsEnabled&&
            (Contains(path,c.PrimaryPath)||c.SecondaryPath is {} secondary&&Contains(path,secondary)));
    }

    public static Vector3d Center(IEnumerable<SceneItem> items)
    {
        var bounds=SceneEnvelope.Measure(items)??throw new ArgumentException("No visible occurrence geometry.",nameof(items));
        return (bounds.Min+bounds.Max)/2;
    }
}
