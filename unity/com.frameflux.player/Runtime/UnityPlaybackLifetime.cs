using System.Collections.Generic;
#if UNITY_EDITOR
using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
#endif

namespace FrameFlux.Unity
{
#if UNITY_EDITOR
    [InitializeOnLoad]
#endif
    internal static class UnityPlaybackLifetime
    {
        private static readonly HashSet<MediaView> Players = new HashSet<MediaView>();
#if UNITY_EDITOR
        static UnityPlaybackLifetime()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingPlayMode) Shutdown();
            };
        }
        // Backend disposal runs on workers and never needs the Unity context.
        // Finish before Unity tears down managed reverse P/Invoke callbacks.
        private static void Shutdown()
        {
            var views = new List<MediaView>(Players);
            var operations = new List<Task>();
            foreach (var view in views) if (view != null) operations.Add(view.DisposeAsync());
            try
            {
                if (!Task.WhenAll(operations).Wait(TimeSpan.FromSeconds(10)))
                    Debug.LogError("FrameFlux playback did not finish shutting down before the Unity domain reload.");
            }
            catch (Exception exception) { Debug.LogException(exception); }
        }
#endif
        internal static void Register(MediaView view) => Players.Add(view);
        internal static void Unregister(MediaView view) => Players.Remove(view);
    }
}
