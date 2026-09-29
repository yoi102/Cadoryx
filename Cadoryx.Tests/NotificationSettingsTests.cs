using System.Text.Json;
using Cadoryx.ViewModels.Services.Platform.Settings;
using Cadoryx.ViewModels.Settings;
using Xunit;

namespace Cadoryx.Tests;

public sealed class NotificationSettingsTests
{
    [Fact]
    public void LegacyAndInvalidSettingsUseApplicationWindow()
    {
        var legacy=JsonSerializer.Deserialize<CadoryxApplicationSettings>("{\"General\":{\"CultureLcid\":2052}}")!;
        legacy.Normalize();
        Assert.Equal(CadNotificationAnchor.ApplicationWindow,legacy.General.NotificationAnchor);
        legacy.General.NotificationAnchor=(CadNotificationAnchor)999;
        legacy.Normalize();
        Assert.Equal(CadNotificationAnchor.ApplicationWindow,legacy.General.NotificationAnchor);
    }
    [Fact]
    public void SettingsEditIsIsolatedAndAppliedChoiceSurvivesPersistenceAndReset()
    {
        var settings=CadoryxApplicationSettings.CreateDefault();var store=new TestStore();
        CadoryxApplicationSettings? applied=null;
        var vm=new ApplicationSettingsViewModel(settings,store,value=>applied=value);
        vm.General.SelectedNotificationAnchor=vm.General.NotificationAnchorOptions.Single(o=>o.Anchor==CadNotificationAnchor.WindowsDesktop);
        Assert.Equal(CadNotificationAnchor.ApplicationWindow,settings.General.NotificationAnchor);
        Assert.True(vm.TryApply());
        Assert.Equal(CadNotificationAnchor.WindowsDesktop,applied!.General.NotificationAnchor);
        Assert.Equal(CadNotificationAnchor.WindowsDesktop,store.Load().Clone().General.NotificationAnchor);
        vm.ResetToDefaults();Assert.True(vm.TryApply());
        Assert.Equal(CadNotificationAnchor.ApplicationWindow,store.Load().General.NotificationAnchor);
    }
    private sealed class TestStore:IApplicationSettingsStore
    {
        private string json="{}";
        public CadoryxApplicationSettings Load()=>JsonSerializer.Deserialize<CadoryxApplicationSettings>(json)!;
        public void Save(CadoryxApplicationSettings settings)=>json=JsonSerializer.Serialize(settings);
    }
}
