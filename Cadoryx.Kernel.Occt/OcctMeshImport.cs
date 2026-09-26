using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel
{
    private static LoadedDocument ImportStl(string path,IAssetStore assets,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var shape=ShapeExchange.ReadStl(Path.GetFullPath(path));
        using var stored=OcctGeometryBridge.StoreShape(shape,assets);
        if(stored.Geometry.Kind==BodyKind.Empty)throw new CadValidationException("STL contains no geometry.");
        var document=DocumentSnapshot.Create(Path.GetFileNameWithoutExtension(path));
        var part=DefinitionId.New();var bodyId=BodyId.New();var featureId=FeatureId.New();var geometry=stored.Geometry;
        var body=new CadBody(bodyId,part,document.Name,geometry,featureId,document.Layers.Keys.First(),new(0xFF86ACC5));
        var root=(AssemblyDefinition)document.Definitions[document.RootAssemblyId];
        document=document with
        {
            Definitions=document.Definitions.Add(part,new PartDefinition(part,document.Name,[bodyId],[featureId]))
                .SetItem(root.Id,root with{Children=[new(ComponentSlotId.New(),part,document.Name,RigidTransform3d.Identity)]}),
            Bodies=document.Bodies.Add(bodyId,body),
            Features=document.Features.Add(featureId,new(featureId,part,"Import STL",new ImportedRecipe(geometry),[],bodyId,geometry))
        };
        document.Validate();token.ThrowIfCancellationRequested();
        return new(document,[assets.Acquire(geometry.AssetId)],
            [new("StlUnitless","STL has no units or assembly metadata. Coordinates are interpreted as millimeters; triangle faces are retained, without reconstructing analytic surfaces or a closed solid.")]);
    }
}
