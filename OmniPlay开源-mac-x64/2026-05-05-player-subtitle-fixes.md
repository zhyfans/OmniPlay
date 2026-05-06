# 2026-05-05 x64 mac 播放器与字幕修复记录

## 背景

按 ARM mac 版 `2026-05-04-player-subtitle-fixes.md` 排查 x64 mac 版后，确认 x64 版存在同类问题：字幕语言码兼容不完整、默认字幕会回落到英语、详情页续播目标不稳定、退出全屏后 mpv drawable 刷新不足、WebDAV 只能单目录挂载、剧集剧照季集映射不足，以及自动下一集和关闭播放器时进度落库目标可能不一致。

## 修改内容

### 1. 字幕语言显示与默认字幕选择

- 支持 `zh-Hans`、`ZH_HANT`、`en-US`、`ja-JP` 等 BCP-47/下划线语言码。
- 中文优先时按“简体中文、通用中文、繁体中文、英文兜底”的顺序主动选择字幕轨。
- 英文优先时保留中文兜底。
- 用户手动选择字幕或加载外挂字幕后，不再被默认字幕逻辑覆盖。

主要文件：

- `OmniPlay/MPVPlayerManager.swift`
- `OmniPlay/PlayerScreen.swift`
- `OmniPlayTests/BusinessLogicIntegrationTests.swift`

### 2. 最近未播完记录与继续播放选集

- 新增 `videoFile.lastPlayedAt`，记录退出播放时仍未播完的文件时间。
- 详情页和继续观看卡片优先使用最近未播完文件，不再只按最高进度或默认集数选择。
- 手动标记已看/未看、播放完成、自动进入下一集时清空对应 `lastPlayedAt`。

主要文件：

- `OmniPlay/AppDatabase.swift`
- `OmniPlay/DatabaseModels.swift`
- `OmniPlay/MovieDetailView.swift`
- `OmniPlay/MovieCardView.swift`
- `OmniPlay/PlayerScreen.swift`
- `OmniPlay/DirectFilePlaybackManager.swift`
- `OmniPlay/PosterWallView.swift`

### 3. 播放器关闭、续播 seek 与自动下一集

- 关闭播放器时从 mpv 读取 `playlist-pos`、`filename`、`time-pos`、`duration` 快照，用真实播放项保存进度。
- 恢复播放不再依赖全局 `start`，改为加载后按当前文件和时长重试 seek。
- 自动进入下一集前清理旧恢复点，并把已经跨过的上一集写为已播。
- 复用独立播放窗口时按请求 `id` 重建 `PlayerScreen`，避免旧播放状态影响新请求。

主要文件：

- `OmniPlay/MPVPlayerManager.swift`
- `OmniPlay/PlayerScreen.swift`
- `OmniPlay/DirectPlaybackWindowManager.swift`

### 4. 播放页 UI 与详情页布局

- 播放页控制栏鼠标静止 3 秒后隐藏，鼠标停留顶部窗口按钮区域时保持可见。
- 详情页小窗口下将进度条换行，避免海报和文字被遮挡。
- 详情页刷新后保留当前选中文件 `fileId`，避免文案和实际播放目标不一致。

主要文件：

- `OmniPlay/PlayerScreen.swift`
- `OmniPlay/MovieDetailView.swift`

### 5. x64 mac 专用显示与工程修复

- `MPVVideoView` 在窗口尺寸变化和退出全屏后强制刷新/重绑 Metal drawable，修复画面缩放不同步。
- 主页深色透明外观下提高工具图标与状态文字可读性，并调整工具按钮顺序。
- x64 工程补回本地 `GRDB.swift-master` package 引用；该目录已存在，缺少引用会导致构建报 `Missing package product 'GRDB'`。

主要文件：

- `OmniPlay/MPVVideoView.swift`
- `OmniPlay/MPVPlayerManager.swift`
- `OmniPlay/PosterWallView.swift`
- `OmniPlay.xcodeproj/project.pbxproj`

### 6. WebDAV 多目录标星挂载

- WebDAV 文件夹浏览支持多个目录标星。
- 关闭文件夹列表后批量挂载已标星目录并触发扫描。

主要文件：

- `OmniPlay/PosterWallView.swift`

### 7. 剧集剧照季集映射

- TMDB 剧集剧照抓取增加季摘要映射，支持本地季号与 TMDB 季号不一致的情况。
- 不再使用本地视频截帧作为剧照兜底。
- 更新失败缓存版本，避免旧失败记录阻止重新抓取。

主要文件：

- `OmniPlay/ThumbnailManager.swift`

## 数据库变更

新增迁移：

- `v5`

新增字段：

- `videoFile.lastPlayedAt: Double?`

## 验证

已执行并通过：

```bash
xcodebuild -quiet -project OmniPlay.xcodeproj -scheme OmniPlay -configuration Debug -sdk macosx build
xcodebuild -quiet -project OmniPlay.xcodeproj -scheme OmniPlay -configuration Debug -sdk macosx test -only-testing:OmniPlayTests
```

结果：

- Debug macOS 构建通过。
- `OmniPlayTests`、`BusinessLogicIntegrationTests`、`WebDAVMockIntegrationTests` 均通过。
