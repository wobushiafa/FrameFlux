using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using FrameFlux.Avalonia;
using FrameFlux.FFmpeg;

namespace FrameFlux.Demo.Avalonia.Views;

public sealed partial class MainView : UserControl
{
    private static readonly IBrush IdleBrush = new SolidColorBrush(Color.Parse("#6B7280"));
    private static readonly IBrush ActiveBrush = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush BusyBrush = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#EF4444"));
    private bool _configuringPlaybackModes;

    public MainView()
    {
        InitializeComponent();
        Player.PlayerFactory = new FfmpegMediaPlayerFactory();
        Player.PropertyChanged += Player_OnPropertyChanged;
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

    private async void StartButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Player.Source = MediaSource.Parse(SourceTextBox.Text ?? string.Empty);
            Player.AutoPlay = true;
            SetStatus("Opening stream", BusyBrush);
            await Player.StartAsync();
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, ErrorBrush);
        }
    }

    private async void StopButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await Player.StopAsync();
        SetStatus("Stopped", IdleBrush);
    }

    private void PlaybackMode_OnSelectionChanged(
        object? sender,
        SelectionChangedEventArgs e)
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

        Player.Overlay = presentationMode == MediaVideoPresentationMode.NativeSurface
            ? null
            : VideoOverlay;
        Player.OpenOptions = Player.OpenOptions with
        {
            Video = Player.OpenOptions.Video with { DecodingPolicy = decodingPolicy }
        };
        Player.PresentationMode = presentationMode;
    }

    private void Player_OnPlaybackStateChanged(
        object? sender,
        MediaPlaybackStateChangedEventArgs e)
    {
        var brush = e.NewState switch
        {
            MediaPlaybackState.Playing => ActiveBrush,
            MediaPlaybackState.Opening or MediaPlaybackState.Reconnecting => BusyBrush,
            MediaPlaybackState.Faulted => ErrorBrush,
            _ => IdleBrush
        };
        RefreshPlaybackStatus(e.NewState, brush);
    }

    private void Player_OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == MediaView.IsHardwareVideoDecodingActiveProperty ||
            e.Property == MediaView.VideoDecoderDiagnosticsProperty ||
            e.Property == MediaView.EffectivePresentationModeProperty)
        {
            RefreshPlaybackStatus(Player.State, GetStateBrush(Player.State));
        }
    }

    private void Player_OnPlaybackError(object? sender, MediaPlaybackErrorEventArgs e) =>
        SetStatus($"{e.Error.Code}: {e.Error.Message}", ErrorBrush);

    private void SetStatus(string text, IBrush brush)
    {
        StatusTextBlock.Text = text;
        StateIndicator.Background = brush;
    }

    private void RefreshPlaybackStatus(MediaPlaybackState state, IBrush brush)
    {
        var presentation = Player.EffectivePresentationMode?.ToString() ?? "none";
        SetStatus(
            $"{state} | Presentation: {presentation} | HW decode: {Player.IsHardwareVideoDecodingActive}",
            brush);
    }

    private static IBrush GetStateBrush(MediaPlaybackState state) => state switch
    {
        MediaPlaybackState.Playing => ActiveBrush,
        MediaPlaybackState.Opening or MediaPlaybackState.Reconnecting => BusyBrush,
        MediaPlaybackState.Faulted => ErrorBrush,
        _ => IdleBrush
    };
}
