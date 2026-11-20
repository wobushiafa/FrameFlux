using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public static class SmokeBuilder
{
    private static string CreateScene()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        new GameObject("FrameFlux Smoke Runner", typeof(MediaViewSmoke));
        const string scene = "Assets/Smoke.unity";
        EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), scene);
        PlayerSettings.runInBackground = true;
        return scene;
    }
    public static void RunEditor()
    {
        CreateScene();
        EditorApplication.isPlaying = true;
    }
    public static void BuildMono() => Build(ScriptingImplementation.Mono2x);
    public static void BuildIl2Cpp() => Build(ScriptingImplementation.IL2CPP);
    private static void Build(ScriptingImplementation backend)
    {
        var scene = CreateScene();
        var group = BuildTargetGroup.Standalone;
        PlayerSettings.SetScriptingBackend(group, backend);
        if (Enum.TryParse("NET_Standard", out ApiCompatibilityLevel profile)) PlayerSettings.SetApiCompatibilityLevel(group, profile);
        else if (Enum.TryParse("NET_Standard_2_0", out profile)) PlayerSettings.SetApiCompatibilityLevel(group, profile);
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
        if (backend == ScriptingImplementation.IL2CPP) PlayerSettings.SetManagedStrippingLevel(group, ManagedStrippingLevel.Medium);
        var args = Environment.GetCommandLineArgs();
        string output = null;
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "-framefluxBuild") output = args[i + 1];
        if (output == null) throw new ArgumentException("Missing -framefluxBuild.");
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { scene }, target = BuildTarget.StandaloneWindows64,
            locationPathName = output, options = BuildOptions.Development
        });
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
