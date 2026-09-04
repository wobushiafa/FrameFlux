using System.Net;
using System.Runtime.InteropServices;
using FrameFlux;
using FrameFlux.WebRtc;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace FrameFlux.WebRtc.Tests;

public sealed class WebRtcMediaPlayerTests
{
    [Fact]
    public async Task Factory_CreatesValidPlayer()
    {
        var factory = new WebRtcMediaPlayerFactory();
        await using var player = factory.Create();

        Assert.NotNull(player);
        Assert.IsType<WebRtcMediaPlayer>(player);
        Assert.NotNull(WebRtcMediaPlayerFactory.Instance);
    }

    [Fact]
    public async Task RealGo2Rtc_Test()
    {
        await using var player = new WebRtcMediaPlayer();
        var mockOutput = new MockMediaVideoOutput(MediaPixelFormat.Bgra32);
        player.VideoOutput = mockOutput;

        var receivedFrames = 0;
        var lastW = 0;
        var lastH = 0;
        var receivedAudioChunks = 0;
        var payloadLog = new List<string>();

        player.FrameReceived += (_, f) =>
        {
            Interlocked.Increment(ref receivedFrames);
            lastW = f.Width;
            lastH = f.Height;
        };

        player.AudioSamplesReceived += (_, _) =>
        {
            Interlocked.Increment(ref receivedAudioChunks);
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var source = MediaSource.Parse("http://diaofanle.com:52004/stream.html?src=cam_009_main");
        var options = new MediaOpenOptions
        {
            Video = new MediaVideoOptions
            {
                DecodingPolicy = MediaVideoDecodingPolicy.SoftwareOnly
            }
        };
        await player.OpenAsync(source, options);
        var openDuration = sw.ElapsedMilliseconds;

        if (player.PeerConnection != null)
        {
            player.PeerConnection.OnVideoFrameReceived += (ep, ts, payload, fmt) =>
            {
                if (payloadLog.Count < 20)
                {
                    lock (payloadLog)
                    {
                        if (payloadLog.Count < 20)
                        {
                            var hex = Convert.ToHexString(payload.Take(16).ToArray());
                            payloadLog.Add($"len={payload.Length}, ts={ts}, hex={hex}");
                        }
                    }
                }
            };
        }

        // Verify WebSocket signaling handshake completes in under 3 seconds (was 12+ seconds previously)
        Assert.True(openDuration < 3000, $"OpenAsync should complete rapidly, took {openDuration}ms");

        await player.PlayAsync();
        Assert.Equal(MediaPlaybackState.Playing, player.State);

        await Task.Delay(5000);

        await player.StopAsync();
        Assert.Equal(MediaPlaybackState.Stopped, player.State);

        Assert.True(mockOutput.PresentedFrameCount > 0);
        Assert.True(receivedFrames > 0);
        // Verify dimensions are 1920x1080 (not squashed or corrupted by YUVJ420P fallback)
        Assert.Equal(1920, lastW);
        Assert.Equal(1080, lastH);
        // Verify live audio frames received and decoded
        Assert.True(receivedAudioChunks > 0, $"Expected audio chunks, got {receivedAudioChunks}");
    }

    [Fact]
    public async Task RealGo2Rtc_HardwareRequired_D3D11_LiveTest()
    {
        await using var player = new WebRtcMediaPlayer();
        var mockOutput = new MockGpuVideoOutput(acceptFrames: true);
        player.VideoOutput = mockOutput;

        var source = MediaSource.Parse("http://diaofanle.com:52004/stream.html?src=cam_009_main");
        var options = new MediaOpenOptions
        {
            Video = new MediaVideoOptions
            {
                DecodingPolicy = MediaVideoDecodingPolicy.HardwareRequired
            }
        };

        await player.OpenAsync(source, options);
        await player.PlayAsync();

        await Task.Delay(5000);

        Assert.True(mockOutput.PresentedFrameCount > 0, $"Expected presented D3D11 frames > 0, got {mockOutput.PresentedFrameCount}");

        await player.StopAsync();
    }

    [Fact]
    public void RTCPFeedback_Nack_Serialization_Succeeds()
    {
        var nack = new RTCPFeedback(12345u, 67890u, RTCPFeedbackTypesEnum.NACK, (ushort)42, (ushort)0);
        var bytes = nack.GetBytes();
        Assert.NotNull(bytes);
        Assert.Equal(16, bytes.Length);
        Assert.Equal(0x81, bytes[0]); // Version 2, FMT=1
        Assert.Equal(205, bytes[1]);  // Payload Type 205 (RTPFB)
    }

    [Fact]
    public void WebRtcRtpLossDetector_SequentialPackets_NoNackGenerated()
    {
        var nacks = new List<RTCPFeedback>();
        var pliCount = 0;
        var detector = new WebRtcRtpLossDetector(nacks.Add, () => pliCount++);

        detector.ProcessRtpPacket(100, 200, 1);
        detector.ProcessRtpPacket(100, 200, 2);
        detector.ProcessRtpPacket(100, 200, 3);
        detector.ProcessRtpPacket(100, 200, 4);

        Assert.Empty(nacks);
        Assert.Equal(0, pliCount);
        Assert.Equal(4, detector.PacketCount);
        Assert.Equal(0, detector.LostPacketCount);
    }

    [Fact]
    public void WebRtcRtpLossDetector_GapDetected_GeneratesNack()
    {
        var nacks = new List<RTCPFeedback>();
        var pliCount = 0;
        var detector = new WebRtcRtpLossDetector(nacks.Add, () => pliCount++);

        detector.ProcessRtpPacket(100, 200, 10);
        // Gap: 11, 12, 13 are missing!
        detector.ProcessRtpPacket(100, 200, 14);

        Assert.Single(nacks);
        Assert.Equal(3, detector.LostPacketCount);
        Assert.Equal(0, pliCount);
    }

    [Fact]
    public void WebRtcRtpLossDetector_LargeGap_RequestsKeyFrameRateLimited()
    {
        var nacks = new List<RTCPFeedback>();
        var pliCount = 0;
        var detector = new WebRtcRtpLossDetector(nacks.Add, () => pliCount++)
        {
            MinPliIntervalMs = 50
        };

        detector.ProcessRtpPacket(100, 200, 10);
        // Huge gap of 15 missing packets:
        detector.ProcessRtpPacket(100, 200, 26);

        Assert.NotEmpty(nacks);
        Assert.Equal(1, pliCount);

        // Immediate subsequent call within interval is rate-limited
        detector.RequestKeyFrameRateLimited();
        Assert.Equal(1, pliCount);
    }

    [Fact]
    public void G711Decoder_DecodesAlawAndUlaw_Correctly()
    {
        byte[] alaw = [0x55, 0x00, 0xFF, 0x7F, 0xD5];
        Span<short> pcmA = stackalloc short[alaw.Length];
        G711Decoder.DecodeAlaw(alaw, pcmA);
        Assert.Equal(-8, pcmA[0]); // 0x55 in A-law represents the negative zero step (-8)
        Assert.Equal(8, pcmA[4]);  // 0xD5 in A-law represents the positive zero step (+8)

        byte[] ulaw = [0xFF, 0x7F, 0x00];
        Span<short> pcmU = stackalloc short[ulaw.Length];
        G711Decoder.DecodeUlaw(ulaw, pcmU);
        Assert.Equal(0, pcmU[0]); // 0xFF in mu-law represents 0
    }

    [Fact]
    public void AudioOutput_Lifecycle_Succeeds()
    {
        using var output = new WebRtcWaveOutAudioOutput();
        output.EnsureFormat(8000, 1);
        output.SetVolume(0.8, false);

        short[] samples = [0, 100, 200, 300, 0, -100, -200, -300];
        output.WriteSamples(samples);
        output.Pause();
        output.Resume();
        output.Reset();
    }

    [Fact]
    public async Task Test_KeyFrame_And_Sdp_Enhancement()
    {
        await using var player = new WebRtcMediaPlayer();
        var source = MediaSource.Parse("http://diaofanle.com:52004/stream.html?src=cam_009_main");
        await player.OpenAsync(source);
        Assert.Equal(MediaPlaybackState.Ready, player.State);

        await player.PlayAsync();
        Assert.Equal(MediaPlaybackState.Playing, player.State);

        player.RequestKeyFrame();
        await Task.Delay(1000);
        await player.StopAsync();
    }

    [Fact]
    public async Task Test_Go2Rtc_WebSocket_Signaling()
    {
        using var ws = new System.Net.WebSockets.ClientWebSocket();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await ws.ConnectAsync(new Uri("ws://diaofanle.com:52004/api/ws?src=cam_009_main"), cts.Token);
        Assert.Equal(System.Net.WebSockets.WebSocketState.Open, ws.State);
    }

    [Fact]
    public async Task Player_HasExpectedCapabilitiesAndRejectsUnsupportedControls()
    {
        await using var player = new WebRtcMediaPlayer();

        Assert.Equal(MediaPlaybackState.Idle, player.State);
        Assert.True(player.Capabilities.IsLive);
        Assert.False(player.Capabilities.CanPause);
        Assert.False(player.Capabilities.CanSeek);
        Assert.False(player.Capabilities.CanChangePlaybackRate);
        Assert.True(player.Capabilities.CanCaptureSnapshots);
        Assert.Null(player.Duration);
        Assert.Equal(1d, player.PlaybackRate);

        Assert.Throws<NotSupportedException>(() => player.PlaybackRate = 1.5d);
        player.PlaybackRate = 1.0d;
        Assert.Equal(1.0d, player.PlaybackRate);

        player.Volume = 0.75d;
        Assert.Equal(0.75d, player.Volume);
        player.Volume = 1.5d;
        Assert.Equal(1.0d, player.Volume);
        player.Volume = -0.5d;
        Assert.Equal(0.0d, player.Volume);

        player.IsMuted = true;
        Assert.True(player.IsMuted);

        await Assert.ThrowsAsync<NotSupportedException>(() => player.PauseAsync().AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(() => player.SeekAsync(TimeSpan.FromSeconds(5)).AsTask());
    }

    [Fact]
    public void EndpointResolver_ParsesVariousUriFormats()
    {
        // 1. webrtc://
        var webrtcSource = MediaSource.Parse("webrtc://live.camera.internal/live/cam1");
        var webrtcResolved = WebRtcEndpointResolver.Resolve(webrtcSource);
        Assert.Equal(WebRtcEndpointKind.Whep, webrtcResolved.Kind);
        Assert.NotNull(webrtcResolved.EndpointUri);
        Assert.Equal("http", webrtcResolved.EndpointUri.Scheme);
        Assert.Equal("live.camera.internal", webrtcResolved.EndpointUri.Host);

        // 2. HTTP WHEP
        var whepSource = MediaSource.Parse("http://127.0.0.1:8889/mystream/whep");
        var whepResolved = WebRtcEndpointResolver.Resolve(whepSource);
        Assert.Equal(WebRtcEndpointKind.Whep, whepResolved.Kind);
        Assert.Equal(whepSource.Uri, whepResolved.EndpointUri);

        // 3. HTTPS WHIP
        var whipSource = MediaSource.Parse("https://127.0.0.1:8889/mystream/whip");
        var whipResolved = WebRtcEndpointResolver.Resolve(whipSource);
        Assert.Equal(WebRtcEndpointKind.Whip, whipResolved.Kind);
        Assert.Equal(whipSource.Uri, whipResolved.EndpointUri);

        // 4. JSON Config Data URI
        var jsonConfig = "{\"sdp\":\"v=0\\r\\no=- 1 1 IN IP4 127.0.0.1\\r\\n\",\"type\":\"offer\",\"iceServers\":[{\"urls\":\"stun:custom.stun:3478\"}]}";
        var dataUriSource = WebRtcSource.FromJson(jsonConfig);
        var dataResolved = WebRtcEndpointResolver.Resolve(dataUriSource);
        Assert.Equal(WebRtcEndpointKind.JsonConfig, dataResolved.Kind);
        Assert.Contains("v=0", dataResolved.RawSdp);
        Assert.Contains(dataResolved.IceServers, s => s.urls == "stun:custom.stun:3478");

        // 5. Raw SDP string via WebRtcSource
        var rawSdpSource = WebRtcSource.FromSdp("v=0\r\no=- 2 2 IN IP4 127.0.0.1\r\ns=Test\r\nt=0 0\r\n");
        var sdpResolved = WebRtcEndpointResolver.Resolve(rawSdpSource);
        Assert.Equal(WebRtcEndpointKind.DirectSdp, sdpResolved.Kind);
        Assert.Contains("v=0", sdpResolved.RawSdp);

        // 6. go2rtc web page -> WebSocket
        var go2rtcWeb = MediaSource.Parse("http://diaofanle.com:52004/stream.html?src=cam_009_main");
        var go2rtcResolved = WebRtcEndpointResolver.Resolve(go2rtcWeb);
        Assert.Equal(WebRtcEndpointKind.Go2RtcWebSocket, go2rtcResolved.Kind);
        Assert.Equal("ws", go2rtcResolved.EndpointUri!.Scheme);
        Assert.Equal("/api/ws", go2rtcResolved.EndpointUri.AbsolutePath);

        // 7. Direct ws:// scheme
        var directWs = MediaSource.Parse("ws://127.0.0.1:1984/api/ws?src=test");
        var directWsResolved = WebRtcEndpointResolver.Resolve(directWs);
        Assert.Equal(WebRtcEndpointKind.Go2RtcWebSocket, directWsResolved.Kind);
    }

    [Fact]
    public void FrameBufferPool_RecyclesUnmanagedMemoryWithoutLeaks()
    {
        using var pool = new WebRtcFrameBufferPool(maximumBufferCount: 4, maximumRetainedBytes: 1024 * 1024);

        var size = 4096;
        var buf1 = pool.Rent(size);
        Assert.NotEqual(IntPtr.Zero, buf1);
        Assert.Equal(0, pool.RetainedBufferCount);

        pool.Return(buf1, size);
        Assert.Equal(1, pool.RetainedBufferCount);
        Assert.Equal(size, pool.RetainedBytes);

        var buf2 = pool.Rent(size);
        Assert.Equal(buf1, buf2); // Reused same buffer
        Assert.Equal(0, pool.RetainedBufferCount);

        pool.Return(buf2, size);
        Assert.Equal(1, pool.RetainedBufferCount);
    }

    [Fact]
    public void MediaFrameLease_ExposesCorrectPlanesAndDisposesBackToPool()
    {
        var returned = false;
        var size = 1920 * 1080 * 4;
        var rawMem = Marshal.AllocHGlobal(size);

        try
        {
            var lease = new WebRtcMediaFrameLease(rawMem, size, l =>
            {
                returned = true;
            });

            lease.ResetBgra(1920, 1080, 1920 * 4);
            Assert.Equal(MediaPixelFormat.Bgra32, lease.PixelFormat);
            Assert.Equal(MediaFrameStorageKind.CpuMemory, lease.StorageKind);
            Assert.True(lease.TryGetCpuBuffer(out var cpuBuf));
            Assert.Equal(rawMem, cpuBuf.Plane0);
            Assert.Equal(1920 * 4, cpuBuf.Plane0Stride);
            Assert.Equal(IntPtr.Zero, cpuBuf.Plane1);

            lease.Dispose();
            Assert.True(returned);
            Assert.True(lease.IsDisposed);
            Assert.False(lease.TryGetCpuBuffer(out _));
        }
        finally
        {
            Marshal.FreeHGlobal(rawMem);
        }
    }

    [Fact]
    public void PixelConverter_ConvertsYuv420PToBgraCorrectly()
    {
        var width = 4;
        var height = 4;
        var yStride = 4;
        var uvStride = 2;
        var bgraStride = width * 4;

        var ySize = yStride * height;
        var uvSize = uvStride * (height / 2);
        var yuvSize = ySize + uvSize * 2;
        var bgraSize = bgraStride * height;

        var yuvMem = Marshal.AllocHGlobal(yuvSize);
        var bgraMem = Marshal.AllocHGlobal(bgraSize);

        try
        {
            // Fill Y with 255 (white), U and V with 128 (neutral)
            unsafe
            {
                var ptr = (byte*)yuvMem;
                for (var i = 0; i < ySize; i++) ptr[i] = 235; // Studio white
                for (var i = ySize; i < yuvSize; i++) ptr[i] = 128;
            }

            var uPlane = yuvMem + ySize;
            var vPlane = yuvMem + ySize + uvSize;

            WebRtcPixelConverter.Yuv420PToBgra32(
                yuvMem, yStride,
                uPlane, uvStride,
                vPlane, uvStride,
                bgraMem, width, height, bgraStride);

            unsafe
            {
                var bgra = (byte*)bgraMem;
                // B, G, R, A
                Assert.True(bgra[0] >= 200); // Blue
                Assert.True(bgra[1] >= 200); // Green
                Assert.True(bgra[2] >= 200); // Red
                Assert.Equal(255, bgra[3]);  // Alpha
            }
        }
        finally
        {
            Marshal.FreeHGlobal(yuvMem);
            Marshal.FreeHGlobal(bgraMem);
        }
    }

    [Fact]
    public async Task Player_DeliversBgraFrameDirectlyToVideoOutput()
    {
        await using var player = new WebRtcMediaPlayer();
        var mockOutput = new MockMediaVideoOutput(supportedFormat: MediaPixelFormat.Bgra32);
        player.VideoOutput = mockOutput;

        var states = new List<MediaPlaybackState>();
        player.StateChanged += (_, e) => states.Add(e.NewState);

        MediaVideoFrame? receivedFrame = null;
        player.FrameReceived += (_, f) => receivedFrame = f;

        // Open with local SDP description
        var sdp = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=Test\r\nt=0 0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=rtpmap:96 H264/90000\r\na=sendonly\r\n";
        await player.OpenAsync(WebRtcSource.FromSdp(sdp), new MediaOpenOptions
        {
            Video = new MediaVideoOptions
            {
                SnapshotPolicy = MediaSnapshotPolicy.KeepLatestFrame
            }
        });

        Assert.Equal(MediaPlaybackState.Ready, player.State);

        await player.PlayAsync();
        Assert.Equal(MediaPlaybackState.Playing, player.State);

        // Manually feed a BGRA frame
        var width = 64;
        var height = 64;
        var stride = width * 4;
        var size = stride * height;
        var buffer = player.FrameBufferPool.Rent(size);
        var lease = new WebRtcMediaFrameLease(buffer, size, l => player.FrameBufferPool.Return(l.Buffer, l.Size));
        lease.ResetBgra(width, height, stride);

        player.DeliverFrame(lease);

        Assert.Equal(1, mockOutput.PresentedFrameCount);
        Assert.NotNull(receivedFrame);
        Assert.Equal(MediaPixelFormat.Bgra32, receivedFrame.PixelFormat);
        Assert.Equal(width, receivedFrame.Width);

        // Snapshot capture verification
        var snapshot = await player.CaptureSnapshotAsync();
        Assert.NotNull(snapshot);
        Assert.Equal(width, snapshot.Width);

        await player.StopAsync();
        Assert.Equal(MediaPlaybackState.Stopped, player.State);
    }

    [Fact]
    public async Task Player_AutoConvertsYuv420PToBgraWhenOutputOnlySupportsBgra()
    {
        await using var player = new WebRtcMediaPlayer();
        var mockOutput = new MockMediaVideoOutput(supportedFormat: MediaPixelFormat.Bgra32);
        player.VideoOutput = mockOutput;

        var sdp = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=Test\r\nt=0 0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=rtpmap:96 H264/90000\r\na=sendonly\r\n";
        await player.OpenAsync(WebRtcSource.FromSdp(sdp));
        await player.PlayAsync();

        // Feed YUV420P frame
        var width = 32;
        var height = 32;
        var yStride = 32;
        var uvStride = 16;
        var totalSize = width * height * 3 / 2;

        var buffer = player.FrameBufferPool.Rent(totalSize);
        var lease = new WebRtcMediaFrameLease(buffer, totalSize, l => player.FrameBufferPool.Return(l.Buffer, l.Size));
        lease.ResetYuv420P(width, height, yStride, uvStride);

        player.DeliverFrame(lease);

        // Output should have received a converted BGRA frame!
        Assert.Equal(1, mockOutput.PresentedFrameCount);
        Assert.Equal(MediaPixelFormat.Bgra32, mockOutput.LastPresentedFormat);
    }

    [Fact]
    public async Task VideoSink_DeliversSamplesViaDirectApi()
    {
        await using var player = new WebRtcMediaPlayer();
        var mockOutput = new MockMediaVideoOutput(supportedFormat: MediaPixelFormat.Bgra32);
        player.VideoOutput = mockOutput;

        var sdp = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=Test\r\nt=0 0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=rtpmap:96 H264/90000\r\na=sendonly\r\n";
        await player.OpenAsync(WebRtcSource.FromSdp(sdp));
        await player.PlayAsync();

        var sample = new byte[64 * 64 * 4];
        player.VideoSink.DeliverDecodedSample(sample, 64, 64, 64 * 4, VideoPixelFormatsEnum.Bgra);

        Assert.Equal(1, mockOutput.PresentedFrameCount);
    }

    [Fact]
    public async Task Player_DisposesFrameWhenOutputRejectsOrMissing()
    {
        await using var player = new WebRtcMediaPlayer();
        var mockOutput = new MockMediaVideoOutput(supportedFormat: MediaPixelFormat.Bgra32, acceptFrames: false);
        player.VideoOutput = mockOutput;

        var sdp = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=Test\r\nt=0 0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=rtpmap:96 H264/90000\r\na=sendonly\r\n";
        await player.OpenAsync(WebRtcSource.FromSdp(sdp));
        await player.PlayAsync();

        var leaseDisposed = false;
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            var lease = new WebRtcMediaFrameLease(buffer, 256, _ => leaseDisposed = true);
            lease.ResetBgra(8, 8, 32);

            player.DeliverFrame(lease);

            // Mock rejected it, player must have disposed the lease!
            Assert.True(leaseDisposed);
            Assert.Equal(1, mockOutput.PresentedFrameCount);

            // Now test when VideoOutput is null
            player.VideoOutput = null;
            var leaseDisposed2 = false;
            var lease2 = new WebRtcMediaFrameLease(buffer, 256, _ => leaseDisposed2 = true);
            lease2.ResetBgra(8, 8, 32);

            player.DeliverFrame(lease2);
            Assert.True(leaseDisposed2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void PixelConverter_ConvertsNv12ToBgraCorrectly()
    {
        var width = 4;
        var height = 4;
        var yStride = 4;
        var uvStride = 4;
        var bgraStride = width * 4;

        var ySize = yStride * height;
        var uvSize = uvStride * (height / 2);
        var totalSize = ySize + uvSize;
        var bgraSize = bgraStride * height;

        var nv12Mem = Marshal.AllocHGlobal(totalSize);
        var bgraMem = Marshal.AllocHGlobal(bgraSize);

        try
        {
            unsafe
            {
                var ptr = (byte*)nv12Mem;
                for (var i = 0; i < ySize; i++) ptr[i] = 235; // Studio white
                for (var i = ySize; i < totalSize; i++) ptr[i] = 128; // U and V neutral
            }

            var uvPlane = nv12Mem + ySize;

            WebRtcPixelConverter.Nv12ToBgra32(
                nv12Mem, yStride,
                uvPlane, uvStride,
                bgraMem, width, height, bgraStride);

            unsafe
            {
                var bgra = (byte*)bgraMem;
                Assert.True(bgra[0] >= 200); // Blue
                Assert.True(bgra[1] >= 200); // Green
                Assert.True(bgra[2] >= 200); // Red
                Assert.Equal(255, bgra[3]);  // Alpha
            }
        }
        finally
        {
            Marshal.FreeHGlobal(nv12Mem);
            Marshal.FreeHGlobal(bgraMem);
        }
    }

    [Fact]
    public void PixelConverter_ConvertsRgbAndRgbaToBgraCorrectly()
    {
        var width = 2;
        var height = 2;
        var rgbMem = Marshal.AllocHGlobal(width * height * 3);
        var rgbaMem = Marshal.AllocHGlobal(width * height * 4);
        var bgraMem = Marshal.AllocHGlobal(width * height * 4);

        try
        {
            unsafe
            {
                var rgb = (byte*)rgbMem;
                // Red pixel: R=255, G=0, B=0
                rgb[0] = 255; rgb[1] = 0; rgb[2] = 0;

                var rgba = (byte*)rgbaMem;
                // Green pixel: R=0, G=255, B=0, A=255
                rgba[0] = 0; rgba[1] = 255; rgba[2] = 0; rgba[3] = 255;
            }

            // Test RGB24 -> BGRA32
            WebRtcPixelConverter.Rgb24ToBgra32(rgbMem, width * 3, bgraMem, width, height, width * 4);
            unsafe
            {
                var bgra = (byte*)bgraMem;
                Assert.Equal(0, bgra[0]);   // Blue
                Assert.Equal(0, bgra[1]);   // Green
                Assert.Equal(255, bgra[2]); // Red
                Assert.Equal(255, bgra[3]); // Alpha
            }

            // Test RGBA32 -> BGRA32
            WebRtcPixelConverter.Rgba32ToBgra32(rgbaMem, width * 4, bgraMem, width, height, width * 4);
            unsafe
            {
                var bgra = (byte*)bgraMem;
                Assert.Equal(0, bgra[0]);   // Blue
                Assert.Equal(255, bgra[1]); // Green
                Assert.Equal(0, bgra[2]);   // Red
                Assert.Equal(255, bgra[3]); // Alpha
            }
        }
        finally
        {
            Marshal.FreeHGlobal(rgbMem);
            Marshal.FreeHGlobal(rgbaMem);
            Marshal.FreeHGlobal(bgraMem);
        }
    }

    [Fact]
    public void DefaultWebRtcVideoDecoder_ChecksFormats()
    {
        using var decoder = new DefaultWebRtcVideoDecoder();
        Assert.True(decoder.CanDecode(new VideoFormat(VideoCodecsEnum.JPEG, 26)));
        Assert.False(decoder.CanDecode(new VideoFormat(VideoCodecsEnum.H264, 96)));
        Assert.False(decoder.CanDecode(new VideoFormat(VideoCodecsEnum.VP8, 97)));

        using var pool = new WebRtcFrameBufferPool();
        var emptyResult = decoder.TryDecode(ReadOnlySpan<byte>.Empty, new VideoFormat(VideoCodecsEnum.JPEG, 26), pool, out _);
        Assert.False(emptyResult);
    }

    [Fact]
    public void FfmpegWebRtcVideoDecoder_DecodingPolicy_Respected()
    {
        if (!FfmpegWebRtcVideoDecoder.IsSupported) return;

        using var decoder = new FfmpegWebRtcVideoDecoder
        {
            DecodingPolicy = MediaVideoDecodingPolicy.SoftwareOnly
        };
        Assert.Equal(MediaVideoDecodingPolicy.SoftwareOnly, decoder.DecodingPolicy);
        Assert.False(decoder.IsHardwareAccelerated);

        decoder.DecodingPolicy = MediaVideoDecodingPolicy.HardwarePreferred;
        Assert.Equal(MediaVideoDecodingPolicy.HardwarePreferred, decoder.DecodingPolicy);
    }

    [Fact]
    public void WebRtcPixelConverter_Guarantees_Opaque_Alpha()
    {
        var width = 64;
        var height = 64;
        var yuvSize = width * height * 2;
        var bgraSize = width * height * 4;

        var yuvMem = Marshal.AllocHGlobal(yuvSize);
        var bgraMem = Marshal.AllocHGlobal(bgraSize);
        try
        {
            // Zero out memory (simulating dirty/blank frame)
            unsafe
            {
                new Span<byte>((void*)yuvMem, yuvSize).Clear();
                new Span<byte>((void*)bgraMem, bgraSize).Clear();
            }

            var yPlane = yuvMem;
            var uPlane = yuvMem + (width * height);
            var vPlane = uPlane + (width * height / 4);

            WebRtcPixelConverter.Yuv420PToBgra32(
                yPlane, width,
                uPlane, width / 2,
                vPlane, width / 2,
                bgraMem, width, height, width * 4,
                isFullRange: true);

            unsafe
            {
                var p = (byte*)bgraMem;
                for (var i = 0; i < width * height; i++)
                {
                    Assert.Equal(255, p[(i * 4) + 3]); // Alpha must ALWAYS be 255 (opaque, no white pollution)
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(yuvMem);
            Marshal.FreeHGlobal(bgraMem);
        }
    }

    [Fact]
    public async Task WebRtcMediaPlayer_VideoOutput_Synchronizes_CanOutputD3D11Texture()
    {
        await using var player = new WebRtcMediaPlayer();
        var mockGpuOutput = new MockGpuVideoOutput(acceptFrames: true);

        // 1. When GPU output attached, decoder preferences should enable D3D11 texture output
        player.VideoOutput = mockGpuOutput;
        var decoder = player.GetType().GetField("_decoder", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(player) as IWebRtcVideoDecoder;
        Assert.NotNull(decoder);
        Assert.True(decoder.CanOutputD3D11Texture);

        // 2. When software output attached, decoder preferences should disable D3D11 texture output
        var mockCpuOutput = new MockMediaVideoOutput(MediaPixelFormat.Bgra32);
        player.VideoOutput = mockCpuOutput;
        Assert.False(decoder.CanOutputD3D11Texture);
    }

    [Fact]
    public async Task WebRtcMediaPlayer_D3D11Texture_DeliverFrame_PresentsDirectly_Or_FallsBack()
    {
        await using var player = new WebRtcMediaPlayer();
        var mockGpuOutput = new MockGpuVideoOutput(acceptFrames: true);
        player.VideoOutput = mockGpuOutput;

        var decoder = player.GetType().GetField("_decoder", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(player) as IWebRtcVideoDecoder;
        Assert.NotNull(decoder);

        // Fake playing state
        player.GetType().GetField("_state", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(player, MediaPlaybackState.Playing);

        // Deliver D3D11 frame to GPU output
        var lease = new WebRtcMediaFrameLease(IntPtr.Zero, 0, _ => { });
        lease.ResetD3D11(1920, 1080, new IntPtr(0x1234), 0);

        player.DeliverFrame(lease);
        Assert.Equal(1, mockGpuOutput.PresentedFrameCount);
        Assert.True(decoder.CanOutputD3D11Texture);

        // Now test fallback when GPU presentation rejects/fails
        var rejectingGpuOutput = new MockGpuVideoOutput(acceptFrames: false);
        player.VideoOutput = rejectingGpuOutput;
        decoder.CanOutputD3D11Texture = true;

        var lease2 = new WebRtcMediaFrameLease(IntPtr.Zero, 0, _ => { });
        lease2.ResetD3D11(1920, 1080, new IntPtr(0x5678), 0);
        player.DeliverFrame(lease2);

        // Presentation rejected -> triggers fallback: CanOutputD3D11Texture set to false
        Assert.False(decoder.CanOutputD3D11Texture);
    }

    [Fact]
    public void FfmpegWebRtcVideoDecoder_HardwareRequired_EnsureCodecContext_H265()
    {
        var decoder = new FfmpegWebRtcVideoDecoder
        {
            DecodingPolicy = MediaVideoDecodingPolicy.HardwareRequired,
            CanOutputD3D11Texture = true
        };

        var method = decoder.GetType().GetMethod("EnsureCodecContext", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        var result = (bool)method.Invoke(decoder, new object[] { VideoCodecsEnum.H265 })!;
        Assert.True(result);
        Assert.True(decoder.IsHardwareAccelerated);
    }

    private sealed class MockGpuVideoOutput : IMediaVideoOutput
    {
        private readonly bool _acceptFrames;

        public MockGpuVideoOutput(bool acceptFrames = true)
        {
            _acceptFrames = acceptFrames;
        }

        public MediaFrameStorageKind PreferredFrameStorage => MediaFrameStorageKind.D3D11Texture;

        public int PresentedFrameCount { get; private set; }

        public bool Supports(MediaFrameStorageKind storageKind, MediaPixelFormat pixelFormat)
        {
            return storageKind == MediaFrameStorageKind.D3D11Texture;
        }

        public bool TryPresent(IMediaFrameLease frame)
        {
            PresentedFrameCount++;
            if (_acceptFrames)
            {
                frame.Dispose();
                return true;
            }
            return false;
        }
    }

    private sealed class MockMediaVideoOutput : IMediaVideoOutput
    {
        private readonly MediaPixelFormat _supportedFormat;
        private readonly bool _acceptFrames;

        public MockMediaVideoOutput(MediaPixelFormat supportedFormat, bool acceptFrames = true)
        {
            _supportedFormat = supportedFormat;
            _acceptFrames = acceptFrames;
        }

        public MediaFrameStorageKind PreferredFrameStorage => MediaFrameStorageKind.CpuMemory;

        public int PresentedFrameCount { get; private set; }

        public MediaPixelFormat LastPresentedFormat { get; private set; }

        public bool Supports(MediaFrameStorageKind storageKind, MediaPixelFormat pixelFormat)
        {
            return storageKind == MediaFrameStorageKind.CpuMemory && pixelFormat == _supportedFormat;
        }

        public bool TryPresent(IMediaFrameLease frame)
        {
            PresentedFrameCount++;
            LastPresentedFormat = frame.PixelFormat;
            if (_acceptFrames)
            {
                frame.Dispose(); // Mock accepts ownership and disposes
                return true;
            }

            return false; // Rejects ownership
        }
    }
}
