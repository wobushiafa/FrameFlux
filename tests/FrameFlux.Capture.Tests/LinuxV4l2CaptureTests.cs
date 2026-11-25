using FrameFlux.Capture;
using Xunit;

namespace FrameFlux.Capture.Tests;

public sealed class LinuxV4l2CaptureTests
{
    [Fact]
    public unsafe void V4l2InteropStructs_MatchLinuxLp64AbiSizes()
    {
        Assert.Equal(104, sizeof(LinuxV4l2Interop.V4l2Capability));
        Assert.Equal(64, sizeof(LinuxV4l2Interop.V4l2FmtDesc));
        Assert.Equal(44, sizeof(LinuxV4l2Interop.V4l2FrmSizeEnum));
        Assert.Equal(8, sizeof(LinuxV4l2Interop.V4l2Fract));
        Assert.Equal(52, sizeof(LinuxV4l2Interop.V4l2FrmIvalEnum));
        Assert.Equal(208, sizeof(LinuxV4l2Interop.V4l2Format));
        Assert.Equal(204, sizeof(LinuxV4l2Interop.V4l2StreamParm));
        Assert.Equal(20, sizeof(LinuxV4l2Interop.V4l2RequestBuffers));
        Assert.Equal(88, sizeof(LinuxV4l2Interop.V4l2Buffer));
        Assert.Equal(64, sizeof(LinuxV4l2Interop.V4l2ExportBuffer));
        Assert.Equal(8, sizeof(LinuxV4l2Interop.PollFd));
    }

    [Fact]
    public void IsVideoCaptureDevice_FiltersMetadataAndM2MNodesUsingDeviceCaps()
    {
        const uint capAndStream = LinuxV4l2Interop.V4L2_CAP_VIDEO_CAPTURE | LinuxV4l2Interop.V4L2_CAP_STREAMING;
        const uint metaAndStream = LinuxV4l2Interop.V4L2_CAP_META_CAPTURE | LinuxV4l2Interop.V4L2_CAP_STREAMING;
        const uint allCaps = capAndStream | metaAndStream | LinuxV4l2Interop.V4L2_CAP_DEVICE_CAPS;

        // Typical UVC camera video node: capabilities has both VIDEO_CAPTURE & META_CAPTURE, device_caps only has VIDEO_CAPTURE.
        Assert.True(LinuxV4l2Interop.IsVideoCaptureDevice(allCaps, capAndStream));

        // Typical UVC metadata companion node (/dev/video1): device_caps has META_CAPTURE -> must be excluded.
        Assert.False(LinuxV4l2Interop.IsVideoCaptureDevice(allCaps, metaAndStream));

        // Hardware M2M codec node -> must be excluded.
        Assert.False(LinuxV4l2Interop.IsVideoCaptureDevice(
            allCaps,
            capAndStream | LinuxV4l2Interop.V4L2_CAP_VIDEO_M2M));

        // Legacy driver without V4L2_CAP_DEVICE_CAPS flag falls back to capabilities.
        Assert.True(LinuxV4l2Interop.IsVideoCaptureDevice(capAndStream, 0));
        Assert.False(LinuxV4l2Interop.IsVideoCaptureDevice(LinuxV4l2Interop.V4L2_CAP_VIDEO_CAPTURE, 0));
    }

    [Fact]
    public void FourCcMappingAndColorSpaceResolution_HandleCommonUvcFormats()
    {
        Assert.Equal(MediaCapturePixelFormat.Nv12, LinuxV4l2Interop.MapFourCcToPixelFormat(LinuxV4l2Interop.V4L2_PIX_FMT_NV12));
        Assert.Equal(MediaCapturePixelFormat.Yuy2, LinuxV4l2Interop.MapFourCcToPixelFormat(LinuxV4l2Interop.V4L2_PIX_FMT_YUYV));
        Assert.Equal(MediaCapturePixelFormat.Mjpeg, LinuxV4l2Interop.MapFourCcToPixelFormat(LinuxV4l2Interop.V4L2_PIX_FMT_MJPEG));
        Assert.Equal(MediaCapturePixelFormat.Yuv420P, LinuxV4l2Interop.MapFourCcToPixelFormat(LinuxV4l2Interop.V4L2_PIX_FMT_YUV420));
        Assert.Equal("MJPG", LinuxV4l2Interop.FormatFourCc(LinuxV4l2Interop.V4L2_PIX_FMT_MJPEG));

        Assert.Equal(
            CaptureYuvColorSpace.Bt601Full,
            LinuxV4l2Interop.ResolveColorSpace(
                LinuxV4l2Interop.V4L2_COLORSPACE_JPEG,
                LinuxV4l2Interop.V4L2_YCBCR_ENC_DEFAULT,
                LinuxV4l2Interop.V4L2_QUANTIZATION_DEFAULT,
                1920,
                1080,
                MediaCapturePixelFormat.Mjpeg));

        Assert.Equal(
            CaptureYuvColorSpace.Bt709Limited,
            LinuxV4l2Interop.ResolveColorSpace(
                LinuxV4l2Interop.V4L2_COLORSPACE_REC709,
                LinuxV4l2Interop.V4L2_YCBCR_ENC_709,
                LinuxV4l2Interop.V4L2_QUANTIZATION_LIM_RANGE,
                1920,
                1080,
                MediaCapturePixelFormat.Nv12));
    }

    [Fact]
    public void StableDeviceIdAndVideoNodeParsing_PreferByIdThenByPathThenNode()
    {
        Assert.Equal(0, LinuxV4l2DeviceEnumerator.TryParseVideoNodeNumber("video0"));
        Assert.Equal(12, LinuxV4l2DeviceEnumerator.TryParseVideoNodeNumber("video12"));
        Assert.Null(LinuxV4l2DeviceEnumerator.TryParseVideoNodeNumber("video"));
        Assert.Null(LinuxV4l2DeviceEnumerator.TryParseVideoNodeNumber("media0"));

        var byId = new Dictionary<string, string> { ["/dev/video0"] = "/dev/v4l/by-id/usb-Realtek_USB_Camera-video-index0" };
        var byPath = new Dictionary<string, string>
        {
            ["/dev/video0"] = "/dev/v4l/by-path/pci-0000:00:14.0-usb-0:2:1.0-video-index0",
            ["/dev/video2"] = "/dev/v4l/by-path/pci-0000:00:14.0-usb-0:3:1.0-video-index0"
        };

        Assert.Equal("/dev/v4l/by-id/usb-Realtek_USB_Camera-video-index0", LinuxV4l2DeviceEnumerator.SelectStableDeviceId("/dev/video0", byId, byPath));
        Assert.Equal("/dev/v4l/by-path/pci-0000:00:14.0-usb-0:3:1.0-video-index0", LinuxV4l2DeviceEnumerator.SelectStableDeviceId("/dev/video2", byId, byPath));
        Assert.Equal("/dev/video4", LinuxV4l2DeviceEnumerator.SelectStableDeviceId("/dev/video4", byId, byPath));
    }

    [Fact]
    public void AlsaAudioPairing_PrefersExactUsbPathThenVidPidAndAvoidsGenericCameraNameFalseMatch()
    {
        var candidates = new List<LinuxAudioEndpointCandidate>
        {
            new("plughw:CARD=MicA,DEV=0", "USB Capture Card A", "/sys/devices/pci0/usb1/1-2", "vid_1234&pid_5678", "HDMI Capture"),
            new("plughw:CARD=MicB,DEV=0", "USB Capture Card B", "/sys/devices/pci0/usb1/1-3", "vid_1234&pid_5678", "HDMI Capture"),
            new("plughw:CARD=DeskMic,DEV=0", "USB Camera Mic", "/sys/devices/pci0/usb1/1-9", "vid_9999&pid_0001", "USB Camera")
        };

        // Two identical VID/PID capture cards on different USB ports -> exact sysfs path disambiguates them.
        Assert.Equal(
            "plughw:CARD=MicB,DEV=0",
            LinuxAlsaAudioBridge.MatchPairedAudioDevice("/sys/devices/pci0/usb1/1-3", "vid_1234&pid_5678", "HDMI Capture", candidates));

        // Silent camera with generic "USB Camera" name must NOT accidentally pair with an unrelated mic.
        Assert.Null(
            LinuxAlsaAudioBridge.MatchPairedAudioDevice("/sys/devices/pci0/usb1/1-5", "vid_0bda&pid_5846", "USB Camera", candidates));
    }

    [Fact]
    public unsafe void AlsaGainScaling_MutesScalesAndClampsS16Samples()
    {
        var samples = new short[] { 1000, -1000, 25000, -25000 };
        fixed (short* ptr = samples)
        {
            LinuxAlsaAudioBridge.ApplyGainS16(ptr, samples.Length, 2.0d);
            Assert.Equal(2000, samples[0]);
            Assert.Equal(-2000, samples[1]);
            Assert.Equal(short.MaxValue, samples[2]);
            Assert.Equal(short.MinValue, samples[3]);

            LinuxAlsaAudioBridge.ApplyGainS16(ptr, samples.Length, 0.0d);
            Assert.All(samples, s => Assert.Equal(0, s));
        }
    }

    [Fact]
    public async Task CaptureMediaPlayer_DeliversDmaBufLeaseAndFallsBackToCpuOnPresentationFailure()
    {
        using var pool = new CaptureSessionPool();
        DmaBufTestBackend? backend = null;
        await using var player = new CaptureMediaPlayer(
            new CapturePlayerOptions(),
            pool,
            bufferPool => backend = new DmaBufTestBackend(bufferPool),
            (_, _, _, _) => null);

        var output = new AdaptiveDmaBufVideoOutput();
        player.VideoOutput = output;

        await player.OpenAsync(CaptureSource.FromDeviceIndex(0));
        await player.PlayAsync();

        // Frame 1: DMA-BUF accepted -> zero CPU conversion requested.
        backend!.EmitFrame(64, 48);
        Assert.Equal(1, output.DmaBufPresented);
        Assert.Equal(0, output.CpuPresented);
        Assert.Equal(1, backend.DmaBufReleaseCount);
        Assert.False(backend.LastNeedsCpuFrame);

        // Frame 2: DMA-BUF rejected -> falls back cleanly, subsequent frame requests CPU fallback.
        output.FailDmaBuf = true;
        backend.EmitFrame(64, 48);
        Assert.Equal(1, output.DmaBufPresented);
        Assert.Equal(2, backend.DmaBufReleaseCount);

        backend.EmitFrame(64, 48);
        Assert.True(backend.LastNeedsCpuFrame);
        Assert.Equal(1, output.CpuPresented);
    }

    [Fact]
    public unsafe void LinuxTurboJpegDecoder_OnLinux_DecodesRealJpegToBgra32()
    {
        if (!OperatingSystem.IsLinux() || !LinuxTurboJpegDecoder.IsAvailable)
        {
            return;
        }

        // Minimal 4x4 solid red (R=255, G=0, B=0) baseline JPEG generated by libjpeg-turbo.
        ReadOnlySpan<byte> red4x4Jpeg =
        [
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01,
            0x00, 0x01, 0x00, 0x00, 0xFF, 0xDB, 0x00, 0x43, 0x00, 0x02, 0x01, 0x01, 0x01, 0x01, 0x01, 0x02,
            0x01, 0x01, 0x01, 0x02, 0x02, 0x02, 0x02, 0x02, 0x04, 0x03, 0x02, 0x02, 0x02, 0x02, 0x05, 0x04,
            0x04, 0x03, 0x04, 0x06, 0x05, 0x06, 0x06, 0x06, 0x05, 0x06, 0x06, 0x06, 0x07, 0x09, 0x08, 0x06,
            0x07, 0x09, 0x07, 0x06, 0x06, 0x08, 0x0B, 0x08, 0x09, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x06, 0x08,
            0x0B, 0x0C, 0x0B, 0x0A, 0x0C, 0x09, 0x0A, 0x0A, 0x0A, 0xFF, 0xDB, 0x00, 0x43, 0x01, 0x02, 0x02,
            0x02, 0x02, 0x02, 0x02, 0x05, 0x03, 0x03, 0x05, 0x0A, 0x07, 0x06, 0x07, 0x0A, 0x0A, 0x0A, 0x0A,
            0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A,
            0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A,
            0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0xFF, 0xC0,
            0x00, 0x11, 0x08, 0x00, 0x04, 0x00, 0x04, 0x03, 0x01, 0x11, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11,
            0x01, 0xFF, 0xC4, 0x00, 0x1F, 0x00, 0x00, 0x01, 0x05, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09,
            0x0A, 0x0B, 0xFF, 0xC4, 0x00, 0xB5, 0x10, 0x00, 0x02, 0x01, 0x03, 0x03, 0x02, 0x04, 0x03, 0x05,
            0x05, 0x04, 0x04, 0x00, 0x00, 0x01, 0x7D, 0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21,
            0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07, 0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08, 0x23,
            0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0, 0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16, 0x17,
            0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A,
            0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A,
            0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A,
            0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99,
            0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7,
            0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5,
            0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1,
            0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0xFA, 0xFF, 0xC4, 0x00, 0x1F, 0x01, 0x00, 0x03,
            0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,
            0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0xFF, 0xC4, 0x00, 0xB5, 0x11, 0x00,
            0x02, 0x01, 0x02, 0x04, 0x04, 0x03, 0x04, 0x07, 0x05, 0x04, 0x04, 0x00, 0x01, 0x02, 0x77, 0x00,
            0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71, 0x13,
            0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0, 0x15,
            0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34, 0xE1, 0x25, 0xF1, 0x17, 0x18, 0x19, 0x1A, 0x26, 0x27,
            0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
            0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
            0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88,
            0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6,
            0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4,
            0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE2,
            0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9,
            0xFA, 0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00, 0xF8,
            0xBE, 0xBF, 0x94, 0xCF, 0xF7, 0xF0, 0xFF, 0xD9
        ];

        using var decoder = LinuxTurboJpegDecoder.TryCreate();
        Assert.NotNull(decoder);

        var bgra = new byte[4 * 4 * 4];
        fixed (byte* srcPtr = red4x4Jpeg)
        fixed (byte* dstPtr = bgra)
        {
            var ok = decoder!.TryDecodeToBgra((IntPtr)srcPtr, red4x4Jpeg.Length, (IntPtr)dstPtr, 4, 4, 16, out var error);
            Assert.True(ok, error);
        }

        // BGRA pixel 0 should be solid red (B~0, G~0, R~255, A=255)
        Assert.True(bgra[0] <= 5);
        Assert.True(bgra[1] <= 5);
        Assert.True(bgra[2] >= 250);
        Assert.Equal(255, bgra[3]);
    }

    [Fact]
    public void BuildBandwidthFallbackChain_PrioritizesMeaningfulStepDownOverMarginalReduction()
    {
        var initial = new MediaCaptureVideoFormat(1280, 720, 20d, MediaCapturePixelFormat.Mjpeg);
        var formats = new List<MediaCaptureVideoFormat>
        {
            initial,
            new(960, 720, 20d, MediaCapturePixelFormat.Mjpeg),
            new(640, 480, 20d, MediaCapturePixelFormat.Mjpeg),
            new(640, 480, 15d, MediaCapturePixelFormat.Yuy2)
        };

        var chain = LinuxV4l2CaptureBackend.BuildBandwidthFallbackChain(formats, initial);
        Assert.Equal(3, chain.Count);
        // 640x480 MJPG (33% pixel area) should come before 960x720 MJPG (75% pixel area)
        Assert.Equal(640, chain[0].Width);
        Assert.Equal(480, chain[0].Height);
        Assert.Equal(MediaCapturePixelFormat.Mjpeg, chain[0].PixelFormat);
    }

    [Fact]
    public async Task LiveLinuxV4l2Camera_EnumeratesStreamsSnapshotsAndReopensCleanly()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var devices = MediaCaptureDevices.GetVideoDevices(includeFormats: true);
        if (devices.Count == 0)
        {
            return;
        }

        Assert.Equal(devices.Count, devices.Select(d => d.UniqueDisplayName).Distinct(StringComparer.Ordinal).Count());
        var first = devices[0];
        Assert.False(string.IsNullOrWhiteSpace(first.DeviceId));
        Assert.NotEmpty(first.Formats);

        await using (var player = new CaptureMediaPlayer())
        {
            var output = new LinuxProbeVideoOutput();
            player.VideoOutput = output;

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var frames = 0;
            player.FrameReceived += (_, frame) =>
            {
                if (frame.Width > 0 && frame.Height > 0 && Interlocked.Increment(ref frames) >= 5)
                {
                    tcs.TrySetResult();
                }
            };

            await player.OpenAsync(
                CaptureSource.FromDevice(first),
                new MediaOpenOptions
                {
                    Video = new MediaVideoOptions { SnapshotPolicy = MediaSnapshotPolicy.KeepLatestFrame }
                });
            await player.PlayAsync();

            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(output.PresentedCount >= 3);

            var snapshot = await player.CaptureSnapshotAsync();
            Assert.NotNull(snapshot);
            Assert.True(snapshot!.Width > 0 && snapshot.Height > 0);
            Assert.Equal(MediaPixelFormat.Bgra32, snapshot.PixelFormat);
            Assert.Contains("V4L2", player.Diagnostics.VideoDecoderDiagnostics, StringComparison.Ordinal);

            await player.StopAsync();
        }

        // Immediate re-open verifies V4L2 fd & mmap buffers were completely released.
        await using (var player2 = new CaptureMediaPlayer())
        {
            var output2 = new LinuxProbeVideoOutput();
            player2.VideoOutput = output2;

            var tcs2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var frames2 = 0;
            player2.FrameReceived += (_, frame) =>
            {
                if (frame.Width > 0 && frame.Height > 0 && Interlocked.Increment(ref frames2) >= 2)
                {
                    tcs2.TrySetResult();
                }
            };

            await player2.OpenAsync(CaptureSource.FromDevice(first));
            await player2.PlayAsync();
            await tcs2.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(output2.PresentedCount >= 1);
            await player2.StopAsync();
        }
    }

    private sealed class AdaptiveDmaBufVideoOutput : IMediaVideoOutput
    {
        public bool FailDmaBuf { get; set; }
        public int DmaBufPresented { get; private set; }
        public int CpuPresented { get; private set; }

        public MediaFrameStorageKind PreferredFrameStorage => MediaFrameStorageKind.DmaBuf;

        public bool Supports(MediaFrameStorageKind storageKind, MediaPixelFormat pixelFormat) =>
            (storageKind == MediaFrameStorageKind.DmaBuf && pixelFormat == MediaPixelFormat.Nv12) ||
            (storageKind == MediaFrameStorageKind.CpuMemory && pixelFormat == MediaPixelFormat.Bgra32);

        public bool TryPresent(IMediaFrameLease frame)
        {
            if (frame.StorageKind == MediaFrameStorageKind.DmaBuf)
            {
                if (FailDmaBuf || !frame.TryGetDmaBuf(out var dmaBuf) || dmaBuf.Layers.Length == 0)
                {
                    return false;
                }

                DmaBufPresented++;
                frame.Dispose();
                return true;
            }

            if (frame.StorageKind == MediaFrameStorageKind.CpuMemory && frame.TryGetCpuBuffer(out _))
            {
                CpuPresented++;
                frame.Dispose();
                return true;
            }

            return false;
        }
    }

    private sealed class DmaBufTestBackend(CaptureFrameBufferPool pool) : ICaptureBackend
    {
        private Func<bool>? _prefersHardware;
        private Func<bool>? _needsCpu;
        private Action<CaptureDeliveredFrame>? _onFrame;
        private int _releaseCount;

        public int DmaBufReleaseCount => Volatile.Read(ref _releaseCount);
        public bool LastNeedsCpuFrame { get; private set; }

        public CaptureOpenResult Open(ParsedCaptureUri parsedUri, MediaOpenOptions openOptions, CapturePlayerOptions playerOptions) =>
            new(
                "/dev/video0",
                "Test V4L2 NV12",
                null,
                new MediaCaptureVideoFormat(64, 48, 30d, MediaCapturePixelFormat.Nv12),
                MediaCapturePixelFormat.Nv12,
                "V4L2 [DMA-BUF]",
                IsHardwareVideoActive: true);

        public void Start(
            Func<bool> prefersHardwareFrame,
            Func<bool> needsCpuFrame,
            Action<CaptureDeliveredFrame> onFrame,
            Action<Exception, bool> onError)
        {
            _prefersHardware = prefersHardwareFrame;
            _needsCpu = needsCpuFrame;
            _onFrame = onFrame;
        }

        public void EmitFrame(int width, int height)
        {
            var wantHw = _prefersHardware?.Invoke() ?? false;
            var wantCpu = _needsCpu?.Invoke() ?? false;
            LastNeedsCpuFrame = wantCpu;

            CaptureMediaFrameLease? hwLease = null;
            CaptureMediaFrameLease? cpuLease = null;

            if (wantHw)
            {
                var dmaBuf = new MediaDmaBufFrameBuffer(
                    new[] { new MediaDmaBufObject(42, width * height * 3 / 2, 0UL) },
                    new[]
                    {
                        new MediaDmaBufLayer(
                            LinuxV4l2Interop.DRM_FORMAT_NV12,
                            new[]
                            {
                                new MediaDmaBufPlane(0, 0, width),
                                new MediaDmaBufPlane(0, width * height, width)
                            })
                    });
                hwLease = CaptureMediaFrameLease.CreateDmaBuf(
                    width,
                    height,
                    dmaBuf,
                    () => Interlocked.Increment(ref _releaseCount),
                    MediaPixelFormat.Nv12);
            }

            if (wantCpu || hwLease is null)
            {
                var size = width * height * 4;
                var ptr = pool.Rent(size);
                cpuLease = CaptureMediaFrameLease.CreateBgraCpu(width, height, width * 4, ptr, size, pool);
            }

            _onFrame?.Invoke(new CaptureDeliveredFrame(hwLease, cpuLease, width, height, 1.0, 0.4));
        }

        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class LinuxProbeVideoOutput : IMediaVideoOutput
    {
        private int _presented;
        public int PresentedCount => Volatile.Read(ref _presented);
        public MediaFrameStorageKind PreferredFrameStorage => MediaFrameStorageKind.DmaBuf;

        public bool Supports(MediaFrameStorageKind storageKind, MediaPixelFormat pixelFormat) =>
            storageKind == MediaFrameStorageKind.DmaBuf ||
            (storageKind == MediaFrameStorageKind.CpuMemory && pixelFormat == MediaPixelFormat.Bgra32);

        public bool TryPresent(IMediaFrameLease frame)
        {
            if (frame.StorageKind == MediaFrameStorageKind.DmaBuf && !frame.TryGetDmaBuf(out _))
            {
                return false;
            }

            if (frame.StorageKind == MediaFrameStorageKind.CpuMemory && !frame.TryGetCpuBuffer(out _))
            {
                return false;
            }

            Interlocked.Increment(ref _presented);
            frame.Dispose();
            return true;
        }
    }
}
