using System;
using UnityEngine;

namespace FrameFlux.Unity.Samples
{
    // Assign the component in the Inspector and bind these methods to UI Buttons.
    public sealed class FrameFluxControls : MonoBehaviour
    {
        [SerializeField] private FrameFluxRawImagePlayer player;
        public async void Play() { try { await player.PlayAsync(); } catch (Exception e) { Debug.LogException(e, this); } }
        public async void Pause() { try { await player.PauseAsync(); } catch (Exception e) { Debug.LogException(e, this); } }
        public async void Stop() { try { await player.StopAsync(); } catch (Exception e) { Debug.LogException(e, this); } }
    }
}
