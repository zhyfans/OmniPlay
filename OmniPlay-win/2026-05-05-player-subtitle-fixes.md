# 2026-05-05 Windows 播放器与字幕修复记录

## 背景

按 ARM mac 版 `2026-05-04-player-subtitle-fixes.md` 排查 Windows 版后，确认 Windows 版存在部分同类问题：字幕语言码兼容不完整、默认字幕选择会受 mpv 当前状态影响、继续观看和详情页选集缺少最近未播完依据、WebDAV/SMB 文件夹只能单个处理、TMDB 剧集剧照缺少季集映射，以及恢复播放 seek 过早时可能不生效。

Windows 已有播放器控制栏 3 秒自动隐藏逻辑；macOS 透明工具栏和 Metal drawable 属于 mac 专用问题，Windows 版不适用。

## 修改内容

### 1. 字幕语言显示与默认字幕选择

- `PlayerTrackDisplayNameFormatter` 支持 `zh-Hans`、`ZH_HANT`、`en-US`、`ja-JP` 等语言码。
- 轨道显示保留“国旗 + 语言”的完整标签，并统一元数据分隔格式。
- 默认字幕选择改为按分数排序：
  - 中文优先：简体中文、通用中文、繁体中文、英文兜底。
  - 英文优先：英文优先，中文兜底。
- 默认字幕即使已被 mpv 标记为当前轨，也会显式下发选择，避免重进播放后回落到错误轨道。
- 用户手动选择字幕或加载外挂字幕后，不再被默认选择逻辑覆盖。

主要文件：

- `src/OmniPlay.Core/Models/Playback/PlayerTrackDisplayNameFormatter.cs`
- `src/OmniPlay.Core/ViewModels/Player/PlayerViewModel.cs`
- `tests/OmniPlay.Tests/PlayerTrackDisplayNameFormatterTests.cs`
- `tests/OmniPlay.Tests/PlayerViewModelTests.cs`

### 2. 最近未播完记录与继续观看

- 新增 `VideoFile.LastPlayedAt` / `videoFile.lastPlayedAt`。
- `UpdatePlaybackStateAsync` 仅在未播完且进度有效时写入 `LastPlayedAt`，已看完或无有效进度时清空。
- 继续观看查询按最近未播完文件选出每个电影/剧集的代表项，并按 `LastPlayedAt` 排序。
- 详情页季集默认选择优先最近未播完，其次才回到下一集未看逻辑。

主要文件：

- `src/OmniPlay.Core/Models/Entities/VideoFile.cs`
- `src/OmniPlay.Core/Models/Library/LibraryPosterItem.cs`
- `src/OmniPlay.Core/Models/Library/LibraryVideoItem.cs`
- `src/OmniPlay.Infrastructure/Data/SqliteDatabase.cs`
- `src/OmniPlay.Infrastructure/Data/VideoFileRepository.cs`
- `src/OmniPlay.Core/ViewModels/Library/PosterWallViewModel.cs`
- `tests/OmniPlay.Tests/VideoFileRepositoryTests.cs`

### 3. 恢复播放 seek

- 启动恢复播放时增加短时间重试，不再只做一次延迟 seek。
- 避免播放器状态尚未就绪时恢复点被忽略，导致从头播放。

主要文件：

- `src/OmniPlay.Core/ViewModels/Player/PlayerViewModel.cs`

### 4. WebDAV/SMB 多目录标星挂载

- 网络共享文件夹支持标星/取消标星。
- 增加批量挂载已标星文件夹命令和底部操作按钮。
- 挂载完成后逐个扫描对应媒体源。

主要文件：

- `src/OmniPlay.Core/Models/Network/NetworkShareFolderItem.cs`
- `src/OmniPlay.Core/ViewModels/Library/PosterWallViewModel.cs`
- `src/OmniPlay.UI/Views/Library/PosterWallView.axaml`

### 5. TMDB 剧集剧照映射

- 下载剧集剧照前先读取 TMDB 季摘要，按本地集数映射到可能的 TMDB 季号。
- 若 episode detail 没有 `still_path`，补充查询 episode images。
- 更新相关测试 fake handler，覆盖本地 `S03E08` 映射到 TMDB `Season 1 Episode 8` 的场景。

主要文件：

- `src/OmniPlay.Infrastructure/Tmdb/TmdbMetadataClient.cs`
- `tests/OmniPlay.Tests/TmdbMetadataClientRestrictionsTests.cs`
- `tests/OmniPlay.Tests/LibraryThumbnailEnricherTests.cs`

### 6. 安装包重复安装检测

- 安装器启动时检查当前用户卸载注册表中的既有 `InstallLocation`。
- 未指定目录时，发现已安装版本会提示“升级现有安装 / 选择其他目录 / 取消”。
- 指定目录且与既有安装目录不一致时，会提示该操作可能产生重复安装。
- 静默安装模式下如果指定不同目录会直接失败，避免无人值守重复安装多个目录。

主要文件：

- `installer/OmniPlay.Setup/Program.cs`

## 数据库变更

新增字段：

- `videoFile.lastPlayedAt REAL NULL`

兼容方式：

- 新建库建表时包含该字段。
- 旧库启动时通过 `EnsureColumn` 自动补列。

## 验证

已执行并通过本次相关测试：

```bash
dotnet test OmniPlay.Windows.slnx --filter "FullyQualifiedName~PlayerTrackDisplayNameFormatterTests|FullyQualifiedName~PlayerViewModelTests|FullyQualifiedName~VideoFileRepositoryTests|FullyQualifiedName~TmdbMetadataClientRestrictionsTests.DownloadEpisodeStillAsync_MapsLocalSeasonToSingleTmdbSeason"
```

结果：

- 43 个相关测试通过。

已执行全量测试：

```bash
dotnet test OmniPlay.Windows.slnx
```

结果：

- 153 通过、24 失败。
- 剩余失败集中在本地 Windows 路径规范化、TMDB 公共源限制 fixture、凭据保护、元数据/海报下载相关用例；不属于本次播放器/字幕/续播修复范围。

已执行解决方案构建：

```bash
dotnet build OmniPlay.Windows.slnx
```

结果：

- 已通过，0 警告、0 错误。
- 验证时发现 DLL 需要位于 `src/OmniPlay.Desktop/Native/mpv/`；已将本地 `native/mpv/` 下的 `libmpv-2.dll` 和 `d3dcompiler_43.dll` 复制到项目声明的路径后重新构建。

已执行安装器项目构建：

```bash
dotnet build installer/OmniPlay.Setup/OmniPlay.Setup.csproj -c Release -p:EnableWindowsTargeting=true -p:NuGetAudit=false
```

结果：

- 已通过，0 警告、0 错误。
