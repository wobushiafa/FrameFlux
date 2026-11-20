using System;
using System.IO;
using System.Threading.Tasks;
using FrameFlux;
using FrameFlux.Unity;
using UnityEngine;
using UnityEngine.UI;

public sealed class MediaViewSmoke : MonoBehaviour
{
    private async void Start()
    {
        int code = 1;
        var result = Argument("-framefluxResult");
        MediaView player = null;
        UnityWhepServer server = null;
        try
        {
            var source = Argument("-framefluxSource");
            if (string.IsNullOrWhiteSpace(source)) throw new Exception("Missing -framefluxSource.");
            var root = new GameObject("FrameFlux Smoke", typeof(RectTransform), typeof(Canvas));
            root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var surface = new GameObject("Video", typeof(RectTransform), typeof(RawImage));
            surface.transform.SetParent(root.transform, false);
            player = surface.AddComponent<MediaView>();
            player.AutoPlay = false;
            surface.AddComponent<MediaRawImage>();
            if (Argument("-framefluxBackend") == "WebRtc")
            {
                server = new UnityWhepServer();
                player.Backend = MediaBackend.WebRtc;
                player.Source = MediaSource.Parse(server.Endpoint);
            }
            else player.Source = MediaSource.Parse(source);
            player.OpenOptions = new MediaOpenOptions
            {
                Video = new MediaVideoOptions { SnapshotPolicy = MediaSnapshotPolicy.KeepLatestFrame },
                Network = new MediaNetworkOptions { Reconnect = new MediaReconnectOptions { IsEnabled = false } }
            };
            await player.PlayAsync();
            await WaitUntil(() => player.Texture != null, TimeSpan.FromSeconds(20));
            Check(surface.GetComponent<RawImage>().texture == player.Texture, "RawImage texture binding");
            Check(player.Texture.width > 0 && player.Texture.height > 0, "Video dimensions");
            var snapshot = await player.CaptureSnapshotAsync();
            Check(snapshot != null && snapshot.Data.Length > 0, "Snapshot");
            if (player.Capabilities.CanPause) { await player.PauseAsync(); Check(player.State == MediaPlaybackState.Paused, "Pause state"); }
            if (player.Capabilities.CanSeek) await player.SeekAsync(TimeSpan.FromSeconds(1));
            if (player.Capabilities.CanChangePlaybackRate) player.PlaybackRate = 1.5;
            player.Volume = 0.25;
            player.IsMuted = true;
            await player.PlayAsync();
            var audio = surface.GetComponent<UnityAudioOutput>();
            await WaitUntil(() => audio != null && audio.PlayedFrames > 0, TimeSpan.FromSeconds(10));
            Check(player.Diagnostics.Audio.Backend == "Unity AudioSource", "Unity audio backend");
            Check(player.Diagnostics.Audio.IsOperational, "Unity audio operational");
            server?.ThrowIfFailed();
            if (player.Backend == MediaBackend.FFmpeg)
            {
                // Exercise the asynchronous disable/enable transition while playback is active.
                player.enabled = false;
                player.enabled = true;
                await Task.Delay(200);
                Check(player.State == MediaPlaybackState.Playing, "Resume after rapid disable/enable");
            }
            await player.StopAsync();
            await player.DisposeAsync();
            await player.DisposeAsync();
            var message = "PASS Unity MediaView " + player.Backend + ": texture, RawImage, snapshot, controls, AudioSource, repeated disposal";
            Debug.Log(message);
            File.WriteAllText(result, message);
            code = 0;
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            if (!string.IsNullOrWhiteSpace(result)) File.WriteAllText(result, "FAIL " + exception);
        }
        finally
        {
            if (player != null) await player.DisposeAsync();
            server?.Dispose();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.Exit(code);
#else
            Application.Quit(code);
#endif
        }
    }

    private static async Task WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow >= end) throw new TimeoutException("Unity smoke check timed out.");
            await Task.Delay(16);
        }
    }
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name + " failed."); }
    private static string Argument(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) return args[i + 1];
        return "";
    }
}
