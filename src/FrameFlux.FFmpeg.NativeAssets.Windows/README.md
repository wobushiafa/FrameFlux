# FrameFlux FFmpeg Native Assets for Windows

This package supplies the Windows x64 FFmpeg shared libraries used by
FrameFlux.FFmpeg. Reference it from the final application together with the
managed FrameFlux packages.

The package contains one matching FFmpeg 9.0.1 ABI family: avcodec 63,
avformat 63, avutil 61, swscale 10, swresample 7 and avfilter 12. Do not replace
individual DLLs with files from another FFmpeg build.

The Windows x64 build enables D3D11VA, DXVA2 and Schannel TLS, uses FFmpeg's
built-in codecs and filters, and is LGPL-3.0-or-later. Its pinned source revision
and Docker build instructions are included under `source-build/windows`.
The corresponding FFmpeg source archive is included under `source`; the
package also includes full LGPL/GPL license texts and MinGW runtime notices.

Native FFmpeg and codec licensing is separate from the managed FrameFlux
source license. Review the upstream build configuration and redistribution
requirements before publishing an application.
