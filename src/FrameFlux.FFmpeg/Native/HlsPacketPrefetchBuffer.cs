using System.Collections.Concurrent;

namespace FrameFlux.FFmpeg;

internal sealed class HlsPacketPrefetchBuffer : IDisposable
{
    private const int PacketCapacity = 1024;
    private const int ErrorEof = -541478725;
    private const int ErrorExit = -1414092869;
    private const int ErrorNoMemory = -12;

    private readonly FFmpegApi _api;
    private readonly IntPtr _formatContext;
    private readonly IntPtr _readPacket;
    private readonly BlockingCollection<DirectVideoPacket> _packets = new(PacketCapacity);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Thread _readerThread;
    private readonly object _ioSync = new();
    private readonly AutoResetEvent _wakeReader = new(false);
    private bool _isEof;
    private bool _isFlushing;
    private int _terminalResult = ErrorEof;
    private int _disposed;

    internal HlsPacketPrefetchBuffer(
        FFmpegApi api,
        IntPtr formatContext,
        IntPtr readPacket)
    {
        _api = api;
        _formatContext = formatContext;
        _readPacket = readPacket;
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
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                if (_packets.TryTake(out packet, 50, _cancellation.Token))
                {
                    return 0;
                }

                if (Volatile.Read(ref _isEof) && _packets.Count == 0)
                {
                    packet = null;
                    return Volatile.Read(ref _terminalResult);
                }
            }
        }
        catch (OperationCanceledException)
        {
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
                while (_packets.TryTake(out var oldPacket))
                {
                    oldPacket.Dispose();
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
            packet.Dispose();
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
                IntPtr clone = IntPtr.Zero;
                lock (_ioSync)
                {
                    if (_cancellation.IsCancellationRequested) break;
                    if (Volatile.Read(ref _isEof)) continue;

                    result = _api.AvReadFrame(_formatContext, _readPacket);
                    if (result >= 0)
                    {
                        clone = _api.AvPacketClone(_readPacket);
                        _api.AvPacketUnref(_readPacket);
                    }
                }

                if (result < 0)
                {
                    Volatile.Write(ref _terminalResult, result);
                    Volatile.Write(ref _isEof, true);
                    continue;
                }

                if (clone == IntPtr.Zero)
                {
                    Volatile.Write(ref _terminalResult, ErrorNoMemory);
                    Volatile.Write(ref _isEof, true);
                    continue;
                }

                DirectVideoPacket? packet = new(_api, clone);
                try
                {
                    while (!_cancellation.IsCancellationRequested && !Volatile.Read(ref _isFlushing))
                    {
                        if (_packets.TryAdd(packet, 50, _cancellation.Token))
                        {
                            packet = null;
                            break;
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
        }
    }
}
