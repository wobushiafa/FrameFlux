using System;
using System.Runtime.InteropServices;
using FrameFlux.WebRtc;
using UnityEngine;
using UnityEngine.Audio;

namespace FrameFlux.Unity
{
    [RequireComponent(typeof(AudioSource))]
    [DisallowMultipleComponent]
    public sealed class UnityAudioOutput : MonoBehaviour
    {
        [SerializeField] private AudioMixerGroup mixerGroup;
        private volatile PcmAudioBuffer buffer = new PcmAudioBuffer();
        private AudioSource audioSource;
        private AudioClip clockClip;
        private int outputRate;
        private volatile bool operational;
        private bool suspended;
        public long PlayedFrames => buffer.PlayedFrames;
        public AudioMixerGroup MixerGroup
        {
            get => mixerGroup;
            set { mixerGroup = value; if (audioSource != null) audioSource.outputAudioMixerGroup = value; }
        }
        private void Awake()
        {
            outputRate = AudioSettings.outputSampleRate;
            audioSource = GetComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.loop = true;
            audioSource.spatialBlend = 0;
            audioSource.outputAudioMixerGroup = mixerGroup;
            clockClip = AudioClip.Create("FrameFlux Audio Clock", 48000, 2, 48000, false);
            audioSource.clip = clockClip;
            audioSource.Play();
            operational = true;
            AudioSettings.OnAudioConfigurationChanged += OnAudioConfigurationChanged;
        }
        private void OnAudioConfigurationChanged(bool deviceChanged) { outputRate = AudioSettings.outputSampleRate; }
        private void OnAudioFilterRead(float[] data, int channels) => buffer.Read(data, channels, outputRate);
        private void OnEnable() { if (audioSource != null) { audioSource.Play(); operational = true; buffer.SetPaused(suspended); } }
        private void OnDisable() { operational = false; buffer.SetPaused(true); }
        private void OnApplicationPause(bool paused) { suspended = paused; buffer.SetPaused(paused || !isActiveAndEnabled); }
        private void OnDestroy()
        {
            operational = false;
            AudioSettings.OnAudioConfigurationChanged -= OnAudioConfigurationChanged;
            buffer.Reset();
            if (audioSource != null) audioSource.Stop();
            if (clockClip != null) Destroy(clockClip);
        }

        // Each session owns an adapter; the component owns Unity resources across source changes.
        public IMediaAudioOutput CreateOutput(int sampleRate, int channels)
        {
            if (sampleRate != 48000 || channels != 2) throw new NotSupportedException("Expected 48000 Hz stereo PCM.");
            return new SessionOutput(this);
        }
        public IWebRtcAudioOutput CreateWebRtcOutput() => new SessionOutput(this);

        private sealed class SessionOutput : IMediaAudioOutput, IWebRtcAudioOutput
        {
            private readonly UnityAudioOutput owner;
            private readonly PcmAudioBuffer sessionBuffer = new PcmAudioBuffer();
            private volatile bool disposed;
            internal SessionOutput(UnityAudioOutput owner) { this.owner = owner; owner.buffer = sessionBuffer; }
            public int SampleRate => 48000;
            public int Channels => 2;
            public long PlayedFrames => sessionBuffer.PlayedFrames;
            public bool IsOperational => !disposed && owner.operational;
            public bool IsSupported => IsOperational;
            public MediaAudioDiagnostics Diagnostics => new MediaAudioDiagnostics("Unity AudioSource", null, null, 48000, 2,
                TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(sessionBuffer.QueuedFrames / 48000d), IsOperational, 0, null);
            public void EnsureFormat(int sampleRate, int channels) => sessionBuffer.Configure(sampleRate, channels);
            public void Write(ReadOnlySpan<byte> pcm) { if (!disposed) sessionBuffer.Write(MemoryMarshal.Cast<byte, short>(pcm)); }
            public void WriteSamples(ReadOnlySpan<short> pcm) { if (!disposed) sessionBuffer.Write(pcm); }
            public void SetVolume(double volume, bool muted) => sessionBuffer.SetVolume(volume, muted);
            public void Pause() => sessionBuffer.SetPaused(true);
            public void Resume() => sessionBuffer.SetPaused(false);
            public void Reset() => sessionBuffer.Reset();
            public void Dispose() { disposed = true; sessionBuffer.Reset(); }
        }
    }
}
