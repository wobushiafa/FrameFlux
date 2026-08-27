using System.Runtime.InteropServices;

namespace FrameFlux.FFmpeg;

internal sealed class WindowsWaveOutAudioOutput : IAudioOutput
{
    private const uint Mapper = uint.MaxValue;
    private const uint HeaderDone = 0x00000001;
    private const uint TimeSamples = 0x00000002;
    private const int MaximumQueuedBuffers = 12;
    private readonly object _sync = new();
    private readonly Queue<PendingBuffer> _pendingBuffers = [];
    private readonly long _startBufferFrames;
    private readonly AudioOutputConfiguration _configuration;
    private readonly string? _fallbackError;
    private IntPtr _handle;
    private long _completedFrames;
    private uint _lastDevicePosition;
    private long _devicePositionWraps;
    private long _pendingFrames;
    private bool _started;

    internal WindowsWaveOutAudioOutput(
        int sampleRate,
        int channels,
        AudioOutputConfiguration? configuration = null,
        string? fallbackError = null)
    {
        SampleRate = sampleRate;
        Channels = channels;
        _configuration = configuration ?? new AudioOutputConfiguration(
            null,
            TimeSpan.FromMilliseconds(100));
        _fallbackError = fallbackError;
        _startBufferFrames = Math.Max(
            1,
            (long)Math.Ceiling(sampleRate * _configuration.BufferDuration.TotalSeconds));
        var format = new WaveFormat
        {
            FormatTag = 1,
            Channels = checked((ushort)channels),
            SamplesPerSecond = checked((uint)sampleRate),
            AverageBytesPerSecond = checked((uint)(sampleRate * channels * sizeof(short))),
            BlockAlign = checked((ushort)(channels * sizeof(short))),
            BitsPerSample = 16
        };
        var result = waveOutOpen(out _handle, Mapper, ref format, IntPtr.Zero, IntPtr.Zero, 0);
        if (result != 0)
        {
            throw new InvalidOperationException($"waveOutOpen failed with code {result}.");
        }
        ThrowIfFailed(waveOutPause(_handle), "waveOutPause");
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public long PlayedFrames
    {
        get
        {
            lock (_sync)
            {
                if (_handle == IntPtr.Zero)
                {
                    return _completedFrames;
                }

                var position = new MmTime { Type = TimeSamples };
                if (waveOutGetPosition(_handle, ref position, Marshal.SizeOf<MmTime>()) == 0 &&
                    position.Type == TimeSamples)
                {
                    if (position.Sample < _lastDevicePosition)
                    {
                        _devicePositionWraps += 1L << 32;
                    }
                    _lastDevicePosition = position.Sample;
                    return _devicePositionWraps + position.Sample;
                }

                return _completedFrames;
            }
        }
    }
    public bool IsOperational => _handle != IntPtr.Zero;
    public MediaAudioDiagnostics Diagnostics
    {
        get
        {
            lock (_sync)
            {
                return new MediaAudioDiagnostics(
                    "waveOut",
                    null,
                    "Windows default audio output",
                    SampleRate,
                    Channels,
                    _configuration.BufferDuration,
                    TimeSpan.FromSeconds((double)_pendingFrames / SampleRate),
                    _handle != IntPtr.Zero,
                    0,
                    _fallbackError);
            }
        }
    }

    public bool TrySetVolume(double volume, bool muted)
    {
        var normalized = muted ? 0d : Math.Clamp(volume, 0d, 1d);
        var channelVolume = checked((uint)Math.Round(normalized * ushort.MaxValue));
        var packedVolume = channelVolume | (channelVolume << 16);
        lock (_sync)
        {
            return _handle != IntPtr.Zero &&
                   waveOutSetVolume(_handle, packedVolume) == 0;
        }
    }

    public void Write(byte[] pcm)
    {
        if (pcm.Length == 0) return;
        lock (_sync)
        {
            if (_handle == IntPtr.Zero) return;
            ReclaimCompletedBuffers();
            while (_pendingBuffers.Count >= MaximumQueuedBuffers)
            {
                Thread.Sleep(1);
                ReclaimCompletedBuffers();
            }

            var pending = CreatePendingBuffer(pcm);
            try
            {
                ThrowIfFailed(
                    waveOutWrite(_handle, pending.HeaderPointer, Marshal.SizeOf<WaveHeader>()),
                    "waveOutWrite");
            }
            catch
            {
                ReleaseBuffer(pending);
                throw;
            }

            _pendingBuffers.Enqueue(pending);
            _pendingFrames += pending.Frames;
            if (!_started &&
                (_pendingFrames >= _startBufferFrames ||
                 _pendingBuffers.Count >= MaximumQueuedBuffers))
            {
                ThrowIfFailed(waveOutRestart(_handle), "waveOutRestart");
                _started = true;
            }
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            if (_handle == IntPtr.Zero)
            {
                return;
            }

            ThrowIfFailed(waveOutReset(_handle), "waveOutReset");
            while (_pendingBuffers.TryDequeue(out var pending))
            {
                ReleaseBuffer(pending);
            }
            _completedFrames = 0;
            _lastDevicePosition = 0;
            _devicePositionWraps = 0;
            _pendingFrames = 0;
            _started = false;
            ThrowIfFailed(waveOutPause(_handle), "waveOutPause");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            var handle = _handle;
            _handle = IntPtr.Zero;
            if (handle == IntPtr.Zero) return;
            _ = waveOutReset(handle);
            while (_pendingBuffers.TryDequeue(out var pending))
            {
                ReleaseBuffer(handle, pending);
            }
            _pendingFrames = 0;
            _ = waveOutClose(handle);
        }
    }

    private PendingBuffer CreatePendingBuffer(byte[] pcm)
    {
        var data = GCHandle.Alloc(pcm, GCHandleType.Pinned);
        var headerPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
        try
        {
            var header = new WaveHeader
            {
                Data = data.AddrOfPinnedObject(),
                BufferLength = checked((uint)pcm.Length)
            };
            Marshal.StructureToPtr(header, headerPointer, false);
            ThrowIfFailed(
                waveOutPrepareHeader(_handle, headerPointer, Marshal.SizeOf<WaveHeader>()),
                "waveOutPrepareHeader");
            return new PendingBuffer(
                data,
                headerPointer,
                pcm.Length / (Channels * sizeof(short)));
        }
        catch
        {
            Marshal.FreeHGlobal(headerPointer);
            data.Free();
            throw;
        }
    }

    private void ReclaimCompletedBuffers()
    {
        while (_pendingBuffers.TryPeek(out var pending) &&
               (Marshal.PtrToStructure<WaveHeader>(pending.HeaderPointer).Flags & HeaderDone) != 0)
        {
            _pendingBuffers.Dequeue();
            _pendingFrames -= pending.Frames;
            _completedFrames += pending.Frames;
            ReleaseBuffer(pending);
        }

        if (_started && _pendingBuffers.Count == 0)
        {
            _ = waveOutPause(_handle);
            _started = false;
        }
    }

    private void ReleaseBuffer(PendingBuffer pending) =>
        ReleaseBuffer(_handle, pending);

    private static void ReleaseBuffer(IntPtr handle, PendingBuffer pending)
    {
        if (handle != IntPtr.Zero)
        {
            _ = waveOutUnprepareHeader(handle, pending.HeaderPointer, Marshal.SizeOf<WaveHeader>());
        }
        Marshal.FreeHGlobal(pending.HeaderPointer);
        pending.Data.Free();
    }

    private static void ThrowIfFailed(uint result, string operation)
    {
        if (result != 0) throw new InvalidOperationException($"{operation} failed with code {result}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat
    {
        internal ushort FormatTag;
        internal ushort Channels;
        internal uint SamplesPerSecond;
        internal uint AverageBytesPerSecond;
        internal ushort BlockAlign;
        internal ushort BitsPerSample;
        internal ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        internal IntPtr Data;
        internal uint BufferLength;
        internal uint BytesRecorded;
        internal nuint User;
        internal uint Flags;
        internal uint Loops;
        internal IntPtr Next;
        internal nuint Reserved;
    }

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    private struct MmTime
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(4)] internal uint Sample;
    }

    private sealed record PendingBuffer(GCHandle Data, IntPtr HeaderPointer, int Frames);

    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out IntPtr handle, uint deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutPause(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveOutRestart(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveOutSetVolume(IntPtr handle, uint volume);
    [DllImport("winmm.dll")] private static extern uint waveOutGetPosition(IntPtr handle, ref MmTime time, int size);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(IntPtr handle);
}
