using System.IO;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.PackageManager;
using UnityEditor.UnityLinker;

namespace FrameFlux.Unity.Editor
{
    // Unity 2022 does not automatically apply link.xml files inside UPM packages.
    // Register the package's rules for both Mono and IL2CPP builds.
    public sealed class FrameFluxLinkerProcessor : IUnityLinkerProcessor
    {
        public int callbackOrder => 0;
        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
        {
            var package = PackageInfo.FindForAssembly(typeof(MediaView).Assembly);
            if (package == null) throw new FileNotFoundException("Could not resolve the FrameFlux UPM package for linker preservation.");
            var path = Path.Combine(package.resolvedPath, "Runtime", "link.xml");
            if (!File.Exists(path)) throw new FileNotFoundException("FrameFlux linker rules are missing.", path);
            return path;
        }
        public void OnBeforeRun(BuildReport report, UnityLinkerBuildPipelineData data) { }
        public void OnAfterRun(BuildReport report, UnityLinkerBuildPipelineData data) { }
    }
}
