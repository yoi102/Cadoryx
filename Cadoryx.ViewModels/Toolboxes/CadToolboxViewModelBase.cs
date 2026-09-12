using AvalonDock.Core;
using AvalonDock.Mvvm.CommunityToolkit;

namespace Cadoryx.ViewModels.Toolboxes;

public abstract class CadToolboxViewModelBase : ObservableToolboxBase
{
    protected CadToolboxViewModelBase(
        string contentId,
        string title,
        DockZone zone,
        string icon,
        bool isOpenByDefault = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        Id = contentId;
        Title = title;
        ToolTipText = title;
        Zone = zone;
        Icon = icon;
        IsOpenByDefault = isOpenByDefault;
        CanClose = false;
    }

    public string ContentId => Id;
}
