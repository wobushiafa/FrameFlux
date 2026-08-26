using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace FrameFlux.Demo.Avalonia.Android;

[Activity(
    Label = "FrameFlux",
    Theme = "@style/FrameFluxTheme.NoActionBar",
    MainLauncher = true,
    ConfigurationChanges =
        ConfigChanges.Orientation |
        ConfigChanges.ScreenSize |
        ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity
{
}
