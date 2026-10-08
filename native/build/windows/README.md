# Windows x64 FFmpeg 9.0.1

The Dockerfile cross-compiles the same `arthenica/FFmpeg` revision used by
FrameFlux's FFmpeg 9.0.1 Android and Linux assets:
`bf1b838f2ab88b4f8fd83443325c782ea0e0f7fa` (`n9.0.1`). It builds the six FFmpeg
shared libraries directly; FrameFlux does not require the FFmpegKit wrapper.

The build enables D3D11VA, DXVA2, Windows threads and Schannel TLS for HTTPS.
It uses FFmpeg's built-in codecs and filters, disables external-library
autodetection, and does not enable GPL or nonfree components. `--enable-version3`
makes these libraries LGPL-3.0-or-later. The GCC runtime is linked statically.
The link search path also prefers the static winpthreads archive for MinGW's
`clock_gettime` and `nanosleep`, avoiding a `libwinpthread-1.dll` dependency.

From the repository root, with Docker available:

```sh
docker build -t frameflux-ffmpeg9-win-x64 native/build/windows
mkdir -p .verify/windows-ffmpeg9/output
docker run --rm \
  -v "$(pwd)/.verify/windows-ffmpeg9/output:/export" \
  frameflux-ffmpeg9-win-x64 \
  sh -c 'cp -a /out/. /export/'
```

On this Windows workstation, run these commands in the existing
`Ubuntu-24.04` WSL distribution, from `/mnt/d/CodeRepository/FrameFlux`.
Build tools are installed inside the image, without changing the host toolchain.

If GitHub is slow from Docker, download the pinned source archive through the
host's existing connection, place it as `ffmpeg-source.tar.gz` alongside a copy
of the Dockerfile in a temporary build context, and build that context instead.
The Dockerfile accepts the cached archive and checks its pinned SHA-256:
`fb1931fd4eb29297ee1c1017a24f800c4d8fbea35b4f2aaeb28308a48a9149b4`.

The DLLs are in `output/bin`. The output also records the configure log,
generated configuration header, compiler version, source revision, and PE
import/export information. Validate the DLLs on Windows before replacing the
complete set under `native/artifacts/runtimes/win-x64/native`.

The matching ABI family is avcodec/avformat 63, avutil 61, swscale 10,
swresample 7 and avfilter 12. Do not mix DLLs from different builds.
