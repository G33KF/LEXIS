#!/bin/sh
# Builds ffmpeg.exe (x64, static, LGPL) with only what speech to text and video frames need: decoding Opus, AMR-NB/WB,
# AAC, ALAC, MP3, Vorbis and FLAC from Ogg, AMR, MP4/M4A, MP3, ADTS, WAV, Matroska/WebM, CAF and FLAC files, resampling,
# writing WAV; decoding H.264, HEVC, MPEG-4, H.263, VP8, VP9 and MJPEG video from MP4/MOV/3GP, Matroska/WebM and AVI,
# scaling, writing JPEG frames.
# Only needed to rebuild it: the result is committed as src/AleappReader.Core/native/ffmpeg.exe.
# Run in an MSYS2 UCRT64 shell (pacman -S make diffutils mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-nasm):
#   sh tools/ffmpeg/build.sh
# Source: FFmpeg n9.0.2 (github.com/FFmpeg/FFmpeg, the tag's tarball, SHA-256 below). Licence: LGPL 2.1 or later.
set -e
TAG=n9.0.2
SHA=6e374ed621e48faa40639307dff48ba6fe574a509977956d2cce9669b7cc27e9
cd "$(dirname "$0")"
mkdir -p src
[ -f src/$TAG.tar.gz ] || curl -fL -o src/$TAG.tar.gz https://github.com/FFmpeg/FFmpeg/archive/refs/tags/$TAG.tar.gz
echo "$SHA  src/$TAG.tar.gz" | sha256sum -c -
rm -rf src/FFmpeg-$TAG && tar -xzf src/$TAG.tar.gz -C src
cd src/FFmpeg-$TAG
./configure --target-os=mingw32 --arch=x86_64 --enable-static --disable-shared --enable-small \
  --disable-everything --disable-autodetect --disable-network --disable-doc --disable-debug \
  --disable-ffplay --disable-ffprobe --disable-avdevice \
  --enable-protocol=file,pipe \
  --enable-demuxer=ogg,amr,amrnb,amrwb,mov,mp3,aac,wav,matroska,caf,flac,avi \
  --enable-decoder=opus,amrnb,amrwb,aac,aac_latm,alac,mp3,mp3float,vorbis,flac,pcm_s16le,pcm_s24le,pcm_f32le,pcm_alaw,pcm_mulaw,h264,hevc,mpeg4,h263,vp8,vp9,mjpeg \
  --enable-parser=opus,aac,aac_latm,mpegaudio,vorbis,flac,h264,hevc,mpeg4video,h263,vp8,vp9 \
  --enable-muxer=wav,image2 --enable-encoder=pcm_s16le,mjpeg \
  --enable-filter=aresample,aformat,anull,atrim,scale,format,null \
  --enable-swresample --enable-swscale \
  --extra-ldflags=-static --pkg-config-flags=--static
make -j"$(nproc)"
strip ffmpeg.exe
cp ffmpeg.exe ../../../../src/AleappReader.Core/native/ffmpeg.exe
sha256sum ../../../../src/AleappReader.Core/native/ffmpeg.exe
