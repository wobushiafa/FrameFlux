using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace FrameFlux.WebRtc;

/// <summary>
/// WebRTC media player implementing FrameFlux's <see cref="IMediaPlayer"/> contract.
/// Uses SIPSorcery for WebRTC networking (ICE, DTLS-SRTP, RTP, SDP) and connects directly
/// to FrameFlux's presentation and rendering pipeline.
/// </summary>
public sealed class WebRtcMediaPlayer : IMediaPlayer
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly WebRtcPlayerOptions _webrtcOptions;
    private readonly WebRtcFrameBufferPool _framePool;
    private readonly WebRtcVideoSink _videoSink;

    private RTCPeerConnection? _peerConnection;
    private Uri? _sessionResourceUri;
    private MediaSource? _source;
    private MediaOpenOptions _options = new();
    private MediaPlaybackState _state = MediaPlaybackState.Idle;
    private IMediaVideoOutput? _videoOutput;
    private EventHandler<MediaVideoFrame>? _frameReceived;
    private MediaSnapshot? _latestSnapshot;

    private readonly Stopwatch _playbackStopwatch = new();
    private double _volume = 1d;
    private bool _isMuted;
    private double _playbackRate = 1d;
    private readonly IWebRtcVideoDecoder _decoder;
    private readonly IWebRtcAudioOutput _audioOutput;
    private Go2RtcWebSocketSignaling? _wsSignaling;
    private long _frameSequence;
    private uint _lastVideoSsrc;
    private volatile bool _hasReceivedKeyFrame;
    private CancellationTokenSource? _keyFrameRequestCts;
    private readonly WebRtcRtpLossDetector _lossDetector;
    private bool _disposed;

    /// <summary>
    /// Event raised when decoded 16-bit PCM audio samples are received.
    /// </summary>
    public event EventHandler<ReadOnlyMemory<short>>? AudioSamplesReceived;

    public WebRtcMediaPlayer(WebRtcPlayerOptions? options = null)
    {
        _webrtcOptions = options ?? new WebRtcPlayerOptions();
        _decoder = _webrtcOptions.VideoDecoder
            ?? (FfmpegWebRtcVideoDecoder.IsSupported ? new FfmpegWebRtcVideoDecoder() : new DefaultWebRtcVideoDecoder());
        _audioOutput = _webrtcOptions.AudioOutput
            ?? (OperatingSystem.IsWindows() ? new WebRtcWaveOutAudioOutput() : NullWebRtcAudioOutput.Instance);
        _audioOutput.SetVolume(_volume, _isMuted);
        _framePool = new WebRtcFrameBufferPool(
            _webrtcOptions.MaxPoolBufferCount,
            _webrtcOptions.MaxPoolRetainedBytes);

        _lossDetector = new WebRtcRtpLossDetector(
            sendRtcpFeedback: feedback =>
            {
                lock (_sync)
                {
                    if (!_disposed && _peerConnection is not null && _state == MediaPlaybackState.Playing)
                    {
                        _peerConnection.SendRtcpFeedback(SDPMediaTypesEnum.video, feedback);
                    }
                }
            },
            requestKeyFrame: RequestKeyFrame);

        _videoSink = new WebRtcVideoSink(
            onRawImage: OnVideoSinkRawImage,
            onSample: OnVideoSinkSample,
            onFrame: OnVideoFrameReceived);
    }

    public MediaSource? Source
    {
        get
        {
            lock (_sync)
            {
                return _source;
            }
        }
    }

    public MediaOpenOptions Options
    {
        get
        {
            lock (_sync)
            {
                return _options;
            }
        }
    }

    public MediaPlaybackState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public MediaCapabilities Capabilities { get; } = new(
        IsLive: true,
        CanPause: false,
        CanSeek: false,
        CanChangePlaybackRate: false,
        CanCaptureSnapshots: true);

    public MediaDiagnostics Diagnostics
    {
        get
        {
            lock (_sync)
            {
                var pcState = _peerConnection?.connectionState.ToString() ?? "None";
                var iceState = _peerConnection?.iceConnectionState.ToString() ?? "None";
                var hwActive = _decoder.IsHardwareAccelerated;
                var modeDesc = hwActive ? "D3D11VA (GPU)" : "FFmpeg CPU (SIMD)";
                return new MediaDiagnostics(
                    IsHardwareVideoDecodingActive: hwActive,
                    VideoDecoderDiagnostics: $"WebRTC [{modeDesc}, Policy: {_decoder.DecodingPolicy}] (PC: {pcState}, ICE: {iceState})",
                    ReadMilliseconds: 0,
                    DecodeMilliseconds: 0,
                    PerformanceSampleCount: (int)Volatile.Read(ref _frameSequence),
                    LastError: null);
            }
        }
    }

    /// <summary>
    /// Gets the underlying WebRTC <see cref="RTCPeerConnection"/>, or null if not open.
    /// </summary>
    public RTCPeerConnection? PeerConnection
    {
        get
        {
            lock (_sync)
            {
                return _peerConnection;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether hardware video decoding is actively in use.
    /// </summary>
    public bool IsHardwareAccelerated => _decoder.IsHardwareAccelerated;

    public double PlaybackRate
    {
        get
        {
            lock (_sync)
            {
                return _playbackRate;
            }
        }
        set
        {
            if (value != 1d)
            {
                throw new NotSupportedException("WebRTC live streams do not support playback-rate changes.");
            }

            lock (_sync)
            {
                _playbackRate = value;
            }
        }
    }

    public double Volume
    {
        get
        {
            lock (_sync)
            {
                return _volume;
            }
        }
        set
        {
            var clamped = Math.Clamp(value, 0d, 1d);
            lock (_sync)
            {
                _volume = clamped;
                _audioOutput.SetVolume(_volume, _isMuted);
            }
        }
    }

    public bool IsMuted
    {
        get
        {
            lock (_sync)
            {
                return _isMuted;
            }
        }
        set
        {
            lock (_sync)
            {
                _isMuted = value;
                _audioOutput.SetVolume(_volume, _isMuted);
            }
        }
    }

    public IMediaVideoOutput? VideoOutput
    {
        get
        {
            lock (_sync)
            {
                return _videoOutput;
            }
        }
        set
        {
            lock (_sync)
            {
                _videoOutput = value;
                UpdateDecoderOutputPreferences();
            }
        }
    }

    private void UpdateDecoderOutputPreferences()
    {
        if (_decoder is null)
        {
            return;
        }

        var output = _videoOutput;
        var wantsD3D11 = output?.PreferredFrameStorage == MediaFrameStorageKind.D3D11Texture
            || (output?.Supports(MediaFrameStorageKind.D3D11Texture, MediaPixelFormat.Unknown) ?? false);
        _decoder.CanOutputD3D11Texture = wantsD3D11;
    }

    public TimeSpan Position => _playbackStopwatch.Elapsed;

    public TimeSpan? Duration => null;

    public bool CanSeek => false;

    public bool CanPause => false;

    /// <summary>
    /// The video sink bridging SIPSorcery's video pipeline to FrameFlux.
    /// </summary>
    public WebRtcVideoSink VideoSink => _videoSink;

    /// <summary>
    /// Direct access to the frame buffer pool.
    /// </summary>
    public WebRtcFrameBufferPool FrameBufferPool => _framePool;

    public MediaSnapshot? LatestSnapshot
    {
        get
        {
            lock (_sync)
            {
                return _latestSnapshot;
            }
        }
    }

    public event EventHandler<MediaPlaybackStateChangedEventArgs>? StateChanged;

    public event EventHandler<MediaPlaybackErrorEventArgs>? Error;

    public event EventHandler<MediaVideoFrame>? FrameReceived
    {
        add
        {
            lock (_sync)
            {
                _frameReceived += value;
            }
        }
        remove
        {
            lock (_sync)
            {
                _frameReceived -= value;
            }
        }
    }

    public async ValueTask OpenAsync(
        MediaSource source,
        MediaOpenOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options?.Validate();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopInternalAsync(cancellationToken).ConfigureAwait(false);

            lock (_sync)
            {
                _source = source;
                _options = options ?? new MediaOpenOptions();
                _decoder.DecodingPolicy = _options.Video.DecodingPolicy;
                UpdateDecoderOutputPreferences();
            }

            SetState(MediaPlaybackState.Opening);

            var resolvedEndpoint = WebRtcEndpointResolver.Resolve(source, _webrtcOptions);

            var config = new RTCConfiguration
            {
                iceServers = resolvedEndpoint.IceServers
            };

            var pc = new RTCPeerConnection(config);
            lock (_sync)
            {
                _peerConnection = pc;
            }

            // Configure video receive track
            var videoTrack = new MediaStreamTrack(
                _videoSink.GetVideoSinkFormats(),
                MediaStreamStatusEnum.RecvOnly);
            pc.addTrack(videoTrack);

            // Configure audio receive track if audio enabled
            if (_options.Audio.IsEnabled)
            {
                var audioTrack = new MediaStreamTrack(
                    new List<AudioFormat>
                    {
                        new(AudioCodecsEnum.PCMA, 8),
                        new(AudioCodecsEnum.PCMU, 0),
                        new(AudioCodecsEnum.OPUS, 111)
                    },
                    MediaStreamStatusEnum.RecvOnly);
                pc.addTrack(audioTrack);
            }

            // Subscribe to PeerConnection events
            pc.OnVideoFrameReceived += (ep, ts, payload, fmt) =>
            {
                _videoSink.GotVideoFrame(ep, ts, payload, fmt);
            };

            pc.OnAudioFrameReceived += OnAudioFrameReceived;

            pc.OnRtpPacketReceived += (ep, mediaType, rtpPacket) =>
            {
                if (mediaType == SDPMediaTypesEnum.video)
                {
                    var firstTime = _lastVideoSsrc == 0;
                    _lastVideoSsrc = rtpPacket.Header.SyncSource;
                    var localSsrc = pc.VideoLocalTrack?.Ssrc ?? 12345678u;
                    _lossDetector.ProcessRtpPacket(localSsrc, rtpPacket.Header.SyncSource, (ushort)rtpPacket.Header.SequenceNumber);

                    if (firstTime && !_hasReceivedKeyFrame)
                    {
                        RequestKeyFrame();
                    }
                }
            };

            pc.onconnectionstatechange += state =>
            {
                if (state == RTCPeerConnectionState.failed)
                {
                    ReportError(new MediaPlaybackError(
                        "ConnectionFailed",
                        "WebRTC peer connection failed.",
                        IsRecoverable: false));
                }
            };

            // Negotiate SDP
            await NegotiateSdpAsync(pc, resolvedEndpoint, cancellationToken).ConfigureAwait(false);

            SetState(MediaPlaybackState.Ready);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetState(MediaPlaybackState.Stopped);
            throw;
        }
        catch (Exception ex)
        {
            ReportError(new MediaPlaybackError(
                "OpenFailed",
                ex.Message,
                IsRecoverable: false,
                ex));
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask PlayAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != MediaPlaybackState.Ready && _state != MediaPlaybackState.Paused)
            {
                if (_state == MediaPlaybackState.Playing)
                {
                    return;
                }

                throw new InvalidOperationException($"Cannot play in state '{_state}'. Call OpenAsync first.");
            }

            _playbackStopwatch.Restart();
            await _videoSink.StartVideoSink().ConfigureAwait(false);
            _audioOutput.Resume();
            _framePool.StartAcceptingReturns();
            SetState(MediaPlaybackState.Playing);
            StartKeyFrameRequester();
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask PauseAsync(CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("WebRTC live streams do not support pause.");
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed || _state == MediaPlaybackState.Stopped || _state == MediaPlaybackState.Idle)
            {
                return;
            }

            SetState(MediaPlaybackState.Stopping);
            await StopInternalAsync(cancellationToken).ConfigureAwait(false);
            SetState(MediaPlaybackState.Stopped);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("WebRTC live streams do not support seeking.");
    }

    public ValueTask<MediaSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return ValueTask.FromResult(_latestSnapshot);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await StopInternalAsync(CancellationToken.None).ConfigureAwait(false);
            _audioOutput.Dispose();
            _decoder.Dispose();
            _framePool.Dispose();
            _videoSink.Dispose();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    /// <summary>
    /// Manually delivers an already prepared <see cref="IMediaFrameLease"/> into the playback pipeline.
    /// Useful for custom decoders or external frame feeds.
    /// </summary>
    public void DeliverFrame(IMediaFrameLease frameLease)
    {
        ArgumentNullException.ThrowIfNull(frameLease);

        if (_disposed || _state != MediaPlaybackState.Playing)
        {
            frameLease.Dispose();
            return;
        }

        IMediaVideoOutput? output;
        EventHandler<MediaVideoFrame>? frameReceived;
        lock (_sync)
        {
            output = _videoOutput;
            frameReceived = _frameReceived;
        }

        var seq = Interlocked.Increment(ref _frameSequence);
        var now = DateTimeOffset.UtcNow;

        // Create snapshot or MediaVideoFrame if needed
        if (frameReceived is not null || _options.Video.SnapshotPolicy == MediaSnapshotPolicy.KeepLatestFrame)
        {
            if (frameLease.TryGetCpuBuffer(out var cpuBuf))
            {
                var data = new byte[cpuBuf.Size];
                Marshal.Copy(cpuBuf.Buffer, data, 0, cpuBuf.Size);

                if (_options.Video.SnapshotPolicy == MediaSnapshotPolicy.KeepLatestFrame)
                {
                    lock (_sync)
                    {
                        _latestSnapshot = new MediaSnapshot(
                            data,
                            frameLease.Width,
                            frameLease.Height,
                            cpuBuf.Plane0Stride,
                            frameLease.PixelFormat,
                            now);
                    }
                }

                frameReceived?.Invoke(this, new MediaVideoFrame(
                    data,
                    frameLease.Width,
                    frameLease.Height,
                    cpuBuf.Plane0Stride,
                    frameLease.PixelFormat,
                    seq,
                    now));
            }
        }

        // Present to video output
        if (output is null)
        {
            frameLease.Dispose();
            return;
        }

        // Case 1: D3D11 hardware texture frame
        if (frameLease.StorageKind == MediaFrameStorageKind.D3D11Texture)
        {
            if (output.Supports(MediaFrameStorageKind.D3D11Texture, frameLease.PixelFormat) &&
                output.TryPresent(frameLease))
            {
                // Directly presented on GPU (GpuComposition / NativeSurface)
                return;
            }

            // Output does not support D3D11 texture or GPU presentation failed: fallback to software
            frameLease.Dispose();
            _decoder.CanOutputD3D11Texture = false;
            return;
        }

        // Case 2: Check if output supports the frame directly
        if (output.Supports(frameLease.StorageKind, frameLease.PixelFormat))
        {
            if (!output.TryPresent(frameLease))
            {
                frameLease.Dispose();
            }
            return;
        }

        // If output only supports BGRA32 and we have YUV420P or NV12, convert to BGRA32
        if (output.Supports(MediaFrameStorageKind.CpuMemory, MediaPixelFormat.Bgra32) &&
            frameLease.TryGetCpuBuffer(out var srcBuf))
        {
            if (frameLease.Width <= 0 || frameLease.Height <= 0)
            {
                frameLease.Dispose();
                return;
            }

            var bgraStride = frameLease.Width * 4;
            var requiredSize = bgraStride * frameLease.Height;
            if (requiredSize <= 0)
            {
                frameLease.Dispose();
                return;
            }

            var bgraBuffer = _framePool.Rent(requiredSize);
            var bgraLease = new WebRtcMediaFrameLease(bgraBuffer, requiredSize, l => _framePool.Return(l.Buffer, l.Size));
            bgraLease.ResetBgra(frameLease.Width, frameLease.Height, bgraStride);

            if (frameLease.PixelFormat == MediaPixelFormat.Yuv420P)
            {
                var isFullRange = (frameLease as WebRtcMediaFrameLease)?.IsFullRange ?? false;
                WebRtcPixelConverter.Yuv420PToBgra32(
                    srcBuf.Plane0,
                    srcBuf.Plane0Stride,
                    srcBuf.Plane1,
                    srcBuf.Plane1Stride,
                    srcBuf.Plane2,
                    srcBuf.Plane2Stride,
                    bgraBuffer,
                    frameLease.Width,
                    frameLease.Height,
                    bgraStride,
                    isFullRange);
            }
            else if (frameLease.PixelFormat == MediaPixelFormat.Nv12)
            {
                WebRtcPixelConverter.Nv12ToBgra32(
                    srcBuf.Plane0,
                    srcBuf.Plane0Stride,
                    srcBuf.Plane1,
                    srcBuf.Plane1Stride,
                    bgraBuffer,
                    frameLease.Width,
                    frameLease.Height,
                    bgraStride);
            }

            frameLease.Dispose();

            if (!output.TryPresent(bgraLease))
            {
                bgraLease.Dispose();
            }
            return;
        }

        // Unsupported format
        frameLease.Dispose();
    }

    private void OnVideoSinkRawImage(RawImage rawImage)
    {
        if (_disposed || _state != MediaPlaybackState.Playing || rawImage.Sample == IntPtr.Zero)
        {
            return;
        }

        var width = (int)rawImage.Width;
        var height = (int)rawImage.Height;
        var stride = rawImage.Stride;

        var pixelFormat = rawImage.PixelFormat switch
        {
            VideoPixelFormatsEnum.Bgra => MediaPixelFormat.Bgra32,
            VideoPixelFormatsEnum.I420 => MediaPixelFormat.Yuv420P,
            VideoPixelFormatsEnum.NV12 => MediaPixelFormat.Nv12,
            _ => MediaPixelFormat.Bgra32
        };

        var size = stride * height;
        if (pixelFormat == MediaPixelFormat.Yuv420P || pixelFormat == MediaPixelFormat.Nv12)
        {
            size = stride * height * 3 / 2;
        }

        var buffer = _framePool.Rent(size);
        unsafe
        {
            Buffer.MemoryCopy((void*)rawImage.Sample, (void*)buffer, size, size);
        }

        var lease = new WebRtcMediaFrameLease(buffer, size, l => _framePool.Return(l.Buffer, l.Size));
        if (pixelFormat == MediaPixelFormat.Bgra32)
        {
            lease.ResetBgra(width, height, stride);
        }
        else if (pixelFormat == MediaPixelFormat.Yuv420P)
        {
            lease.ResetYuv420P(width, height, stride, stride / 2);
        }
        else if (pixelFormat == MediaPixelFormat.Nv12)
        {
            lease.ResetNv12(width, height, stride, stride);
        }

        DeliverFrame(lease);
    }

    private void OnVideoSinkSample(byte[] sample, uint width, uint height, int stride, VideoPixelFormatsEnum format)
    {
        if (_disposed || _state != MediaPlaybackState.Playing || sample.Length == 0)
        {
            return;
        }

        var w = (int)width;
        var h = (int)height;
        var pixelFormat = format switch
        {
            VideoPixelFormatsEnum.Bgra => MediaPixelFormat.Bgra32,
            VideoPixelFormatsEnum.I420 => MediaPixelFormat.Yuv420P,
            VideoPixelFormatsEnum.NV12 => MediaPixelFormat.Nv12,
            _ => MediaPixelFormat.Bgra32
        };

        var buffer = _framePool.Rent(sample.Length);
        Marshal.Copy(sample, 0, buffer, sample.Length);

        var lease = new WebRtcMediaFrameLease(buffer, sample.Length, l => _framePool.Return(l.Buffer, l.Size));
        if (pixelFormat == MediaPixelFormat.Bgra32)
        {
            lease.ResetBgra(w, h, stride);
        }
        else if (pixelFormat == MediaPixelFormat.Yuv420P)
        {
            lease.ResetYuv420P(w, h, stride, stride / 2);
        }
        else if (pixelFormat == MediaPixelFormat.Nv12)
        {
            lease.ResetNv12(w, h, stride, stride);
        }

        DeliverFrame(lease);
    }

    private void OnVideoFrameReceived(IPEndPoint endpoint, uint timestamp, byte[] payload, VideoFormat format)
    {
        if (_disposed || _state != MediaPlaybackState.Playing)
        {
            return;
        }

        if (!_hasReceivedKeyFrame && IsKeyFrame(payload, format.Codec))
        {
            _hasReceivedKeyFrame = true;
        }

        if (_decoder.CanDecode(format))
        {
            if (_decoder.TryDecode(payload, format, _framePool, out var decodedFrame) && decodedFrame is not null)
            {
                DeliverFrame(decodedFrame);
            }
            else
            {
                // Decode failed or corrupt frame dropped - trigger rate-limited keyframe refresh
                _lossDetector.RequestKeyFrameRateLimited();
            }
        }
    }

    private async Task NegotiateSdpAsync(
        RTCPeerConnection pc,
        WebRtcResolvedEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        if (endpoint.Kind == WebRtcEndpointKind.Go2RtcWebSocket)
        {
            var offer = pc.createOffer();
            var enhancedOfferSdp = EnhanceSdpOffer(offer.sdp);
            var enhancedOffer = new RTCSessionDescriptionInit
            {
                type = RTCSdpType.offer,
                sdp = enhancedOfferSdp
            };
            await pc.setLocalDescription(enhancedOffer).ConfigureAwait(false);

            var signaling = await Go2RtcWebSocketSignaling.ConnectAndExchangeAsync(
                endpoint.EndpointUri!,
                pc,
                enhancedOfferSdp,
                cancellationToken).ConfigureAwait(false);

            lock (_sync)
            {
                _wsSignaling = signaling;
            }

            var answerSdp = await signaling.WaitForAnswerAsync(cancellationToken).ConfigureAwait(false);

            var answer = new RTCSessionDescriptionInit
            {
                type = RTCSdpType.answer,
                sdp = answerSdp
            };

            var setResult = pc.setRemoteDescription(answer);
            if (setResult != SetDescriptionResultEnum.OK)
            {
                throw new InvalidOperationException($"Failed to set remote SDP description from WebSocket: {setResult}.");
            }

            signaling.OnRemoteDescriptionSet();
        }
        else if (endpoint.Kind is WebRtcEndpointKind.Whep or WebRtcEndpointKind.Whip)
        {
            var offer = pc.createOffer();
            var enhancedOfferSdp = EnhanceSdpOffer(offer.sdp);
            var enhancedOffer = new RTCSessionDescriptionInit
            {
                type = RTCSdpType.offer,
                sdp = enhancedOfferSdp
            };
            await pc.setLocalDescription(enhancedOffer).ConfigureAwait(false);

            var (answerSdp, sessionResourceUri) = await WebRtcEndpointResolver.ExchangeWhepOfferAsync(
                endpoint.EndpointUri!,
                enhancedOfferSdp,
                _webrtcOptions,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            lock (_sync)
            {
                _sessionResourceUri = sessionResourceUri;
            }

            var answer = new RTCSessionDescriptionInit
            {
                type = RTCSdpType.answer,
                sdp = answerSdp
            };

            var setResult = pc.setRemoteDescription(answer);
            if (setResult != SetDescriptionResultEnum.OK)
            {
                throw new InvalidOperationException($"Failed to set remote SDP description: {setResult}.");
            }
        }
        else if (endpoint.Kind == WebRtcEndpointKind.DirectSdp || endpoint.Kind == WebRtcEndpointKind.JsonConfig)
        {
            if (!string.IsNullOrWhiteSpace(endpoint.RawSdp))
            {
                var isOffer = endpoint.SdpType.Equals("offer", StringComparison.OrdinalIgnoreCase);
                if (isOffer)
                {
                    var remoteOffer = new RTCSessionDescriptionInit
                    {
                        type = RTCSdpType.offer,
                        sdp = endpoint.RawSdp
                    };
                    pc.setRemoteDescription(remoteOffer);
                    var answer = pc.createAnswer();
                    await pc.setLocalDescription(answer).ConfigureAwait(false);
                }
                else
                {
                    var offer = pc.createOffer();
                    var enhancedOfferSdp = EnhanceSdpOffer(offer.sdp);
                    var enhancedOffer = new RTCSessionDescriptionInit
                    {
                        type = RTCSdpType.offer,
                        sdp = enhancedOfferSdp
                    };
                    await pc.setLocalDescription(enhancedOffer).ConfigureAwait(false);
                    var remoteAnswer = new RTCSessionDescriptionInit
                    {
                        type = RTCSdpType.answer,
                        sdp = endpoint.RawSdp
                    };
                    pc.setRemoteDescription(remoteAnswer);
                }
            }
        }
    }

    /// <summary>
    /// Requests an immediate IDR keyframe (Full Intra Refresh) from the remote WebRTC peer via RTCP PLI.
    /// </summary>
    public void RequestKeyFrame()
    {
        lock (_sync)
        {
            if (_disposed || _peerConnection is null || _state != MediaPlaybackState.Playing)
            {
                return;
            }

            try
            {
                var localSsrc = _peerConnection.VideoLocalTrack?.Ssrc ?? 0;
                if (localSsrc == 0)
                {
                    localSsrc = 12345678u;
                }

                var remoteSsrc = _peerConnection.VideoRemoteTrack?.Ssrc ?? 0;
                if (remoteSsrc == 0)
                {
                    remoteSsrc = _lastVideoSsrc;
                }

                var pli = new RTCPFeedback(localSsrc, remoteSsrc, PSFBFeedbackTypesEnum.PLI);
                _peerConnection.SendRtcpFeedback(SDPMediaTypesEnum.video, pli);
            }
            catch
            {
                // Socket may not be ready yet
            }
        }
    }

    private void StartKeyFrameRequester()
    {
        _hasReceivedKeyFrame = false;
        _keyFrameRequestCts?.Cancel();
        _keyFrameRequestCts?.Dispose();
        var cts = new CancellationTokenSource();
        _keyFrameRequestCts = cts;

        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 10 && !cts.IsCancellationRequested; i++)
            {
                RequestKeyFrame();
                try
                {
                    await Task.Delay(300, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });
    }

    private static bool IsKeyFrame(ReadOnlySpan<byte> payload, VideoCodecsEnum codec)
    {
        if (payload.Length < 5)
        {
            return false;
        }

        var offset = 0;
        if (payload[0] == 0 && payload[1] == 0 && payload[2] == 1)
        {
            offset = 3;
        }
        else if (payload[0] == 0 && payload[1] == 0 && payload[2] == 0 && payload[3] == 1)
        {
            offset = 4;
        }

        if (offset >= payload.Length)
        {
            return false;
        }

        if (codec == VideoCodecsEnum.H265)
        {
            var nalType = (payload[offset] >> 1) & 0x3F;
            return nalType is 19 or 20 or 21;
        }
        else if (codec == VideoCodecsEnum.H264)
        {
            var nalType = payload[offset] & 0x1F;
            return nalType == 5;
        }

        return true;
    }

    private static string EnhanceSdpOffer(string sdp)
    {
        var lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var enhanced = new List<string>(lines.Length + 16);

        foreach (var line in lines)
        {
            enhanced.Add(line);
            if (line.StartsWith("a=rtpmap:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line["a=rtpmap:".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1 && int.TryParse(parts[0], out var pt))
                {
                    var codecName = parts[1].Split('/')[0];
                    if (codecName.Equals("H264", StringComparison.OrdinalIgnoreCase) ||
                        codecName.Equals("H265", StringComparison.OrdinalIgnoreCase) ||
                        codecName.Equals("VP8", StringComparison.OrdinalIgnoreCase) ||
                        codecName.Equals("VP9", StringComparison.OrdinalIgnoreCase) ||
                        codecName.Equals("AV1", StringComparison.OrdinalIgnoreCase))
                    {
                        enhanced.Add($"a=rtcp-fb:{pt} nack");
                        enhanced.Add($"a=rtcp-fb:{pt} nack pli");
                        enhanced.Add($"a=rtcp-fb:{pt} ccm fir");
                        enhanced.Add($"a=rtcp-fb:{pt} goog-remb");
                    }
                }
            }
        }

        return string.Join("\r\n", enhanced);
    }

    private async Task StopInternalAsync(CancellationToken cancellationToken)
    {
        _playbackStopwatch.Stop();
        _playbackStopwatch.Reset();
        _keyFrameRequestCts?.Cancel();
        _keyFrameRequestCts = null;
        _lossDetector.Reset();
        await _videoSink.CloseVideoSink().ConfigureAwait(false);

        Go2RtcWebSocketSignaling? wsSignaling;
        RTCPeerConnection? pc;
        Uri? sessionUri;
        lock (_sync)
        {
            wsSignaling = _wsSignaling;
            _wsSignaling = null;
            pc = _peerConnection;
            _peerConnection = null;
            sessionUri = _sessionResourceUri;
            _sessionResourceUri = null;
        }

        if (wsSignaling is not null)
        {
            await wsSignaling.DisposeAsync().ConfigureAwait(false);
        }

        if (sessionUri is not null)
        {
            await WebRtcEndpointResolver.TerminateWhepSessionAsync(
                sessionUri,
                _webrtcOptions,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (pc is not null)
        {
            try
            {
                pc.close();
            }
            catch
            {
                // Ignore errors closing peer connection
            }
        }

        _audioOutput.Reset();
        _framePool.StopAcceptingReturns();
    }

    private void OnAudioFrameReceived(EncodedAudioFrame frame)
    {
        if (_disposed || _state != MediaPlaybackState.Playing || !_options.Audio.IsEnabled)
        {
            return;
        }

        var format = frame.AudioFormat;
        var encoded = frame.EncodedAudio;
        if (encoded is null || encoded.Length == 0)
        {
            return;
        }

        if (format.Codec == AudioCodecsEnum.PCMA)
        {
            var sampleRate = format.ClockRate > 0 ? format.ClockRate : 8000;
            _audioOutput.EnsureFormat(sampleRate, 1);

            Span<short> pcm = stackalloc short[Math.Min(encoded.Length, 2048)];
            var remaining = encoded.AsSpan();
            while (!remaining.IsEmpty)
            {
                var chunk = Math.Min(remaining.Length, pcm.Length);
                G711Decoder.DecodeAlaw(remaining[..chunk], pcm[..chunk]);
                var pcmSpan = pcm[..chunk];
                _audioOutput.WriteSamples(pcmSpan);

                var audioSamplesReceived = AudioSamplesReceived;
                if (audioSamplesReceived is not null)
                {
                    audioSamplesReceived.Invoke(this, pcmSpan.ToArray());
                }

                remaining = remaining[chunk..];
            }
        }
        else if (format.Codec == AudioCodecsEnum.PCMU)
        {
            var sampleRate = format.ClockRate > 0 ? format.ClockRate : 8000;
            _audioOutput.EnsureFormat(sampleRate, 1);

            Span<short> pcm = stackalloc short[Math.Min(encoded.Length, 2048)];
            var remaining = encoded.AsSpan();
            while (!remaining.IsEmpty)
            {
                var chunk = Math.Min(remaining.Length, pcm.Length);
                G711Decoder.DecodeUlaw(remaining[..chunk], pcm[..chunk]);
                var pcmSpan = pcm[..chunk];
                _audioOutput.WriteSamples(pcmSpan);

                var audioSamplesReceived = AudioSamplesReceived;
                if (audioSamplesReceived is not null)
                {
                    audioSamplesReceived.Invoke(this, pcmSpan.ToArray());
                }

                remaining = remaining[chunk..];
            }
        }
    }

    private void SetState(MediaPlaybackState newState)
    {
        MediaPlaybackState oldState;
        lock (_sync)
        {
            oldState = _state;
            if (oldState == newState)
            {
                return;
            }

            _state = newState;
        }

        StateChanged?.Invoke(this, new MediaPlaybackStateChangedEventArgs(oldState, newState));
    }

    private void ReportError(MediaPlaybackError error)
    {
        if (!error.IsRecoverable)
        {
            SetState(MediaPlaybackState.Faulted);
        }

        Error?.Invoke(this, new MediaPlaybackErrorEventArgs(error));
    }
}
