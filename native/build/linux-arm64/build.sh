#!/bin/sh
set -eu
./configure --target-os=linux --arch=aarch64 --enable-cross-compile \
  --cross-prefix=aarch64-linux-gnu- --enable-shared --disable-static \
  --enable-version3 --disable-programs --disable-doc --disable-debug \
  --disable-autodetect
make -j8
