# Р СљР С‘Р Р…Р С‘Р СР В°Р В»РЎРЉР Р…Р В°РЎРЏ РЎРѓР В±Р С•РЎР‚Р С”Р В° FFmpeg Р Т‘Р В»РЎРЏ Aura

`ffmpeg.exe` РЎРѓР С•Р В±РЎР‚Р В°Р Р… Р С‘Р В· Р С‘РЎРѓРЎвЂ¦Р С•Р Т‘Р Р…Р С‘Р С”Р С•Р Р† **FFmpeg 7.1.1** Р С—Р С•Р Т‘ Windows x64 (MinGW-w64, gcc 16.2).
Р РЋР В±Р С•РЎР‚Р С”Р В° **LGPL 2.1+**, Р В±Р ВµР В· GPL-Р С”Р С•Р СР С—Р С•Р Р…Р ВµР Р…РЎвЂљР С•Р Р† (libx264/libx265 Р Р…Р Вµ Р Р†Р С”Р В»РЎР‹РЎвЂЎР ВµР Р…РЎвЂ№).

Р вЂ”Р В°РЎвЂЎР ВµР С РЎРѓР Р†Р С•Р в„– Р В±Р С‘Р Р…Р В°РЎР‚Р Р…Р С‘Р С”: Windows Р В±Р ВµР В· HEVC-РЎР‚Р В°РЎРѓРЎв‚¬Р С‘РЎР‚Р ВµР Р…Р С‘РЎРЏ Р Р…Р Вµ Р Т‘Р ВµР С”Р С•Р Т‘Р С‘РЎР‚РЎС“Р ВµРЎвЂљ Р В·Р В°Р С—Р С‘РЎРѓР С‘ Aura
(HEVC Main10), Р В° Р С—Р С•Р В»Р Р…РЎвЂ№Р в„– ffmpeg Р Р†Р ВµРЎРѓР С‘РЎвЂљ ~100 Р СљР вЂ. Р вЂ”Р Т‘Р ВµРЎРѓРЎРЉ РЎвЂљР С•Р В»РЎРЉР С”Р С• Р Р…РЎС“Р В¶Р Р…Р С•Р Вµ:

- Р Т‘Р ВµР С”Р С•Р Т‘Р ВµРЎР‚РЎвЂ№: HEVC, H.264, AAC, MP3;
- РЎРЊР Р…Р С”Р С•Р Т‘Р ВµРЎР‚РЎвЂ№: `h264_nvenc`, `hevc_nvenc`, AAC, `mpeg4` (Р В·Р В°Р С—Р В°РЎРѓР Р…Р С•Р в„– Р В±Р ВµР В· GPU);
- Р Т‘Р ВµР СРЎС“Р С”РЎРѓР ВµРЎР‚РЎвЂ№: MP4/MOV, Matroska (MKV);
- Р СРЎС“Р С”РЎРѓР ВµРЎР‚: MP4;
- РЎвЂћР С‘Р В»РЎРЉРЎвЂљРЎР‚РЎвЂ№: scale, pad, setsar, fps, format, concat, color, setpts/asetpts,
  atrim, adelay, amix, volume, aresample, aformat, alimiter, apad, anullsrc, anull;
- Р С—РЎР‚Р С•РЎвЂљР С•Р С”Р С•Р В»РЎвЂ№: file, pipe.

Р ВРЎРѓРЎвЂ¦Р С•Р Т‘Р Р…Р С‘Р С”Р С‘: https://ffmpeg.org/releases/ffmpeg-7.1.1.tar.xz
NVENC headers: https://github.com/FFmpeg/nv-codec-headers

Р РЋР В±Р С•РЎР‚Р С”Р В°:

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

SHA-256: `5b99449d00892248b29f950e0e9072bde9ddf01fde1ee969bb7b7b773ec8762e`.
