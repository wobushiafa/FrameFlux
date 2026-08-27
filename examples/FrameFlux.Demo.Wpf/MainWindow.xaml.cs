using System.Windows;
using FrameFlux;
using FrameFlux.FFmpeg;

namespace FrameFlux.Demo.Wpf;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Player.PlayerFactory = new FfmpegMediaPlayerFactory();
        Player.OpenOptions = new MediaOpenOptions
        {
            LowLatency = true,
            Transport = MediaTransport.Tcp,
            HardwareAcceleration = MediaHardwareAcceleration.Enabled,
            FallbackToSoftwareDecoding = false,
            RenderPreference = MediaRenderPreference.NativeSurface
        };
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Player.Source = MediaSource.Parse(SourceTextBox.Text);
            await Player.StartAsync();
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = exception.Message;
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e) => await Player.StopAsync();

    private void Player_PlaybackStateChanged(object? sender, MediaPlaybackStateChangedEventArgs eventArgs) =>
        StatusTextBlock.Text =
            $"State: {eventArgs.NewState} | HW: {Player.IsHardwareAccelerationActive} | {Player.HardwareDiagnostics}";

    private void Player_PlaybackError(object? sender, MediaPlaybackErrorEventArgs eventArgs) =>
        StatusTextBlock.Text = $"{eventArgs.Error.Code}: {eventArgs.Error.Message}";

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        await Player.DisposeAsync();
    }
}
