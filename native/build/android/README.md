# Existing Android FFmpeg 9.0.1 builds

The arm64-v8a and x86_64 libraries target API 24 and use Android NDK
27.3.13750724. Every packaged ELF LOAD segment has at least 16384-byte alignment.
The package excludes the older 32-bit libraries, FFmpegKit wrappers, avdevice,
and libc++_shared.so; none is needed by these six directly loaded FFmpeg libraries.

The included FFmpeg archive is commit
`bf1b838f2ab88b4f8fd83443325c782ea0e0f7fa` (n9.0.1). The included FFmpegKitNext
build-script archive is commit `5e51b2da4c3593c0f2f9b49f53eeb497d93e39d3`.
Extract the build scripts and apply `FFMPEG-KIT-NEXT-LOCAL.patch` from the
package root with `git apply --unidiff-zero`. Configure `ANDROID_SDK_ROOT`
and `ANDROID_NDK_ROOT` for API 24 and the recorded NDK, then use:

```sh
./android.sh --api-level=24 --disable-arm-v7a --disable-arm-v7a-neon --disable-x86
```

Use the included FFmpeg source at that revision for the builder's FFmpeg
source tree. The builder also uses the included cpu_features 0.11.0 source
(arthenica mirror, commit `81d13c49649f0714dd41fb56bb246398b6584085`) for its static
ndk_compat library, under the included Apache 2.0 license. The native configuration disables GPL, nonfree and
external codec libraries. `build-record.json` records the configure command
and SHA-256 of every shipped binary. Verify 16 KB LOAD alignment before
copying the six FFmpeg libraries from each API 24 output directory.
