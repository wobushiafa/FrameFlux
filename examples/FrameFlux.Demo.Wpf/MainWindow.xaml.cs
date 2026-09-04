using System.ComponentModel;
using System.Windows;
using FrameFlux;
using FrameFlux.FFmpeg;
using FrameFlux.WebRtc;
using Microsoft.Win32;

namespace FrameFlux.Demo.Wpf;

public partial class MainWindow : Window
{
    private static readonly IMediaPlayerFactory FfmpegPlayerFactory = new FfmpegMediaPlayerFactory();
    private static readonly IMediaPlayerFactory WebRtcPlayerFactory = new WebRtcMediaPlayerFactory();

    private bool _configuringPlaybackModes;
    private readonly DependencyPropertyDescriptor _hardwareDiagnosticsDescriptor;
    private readonly DependencyPropertyDescriptor _decoderDiagnosticsDescriptor;

    public MainWindow()
    {
        InitializeComponent();
        InitializePlaybackControls();
        Player.PlayerFactory = FfmpegPlayerFactory;
        _hardwareDiagnosticsDescriptor = DependencyPropertyDescriptor.FromProperty(
            FrameFlux.Wpf.MediaView.IsHardwareVideoDecodingActiveProperty,
            typeof(FrameFlux.Wpf.MediaView))!;
        _decoderDiagnosticsDescriptor = DependencyPropertyDescriptor.FromProperty(
            FrameFlux.Wpf.MediaView.VideoDecoderDiagnosticsProperty,
            typeof(FrameFlux.Wpf.MediaView))!;
        _hardwareDiagnosticsDescriptor.AddValueChanged(Player, Player_DiagnosticsChanged);
        _decoderDiagnosticsDescriptor.AddValueChanged(Player, Player_DiagnosticsChanged);
        var options = new MediaOpenOptions
        {
            Network = new MediaNetworkOptions
            {
                LatencyMode = MediaLatencyMode.Low,
                Transport = MediaTransport.Tcp
            },
            Video = new MediaVideoOptions
            {
                DecodingPolicy = MediaVideoDecodingPolicy.HardwareRequired
            }
        };
        Player.OpenOptions = options;
        Player.PresentationMode = MediaVideoPresentationMode.GpuComposition;
        _configuringPlaybackModes = true;
        HardwareModeComboBox.ItemsSource =
            Enum.GetValues<MediaVideoDecodingPolicy>();
        RenderModeComboBox.ItemsSource =
            Enum.GetValues<MediaVideoPresentationMode>();
        HardwareModeComboBox.SelectedItem = options.Video.DecodingPolicy;
        RenderModeComboBox.SelectedItem = Player.PresentationMode;
        _configuringPlaybackModes = false;
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (Player.State == MediaPlaybackState.Playing)
        {
            await Player.PauseAsync();
            return;
        }
        if (Player.State == MediaPlaybackState.Paused)
        {
            await Player.ResumeAsync();
            return;
        }

        SetSourceCommandsEnabled(false);
        try
        {
            await StartSourceAsync(MediaSource.Parse(SourceTextBox.Text));
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = exception.Message;
        }
        finally
        {
            SetSourceCommandsEnabled(true);
        }
    }

    private async void OpenFileButton_Click(object sender, RoutedEventArgs e)
    {
        SetSourceCommandsEnabled(false);
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "Open media file",
                Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.webm|All files|*.*",
                Multiselect = false
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            SourceTextBox.Text = dialog.FileName;
            await StartSourceAsync(MediaSource.FromFile(dialog.FileName));
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = exception.Message;
        }
        finally
        {
            SetSourceCommandsEnabled(true);
        }
    }

    private async Task StartSourceAsync(MediaSource source)
    {
        await Player.StopAsync();
        Player.PlayerFactory = IsWebRtcSource(source)
            ? WebRtcPlayerFactory
            : FfmpegPlayerFactory;
        Player.Source = source;
        SourceKindTextBlock.Text = source.Uri.IsFile ? "FILE" : "LIVE";
        SourceKindIndicator.Background = source.Uri.IsFile
            ? System.Windows.Media.Brushes.Gray
            : System.Windows.Media.Brushes.Red;
        StatusTextBlock.Text = source.Uri.IsFile ? "Opening file" : "Opening stream";
        await Player.StartAsync();
    }

    private static bool IsWebRtcSource(MediaSource source)
    {
        var uri = source.Uri;
        return uri.Scheme.Equals("webrtc", StringComparison.OrdinalIgnoreCase) ||
               uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase) ||
               uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase) ||
               uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
               uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
               uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase);
    }

    private void SetSourceCommandsEnabled(bool enabled)
    {
        OpenFileButton.IsEnabled = enabled;
        StartButton.IsEnabled = enabled;
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e) => await Player.StopAsync();

    private void PlaybackMode_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_configuringPlaybackModes ||
            HardwareModeComboBox.SelectedItem is not
                MediaVideoDecodingPolicy decodingPolicy ||
            RenderModeComboBox.SelectedItem is not
                MediaVideoPresentationMode presentationMode)
        {
            return;
        }

        var requiresGpuFrames = presentationMode is
            MediaVideoPresentationMode.NativeSurface or
            MediaVideoPresentationMode.GpuComposition;
        if (sender == HardwareModeComboBox &&
            decodingPolicy == MediaVideoDecodingPolicy.SoftwareOnly &&
            requiresGpuFrames)
        {
            presentationMode = MediaVideoPresentationMode.SoftwareBitmap;
            _configuringPlaybackModes = true;
            RenderModeComboBox.SelectedItem = presentationMode;
            _configuringPlaybackModes = false;
        }
        else if (sender == RenderModeComboBox &&
                 decodingPolicy == MediaVideoDecodingPolicy.SoftwareOnly &&
                 requiresGpuFrames)
        {
            decodingPolicy = MediaVideoDecodingPolicy.HardwareRequired;
            _configuringPlaybackModes = true;
            HardwareModeComboBox.SelectedItem = decodingPolicy;
            _configuringPlaybackModes = false;
        }

        var overlayAttached = Player.Children.Contains(VideoOverlay);
        if (presentationMode == MediaVideoPresentationMode.NativeSurface)
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
            Video = Player.OpenOptions.Video with { DecodingPolicy = decodingPolicy }
        };
        Player.PresentationMode = presentationMode;
    }

    private void Player_PlaybackStateChanged(object? sender, MediaPlaybackStateChangedEventArgs eventArgs) =>
        RefreshPlaybackStatus(eventArgs.NewState);

    private void Player_DiagnosticsChanged(object? sender, EventArgs eventArgs) =>
        RefreshPlaybackStatus(Player.State);

    private void RefreshPlaybackStatus(MediaPlaybackState state) =>
        StatusTextBlock.Text =
            $"State: {state} | Presentation: {Player.EffectivePresentationMode?.ToString() ?? "none"} | HW decode: {Player.IsHardwareVideoDecodingActive} | Decoder: {Player.VideoDecoderDiagnostics}";

    private void Player_PlaybackError(object? sender, MediaPlaybackErrorEventArgs eventArgs) =>
        StatusTextBlock.Text = $"{eventArgs.Error.Code}: {eventArgs.Error.Message}";

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _hardwareDiagnosticsDescriptor.RemoveValueChanged(Player, Player_DiagnosticsChanged);
        _decoderDiagnosticsDescriptor.RemoveValueChanged(Player, Player_DiagnosticsChanged);
        await Player.DisposeAsync();
    }
}
