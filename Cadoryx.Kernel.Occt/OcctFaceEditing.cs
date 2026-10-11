using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel:IFaceEditResolver
{
    private static readonly FeatureModelingOptions FaceEditOptions=new(){FuzzyTolerance=1e-7,NonDestructive=true,RepairInputs=false,UnifyResult=true};
    public async Task<Vector3d> InspectPlanarFaceAsync(DocumentSnapshot snapshot,ExactTopologySelection face,IAssetStore assets,CancellationToken token=default)
    {
        if(face.Kind!=HistoryShapeKind.Face)throw new CadValidationException("Select an exact planar face.");
        await ConfirmAsync(snapshot,face,assets,token).ConfigureAwait(false);
        if(snapshot.Features[face.FeatureId].IsSuppressed)throw new CadValidationException("Restore the source before choosing its face.");
        return await Run(()=>
        {
            using var shape=OcctGeometryBridge.ReadShape(snapshot.Features[face.FeatureId].Result,assets);using var solid=CopyOffsetSolid(shape,token);
            using var map=RepairSnapshot.Create(shape);ConfirmFaceFeatureMap(map,[face]);var members=shape.GetFaces();
            try{return NativePlanarNormal(ExactFaceMember(shape,members,face.FullTopologyIndex));}finally{foreach(var member in members)member.Dispose();}
        },token).ConfigureAwait(false);
    }
    public Task ConfirmFaceEditRecipeAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken token=default)
    {
        if(recipe is not (PlanarPushPullRecipe or DefeatureRecipe))throw new CadValidationException("Expected a face-edit recipe.");recipe.Validate();
        return Run(()=>
        {
            using var shape=OcctGeometryBridge.ReadShape(FaceFeatureRecipes.Source(recipe),assets);using var accepted=CopyOffsetSolid(shape,token);
            using var map=RepairSnapshot.Create(shape);ConfirmFaceFeatureMap(map,FaceFeatureRecipes.Faces(recipe));var members=shape.GetFaces();
            try
            {
                foreach(var face in FaceFeatureRecipes.Faces(recipe))_=ExactFaceMember(shape,members,face.FullTopologyIndex);
                if(recipe is PlanarPushPullRecipe r)ConfirmPlanarNormal(NativePlanarNormal(ExactFaceMember(shape,members,r.Face.FullTopologyIndex)),r.OutwardNormal);
                return true;
            }
            finally{foreach(var member in members)member.Dispose();}
        },token);
    }
    private static Shape ExactFaceMember(Shape source,IReadOnlyList<Shape> members,int index)
    {
        var exact=members.Where(face=>RepairSnapshot.FindTopologyIndex(source,face)==index).ToArray();
        if(exact.Length!=1)throw new CadValidationException("Face is not a unique native member of the exact source.");return exact[0];
    }
    private static Vector3d NativePlanarNormal(Shape face)
    {
        var surface=face.GetFaceSurfaceSnapshot();if(surface.SurfaceType!=SurfaceGeometryType.Plane)throw new CadValidationException("Finite planar push/pull requires a plane face; curved surfaces need their own operation.");
        // EvaluateFace applies the face orientation, so this is the solid's outward normal.
        var point=face.EvaluateFace((surface.FirstUParameter+surface.LastUParameter)/2,(surface.FirstVParameter+surface.LastVParameter)/2);
        var normal=new Vector3d(point.Normal.X,point.Normal.Y,point.Normal.Z);normal.Validate();return normal.Normalized();
    }
    private static void ConfirmPlanarNormal(Vector3d actual,Vector3d expected)
    {if((actual-expected).Length>1e-10)throw new CadValidationException("Planar face normal differs from the reviewed native direction. Reselect explicitly.");}
    private GeometryResult EvaluatePlanarPushPull(PlanarPushPullRecipe recipe,IAssetStore assets,CancellationToken token)
    {
        using var shape=OcctGeometryBridge.ReadShape(recipe.Source,assets);using var accepted=CopyOffsetSolid(shape,token);
        using var source=RepairSnapshot.Create(shape);ConfirmFaceFeatureMap(source,[recipe.Face]);var members=shape.GetFaces();
        try
        {
            var face=ExactFaceMember(shape,members,recipe.Face.FullTopologyIndex);ConfirmPlanarNormal(NativePlanarNormal(face),recipe.OutwardNormal);
            var d=recipe.OutwardNormal*recipe.Distance;using var vector=GpVec.Create(d.X,d.Y,d.Z);using var prism=face.Extrude(vector);using var tool=RepairSnapshot.Create(prism);
            var built=BooleanHistoryModeling.Build(recipe.Distance>0?TopologyBooleanOperation.Fuse:TopologyBooleanOperation.Cut,[source,tool]);
            try
            {
                using var valid=CopyOffsetSolid(built.RequireShape(),token);double before=AuthoringMeasure(shape,InspectionPropertyKind.Volume),after=AuthoringMeasure(valid,InspectionPropertyKind.Volume);
                using var overlapShape=shape.Common(valid);double overlap=AuthoringMeasure(overlapShape,InspectionPropertyKind.Volume),epsilon=Math.Max(1e-6,before*1e-7);
                if(recipe.Distance>0?(after-before<=epsilon||Math.Abs(overlap-before)>epsilon):(before-after<=epsilon||Math.Abs(overlap-after)>epsilon))
                    throw new CadValidationException("Face push/pull did not change material in its authored direction or returned disconnected material.");
                using(var unchanged=RepairSnapshot.Create(shape))if(unchanged.Fingerprint!=source.Fingerprint)throw new CadValidationException("Native face push/pull mutated its source representation.");
                return FinishLocal(recipe.Source,source,built,assets,token,[tool]);
            }
            catch{built.Dispose();throw;}
        }
        finally{foreach(var member in members)member.Dispose();}
    }
    private GeometryResult EvaluateDefeature(DefeatureRecipe recipe,IAssetStore assets,CancellationToken token)
    {
        using var source=OcctGeometryBridge.ReadShape(recipe.Source,assets);using var accepted=CopyOffsetSolid(source,token);using var map=RepairSnapshot.Create(source);
        ConfirmFaceFeatureMap(map,recipe.Faces);var members=source.GetFaces();
        try
        {
            var faces=recipe.Faces.Select(f=>ExactFaceMember(source,members,f.FullTopologyIndex)).ToArray();
            using var built=FeatureModeling.Defeature(source,faces,FaceEditOptions);
            if(!built.Diagnostics.Succeeded)throw new CadValidationException("Native face removal did not complete: "+built.Diagnostics.StageMessage);
            // These indices identify supplied arguments (source=0, selected faces=1..N),
            // not slots in the source topology map. A changed root fingerprint alone
            // cannot prove that every requested face was removed.
            for(int i=0;i<faces.Length;i++)
                if(!built.DeletedSourceIndices.Contains(i+1))throw new CadValidationException("Native history did not confirm removal of every selected face; partial and no-op results are rejected.");
            using var valid=CopyOffsetSolid(built.RequireShape(),token);using var final=RepairSnapshot.Create(valid);
            if(final.Fingerprint==map.Fingerprint)throw new CadValidationException("Face removal produced no verified geometric change.");
            using(var unchanged=RepairSnapshot.Create(source))if(unchanged.Fingerprint!=map.Fingerprint)throw new CadValidationException("Native face removal mutated its source representation.");
            using var stored=OcctGeometryBridge.StoreShape(valid,assets);token.ThrowIfCancellationRequested();
            return new(stored.Geometry,GeometryAssetLease.Acquire(stored.Geometry,assets),[new("HISTORY.UNSUPPORTED","Defeaturing reports aggregate argument history without a full source-subshape locator; select final topology explicitly."),new("STYLE.UNSUPPORTED","Removed and healed faces have no verified source style correspondence.")]);
        }
        finally{foreach(var member in members)member.Dispose();}
    }
}
