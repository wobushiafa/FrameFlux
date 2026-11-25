using FrameFlux.Capture;
using Xunit;

namespace FrameFlux.Capture.Tests;

public sealed class WindowsLiveCameraIntegrationTests
{
    [Fact]
    public async Task LiveUsbCamera_EnumeratesStreamsSnapshotsAndReopensCleanly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var devices = MediaCaptureDevices.GetVideoDevices(includeFormats: true);
        if (devices.Count == 0)
        {
            return;
        }

        // Verify duplicate device names are uniquely disambiguated.
        Assert.Equal(devices.Count, devices.Select(d => d.UniqueDisplayName).Distinct(StringComparer.Ordinal).Count());
        var first = devices[0];
        Assert.False(string.IsNullOrWhiteSpace(first.DeviceId));
        Assert.NotEmpty(first.Formats);

        // Pass 1: D3D11 hardware texture presentation path + snapshot + constant Texture2DArray check
        await using (var player = new CaptureMediaPlayer())
        {
            var d3d11Output = new ProbeVideoOutput(MediaFrameStorageKind.D3D11Texture);
            player.VideoOutput = d3d11Output;

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var frameCount = 0;
            player.FrameReceived += (_, frame) =>
            {
                if (frame.Width > 0 && frame.Height > 0 && Interlocked.Increment(ref frameCount) >= 5)
                {
                    tcs.TrySetResult();
                }
            };

            var source = CaptureSource.FromDevice(first);
            var options = new MediaOpenOptions
            {
                Video = new MediaVideoOptions
                {
                    DecodingPolicy = MediaVideoDecodingPolicy.HardwareRequired,
                    SnapshotPolicy = MediaSnapshotPolicy.KeepLatestFrame
                }
            };

            await player.OpenAsync(source, options);
            await player.PlayAsync();

            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True(d3d11Output.PresentedCount >= 3);
            Assert.Single(d3d11Output.DistinctTexturePointers);
            Assert.All(d3d11Output.ObservedSlices, slice => Assert.InRange(slice, 0, 15));
            Assert.True(d3d11Output.ObservedSlices.Distinct().Count() >= 2);

            var snapshot = await player.CaptureSnapshotAsync();
            Assert.NotNull(snapshot);
            Assert.True(snapshot!.Width > 0 && snapshot.Height > 0);
            Assert.Equal(MediaPixelFormat.Bgra32, snapshot.PixelFormat);

            await player.StopAsync();
        }

        // Pass 2: Immediate re-open in SoftwareOnly mode verifies camera handle was completely released
        await using (var player2 = new CaptureMediaPlayer())
        {
            var cpuOutput = new ProbeVideoOutput(MediaFrameStorageKind.CpuMemory);
            player2.VideoOutput = cpuOutput;

            var tcs2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var frameCount2 = 0;
            player2.FrameReceived += (_, frame) =>
            {
                if (frame.Width > 0 && frame.Height > 0 && Interlocked.Increment(ref frameCount2) >= 2)
                {
                    tcs2.TrySetResult();
                }
            };

            await player2.OpenAsync(
                CaptureSource.FromDevice(first),
                new MediaOpenOptions
                {
                    Video = new MediaVideoOptions { DecodingPolicy = MediaVideoDecodingPolicy.SoftwareOnly }
                });
            await player2.PlayAsync();

            await tcs2.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True(cpuOutput.PresentedCount >= 1);
            await player2.StopAsync();
        }
    }

    private sealed class ProbeVideoOutput(MediaFrameStorageKind preferred) : IMediaVideoOutput
    {
        private readonly object _sync = new();
        private readonly HashSet<IntPtr> _texturePointers = [];
        private readonly List<int> _slices = [];
        private int _presented;

        public int PresentedCount => Volatile.Read(ref _presented);

        public IReadOnlyCollection<IntPtr> DistinctTexturePointers
        {
            get
            {
                lock (_sync)
                {
                    return [.. _texturePointers];
                }
            }
        }

        public IReadOnlyList<int> ObservedSlices
        {
            get
            {
                lock (_sync)
                {
                    return [.. _slices];
                }
            }
        }

        public MediaFrameStorageKind PreferredFrameStorage => preferred;

        public bool Supports(MediaFrameStorageKind storageKind, MediaPixelFormat pixelFormat) =>
            storageKind == preferred && pixelFormat == MediaPixelFormat.Bgra32;

        public bool TryPresent(IMediaFrameLease frame)
        {
            if (preferred == MediaFrameStorageKind.D3D11Texture)
            {
                if (!frame.TryGetD3D11Texture(out var texture))
                {
                    return false;
                }

                lock (_sync)
                {
                    _texturePointers.Add(texture.Texture);
                    _slices.Add(texture.ArraySlice);
                }
            }

            if (preferred == MediaFrameStorageKind.CpuMemory && !frame.TryGetCpuBuffer(out _))
            {
                return false;
            }

            Interlocked.Increment(ref _presented);
            frame.Dispose();
            return true;
        }
    }
}
