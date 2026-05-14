Bundled FFmpeg runtime for OmniPlay Synology x86_64 packages.

Source:
https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linux64-gpl-shared.tar.xz

Downloaded archive SHA-256:
b6c0c2a520822eea430f3d5a5e4bef133a28ac077502948458277ef977911774

Package notes:
- This directory is copied into the SPK payload as tools/ffmpeg.
- ffmpeg, ffprobe, their required FFmpeg shared libraries, the VAAPI runtime,
  and Intel VAAPI/oneVPL user-space runtime files are bundled.
- The shared libraries are stored under their runtime SONAME names to avoid
  duplicate files from symlink expansion in the SPK tar writer.
- This is a GPL FFmpeg build. Keep LICENSE.txt with redistributed packages.

Additional VAAPI runtime packages already bundled under lib/:
- libva-drm2_2.17.0-1_amd64.deb
- libva2_2.17.0-1_amd64.deb
- libdrm2_2.4.114-1+b1_amd64.deb

Source:
https://deb.debian.org/debian/pool/main/

Intel GPU user-space runtime packages already bundled under lib/ and lib/dri/:
- intel-media-va-driver_23.1.1+dfsg1-1_amd64.deb
  SHA-256: c4df8a55dd645ca67caad7a5c3c1a7cfe363bd16e9a48393dfc4a2b1742b5388
  Bundled file: lib/dri/iHD_drv_video.so
- libigdgmm12_22.3.3+ds1-1_amd64.deb
  SHA-256: 9591d6be5d624422ae58b9bba0e013bd6dbdd1f1420d60de4eb2cd1186f2f44a
  Bundled file: lib/libigdgmm.so.12
- libvpl2_2023.1.1-1_amd64.deb
  SHA-256: 37e9a5add609ced0a6e200a310633f95c338218199e7426c8bef99fb19aeb81e
  Bundled file: lib/libvpl.so.2
- libmfx-gen1.2_22.6.4-1_amd64.deb
  SHA-256: e361f6428017c6befce2fb3fa6b00017a4d69119bf19b4f7f1094cb0a59e0a2c
  Bundled file: lib/libmfx-gen.so.1.2

The Intel runtime is used by Synology startup scripts through:
- LD_LIBRARY_PATH=$PACKAGE_TARGET/tools/ffmpeg/lib
- LIBVA_DRIVERS_PATH=$PACKAGE_TARGET/tools/ffmpeg/lib/dri
- LIBVA_DRIVER_NAME=iHD when iHD_drv_video.so is present
- ONEVPL_SEARCH_PATH=$PACKAGE_TARGET/tools/ffmpeg/lib

Copyright notices from the Debian packages are stored in:
share/doc/intel-runtime/

Additional X11/libva-x11 runtime packages bundled under lib/:
- libx11-6_1.8.4-2+deb12u2_amd64.deb
  SHA-256: d88c973e79fd9b65838d77624142952757e47a6eb1a58602acf0911cf35989f4
  Bundled file: lib/libX11.so.6
- libx11-xcb1_1.8.4-2+deb12u2_amd64.deb
  SHA-256: f5da45e1d881a793250a96613f28c471a248877f1a0f18a5c90e2a620a76c898
  Bundled file: lib/libX11-xcb.so.1
- libva-x11-2_2.17.0-1_amd64.deb
  SHA-256: 95db1ecd8c2d1c3f99a750f1b2d9ba1959b7cd9cbd7eabfc1b8a86a095b379a6
  Bundled file: lib/libva-x11.so.2
- libxcb1_1.15-1_amd64.deb
  SHA-256: fdc61332a3892168f3cc9cfa1fe9cf11a91dc3e0acacbc47cbc50ebaa234cc71
  Bundled file: lib/libxcb.so.1
- libxcb-dri3-0_1.15-1_amd64.deb
  SHA-256: 02699b144b9467de8636d27a76984b8f4e7b66e2d25d96df2b9677be86ee9a29
  Bundled file: lib/libxcb-dri3.so.0
- libxau6_1.0.9-1_amd64.deb
  SHA-256: 679db1c4579ec7c61079adeaae8528adeb2e4bf5465baa6c56233b995d714750
  Bundled file: lib/libXau.so.6
- libxdmcp6_1.1.2-3_amd64.deb
  SHA-256: ecb8536f5fb34543b55bb9dc5f5b14c9dbb4150a7bddb3f2287b7cab6e9d25ef
  Bundled file: lib/libXdmcp.so.6
- libxext6_1.3.4-1+b1_amd64.deb
  SHA-256: 504b7be9d7df4f6f4519e8dd4d6f9d03a9fb911a78530fa23a692fba3058cba6
  Bundled file: lib/libXext.so.6
- libxfixes3_6.0.0-2_amd64.deb
  SHA-256: 1cd616396ff2ecae77e6e8b5b7695d414f0146de2d147837a2a02165f99e1a2c
  Bundled file: lib/libXfixes.so.3
- libbsd0_0.11.7-2_amd64.deb
  SHA-256: bb31cc8b40f962a85b2cec970f7f79cc704a1ae4bad24257a822055404b2c60b
  Bundled file: lib/libbsd.so.0
- libmd0_1.0.4-2_amd64.deb
  SHA-256: 03539fd30c509e27101d13a56e52eda9062bdf1aefe337c07ab56def25a13eab
  Bundled file: lib/libmd.so.0

Copyright notices from the Debian packages are stored in:
share/doc/x11-runtime/
