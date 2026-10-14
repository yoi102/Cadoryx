using Cadoryx.Db;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel
{
    // The SDK entry must directly accept original native outer/hole wires.
    // Its precise orientation/ownership contract is qualified upstream first;
    // no typed-chain conversion, fitted edges, 3D cutters or guessed IDs occur.
    private static Shape BuildCompleteFiniteSurfaceStartPrism(SketchProfile profile,
        RigidTransform3d placement,Func<Shape,IReadOnlyList<Shape>,Shape> makeFace,
        CancellationToken token)
    {
        profile.Validate();
        Shape Build(SketchProfile material)
        {
            token.ThrowIfCancellationRequested();
            var holes=HoleBoundaries(material).ToArray();
            ValidatePlanarRegion(OuterBoundary(material),holes,
                material.Islands.Select(i=>OuterBoundary(i.Profile)).ToArray(),token);
            using var outer=SectionWire(OuterBoundary(material),RigidTransform3d.Identity,token);
            var inner=new List<Shape>();var pieces=new List<Shape>();
            try
            {
                foreach(var hole in holes)
                {
                    using var boundary=SectionWire(OuterBoundary(hole),RigidTransform3d.Identity,token);
                    // The SDK face contract requires explicit hole orientation.
                    // Reverse the native TopoDS value, never rebuild its curves.
                    inner.Add(boundary.Reversed());
                }
                token.ThrowIfCancellationRequested();
                using var face=makeFace(outer,inner);
                RequireClear(face,token);
                using var direction=GpVec.Create(0,0,1);
                pieces.Add(face.Extrude(direction));
                foreach(var island in material.Islands)pieces.Add(Build(island.Profile));
                token.ThrowIfCancellationRequested();
                var result=pieces.Count==1?pieces[0].Clone():ShapeFactory.CreateCompound(pieces);
                try{RequireClear(result,token);return result;}
                catch{result.Dispose();throw;}
            }
            finally
            {
                foreach(var piece in pieces)piece.Dispose();
                foreach(var wire in inner)wire.Dispose();
            }
        }
        using var local=Build(profile);
        token.ThrowIfCancellationRequested();
        using var transform=OcctGeometryBridge.ToNative(placement);
        return local.Transformed(transform);
    }
}
