using Cadoryx.Db;
using Cadoryx.Lang.Strings;

namespace Cadoryx.Commands;

public static class ResourceCommands
{
    public static ICadDocumentCommand AddPart(DefinitionId id, string name) => new EditDocumentCommand(Strings.AddPart, doc =>
    {
        CadGuard.Id(id); CadGuard.Name(name);
        var part = new PartDefinition(id, name, [], []);
        var root = (AssemblyDefinition)doc.Definitions[doc.RootAssemblyId];
        return doc with { Definitions = doc.Definitions.Add(id, part).SetItem(root.Id,
            root with { Children = root.Children.Add(new(ComponentSlotId.New(), id, name, RigidTransform3d.Identity)) }) };
    });

    public static ICadDocumentCommand RenamePart(DefinitionId id, string name) => new EditDocumentCommand(Strings.Rename, doc =>
    {
        CadGuard.Name(name);
        if (doc.Definitions[id] is not PartDefinition part) throw new CadValidationException(Strings.SelectTargetPart);
        return part.Name == name ? doc : doc with { Definitions = doc.Definitions.SetItem(id, part with { Name = name }) };
    });

    public static ICadDocumentCommand AddLayer(CadLayer value) => new EditDocumentCommand(Strings.AddLayer, doc =>
    {
        Unique(value.Name, doc.Layers.Values.Select(l => l.Name));
        return doc with { Layers = doc.Layers.Add(value.Id, value) };
    });
    public static ICadDocumentCommand UpdateLayer(CadLayer value) => new EditDocumentCommand(Strings.EditLayer, doc =>
    {
        var previous = doc.Layers[value.Id]; Unique(value.Name, doc.Layers.Values.Where(l => l.Id != value.Id).Select(l => l.Name));
        return previous == value ? doc : doc with { Layers = doc.Layers.SetItem(value.Id, value) };
    });
    public static ICadDocumentCommand DeleteLayer(LayerId id) => new EditDocumentCommand(Strings.DeleteLayer, doc =>
    {
        if (!doc.Layers.ContainsKey(id)) throw new CadValidationException(Strings.SelectLayer);
        if (doc.Layers.Count == 1) throw new CadValidationException(Strings.LastLayerRequired);
        if (LayerReferences(doc, id) > 0) throw new CadValidationException(Strings.ResourceInUse);
        return doc with { Layers = doc.Layers.Remove(id) };
    });
    public static ICadDocumentCommand AddMaterial(CadMaterial value) => new EditDocumentCommand(Strings.AddMaterial, doc =>
    {
        Unique(value.Name, doc.Materials.Values.Select(m => m.Name)); CadGuard.Positive(value.DensityKgPerMm3);
        return doc with { Materials = doc.Materials.Add(value.Id, value) };
    });
    public static ICadDocumentCommand UpdateMaterial(CadMaterial value) => new EditDocumentCommand(Strings.EditMaterial, doc =>
    {
        var previous = doc.Materials[value.Id]; Unique(value.Name, doc.Materials.Values.Where(m => m.Id != value.Id).Select(m => m.Name));
        CadGuard.Positive(value.DensityKgPerMm3);
        return previous == value ? doc : doc with { Materials = doc.Materials.SetItem(value.Id, value) };
    });
    public static ICadDocumentCommand DeleteMaterial(MaterialId id) => new EditDocumentCommand(Strings.DeleteMaterial, doc =>
    {
        if (!doc.Materials.ContainsKey(id)) throw new CadValidationException(Strings.SelectMaterial);
        if (MaterialReferences(doc, id) > 0) throw new CadValidationException(Strings.ResourceInUse);
        return doc with { Materials = doc.Materials.Remove(id) };
    });
    public static int LayerReferences(DocumentSnapshot doc, LayerId id) => doc.Bodies.Values.Count(b => b.LayerId == id)
        + doc.Features.Values.Count(f => f.OutputMetadata?.Layer == id);
    public static int MaterialReferences(DocumentSnapshot doc, MaterialId id) => doc.Bodies.Values.Count(b => b.MaterialId == id)
        + doc.Features.Values.Count(f => f.OutputMetadata?.Material == id);
    private static void Unique(string name, IEnumerable<string> existing)
    {
        CadGuard.Name(name);
        if (existing.Any(n => string.Equals(n.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))) throw new CadValidationException(Strings.DuplicateResourceName);
    }
}
