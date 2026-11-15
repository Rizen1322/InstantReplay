# РњРёРЅРёРјР°Р»СЊРЅР°СЏ СЃР±РѕСЂРєР° FFmpeg РґР»СЏ Aura

`ffmpeg.exe` СЃРѕР±СЂР°РЅ РёР· РёСЃС…РѕРґРЅРёРєРѕРІ **FFmpeg 7.1.1** РїРѕРґ Windows x64 (MinGW-w64, gcc 16.2).
РЎР±РѕСЂРєР° **LGPL 2.1+**, Р±РµР· GPL-РєРѕРјРїРѕРЅРµРЅС‚РѕРІ (libx264/libx265 РЅРµ РІРєР»СЋС‡РµРЅС‹).

Р—Р°С‡РµРј СЃРІРѕР№ Р±РёРЅР°СЂРЅРёРє: Windows Р±РµР· HEVC-СЂР°СЃС€РёСЂРµРЅРёСЏ РЅРµ РґРµРєРѕРґРёСЂСѓРµС‚ Р·Р°РїРёСЃРё Aura
(HEVC Main10), Р° РїРѕР»РЅС‹Р№ ffmpeg РІРµСЃРёС‚ ~100 РњР‘. Р—РґРµСЃСЊ С‚РѕР»СЊРєРѕ РЅСѓР¶РЅРѕРµ:

- РґРµРєРѕРґРµСЂС‹: HEVC, H.264, AAC, MP3;
- СЌРЅРєРѕРґРµСЂС‹: `h264_nvenc`, `hevc_nvenc`, AAC, `mpeg4` (Р·Р°РїР°СЃРЅРѕР№ Р±РµР· GPU);
- РґРµРјСѓРєСЃРµСЂС‹: MP4/MOV, Matroska (MKV);
- РјСѓРєСЃРµСЂ: MP4;
- С„РёР»СЊС‚СЂС‹: scale, pad, setsar, fps, format, concat, color, setpts/asetpts,
  atrim, adelay, amix, volume, aresample, aformat, alimiter, apad, anullsrc, anull;
- РїСЂРѕС‚РѕРєРѕР»С‹: file, pipe.

РСЃС…РѕРґРЅРёРєРё: https://ffmpeg.org/releases/ffmpeg-7.1.1.tar.xz
NVENC headers: https://github.com/FFmpeg/nv-codec-headers

РЎР±РѕСЂРєР°:

```sh
./configure --target-os=mingw32 --arch=x86_64 --disable-x86asm \
  --disable-doc --disable-debug --disable-network --disable-autodetect \
  --disable-everything --enable-ffmpeg --enable-avcodec --enable-avformat \
  --enable-avfilter --enable-swscale --enable-swresample \
  --enable-protocol=file,pipe --enable-demuxer=mov,matroska --enable-muxer=mp4 \
  --enable-decoder=hevc,h264,aac,mp3 --enable-encoder=h264_nvenc,hevc_nvenc,aac,mpeg4 \
  --enable-parser=hevc,h264,aac \
  --enable-filter=scale,fps,format,pad,setsar,concat,amix,volume,adelay,atrim,apad,anullsrc,alimiter,aresample,anull,color,setpts,asetpts \
  --enable-bsf=hevc_mp4toannexb,h264_mp4toannexb,aac_adtstoasc \
  --enable-ffnvcodec --enable-nvenc --extra-cflags=-O2
```

SHA-256: `97246fcc6831afe73eb22167d63359adeead078c82aecea6bd26c242d1157921`.
