using System.Collections.Concurrent;

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
    private readonly BlockingCollection<BufferedPacket> _packets = new(PacketCapacity);
    private readonly Queue<double> _videoTimestamps = new();
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
    private readonly AutoResetEvent _wakeReader = new(false);
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
            lock (_queueSync)
            {
                if (!_bufferBeforePlayback || !_isPriming || IsReadyToPlay())
                {
                    if (_packets.TryTake(out var bufferedPacket))
                    {
                        if (bufferedPacket.VideoTimestamp is not null)
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
                    }
                }

                if (nextPacket is null && Volatile.Read(ref _isEof) && _packets.Count == 0)
                {
                    terminalResult = Volatile.Read(ref _terminalResult);
                }
                else if (nextPacket is null && _bufferBeforePlayback && !_bufferingNotified)
                {
                    _bufferingNotified = true;
                    notifyBuffering = true;
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

            _cancellation.Token.WaitHandle.WaitOne(25);
        }

        packet = null;
        return Volatile.Read(ref _terminalResult);
    }

    internal int Seek(Func<int> seekAction)
    {
        Volatile.Write(ref _isFlushing, true);
        lock (_ioSync)
        {
            try
            {
                _generation++;
                lock (_queueSync)
                {
                    while (_packets.TryTake(out var oldPacket))
                    {
                        oldPacket.Packet.Dispose();
                    }
                    _videoTimestamps.Clear();
                    _isPriming = true;
                    _hasStarted = false;
                }

                var result = seekAction();
                Volatile.Write(ref _terminalResult, ErrorEof);
                Volatile.Write(ref _isEof, false);
                _wakeReader.Set();
                return result;
            }
            finally
            {
                Volatile.Write(ref _isFlushing, false);
            }
        }
    }

    internal void Cancel() => _cancellation.Cancel();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cancellation.Cancel();
        _wakeReader.Set();
        if (Thread.CurrentThread != _readerThread)
        {
            _readerThread.Join();
        }

        while (_packets.TryTake(out var packet))
        {
            packet.Packet.Dispose();
        }
        _packets.Dispose();
        _wakeReader.Dispose();
        _cancellation.Dispose();
    }

    private void ReadPackets()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                if (Volatile.Read(ref _isEof))
                {
                    WaitHandle.WaitAny([_cancellation.Token.WaitHandle, _wakeReader]);
                    if (_cancellation.IsCancellationRequested) break;
                }

                int result;
                int generation;
                IntPtr clone = IntPtr.Zero;
                lock (_ioSync)
                {
                    if (_cancellation.IsCancellationRequested) break;
                    if (Volatile.Read(ref _isEof)) continue;

                    generation = _generation;
                    result = _api.AvReadFrame(_formatContext, _readPacket);
                    if (result >= 0)
                    {
                        clone = _api.AvPacketClone(_readPacket);
                        _api.AvPacketUnref(_readPacket);
                    }
                }

                if (result < 0)
                {
                    lock (_ioSync)
                    {
                        if (generation != _generation) continue;
                        Volatile.Write(ref _terminalResult, result);
                        Volatile.Write(ref _isEof, true);
                    }
                    continue;
                }

                if (clone == IntPtr.Zero)
                {
                    lock (_ioSync)
                    {
                        if (generation != _generation) continue;
                        Volatile.Write(ref _terminalResult, ErrorNoMemory);
                        Volatile.Write(ref _isEof, true);
                    }
                    continue;
                }

                DirectVideoPacket? packet = new(_api, clone);
                try
                {
                    double? videoTimestamp = null;
                    if (FFmpegAbi.GetPacketStreamIndex(clone) == _videoStreamIndex)
                    {
                        var info = FFmpegAbi.ReadPacket(clone);
                        var timestamp = info.DecodeTimestamp != long.MinValue
                            ? info.DecodeTimestamp
                            : info.PresentationTimestamp;
                        if (timestamp != long.MinValue)
                        {
                            videoTimestamp = timestamp * _videoTimeBase;
                        }
                    }

                    while (!_cancellation.IsCancellationRequested && !Volatile.Read(ref _isFlushing))
                    {
                        lock (_ioSync)
                        {
                            if (generation != _generation)
                            {
                                break;
                            }

                            lock (_queueSync)
                            {
                                if (_packets.TryAdd(new BufferedPacket(packet, videoTimestamp)))
                                {
                                    if (videoTimestamp is { } time)
                                    {
                                        _videoTimestamps.Enqueue(time);
                                    }
                                    packet = null;
                                    break;
                                }
                            }
                        }

                        Thread.Sleep(10);
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
        }
    }

    private bool IsReadyToPlay()
    {
        var targetSeconds = _hasStarted ? _rebufferSeconds : _initialBufferSeconds;
        return NetworkPacketBufferingPolicy.IsReady(
            _packets.Count,
            PacketCapacity,
            _videoTimestamps.Count > 0 ? _videoTimestamps.Peek() : null,
            _videoTimestamps.Count > 0 ? _videoTimestamps.Last() : null,
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
