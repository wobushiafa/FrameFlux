# Linux x64 FFmpeg 9.0.1

This build uses Ubuntu 22.04 so the Linux x64 libraries require at most glibc
2.35, matching the existing ARM64 package baseline. It enables VAAPI, libdrm
and GnuTLS for hardware decoding, DRM PRIME export and HTTPS input. The six
libraries are LGPL-3.0-or-later; GPL and nonfree components are not enabled.

The source revision and archive checksum match the Windows build. From the
repository root, with Docker available:

```sh
docker build -t frameflux-ffmpeg9-linux-x64 native/build/linux
mkdir -p .verify/linux-ffmpeg9/output
docker run --rm -v "$(pwd)/.verify/linux-ffmpeg9/output:/export" \
  frameflux-ffmpeg9-linux-x64 sh -c 'cp -a /out/. /export/'
```

An optional cached `ffmpeg-source.tar.gz` can be supplied alongside a copy of
the Dockerfile in a temporary build context; its SHA-256 is checked before use.
Copy the six versioned SONAME files from `output/lib`, dereferencing symlinks,
to `native/artifacts/runtimes/linux-x64/native` after validation.

Target machines need glibc 2.35 or newer, libva 2.14 or newer, libva-drm,
libdrm and GnuTLS 3.7.3 or newer. VAAPI additionally requires a driver and an
accessible DRM render node. The ARM64 libraries remain a software-decoding
build, with the configuration recorded in `FFMPEG-SOURCE.txt`.
