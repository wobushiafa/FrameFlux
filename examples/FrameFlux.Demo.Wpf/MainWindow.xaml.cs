using System.Windows;
using FrameFlux;
using FrameFlux.FFmpeg;

namespace FrameFlux.Demo.Wpf;

public partial class MainWindow : Window
{
    private bool _configuringPlaybackModes;

    public MainWindow()
    {
        InitializeComponent();
        Player.PlayerFactory = new FfmpegMediaPlayerFactory();
        var options = new MediaOpenOptions
        {
            LowLatency = true,
            Transport = MediaTransport.Tcp,
            HardwareAcceleration = MediaHardwareAcceleration.Enabled,
            FallbackToSoftwareDecoding = false,
            RenderPreference = MediaRenderPreference.CompositedGpu
        };
        Player.OpenOptions = options;
        _configuringPlaybackModes = true;
        HardwareModeComboBox.ItemsSource =
            Enum.GetValues<MediaHardwareAcceleration>();
        RenderModeComboBox.ItemsSource =
            Enum.GetValues<MediaRenderPreference>();
        HardwareModeComboBox.SelectedItem = options.HardwareAcceleration;
        RenderModeComboBox.SelectedItem = options.RenderPreference;
        _configuringPlaybackModes = false;
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

    private void PlaybackMode_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_configuringPlaybackModes ||
            HardwareModeComboBox.SelectedItem is not
                MediaHardwareAcceleration hardwareAcceleration ||
            RenderModeComboBox.SelectedItem is not
                MediaRenderPreference renderPreference)
        {
            return;
        }

        var overlayAttached = Player.Children.Contains(VideoOverlay);
        if (renderPreference == MediaRenderPreference.NativeSurface)
        {
            if (overlayAttached)
            {
                Player.Children.Remove(VideoOverlay);
            }
        }
        else if (!overlayAttached)
        {
            Player.Children.Add(VideoOverlay);
        }

        Player.OpenOptions = Player.OpenOptions with
        {
            HardwareAcceleration = hardwareAcceleration,
            RenderPreference = renderPreference
        };
    }

    private void Player_PlaybackStateChanged(object? sender, MediaPlaybackStateChangedEventArgs eventArgs) =>
        StatusTextBlock.Text =
            $"State: {eventArgs.NewState} | Renderer: {Player.ActiveRendererId ?? "none"} | HW: {Player.IsHardwareAccelerationActive}";

    private void Player_PlaybackError(object? sender, MediaPlaybackErrorEventArgs eventArgs) =>
        StatusTextBlock.Text = $"{eventArgs.Error.Code}: {eventArgs.Error.Message}";

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        await Player.DisposeAsync();
    }
}
