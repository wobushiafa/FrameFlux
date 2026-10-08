# Linux ARM64 FFmpeg 9.0.1

The existing ARM64 libraries use the FFmpeg source archive included in this
package, commit `bf1b838f2ab88b4f8fd83443325c782ea0e0f7fa`. They were built with
`aarch64-linux-gnu-gcc` 15 and the Debian `libc6-arm64-cross` and
`libc6-dev-arm64-cross` 2.36-8cross1 sysroot. Their highest required GLIBC
symbol version is 2.35. Hardware acceleration is disabled in this build.

Install that cross toolchain and sysroot, extract the included FFmpeg archive,
and run `build.sh` from the extracted source directory. Keep the sysroot at
the recorded version when reproducing the glibc baseline.
