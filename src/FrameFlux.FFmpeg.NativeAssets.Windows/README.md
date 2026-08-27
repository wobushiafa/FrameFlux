# FrameFlux FFmpeg Native Assets for Windows

This package supplies the Windows x64 FFmpeg shared libraries used by
FrameFlux.FFmpeg. Reference it from the final application together with the
managed FrameFlux packages.

The package contains one matching FFmpeg 7 ABI family: avcodec 61, avformat 61,
avutil 59, swscale 8, and swresample 5. Do not replace individual DLLs with
files from another FFmpeg build.

Native FFmpeg and codec licensing is separate from the managed FrameFlux
source license. Review the upstream build configuration and redistribution
requirements before publishing an application.
