# 2026-05-06 Plex / Emby / Jellyfin 预扫描功能记录

## 新增功能

- 添加 Plex / Emby / Jellyfin 弹窗新增“预扫描”按钮。
- 点击“预扫描”会使用当前服务器地址、访问令牌和 UserId 枚举电影/剧集条目，并显示发现数量和示例文件名。
- 点击“保存并扫描”前会自动先执行一次预扫描；预扫描失败时不会保存媒体源，避免无效配置进入媒体库。

## 实现说明

- 新增 `MediaServerPreflightChecker`。
- 预扫描复用 `MediaServerScanner`，与正式扫描使用同一套 Plex / Emby / Jellyfin 枚举逻辑。
- Plex 使用 `X-Plex-Token`，Emby/Jellyfin 使用 `api_key`。
- Emby/Jellyfin 继续支持可选 UserId。

## 验证

- `xcodebuild -project OmniPlay开源-mac-x64/OmniPlay.xcodeproj -scheme OmniPlay -configuration Debug -sdk macosx -destination "platform=macOS,arch=x86_64" build` 通过。
