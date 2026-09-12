using AvalonDock.Core;
using Cadoryx.Lang.Strings;
using Cadoryx.ViewModels.Services.Platform;

namespace Cadoryx.ViewModels.Toolboxes;

public sealed class ModelingToolboxViewModel : CadToolboxViewModelBase
{
    private CadDocumentViewModel? document;

    public ModelingToolboxViewModel(IToolboxIconProvider iconProvider)
        : base("toolbox.modeling", Strings.ModelingParameters, DockZone.RightTop, "", isOpenByDefault: true)
    {
        ArgumentNullException.ThrowIfNull(iconProvider);
        Icon=iconProvider.Modeling;
    }

    public CadDocumentViewModel? Document
    {
        get => document;
        private set => SetProperty(ref document, value);
    }

    public void Bind(CadDocumentViewModel? value) => Document = value;
}
