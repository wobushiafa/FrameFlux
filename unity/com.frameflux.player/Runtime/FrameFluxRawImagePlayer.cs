using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace FrameFlux.Unity
{
    [RequireComponent(typeof(RawImage))]
    public sealed class FrameFluxRawImagePlayer : MonoBehaviour
    {
        [SerializeField] private string source = "";
        [SerializeField] private bool autoPlay = true;
        [SerializeField] private bool enableAudio = true;
        [Tooltip("Leave empty to locate the packaged Windows x64 FFmpeg libraries.")]
        [SerializeField] private string nativeDirectory = "";
        private FrameFluxNativePlayer player;
        private Task initialization;
        private RawImage image;
        private Texture2D texture;
        private byte[] pixels;
        private bool destroying;
        private string lastError;

        public FrameFluxPlaybackState State { get; private set; }
        public double Position { get; private set; }
        /// <summary>Seconds, or -1 when unknown/live.</summary>
        public double Duration { get; private set; } = -1;
        public Texture2D Texture => texture;
        public string AudioBackend { get; private set; } = "None";
        public bool IsAudioOperational { get; private set; }
        public event Action<string> PlaybackError;

        private async void Start()
        {
            image = GetComponent<RawImage>();
            initialization = InitializeAsync();
            try { await initialization; }
            catch (Exception exception) { ReportError(exception.Message); }
        }

        private async Task InitializeAsync()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor &&
                Application.platform != RuntimePlatform.WindowsPlayer)
                throw new PlatformNotSupportedException("FrameFlux currently supports Windows x64 Unity only.");
            var created = await FrameFluxNativePlayer.CreateAsync(ResolveNativeDirectory());
            if (destroying) { await created.DisposeAsync(); return; }
            player = created;
            if (!string.IsNullOrWhiteSpace(source))
            {
                await player.OpenAsync(source, enableAudio);
                if (autoPlay && !destroying) await player.PlayAsync();
            }
        }

        public async Task OpenAsync(string mediaSource, bool play = true)
        {
            var ready = await GetPlayerAsync();
            await ready.OpenAsync(mediaSource, enableAudio);
            lastError = null;
            if (play && !destroying) await ready.PlayAsync();
        }

        public async Task PlayAsync() => await (await GetPlayerAsync()).PlayAsync();
        public async Task PauseAsync() => await (await GetPlayerAsync()).PauseAsync();
        public async Task StopAsync() => await (await GetPlayerAsync()).StopAsync();
        public async Task SeekAsync(double seconds) => await (await GetPlayerAsync()).SeekAsync(seconds);
        public async Task SetVolumeAsync(double volume) => await (await GetPlayerAsync()).SetVolumeAsync(volume);
        public async Task SetMutedAsync(bool muted) => await (await GetPlayerAsync()).SetMutedAsync(muted);
        public async Task SetPlaybackRateAsync(double rate) => await (await GetPlayerAsync()).SetPlaybackRateAsync(rate);

        private async Task<FrameFluxNativePlayer> GetPlayerAsync()
        {
            if (initialization == null) throw new InvalidOperationException("Wait until the component Start has run.");
            await initialization;
            if (destroying || player == null) throw new ObjectDisposedException(nameof(FrameFluxRawImagePlayer));
            return player;
        }

        private void Update()
        {
            if (player == null || destroying || initialization == null || !initialization.IsCompleted) return;
            try
            {
                if (player.TryGetStatus(out var state, out var position, out var duration))
                {
                    State = state;
                    Position = position;
                    Duration = duration;
                }
                if (player.TryGetAudioStatus(out var audioBackend, out var audioOperational))
                {
                    AudioBackend = audioBackend;
                    IsAudioOperational = audioOperational;
                }
                if (player.TryCopyFrame(ref pixels, out var width, out var height))
                {
                    if (texture == null || texture.width != width || texture.height != height)
                    {
                        if (texture != null) Destroy(texture);
                        texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                        image.texture = texture;
                    }
                    texture.LoadRawTextureData(pixels);
                    texture.Apply(false, false);
                }
                var error = player.GetPlaybackError();
                if (!string.IsNullOrEmpty(error) && error != lastError) ReportError(error);
            }
            catch (Exception exception)
            {
                if (exception.Message != lastError) ReportError(exception.Message);
            }
        }

        private void ReportError(string message)
        {
            if (destroying) return;
            lastError = message;
            Debug.LogError("FrameFlux: " + message, this);
            PlaybackError?.Invoke(message);
        }

        private async void OnDestroy()
        {
            destroying = true;
            if (image != null && image.texture == texture) image.texture = null;
            if (texture != null) Destroy(texture);
            try
            {
                if (initialization != null)
                {
                    try { await initialization; }
                    catch { /* Initialization errors are reported by Start. */ }
                }
                if (player != null) await player.DisposeAsync();
            }
            catch (Exception exception) { Debug.LogError("FrameFlux cleanup: " + exception.Message); }
            finally { player = null; pixels = null; }
        }

        private string ResolveNativeDirectory()
        {
            if (!string.IsNullOrWhiteSpace(nativeDirectory)) return Path.GetFullPath(nativeDirectory);
#if UNITY_EDITOR
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(FrameFluxRawImagePlayer).Assembly);
            if (package != null)
                return Path.Combine(package.resolvedPath, "Runtime", "Plugins", "x86_64");
#endif
            foreach (var path in new[]
            {
                Path.Combine(Application.dataPath, "Plugins", "x86_64"),
                Path.Combine(Application.dataPath, "Plugins"),
                Path.GetDirectoryName(Application.dataPath)
            })
            {
                if (Directory.Exists(path) && Directory.GetFiles(path, "avutil*.dll").Length > 0) return path;
            }
            throw new DirectoryNotFoundException("Packaged FFmpeg libraries were not found. Set Native Directory explicitly.");
        }
    }
}
