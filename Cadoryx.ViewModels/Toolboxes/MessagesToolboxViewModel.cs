using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using AvalonDock.Core;
using Cadoryx.ViewModels.Services.Platform.Notifications;

namespace Cadoryx.ViewModels.Toolboxes;

public partial class MessagesToolboxViewModel : CadToolboxViewModelBase
{
    public MessagesToolboxViewModel(ICadMessageLog messageLog)
        : base("toolbox.messages", "消息", DockZone.BottomLeft, "≡", isOpenByDefault: true)
    {
        ArgumentNullException.ThrowIfNull(messageLog);
        ClearCommand=new CommunityToolkit.Mvvm.Input.RelayCommand(messageLog.Clear);
        if (messageLog.Entries.Count == 0)
        {
            messageLog.Add("Cadoryx UI 工作区已加载。", CadMessageLevel.Information, "Cadoryx");
            messageLog.Add("支持 STEP/IGES 导入，STEP/IGES/STL 导出及 Cadoryx 原生文档。", CadMessageLevel.Information, "Cadoryx");
        }

        Messages = messageLog.Entries;
    }

    public ReadOnlyObservableCollection<CadMessageEntry> Messages { get; }
    public CommunityToolkit.Mvvm.Input.IRelayCommand ClearCommand {get;}
}
