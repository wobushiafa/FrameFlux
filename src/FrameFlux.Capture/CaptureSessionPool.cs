using System.Diagnostics;

namespace FrameFlux.Capture;

internal sealed class CaptureSessionPool : IDisposable
{
    internal static CaptureSessionPool Shared { get; } = new();

    private readonly object _sync = new();
    private readonly Dictionary<CaptureSessionKey, CapturePhysicalSession> _sessions = [];
    private bool _disposed;

    internal CaptureSessionLease Acquire(
        CaptureSessionKey key,
        Func<CapturePhysicalSession> factory)
    {
        CapturePhysicalSession session;
        lock (_sync)
        {
            RuntimeCompat.NotDisposed(_disposed, this);
            if (!_sessions.TryGetValue(key, out session!))
            {
                session = factory();
                _sessions.Add(key, session);
            }

            session.AddPoolReference();
        }

        return new CaptureSessionLease(session, () => Release(key, session));
    }

    private ValueTask Release(CaptureSessionKey key, CapturePhysicalSession session)
    {
        var shouldDispose = false;
        lock (_sync)
        {
            if (session.ReleasePoolReference() == 0 &&
                _sessions.TryGetValue(key, out var current) &&
                ReferenceEquals(current, session))
            {
                _sessions.Remove(key);
                shouldDispose = true;
            }
        }

        return shouldDispose ? session.DisposeAsync() : default;
    }

    public void Dispose()
    {
        List<CapturePhysicalSession> sessions;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}

internal sealed record CaptureSessionKey(
    string CanonicalDeviceKey,
    MediaCapturePixelFormat? RequestedFormat,
    int? Width,
    int? Height,
    double? FrameRate,
    CaptureAudioMode AudioMode,
    string? AudioDeviceId,
    MediaVideoDecodingPolicy DecodingPolicy,
    bool AudioEnabled)
{
    internal static CaptureSessionKey Create(
        ParsedCaptureUri parsed,
        MediaOpenOptions options,
        string? resolvedDeviceId = null)
    {
        var canonicalId = !string.IsNullOrWhiteSpace(parsed.DeviceId)
            ? parsed.DeviceId
            : resolvedDeviceId;

        var deviceKey = !string.IsNullOrWhiteSpace(canonicalId)
            ? $"id:{canonicalId.Trim().ToLowerInvariant()}"
            : $"index:{parsed.DeviceIndex ?? 0}";
        return new CaptureSessionKey(
            deviceKey,
            parsed.PixelFormat,
            parsed.Width,
            parsed.Height,
            parsed.FrameRate,
            parsed.AudioMode,
            parsed.AudioDeviceId,
            options.Video.DecodingPolicy,
            options.Audio.IsEnabled);
    }
}

internal sealed class CaptureSessionLease : IAsyncDisposable
{
    private readonly Func<ValueTask> _releaseAction;
    private int _disposed;

    internal CaptureSessionLease(CapturePhysicalSession session, Func<ValueTask> releaseAction)
    {
        Session = session;
        _releaseAction = releaseAction;
    }

    internal CapturePhysicalSession Session { get; }

    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref _disposed, 1) == 0
            ? _releaseAction()
            : default;
}

internal sealed class CaptureSessionSubscriber
{
    private volatile IMediaVideoOutput? _lastFailedHardwareOutput;

    internal CaptureSessionSubscriber(
        Func<IMediaVideoOutput?> getVideoOutput,
        Func<MediaVideoDecodingPolicy> getDecodingPolicy,
        Func<bool> needsCpuFrame,
        Action<CaptureMediaFrameLease?, int, int> onFrame,
        Action<MediaPlaybackState> onStateChanged,
        Action<MediaPlaybackError> onError)
    {
        GetVideoOutput = getVideoOutput;
        GetDecodingPolicy = getDecodingPolicy;
        NeedsCpuFrame = needsCpuFrame;
        OnFrame = onFrame;
        OnStateChanged = onStateChanged;
        OnError = onError;
    }

    internal Func<IMediaVideoOutput?> GetVideoOutput { get; }
    internal Func<MediaVideoDecodingPolicy> GetDecodingPolicy { get; }
    internal Func<bool> NeedsCpuFrame { get; }
    internal Action<CaptureMediaFrameLease?, int, int> OnFrame { get; }
    internal Action<MediaPlaybackState> OnStateChanged { get; }
    internal Action<MediaPlaybackError> OnError { get; }

    internal bool PrefersHardwareFrame => PrefersHardwareFor(GetVideoOutput());

    internal bool PrefersHardwareFor(IMediaVideoOutput? output)
    {
        if (output is null || GetDecodingPolicy() == MediaVideoDecodingPolicy.SoftwareOnly)
        {
            return false;
        }

        return output.Supports(MediaFrameStorageKind.D3D11Texture, MediaPixelFormat.Nv12) ||
               output.Supports(MediaFrameStorageKind.D3D11Texture, MediaPixelFormat.Bgra32) ||
               output.Supports(MediaFrameStorageKind.DmaBuf, MediaPixelFormat.Nv12);
    }

    internal bool WantsCpuFrame
    {
        get
        {
            if (NeedsCpuFrame())
            {
                return true;
            }

            var output = GetVideoOutput();
            if (output is null)
            {
                return false;
            }

            // Adaptive WPF/Avalonia outputs report Supports() == true for both hardware (D3D11/DMA-BUF)
            // and CpuMemory/Bgra32. Do not request CPU frames when hardware output is preferred and healthy,
            // otherwise every frame forces GPU->CPU readback and scalar color conversion only to discard it.
            if (PrefersHardwareFor(output) &&
                !ReferenceEquals(_lastFailedHardwareOutput, output))
            {
                return false;
            }

            return output.Supports(MediaFrameStorageKind.CpuMemory, MediaPixelFormat.Bgra32);
        }
    }

    internal void ReportHardwarePresentationResult(IMediaVideoOutput output, bool succeeded)
    {
        if (succeeded)
        {
            if (ReferenceEquals(_lastFailedHardwareOutput, output))
            {
                _lastFailedHardwareOutput = null;
            }
        }
        else
        {
            _lastFailedHardwareOutput = output;
        }
    }
}

internal sealed class CapturePhysicalSession : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ParsedCaptureUri _parsedUri;
    private readonly MediaOpenOptions _openOptions;
    private readonly CapturePlayerOptions _playerOptions;
    private readonly CaptureFrameBufferPool _bufferPool;
    private readonly Func<CaptureFrameBufferPool, ICaptureBackend> _backendFactory;
    private readonly Func<string?, MediaOpenOptions, double, bool, ICaptureAudioLoopback?> _audioFactory;
    private readonly HashSet<CaptureSessionSubscriber> _subscribers = [];
    private volatile CaptureSessionSubscriber[] _subscriberSnapshot = [];
    private ICaptureBackend? _backend;
    private ICaptureAudioLoopback? _audioLoopback;
    private CaptureOpenResult? _openResult;
    private CancellationTokenSource? _reconnectCts;
    private Task? _reconnectTask;
    private MediaPlaybackState _state = MediaPlaybackState.Idle;
    private MediaReconnectDiagnostics _reconnectDiagnostics = MediaReconnectDiagnostics.Empty;
    private double _readMilliseconds;
    private double _decodeMilliseconds;
    private int _sampleCount;
    private string? _lastError;
    private int _poolReferences;
    private double _volume = 1d;
    private bool _isMuted;
    private bool _opened;
    private bool _started;
    private bool _disposed;

    internal CapturePhysicalSession(
        ParsedCaptureUri parsedUri,
        MediaOpenOptions openOptions,
        CapturePlayerOptions playerOptions,
        Func<CaptureFrameBufferPool, ICaptureBackend> backendFactory,
        Func<string?, MediaOpenOptions, double, bool, ICaptureAudioLoopback?> audioFactory)
    {
        _parsedUri = parsedUri;
        _openOptions = openOptions;
        _playerOptions = playerOptions;
        _bufferPool = new CaptureFrameBufferPool(
            playerOptions.MaxPoolBufferCount,
            playerOptions.MaxPoolRetainedBytes);
        _backendFactory = backendFactory;
        _audioFactory = audioFactory;
    }

    internal MediaDiagnostics Diagnostics
    {
        get
        {
            lock (_sync)
            {
                var liveProvider = _backend as ICaptureLiveDiagnosticsProvider;
                var diagText = liveProvider?.CurrentDiagnosticsDescription ??
                               _openResult?.DiagnosticsDescription ??
                               "Capture Idle";
                var hwActive = liveProvider?.CurrentIsHardwareVideoActive ??
                               _openResult?.IsHardwareVideoActive ??
                               false;
                var audioDiag = _audioLoopback?.Diagnostics ?? MediaAudioDiagnostics.Empty;
                return new MediaDiagnostics(
                    IsHardwareVideoDecodingActive: hwActive,
                    VideoDecoderDiagnostics: diagText,
                    ReadMilliseconds: _readMilliseconds,
                    DecodeMilliseconds: _decodeMilliseconds,
                    PerformanceSampleCount: _sampleCount,
                    LastError: _lastError)
                {
                    Audio = audioDiag,
                    Reconnect = _reconnectDiagnostics
                };
            }
        }
    }

    internal MediaPlaybackState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    internal void AddPoolReference() => _poolReferences++;

    internal int ReleasePoolReference() => --_poolReferences;

    internal async ValueTask OpenAsync(
        CaptureSessionSubscriber subscriber,
        double volume,
        bool isMuted,
        CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RuntimeCompat.NotDisposed(_disposed, this);
            lock (_sync)
            {
                if (_subscribers.Add(subscriber))
                {
                    _subscriberSnapshot = [.. _subscribers];
                }
                _volume = volume;
                _isMuted = isMuted;
            }

            if (_opened)
            {
                subscriber.OnStateChanged(_state);
                return;
            }

            TransitionTo(MediaPlaybackState.Opening);
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                OpenBackendCore();
            }, cancellationToken).ConfigureAwait(false);

            TransitionTo(MediaPlaybackState.Ready);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    internal async ValueTask StartAsync(
        CaptureSessionSubscriber subscriber,
        double volume,
        bool isMuted,
        CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RuntimeCompat.NotDisposed(_disposed, this);
            lock (_sync)
            {
                if (_subscribers.Add(subscriber))
                {
                    _subscriberSnapshot = [.. _subscribers];
                }
                _volume = volume;
                _isMuted = isMuted;
                _audioLoopback?.SetVolume(volume, isMuted, _openOptions.Audio.GainDecibels);
            }

            if (_started)
            {
                subscriber.OnStateChanged(_state);
                return;
            }

            if (!_opened)
            {
                OpenBackendCore();
            }

            _bufferPool.StartAcceptingReturns();
            _backend!.Start(
                prefersHardwareFrame: AnySubscriberPrefersHardwareFrame,
                needsCpuFrame: AnySubscriberWantsCpuFrame,
                onFrame: HandleDeliveredFrame,
                onError: HandleBackendError);
            _audioLoopback?.Start();

            lock (_sync)
            {
                _started = true;
            }

            TransitionTo(MediaPlaybackState.Playing);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    internal async ValueTask DetachAndMaybeStopAsync(
        CaptureSessionSubscriber subscriber,
        bool stopIfEmpty)
    {
        bool remainingEmpty;
        lock (_sync)
        {
            if (_subscribers.Remove(subscriber))
            {
                _subscriberSnapshot = [.. _subscribers];
            }
            remainingEmpty = _subscribers.Count == 0;
        }

        if (stopIfEmpty && remainingEmpty)
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
    }

    internal void UpdateAudioLevels(double volume, bool isMuted)
    {
        lock (_sync)
        {
            _volume = volume;
            _isMuted = isMuted;
            _audioLoopback?.SetVolume(volume, isMuted, _openOptions.Audio.GainDecibels);
        }
    }

    private void OpenBackendCore()
    {
        var backend = _backendFactory(_bufferPool);
        try
        {
            var result = backend.Open(_parsedUri, _openOptions, _playerOptions);
            var resolvedAudioId = ResolveAudioDeviceId(_parsedUri, result.PairedAudioDeviceId);
            ICaptureAudioLoopback? audio = null;
            if (_openOptions.Audio.IsEnabled &&
                _parsedUri.AudioMode != CaptureAudioMode.Disabled &&
                (resolvedAudioId is not null || _parsedUri.AudioMode == CaptureAudioMode.DefaultSystemInput))
            {
                audio = _audioFactory(resolvedAudioId, _openOptions, _volume, _isMuted);
            }

            lock (_sync)
            {
                _backend = backend;
                _openResult = result;
                _audioLoopback = audio;
                _lastError = null;
                _opened = true;
            }
        }
        catch
        {
            backend.Dispose();
            throw;
        }
    }

    private static string? ResolveAudioDeviceId(ParsedCaptureUri parsed, string? pairedAudioDeviceId) =>
        parsed.AudioMode switch
        {
            CaptureAudioMode.Disabled => null,
            CaptureAudioMode.SpecificDevice => parsed.AudioDeviceId,
            CaptureAudioMode.DefaultSystemInput => null,
            _ => pairedAudioDeviceId
        };

    private static bool SupportsHardwareStorage(IMediaVideoOutput output, MediaFrameStorageKind storageKind) =>
        storageKind switch
        {
            MediaFrameStorageKind.D3D11Texture =>
                output.Supports(MediaFrameStorageKind.D3D11Texture, MediaPixelFormat.Nv12) ||
                output.Supports(MediaFrameStorageKind.D3D11Texture, MediaPixelFormat.Bgra32),
            MediaFrameStorageKind.DmaBuf =>
                output.Supports(MediaFrameStorageKind.DmaBuf, MediaPixelFormat.Nv12),
            _ => false
        };

    private bool AnySubscriberPrefersHardwareFrame()
    {
        var subscribers = _subscriberSnapshot;
        for (var i = 0; i < subscribers.Length; i++)
        {
            if (subscribers[i].PrefersHardwareFrame)
            {
                return true;
            }
        }

        return false;
    }

    private bool AnySubscriberWantsCpuFrame()
    {
        var subscribers = _subscriberSnapshot;
        for (var i = 0; i < subscribers.Length; i++)
        {
            if (subscribers[i].WantsCpuFrame)
            {
                return true;
            }
        }

        return false;
    }

    private void HandleDeliveredFrame(CaptureDeliveredFrame delivered)
    {
        CaptureSessionSubscriber[] subscribers;
        lock (_sync)
        {
            if (_disposed || !_started)
            {
                delivered.HardwareLease?.Dispose();
                delivered.CpuLease?.Dispose();
                return;
            }

            _readMilliseconds = delivered.ReadMilliseconds;
            _decodeMilliseconds = delivered.DecodeMilliseconds;
            _sampleCount++;
            subscribers = _subscriberSnapshot;
        }

        try
        {
            for (var i = 0; i < subscribers.Length; i++)
            {
                var subscriber = subscribers[i];
                var output = subscriber.GetVideoOutput();
                var presented = false;

                if (output is not null)
                {
                    if (subscriber.PrefersHardwareFor(output) &&
                        delivered.HardwareLease is not null &&
                        SupportsHardwareStorage(output, delivered.HardwareLease.StorageKind))
                    {
                        var retained = delivered.HardwareLease.Retain();
                        try
                        {
                            presented = output.TryPresent(retained);
                        }
                        catch
                        {
                            presented = false;
                        }

                        subscriber.ReportHardwarePresentationResult(output, presented);
                        if (!presented)
                        {
                            retained.Dispose();
                        }
                    }

                    if (!presented &&
                        delivered.CpuLease is not null &&
                        output.Supports(MediaFrameStorageKind.CpuMemory, MediaPixelFormat.Bgra32))
                    {
                        var retainedCpu = delivered.CpuLease.Retain();
                        try
                        {
                            presented = output.TryPresent(retainedCpu);
                        }
                        catch
                        {
                            presented = false;
                        }

                        if (!presented)
                        {
                            retainedCpu.Dispose();
                        }
                    }
                }

                if (delivered.CpuLease is not null && subscriber.NeedsCpuFrame())
                {
                    var snapshotLease = delivered.CpuLease.Retain();
                    try
                    {
                        subscriber.OnFrame(snapshotLease, delivered.Width, delivered.Height);
                    }
                    finally
                    {
                        snapshotLease.Dispose();
                    }
                }
            }
        }
        finally
        {
            delivered.HardwareLease?.Dispose();
            delivered.CpuLease?.Dispose();
        }
    }

    private void HandleBackendError(Exception exception, bool isRecoverable)
    {
        bool shouldReconnect;
        lock (_sync)
        {
            if (_disposed || !_started)
            {
                return;
            }

            _lastError = exception.Message;
            shouldReconnect = isRecoverable &&
                              _openOptions.Network.Reconnect.IsEnabled &&
                              (_openOptions.Network.Reconnect.MaximumAttempts is null ||
                               _reconnectDiagnostics.ConsecutiveFailures < _openOptions.Network.Reconnect.MaximumAttempts.Value);
        }

        if (!shouldReconnect)
        {
            _ = StopAndFaultAsync(exception);
            return;
        }

        StartReconnectLoop(exception);
    }

    private void StartReconnectLoop(Exception triggerException)
    {
        lock (_sync)
        {
            if (_disposed || _reconnectTask is { IsCompleted: false })
            {
                return;
            }

            _reconnectCts?.Dispose();
            _reconnectCts = new CancellationTokenSource();
            var token = _reconnectCts.Token;
            _reconnectTask = Task.Run(() => ReconnectLoopAsync(triggerException, token), token);
        }
    }

    private async Task ReconnectLoopAsync(Exception triggerException, CancellationToken token)
    {
        var reconnect = _openOptions.Network.Reconnect;
        TransitionTo(MediaPlaybackState.Reconnecting);
        NotifyError(new MediaPlaybackError(
            "CaptureDeviceDisconnected",
            triggerException.Message,
            IsRecoverable: true,
            triggerException));

        await CleanupBackendAsync().ConfigureAwait(false);

        var currentDelay = reconnect.InitialDelay;
        var consecutive = 0;

        while (!token.IsCancellationRequested)
        {
            if (reconnect.MaximumAttempts is { } max && consecutive >= max)
            {
                break;
            }

            consecutive++;
            lock (_sync)
            {
                _reconnectDiagnostics = _reconnectDiagnostics with
                {
                    ConsecutiveFailures = consecutive,
                    TotalAttempts = _reconnectDiagnostics.TotalAttempts + 1,
                    NextDelay = currentDelay
                };
            }

            try
            {
                if (currentDelay > TimeSpan.Zero)
                {
                    await Task.Delay(currentDelay, token).ConfigureAwait(false);
                }

                await _lifecycleGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (_disposed || token.IsCancellationRequested)
                    {
                        return;
                    }

                    OpenBackendCore();
                    _bufferPool.StartAcceptingReturns();
                    _backend!.Start(
                        prefersHardwareFrame: AnySubscriberPrefersHardwareFrame,
                        needsCpuFrame: AnySubscriberWantsCpuFrame,
                        onFrame: HandleDeliveredFrame,
                        onError: HandleBackendError);
                    _audioLoopback?.Start();

                    lock (_sync)
                    {
                        _started = true;
                        _reconnectDiagnostics = _reconnectDiagnostics with
                        {
                            ConsecutiveFailures = 0,
                            RecoveryCount = _reconnectDiagnostics.RecoveryCount + 1,
                            NextDelay = TimeSpan.Zero
                        };
                    }

                    TransitionTo(MediaPlaybackState.Playing);
                    return;
                }
                finally
                {
                    _lifecycleGate.Release();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception retryException)
            {
                lock (_sync)
                {
                    _lastError = retryException.Message;
                }

                await CleanupBackendAsync().ConfigureAwait(false);
                var nextTicks = Math.Min(
                    reconnect.MaximumDelay.Ticks,
                    Math.Max(reconnect.InitialDelay.Ticks, currentDelay.Ticks * 2));
                currentDelay = TimeSpan.FromTicks(nextTicks);
            }
        }

        if (!token.IsCancellationRequested)
        {
            TransitionTo(MediaPlaybackState.Faulted);
            NotifyError(new MediaPlaybackError(
                "CaptureReconnectExhausted",
                _lastError ?? "Unable to reconnect to the capture device.",
                IsRecoverable: false,
                triggerException));
        }
    }

    private async Task StopAndFaultAsync(Exception exception)
    {
        await CleanupBackendAsync().ConfigureAwait(false);
        TransitionTo(MediaPlaybackState.Faulted);
        NotifyError(new MediaPlaybackError(
            "CaptureFailed",
            exception.Message,
            IsRecoverable: false,
            exception));
    }

    private async ValueTask StopCoreAsync()
    {
        CancellationTokenSource? reconnectCts;
        Task? reconnectTask;
        lock (_sync)
        {
            reconnectCts = _reconnectCts;
            _reconnectCts = null;
            reconnectTask = _reconnectTask;
            _reconnectTask = null;
        }

        reconnectCts?.Cancel();
        if (reconnectTask is not null)
        {
            try
            {
                await reconnectTask.ConfigureAwait(false);
            }
            catch
            {
                // Cancellation / background reconnect error already handled.
            }
        }

        reconnectCts?.Dispose();
        await CleanupBackendAsync().ConfigureAwait(false);
        TransitionTo(MediaPlaybackState.Stopped);
    }

    private async Task CleanupBackendAsync()
    {
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            ICaptureBackend? backend;
            ICaptureAudioLoopback? audio;
            lock (_sync)
            {
                backend = _backend;
                _backend = null;
                audio = _audioLoopback;
                _audioLoopback = null;
                _started = false;
                _opened = false;
            }

            audio?.Stop();
            audio?.Dispose();
            backend?.Stop();
            backend?.Dispose();
            _bufferPool.StopAcceptingReturns();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void TransitionTo(MediaPlaybackState newState)
    {
        CaptureSessionSubscriber[] subscribers;
        lock (_sync)
        {
            if (_state == newState)
            {
                return;
            }

            _state = newState;
            subscribers = [.. _subscribers];
        }

        foreach (var subscriber in subscribers)
        {
            subscriber.OnStateChanged(newState);
        }
    }

    private void NotifyError(MediaPlaybackError error)
    {
        CaptureSessionSubscriber[] subscribers;
        lock (_sync)
        {
            subscribers = [.. _subscribers];
        }

        foreach (var subscriber in subscribers)
        {
            subscriber.OnError(error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await StopCoreAsync().ConfigureAwait(false);
        _bufferPool.Dispose();
        _lifecycleGate.Dispose();
    }
}
