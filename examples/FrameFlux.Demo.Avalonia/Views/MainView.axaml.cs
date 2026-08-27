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

    public MainView()
    {
        InitializeComponent();
        Player.PlayerFactory = new FfmpegMediaPlayerFactory();
        Player.PropertyChanged += Player_OnPropertyChanged;
        Player.OpenOptions = new MediaOpenOptions
        {
            LowLatency = true,
            Transport = MediaTransport.Tcp,
            HardwareAcceleration = MediaHardwareAcceleration.Enabled,
            RenderPreference = MediaRenderPreference.CompositedGpu,
            FallbackToSoftwareDecoding = false
        };
    }

    private async void StartButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Player.Source = MediaSource.Parse(SourceTextBox.Text ?? string.Empty);
            Player.IsPlaybackEnabled = true;
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
        if (e.Property == MediaView.IsHardwareAccelerationActiveProperty ||
            e.Property == MediaView.HardwareDiagnosticsProperty ||
            e.Property == MediaView.ActiveRendererIdProperty)
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
        var renderer = Player.ActiveRendererId ?? "none";
        SetStatus(
            $"{state} | Renderer: {renderer} | HW: {Player.IsHardwareAccelerationActive}",
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
