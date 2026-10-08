# FrameFlux FFmpeg Native Assets for Android

This package supplies FFmpeg 9.0.1's six shared libraries for arm64-v8a and
x86_64. Consumers must target Android API level 24 or later. The libraries
were built with NDK 27.3.13750724 and have 16 KB ELF LOAD alignment.

Packing validates every shipped library's ELF LOAD alignment. The older
32-bit assets in the repository are excluded from this package.

The package intentionally excludes FFmpegKit and avdevice libraries that are
not in FrameFlux's direct dependency closure. These libraries also do not
depend on libc++_shared.so, which is excluded.

The native libraries are LGPL-3.0-or-later, separately from FrameFlux's MIT
license. The package includes the license texts, corresponding FFmpeg and
cpu_features sources, FFmpegKitNext build scripts, local patches and binary
build records. See `source-build/android/README.md` for reproduction instructions.
