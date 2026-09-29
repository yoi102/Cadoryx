using System.Text.Json;
using Cadoryx.ViewModels.Services.Platform.Settings;
using Cadoryx.ViewModels.Settings;
using Xunit;

namespace Cadoryx.Tests;

public sealed class RadialMenuSettingsTests
{
    [Fact]
    public void LegacyAndMalformedPagesNormalizeWithoutSharingArrays()
    {
        var legacy = JsonSerializer.Deserialize<CadoryxApplicationSettings>("{}")!;
        legacy.Normalize();
        Assert.True(legacy.RadialMenu.IsEnabled);
        Assert.Equal(8, legacy.RadialMenu.Middle.Length);
        Assert.Equal(8, legacy.RadialMenu.Alt.Length); // Old application settings had no Alt page.
        legacy.RadialMenu.Middle = [(CadoryxRadialAction)999, CadoryxRadialAction.Save];
        legacy.RadialMenu.Control = null!;
        legacy.RadialMenu.Alt = [(CadoryxRadialAction)999];
        legacy.Normalize();
        Assert.Equal(CadoryxRadialAction.None, legacy.RadialMenu.Middle[0]);
        Assert.Equal(CadoryxRadialAction.Save, legacy.RadialMenu.Middle[1]);
        Assert.Equal(8, legacy.RadialMenu.Control.Length);
        Assert.Equal(CadoryxRadialAction.None, legacy.RadialMenu.Alt[0]);
        Assert.Equal(8, legacy.RadialMenu.Alt.Length);
        var copy = legacy.Clone();
        copy.RadialMenu.Middle[1] = CadoryxRadialAction.Undo;
        Assert.Equal(CadoryxRadialAction.Save, legacy.RadialMenu.Middle[1]);
    }

    [Fact]
    public void EditingFourPagesDoesNotApplyUntilSavedAndResetRestoresDefaults()
    {
        var settings = new CadoryxApplicationSettings();
        var store = new Store();
        var vm = new ApplicationSettingsViewModel(settings, store, _ => { });
        vm.RadialMenu.IsEnabled = false;
        foreach (var page in vm.RadialMenu.Pages)
            page.Slots[0].SelectedAction = vm.RadialMenu.ActionOptions.Single(o => o.Action == CadoryxRadialAction.Cylinder);
        Assert.True(settings.RadialMenu.IsEnabled);
        Assert.True(vm.TryApply());
        var restored = store.Load(); restored.Normalize();
        Assert.False(restored.RadialMenu.IsEnabled);
        Assert.All(Enum.GetValues<CadoryxRadialPage>(), page => Assert.Equal(CadoryxRadialAction.Cylinder, restored.RadialMenu.Get(page)[0]));
        vm.ResetToDefaults(); Assert.True(vm.TryApply());
        Assert.True(store.Load().RadialMenu.IsEnabled);
        Assert.Equal(CadoryxRadialAction.Fit, store.Load().RadialMenu.Middle[0]);
    }

    private sealed class Store : IApplicationSettingsStore
    {
        private string json = "{}";
        public CadoryxApplicationSettings Load() => JsonSerializer.Deserialize<CadoryxApplicationSettings>(json)!;
        public void Save(CadoryxApplicationSettings settings) => json = JsonSerializer.Serialize(settings);
    }
}
