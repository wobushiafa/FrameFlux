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
            Name = "FrameFlux HLS packet prefetch",
            Priority = ThreadPriority.BelowNormal
        };
        _readerThread.Start();
    }

    internal int Read(out DirectVideoPacket? packet)
    {
        try
        {
            if (_packets.TryTake(
                    out packet,
                    Timeout.Infinite,
                    _cancellation.Token))
            {
                return 0;
            }
        }
        catch (OperationCanceledException)
        {
        }

        packet = null;
        return Volatile.Read(ref _terminalResult);
    }

    internal void Cancel() => _cancellation.Cancel();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cancellation.Cancel();
        if (Thread.CurrentThread != _readerThread)
        {
            _readerThread.Join();
        }

        while (_packets.TryTake(out var packet))
        {
            packet.Dispose();
        }
        _packets.Dispose();
        _cancellation.Dispose();
    }

    private void ReadPackets()
    {
        var terminalResult = ErrorEof;
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                var result = _api.AvReadFrame(_formatContext, _readPacket);
                if (result < 0)
                {
                    terminalResult = result;
                    break;
                }

                var clone = _api.AvPacketClone(_readPacket);
                _api.AvPacketUnref(_readPacket);
                if (clone == IntPtr.Zero)
                {
                    terminalResult = ErrorNoMemory;
                    break;
                }

                DirectVideoPacket? packet = new(_api, clone);
                try
                {
                    _packets.Add(packet, _cancellation.Token);
                    packet = null;
                }
                finally
                {
                    packet?.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            terminalResult = ErrorExit;
        }
        finally
        {
            Volatile.Write(ref _terminalResult, terminalResult);
            _packets.CompleteAdding();
        }
    }
}
