"""Compiler, bundled x64 Mono and core IL2CPP checks; not Unity Player acceptance."""
import argparse
import ctypes
from pathlib import Path
import shutil
import subprocess
import sys
import uuid


def run(command, log, timeout=300):
    with log.open("w", encoding="utf-8") as output:
        result = subprocess.run(command, stdout=output, stderr=subprocess.STDOUT, timeout=timeout)
    if result.returncode:
        raise RuntimeError(f"Check failed ({result.returncode}); see {log}")
    output_text = log.read_text(encoding="utf-8-sig", errors="replace")[-3000:]
    # Windows redirected terminals may use GBK; preserve output without failing a passed check.
    terminal_encoding = sys.stdout.encoding or "utf-8"
    print(output_text.encode(terminal_encoding, errors="backslashreplace").decode(terminal_encoding), flush=True)


def compile_sources(data, work, name, sources, references, defines=None, executable=False):
    assembly = work / (name + (".exe" if executable else ".dll"))
    arguments = ["-nostdlib+", "-langversion:9.0", "-target:" + ("exe" if executable else "library"),
                 f'-out:"{assembly}"']
    if defines:
        arguments.append("-define:" + defines)
    arguments += [f'-reference:"{reference}"' for reference in references]
    arguments += [f'"{source}"' for source in sources]
    response = work / (name + ".rsp")
    response.write_text("\n".join(arguments), encoding="utf-8")
    run([str(data / "NetCoreRuntime/dotnet.exe"), str(data / "DotNetSdkRoslyn/csc.dll"),
         "@" + str(response)], work / (name + "-compile.log"))
    return assembly


def mono_worker(data, assembly, source, native):
    # Unity's bin/mono.exe is 32-bit on this installation. Embed its actual x64 runtime.
    if ctypes.sizeof(ctypes.c_void_p) != 8:
        raise RuntimeError("Use 64-bit Python for the Windows x64 native plugins.")
    base = data / "MonoBleedingEdge"
    library = ctypes.CDLL(str(base / "EmbedRuntime/mono-2.0-sgen.dll"))
    library.mono_set_dirs.argtypes = [ctypes.c_char_p, ctypes.c_char_p]
    library.mono_set_dirs(str(base / "lib").encode(), str(base / "etc").encode())
    library.mono_set_assemblies_path.argtypes = [ctypes.c_char_p]
    paths = [assembly.parent, base / "lib/mono/unityjit-win32",
             base / "lib/mono/unityjit-win32/Facades",
             data / "NetStandard/compat/2.1.0/shims/netstandard",
             data / "NetStandard/compat/2.1.0/shims/netfx"]
    library.mono_set_assemblies_path(";".join(map(str, paths)).encode())
    library.mono_jit_init_version.argtypes = [ctypes.c_char_p, ctypes.c_char_p]
    library.mono_jit_init_version.restype = ctypes.c_void_p
    library.mono_domain_assembly_open.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
    library.mono_domain_assembly_open.restype = ctypes.c_void_p
    library.mono_jit_exec.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int,
                                    ctypes.POINTER(ctypes.c_char_p)]
    library.mono_jit_exec.restype = ctypes.c_int
    library.mono_jit_cleanup.argtypes = [ctypes.c_void_p]
    domain = library.mono_jit_init_version(b"FrameFlux Mono smoke", b"v4.0.30319")
    if not domain:
        raise RuntimeError("Could not initialize Mono.")
    try:
        loaded = library.mono_domain_assembly_open(domain, str(assembly).encode())
        if not loaded:
            raise RuntimeError("Could not load test assembly.")
        arguments = [str(assembly), str(native), str(source)]
        argv = (ctypes.c_char_p * len(arguments))(*(value.encode() for value in arguments))
        return library.mono_jit_exec(domain, loaded, len(arguments), argv)
    finally:
        library.mono_jit_cleanup(domain)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity-editor", type=Path, required=True)
    parser.add_argument("--source", type=Path, required=True, help="Local fixture with video and audio")
    parser.add_argument("--checks", nargs="+", choices=["compiler", "mono", "il2cpp"],
                        default=["compiler", "mono", "il2cpp"])
    parser.add_argument("--mono-worker", type=Path, help=argparse.SUPPRESS)
    parser.add_argument("--native-directory", type=Path, help=argparse.SUPPRESS)
    args = parser.parse_args()
    data = args.unity_editor.resolve().parent / "Data"
    repository = Path(__file__).resolve().parent.parent
    package = repository / "artifacts/unity-managed/com.frameflux.player"
    native = (args.native_directory or package / "Runtime/Plugins/x86_64").resolve()
    if args.mono_worker:
        return mono_worker(data, args.mono_worker.resolve(), args.source.resolve(), native)
    if not args.unity_editor.is_file() or not args.source.is_file():
        raise RuntimeError("Unity executable and local audio/video fixture must exist.")
    managed = sorted((package / "Runtime/Plugins/Managed").glob("*.dll"))
    if not managed:
        raise RuntimeError("Run build-unity-package.ps1 first.")
    # Never reuse an IL2CPP input directory containing unstripped Editor engine assemblies.
    work = repository / ".verify/unity-backends" / uuid.uuid4().hex
    work.mkdir(parents=True)
    print(f"Check artifacts: {work}", flush=True)
    reference = data / "NetStandard/ref/2.1.0/netstandard.dll"
    visible = [p for p in managed if p.name not in ("Microsoft.Bcl.AsyncInterfaces.dll", "Tmds.LibC.dll")]
    if "compiler" in args.checks:
        ui = sorted((data / "Resources/PackageManager/ProjectTemplates/libcache").rglob("UnityEngine.UI.dll"))
        if not ui:
            raise RuntimeError("Unity template UI reference not found.")
        references = [reference, data / "Managed/UnityEditor.dll", ui[0]] + visible
        references += sorted((data / "Managed/UnityEngine").glob("*.dll"))
        component = compile_sources(data, work, "FrameFlux.Unity",
                                    sorted((package / "Runtime").glob("*.cs")), references,
                                    "UNITY_EDITOR,UNITY_STANDALONE_WIN,UNITY_2022_3")
        compile_sources(data, work, "UnitySmokeScripts",
                        sorted((repository / "unity/Tests~/Runtime").glob("*.cs")) +
                        sorted((repository / "unity/Tests~/Editor").glob("*.cs")) +
                        sorted((package / "Editor").glob("*.cs")) +
                        sorted((package / "Samples~/MediaViewControls").glob("*.cs")),
                        references + [component], "UNITY_EDITOR,UNITY_STANDALONE_WIN,UNITY_2022_3")
        print("PASS Unity bundled C# compiler (components, sample and acceptance harness)", flush=True)
    if "mono" in args.checks or "il2cpp" in args.checks:
        smoke = compile_sources(data, work, "MonoSmoke",
                                sorted((repository / "unity/Tests~/Backends").glob("*.cs")),
                                [reference] + visible, executable=True)
        for path in managed:
            shutil.copy2(path, work / path.name)
    if "mono" in args.checks:
        run([sys.executable, str(Path(__file__).resolve()), "--unity-editor", str(args.unity_editor.resolve()),
             "--source", str(args.source.resolve()), "--mono-worker", str(smoke),
             "--native-directory", str(native)], work / "mono.log", timeout=120)
        print("PASS bundled x64 Mono backend playback (not Unity Player)", flush=True)
    if "il2cpp" in args.checks:
        inputs, cpp = work / "il2cpp-input", work / "il2cpp-cpp"
        inputs.mkdir()
        profile = data / "MonoBleedingEdge/lib/mono/unityaot-win32"
        for path in sorted(profile.rglob("*.dll")):
            shutil.copy2(path, inputs / path.name)
        for path in managed:
            shutil.copy2(path, inputs / path.name)
        shutil.copy2(smoke, inputs / smoke.name)
        run([str(data / "il2cpp/build/deploy/il2cpp.exe"), "--convert-to-cpp",
             "--dotnetprofile=unityaot-win32", "--directory=" + str(inputs),
             "--generatedcppdir=" + str(cpp), "--emit-null-checks", "--enable-array-bounds-check",
             "--jobs=2"], work / "il2cpp.log", timeout=600)
        table = (cpp / "Il2CppReversePInvokeWrapperTable.cpp").read_text(encoding="utf-8")
        for callback in ["DirectFfmpegSession_HandleInterrupt", "FFmpegHardwareDecoderContext_SelectHardwareFormat",
                         "FfmpegWebRtcVideoDecoder_SelectHardwareFormat"]:
            if callback not in table:
                raise RuntimeError("Missing reverse P/Invoke registration: " + callback)
        print("PASS core IL2CPP C++ generation and callback registration (not Player build)", flush=True)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as exception:
        print(str(exception), file=sys.stderr)
        sys.exit(1)
