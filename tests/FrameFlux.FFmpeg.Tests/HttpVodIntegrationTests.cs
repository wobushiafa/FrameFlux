using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FrameFlux;
using FrameFlux.FFmpeg;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class HttpVodIntegrationTests
{
    [NativeHttpFact]
    [Trait("Category", "Integration")]
    public async Task ProgressiveHttpVideo_PlaysAndSeeks()
    {
        var nativeDirectory = Environment.GetEnvironmentVariable("FRAMEFLUX_FFMPEG_LIBRARY_DIR")!;
        FFmpegHelper.RegisterFFmpeg(nativeDirectory);

        var mediaPath = Path.Combine(Path.GetTempPath(), $"frameflux-http-{Guid.NewGuid():N}.mp4");
        try
        {
            using (var ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg")
            {
                ArgumentList =
                {
                    "-v", "error", "-f", "lavfi", "-i", "testsrc=size=64x64:rate=10",
                    "-t", "12", "-c:v", "mpeg4", "-g", "10", "-q:v", "5", "-movflags", "+faststart",
                    "-y", mediaPath
                },
                UseShellExecute = false
            })!)
            {
                await ffmpeg.WaitForExitAsync();
                Assert.Equal(0, ffmpeg.ExitCode);
            }

            await using var server = new RangeHttpServer(await File.ReadAllBytesAsync(mediaPath));
            await using var player = new FfmpegMediaPlayer();
            var frames = 0;
            long latestFramePositionTicks = 0;
            player.FrameReceived += (_, _) =>
            {
                Interlocked.Exchange(ref latestFramePositionTicks, player.Position.Ticks);
                Interlocked.Increment(ref frames);
            };
            await player.OpenAsync(MediaSource.Parse(server.Url), new MediaOpenOptions
            {
                Video = new MediaVideoOptions { DecodingPolicy = MediaVideoDecodingPolicy.SoftwareOnly },
                Audio = new MediaAudioOptions { IsEnabled = false }
            });
            await player.PlayAsync();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref frames) > 0, TimeSpan.FromSeconds(10)));
            Assert.InRange(player.Duration!.Value.TotalSeconds, 11.5d, 12.5d);

            var framesBeforeSeek = Volatile.Read(ref frames);
            await player.SeekAsync(TimeSpan.FromSeconds(9));
            Assert.True(SpinWait.SpinUntil(() =>
                Volatile.Read(ref frames) > framesBeforeSeek &&
                TimeSpan.FromTicks(Interlocked.Read(ref latestFramePositionTicks)) >= TimeSpan.FromSeconds(8),
                TimeSpan.FromSeconds(10)));

            framesBeforeSeek = Volatile.Read(ref frames);
            await player.SeekAsync(TimeSpan.FromSeconds(2));
            Assert.True(SpinWait.SpinUntil(() =>
                Volatile.Read(ref frames) > framesBeforeSeek &&
                TimeSpan.FromTicks(Interlocked.Read(ref latestFramePositionTicks)) is { } position &&
                position >= TimeSpan.FromSeconds(1) && position <= TimeSpan.FromSeconds(4),
                TimeSpan.FromSeconds(10)));
            await player.StopAsync();
        }
        finally
        {
            if (File.Exists(mediaPath)) File.Delete(mediaPath);
        }
    }

    private sealed class RangeHttpServer : IAsyncDisposable
    {
        private readonly byte[] _content;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _acceptTask;

        internal RangeHttpServer(byte[] content)
        {
            _content = content;
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/video.mp4";
            _acceptTask = AcceptAsync();
        }

        internal string Url { get; }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(() => ServeAsync(client));
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var request = await reader.ReadLineAsync();
                if (request is null) return;
                var start = 0;
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                {
                    if (!line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase)) continue;
                    var value = line[13..].Split('-', 2)[0];
                    if (int.TryParse(value, out var parsed)) start = Math.Clamp(parsed, 0, _content.Length - 1);
                }

                var length = _content.Length - start;
                var partial = start > 0;
                var headers = $"HTTP/1.1 {(partial ? "206 Partial Content" : "200 OK")}\r\n" +
                    "Content-Type: video/mp4\r\nAccept-Ranges: bytes\r\n" +
                    $"Content-Length: {length}\r\n" +
                    (partial ? $"Content-Range: bytes {start}-{_content.Length - 1}/{_content.Length}\r\n" : "") +
                    "Connection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
                if (!request.StartsWith("HEAD ", StringComparison.OrdinalIgnoreCase))
                    await stream.WriteAsync(_content.AsMemory(start, length));
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _acceptTask;
            _stop.Dispose();
        }
    }

    private sealed class NativeHttpFactAttribute : FactAttribute
    {
        public NativeHttpFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("FRAMEFLUX_RUN_NATIVE_HTTP_TESTS") != "1" ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FRAMEFLUX_FFMPEG_LIBRARY_DIR")))
                Skip = "Set FRAMEFLUX_RUN_NATIVE_HTTP_TESTS=1 and FRAMEFLUX_FFMPEG_LIBRARY_DIR to run the native HTTP playback test.";
        }
    }
}
