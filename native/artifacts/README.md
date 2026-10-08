# Native runtime artifacts

Place one complete FFmpeg build for each runtime under the NuGet runtime layout below. FrameFlux loads `avutil`, `swscale`, `avcodec`, and `avformat` directly; no `frameflux_ffmpeg` adapter library is required.

```text
native/artifacts/runtimes/
  win-x64/native/
    avcodec-63.dll
    avfilter-12.dll
    avformat-63.dll
    avutil-61.dll
    swresample-7.dll
    swscale-10.dll
  linux-x64/native/
    libavcodec.so.63
    libavfilter.so.12
    libavformat.so.63
    libavutil.so.61
    libswresample.so.7
    libswscale.so.10
  linux-arm64/native/
    libavcodec.so.63
    libavfilter.so.12
    libavformat.so.63
    libavutil.so.61
    libswresample.so.7
    libswscale.so.10
  android-arm64/native/
    libavcodec.so
    libavfilter.so
    libavformat.so
    libavutil.so
    libswresample.so
    libswscale.so
  android-x64/native/
    libavcodec.so
    libavfilter.so
    libavformat.so
    libavutil.so
    libswresample.so
    libswscale.so
```

All files in one runtime directory must use the same architecture and come from
the same FFmpeg build. The published Android package contains arm64 and x64
libraries with 16 KB ELF LOAD alignment. Older 32-bit and wrapper libraries are
excluded. Linux packages require glibc 2.35 or newer. Source and build records
are included in each native package.

Demo projects set `FrameFluxCopyNativeAssets=true`, so the current host RID is copied automatically. Other local projects can opt in or set the RID explicitly:

```powershell
dotnet build -p:FrameFluxCopyNativeAssets=true -p:FrameFluxNativeRuntimeIdentifier=win-x64
```

Android `.so` files are packaged into the APK under the matching ABI directory. The Android target maps `android-arm`, `android-arm64`, `android-x86`, and `android-x64` to `lib/armeabi-v7a`, `lib/arm64-v8a`, `lib/x86`, and `lib/x86_64`.
