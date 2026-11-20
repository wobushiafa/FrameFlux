using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class PreviewBuilder
{
    public static void Run()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        new GameObject("FrameFlux Playback Preview", typeof(AudioListener), typeof(MediaPlaybackPreview));
        EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), "Assets/Preview.unity");
        PlayerSettings.runInBackground = true;
        EditorApplication.ExecuteMenuItem("Window/General/Game");
        EditorApplication.isPlaying = true;
    }
}
