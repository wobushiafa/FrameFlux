# FrameFlux FFmpeg Native Assets for Android

This package supplies the five FFmpeg components and libc++ runtime required by
FrameFlux for armeabi-v7a, arm64-v8a, x86, and x86_64. Consumers must target
Android API level 24 or later.

Packing is blocked when any ELF LOAD segment is aligned below 16 KB. The
currently checked-in assets are 4 KB builds, so they must be replaced before
publishing for Android 16. For temporary local testing only, the guard can be
bypassed with the FrameFluxAllowUnsupportedAndroidPageAlignment MSBuild
property set to true.

The package intentionally excludes FFmpegKit, avfilter, and avdevice libraries
that are not in FrameFlux's direct dependency closure.

Native FFmpeg, FFmpegKit, codec, and libc++ licensing is separate from the
managed FrameFlux source license. Review all upstream redistribution
requirements before publishing an application.
