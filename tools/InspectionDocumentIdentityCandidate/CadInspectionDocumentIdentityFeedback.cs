using System.Text.Json;
using Cadoryx.Db;

namespace Cadoryx.ViewModels.Toolboxes;

// Runs only after the original inspection and its parsing, owner and lifecycle
// guards. A shared GUID is accepted when the requested typed namespace exists.
internal static class CadInspectionDocumentIdentityFeedback
{
    internal static string? Read(DocumentSnapshot snapshot, string area, Guid? id,
        Guid? partId, int offset, int limit)
    {
        string? role = null;
        bool exists = false;
        if (id == snapshot.Id.Value)
        {
            (role, exists) = area switch
            {
                "parts" => ("part definition", IsPart(snapshot, id.Value)),
                "bodies" or "selection" => ("body", snapshot.Bodies.ContainsKey(new(id.Value))),
                "features" or "recipes" or "timeline" => ("feature", snapshot.Features.ContainsKey(new(id.Value))),
                "layers" => ("layer", snapshot.Layers.ContainsKey(new(id.Value))),
                "materials" => ("material", snapshot.Materials.ContainsKey(new(id.Value))),
                "sketches" or "sketch_geometry" => ("sketch", snapshot.Sketches.ContainsKey(new(id.Value))),
                "occurrences" => ("component slot", HasSlot(snapshot, id.Value)),
                "assembly" => ("component slot or assembly constraint", HasSlot(snapshot, id.Value) || snapshot.AssemblyConstraints.ContainsKey(new(id.Value))),
                "parameters" => ("parameter", snapshot.Parameters.Parameters.Any(p => p.Id == id)),
                "parameter_bindings" => ("parameter binding", snapshot.Parameters.Bindings.Any(b => b.Id == id)),
                "parameter_targets" => ("parameter target owner", HasTargetOwner(snapshot, id.Value)),
                "datums" or "datum_associations" => ("datum", HasDatum(snapshot, id.Value)),
                "assembly_patterns" => ("assembly pattern", snapshot.AssemblyPatterns.ContainsKey(id.Value)),
                _ => (null, false)
            };
        }
        if (role is not null && !exists)
        {
            var arguments = new Dictionary<string, object> { ["area"] = area, ["offset"] = offset, ["limit"] = limit };
            if (partId is { } owner) arguments["partId"] = owner.ToString("D");
            return JsonSerializer.Serialize(new
            {
                success = false, code = "inspection_document_id_filter", area, field = "id",
                message = $"id selects a {role}, not documentId. Read the same inspection without id, then explicitly choose the returned object identity if a filter is needed.",
                reinspection = new { toolName = "cad_inspect", arguments },
                nextStep = "The suggested read was not executed. No object was selected, identity replaced or request retried."
            });
        }
        if (partId == snapshot.Id.Value && UsesPartFilter(area) &&
            !(area == "parameter_targets" ? snapshot.Definitions.ContainsKey(new(partId.Value)) : IsPart(snapshot, partId.Value)))
        {
            var targets = area == "parameter_targets";
            return JsonSerializer.Serialize(new
            {
                success = false, code = "inspection_document_part_filter", area, field = "partId",
                message = targets
                    ? "partId filters the target owner definition (part or assembly), not documentId. Read parameter_targets without partId, then explicitly choose the actual items[].ownerDefinitionId."
                    : "partId selects a part definition, not documentId. Read parts without id, then explicitly choose a part and copy its items[].id into partId.",
                reinspection = new { toolName = "cad_inspect", arguments = new { area = targets ? "parameter_targets" : "parts", offset = 0, limit } },
                nextStep = "The suggested read was not executed. No owner was selected, identity replaced or request retried."
            });
        }
        return null;
    }

    private static bool IsPart(DocumentSnapshot snapshot, Guid id) => snapshot.Definitions.GetValueOrDefault(new(id)) is PartDefinition;
    private static bool HasSlot(DocumentSnapshot snapshot, Guid id) => snapshot.Definitions.Values.OfType<AssemblyDefinition>().Any(a => a.Children.Any(c => c.Id.Value == id));
    private static bool HasDatum(DocumentSnapshot snapshot, Guid id) => snapshot.Datums.Planes.ContainsKey(id) || snapshot.Datums.Axes.ContainsKey(id) || snapshot.Datums.Points.ContainsKey(id);
    private static bool HasTargetOwner(DocumentSnapshot snapshot, Guid id) => snapshot.Features.ContainsKey(new(id)) || snapshot.Sketches.ContainsKey(new(id)) || HasDatum(snapshot, id) || snapshot.AssemblyPatterns.ContainsKey(id);
    private static bool UsesPartFilter(string area) => area is "bodies" or "features" or "sketches" or "recipes" or "sketch_geometry" or "parameter_targets" or "datums" or "datum_associations";
}
