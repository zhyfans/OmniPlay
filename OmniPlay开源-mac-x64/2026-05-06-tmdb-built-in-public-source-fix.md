# 2026-05-06 TMDB 内置公共源修复记录

## 问题

x64 mac 版设置中启用公共 TMDB API 后，如果未填写自定义 API，实际解析出的公共 API Key 为空字符串，导致测试与刮削都无法使用公共源。

ARM mac 版已经内置共享受限 TMDB API Key；x64 mac 版遗漏同步该配置。

## 修复

- 在 `OmniPlay/TMDBService.swift` 的 `TMDBAPIConfig.publicApiKey` 中补齐与 ARM mac 版一致的共享受限 TMDB API Key。
- 保持原有行为不变：用户填写自定义 API 时优先使用自定义 API；未填写且开启公共源时使用内置公共源。

## 验证

- 未填写自定义 API 且开启“公共 TMDB API”时，`TMDBAPIConfig.resolvedApiKey` 不再为空。
- 设置页测试和自动刮削会使用公共源发起 TMDB 请求。
- 执行 `xcodebuild -project OmniPlay.xcodeproj -scheme OmniPlay -configuration Release -sdk macosx -destination platform=macOS,arch=x86_64 -derivedDataPath /tmp/omniplay-mac-x64-release build`，Release 构建通过。
- 构建产物 `/tmp/omniplay-mac-x64-release/Build/Products/Release/觅影.app/Contents/MacOS/觅影` 已确认是 `Mach-O 64-bit executable x86_64`。
- 已重新生成 `dist/OmniPlay-mac-x64-2026-05-06.zip`，并通过 `unzip -t` 校验。
