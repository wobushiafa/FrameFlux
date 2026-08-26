# Native runtime artifacts

Place one complete FFmpeg build for each runtime under the NuGet runtime layout below. FrameFlux loads `avutil`, `swscale`, `avcodec`, and `avformat` directly; no `frameflux_ffmpeg` adapter library is required.

```text
native/artifacts/runtimes/
  win-x64/native/
    avcodec-61.dll
    avformat-61.dll
    avutil-59.dll
    swresample-5.dll
    swscale-8.dll
  linux-x64/native/
    libavcodec.so.62.30.100
    libavformat.so.62.13.102
    libavutil.so.60.30.100
    libswresample.so.6.1.100
    libswscale.so.9.7.100
  android-arm64/native/
    libavcodec.so
    libavformat.so
    libavutil.so
    libswresample.so
    libswscale.so
  android-arm/native/
    libavcodec_neon.so
    libavformat_neon.so
    libavutil_neon.so
    libswresample_neon.so
    libswscale_neon.so
```

The same pattern applies to `linux-arm64`, `osx-x64`, `osx-arm64`, `android-x64`, and `android-x86`. All files in one runtime directory must use the same architecture and come from the same FFmpeg build.

Demo projects set `FrameFluxCopyNativeAssets=true`, so the current host RID is copied automatically. Other local projects can opt in or set the RID explicitly:

```powershell
dotnet build -p:FrameFluxCopyNativeAssets=true -p:FrameFluxNativeRuntimeIdentifier=win-x64
```

Android `.so` files are packaged into the APK under the matching ABI directory. The Android target maps `android-arm`, `android-arm64`, `android-x86`, and `android-x64` to `lib/armeabi-v7a`, `lib/arm64-v8a`, `lib/x86`, and `lib/x86_64`.

