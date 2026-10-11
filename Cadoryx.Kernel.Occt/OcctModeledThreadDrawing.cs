using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel
{
    // A dedicated proof for a direct current author. The pilot's independently measured
    // dimensions and the tooth faces' native Boolean relations are separate witnesses.
    /// <summary>Native geometry proof only. The document resolver separately confirms
    /// occurrence, producer and any immutable attachment history before accepting it.</summary>
    public Task<DrawingHoleEvidence> InspectNativeModeledThreadAsync(HoleRecipe recipe,GeometryAssetRef current,
        DrawingTopologyReference selected,IAssetStore assets,CancellationToken token=default)
        =>Run(()=>InspectModeledThreadDrawing(current,selected,recipe,assets,token),token);

    private DrawingHoleEvidence InspectModeledThreadDrawing(GeometryAssetRef currentGeometry,DrawingTopologyReference selected,
        HoleRecipe recipe,IAssetStore assets,CancellationToken token)
    {
        recipe.Validate();
        selected.Validate(selected.Path.DocumentId);currentGeometry.Validate();
        if(currentGeometry.AssetId!=selected.Asset||currentGeometry.Revision!=selected.Revision||
            recipe.Thread?.Representation!=ThreadHoleRepresentation.Modeled)
            throw new CadValidationException("Native thread proof requires the exact selected current asset and modeled author.");
        using var pilot=EvaluateModeledThreadPilot(recipe,assets,token);
        using var pilotShape=OcctGeometryBridge.ReadShape(pilot.Geometry,assets);
        using var pilotMap=RepairSnapshot.Create(pilotShape);
        var entry=recipe.Frame.Apply(new(recipe.Position.X,recipe.Position.Y,0));
        var direction=recipe.Frame.Rotation.Rotate(new(0,0,-1));
        static Vector3d P(NativeHoleInspector.Point p)=>new(p.X,p.Y,p.Z);
        var candidates=new List<NativeHoleInspector.Evidence>();
        foreach(var slot in pilotMap.Topology.Where(t=>t.Kind==ShapeKind.Face))
        {
            token.ThrowIfCancellationRequested();
            using var face=pilotMap.CopySubshape(slot.Selection);
            if(face.GetFaceSurfaceSnapshot().SurfaceType!=SurfaceGeometryType.Cylinder)continue;
            try
            {
                var proof=NativeHoleInspector.Inspect(pilotShape,slot.Selection.Index,token);
                if((P(proof.Entry)-entry).Length<1e-6&&(P(proof.Direction)-direction).Length<1e-6&&
                    Math.Abs(proof.Diameter-recipe.Diameter)<1e-6&&proof.Through==recipe.Through&&
                    (recipe.Through||Math.Abs(proof.Depth-recipe.Depth)<1e-6)&&proof.Head==0)candidates.Add(proof);
            }
            catch(InvalidOperationException){/* An unrelated cylinder is not this independently proved pilot. */}
        }
        if(candidates.Count!=1)throw new CadValidationException("Modeled thread requires one independently measured complete pilot. Missing or multiple proofs are rejected.");
        var measured=candidates[0];
        using var cut=EvaluateModeledThreadCut(recipe,pilot.Geometry,assets,token);
        var history=cut.TopologyHistory??throw new CadValidationException("Modeled thread drawing requires the complete native pilot-to-tooth Boolean history.");
        history.Validate();
        using var replay=OcctGeometryBridge.ReadShape(cut.Geometry,assets);
        using var current=OcctGeometryBridge.ReadShape(currentGeometry,assets);
        using var currentMap=RepairSnapshot.Create(current);using var replayMap=RepairSnapshot.Create(replay);
        if(currentMap.Fingerprint!=selected.Fingerprint||selected.FullTopologyIndex>=currentMap.Topology.Count||
            BuildDrawingAttachment(selected.Path.DocumentId,selected.Path,selected.DefinitionId,selected.BodyId,selected.FeatureId,
                currentGeometry,currentMap,selected.FullTopologyIndex,token)!=selected with{HistoryOrigin=null})
            throw new CadValidationException("Selected native thread attachment differs from the original indexed BRep.");
        if(!BrepDirectionRoundtrip.Matches(CanonicalBrep(replay),CanonicalBrep(current))||
            !VerifyHistoryRoundtrip(replayMap,currentMap,token))
            throw new CadValidationException("Current tooth solid differs from complete native author replay. No approximate topology matching is accepted.");
        int[] shaftFaces=history.Entries.Where(e=>e.SourceArgument==0&&e.SourceIndex==measured.ShaftFace&&
            e.ResultKind==HistoryShapeKind.Face&&e.ResultIndex.HasValue).Select(e=>e.ResultIndex!.Value).Distinct().ToArray();
        int[] toothFaces=history.Entries.Where(e=>e.SourceArgument==1&&e.SourceKind==HistoryShapeKind.Face&&
            e.ResultKind==HistoryShapeKind.Face&&e.ResultIndex.HasValue).Select(e=>e.ResultIndex!.Value).Distinct().ToArray();
        if(shaftFaces.Length==0||toothFaces.Length==0||!shaftFaces.Concat(toothFaces).Contains(selected.FullTopologyIndex))
            throw new CadValidationException("Select a native-proven thread tooth face or surviving pilot wall, not an unrelated face.");
        int[] proven=history.Entries.Where(e=>e.ResultKind==HistoryShapeKind.Face&&e.ResultIndex.HasValue&&
            (e.SourceArgument==0&&measured.Faces.Contains(e.SourceIndex)||e.SourceArgument==1&&e.SourceKind==HistoryShapeKind.Face))
            .Select(e=>e.ResultIndex!.Value).Distinct().Order().ToArray();
        var result=new DrawingHoleEvidence(selected,P(measured.Entry),P(measured.Direction),measured.Diameter,measured.Through,
            measured.Depth,HoleHead.Plain,0,0,0,DrawingHoleSpecificationSource.CurrentAuthoredHole,selected.FeatureId,
            Thread:recipe.Thread){ProvenFaceIndices=[..proven]};
        result.Validate(selected.Path.DocumentId);token.ThrowIfCancellationRequested();return result;
    }
}
