# FrameFlux FFmpeg Native Assets for Linux

This package supplies the Linux x64 FFmpeg shared libraries used by
FrameFlux.FFmpeg. Reference it from the final application together with the
managed FrameFlux packages.

The package contains one matching FFmpeg 9 ABI family: avcodec 63, avformat 63,
avutil 61, avfilter 12, swscale 10, and swresample 7. Do not replace individual shared
libraries with files from another FFmpeg build.

FrameFlux calls the packaged FFmpeg exports directly for VAAPI initialization;
no additional adapter library is required. The Linux build enables VAAPI and
libdrm so hardware frames can be exported as DRM PRIME. Target machines need
libva, libva-drm, and libdrm runtimes, a working VAAPI driver, and access to a
DRM render node, normally under `/dev/dri`.

Native FFmpeg and codec licensing is separate from the managed FrameFlux
source license. Review the upstream build configuration and redistribution
requirements before publishing an application.
