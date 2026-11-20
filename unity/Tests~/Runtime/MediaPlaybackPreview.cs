using System;
using System.Threading.Tasks;
using FrameFlux;
using FrameFlux.Unity;
using UnityEngine;
using UnityEngine.UI;

// Interactive preview stays open; automatic assertions live in MediaViewSmoke.
public sealed class MediaPlaybackPreview : MonoBehaviour
{
    private MediaView player;
    private UnityWhepServer server;
    private string status = "Opening media...";
    private bool busy, sawPlaying, audioReported;
    private bool loop = true;

    private async void Start()
    {
        try
        {
            var canvasObject = new GameObject("Video Canvas", typeof(RectTransform), typeof(Canvas));
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var frame = new GameObject("Video Area", typeof(RectTransform));
            frame.transform.SetParent(canvasObject.transform, false);
            var frameRect = (RectTransform)frame.transform;
            frameRect.anchorMin = Vector2.zero; frameRect.anchorMax = Vector2.one;
            frameRect.offsetMin = new Vector2(10, 70); frameRect.offsetMax = new Vector2(-10, -70);
            var surface = new GameObject("Video", typeof(RectTransform), typeof(RawImage), typeof(AspectRatioFitter));
            surface.transform.SetParent(frame.transform, false);
            surface.GetComponent<AspectRatioFitter>().aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            player = surface.AddComponent<MediaView>();
            player.AutoPlay = false;
            surface.AddComponent<MediaRawImage>();
            player.Error += (_, args) => { status = args.Error.Message; Debug.LogError(status); };
            player.TextureChanged += texture =>
            {
                if (texture != null) Debug.Log("FrameFlux preview texture: " + texture.width + "x" + texture.height);
            };
            if (Argument("-framefluxBackend") == "WebRtc")
            {
                server = new UnityWhepServer();
                player.Backend = MediaBackend.WebRtc;
                player.Source = MediaSource.Parse(server.Endpoint);
                loop = false;
            }
            else player.Source = MediaSource.Parse(Argument("-framefluxSource"));
            await RestartAsync();
        }
        catch (Exception exception) { Report(exception); }
    }

    private void Update()
    {
        if (player == null || busy) return;
        if (player.State == MediaPlaybackState.Playing) sawPlaying = true;
        if (loop && sawPlaying && player.State == MediaPlaybackState.Stopped)
        {
            sawPlaying = false;
            _ = RestartAsync();
        }
        var audio = player.GetComponent<UnityAudioOutput>();
        if (!audioReported && audio != null && audio.PlayedFrames > 0)
        {
            audioReported = true;
            Debug.Log("FrameFlux preview AudioSource rendered PCM frames.");
        }
    }

    private void OnGUI()
    {
        GUI.Box(new Rect(0, 0, Screen.width, 60), "FrameFlux Playback Preview");
        GUI.Label(new Rect(12, 28, Screen.width - 24, 25), player == null ? status :
            player.State + " | " + player.Position.ToString(@"mm\:ss") + " | " + status);
        GUI.Box(new Rect(0, Screen.height - 60, Screen.width, 60), "");
        GUI.enabled = player != null && !busy;
        if (GUI.Button(new Rect(12, Screen.height - 48, 95, 32), "Play")) _ = ObserveAsync(() => player.PlayAsync());
        GUI.enabled = player != null && !busy && player.Capabilities.CanPause;
        if (GUI.Button(new Rect(117, Screen.height - 48, 95, 32), "Pause")) _ = ObserveAsync(() => player.PauseAsync());
        GUI.enabled = player != null && !busy && player.Backend == MediaBackend.FFmpeg;
        if (GUI.Button(new Rect(222, Screen.height - 48, 95, 32), "Restart")) _ = RestartAsync();
        loop = GUI.Toggle(new Rect(332, Screen.height - 43, 100, 30), loop, "Loop");
        GUI.enabled = true;
    }

    private async Task RestartAsync()
    {
        await ObserveAsync(() => player.StartAsync());
    }
    private async Task ObserveAsync(Func<Task> operation)
    {
        if (busy) return;
        busy = true;
        try { await operation(); status = "Playing video and audio"; }
        catch (Exception exception) { Report(exception); }
        finally { busy = false; }
    }
    private void Report(Exception exception) { status = exception.Message; Debug.LogException(exception); }
    private void OnDestroy() { server?.Dispose(); }
    private static string Argument(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) return args[i + 1];
        return "";
    }
}
