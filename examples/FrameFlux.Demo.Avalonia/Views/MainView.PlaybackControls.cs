using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace FrameFlux.Demo.Avalonia.Views;

public sealed partial class MainView
{
    private const double TimelineMediaStepMilliseconds = 100d;
    private const double MinimumTimelineRefreshMilliseconds = 16d;
    private const double MaximumTimelineRefreshMilliseconds = 250d;
    private readonly double[] _playbackRates = [0.25d, 0.5d, 1d, 1.5d, 2d, 3d, 4d];
    private DispatcherTimer? _positionTimer;
    private bool _seeking;

    private void InitializePlaybackControls()
    {
        PlaybackRateComboBox.ItemsSource = _playbackRates.Select(rate => $"{rate:0.##}x").ToArray();
        PlaybackRateComboBox.SelectedIndex = Array.IndexOf(_playbackRates, 1d);
        _positionTimer = new DispatcherTimer
        {
            Interval = GetTimelineRefreshInterval(1d)
        };
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
        if (PlaybackRateComboBox.SelectedIndex is < 0 ||
            PlaybackRateComboBox.SelectedIndex >= _playbackRates.Length)
        {
            return;
        }

        var rate = _playbackRates[PlaybackRateComboBox.SelectedIndex];
        if (Player.Capabilities.CanChangePlaybackRate || !Player.Capabilities.IsLive)
        {
            Player.PlaybackRate = rate;
        }
    }

    private void RefreshTimeline()
    {
        if (_positionTimer is not null)
        {
            var interval = GetTimelineRefreshInterval(Player.PlaybackRate);
            if (_positionTimer.Interval != interval)
            {
                _positionTimer.Interval = interval;
            }
        }

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

    private static TimeSpan GetTimelineRefreshInterval(double playbackRate)
    {
        var rate = double.IsFinite(playbackRate) && playbackRate > 0d
            ? playbackRate
            : 1d;
        return TimeSpan.FromMilliseconds(Math.Clamp(
            TimelineMediaStepMilliseconds / rate,
            MinimumTimelineRefreshMilliseconds,
            MaximumTimelineRefreshMilliseconds));
    }

    private static string FormatTime(TimeSpan value) =>
        value.TotalHours >= 1d
            ? $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";
}
