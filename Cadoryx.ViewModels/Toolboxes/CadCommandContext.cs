using Cadoryx.CommandLine;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Rendering;

namespace Cadoryx.ViewModels.Toolboxes;

internal sealed class CadCommandContext(CadDocumentViewModel document) : ICadCommandContext
{
    public string DocumentName => document.Session.Snapshot.Name;
    public int BodyCount => document.Session.Snapshot.Bodies.Count;
    public int SelectionCount => document.Selection.Items.Length;
    public bool CanUndo => document.Session.CanUndo;
    public bool CanRedo => document.Session.CanRedo;
    public IReadOnlyList<string> ListBodies(int limit) => document.Session.Snapshot.Bodies.Values
        .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
        .Take(Math.Clamp(limit, 1, 200))
        .Select(x => $"{x.Name} [{x.Id}]").ToArray();
    public IReadOnlyList<string> FindBodies(string query, int limit) =>
        BodyInstances().Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(limit, 1, 200)).Select(x => x.Breadcrumb).ToArray();
    public IReadOnlyList<string> ListSelection(int limit) => document.Selection.Items.Take(limit)
        .Select(x => document.Session.Snapshot.Bodies.TryGetValue(x.BodyId, out var body)
            ? $"{body.Name} [{body.Id}]" : $"Stale body [{x.BodyId}]").ToArray();
    public CadCommandResult SelectBody(string exactName)
    {
        var exact = BodyInstances().Where(x =>
            string.Equals(x.Name, exactName, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (exact.Length != 1) return new(false, exact.Length == 0
            ? "Cannot select: no body with that exact name." : "Cannot select: name is ambiguous across body instances.");
        document.Selection.Replace([exact[0].Body!]);
        return new(true, $"Selected: {exact[0].Breadcrumb}");
    }
    private IEnumerable<ModelSearchHit> BodyInstances()
    {
        var snapshot = document.Session.Snapshot;
        var breadcrumbs = new Dictionary<OccurrencePath, string>();
        foreach (var occurrence in snapshot.EnumerateOccurrences())
        {
            var parent = new OccurrencePath(snapshot.Id, occurrence.Path.Slots.RemoveAt(occurrence.Path.Slots.Length - 1));
            var breadcrumb = (breadcrumbs.TryGetValue(parent, out var prefix) ? prefix + " / " : "") + occurrence.Name;
            breadcrumbs[occurrence.Path] = breadcrumb;
            if (snapshot.Definitions[occurrence.DefinitionId] is not PartDefinition part) continue;
            foreach (var id in part.Bodies)
            {
                var body = snapshot.Bodies[id];
                yield return new ModelSearchHit(body.Name, breadcrumb + " / " + body.Name, occurrence.Path,
                    new SelectionTarget(occurrence.Path, id, body.Geometry.Revision));
            }
        }
    }
    public Task UndoAsync() => document.Session.UndoAsync();
    public Task RedoAsync() => document.Session.RedoAsync();
    public void Fit() => document.FitView();
    public void SetView(string direction) => document.SetView(direction.ToUpperInvariant() switch
    {
        "TOP" => CadProjection.Top, "FRONT" => CadProjection.Front,
        "RIGHT" => CadProjection.Right, "BACK" => CadProjection.Back,
        "LEFT" => CadProjection.Left, "BOTTOM" => CadProjection.Bottom,
        _ => CadProjection.Axonometric
    });
    public void ClearSelection() => document.Selection.Replace([]);
    public void StartInteractiveTool(string kind)
    {
        if (document.IsDetached || document.IsReadOnly || document.IsClosingRequested)
            throw new InvalidOperationException("The active document cannot be edited.");
        document.StartTool(string.Equals(kind, "BOX", StringComparison.OrdinalIgnoreCase) ? "Box" : "Cylinder");
        if (!document.IsViewportConstructing)
            throw new InvalidOperationException("The interactive construction could not start in the current document state.");
    }
    public bool CancelInteractiveTool()
    {
        if (!document.IsViewportConstructing) return false;
        document.CancelViewportConstruction();
        return true;
    }
    public Task CreateBoxAsync(string name, double width, double depth, double height, double x, double y, double z) =>
        CreateAsync(new BoxRecipe(width, depth, height, RigidTransform3d.Translate(x, y, z)), name);
    public Task CreateCylinderAsync(string name, double radius, double height, double x, double y, double z) =>
        CreateAsync(new CylinderRecipe(radius, height, RigidTransform3d.Translate(x, y, z)), name);
    private Task CreateAsync(GeometryRecipe recipe, string name)
    {
        if (document.IsDetached || document.IsReadOnly || document.IsClosingRequested)
            throw new InvalidOperationException("The active document cannot be edited.");
        return document.Session.ExecuteAsync(new AddBodyCommand(recipe, name,
            document.SelectedTargetPart, document.SelectedCreationLayer, document.SelectedCreationMaterial));
    }
}
