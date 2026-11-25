#if !ANDROID
using System.Diagnostics;
using System.Globalization;

namespace FrameFlux.Capture;

internal sealed unsafe class LinuxV4l2CaptureBackend : ICaptureBackend, ICaptureLiveDiagnosticsProvider
{
    private const int DefaultMmapBufferCount = 4;
    private const int DmaBufMmapBufferCount = 6;
    private const int PollTimeoutMilliseconds = 500;
    private const int MaxConsecutiveDecodeFailures = 30;
    private const int StartupErrorFramesBeforeFallback = 8;

    private readonly CaptureFrameBufferPool _bufferPool;
    private readonly object _sync = new();
    private readonly object _queueSync = new();

    private int _videoFd = -1;
    private int _wakeFd = -1;
    private MmapBufferSlot[] _buffers = [];
    private LinuxTurboJpegDecoder? _turboJpeg;
    private Thread? _captureThread;
    private CancellationTokenSource? _cts;

    private string _deviceDisplayName = string.Empty;
    private string _deviceNodePath = string.Empty;
    private string _busInfo = string.Empty;
    private IReadOnlyList<uint> _rawFourCcs = [];
    private List<MediaCaptureVideoFormat> _fallbackFormats = [];
    private int _nextFallbackIndex;
    private bool _allowHardware;
    private int _initialWidth;
    private int _initialHeight;
    private int _width;
    private int _height;
    private int _bytesPerLine;
    private uint _negotiatedFourCc;
    private MediaCapturePixelFormat _negotiatedPixelFormat;
    private CaptureYuvColorSpace _yuvColorSpace = CaptureYuvColorSpace.Bt601Limited;
    private bool _dmaBufActive;
    private string? _liveDiagnosticsDescription;
    private bool _streaming;
    private int _inFlightDmaBufCount;
    private bool _disposed;

    public string? CurrentDiagnosticsDescription => Volatile.Read(ref _liveDiagnosticsDescription);

    public bool? CurrentIsHardwareVideoActive => Volatile.Read(ref _dmaBufActive);

    private sealed class MmapBufferSlot
    {
        public uint Index;
        public IntPtr MappedAddress;
        public nuint MappedLength;
        public int DmaBufFd = -1;
    }

    internal LinuxV4l2CaptureBackend(CaptureFrameBufferPool bufferPool)
    {
        _bufferPool = bufferPool;
    }

    public CaptureOpenResult Open(
        ParsedCaptureUri parsedUri,
        MediaOpenOptions openOptions,
        CapturePlayerOptions playerOptions)
    {
        var records = LinuxV4l2DeviceEnumerator.EnumerateVideoDeviceRecords(includeFormats: false);
        LinuxVideoDeviceRecord? target = null;

        if (!string.IsNullOrWhiteSpace(parsedUri.DeviceId))
        {
            var requestedId = parsedUri.DeviceId.Trim();
            target = records.FirstOrDefault(r =>
                r.DeviceId.Equals(requestedId, StringComparison.Ordinal) ||
                r.DeviceNodePath.Equals(requestedId, StringComparison.Ordinal));

            if (target is null)
            {
                var resolvedNode = LinuxV4l2DeviceEnumerator.ResolveDeviceNodePath(requestedId);
                if (!string.IsNullOrWhiteSpace(resolvedNode))
                {
                    target = records.FirstOrDefault(r => r.DeviceNodePath.Equals(resolvedNode, StringComparison.Ordinal));
                    if (target is null && File.Exists(resolvedNode))
                    {
                        target = new LinuxVideoDeviceRecord(
                            requestedId,
                            resolvedNode,
                            Path.GetFileName(resolvedNode),
                            string.Empty,
                            null,
                            null,
                            null,
                            []);
                    }
                }
            }

            if (target is null)
            {
                throw new InvalidOperationException($"Linux capture device '{parsedUri.DeviceId}' was not found.");
            }
        }
        else
        {
            var index = parsedUri.DeviceIndex ?? 0;
            if (index < 0 || index >= records.Count)
            {
                throw new InvalidOperationException(
                    records.Count == 0
                        ? "No local V4L2 video capture devices were detected. Ensure the camera is connected and the current user is in the 'video' group."
                        : $"Linux capture device index {index} is out of range (0..{records.Count - 1}).");
            }

            target = records[index];
        }

        var fd = LinuxV4l2Interop.Open(
            target.DeviceNodePath,
            LinuxV4l2Interop.O_RDWR | LinuxV4l2Interop.O_NONBLOCK | LinuxV4l2Interop.O_CLOEXEC);
        if (fd < 0)
        {
            var errno = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            throw LinuxV4l2Interop.CreateOpenDeviceException(target.DeviceNodePath, errno);
        }

        var turboJpegAvailable = LinuxTurboJpegDecoder.IsAvailable;
        LinuxTurboJpegDecoder? turboJpeg = null;
        MmapBufferSlot[] mappedBuffers = [];

        try
        {
            var allFormats = LinuxV4l2DeviceEnumerator.EnumerateFormatsOnFd(fd);
            var decodableFormats = allFormats
                .Where(f => LinuxV4l2Interop.IsSupportedLinuxPixelFormat(f.PixelFormat, turboJpegAvailable))
                .ToList();

            var requestedFormat = parsedUri.PixelFormat ?? playerOptions.PreferredPixelFormat;
            if (requestedFormat == MediaCapturePixelFormat.Mjpeg && !turboJpegAvailable)
            {
                throw new PlatformNotSupportedException(
                    "MJPEG capture on Linux requires 'libturbojpeg.so.0'. Install it (e.g. 'sudo apt install libturbojpeg') or select an uncompressed format.");
            }

            if (decodableFormats.Count == 0 && allFormats.Count > 0)
            {
                var availableSummary = string.Join(", ", allFormats.Select(static f => f.PixelFormat).Distinct());
                var hint = !turboJpegAvailable && allFormats.Any(static f => f.PixelFormat == MediaCapturePixelFormat.Mjpeg)
                    ? " Install 'libturbojpeg' to enable MJPEG decoding."
                    : string.Empty;
                throw new NotSupportedException(
                    $"Linux capture device '{target.DisplayName}' only exposes unsupported formats ({availableSummary}).{hint}");
            }

            var selected = CaptureFormatSelector.SelectBestFormat(
                decodableFormats,
                parsedUri.Width,
                parsedUri.Height,
                parsedUri.FrameRate,
                parsedUri.PixelFormat,
                openOptions.Video,
                playerOptions);

            var rawFourCcs = LinuxV4l2DeviceEnumerator.EnumerateRawFourCcsOnFd(fd);
            var desiredFourCc = selected is not null
                ? ResolveBestFourCc(selected.PixelFormat, rawFourCcs)
                : LinuxV4l2Interop.V4L2_PIX_FMT_YUYV;

            LinuxV4l2Interop.V4l2Format fmt = default;
            fmt.Type = LinuxV4l2Interop.V4L2_BUF_TYPE_VIDEO_CAPTURE;
            fmt.Width = checked((uint)(selected?.Width ?? parsedUri.Width ?? playerOptions.PreferredWidth ?? 1280));
            fmt.Height = checked((uint)(selected?.Height ?? parsedUri.Height ?? playerOptions.PreferredHeight ?? 720));
            fmt.PixelFormat = desiredFourCc;
            fmt.Field = LinuxV4l2Interop.V4L2_FIELD_ANY;

            if (LinuxV4l2Interop.Ioctl(fd, LinuxV4l2Interop.VIDIOC_S_FMT, &fmt, out var sFmtErr) < 0)
            {
                throw new IOException(
                    $"VIDIOC_S_FMT failed on '{target.DeviceNodePath}' for {fmt.Width}x{fmt.Height} {LinuxV4l2Interop.FormatFourCc(desiredFourCc)} (errno={sFmtErr}).");
            }

            var actualFormat = LinuxV4l2Interop.MapFourCcToPixelFormat(fmt.PixelFormat);
            if (!LinuxV4l2Interop.IsSupportedLinuxPixelFormat(actualFormat, turboJpegAvailable))
            {
                throw new NotSupportedException(
                    $"V4L2 device '{target.DeviceNodePath}' negotiated unsupported pixel format '{LinuxV4l2Interop.FormatFourCc(fmt.PixelFormat)}'.");
            }

            var targetFps = selected?.FrameRate ?? parsedUri.FrameRate ?? playerOptions.PreferredFrameRate ?? 30d;
            var actualFps = TryConfigureFrameRate(fd, targetFps);

            if (actualFormat == MediaCapturePixelFormat.Mjpeg)
            {
                turboJpeg = LinuxTurboJpegDecoder.TryCreate()
                    ?? throw new PlatformNotSupportedException("Failed to initialize libturbojpeg decompressor.");
            }

            var allowHardware = openOptions.Video.DecodingPolicy != MediaVideoDecodingPolicy.SoftwareOnly &&
                                playerOptions.EnableHardwareVideoOutput;
            var wantDmaBuf = allowHardware && actualFormat == MediaCapturePixelFormat.Nv12;
            var requestedBufferCount = wantDmaBuf ? DmaBufMmapBufferCount : DefaultMmapBufferCount;

            mappedBuffers = AllocateAndMapBuffers(fd, requestedBufferCount, wantDmaBuf, out var dmaBufActive);

            var negotiatedWidth = checked((int)fmt.Width);
            var negotiatedHeight = checked((int)fmt.Height);
            var bytesPerLine = fmt.BytesPerLine > 0
                ? checked((int)fmt.BytesPerLine)
                : ComputeDefaultBytesPerLine(negotiatedWidth, fmt.PixelFormat);
            var colorSpace = LinuxV4l2Interop.ResolveColorSpace(
                fmt.ColorSpace,
                fmt.YCbCrEnc,
                fmt.Quantization,
                negotiatedWidth,
                negotiatedHeight,
                actualFormat);

            var allowExplicitSize = parsedUri.Width.HasValue || parsedUri.Height.HasValue ||
                                    playerOptions.PreferredWidth.HasValue || playerOptions.PreferredHeight.HasValue;
            var negotiatedVideoFormat = new MediaCaptureVideoFormat(
                negotiatedWidth,
                negotiatedHeight,
                actualFps > 0d ? actualFps : targetFps,
                actualFormat);
            var fallbackFormats = allowExplicitSize
                ? []
                : BuildBandwidthFallbackChain(decodableFormats, negotiatedVideoFormat);

            var outputPixelFormat = dmaBufActive ? MediaCapturePixelFormat.Nv12 : MediaCapturePixelFormat.Bgra32;
            var diag = BuildDiagnosticsDescription(
                target.DisplayName,
                target.DeviceNodePath,
                negotiatedWidth,
                negotiatedHeight,
                negotiatedVideoFormat.FrameRate,
                fmt.PixelFormat,
                actualFormat,
                outputPixelFormat,
                dmaBufActive,
                fallbackFrom: null);

            lock (_sync)
            {
                _videoFd = fd;
                _buffers = mappedBuffers;
                _turboJpeg = turboJpeg;
                _deviceDisplayName = target.DisplayName;
                _deviceNodePath = target.DeviceNodePath;
                _busInfo = target.BusInfo;
                _rawFourCcs = rawFourCcs;
                _fallbackFormats = fallbackFormats;
                _nextFallbackIndex = 0;
                _allowHardware = allowHardware;
                _initialWidth = negotiatedWidth;
                _initialHeight = negotiatedHeight;
                _width = negotiatedWidth;
                _height = negotiatedHeight;
                _bytesPerLine = bytesPerLine;
                _negotiatedFourCc = fmt.PixelFormat;
                _negotiatedPixelFormat = actualFormat;
                _yuvColorSpace = colorSpace;
                Volatile.Write(ref _dmaBufActive, dmaBufActive);
                Volatile.Write(ref _liveDiagnosticsDescription, diag);
            }

            // Ownership transferred to fields.
            fd = -1;
            turboJpeg = null;
            mappedBuffers = [];

            return new CaptureOpenResult(
                target.DeviceId,
                target.DisplayName,
                target.PairedAudioDeviceId,
                negotiatedVideoFormat,
                outputPixelFormat,
                diag,
                IsHardwareVideoActive: dmaBufActive);
        }
        finally
        {
            turboJpeg?.Dispose();
            if (mappedBuffers.Length > 0)
            {
                ReleaseBuffers(fd, mappedBuffers);
            }

            if (fd >= 0)
            {
                _ = LinuxV4l2Interop.Close(fd);
            }
        }
    }

    public void Start(
        Func<bool> prefersHardwareFrame,
        Func<bool> needsCpuFrame,
        Action<CaptureDeliveredFrame> onFrame,
        Action<Exception, bool> onError)
    {
        lock (_sync)
        {
            RuntimeCompat.NotDisposed(_disposed, this);
            if (_captureThread is not null)
            {
                return;
            }

            if (_videoFd < 0 || _buffers.Length == 0)
            {
                throw new InvalidOperationException("Open the V4L2 capture backend before starting.");
            }

            var wakeFd = LinuxV4l2Interop.EventFd(0, LinuxV4l2Interop.EFD_NONBLOCK | LinuxV4l2Interop.EFD_CLOEXEC);
            if (wakeFd < 0)
            {
                var errno = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                throw new IOException($"eventfd() failed (errno={errno}).");
            }

            try
            {
                lock (_queueSync)
                {
                    for (var i = 0; i < _buffers.Length; i++)
                    {
                        LinuxV4l2Interop.V4l2Buffer qbuf = default;
                        qbuf.Type = LinuxV4l2Interop.V4L2_BUF_TYPE_VIDEO_CAPTURE;
                        qbuf.Memory = LinuxV4l2Interop.V4L2_MEMORY_MMAP;
                        qbuf.Index = _buffers[i].Index;

                        if (LinuxV4l2Interop.Ioctl(_videoFd, LinuxV4l2Interop.VIDIOC_QBUF, &qbuf, out var qErr) < 0)
                        {
                            throw new IOException($"VIDIOC_QBUF failed for buffer {i} on '{_deviceNodePath}' (errno={qErr}).");
                        }
                    }

                    var bufType = LinuxV4l2Interop.V4L2_BUF_TYPE_VIDEO_CAPTURE;
                    if (LinuxV4l2Interop.Ioctl(_videoFd, LinuxV4l2Interop.VIDIOC_STREAMON, ref bufType, out var onErr) < 0)
                    {
                        throw new IOException($"VIDIOC_STREAMON failed on '{_deviceNodePath}' (errno={onErr}).");
                    }

                    _streaming = true;
                    Volatile.Write(ref _inFlightDmaBufCount, 0);
                }
            }
            catch
            {
                _ = LinuxV4l2Interop.Close(wakeFd);
                throw;
            }

            _wakeFd = wakeFd;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _captureThread = new Thread(() => RunCaptureLoop(token, prefersHardwareFrame, needsCpuFrame, onFrame, onError))
            {
                IsBackground = true,
                Name = $"FrameFlux.Capture.V4L2 ({_deviceDisplayName})"
            };
            _captureThread.Start();
        }
    }

    private void RunCaptureLoop(
        CancellationToken token,
        Func<bool> prefersHardwareFrame,
        Func<bool> needsCpuFrame,
        Action<CaptureDeliveredFrame> onFrame,
        Action<Exception, bool> onError)
    {
        var consecutiveDecodeFailures = 0;
        var consecutiveBufferErrors = 0;
        var deliveredAnyFrame = false;
        var streamStartTicks = Stopwatch.GetTimestamp();
        var isVirtualUsbIp = _busInfo.Contains("vhci_hcd", StringComparison.OrdinalIgnoreCase);
        var startupStallThreshold = TimeSpan.FromMilliseconds(isVirtualUsbIp ? 1200 : 2500);
        var pollFds = stackalloc LinuxV4l2Interop.PollFd[2];

        while (!token.IsCancellationRequested)
        {
            var videoFd = Volatile.Read(ref _videoFd);
            var wakeFd = Volatile.Read(ref _wakeFd);
            if (videoFd < 0)
            {
                break;
            }

            var readStart = Stopwatch.GetTimestamp();
            pollFds[0] = new LinuxV4l2Interop.PollFd { Fd = videoFd, Events = LinuxV4l2Interop.POLLIN };
            pollFds[1] = new LinuxV4l2Interop.PollFd { Fd = wakeFd, Events = LinuxV4l2Interop.POLLIN };
            var pollCount = wakeFd >= 0 ? (nuint)2 : (nuint)1;

            var ready = LinuxV4l2Interop.Poll(pollFds, pollCount, PollTimeoutMilliseconds);
            if (token.IsCancellationRequested)
            {
                break;
            }

            if (ready < 0)
            {
                var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                if (err == LinuxV4l2Interop.EINTR)
                {
                    continue;
                }

                onError(new IOException($"V4L2 poll() failed on '{_deviceNodePath}' (errno={err})."), true);
                break;
            }

            if (ready == 0)
            {
                // On bandwidth-constrained USB hubs or WSL2 USB/IP (uvcvideo nodrop=0), incomplete isochronous
                // frames are dropped inside the kernel so poll() times out with zero frames delivered.
                // Automatically step down to a lower-bandwidth format if available.
                if (!deliveredAnyFrame &&
                    RuntimeCompat.Elapsed(streamStartTicks) >= startupStallThreshold &&
                    TryStepDownFormat())
                {
                    consecutiveBufferErrors = 0;
                    consecutiveDecodeFailures = 0;
                    streamStartTicks = Stopwatch.GetTimestamp();
                }
                continue;
            }

            if (wakeFd >= 0 && (pollFds[1].Revents & LinuxV4l2Interop.POLLIN) != 0)
            {
                break;
            }

            if ((pollFds[0].Revents & (LinuxV4l2Interop.POLLERR | LinuxV4l2Interop.POLLHUP | LinuxV4l2Interop.POLLNVAL)) != 0)
            {
                onError(
                    new IOException($"V4L2 capture device '{_deviceDisplayName}' ({_deviceNodePath}) was disconnected."),
                    true);
                break;
            }

            if ((pollFds[0].Revents & LinuxV4l2Interop.POLLIN) == 0)
            {
                continue;
            }

            LinuxV4l2Interop.V4l2Buffer dqbuf = default;
            dqbuf.Type = LinuxV4l2Interop.V4L2_BUF_TYPE_VIDEO_CAPTURE;
            dqbuf.Memory = LinuxV4l2Interop.V4L2_MEMORY_MMAP;

            int dqResult;
            int dqErr;
            lock (_queueSync)
            {
                if (!_streaming || _videoFd < 0)
                {
                    break;
                }

                dqResult = LinuxV4l2Interop.Ioctl(_videoFd, LinuxV4l2Interop.VIDIOC_DQBUF, &dqbuf, out dqErr);
            }

            if (dqResult < 0)
            {
                if (dqErr == LinuxV4l2Interop.EAGAIN)
                {
                    continue;
                }

                if (!token.IsCancellationRequested)
                {
                    var recoverable = dqErr is LinuxV4l2Interop.ENODEV or LinuxV4l2Interop.EIO;
                    onError(
                        new IOException($"VIDIOC_DQBUF failed on '{_deviceNodePath}' (errno={dqErr})."),
                        recoverable);
                }
                break;
            }

            var readMs = RuntimeCompat.Elapsed(readStart).TotalMilliseconds;
            var slotIndex = dqbuf.Index;
            if (slotIndex >= (uint)_buffers.Length)
            {
                TryRequeueBuffer(slotIndex);
                continue;
            }

            if ((dqbuf.Flags & LinuxV4l2Interop.V4L2_BUF_FLAG_ERROR) != 0 || dqbuf.BytesUsed == 0)
            {
                TryRequeueBuffer(slotIndex);
                consecutiveBufferErrors++;
                if (!deliveredAnyFrame &&
                    consecutiveBufferErrors >= StartupErrorFramesBeforeFallback &&
                    TryStepDownFormat())
                {
                    consecutiveBufferErrors = 0;
                    consecutiveDecodeFailures = 0;
                    streamStartTicks = Stopwatch.GetTimestamp();
                }
                continue;
            }

            consecutiveBufferErrors = 0;

            var slot = _buffers[slotIndex];
            var decodeStart = Stopwatch.GetTimestamp();
            var requeueNow = true;

            try
            {
                var wantHw = _dmaBufActive && prefersHardwareFrame();
                var wantCpu = needsCpuFrame();
                CaptureMediaFrameLease? hardwareLease = null;
                CaptureMediaFrameLease? cpuLease = null;

                // Zero-copy DMA-BUF fast path (only when at least 2 buffers remain queued in the V4L2 ring).
                if (wantHw &&
                    slot.DmaBufFd >= 0 &&
                    Volatile.Read(ref _inFlightDmaBufCount) < Math.Max(1, _buffers.Length - 2))
                {
                    var ySize = (long)_bytesPerLine * _height;
                    var totalSize = Math.Max((long)slot.MappedLength, ySize + ((long)_bytesPerLine * ((_height + 1) / 2)));
                    var dmaBuf = new MediaDmaBufFrameBuffer(
                        new[] { new MediaDmaBufObject(slot.DmaBufFd, totalSize, 0UL) },
                        new[]
                        {
                            new MediaDmaBufLayer(
                                LinuxV4l2Interop.DRM_FORMAT_NV12,
                                new[]
                                {
                                    new MediaDmaBufPlane(0, 0, _bytesPerLine),
                                    new MediaDmaBufPlane(0, checked((int)ySize), _bytesPerLine)
                                })
                        });

                    Interlocked.Increment(ref _inFlightDmaBufCount);
                    var capturedIndex = slot.Index;
                    hardwareLease = CaptureMediaFrameLease.CreateDmaBuf(
                        _width,
                        _height,
                        dmaBuf,
                        releaseCallback: () =>
                        {
                            Interlocked.Decrement(ref _inFlightDmaBufCount);
                            TryRequeueBuffer(capturedIndex);
                        },
                        MediaPixelFormat.Nv12);
                    requeueNow = false;
                }

                if (hardwareLease is null || wantCpu)
                {
                    if (!TryConvertSlotToCpuBgra(slot, checked((int)dqbuf.BytesUsed), out cpuLease, out var decodeError))
                    {
                        hardwareLease?.Dispose();
                        consecutiveDecodeFailures++;
                        if (consecutiveDecodeFailures >= MaxConsecutiveDecodeFailures)
                        {
                            onError(
                                new InvalidOperationException(
                                    decodeError ?? $"Failed to decode {MaxConsecutiveDecodeFailures} consecutive V4L2 frames."),
                                true);
                            break;
                        }

                        continue;
                    }
                }

                consecutiveDecodeFailures = 0;
                deliveredAnyFrame = true;

                if (requeueNow)
                {
                    TryRequeueBuffer(slotIndex);
                    requeueNow = false;
                }

                var decodeMs = RuntimeCompat.Elapsed(decodeStart).TotalMilliseconds;
                onFrame(new CaptureDeliveredFrame(hardwareLease, cpuLease, _width, _height, readMs, decodeMs));
            }
            catch (Exception exception)
            {
                if (requeueNow)
                {
                    TryRequeueBuffer(slotIndex);
                }

                if (!token.IsCancellationRequested)
                {
                    onError(exception, false);
                }
                break;
            }
        }
    }

    private bool TryConvertSlotToCpuBgra(
        MmapBufferSlot slot,
        int bytesUsed,
        out CaptureMediaFrameLease? cpuLease,
        out string? error)
    {
        cpuLease = null;
        error = null;

        var width = _width;
        var height = _height;
        var bgraStride = checked(width * 4);
        var bgraSize = checked(bgraStride * height);
        var dest = _bufferPool.Rent(bgraSize);

        try
        {
            var src = slot.MappedAddress;
            var stride = _bytesPerLine;

            switch (_negotiatedPixelFormat)
            {
                case MediaCapturePixelFormat.Mjpeg:
                    if (_turboJpeg is null ||
                        !_turboJpeg.TryDecodeToBgra(src, bytesUsed, dest, width, height, bgraStride, out error))
                    {
                        _bufferPool.Return(dest, bgraSize);
                        return false;
                    }
                    break;

                case MediaCapturePixelFormat.Yuy2:
                    if (bytesUsed < stride * height)
                    {
                        error = $"Short YUYV frame ({bytesUsed} bytes).";
                        _bufferPool.Return(dest, bgraSize);
                        return false;
                    }

                    CapturePixelConverter.Yuy2ToBgra32(src, stride, dest, width, height, bgraStride, _yuvColorSpace);
                    break;

                case MediaCapturePixelFormat.Nv12:
                {
                    var yPlaneBytes = checked(stride * height);
                    var uvPlaneBytes = checked(stride * ((height + 1) >> 1));
                    if (bytesUsed < yPlaneBytes + uvPlaneBytes)
                    {
                        error = $"Short NV12 frame ({bytesUsed} bytes).";
                        _bufferPool.Return(dest, bgraSize);
                        return false;
                    }

                    CapturePixelConverter.Nv12ToBgra32(
                        src,
                        stride,
                        src + yPlaneBytes,
                        stride,
                        dest,
                        width,
                        height,
                        bgraStride,
                        _yuvColorSpace);
                    break;
                }

                case MediaCapturePixelFormat.Yuv420P:
                {
                    var yPlaneBytes = checked(stride * height);
                    var uvStride = (stride + 1) >> 1;
                    var uvRows = (height + 1) >> 1;
                    var uvPlaneBytes = checked(uvStride * uvRows);
                    if (bytesUsed < yPlaneBytes + (uvPlaneBytes * 2))
                    {
                        error = $"Short YUV420P frame ({bytesUsed} bytes).";
                        _bufferPool.Return(dest, bgraSize);
                        return false;
                    }

                    CapturePixelConverter.Yuv420PToBgra32(
                        src,
                        stride,
                        src + yPlaneBytes,
                        uvStride,
                        src + yPlaneBytes + uvPlaneBytes,
                        uvStride,
                        dest,
                        width,
                        height,
                        bgraStride,
                        _yuvColorSpace);
                    break;
                }

                case MediaCapturePixelFormat.Bgra32:
                    if (_negotiatedFourCc == LinuxV4l2Interop.V4L2_PIX_FMT_RGB24)
                    {
                        CapturePixelConverter.Rgb24ToBgra32(src, stride, dest, width, height, bgraStride);
                    }
                    else if (_negotiatedFourCc == LinuxV4l2Interop.V4L2_PIX_FMT_BGR24)
                    {
                        CapturePixelConverter.Bgr24ToBgra32(src, stride, dest, width, height, bgraStride);
                    }
                    else
                    {
                        CapturePixelConverter.Rgb32ToBgra32(src, stride, dest, width, height, bgraStride, forceOpaqueAlpha: true);
                    }
                    break;

                default:
                    error = $"Unsupported negotiated V4L2 pixel format {_negotiatedPixelFormat}.";
                    _bufferPool.Return(dest, bgraSize);
                    return false;
            }

            cpuLease = CaptureMediaFrameLease.CreateBgraCpu(width, height, bgraStride, dest, bgraSize, _bufferPool);
            return true;
        }
        catch (Exception ex)
        {
            _bufferPool.Return(dest, bgraSize);
            error = ex.Message;
            return false;
        }
    }

    private void TryRequeueBuffer(uint index)
    {
        lock (_queueSync)
        {
            if (!_streaming || _videoFd < 0)
            {
                return;
            }

            LinuxV4l2Interop.V4l2Buffer qbuf = default;
            qbuf.Type = LinuxV4l2Interop.V4L2_BUF_TYPE_VIDEO_CAPTURE;
            qbuf.Memory = LinuxV4l2Interop.V4L2_MEMORY_MMAP;
            qbuf.Index = index;
            _ = LinuxV4l2Interop.Ioctl(_videoFd, LinuxV4l2Interop.VIDIOC_QBUF, &qbuf, out _);
        }
    }

    public void Stop()
    {
        Thread? thread;
        CancellationTokenSource? cts;
        int wakeFd;

        lock (_sync)
        {
            cts = _cts;
            _cts = null;
            thread = _captureThread;
            _captureThread = null;
            wakeFd = _wakeFd;
            _wakeFd = -1;
        }

        cts?.Cancel();
        if (wakeFd >= 0)
        {
            ulong signal = 1;
            _ = LinuxV4l2Interop.Write(wakeFd, &signal, sizeof(ulong));
        }

        if (thread is not null && thread != Thread.CurrentThread)
        {
            try
            {
                thread.Join(TimeSpan.FromSeconds(3));
            }
            catch
            {
                // Best-effort join on shutdown.
            }
        }

        if (wakeFd >= 0)
        {
            _ = LinuxV4l2Interop.Close(wakeFd);
        }

        cts?.Dispose();

        lock (_queueSync)
        {
            if (_streaming && _videoFd >= 0)
            {
                var bufType = LinuxV4l2Interop.V4L2_BUF_TYPE_VIDEO_CAPTURE;
                _ = LinuxV4l2Interop.Ioctl(_videoFd, LinuxV4l2Interop.VIDIOC_STREAMOFF, ref bufType, out _);
                _streaming = false;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stop();

        int videoFd;
        MmapBufferSlot[] buffers;
        LinuxTurboJpegDecoder? turboJpeg;
        lock (_sync)
        {
            videoFd = _videoFd;
            _videoFd = -1;
            buffers = _buffers;
            _buffers = [];
            turboJpeg = _turboJpeg;
            _turboJpeg = null;
        }

        turboJpeg?.Dispose();
        ReleaseBuffers(videoFd, buffers);
        if (videoFd >= 0)
        {
            _ = LinuxV4l2Interop.Close(videoFd);
        }
    }

    private static uint ResolveBestFourCc(MediaCapturePixelFormat target, IReadOnlyList<uint> deviceFourCcs)
    {
        foreach (var fourCc in deviceFourCcs)
        {
            if (LinuxV4l2Interop.MapFourCcToPixelFormat(fourCc) == target)
            {
                return fourCc;
            }
        }

        return LinuxV4l2Interop.MapPixelFormatToPreferredFourCc(target);
    }

    private static double TryConfigureFrameRate(int fd, double requestedFps)
    {
        LinuxV4l2Interop.V4l2StreamParm parm = default;
        parm.Type = LinuxV4l2Interop.V4L2_BUF_TYPE_VIDEO_CAPTURE;
        if (LinuxV4l2Interop.Ioctl(fd, LinuxV4l2Interop.VIDIOC_G_PARM, &parm, out _) < 0)
        {
            return requestedFps;
        }

        if ((parm.CaptureCapability & LinuxV4l2Interop.V4L2_CAP_TIMEPERFRAME) != 0 && requestedFps > 0d)
        {
            parm.TimePerFrame.Numerator = 1000;
            parm.TimePerFrame.Denominator = checked((uint)Math.Max(1, Math.Round(requestedFps * 1000d)));
            _ = LinuxV4l2Interop.Ioctl(fd, LinuxV4l2Interop.VIDIOC_S_PARM, &parm, out _);
        }

        var actual = LinuxV4l2DeviceEnumerator.FractToFps(parm.TimePerFrame);
        return actual > 0d ? Math.Round(actual, 2) : requestedFps;
    }

    private static MmapBufferSlot[] AllocateAndMapBuffers(
        int fd,
        int requestedCount,
        bool tryExportDmaBuf,
        out bool dmaBufActive)
    {
        dmaBufActive = false;

        LinuxV4l2Interop.V4l2RequestBuffers req = default;
        req.Count = checked((uint)requestedCount);
        req.Type = LinuxV4l2Interop.V4L2_BUF_TYPE_VIDEO_CAPTURE;
        req.Memory = LinuxV4l2Interop.V4L2_MEMORY_MMAP;

        if (LinuxV4l2Interop.Ioctl(fd, LinuxV4l2Interop.VIDIOC_REQBUFS, &req, out var reqErr) < 0 || req.Count == 0)
        {
            throw new IOException($"VIDIOC_REQBUFS failed (count={requestedCount}, errno={reqErr}).");
        }

        var count = checked((int)req.Count);
        var slots = new MmapBufferSlot[count];
        try
        {
            var allExported = tryExportDmaBuf && count >= 3;
            for (var i = 0; i < count; i++)
            {
                LinuxV4l2Interop.V4l2Buffer query = default;
                query.Type = LinuxV4l2Interop.V4L2_BUF_TYPE_VIDEO_CAPTURE;
                query.Memory = LinuxV4l2Interop.V4L2_MEMORY_MMAP;
                query.Index = checked((uint)i);

                if (LinuxV4l2Interop.Ioctl(fd, LinuxV4l2Interop.VIDIOC_QUERYBUF, &query, out var qErr) < 0)
                {
                    throw new IOException($"VIDIOC_QUERYBUF failed for buffer {i} (errno={qErr}).");
                }

                var mapped = LinuxV4l2Interop.Mmap(
                    IntPtr.Zero,
                    query.Length,
                    LinuxV4l2Interop.PROT_READ | LinuxV4l2Interop.PROT_WRITE,
                    LinuxV4l2Interop.MAP_SHARED,
                    fd,
                    (nint)query.Offset);
                if (mapped == LinuxV4l2Interop.MapFailed)
                {
                    var mapErr = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    throw new IOException($"mmap() failed for V4L2 buffer {i} (errno={mapErr}).");
                }

                var slot = new MmapBufferSlot
                {
                    Index = query.Index,
                    MappedAddress = mapped,
                    MappedLength = query.Length
                };

                if (allExported)
                {
                    LinuxV4l2Interop.V4l2ExportBuffer exp = default;
                    exp.Type = LinuxV4l2Interop.V4L2_BUF_TYPE_VIDEO_CAPTURE;
                    exp.Index = query.Index;
                    exp.Plane = 0;
                    exp.Flags = (uint)(LinuxV4l2Interop.O_RDONLY | LinuxV4l2Interop.O_CLOEXEC);

                    if (LinuxV4l2Interop.Ioctl(fd, LinuxV4l2Interop.VIDIOC_EXPBUF, &exp, out _) == 0 && exp.Fd >= 0)
                    {
                        slot.DmaBufFd = exp.Fd;
                    }
                    else
                    {
                        allExported = false;
                    }
                }

                slots[i] = slot;
            }

            if (!allExported)
            {
                for (var i = 0; i < count; i++)
                {
                    if (slots[i].DmaBufFd >= 0)
                    {
                        _ = LinuxV4l2Interop.Close(slots[i].DmaBufFd);
                        slots[i].DmaBufFd = -1;
                    }
                }
            }

            dmaBufActive = allExported;
            return slots;
        }
        catch
        {
            ReleaseBuffers(fd, slots);
            throw;
        }
    }

    private static void ReleaseBuffers(int fd, MmapBufferSlot[] slots)
    {
        for (var i = 0; i < slots.Length; i++)
        {
            var slot = slots[i];
            if (slot is null)
            {
                continue;
            }

            if (slot.DmaBufFd >= 0)
            {
                _ = LinuxV4l2Interop.Close(slot.DmaBufFd);
                slot.DmaBufFd = -1;
            }

            if (slot.MappedAddress != IntPtr.Zero && slot.MappedAddress != LinuxV4l2Interop.MapFailed && slot.MappedLength > 0)
            {
                _ = LinuxV4l2Interop.Munmap(slot.MappedAddress, slot.MappedLength);
                slot.MappedAddress = IntPtr.Zero;
            }
        }

        if (fd >= 0)
        {
            LinuxV4l2Interop.V4l2RequestBuffers zeroReq = default;
            zeroReq.Count = 0;
            zeroReq.Type = LinuxV4l2Interop.V4L2_BUF_TYPE_VIDEO_CAPTURE;
            zeroReq.Memory = LinuxV4l2Interop.V4L2_MEMORY_MMAP;
            _ = LinuxV4l2Interop.Ioctl(fd, LinuxV4l2Interop.VIDIOC_REQBUFS, &zeroReq, out _);
        }
    }

    internal static List<MediaCaptureVideoFormat> BuildBandwidthFallbackChain(
        IReadOnlyList<MediaCaptureVideoFormat> decodableFormats,
        MediaCaptureVideoFormat initialFormat)
    {
        var initialArea = (long)initialFormat.Width * initialFormat.Height;
        var seen = new HashSet<(int Width, int Height, MediaCapturePixelFormat Format)>
        {
            (initialFormat.Width, initialFormat.Height, initialFormat.PixelFormat)
        };

        var candidates = new List<MediaCaptureVideoFormat>();
        foreach (var candidate in decodableFormats
            .Where(f => (long)f.Width * f.Height < initialArea)
            .OrderByDescending(static f => f.PixelFormat == MediaCapturePixelFormat.Mjpeg ? 1 : 0)
            .ThenByDescending(static f => (long)f.Width * f.Height)
            .ThenByDescending(static f => f.FrameRate))
        {
            if (seen.Add((candidate.Width, candidate.Height, candidate.PixelFormat)))
            {
                candidates.Add(candidate);
            }
        }

        // Prefer stepping down meaningfully in pixel area first (e.g. 1280x720 -> 640x480 before 960x720)
        // so bandwidth-constrained USB hubs / WSL2 USB/IP recover in a single hop.
        var prioritized = new List<MediaCaptureVideoFormat>(candidates.Count);
        var deferred = new List<MediaCaptureVideoFormat>();
        foreach (var candidate in candidates)
        {
            var area = (long)candidate.Width * candidate.Height;
            if (area <= (initialArea * 60) / 100)
            {
                prioritized.Add(candidate);
            }
            else
            {
                deferred.Add(candidate);
            }
        }

        prioritized.AddRange(deferred);
        return prioritized;
    }

    private bool TryStepDownFormat()
    {
        lock (_queueSync)
        {
            if (!_streaming || _videoFd < 0 || _nextFallbackIndex >= _fallbackFormats.Count)
            {
                return false;
            }

            while (_nextFallbackIndex < _fallbackFormats.Count)
            {
                var next = _fallbackFormats[_nextFallbackIndex++];
                var bufType = LinuxV4l2Interop.V4L2_BUF_TYPE_VIDEO_CAPTURE;
                _ = LinuxV4l2Interop.Ioctl(_videoFd, LinuxV4l2Interop.VIDIOC_STREAMOFF, ref bufType, out _);
                _streaming = false;

                ReleaseBuffers(_videoFd, _buffers);
                _buffers = [];

                var desiredFourCc = ResolveBestFourCc(next.PixelFormat, _rawFourCcs);
                LinuxV4l2Interop.V4l2Format fmt = default;
                fmt.Type = LinuxV4l2Interop.V4L2_BUF_TYPE_VIDEO_CAPTURE;
                fmt.Width = checked((uint)next.Width);
                fmt.Height = checked((uint)next.Height);
                fmt.PixelFormat = desiredFourCc;
                fmt.Field = LinuxV4l2Interop.V4L2_FIELD_ANY;

                if (LinuxV4l2Interop.Ioctl(_videoFd, LinuxV4l2Interop.VIDIOC_S_FMT, &fmt, out _) < 0)
                {
                    continue;
                }

                var actualFormat = LinuxV4l2Interop.MapFourCcToPixelFormat(fmt.PixelFormat);
                if (!LinuxV4l2Interop.IsSupportedLinuxPixelFormat(actualFormat, LinuxTurboJpegDecoder.IsAvailable))
                {
                    continue;
                }

                var actualFps = TryConfigureFrameRate(_videoFd, next.FrameRate);

                if (actualFormat == MediaCapturePixelFormat.Mjpeg && _turboJpeg is null)
                {
                    _turboJpeg = LinuxTurboJpegDecoder.TryCreate();
                    if (_turboJpeg is null)
                    {
                        continue;
                    }
                }

                var wantDmaBuf = _allowHardware && actualFormat == MediaCapturePixelFormat.Nv12;
                var bufferCount = wantDmaBuf ? DmaBufMmapBufferCount : DefaultMmapBufferCount;
                try
                {
                    _buffers = AllocateAndMapBuffers(_videoFd, bufferCount, wantDmaBuf, out var dmaBufActive);
                    for (var i = 0; i < _buffers.Length; i++)
                    {
                        LinuxV4l2Interop.V4l2Buffer qbuf = default;
                        qbuf.Type = LinuxV4l2Interop.V4L2_BUF_TYPE_VIDEO_CAPTURE;
                        qbuf.Memory = LinuxV4l2Interop.V4L2_MEMORY_MMAP;
                        qbuf.Index = _buffers[i].Index;
                        if (LinuxV4l2Interop.Ioctl(_videoFd, LinuxV4l2Interop.VIDIOC_QBUF, &qbuf, out _) < 0)
                        {
                            throw new IOException("VIDIOC_QBUF failed during fallback.");
                        }
                    }

                    if (LinuxV4l2Interop.Ioctl(_videoFd, LinuxV4l2Interop.VIDIOC_STREAMON, ref bufType, out _) < 0)
                    {
                        throw new IOException("VIDIOC_STREAMON failed during fallback.");
                    }

                    _width = checked((int)fmt.Width);
                    _height = checked((int)fmt.Height);
                    _bytesPerLine = fmt.BytesPerLine > 0
                        ? checked((int)fmt.BytesPerLine)
                        : ComputeDefaultBytesPerLine(_width, fmt.PixelFormat);
                    _negotiatedFourCc = fmt.PixelFormat;
                    _negotiatedPixelFormat = actualFormat;
                    _yuvColorSpace = LinuxV4l2Interop.ResolveColorSpace(
                        fmt.ColorSpace,
                        fmt.YCbCrEnc,
                        fmt.Quantization,
                        _width,
                        _height,
                        actualFormat);
                    var outputPixelFormat = dmaBufActive ? MediaCapturePixelFormat.Nv12 : MediaCapturePixelFormat.Bgra32;
                    Volatile.Write(ref _dmaBufActive, dmaBufActive);
                    Volatile.Write(
                        ref _liveDiagnosticsDescription,
                        BuildDiagnosticsDescription(
                            _deviceDisplayName,
                            _deviceNodePath,
                            _width,
                            _height,
                            actualFps > 0d ? actualFps : next.FrameRate,
                            fmt.PixelFormat,
                            actualFormat,
                            outputPixelFormat,
                            dmaBufActive,
                            fallbackFrom: $"{_initialWidth}x{_initialHeight}"));
                    _streaming = true;
                    Volatile.Write(ref _inFlightDmaBufCount, 0);
                    return true;
                }
                catch
                {
                    ReleaseBuffers(_videoFd, _buffers);
                    _buffers = [];
                }
            }

            return false;
        }
    }

    private static string BuildDiagnosticsDescription(
        string displayName,
        string deviceNodePath,
        int width,
        int height,
        double fps,
        uint fourCc,
        MediaCapturePixelFormat negotiatedFormat,
        MediaCapturePixelFormat outputPixelFormat,
        bool dmaBufActive,
        string? fallbackFrom)
    {
        var modeTag = dmaBufActive
            ? "DMA-BUF NV12"
            : negotiatedFormat == MediaCapturePixelFormat.Mjpeg
                ? "CPU TurboJPEG"
                : "CPU SIMD";
        var fpsText = fps > 0d
            ? $"{fps.ToString("F0", CultureInfo.InvariantCulture)}fps"
            : "?fps";
        var fallbackNote = string.IsNullOrEmpty(fallbackFrom)
            ? string.Empty
            : $", fallback from {fallbackFrom}";
        return $"V4L2 [{displayName} ({deviceNodePath}): {width}x{height}@{fpsText} " +
               $"{LinuxV4l2Interop.FormatFourCc(fourCc)} -> {outputPixelFormat}, {modeTag}{fallbackNote}]";
    }

    private static int ComputeDefaultBytesPerLine(int width, uint fourCc) =>
        fourCc switch
        {
            LinuxV4l2Interop.V4L2_PIX_FMT_NV12 or LinuxV4l2Interop.V4L2_PIX_FMT_YUV420 => width,
            LinuxV4l2Interop.V4L2_PIX_FMT_YUYV => checked(width * 2),
            LinuxV4l2Interop.V4L2_PIX_FMT_RGB24 or LinuxV4l2Interop.V4L2_PIX_FMT_BGR24 => checked(width * 3),
            _ => checked(width * 4)
        };
}
#endif
