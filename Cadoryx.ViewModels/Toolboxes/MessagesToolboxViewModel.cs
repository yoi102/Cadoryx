using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using AvalonDock.Core;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.Lang.Strings;

namespace Cadoryx.ViewModels.Toolboxes;

public partial class MessagesToolboxViewModel : CadToolboxViewModelBase
{
    public MessagesToolboxViewModel(ICadMessageLog messageLog)
        : base("toolbox.messages", Strings.Messages, DockZone.BottomLeft, "≡", isOpenByDefault: true)
    {
        ArgumentNullException.ThrowIfNull(messageLog);
        ClearCommand=new CommunityToolkit.Mvvm.Input.RelayCommand(messageLog.Clear);
        if (messageLog.Entries.Count == 0)
        {
            messageLog.Add(Strings.WorkspaceReadyMessage, CadMessageLevel.Information, "Cadoryx");
            messageLog.Add(Strings.ImportExportSupportMessage, CadMessageLevel.Information, "Cadoryx");
        }

        Messages = messageLog.Entries;
    }

    public ReadOnlyObservableCollection<CadMessageEntry> Messages { get; }
    public CommunityToolkit.Mvvm.Input.IRelayCommand ClearCommand {get;}
}
