using System;
using UnityEngine;

namespace FrameFlux.Unity
{
    public enum MediaBackend { FFmpeg, WebRtc }
    public enum MediaDisableBehavior { KeepPlaying, Pause, Stop }

    [Serializable]
    public sealed class MediaViewOptions
    {
        public MediaVideoDecodingPolicy decodingPolicy = MediaVideoDecodingPolicy.Automatic;
        public MediaVideoPresentationMode presentationMode = MediaVideoPresentationMode.Automatic;
        public MediaTransport transport = MediaTransport.Tcp;
        public MediaLatencyMode latencyMode = MediaLatencyMode.Default;
        public bool enableAudio = true;
        public bool keepSnapshot;
        [Min(0.01f)] public float openTimeoutSeconds = 10;
        [Min(0.01f)] public float readTimeoutSeconds = 10;
        [Min(0)] public float initialBufferSeconds = 0.25f;
        [Min(0)] public float rebufferSeconds = 0.25f;
        public bool reconnect = true;
        [Min(0.01f)] public float reconnectDelaySeconds = 1;
        [Min(0.01f)] public float maximumReconnectDelaySeconds = 30;
        public int maximumReconnectAttempts = -1;
        [Min(0)] public int maximumWidth;
        [Min(0)] public int maximumHeight;
        [Min(0)] public float maximumFrameRate;

        public MediaOpenOptions ToOpenOptions() => new MediaOpenOptions
        {
            SessionSharing = MediaSessionSharingMode.Dedicated,
            Network = new MediaNetworkOptions
            {
                Transport = transport, LatencyMode = latencyMode,
                OpenTimeout = TimeSpan.FromSeconds(openTimeoutSeconds),
                ReadTimeout = TimeSpan.FromSeconds(readTimeoutSeconds),
                InitialBufferDuration = TimeSpan.FromSeconds(initialBufferSeconds),
                RebufferDuration = TimeSpan.FromSeconds(rebufferSeconds),
                Reconnect = new MediaReconnectOptions
                {
                    IsEnabled = reconnect, InitialDelay = TimeSpan.FromSeconds(reconnectDelaySeconds),
                    MaximumDelay = TimeSpan.FromSeconds(maximumReconnectDelaySeconds),
                    MaximumAttempts = maximumReconnectAttempts < 0 ? (int?)null : maximumReconnectAttempts
                }
            },
            Video = new MediaVideoOptions
            {
                DecodingPolicy = decodingPolicy,
                SnapshotPolicy = keepSnapshot ? MediaSnapshotPolicy.KeepLatestFrame : MediaSnapshotPolicy.Disabled,
                MaximumWidth = maximumWidth <= 0 ? (int?)null : maximumWidth,
                MaximumHeight = maximumHeight <= 0 ? (int?)null : maximumHeight,
                MaximumFrameRate = maximumFrameRate <= 0 ? (double?)null : maximumFrameRate
            },
            Audio = new MediaAudioOptions { IsEnabled = enableAudio }
        };
    }
}
