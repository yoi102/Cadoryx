using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Rendering;

public sealed record SceneItem(OccurrencePath Path,BodyId BodyId,GeometryAssetRef Geometry,RigidTransform3d WorldTransform,uint Argb);
public sealed record CadScene(DocumentId DocumentId,DocumentStateId StateId,ImmutableArray<SceneItem> Items)
{
    public static CadScene FromDocument(DocumentSnapshot document)
    {
        var items=ImmutableArray.CreateBuilder<SceneItem>();
        foreach(var occurrence in document.EnumerateOccurrences())
        {
            if(!occurrence.IsVisible||document.Definitions[occurrence.DefinitionId] is not PartDefinition part)continue;
            foreach(var id in part.Bodies)
            {
                var body=document.Bodies[id];var layer=document.Layers[body.LayerId];
                if(!body.IsVisible||!layer.IsVisible||body.Geometry.Kind==BodyKind.Empty)continue;
                var appearance=occurrence.AppearanceOverride??body.Appearance;
                items.Add(new(occurrence.Path,id,body.Geometry,occurrence.WorldTransform,appearance.ByLayer?layer.Argb:appearance.Argb));
            }
        }
        return new(document.Id,document.StateId,items.ToImmutable());
    }
}
public enum CadProjection { Axonometric, Front, Top, Right, Left, Back, Bottom }
public enum CadDisplayMode { Shaded, Wireframe }
public sealed record ViewportCapabilities(bool WpfOverlay,bool SubshapeSelection,bool SnapshotCapture);
public interface ICadViewport : IDisposable
{
    ViewportCapabilities Capabilities { get; }
    void SetScene(CadScene scene);
    void FitAll();
    void SetProjection(CadProjection projection);
    void SetDisplayMode(CadDisplayMode mode);
}
