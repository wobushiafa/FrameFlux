using System;
using System.Threading.Tasks;
using FrameFlux.Unity;
using UnityEngine;

namespace FrameFlux.Unity.Samples
{
    public sealed class MediaViewControls : MonoBehaviour
    {
        [SerializeField] private MediaView player;

        // Bind these methods to Unity UI Button.onClick.
        public async void Play() { await Observe(() => player.PlayAsync()); }
        public async void Pause() { if (player != null && player.Capabilities.CanPause) await Observe(() => player.PauseAsync()); }
        public async void Stop() { await Observe(() => player.StopAsync()); }
        public void SetVolume(float value) { if (player != null) player.Volume = value; }
        public void SetMuted(bool value) { if (player != null) player.IsMuted = value; }

        private async Task Observe(Func<Task> operation)
        {
            if (player == null) { Debug.LogError("Assign a MediaView to the controls.", this); return; }
            try { await operation(); }
            catch (Exception exception) { Debug.LogException(exception, this); }
        }
    }
}
