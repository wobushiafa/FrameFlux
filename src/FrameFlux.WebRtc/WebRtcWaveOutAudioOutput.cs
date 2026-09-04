using System.Runtime.InteropServices;

namespace FrameFlux.WebRtc;

/// <summary>
/// Low latency, lightweight Windows waveOut audio output.
/// Plays 16-bit linear PCM audio streams directly to the default Windows sound device
/// without requiring any third-party audio packages.
/// </summary>
public sealed class WebRtcWaveOutAudioOutput : IWebRtcAudioOutput
{
    private const uint WaveMapper = unchecked((uint)-1);
    private const uint WhdrDone = 0x00000001;
    private const ushort WaveFormatPcm = 1;
    private const int MaximumQueuedBuffers = 16;

    private readonly object _sync = new();
    private readonly Queue<PendingBuffer> _pendingBuffers = [];
    private IntPtr _handle;
    private int _sampleRate;
    private int _channels;
    private double _volume = 1.0;
    private bool _isMuted;
    private bool _started;
    private bool _disposed;

    public bool IsSupported => OperatingSystem.IsWindows();

    public void EnsureFormat(int sampleRate, int channels)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 4000);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        lock (_sync)
        {
            if (_handle != IntPtr.Zero && _sampleRate == sampleRate && _channels == channels)
            {
                return;
            }

            CloseDeviceUnsafe();

            _sampleRate = sampleRate;
            _channels = channels;

            var format = new WaveFormat
            {
                FormatTag = WaveFormatPcm,
                Channels = checked((ushort)channels),
                SamplesPerSecond = checked((uint)sampleRate),
                AverageBytesPerSecond = checked((uint)(sampleRate * channels * sizeof(short))),
                BlockAlign = checked((ushort)(channels * sizeof(short))),
                BitsPerSample = 16,
                ExtraSize = 0
            };

            var res = waveOutOpen(out _handle, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, 0);
            if (res != 0)
            {
                _handle = IntPtr.Zero;
                return;
            }

            waveOutPause(_handle);
            _started = false;
        }
    }

    public void WriteSamples(ReadOnlySpan<short> samples)
    {
        if (!OperatingSystem.IsWindows() || samples.IsEmpty)
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed || _handle == IntPtr.Zero)
            {
                return;
            }

            ReclaimCompletedBuffersUnsafe();

            // To avoid latency accumulation in live streaming, cap queue depth
            if (_pendingBuffers.Count >= MaximumQueuedBuffers)
            {
                ReclaimAllBuffersUnsafe();
            }

            var byteLength = samples.Length * sizeof(short);
            var byteBuffer = new byte[byteLength];

            // Apply software volume/mute attenuation
            var vol = _isMuted ? 0.0 : Math.Clamp(_volume, 0.0, 1.0);
            if (vol < 0.999)
            {
                for (var i = 0; i < samples.Length; i++)
                {
                    var scaled = (short)Math.Clamp((int)(samples[i] * vol), short.MinValue, short.MaxValue);
                    byteBuffer[i * 2] = (byte)(scaled & 0xFF);
                    byteBuffer[i * 2 + 1] = (byte)((scaled >> 8) & 0xFF);
                }
            }
            else
            {
                MemoryMarshal.AsBytes(samples).CopyTo(byteBuffer);
            }

            var pinnedHandle = GCHandle.Alloc(byteBuffer, GCHandleType.Pinned);
            var headerPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());

            var header = new WaveHeader
            {
                Data = pinnedHandle.AddrOfPinnedObject(),
                BufferLength = (uint)byteLength
            };

            Marshal.StructureToPtr(header, headerPtr, false);

            if (waveOutPrepareHeader(_handle, headerPtr, Marshal.SizeOf<WaveHeader>()) != 0)
            {
                pinnedHandle.Free();
                Marshal.FreeHGlobal(headerPtr);
                return;
            }

            if (waveOutWrite(_handle, headerPtr, Marshal.SizeOf<WaveHeader>()) != 0)
            {
                waveOutUnprepareHeader(_handle, headerPtr, Marshal.SizeOf<WaveHeader>());
                pinnedHandle.Free();
                Marshal.FreeHGlobal(headerPtr);
                return;
            }

            _pendingBuffers.Enqueue(new PendingBuffer(headerPtr, pinnedHandle));

            if (!_started && _pendingBuffers.Count >= 2)
            {
                waveOutRestart(_handle);
                _started = true;
            }
        }
    }

    public void SetVolume(double volume, bool isMuted)
    {
        lock (_sync)
        {
            _volume = Math.Clamp(volume, 0.0, 1.0);
            _isMuted = isMuted;
        }
    }

    public void Pause()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (_sync)
        {
            if (_handle != IntPtr.Zero && _started)
            {
                waveOutPause(_handle);
                _started = false;
            }
        }
    }

    public void Resume()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (_sync)
        {
            if (_handle != IntPtr.Zero && !_started && _pendingBuffers.Count > 0)
            {
                waveOutRestart(_handle);
                _started = true;
            }
        }
    }

    public void Reset()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (_sync)
        {
            if (_handle != IntPtr.Zero)
            {
                waveOutReset(_handle);
                ReclaimAllBuffersUnsafe();
                _started = false;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            CloseDeviceUnsafe();
        }
    }

    private void ReclaimCompletedBuffersUnsafe()
    {
        while (_pendingBuffers.Count > 0)
        {
            var peek = _pendingBuffers.Peek();
            var header = Marshal.PtrToStructure<WaveHeader>(peek.HeaderPointer);
            if ((header.Flags & WhdrDone) != 0)
            {
                var buffer = _pendingBuffers.Dequeue();
                ReleaseBufferUnsafe(buffer);
            }
            else
            {
                break;
            }
        }
    }

    private void ReclaimAllBuffersUnsafe()
    {
        while (_pendingBuffers.TryDequeue(out var buffer))
        {
            ReleaseBufferUnsafe(buffer);
        }
    }

    private void ReleaseBufferUnsafe(PendingBuffer buffer)
    {
        if (_handle != IntPtr.Zero)
        {
            waveOutUnprepareHeader(_handle, buffer.HeaderPointer, Marshal.SizeOf<WaveHeader>());
        }

        if (buffer.PinnedHandle.IsAllocated)
        {
            buffer.PinnedHandle.Free();
        }

        Marshal.FreeHGlobal(buffer.HeaderPointer);
    }

    private void CloseDeviceUnsafe()
    {
        if (_handle == IntPtr.Zero) return;

        waveOutReset(_handle);
        ReclaimAllBuffersUnsafe();
        waveOutClose(_handle);
        _handle = IntPtr.Zero;
        _started = false;
    }

    private readonly record struct PendingBuffer(IntPtr HeaderPointer, GCHandle PinnedHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(
        out IntPtr hWaveOut,
        uint uDeviceID,
        ref WaveFormat lpFormat,
        IntPtr dwCallback,
        IntPtr dwInstance,
        uint fdwOpen);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutPause(IntPtr hWaveOut);

    [DllImport("winmm.dll")]
    private static extern int waveOutRestart(IntPtr hWaveOut);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr hWaveOut);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr hWaveOut);
}
