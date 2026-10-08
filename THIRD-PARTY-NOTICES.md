# Third-Party Notices

FrameFlux source and managed packages are MIT licensed. Native packages carry
separate third-party licenses; FrameFlux's MIT license does not replace them.
Managed dependency versions are recorded in Directory.Packages.props and the
NuGet dependency groups, including Avalonia, SIPSorcery, FFmpeg.AutoGen and NAudio.

## FFmpeg 9.0.1 native packages

All three native packages use FFmpeg n9.0.1 from arthenica/FFmpeg, commit
bf1b838f2ab88b4f8fd83443325c782ea0e0f7fa. The configurations enable version3
without GPL or nonfree components. The six FFmpeg libraries are
LGPL-3.0-or-later. Each package includes the corresponding source archive,
LGPL and GPL texts, provenance, reproduction instructions and build records.
The GPL text is included because the LGPL incorporates GPL terms; it does
not mean these builds enable FFmpeg's GPL-only components.

- Windows x64: MinGW-w64 GCC 13.2, D3D11VA/DXVA2 and Schannel. MinGW runtime
  notices are included. FFmpegKit and external codecs are not bundled.
- Linux x64: Ubuntu 22.04, GCC 11.4, glibc 2.35 baseline, VAAPI, libdrm and
  GnuTLS. libva, libva-drm, libdrm and GnuTLS are dynamic system dependencies.
- Linux ARM64: software-decoding build using GCC 15 and the Debian
  2.36-8cross1 cross sysroot; highest required glibc symbol version is 2.35.
- Android arm64/x64: NDK 27.3.13750724, API 24, FFmpegKitNext 9.0.0 build
  scripts at commit 5e51b2da4c3593c0f2f9b49f53eeb497d93e39d3 with the included
  local patch. Corresponding FFmpegKitNext scripts and cpu_features 0.11.0
  source are included. The static ndk_compat component is Apache 2.0 licensed;
  its license is included. All packaged ELF LOAD segments meet 16 KB alignment.

See native/licenses/FFMPEG-SOURCE.txt, native/build, and the package's source
and source-build directories for exact configurations and checksums. Runtime
libraries remain shared so applications can replace them with compatible builds.
Application distributors must retain the relevant notices and meet the upstream
licenses' requirements for their distribution.

## Historical assets

The earlier Windows/Linux builds enabled GPL and version3 and must be treated
as GPLv3-or-later. They are replaced by the FFmpeg 9 packages described above.
Older Android 32-bit and wrapper libraries remain in the repository for history
but are excluded from the published Android package, as is libc++_shared.so.
