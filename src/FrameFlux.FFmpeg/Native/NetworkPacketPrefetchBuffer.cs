namespace FrameFlux.FFmpeg;

internal sealed class NetworkPacketPrefetchBuffer : IDisposable
{
    private const int PacketCapacity = 1024;
    private const int ErrorEof = -541478725;
    private const int ErrorExit = -1414092869;
    private const int ErrorNoMemory = -12;

    private readonly FFmpegApi _api;
    private readonly IntPtr _formatContext;
    private readonly IntPtr _readPacket;
    private readonly Queue<BufferedPacket> _packets = new(PacketCapacity);
    private readonly Queue<double> _videoTimestamps = new(256);
    private readonly Stack<IntPtr> _packetPool = new(PacketCapacity + 1);
    private readonly Action<IntPtr> _returnPacketToPool;
    private readonly object _queueSync = new();
    private readonly int _videoStreamIndex;
    private readonly double _videoTimeBase;
    private readonly bool _bufferBeforePlayback;
    private readonly Action<bool>? _bufferingChanged;
    private readonly double _initialBufferSeconds;
    private readonly double _rebufferSeconds;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Thread _readerThread;
    private readonly object _ioSync = new();
    private double _lastVideoTimestamp;
    private bool _isEof;
    private bool _isFlushing;
    private int _terminalResult = ErrorEof;
    private int _generation;
    private int _disposed;
    private bool _isPriming = true;
    private bool _hasStarted;
    private bool _bufferingNotified;

    internal NetworkPacketPrefetchBuffer(
        FFmpegApi api,
        IntPtr formatContext,
        IntPtr readPacket,
        int videoStreamIndex,
        int videoTimeBaseNumerator,
        int videoTimeBaseDenominator,
        bool bufferBeforePlayback,
        int initialBufferDurationMilliseconds,
        int rebufferDurationMilliseconds,
        Action<bool>? bufferingChanged)
    {
        _api = api;
        _formatContext = formatContext;
        _readPacket = readPacket;
        _videoStreamIndex = videoStreamIndex;
        _videoTimeBase = (double)videoTimeBaseNumerator / Math.Max(1, videoTimeBaseDenominator);
        _bufferBeforePlayback = bufferBeforePlayback;
        _bufferingChanged = bufferingChanged;
        _initialBufferSeconds = initialBufferDurationMilliseconds / 1000d;
        _rebufferSeconds = rebufferDurationMilliseconds / 1000d;
        _returnPacketToPool = ReturnPacketToPool;
        _readerThread = new Thread(ReadPackets)
        {
            IsBackground = true,
            Name = "FrameFlux packet prefetch",
            Priority = ThreadPriority.BelowNormal
        };
        _readerThread.Start();
    }

    internal int Read(out DirectVideoPacket? packet)
    {
        while (!_cancellation.IsCancellationRequested)
        {
            BufferedPacket? nextPacket = null;
            bool? notifyBuffering = null;
            int? terminalResult = null;
            var shouldWait = false;

            lock (_queueSync)
            {
                if (!_bufferBeforePlayback || !_isPriming || IsReadyToPlay())
                {
                    if (_packets.Count > 0)
                    {
                        var bufferedPacket = _packets.Dequeue();
                        if (bufferedPacket.VideoTimestamp is not null && _videoTimestamps.Count > 0)
                        {
                            _videoTimestamps.Dequeue();
                        }

                        _isPriming = _bufferBeforePlayback && _packets.Count == 0;
                        nextPacket = bufferedPacket;
                        if (_bufferBeforePlayback && (!_hasStarted || _bufferingNotified))
                        {
                            _bufferingNotified = false;
                            notifyBuffering = false;
                        }

                        _hasStarted = true;
                        // Wake producer immediately if it was waiting on a full queue.
                        Monitor.PulseAll(_queueSync);
                    }
                }

                if (nextPacket is null)
                {
                    if (Volatile.Read(ref _isEof) && _packets.Count == 0)
                    {
                        terminalResult = Volatile.Read(ref _terminalResult);
                    }
                    else
                    {
                        if (_bufferBeforePlayback && !_bufferingNotified)
                        {
                            _bufferingNotified = true;
                            notifyBuffering = true;
                        }

                        shouldWait = true;
                    }
                }
            }

            if (notifyBuffering is { } isBuffering)
            {
                _bufferingChanged?.Invoke(isBuffering);
            }

            if (nextPacket is { } buffered)
            {
                packet = buffered.Packet;
                return 0;
            }

            if (terminalResult is { } result)
            {
                packet = null;
                return result;
            }

            if (shouldWait)
            {
                lock (_queueSync)
                {
                    if (!_cancellation.IsCancellationRequested &&
                        !Volatile.Read(ref _isEof) &&
                        (_packets.Count == 0 || (_bufferBeforePlayback && _isPriming && !IsReadyToPlay())))
                    {
                        Monitor.Wait(_queueSync, 25);
                    }
                }
            }
        }

        packet = null;
        return Volatile.Read(ref _terminalResult);
    }

    internal int Seek(Func<int> seekAction)
    {
        Volatile.Write(ref _isFlushing, true);
        lock (_queueSync)
        {
            Monitor.PulseAll(_queueSync);
        }

        lock (_ioSync)
        {
            try
            {
                Interlocked.Increment(ref _generation);
                lock (_queueSync)
                {
                    while (_packets.Count > 0)
                    {
                        _packets.Dequeue().Packet.Dispose();
                    }

                    _videoTimestamps.Clear();
                    _lastVideoTimestamp = 0d;
                    _isPriming = true;
                    _hasStarted = false;
                }

                var result = seekAction();
                Volatile.Write(ref _terminalResult, ErrorEof);
                Volatile.Write(ref _isEof, false);
                lock (_queueSync)
                {
                    Monitor.PulseAll(_queueSync);
                }

                return result;
            }
            finally
            {
                Volatile.Write(ref _isFlushing, false);
                lock (_queueSync)
                {
                    Monitor.PulseAll(_queueSync);
                }
            }
        }
    }

    internal void Cancel()
    {
        _cancellation.Cancel();
        lock (_queueSync)
        {
            Monitor.PulseAll(_queueSync);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Cancel();
        if (Thread.CurrentThread != _readerThread)
        {
            _readerThread.Join();
        }

        lock (_queueSync)
        {
            while (_packets.Count > 0)
            {
                _packets.Dequeue().Packet.Dispose();
            }

            while (_packetPool.Count > 0)
            {
                var pooled = _packetPool.Pop();
                _api.AvPacketFree(ref pooled);
            }
        }

        _cancellation.Dispose();
    }

    private IntPtr RentNativePacket()
    {
        lock (_queueSync)
        {
            if (_packetPool.Count > 0)
            {
                return _packetPool.Pop();
            }
        }

        return _api.AvPacketAlloc();
    }

    private void ReturnPacketToPool(IntPtr nativePacket)
    {
        if (nativePacket == IntPtr.Zero)
        {
            return;
        }

        _api.AvPacketUnref(nativePacket);
        var freePacket = false;
        lock (_queueSync)
        {
            if (Volatile.Read(ref _disposed) != 0 || _packetPool.Count >= PacketCapacity + 2)
            {
                freePacket = true;
            }
            else
            {
                _packetPool.Push(nativePacket);
            }
        }

        if (freePacket)
        {
            _api.AvPacketFree(ref nativePacket);
        }
    }

    private void ReadPackets()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                if (Volatile.Read(ref _isEof))
                {
                    lock (_queueSync)
                    {
                        while (!_cancellation.IsCancellationRequested && Volatile.Read(ref _isEof))
                        {
                            Monitor.Wait(_queueSync, 50);
                        }
                    }

                    if (_cancellation.IsCancellationRequested)
                    {
                        break;
                    }
                }

                // Wait until the queue has capacity before blocking on av_read_frame.
                lock (_queueSync)
                {
                    while (!_cancellation.IsCancellationRequested &&
                           !Volatile.Read(ref _isFlushing) &&
                           !Volatile.Read(ref _isEof) &&
                           _packets.Count >= PacketCapacity)
                    {
                        Monitor.Wait(_queueSync, 50);
                    }
                }

                if (_cancellation.IsCancellationRequested)
                {
                    break;
                }

                var nativePacket = RentNativePacket();
                if (nativePacket == IntPtr.Zero)
                {
                    Volatile.Write(ref _terminalResult, ErrorNoMemory);
                    Volatile.Write(ref _isEof, true);
                    lock (_queueSync)
                    {
                        Monitor.PulseAll(_queueSync);
                    }

                    continue;
                }

                int result;
                int generation;
                lock (_ioSync)
                {
                    if (_cancellation.IsCancellationRequested)
                    {
                        ReturnPacketToPool(nativePacket);
                        break;
                    }

                    if (Volatile.Read(ref _isEof))
                    {
                        ReturnPacketToPool(nativePacket);
                        continue;
                    }

                    generation = Volatile.Read(ref _generation);
                    result = _api.AvReadFrame(_formatContext, nativePacket);
                }

                if (result < 0)
                {
                    ReturnPacketToPool(nativePacket);
                    if (generation == Volatile.Read(ref _generation))
                    {
                        Volatile.Write(ref _terminalResult, result);
                        Volatile.Write(ref _isEof, true);
                        lock (_queueSync)
                        {
                            Monitor.PulseAll(_queueSync);
                        }
                    }

                    continue;
                }

                DirectVideoPacket? packet = new(_api, nativePacket, _returnPacketToPool);
                try
                {
                    double? videoTimestamp = null;
                    if (FFmpegAbi.GetPacketStreamIndex(nativePacket) == _videoStreamIndex)
                    {
                        var info = FFmpegAbi.ReadPacket(nativePacket);
                        var timestamp = info.DecodeTimestamp != long.MinValue
                            ? info.DecodeTimestamp
                            : info.PresentationTimestamp;
                        if (timestamp != long.MinValue)
                        {
                            videoTimestamp = timestamp * _videoTimeBase;
                        }
                    }

                    lock (_queueSync)
                    {
                        while (!_cancellation.IsCancellationRequested &&
                               !Volatile.Read(ref _isFlushing) &&
                               generation == Volatile.Read(ref _generation) &&
                               _packets.Count >= PacketCapacity)
                        {
                            Monitor.Wait(_queueSync, 50);
                        }

                        if (!_cancellation.IsCancellationRequested &&
                            !Volatile.Read(ref _isFlushing) &&
                            generation == Volatile.Read(ref _generation) &&
                            _packets.Count < PacketCapacity)
                        {
                            _packets.Enqueue(new BufferedPacket(packet, videoTimestamp));
                            if (videoTimestamp is { } time)
                            {
                                _videoTimestamps.Enqueue(time);
                                _lastVideoTimestamp = time;
                            }

                            packet = null;
                            Monitor.PulseAll(_queueSync);
                        }
                    }
                }
                finally
                {
                    packet?.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            Volatile.Write(ref _terminalResult, ErrorExit);
            lock (_queueSync)
            {
                Monitor.PulseAll(_queueSync);
            }
        }
    }

    private bool IsReadyToPlay()
    {
        var targetSeconds = _hasStarted ? _rebufferSeconds : _initialBufferSeconds;
        var hasVideoTimestamps = _videoTimestamps.Count > 0;
        return NetworkPacketBufferingPolicy.IsReady(
            _packets.Count,
            PacketCapacity,
            hasVideoTimestamps ? _videoTimestamps.Peek() : null,
            hasVideoTimestamps ? _lastVideoTimestamp : null,
            targetSeconds,
            Volatile.Read(ref _isEof));
    }

    private readonly record struct BufferedPacket(DirectVideoPacket Packet, double? VideoTimestamp);
}

internal static class NetworkPacketBufferingPolicy
{
    internal static bool IsReady(
        int packetCount,
        int packetCapacity,
        double? firstVideoTimestamp,
        double? lastVideoTimestamp,
        double targetSeconds,
        bool sourceEnded) =>
        sourceEnded ||
        targetSeconds <= 0 ||
        packetCount >= packetCapacity ||
        firstVideoTimestamp is { } first &&
        lastVideoTimestamp is { } last &&
        last - first >= targetSeconds;
}
