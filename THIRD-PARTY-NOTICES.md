# Third-Party Notices

FrameFlux depends on third-party managed libraries and can redistribute optional native FFmpeg builds. Package consumers remain responsible for reviewing the licenses that apply to their chosen dependency graph and deployment.

FrameFlux source code and managed packages are licensed under the MIT License. That license does not replace or modify the licenses of any third-party dependency or native binary.

## Managed dependencies

The managed packages reference components including Avalonia, Avalonia UI platform packages, FFmpeg.AutoGen, Microsoft.Extensions.DependencyInjection.Abstractions, and System.Collections.Immutable. Exact versions are defined in `Directory.Packages.props` and emitted into each NuGet package dependency group.

Before release, the license and notice requirements for each resolved managed dependency must be reviewed against the shipped package contents.

## Windows and Linux FFmpeg binaries

The FFmpeg binaries currently stored in this repository report builds configured with `--enable-gpl` and `--enable-version3`. They must therefore be treated as GPLv3-or-later builds unless the original build records establish a different result.

Do not publish `FrameFlux.FFmpeg.NativeAssets.Windows` or `FrameFlux.FFmpeg.NativeAssets.Linux` until all of the following are recorded and supplied as required by the applicable licenses:

- Exact upstream source revision and patches.
- Complete configure command and reproducible build instructions.
- Corresponding source distribution or a legally sufficient source offer.
- Full applicable license texts and copyright notices.
- Licenses and source obligations for every enabled external codec or library.

## Android FFmpeg binaries

The Android assets include FFmpeg-family shared libraries and `libc++_shared.so`. Their exact FFmpegKit or other build origin, upstream revisions, patches, configure flags, NDK version, and applicable licenses must be recorded before redistribution.

The older Android 32-bit binaries use 4 KB ELF LOAD alignment. They are excluded from the FFmpeg 9 local package candidate and require aligned replacements before 32-bit distribution.

## FFmpeg 9 local package candidates

The Linux x64 and Android arm64/x64 native package candidates use FFmpeg 9.0.1 built by FFmpegKitNext 9.0.0 from source commit `5e51b2da4c3593c0f2f9b49f53eeb497d93e39d3`. The FFmpeg source tag is `n9.0.1`. The Linux binary reports `--enable-version3`, no `--enable-gpl`, and `LGPL version 3 or later`. The package candidates include the LGPL text and a source provenance note under `native/licenses`.

These locally packed files are not approved for public distribution yet. The source note identifies upstream sources but does not itself provide the corresponding source, patches, and build instructions required for redistribution. The Linux binary also requires glibc 2.43, so it is unsuitable for a general `linux-x64` release. The Android package candidate includes only arm64 and x64: the 32-bit FFmpeg 9 outputs have 4 KB ELF LOAD alignment.

This notice is an engineering inventory. It does not by itself satisfy any third-party license obligation and is not legal advice.
