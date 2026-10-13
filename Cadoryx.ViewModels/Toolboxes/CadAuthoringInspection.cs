using System.Text.Json;
using System.Text.Json.Serialization;
using Cadoryx.Db;
using Cadoryx.Editor;

namespace Cadoryx.ViewModels.Toolboxes;

internal static class CadAuthoringInspection
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static bool Supports(string area) => area is "parameters" or "parameter_bindings" or
        "parameter_targets" or "datums" or "datum_associations" or "timeline" or "recipes" or "sketch_geometry" or "assembly_patterns";

    internal static string Read(CadDocumentSession session, string area, Guid? id, Guid? partId, int offset, int limit,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (offset is < 0 or > 100000 || limit is < 1 or > 50) throw new ArgumentException("Invalid inspection page budget.");
        var snapshot = session.Snapshot;
        object[] items;int? pageTotal=null;
        switch (area)
        {
            case "assembly_patterns":
                if(partId is not null)throw new ArgumentException("Assembly layouts have no partId filter.");
                items=snapshot.AssemblyPatterns.Values.Where(p=>id is null||p.Id==id).OrderBy(p=>p.Id).Select(p=>CadModelAuthoring.InspectAssembly(snapshot,p)).ToArray();break;
            case "recipes":
                items=snapshot.Definitions.Values.OfType<PartDefinition>().OrderBy(p=>p.Id.Value)
                    .Where(p=>partId is null||p.Id.Value==partId).SelectMany(p=>p.Features).Select(f=>snapshot.Features[f])
                    .Where(f=>id is null||f.Id.Value==id).Select(f=>CadModelAuthoring.InspectRecipe(snapshot,f)).ToArray();break;
            case "sketch_geometry":
                var sketches=snapshot.Sketches.Values.Where(s=>(id is null||s.Id.Value==id)&&(partId is null||s.PartId.Value==partId))
                    .OrderBy(s=>s.Id.Value).ToArray();pageTotal=sketches.Length;
                items=sketches.Skip(offset).Take(limit).Select(s=>CadModelAuthoring.InspectSketch(snapshot,s,token)).ToArray();break;
            case "parameters":
                if (partId is not null) throw new ArgumentException("parameters has no partId filter.");
                var values = ParameterGraph.Evaluate(snapshot, token);
                items = snapshot.Parameters.Parameters.Where(p => id is null || p.Id == id)
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id)
                    .Select(p => (object)new { p.Id, p.Name, p.Dimension, p.Expression, reference = p.Reference,
                        value = values[p.Id].Value, unit = Unit(p.Dimension),
                        dependencies = p.Reference is null ? ParameterExpressions.Dependencies(p.Expression) : [],
                        bindingIds = snapshot.Parameters.Bindings.Where(b => b.Parameter == p.Id).Select(b => b.Id).Order().ToArray() }).ToArray();
                break;
            case "parameter_bindings":
                if (partId is not null) throw new ArgumentException("parameter_bindings has no partId filter.");
                items = snapshot.Parameters.Bindings.Where(b => id is null || b.Id == id).OrderBy(b => b.Id)
                    .Select(b => { var quantity = ParameterTargets.Read(snapshot, b.Target); var dimension = Dimension(quantity);
                        return (object)new { b.Id, parameterId = b.Parameter, b.Target, dimension,
                            authoredValue = quantity.Value, unit = Unit(dimension) }; }).ToArray();
                break;
            case "parameter_targets":
                items = ParameterTargets.All(snapshot).Where(t => (id is null || t.Owner == id) &&
                        (partId is null || Part(snapshot, t).Value == partId))
                    .OrderBy(t => t.Kind).ThenBy(t => t.Owner).ThenBy(t => t.Property, StringComparer.Ordinal)
                    .ThenBy(t => t.Constraint).ThenBy(t => t.Curve).ThenBy(t => t.Index)
                    .Select(t => { var quantity = ParameterTargets.Read(snapshot, t); var dimension = Dimension(quantity);
                        return (object)new { target = t, ownerDefinitionId = Part(snapshot, t).ToString(), dimension,
                            value = quantity.Value, unit = Unit(dimension),
                            bindingId = snapshot.Parameters.Bindings.SingleOrDefault(b => b.Target == t)?.Id }; }).ToArray();
                break;
            case "datums":
                items = snapshot.Datums.Planes.Values.Select(p => (p.Id, p.PartId, p.Name, Type: "Plane", Author: (object)p))
                    .Concat(snapshot.Datums.Axes.Values.Select(a => (a.Id, a.PartId, a.Name, Type: "Axis", Author: (object)a)))
                    .Concat(snapshot.Datums.Points.Values.Select(p => (p.Id, p.PartId, p.Name, Type: "Point", Author: (object)p)))
                    .Where(p => (id is null || p.Id == id) && (partId is null || p.PartId.Value == partId))
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id)
                    .Select(p => (object)new { p.Id, partId = p.PartId.ToString(), p.Name, type = p.Type, author = p.Author,
                        revision=DatumConstructions.Revision(snapshot.Datums,p.Id),coordinateSpace="part_local",edit=CadDatumJson.Edit(snapshot,p.Author),
                        sketchConsumers=snapshot.Datums.SketchBindings.Values.Where(b=>b.Datum==p.Id).OrderBy(b=>b.Sketch.Value).Select(b=>b.Sketch.ToString()).ToArray(),
                        workplaneConsumer=snapshot.Datums.WorkPlane?.Datum==p.Id?snapshot.Datums.WorkPlane.Occurrence.ToString():null }).ToArray();
                break;
            case "datum_associations":
                items=snapshot.Datums.SketchBindings.Values.Where(b=>(id is null||b.Datum==id)&&(partId is null||snapshot.Sketches[b.Sketch].PartId.Value==partId))
                    .OrderBy(b=>b.Sketch.Value).Select(b=>(object)new{type="Sketch",datumId=b.Datum,datumRevision=DatumConstructions.Revision(snapshot.Datums,b.Datum),
                        appliedRevision=b.Revision,sketchId=b.Sketch.ToString(),sketchRevision=snapshot.Sketches[b.Sketch].Revision,
                        partId=snapshot.Sketches[b.Sketch].PartId.ToString(),coordinateSpace="part_local",frame=CadDatumJson.Frame(snapshot.Sketches[b.Sketch].Plane)}).ToArray();
                if(snapshot.Datums.WorkPlane is {} work&&(id is null||work.Datum==id)&&(partId is null||snapshot.Datums.Planes[work.Datum].PartId.Value==partId))
                    items=[..items,new{type="Workplane",datumId=work.Datum,datumRevision=DatumConstructions.Revision(snapshot.Datums,work.Datum),appliedRevision=work.Revision,
                        partId=snapshot.Datums.Planes[work.Datum].PartId.ToString(),path=work.Occurrence.ToString(),coordinateSpace="world",
                        occurrenceTransform=work.OccurrenceTransform,frame=CadDatumJson.Frame(new(snapshot.Settings.WorkPlane.Origin,snapshot.Settings.WorkPlane.Rotation))}];
                break;
            case "timeline":
                var requested = new DefinitionId(partId ?? throw new ArgumentException("timeline requires an exact partId, not id. Query cad_inspect area=parts without id; copy the chosen items[].id into partId. id is only an optional feature filter within that part."));
                if (snapshot.Definitions.GetValueOrDefault(requested) is not PartDefinition part)
                    throw new ArgumentException("Exact partId is not a part definition. Query cad_inspect area=parts without id; explicitly choose a part and copy its items[].id into partId. Do not use documentId, bodyId, featureId or rootAssemblyId.");
                items = part.Features.Select((featureId, index) => (Feature: snapshot.Features[featureId], Index: index))
                    .Where(f => id is null || f.Feature.Id.Value == id)
                    .Select(f => (object)new { id = f.Feature.Id.ToString(), position = f.Index,
                        partId = part.Id.ToString(), f.Feature.Name, recipe = f.Feature.Recipe.GetType().Name,
                        outputBodyId = f.Feature.OutputBodyId.ToString(), revision = f.Feature.Result.Revision.ToString(),
                        inputs = f.Feature.Inputs.Select(input => input.ToString()).ToArray(),
                        f.Feature.IsStale, f.Feature.IsSuppressed }).ToArray();
                break;
            default: throw new ArgumentException("Unknown authoring inspection area.");
        }
        token.ThrowIfCancellationRequested();
        var cursor = session.FeatureCursor;
        return JsonSerializer.Serialize(new { success = true, documentId = snapshot.Id.ToString(), stateId = snapshot.StateId.ToString(),
            area, offset, limit, total = pageTotal??items.Length, hasMore = (long)offset + limit < (pageTotal??items.Length),
            units = new { length = "mm", angle = "rad", scalar = "1" }, authorState = true,
            rolledBack = cursor is not null,
            // Explicit null is an authoring expectation, not an optional omitted field.
            cursor = cursor is null ? JsonSerializer.SerializeToElement<object?>(null) :
                JsonSerializer.SerializeToElement(new { stateId = cursor.State.ToString(), partId = cursor.Part.ToString(), cursor.Position }, JsonOptions),
            items = pageTotal is not null?items:items.Skip(offset).Take(limit).ToArray() }, JsonOptions);
    }

    internal static string Unit(ParameterDimension dimension) => dimension switch
        { ParameterDimension.Length => "mm", ParameterDimension.Angle => "rad", _ => "1" };
    internal static ParameterDimension Dimension(ParameterQuantity quantity) =>
        Enum.GetValues<ParameterDimension>().Single(quantity.HasDimension);

    private static DefinitionId Part(DocumentSnapshot snapshot, ParameterTarget target) => target.Kind switch
    {
        ParameterTargetKind.Feature => snapshot.Features[new(target.Owner)].PartId,
        ParameterTargetKind.SketchConstraint or ParameterTargetKind.ExactSketchCurve or ParameterTargetKind.SketchRegion => snapshot.Sketches[new(target.Owner)].PartId,
        ParameterTargetKind.AssemblyPattern => snapshot.AssemblyPatterns[target.Owner].Owner,
        ParameterTargetKind.Datum => snapshot.Datums.Planes.TryGetValue(target.Owner, out var plane) ? plane.PartId :
            snapshot.Datums.Axes.TryGetValue(target.Owner, out var axis) ? axis.PartId : snapshot.Datums.Points[target.Owner].PartId,
        _ => throw new ArgumentException("Unknown target kind.")
    };
}
