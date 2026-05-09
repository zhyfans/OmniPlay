# 2026-05-06 本地刮削文件与媒体服务器源功能记录

## 新增功能

- 设置页新增“读取本地 NFO、海报和剧照”开关，默认关闭。
- 设置页新增“刮削完成后保存 NFO、海报和剧照到本地”开关，默认关闭。
- 媒体库新增 Plex、Emby、Jellyfin 源入口。

## 本地刮削文件

- 读取仅对本地文件源生效，WebDAV、Plex、Emby、Jellyfin 不会读取或写入本地 sidecar 文件。
- 支持读取同名 `.nfo`、`movie.nfo`、`tvshow.nfo`。
- 支持读取电影海报：同名图片、`同名-poster`、`poster`、`folder`、`cover`。
- 支持读取剧集剧照：同名图片、`同名-thumb`、`同名-thumbnail`。
- 支持图片扩展名：`.jpg`、`.jpeg`、`.png`、`.webp`。
- 从本地 NFO 成功导入的条目会锁定已导入元数据，避免后续自动 TMDB 刮削覆盖本地信息。
- 开启导出后，TMDB 刮削完成会写出电影同名 `.nfo`、剧集目录 `tvshow.nfo`、海报和剧集 `同名-thumb` 图片。

## Plex / Emby / Jellyfin

- 新增源只作为媒体文件传输和枚举入口。
- 播放解码仍由 OmniPlay 本地播放器完成。
- 刮削仍由 OmniPlay 客户端使用 TMDB 完成，不使用 Plex、Emby、Jellyfin 的媒体库元数据替代 TMDB。
- Plex 请求使用 `X-Plex-Token`，Emby/Jellyfin 请求使用 `api_key`。
- Emby/Jellyfin 可填写 UserId；填写后按用户媒体库枚举，未填写则按全局 `Items` 接口枚举。

## 验证

- `xcodebuild -project OmniPlay开源-mac-ARM/OmniPlay.xcodeproj -scheme OmniPlay -configuration Debug -sdk macosx build` 通过。
