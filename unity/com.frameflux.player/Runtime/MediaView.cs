using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FrameFlux.FFmpeg;
using FrameFlux.Presentation;
using FrameFlux.WebRtc;
using UnityEngine;

namespace FrameFlux.Unity
{
    /// <summary>Platform presentation control sharing FrameFlux's contracts and playback lifecycle.</summary>
    [DisallowMultipleComponent]
    public sealed class MediaView : MonoBehaviour
    {
        [SerializeField] private string source = "";
        [SerializeField] private bool autoPlay = true;
        [SerializeField] private MediaBackend backend;
        [SerializeField] private MediaDisableBehavior disableBehavior = MediaDisableBehavior.Pause;
        [SerializeField] private MediaViewOptions options = new MediaViewOptions();
        [SerializeField] private string nativeLibraryDirectory = "";
        [SerializeField] private UnityAudioOutput audioOutput;
        private MediaPlaybackController controller;
        private UnityTextureOutput textureOutput;
        private IMediaVideoOutput customVideoOutput;
        private CancellationTokenSource lifetime;
        private readonly ConcurrentQueue<Action> events = new ConcurrentQueue<Action>();
        private int mainThread;
        private bool released, resumeOnEnable;
        private Task disableOperation = Task.CompletedTask;
        private int activationVersion;
        private Task disposal = Task.CompletedTask;
        private EventHandler<MediaVideoFrame> frameReceived;
        private MediaVideoFrame pendingFrameNotification;

        public MediaSource Source { get; set; }
        public IMediaPlayerFactory PlayerFactory { get; set; }
        public MediaOpenOptions OpenOptions { get; set; }
        public IMediaVideoOutput VideoOutput { get => customVideoOutput ?? textureOutput; set => customVideoOutput = value; }
        public MediaVideoPresentationMode PresentationMode { get; set; } = MediaVideoPresentationMode.Automatic;
        public bool AutoPlay { get => autoPlay; set => autoPlay = value; }
        public MediaBackend Backend { get => backend; set => backend = value; }
        public MediaDisableBehavior DisableBehavior { get => disableBehavior; set => disableBehavior = value; }
        public MediaPlaybackState State => controller?.State ?? MediaPlaybackState.Idle;
        public MediaCapabilities Capabilities => controller?.Capabilities ?? MediaCapabilities.None;
        public MediaDiagnostics Diagnostics => controller?.RefreshDiagnostics() ?? MediaDiagnostics.Empty;
        public MediaPlaybackError LastError => controller?.LastError;
        public TimeSpan Position => controller?.Position ?? TimeSpan.Zero;
        public TimeSpan? Duration => controller?.Duration;
        public Texture2D Texture => textureOutput?.Texture;
        public double Volume { get => controller.Volume; set { AssertMainThread(); controller.Volume = value; } }
        public bool IsMuted { get => controller.IsMuted; set { AssertMainThread(); controller.IsMuted = value; } }
        public double PlaybackRate { get => controller.PlaybackRate; set { AssertMainThread(); controller.PlaybackRate = value; } }
        public Task Disposal => disposal;
        public event EventHandler<MediaPlaybackStateChangedEventArgs> StateChanged;
        public event EventHandler<MediaPlaybackErrorEventArgs> Error;
        public event Action<Texture2D> TextureChanged;
        /// <summary>Fires on the main thread only when subscribed. Data is copied before the decoder reuses it.</summary>
        public event EventHandler<MediaVideoFrame> FrameReceived
        {
            add { AssertMainThread(); if (frameReceived == null) controller.FrameReceived += OnFrameReceived; frameReceived += value; }
            remove { AssertMainThread(); frameReceived -= value; if (frameReceived == null) controller.FrameReceived -= OnFrameReceived; }
        }

        private void Awake() { Initialize(); }
        private void Initialize()
        {
            mainThread = Thread.CurrentThread.ManagedThreadId;
            released = false;
            controller = new MediaPlaybackController();
            textureOutput = new UnityTextureOutput();
            lifetime = new CancellationTokenSource();
            UnityPlaybackLifetime.Register(this);
            controller.StateChanged += OnStateChanged;
            controller.Error += OnError;
            if (frameReceived != null) controller.FrameReceived += OnFrameReceived;
            PresentationMode = options.presentationMode;
        }
        private async void Start()
        {
            try
            {
                if (Source == null && !string.IsNullOrWhiteSpace(source)) Source = MediaSource.Parse(source);
                if (autoPlay && Source != null) await PlayAsync();
            }
            catch (OperationCanceledException) when (released) { }
            catch (Exception exception) { if (!released) Report(exception); }
        }
        private void OnStateChanged(object sender, MediaPlaybackStateChangedEventArgs args)
        {
            if (ReferenceEquals(sender, controller)) Post(() => StateChanged?.Invoke(this, args));
        }
        private void OnError(object sender, MediaPlaybackErrorEventArgs args)
        {
            if (ReferenceEquals(sender, controller)) Post(() => Error?.Invoke(this, args));
        }
        private void OnFrameReceived(object sender, MediaVideoFrame frame)
        {
            if (!released && ReferenceEquals(sender, controller)) Interlocked.Exchange(ref pendingFrameNotification, new MediaVideoFrame(frame.Data.ToArray(),
                frame.Width, frame.Height, frame.Stride, frame.PixelFormat, frame.Sequence, frame.CapturedAt));
        }
        private void Post(Action action) { if (!released) events.Enqueue(action); }
        private void Update()
        {
            while (events.TryDequeue(out var action)) action();
            if (released) return;
            var notification = Interlocked.Exchange(ref pendingFrameNotification, null);
            if (notification != null) frameReceived?.Invoke(this, notification);
            try { if (textureOutput.UpdateTexture()) TextureChanged?.Invoke(Texture); }
            catch (Exception exception) { Report(exception); }
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            AssertMainThread();
            if (released) throw new ObjectDisposedException(nameof(MediaView));
            if (Source == null) throw new InvalidOperationException("Set Source before starting playback.");
            if (customVideoOutput == null && PresentationMode != MediaVideoPresentationMode.Automatic &&
                PresentationMode != MediaVideoPresentationMode.SoftwareBitmap)
                throw new NotSupportedException("The built-in Unity output presents CPU frames. Supply a platform video output for an explicit GPU presentation mode.");
            var openOptions = OpenOptions ?? options.ToOpenOptions();
            openOptions.Validate();
            var factory = PlayerFactory ?? CreateDefaultFactory();
            var playback = controller;
            var output = VideoOutput;
            var mediaSource = Source;
            textureOutput.Clear();
            TextureChanged?.Invoke(null);
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken))
                await Task.Run(() => playback.StartAsync(factory, mediaSource, openOptions, output, linked.Token).AsTask(), linked.Token);
        }
        public Task PlayAsync(CancellationToken cancellationToken = default)
        {
            AssertMainThread();
            var playback = controller;
            return playback.HasPlayer ? RunAsync(() => playback.ResumeAsync(cancellationToken)) : StartAsync(cancellationToken);
        }
        public Task PauseAsync(CancellationToken cancellationToken = default)
        {
            AssertMainThread(); var playback = controller; return RunAsync(() => playback.PauseAsync(cancellationToken));
        }
        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            AssertMainThread();
            var playback = controller;
            await RunAsync(() => playback.StopAsync(cancellationToken));
            if (released) return;
            textureOutput.Clear(); TextureChanged?.Invoke(null);
        }
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
        {
            AssertMainThread(); var playback = controller; return RunAsync(() => playback.SeekAsync(position, cancellationToken));
        }
        public Task<MediaSnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken = default)
        {
            AssertMainThread(); var playback = controller; return Task.Run(() => playback.CaptureSnapshotAsync(cancellationToken).AsTask());
        }
        private static Task RunAsync(Func<System.Threading.Tasks.ValueTask> operation) => Task.Run(() => operation().AsTask());
        private IMediaPlayerFactory CreateDefaultFactory()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor && Application.platform != RuntimePlatform.WindowsPlayer)
                throw new PlatformNotSupportedException("This package currently supports Unity Windows x64.");
            var directory = ResolveNativeDirectory();
            if (audioOutput == null)
            {
                audioOutput = GetComponent<UnityAudioOutput>();
                if (audioOutput == null) audioOutput = gameObject.AddComponent<UnityAudioOutput>();
            }
            if (backend == MediaBackend.FFmpeg)
            {
                FFmpegHelper.RegisterFFmpeg(directory);
                return new FfmpegMediaPlayerFactory(new FfmpegMediaPlayerFactoryOptions { AudioOutputFactory = audioOutput.CreateOutput });
            }
            FfmpegWebRtcVideoDecoder.ConfigureNativeLibraryDirectory(directory);
            if (!FfmpegWebRtcVideoDecoder.IsSupported) throw new DllNotFoundException("WebRTC could not load packaged FFmpeg libraries.");
            var unityAudio = audioOutput;
            return new SinglePlayerFactory(() => new WebRtcMediaPlayer(new WebRtcPlayerOptions
                { VideoDecoder = new FfmpegWebRtcVideoDecoder(), AudioOutput = unityAudio.CreateWebRtcOutput() }));
        }
        private string ResolveNativeDirectory()
        {
            if (!string.IsNullOrWhiteSpace(nativeLibraryDirectory)) return Path.GetFullPath(nativeLibraryDirectory);
            if (!Application.isEditor) return Path.Combine(Application.dataPath, "Plugins", "x86_64");
#if UNITY_EDITOR
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(MediaView).Assembly);
            if (package != null) return Path.Combine(package.resolvedPath, "Runtime", "Plugins", "x86_64");
#endif
            return Path.GetFullPath("Packages/com.frameflux.player/Runtime/Plugins/x86_64");
        }
        private void OnDisable()
        {
            activationVersion++;
            if (released || controller == null || !controller.HasPlayer) return;
            resumeOnEnable = disableBehavior == MediaDisableBehavior.Pause && State == MediaPlaybackState.Playing;
            if (disableBehavior == MediaDisableBehavior.Pause)
                disableOperation = ObserveAsync(Capabilities.CanPause ? PauseAsync() : StopAsync());
            else if (disableBehavior == MediaDisableBehavior.Stop) disableOperation = ObserveAsync(StopAsync());
        }
        private void OnEnable()
        {
            activationVersion++;
            if (released) Initialize();
            if (resumeOnEnable) _ = ResumeAfterEnableAsync(activationVersion);
        }
        private async Task ResumeAfterEnableAsync(int version)
        {
            await disableOperation;
            if (released || version != activationVersion || !isActiveAndEnabled || !resumeOnEnable) return;
            resumeOnEnable = false;
            await ObserveAsync(PlayAsync());
        }
        private void OnApplicationQuit() { Release(); }
        private void OnDestroy() { Release(); }
        public Task DisposeAsync() { AssertMainThread(); Release(); return disposal; }
        private void Release()
        {
            if (released || controller == null) return;
            released = true;
            UnityPlaybackLifetime.Unregister(this);
            lifetime.Cancel();
            controller.StateChanged -= OnStateChanged; controller.Error -= OnError;
            controller.FrameReceived -= OnFrameReceived;
            Interlocked.Exchange(ref pendingFrameNotification, null);
            textureOutput.Dispose();
            TextureChanged?.Invoke(null);
            while (events.TryDequeue(out _)) { }
            var playback = controller;
            var cancellation = lifetime;
            disposal = Task.Run(async () => { try { await playback.DisposeAsync(); } finally { cancellation.Dispose(); } });
            _ = ObserveAsync(disposal);
        }
        private async Task ObserveAsync(Task operation)
        {
            try { await operation; }
            catch (OperationCanceledException) when (released) { }
            catch (Exception exception) { if (!released) Report(exception); else Debug.LogException(exception); }
        }
        private void Report(Exception exception)
        {
            Debug.LogException(exception, this);
            Error?.Invoke(this, new MediaPlaybackErrorEventArgs(new MediaPlaybackError("UnityPresentation", exception.Message, false, exception)));
        }
        private void AssertMainThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != mainThread) throw new InvalidOperationException("Call MediaView from the Unity main thread.");
        }
        private sealed class SinglePlayerFactory : IMediaPlayerFactory
        {
            private Func<IMediaPlayer> create;
            internal SinglePlayerFactory(Func<IMediaPlayer> create) { this.create = create; }
            public IMediaPlayer Create() => (Interlocked.Exchange(ref create, null) ?? throw new InvalidOperationException("This factory has already been consumed."))();
        }
    }
}
