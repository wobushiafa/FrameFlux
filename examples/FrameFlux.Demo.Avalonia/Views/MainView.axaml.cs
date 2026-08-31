using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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
    private string? _temporaryMediaPath;

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
                DecodingPolicy = MediaVideoDecodingPolicy.HardwarePreferred
            }
        };
        Player.OpenOptions = options;
        Player.PresentationMode = MediaVideoPresentationMode.Automatic;
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
        SetSourceCommandsEnabled(false);
        try
        {
            var source = MediaSource.Parse(SourceTextBox.Text ?? string.Empty);
            await StartSourceAsync(source);
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, ErrorBrush);
        }
        finally
        {
            SetSourceCommandsEnabled(true);
        }
    }

    private async void OpenFileButton_OnClick(object? sender, RoutedEventArgs e)
    {
        SetSourceCommandsEnabled(false);
        try
        {
            var topLevel = TopLevel.GetTopLevel(this) ??
                throw new InvalidOperationException("The file picker is unavailable.");
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "Open media file",
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new FilePickerFileType("Video files")
                        {
                            Patterns = ["*.mp4", "*.mkv", "*.mov", "*.avi", "*.webm"],
                            MimeTypes = ["video/*"]
                        }
                    ]
                });
            var file = files.FirstOrDefault();
            if (file is null)
            {
                return;
            }

            var path = file.TryGetLocalPath();
            string? temporaryPath = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                temporaryPath = await CopyToTemporaryFileAsync(file);
                path = temporaryPath;
            }

            SourceTextBox.Text = path;
            await StartSourceAsync(MediaSource.FromFile(path), temporaryPath);
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, ErrorBrush);
        }
        finally
        {
            SetSourceCommandsEnabled(true);
        }
    }

    private async Task StartSourceAsync(MediaSource source, string? temporaryPath = null)
    {
        await Player.StopAsync();
        DeleteSupersededTemporaryFile(source);
        _temporaryMediaPath = temporaryPath ?? _temporaryMediaPath;
        Player.Source = source;
        SourceKindTextBlock.Text = source.Uri.IsFile ? "FILE" : "LIVE";
        SourceKindIndicator.Background = source.Uri.IsFile ? IdleBrush : ErrorBrush;
        SetStatus(source.Uri.IsFile ? "Opening file" : "Opening stream", BusyBrush);
        await Player.StartAsync();
    }

    private static async Task<string> CopyToTemporaryFileAsync(IStorageFile file)
    {
        var extension = Path.GetExtension(file.Name);
        var path = Path.Combine(
            Path.GetTempPath(),
            $"frameflux-demo-{Guid.NewGuid():N}{extension}");
        await using var input = await file.OpenReadAsync();
        await using var output = File.Create(path);
        await input.CopyToAsync(output);
        return path;
    }

    private void DeleteSupersededTemporaryFile(MediaSource source)
    {
        if (_temporaryMediaPath is null ||
            source.Uri.IsFile &&
            string.Equals(
                _temporaryMediaPath,
                source.Uri.LocalPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        File.Delete(_temporaryMediaPath);
        _temporaryMediaPath = null;
    }

    private void SetSourceCommandsEnabled(bool enabled)
    {
        OpenFileButton.IsEnabled = enabled;
        StartButton.IsEnabled = enabled;
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
