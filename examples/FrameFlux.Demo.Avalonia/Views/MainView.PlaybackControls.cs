using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace FrameFlux.Demo.Avalonia.Views;

public sealed partial class MainView
{
    private readonly double[] _playbackRates = [0.5d, 1d, 1.5d, 2d];
    private DispatcherTimer? _positionTimer;
    private bool _seeking;

    private void InitializePlaybackControls()
    {
        PlaybackRateComboBox.ItemsSource = _playbackRates.Select(rate => $"{rate:0.#}x").ToArray();
        PlaybackRateComboBox.SelectedIndex = 1;
        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _positionTimer.Tick += (_, _) => RefreshTimeline();
        _positionTimer.Start();
    }

    private void PositionSlider_OnPointerPressed(object? sender, PointerPressedEventArgs e) =>
        _seeking = true;

    private async void PositionSlider_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _seeking = false;
        if (Player.Capabilities.CanSeek)
        {
            await Player.SeekAsync(TimeSpan.FromSeconds(PositionSlider.Value));
        }
    }

    private void PlaybackRateComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PlaybackRateComboBox.SelectedIndex is < 0 or > 3)
        {
            return;
        }

        var rate = _playbackRates[PlaybackRateComboBox.SelectedIndex];
        if (Player.Capabilities.CanChangePlaybackRate || !Player.Capabilities.IsLive)
        {
            Player.PlaybackRate = rate;
        }
        RateAudioNotice.IsVisible = rate != 1d && Player.Capabilities.CanChangePlaybackRate;
    }

    private void RefreshTimeline()
    {
        var position = Player.Position;
        var duration = Player.Duration ?? TimeSpan.Zero;
        PositionTextBlock.Text = FormatTime(position);
        DurationTextBlock.Text = FormatTime(duration);
        PositionSlider.IsEnabled = Player.Capabilities.CanSeek;
        PlaybackRateComboBox.IsEnabled = Player.Capabilities.CanChangePlaybackRate;
        PositionSlider.Maximum = Math.Max(1d, duration.TotalSeconds);
        if (!_seeking)
        {
            PositionSlider.Value = Math.Clamp(position.TotalSeconds, 0d, PositionSlider.Maximum);
        }
        StartButton.Content = Player.State == MediaPlaybackState.Playing ? "Pause" : "Play";
    }

    private static string FormatTime(TimeSpan value) =>
        value.TotalHours >= 1d
            ? $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";
}
