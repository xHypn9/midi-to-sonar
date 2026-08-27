using SonarMidiController.App.Services;

namespace SonarController.Tests;

public sealed class AppSettingsStoreTests
{
    [Fact]
    public void PreservesWidgetVisibilityAndPosition()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "SonarMidiControllerTests",
            Guid.NewGuid().ToString("N"));
        var store = new AppSettingsStore(directory);

        store.Save(new AppSettings
        {
            StartWithWindows = true,
            StartMinimized = true,
            ShowWidget = true,
            LockWidget = true,
            WidgetBackgroundOpacity = 42,
            WidgetMuteOpacity = 73,
            HasWidgetPosition = true,
            WidgetLeft = 1920.5,
            WidgetTop = 45.25,
            HasWidgetSize = true,
            WidgetWidth = 780
        });

        var loaded = store.Load();

        Assert.True(loaded.StartWithWindows);
        Assert.True(loaded.StartMinimized);
        Assert.True(loaded.ShowWidget);
        Assert.True(loaded.LockWidget);
        Assert.Equal(42, loaded.WidgetBackgroundOpacity);
        Assert.Equal(73, loaded.WidgetMuteOpacity);
        Assert.True(loaded.HasWidgetPosition);
        Assert.Equal(1920.5, loaded.WidgetLeft);
        Assert.Equal(45.25, loaded.WidgetTop);
        Assert.True(loaded.HasWidgetSize);
        Assert.Equal(780, loaded.WidgetWidth);
    }
}
