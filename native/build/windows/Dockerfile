FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

ARG DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential ca-certificates git nasm pkg-config \
    gcc-mingw-w64-x86-64-posix binutils-mingw-w64-x86-64 \
    mingw-w64-x86-64-dev

# The same FFmpeg revision used by the Android and Linux 9.0.1 assets.
ARG FFMPEG_REVISION=bf1b838f2ab88b4f8fd83443325c782ea0e0f7fa
ARG FFMPEG_SOURCE_SHA256=fb1931fd4eb29297ee1c1017a24f800c4d8fbea35b4f2aaeb28308a48a9149b4
COPY . /build-input/
RUN if [ -f /build-input/ffmpeg-source.tar.gz ]; then \
        cp /build-input/ffmpeg-source.tar.gz /tmp/ffmpeg-source.tar.gz; \
    else \
        curl --fail --location --retry 3 --connect-timeout 15 --max-time 900 \
        "https://codeload.github.com/arthenica/FFmpeg/tar.gz/${FFMPEG_REVISION}" \
        -o /tmp/ffmpeg-source.tar.gz; \
    fi \
    && printf '%s  %s\n' "$FFMPEG_SOURCE_SHA256" /tmp/ffmpeg-source.tar.gz | sha256sum -c - \
    && mkdir -p /build/ffmpeg \
    && tar -xzf /tmp/ffmpeg-source.tar.gz --strip-components=1 -C /build/ffmpeg

WORKDIR /build/ffmpeg
RUN ./configure \
    --prefix=/out \
    --target-os=mingw32 --arch=x86_64 --enable-cross-compile \
    --cross-prefix=x86_64-w64-mingw32- \
    --cc=x86_64-w64-mingw32-gcc-posix \
    --enable-shared --disable-static --enable-version3 \
    --disable-programs --disable-doc --disable-debug --disable-avdevice \
    --disable-autodetect --enable-w32threads --enable-network \
    --enable-d3d11va --enable-dxva2 --enable-schannel \
    --extra-ldflags=-static-libgcc \
    && mkdir -p /static-runtime \
    && cp "$(x86_64-w64-mingw32-gcc-posix -print-file-name=libwinpthread.a)" /static-runtime/libwinpthread.a \
    && cp /static-runtime/libwinpthread.a /static-runtime/libpthread.a \
    && sed -i 's|^LDFLAGS=|LDFLAGS= -L/static-runtime |' ffbuild/config.mak \
    && make -j8 \
    && make install \
    && cp ffbuild/config.log /out/config.log \
    && cp config.h /out/config.h \
    && cp ffbuild/config.mak /out/config.mak \
    && cp COPYING.GPLv3 COPYING.LGPLv3 /out/ \
    && cp /usr/share/doc/mingw-w64-common/copyright /out/MINGW-W64-NOTICES.txt \
    && printf '%s\n' "$FFMPEG_REVISION" > /out/source-revision.txt \
    && sha256sum /tmp/ffmpeg-source.tar.gz > /out/source-archive-sha256.txt \
    && cp /tmp/ffmpeg-source.tar.gz /out/ffmpeg-source.tar.gz \
    && x86_64-w64-mingw32-gcc-posix --version > /out/compiler-version.txt \
    && for dll in /out/bin/*.dll; do \
         x86_64-w64-mingw32-objdump -p "$dll" > "$dll.pe.txt"; \
       done

CMD ["/bin/true"]
