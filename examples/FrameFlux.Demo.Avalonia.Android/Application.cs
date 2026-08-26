using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace FrameFlux.Demo.Avalonia.Android;

[Application]
public sealed class Application : AvaloniaAndroidApplication<global::FrameFlux.Demo.Avalonia.App>
{
    public Application(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder).WithInterFont();
}
