# FrameFlux FFmpeg Native Assets for Linux

This package supplies the Linux x64 FFmpeg shared libraries used by
FrameFlux.FFmpeg. Reference it from the final application together with the
managed FrameFlux packages.

The package contains one matching FFmpeg 8 ABI family: avcodec 62, avformat 62,
avutil 60, swscale 9, and swresample 6. Do not replace individual shared
libraries with files from another FFmpeg build.

FrameFlux calls the packaged FFmpeg exports directly for VAAPI initialization;
no additional adapter library is required. Target machines need a working
VAAPI driver and access to a DRM render node, normally under `/dev/dri`.

Native FFmpeg and codec licensing is separate from the managed FrameFlux
source license. Review the upstream build configuration and redistribution
requirements before publishing an application.
