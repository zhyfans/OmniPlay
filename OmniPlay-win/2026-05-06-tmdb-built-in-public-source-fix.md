# 2026-05-06 TMDB 内置公共源修复记录

## 问题

Windows 版设置页在未填写自定义 TMDB API、勾选“内置公共 TMDB 源”后点击测试，仍提示“未启用内置公共 TMDB 源，也未填写自定义 API Key”。

排查后确认 UI 勾选状态已经正确传入 `TmdbSettings.EnableBuiltInPublicSource`，问题出在后端内置公共源使用的 `DefaultApiKey` 为空字符串。也就是说设置项没有丢失，但公共源凭据解析后仍被判定为未配置。

## 修复

- 在 `src/OmniPlay.Infrastructure/Tmdb/TmdbMetadataClient.cs` 中补齐与 ARM mac 版一致的共享受限 TMDB API Key。
- 保持原有优先级不变：自定义 Access Token > 自定义 API Key > 环境变量 > 内置公共受限源。
- 未填写自定义 API 且启用内置公共源时，测试连接会使用内置公共受限源请求 TMDB `/configuration`。

## 验证

- 覆盖现有 `TmdbConnectionTesterTests.TestConnectionAsync_IdentifiesBuiltInPublicSourceAsRestricted` 用例：空自定义 API + 启用内置公共源应返回成功并提示“内置公共受限源”。
- 覆盖公共源限制相关用例：内置公共源会走受限 fan-out 和轻量刮削路径。
- 执行 `dotnet test OmniPlay-win/OmniPlay.Windows.slnx --filter "FullyQualifiedName~SettingsViewModelTests|FullyQualifiedName~TmdbConnectionTesterTests|FullyQualifiedName~TmdbMetadataClientRestrictionsTests"`，27 个相关用例通过。
- 已重新生成 `dist/OmniPlay-win-x64-portable-2026-05-06.zip` 和 `dist/览影-OmniPlay-x64-setup.exe`；便携 zip 已通过 `unzip -t` 校验。
- 当前构建环境是 macOS，Windows `.exe` 安装包无法在本机直接执行 `/verify`，已确认输出为 x86-64 PE 文件。
