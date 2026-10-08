FROM public.ecr.aws/ubuntu/ubuntu:22.04

ARG DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential ca-certificates curl nasm pkg-config python3 \
    libva-dev libdrm-dev libgnutls28-dev

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
RUN ./configure --prefix=/out --target-os=linux --arch=x86_64 \
    --enable-shared --disable-static --enable-version3 \
    --disable-programs --disable-doc --disable-debug --disable-avdevice \
    --disable-autodetect --enable-pthreads --enable-network \
    --enable-vaapi --enable-libdrm --enable-gnutls \
    && make -j8 && make install \
    && cp ffbuild/config.log ffbuild/config.mak config.h /out/ \
    && cp COPYING.GPLv3 COPYING.LGPLv3 /out/ \
    && printf '%s\n' "$FFMPEG_REVISION" > /out/source-revision.txt \
    && gcc --version > /out/compiler-version.txt \
    && for lib in /out/lib/*.so; do \
         readelf -d -V "$lib" > "$lib.elf.txt"; \
       done

CMD ["/bin/true"]
