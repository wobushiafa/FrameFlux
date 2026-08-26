#if !ANDROID
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using FrameFlux.FFmpeg;

namespace FrameFlux.Avalonia;

internal sealed class WindowsD3D11RtspVideoView : NativeControlHost, IDisposable
{
    private readonly RtspSource _source;
    private readonly RtspSessionOptions _options;
    private readonly WindowsD3D11Presenter _presenter = new();
    private RtspStreamClient? _client;
    private double _volume;
    private bool _isMuted;
    private Stretch _stretch;
    private bool _disposed;

    internal WindowsD3D11RtspVideoView(
        RtspSource source,
        RtspSessionOptions options,
        Stretch stretch)
    {
        _source = source;
        _options = options;
        _volume = options.Volume;
        _isMuted = options.IsMuted;
        _stretch = stretch;
    }

    internal event EventHandler<RtspConnectionStateChangedEventArgs>? ConnectionStateChanged;
    internal event EventHandler<RtspStreamErrorEventArgs>? StreamError;
    internal event EventHandler<bool>? HardwareAccelerationChanged;

    internal string HardwareDiagnostics => _client?.HardwareDiagnostics ?? "Not started";

    internal Stretch Stretch
    {
        get => _stretch;
        set => _stretch = value;
    }

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is not null)
        {
            return;
        }

        var client = new RtspStreamClient(
            _source.Uri.ToString(),
            new RtspStreamOptions
            {
                UseHardwareAcceleration = true,
                HardwareAccelerationMode = RtspHardwareAccelerationMode.Enabled,
                RenderMode = RtspRenderMode.NativeSurface,
                FallbackToSoftwareDecoding = false,
                Transport = _options.Transport.ToString().ToLowerInvariant(),
                OpenTimeoutMilliseconds = ToMilliseconds(_options.OpenTimeout),
                EndpointProbeTimeoutMilliseconds = ToMilliseconds(_options.EndpointProbeTimeout),
                ReadTimeoutMilliseconds = ToMilliseconds(_options.ReadTimeout),
                ReconnectDelayMilliseconds = ToMilliseconds(_options.ReconnectDelay),
                MaxConcurrentOpenStreams = _options.MaxConcurrentOpenStreams,
                MaxFramesPerSecond = _options.MaxFramesPerSecond,
                MaxVideoWidth = _options.MaxVideoWidth,
                MaxVideoHeight = _options.MaxVideoHeight,
                LowLatency = _options.LowLatency,
                EnableAudio = _options.EnableAudio,
                Volume = _volume,
                IsMuted = _isMuted
            });
        client.OnFrameLeaseReceived += OnFrameReceived;
        client.ConnectionStateChanged += OnConnectionStateChanged;
        client.StreamError += OnStreamError;
        client.HardwareAccelerationChanged += OnHardwareAccelerationChanged;
        client.SetFrameDeliveryEnabled(true);
        _client = client;
        client.Start();
    }

    internal void SetVolume(double volume)
    {
        _volume = Math.Clamp(volume, 0d, 1d);
        _client?.SetVolume(_volume);
    }

    internal void SetMuted(bool muted)
    {
        _isMuted = muted;
        _client?.SetMuted(muted);
    }

    internal void Stop()
    {
        var client = _client;
        _client = null;
        if (client is null)
        {
            return;
        }

        client.OnFrameLeaseReceived -= OnFrameReceived;
        client.ConnectionStateChanged -= OnConnectionStateChanged;
        client.StreamError -= OnStreamError;
        client.HardwareAccelerationChanged -= OnHardwareAccelerationChanged;
        client.Stop(waitForExit: true);
        client.Dispose();
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var window = _presenter.CreateWindow(parent.Handle);
        return new PlatformHandle(window, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _presenter.DestroyWindow();
        base.DestroyNativeControlCore(control);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _presenter.Dispose();
    }

    private void OnFrameReceived(RtspFrameLease lease)
    {
        try
        {
            var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
            _presenter.Present(
                lease,
                (int)Math.Ceiling(Bounds.Width * scaling),
                (int)Math.Ceiling(Bounds.Height * scaling),
                _stretch);
        }
        catch (Exception exception)
        {
            DispatchStreamError(new RtspStreamError(
                RtspStreamErrorKind.DecodeFailed,
                $"D3D11 presentation failed: {exception.Message}",
                Exception: exception,
                WillRetry: false));
        }
        finally
        {
            lease.Dispose();
        }
    }

    private void OnConnectionStateChanged(
        object? sender,
        RtspConnectionStateChangedEventArgs e) =>
        ConnectionStateChanged?.Invoke(this, e);

    private void OnStreamError(object? sender, RtspStreamErrorEventArgs e) =>
        StreamError?.Invoke(this, e);

    private void OnHardwareAccelerationChanged(object? sender, bool active) =>
        HardwareAccelerationChanged?.Invoke(this, active);

    private void DispatchStreamError(RtspStreamError error) =>
        Dispatcher.UIThread.Post(
            () => StreamError?.Invoke(this, new RtspStreamErrorEventArgs(error)),
            DispatcherPriority.Background);

    private static int ToMilliseconds(TimeSpan value) =>
        checked((int)Math.Min(value.TotalMilliseconds, int.MaxValue));
}
#endif
