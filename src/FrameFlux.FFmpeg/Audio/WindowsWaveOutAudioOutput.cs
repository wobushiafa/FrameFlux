using System.Runtime.InteropServices;

namespace FrameFlux.FFmpeg;

internal sealed unsafe class WindowsWaveOutAudioOutput : IAudioOutput, IInterruptibleAudioOutput, ISlicedAudioOutput
{
    private const uint Mapper = uint.MaxValue;
    private const uint HeaderDone = 0x00000001;
    private const uint TimeSamples = 0x00000002;
    private const uint CallbackEvent = 0x00050000;
    private const int MaximumQueuedBuffers = 12;
    private readonly object _sync = new();
    private readonly WaveSlot[] _slots = new WaveSlot[MaximumQueuedBuffers];
    private readonly Queue<int> _pendingSlots = new(MaximumQueuedBuffers);
    private readonly Stack<int> _freeSlots = new(MaximumQueuedBuffers);
    private readonly AutoResetEvent _bufferDoneEvent = new(false);
    private readonly ManualResetEvent _stopEvent = new(false);
    private readonly WaitHandle[] _waitHandles;
    private readonly long _startBufferFrames;
    private readonly AudioOutputConfiguration _configuration;
    private readonly string? _fallbackError;
    private IntPtr _handle;
    private long _completedFrames;
    private uint _lastDevicePosition;
    private long _devicePositionWraps;
    private long _pendingFrames;
    private bool _started;
    private int _stopping;

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
        _waitHandles = [_stopEvent, _bufferDoneEvent];
        for (var i = 0; i < MaximumQueuedBuffers; i++)
        {
            _slots[i] = new WaveSlot((WaveHeader*)Marshal.AllocHGlobal(sizeof(WaveHeader)));
            *_slots[i].Header = default;
            _freeSlots.Push(i);
        }

        var format = new WaveFormat
        {
            FormatTag = 1,
            Channels = checked((ushort)channels),
            SamplesPerSecond = checked((uint)sampleRate),
            AverageBytesPerSecond = checked((uint)(sampleRate * channels * sizeof(short))),
            BlockAlign = checked((ushort)(channels * sizeof(short))),
            BitsPerSample = 16
        };
        var result = waveOutOpen(
            out _handle,
            Mapper,
            ref format,
            _bufferDoneEvent.SafeWaitHandle.DangerousGetHandle(),
            IntPtr.Zero,
            CallbackEvent);
        if (result != 0)
        {
            ReleaseAllSlots();
            _bufferDoneEvent.Dispose();
            _stopEvent.Dispose();
            throw new InvalidOperationException($"waveOutOpen failed with code {result}.");
        }

        try
        {
            ThrowIfFailed(waveOutPause(_handle), "waveOutPause");
        }
        catch
        {
            Dispose();
            throw;
        }
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
                if (waveOutGetPosition(_handle, ref position, sizeof(MmTime)) == 0 &&
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

    public void Write(byte[] pcm) => Write(pcm, 0, pcm.Length);

    public void Write(byte[] pcm, int offset, int count)
    {
        if (count <= 0 || Volatile.Read(ref _stopping) != 0)
        {
            return;
        }

        while (true)
        {
            lock (_sync)
            {
                if (_handle == IntPtr.Zero || Volatile.Read(ref _stopping) != 0)
                {
                    return;
                }

                ReclaimCompletedBuffers();
                if (_freeSlots.Count > 0)
                {
                    var slotIndex = _freeSlots.Pop();
                    var slot = _slots[slotIndex];
                    try
                    {
                        PrepareAndWriteSlot(slot, pcm, offset, count);
                    }
                    catch
                    {
                        UnprepareSlot(_handle, slot);
                        _freeSlots.Push(slotIndex);
                        throw;
                    }

                    _pendingSlots.Enqueue(slotIndex);
                    _pendingFrames += slot.Frames;
                    if (!_started &&
                        (_pendingFrames >= _startBufferFrames ||
                         _pendingSlots.Count >= MaximumQueuedBuffers))
                    {
                        ThrowIfFailed(waveOutRestart(_handle), "waveOutRestart");
                        _started = true;
                    }

                    return;
                }
            }

            var signaled = WaitHandle.WaitAny(_waitHandles, 50);
            if (signaled == 0)
            {
                return;
            }
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            if (_handle == IntPtr.Zero || Volatile.Read(ref _stopping) != 0)
            {
                return;
            }

            ThrowIfFailed(waveOutReset(_handle), "waveOutReset");
            while (_pendingSlots.TryDequeue(out var slotIndex))
            {
                UnprepareSlot(_handle, _slots[slotIndex]);
                _freeSlots.Push(slotIndex);
            }

            _completedFrames = 0;
            _lastDevicePosition = 0;
            _devicePositionWraps = 0;
            _pendingFrames = 0;
            _started = false;
            ThrowIfFailed(waveOutPause(_handle), "waveOutPause");
        }
    }

    public void RequestStop()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
        {
            return;
        }

        _stopEvent.Set();
        lock (_sync)
        {
            if (_handle != IntPtr.Zero)
            {
                _ = waveOutReset(_handle);
            }
        }
    }

    public void Dispose()
    {
        RequestStop();
        lock (_sync)
        {
            var handle = _handle;
            _handle = IntPtr.Zero;
            if (handle != IntPtr.Zero)
            {
                _ = waveOutReset(handle);
                while (_pendingSlots.TryDequeue(out var slotIndex))
                {
                    UnprepareSlot(handle, _slots[slotIndex]);
                    _freeSlots.Push(slotIndex);
                }
                _pendingFrames = 0;
                _ = waveOutClose(handle);
            }

            ReleaseAllSlots();
        }

        _bufferDoneEvent.Dispose();
        _stopEvent.Dispose();
    }

    private void PrepareAndWriteSlot(WaveSlot slot, byte[] pcm, int offset, int count)
    {
        slot.EnsureCapacity(count);
        fixed (byte* src = &pcm[offset])
        {
            Buffer.MemoryCopy(src, (void*)slot.DataPtr, slot.Capacity, count);
        }

        *slot.Header = new WaveHeader
        {
            Data = slot.DataPtr,
            BufferLength = checked((uint)count)
        };
        slot.Frames = count / (Channels * sizeof(short));
        ThrowIfFailed(
            waveOutPrepareHeader(_handle, (IntPtr)slot.Header, sizeof(WaveHeader)),
            "waveOutPrepareHeader");
        slot.Prepared = true;
        ThrowIfFailed(
            waveOutWrite(_handle, (IntPtr)slot.Header, sizeof(WaveHeader)),
            "waveOutWrite");
    }

    private void ReclaimCompletedBuffers()
    {
        while (_pendingSlots.TryPeek(out var slotIndex) &&
               (_slots[slotIndex].Header->Flags & HeaderDone) != 0)
        {
            _pendingSlots.Dequeue();
            var slot = _slots[slotIndex];
            _pendingFrames -= slot.Frames;
            _completedFrames += slot.Frames;
            UnprepareSlot(_handle, slot);
            _freeSlots.Push(slotIndex);
        }

        if (_started && _pendingSlots.Count == 0)
        {
            _ = waveOutPause(_handle);
            _started = false;
        }
    }

    private static void UnprepareSlot(IntPtr handle, WaveSlot slot)
    {
        if (slot.Prepared && handle != IntPtr.Zero)
        {
            _ = waveOutUnprepareHeader(handle, (IntPtr)slot.Header, sizeof(WaveHeader));
        }
        slot.Prepared = false;
        slot.Frames = 0;
    }

    private void ReleaseAllSlots()
    {
        for (var i = 0; i < _slots.Length; i++)
        {
            var slot = _slots[i];
            if (slot is null)
            {
                continue;
            }

            _slots[i] = null!;
            slot.Dispose();
        }
    }

    private static void ThrowIfFailed(uint result, string operation)
    {
        if (result != 0) throw new InvalidOperationException($"{operation} failed with code {result}.");
    }

    private sealed class WaveSlot(WaveHeader* header) : IDisposable
    {
        internal WaveHeader* Header { get; private set; } = header;
        internal IntPtr DataPtr { get; private set; }
        internal int Capacity { get; private set; }
        internal int Frames { get; set; }
        internal bool Prepared { get; set; }

        internal void EnsureCapacity(int requiredBytes)
        {
            if (requiredBytes <= Capacity)
            {
                return;
            }

            var newCapacity = Math.Max(requiredBytes, Math.Max(4096, Capacity * 2));
            DataPtr = DataPtr == IntPtr.Zero
                ? Marshal.AllocHGlobal(newCapacity)
                : Marshal.ReAllocHGlobal(DataPtr, (IntPtr)newCapacity);
            Capacity = newCapacity;
        }

        public void Dispose()
        {
            if (DataPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(DataPtr);
                DataPtr = IntPtr.Zero;
                Capacity = 0;
            }

            if (Header != null)
            {
                Marshal.FreeHGlobal((IntPtr)Header);
                Header = null;
            }
        }
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

    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out IntPtr handle, uint deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutPause(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveOutRestart(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveOutGetPosition(IntPtr handle, ref MmTime time, int size);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(IntPtr handle);
}
