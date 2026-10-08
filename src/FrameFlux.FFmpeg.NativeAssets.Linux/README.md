# FrameFlux FFmpeg Native Assets for Linux

This package supplies the Linux x64 and ARM64 FFmpeg shared libraries used by
FrameFlux.FFmpeg. Reference it from the final application together with the
managed FrameFlux packages. Both architectures require glibc 2.35 or newer.

The package contains one matching FFmpeg 9 ABI family: avcodec 63, avformat 63,
avutil 61, avfilter 12, swscale 10, and swresample 7. Do not replace individual shared
libraries with files from another FFmpeg build.

FrameFlux calls the packaged FFmpeg exports directly; no additional adapter
library is required. The x64 build enables VAAPI and libdrm so hardware frames
can be exported as DRM PRIME. Target machines need libva, libva-drm, and libdrm
runtimes (libva 2.14+, libdrm and GnuTLS 3.7.3+), a working VAAPI driver, and access to a DRM render node, normally
under `/dev/dri`. The ARM64 build provides software decoding; hardware decoding
depends on a platform-specific FFmpeg build and driver. Its highest required
glibc symbol version is 2.35.

The native libraries are LGPL-3.0-or-later, separately from FrameFlux's MIT
license. The package includes corresponding FFmpeg source, LGPL/GPL license
texts, build recipes and records under `source` and `source-build`.
