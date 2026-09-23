using System.Text.Json;
using Cadoryx.ViewModels.Services.Platform.Settings;
using Xunit;

namespace Cadoryx.Tests;

public sealed class ViewCubeSettingsTests
{
    [Fact]
    public void RotationStepDefaultsTo45AndSurvivesSettingsPersistence()
    {
        var settings=CadoryxApplicationSettings.CreateDefault();
        Assert.Equal(45,settings.Viewport.ViewCubeRotationDegrees);
        settings.Viewport.ViewCubeRotationDegrees=30;
        Assert.Equal(30,settings.Clone().Viewport.ViewCubeRotationDegrees);
        var restored=JsonSerializer.Deserialize<CadoryxApplicationSettings>(JsonSerializer.Serialize(settings))!;
        restored.Normalize();
        Assert.Equal(30,restored.Viewport.ViewCubeRotationDegrees);
        restored.Viewport.ViewCubeRotationDegrees=0;
        restored.Normalize();
        Assert.Equal(45,restored.Viewport.ViewCubeRotationDegrees);
    }
}
