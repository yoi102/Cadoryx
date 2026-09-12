using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using AvalonDock;
using AvalonDock.Controls;
using AvalonDock.Core;
using AvalonDock.Core.Serialization;
using AvalonDock.Layout;
using AvalonDock.Serializer.Json;

namespace Cadoryx.wpf.Services.Application;

/// <summary>
/// Persists the user's AvalonDock arrangement without coupling the ViewModels to WPF.
/// </summary>
public sealed class ToolboxLayoutPersistenceService
{
    private readonly string _layoutFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cadoryx",
        "toolbox-layout.json");

    public void Save(ToggleDockingManager dockingManager, IEnumerable<IDockable> anchorables)
    {
        ArgumentNullException.ThrowIfNull(dockingManager);
        ArgumentNullException.ThrowIfNull(anchorables);

        try
        {
            SynchronizeToolboxZones(dockingManager);
            foreach (var toolbox in anchorables.OfType<IToolbox>())
                toolbox.IsOpenByDefault = toolbox.IsOpen;

            var directory = Path.GetDirectoryName(_layoutFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var temporaryFilePath = $"{_layoutFilePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(
                           temporaryFilePath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None))
                {
                    new JsonLayoutSerializer(dockingManager).Serialize(stream);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryFilePath, _layoutFilePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryFilePath))
                    File.Delete(temporaryFilePath);
            }
        }
        catch (Exception ex) when (IsPersistenceException(ex))
        {
            Debug.WriteLine($"Failed to save Cadoryx toolbox layout: {ex}");
        }
    }

    public bool Restore(ToggleDockingManager dockingManager, IEnumerable<IDockable> anchorables)
    {
        ArgumentNullException.ThrowIfNull(dockingManager);
        ArgumentNullException.ThrowIfNull(anchorables);

        if (!File.Exists(_layoutFilePath))
            return false;

        try
        {
            var toolboxes = anchorables
                .OfType<IToolbox>()
                .Where(toolbox => !string.IsNullOrWhiteSpace(toolbox.Id))
                .ToDictionary(toolbox => toolbox.Id!, StringComparer.Ordinal);
            var startupDocuments = CaptureStartupDocuments(dockingManager);

            using var stream = new FileStream(
                _layoutFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            var serializer = new JsonLayoutSerializer(dockingManager);
            serializer.LayoutSerializationCallback += (_, args) =>
            {
                if (args.Model is ISerializableLayoutAnchorable)
                {
                    var contentId = args.Model.ContentId?.Trim();
                    if (!string.IsNullOrWhiteSpace(contentId) && toolboxes.TryGetValue(contentId, out var toolbox))
                        args.Content = toolbox;
                    else
                        args.Cancel = true;

                    return;
                }

                if (args.Model is ISerializableLayoutDocument document)
                {
                    var contentId = document.ContentId?.Trim();
                    if (!string.IsNullOrWhiteSpace(contentId) && startupDocuments.TryGetValue(contentId, out var startupDocument))
                        args.Content = startupDocument.Content;
                    else
                        args.Cancel = true;
                }
            };

            serializer.Deserialize(stream);
            EnsureStartupDocuments(dockingManager, startupDocuments.Values);
            SynchronizeToolboxZones(dockingManager);
            return true;
        }
        catch (Exception ex) when (IsPersistenceException(ex))
        {
            Debug.WriteLine($"Failed to restore Cadoryx toolbox layout: {ex}");
            return false;
        }
    }

    private static Dictionary<string, StartupDocument> CaptureStartupDocuments(ToggleDockingManager dockingManager)
    {
        return dockingManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .Where(document => !string.IsNullOrWhiteSpace(document.ContentId) && document.Content is not null)
            .GroupBy(document => document.ContentId!.Trim(), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new StartupDocument(
                    group.Key,
                    group.First().Title,
                    group.First().ToolTip,
                    group.First().Content!),
                StringComparer.Ordinal);
    }

    private static void EnsureStartupDocuments(
        ToggleDockingManager dockingManager,
        IEnumerable<StartupDocument> startupDocuments)
    {
        var documentPane = dockingManager.Layout
            .Descendents()
            .OfType<LayoutDocumentPane>()
            .FirstOrDefault();
        if (documentPane is null)
            return;

        foreach (var startupDocument in startupDocuments)
        {
            var exists = dockingManager.Layout
                .Descendents()
                .OfType<LayoutDocument>()
                .Any(document => string.Equals(
                    document.ContentId,
                    startupDocument.ContentId,
                    StringComparison.Ordinal));
            if (exists)
                continue;

            documentPane.Children.Add(new LayoutDocument
            {
                ContentId = startupDocument.ContentId,
                Title = startupDocument.Title,
                ToolTip = startupDocument.ToolTip,
                Content = startupDocument.Content
            });
        }
    }

    private static bool IsPersistenceException(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            InvalidOperationException or
            NotSupportedException or
            System.Security.SecurityException or
            System.Text.Json.JsonException;

    private static void SynchronizeToolboxZones(DependencyObject root)
    {
        foreach (var buttonBar in FindVisualChildren<ToggleDockButtonBar>(root))
        {
            foreach (var button in buttonBar.Items.OfType<ToggleDockButton>())
            {
                if (button.Anchorable?.Content is IToolbox toolbox)
                    toolbox.Zone = button.Zone;
            }
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;

            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    private sealed record StartupDocument(
        string ContentId,
        string? Title,
        object? ToolTip,
        object Content);
}
