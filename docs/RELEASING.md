# Releasing FrameFlux

Version 0.1.3 publishes 11 managed packages and three optional native FFmpeg
packages. Managed packages are MIT licensed; native package licenses are
recorded in THIRD-PARTY-NOTICES.md and their package license files.

## Validation and packaging

Run the full Release build and tests, including the appropriate native playback
smoke tests. Android binaries must pass 16 KB ELF LOAD alignment without an
override. Windows and Linux packages must contain a complete matching FFmpeg
ABI family and the recorded source/build artifacts.

```powershell
./eng/prepare-native-sources.ps1
dotnet build FrameFlux.slnx -c Release
dotnet test FrameFlux.slnx -c Release --no-build
./eng/pack-managed.ps1
```

The source preparation script downloads pinned archives and checks their SHA-256.
Native projects require corresponding source when packing. The publishing
workflow checks out Git LFS binaries, packs all 14 projects, and runs
eng/verify-packages.py to validate package identities, exact internal dependency
versions, licenses, sources, native architecture, Android alignment and Linux
glibc requirements. Examples must never appear as NuGet packages.

## Native runtime support

- Windows: x64, FFmpeg 9.0.1, D3D11VA/DXVA2 and Schannel.
- Linux: x64 and ARM64, glibc 2.35 or newer. x64 additionally requires libva,
  libva-drm, libdrm and GnuTLS; VAAPI needs a driver and accessible render node.
- Android: arm64-v8a and x86_64, API 24+, 16 KB aligned. Android native libraries
  are already built; publishing does not rebuild them. The 32-bit assets,
  libc++ and FFmpegKit wrappers are excluded from the native package.

## Publication

Update CHANGELOG.md and Directory.Build.props together. With user authorization,
commit and push the release, then push the matching v<version> tag. The
publish-nuget.yml workflow authenticates through NuGet trusted publishing and
uploads all packages and managed symbol packages. Confirm the workflow succeeds
and verify every package version on NuGet before reporting completion.

For external WebRTC smoke tests, set FRAMEFLUX_GO2RTC_URL to a reachable go2rtc
stream.html?src=... endpoint. Set FRAMEFLUX_RUN_NATIVE_HTTP_TESTS=1 and
FRAMEFLUX_FFMPEG_LIBRARY_DIR to run FFmpeg HTTP playback and seek tests; these
also require an ffmpeg executable on PATH.
