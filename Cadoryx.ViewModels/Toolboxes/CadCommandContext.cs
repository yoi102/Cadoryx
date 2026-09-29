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
    public int SelectionCount => document.Selection.Items.Length > 0 ? document.Selection.Items.Length :
        document.Selection.Occurrence is null ? 0 : 1;
    public bool CanUndo => document.Session.CanUndo;
    public bool CanRedo => document.Session.CanRedo;
    public IReadOnlyList<string> ListBodies(int limit) => document.Session.Snapshot.Bodies.Values
        .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
        .Take(Math.Clamp(limit, 1, 200))
        .Select(x => $"{x.Name} [{x.Id}]").ToArray();
    public IReadOnlyList<string> FindBodies(string query, int limit) =>
        BodyInstances().Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(limit, 1, 200)).Select(x => x.Breadcrumb).ToArray();
    public IReadOnlyList<string> ListSelection(int limit) => document.Selection.Items.Length == 0 &&
        document.Selection.Occurrence is {} path ? [$"Instance [{path}]"] :
        document.Selection.Items.Take(limit)
            .Select(x => document.Session.Snapshot.Bodies.TryGetValue(x.BodyId, out var body)
                ? $"{body.Name} [{body.Id}]" : $"Stale body [{x.BodyId}]").ToArray();
    public IReadOnlyList<string> ListParts(int limit) => document.Session.Snapshot.Definitions.Values
        .OfType<PartDefinition>().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Take(limit)
        .Select(x => $"{x.Name} [{x.Id}] | bodies: {x.Bodies.Length} | features: {x.Features.Length}").ToArray();
    public IReadOnlyList<string> ListFeatures(int limit) => document.Session.Snapshot.Features.Values
        .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Take(limit)
        .Select(x => $"{x.Name} [{x.Id}] | part: {x.PartId}").ToArray();
    public IReadOnlyList<string> ListLayers(int limit) => document.Session.Snapshot.Layers.Values
        .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Take(limit)
        .Select(x => $"{x.Name} [{x.Id}] | {(x.IsVisible ? "visible" : "hidden")} | {(x.IsLocked ? "locked" : "editable")}").ToArray();
    public IReadOnlyList<string> ListMaterials(int limit) => document.Session.Snapshot.Materials.Values
        .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Take(limit)
        .Select(x => $"{x.Name} [{x.Id}] | density: {x.DensityKgPerMm3.ToString(System.Globalization.CultureInfo.InvariantCulture)} kg/mm³").ToArray();
    public IReadOnlyList<string> ListSketches(int limit) => document.Session.Snapshot.Sketches.Values
        .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Take(limit)
        .Select(x => $"{x.Name} [{x.Id}] | part: {x.PartId}").ToArray();
    public IReadOnlyList<string> ListDrawings(int limit) => document.Session.Snapshot.DrawingSheets.Values
        .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Take(limit)
        .Select(x => $"{x.Name} [{x.Id:D}] | views: {x.Views.Length}").ToArray();
    public IReadOnlyList<string> ListOccurrences(int limit) => document.Session.Snapshot.EnumerateOccurrences()
        .Take(limit).Select(x => $"{x.Name} [{x.Path}]").ToArray();
    public string ScaleSummary
    {
        get
        {
            var report = document.ScaleReport;
            return $"Definitions: {report.Definitions} | instances: {report.Occurrences} | features: {report.Features} | assets: {report.UniqueAssets} ({report.AssetBytes} bytes)";
        }
    }
    public CadGridState Grid => new(document.GridVisible, document.GridSpacingMm, document.SnapToGrid);
    public Task SetGridAsync(bool? visible = null, double? spacingMm = null, bool? snap = null)
    {
        if (document.IsDetached || document.IsReadOnly || document.IsClosingRequested)
            throw new InvalidOperationException("The active document cannot be edited.");
        return document.SetGridAsync(visible, spacingMm, snap);
    }
    public string DisplayMode => document.CurrentDisplayMode.ToString().ToUpperInvariant();
    public void SetDisplayMode(string mode) => document.SetDisplay(
        mode.Equals("WIREFRAME", StringComparison.OrdinalIgnoreCase) ? CadDisplayMode.Wireframe : CadDisplayMode.Shaded);
    public CadCommandResult IsolateSelection()
    {
        if (!document.Review.IsolateCommand.CanExecute(null)) return new(false, "Select a body or instance first.");
        document.Review.IsolateCommand.Execute(null);return new(true, "Selection isolated.");
    }
    public CadCommandResult HideSelection()
    {
        if (!document.Review.HideCommand.CanExecute(null)) return new(false, "Select a body or instance first.");
        document.Review.HideCommand.Execute(null);return new(true, "Selection hidden.");
    }
    public CadCommandResult ShowAll()
    {
        if (!document.Review.ShowAllCommand.CanExecute(null)) return new(false, "Nothing is temporarily hidden.");
        document.Review.ShowAllCommand.Execute(null);return new(true, "All geometry restored.");
    }
    public CadCommandResult FocusSelection()
    {
        if (!document.Review.FocusCommand.CanExecute(null)) return new(false, "Select a body or instance first.");
        var visible = document.Review.Filter(document.Scene).Items;
        var hasVisibleSelection = document.Selection.Items.Any(selected =>
            visible.Any(item => item.Path.Equals(selected.Path) && item.BodyId == selected.BodyId));
        if (!hasVisibleSelection && document.Selection.Occurrence is {} path)
            hasVisibleSelection = visible.Any(item => item.Path.DocumentId == path.DocumentId &&
                item.Path.Slots.Length >= path.Slots.Length &&
                item.Path.Slots.Take(path.Slots.Length).SequenceEqual(path.Slots));
        if (!hasVisibleSelection) return new(false, "Selected geometry is not visible.");
        document.Review.FocusCommand.Execute(null);return new(true, "Selection focused.");
    }
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
