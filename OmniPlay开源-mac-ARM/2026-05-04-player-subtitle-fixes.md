# 2026-05-04 播放器与字幕修复记录

## 背景

本次修改集中修复 mac 版播放器的字幕识别、续播选集、播放页控制栏隐藏行为，并追加修复“设置中文优先字幕后，退出播放再进入默认切回英语”的问题。后续条目只作为追加记录，补充主页透明外观、WebDAV 标星挂载、详情页小窗布局、全屏退出缩放、剧集剧照映射、续播进度和自动下一集相关修复；后续新增修改继续追加，不覆盖既有记录。

## 修改内容

### 1. 字幕语言显示规范化

- 修复部分字幕语言码没有按“国旗 + 语言”格式显示的问题。
- 现在支持识别 `zh-Hans`、`ZH_HANT`、`en-US`、`ja-JP` 等常见 BCP-47 写法。
- 中文相关语言码统一显示为 `🇨🇳 中文`，英语显示为 `🇺🇸 英语`。

主要文件：

- `OmniPlay/MPVPlayerManager.swift`

### 2. 中文优先字幕重进播放后被切到英语

- 修复设置里选择“默认首选字幕：中文优先”，且上次播放使用简体字幕时，退出播放再重进后默认字幕自动切换到英语的问题。
- 不再只依赖 mpv 的 `sid=auto` 和 `slang` 自动匹配。
- 播放开始后读取 mpv 字幕轨道列表，由应用按规则主动设置 `sid`：
  - 中文优先时：简体中文优先，其次中文通用，再其次繁体中文。
  - 没有中文轨道时才回退到英文。
  - 英语优先时：英文优先，中文作为兜底。
  - 关闭字幕时不自动选择字幕。
- 用户在当前播放中手动选择字幕或加载外挂字幕后，不再被自动选择逻辑覆盖。

主要文件：

- `OmniPlay/MPVPlayerManager.swift`
- `OmniPlay/PlayerScreen.swift`

### 3. 详情页继续播放选集规则

- 给详情页“继续播放”的季数/集数选定增加前置规则：优先自动选定上次退出时仍未播完的集数。
- 新增 `VideoFile.lastPlayedAt` 字段记录最近一次退出播放且未播完的时间。
- 退出播放时：
  - 未播完且进度超过 5 秒，写入 `lastPlayedAt`。
  - 已看完或手动标记已看/未看时，清空 `lastPlayedAt`。
- 详情页优先选择最近一次未播完的集；没有该记录时再按原逻辑选择下一集未看内容。
- 继续观看卡片进度也改为优先展示最近一次未播完的文件。

主要文件：

- `OmniPlay/AppDatabase.swift`
- `OmniPlay/DatabaseModels.swift`
- `OmniPlay/MovieDetailView.swift`
- `OmniPlay/MovieCardView.swift`
- `OmniPlay/PlayerScreen.swift`
- `OmniPlay/DirectFilePlaybackManager.swift`

### 4. 播放页鼠标与控制栏自动隐藏

- 将播放页控制 UI 自动隐藏时间调整为鼠标静止 3 秒后隐藏。
- 鼠标停留在顶部横条区域时，不再自动隐藏控制 UI 和 macOS 左上角窗口按钮。
- 鼠标离开顶部横条区域后，恢复 3 秒隐藏计时。
- 关闭播放窗口时强制恢复鼠标可见，避免全屏退出后光标状态异常。

主要文件：

- `OmniPlay/PlayerScreen.swift`

### 5. macOS 26 深色透明外观可读性

- 优化深色模式叠加透明外观时主页右上角工具图标的颜色。
- 保留原有图标样式，只在深色透明外观下加深图标颜色，避免浅色或半透明背景导致看不清。
- 加深主页左上角扫描/日志状态文字颜色，提升透明背景上的可读性。

主要文件：

- `OmniPlay/PosterWallView.swift`

### 6. 主页工具按钮顺序

- 将主页右上角“离线缓存”按钮移动到“扫描”和“设置”按钮中间。
- 保持原有按钮功能不变，只调整工具栏排列。

主要文件：

- `OmniPlay/PosterWallView.swift`

### 7. WebDAV 共享文件夹标星挂载

- WebDAV 共享文件夹支持多个文件夹同时标星。
- 文件夹列表增加关闭按钮，关闭列表后自动加载已标星文件夹作为挂载媒体源。
- 标星逻辑从单选目标调整为多选目标，便于一次挂载多个常用目录。

主要文件：

- `OmniPlay/PosterWallView.swift`
- `OmniPlay/DatabaseModels.swift`

### 8. 详情页小窗播放进度布局

- 修复详情页窗口较小时，有播放进度时海报和文字被部分遮挡的问题。
- 小窗模式下将进度条放到播放按钮横向行的下一行。
- 宽屏或全屏空间足够时，播放按钮和进度条仍保持一行显示。

主要文件：

- `OmniPlay/MovieDetailView.swift`

### 9. 播放页退出全屏后的画面缩放

- 排查并修复播放状态下按 `Esc` 退出全屏后，视频画面未按窗口大小重新缩放的问题。
- `MPVVideoView` 在窗口尺寸变化、退出全屏和布局刷新时重新绑定/刷新 Metal drawable。
- 暂停和播放状态下退出全屏都应按窗口比例重新铺满，不再出现画面显示不完整。

主要文件：

- `OmniPlay/MPVVideoView.swift`
- `OmniPlay/MPVPlayerManager.swift`

### 10. 剧集剧照抓取与季集映射

- 修复部分剧集刮削不到剧照的问题。
- 不再使用本地视频截帧作为剧集剧照兜底，避免偏离 TMDB 官方素材。
- 当 TMDB 的季号与本地文件名季号不完全一致时，不改变本地文件名解析出的季数/集数。
- 剧照抓取增加映射规则：按本地集数映射到 TMDB 中集数数量匹配的季，解决例如本地 `S03` 对应 TMDB `Season 1 共 47 集` 的场景。
- 更新剧照失败缓存版本，避免旧失败记录阻止重新抓取。

主要文件：

- `OmniPlay/ThumbnailManager.swift`

### 11. 详情页选集与实际播放目标一致

- 修复详情页分集剧照选中某一集后，“继续播放”文案显示选中集数，但实际播放另一集的问题。
- 详情页刷新媒体库数据时优先保留用户当前选中的具体文件 `fileId`，避免只按季保留导致回落到该季第一集或旧目标。
- 独立播放窗口复用时给 `PlayerScreen` 增加请求级 `id`，强制重建播放器视图，避免复用旧播放状态。

主要文件：

- `OmniPlay/MovieDetailView.swift`
- `OmniPlay/DirectPlaybackWindowManager.swift`

### 12. 续播进度从显示进度开始

- 修复未播完剧集点击“继续播放”会从该集开头开始的问题。
- 不再把恢复点写入 mpv 的全局 `start` 选项，避免恢复点被新加载流程忽略或影响后续剧集。
- 播放首个文件时记录待恢复进度，等待 mpv 确认当前文件、时长可用后再执行绝对 seek。
- 如果第一次 seek 过早未生效，会短时间重试，直到当前位置接近显示的播放进度。

主要文件：

- `OmniPlay/MPVPlayerManager.swift`
- `OmniPlay/PlayerScreen.swift`

### 13. 自动播放下一集进度处理

- 修复自动进入下一集时，未播剧集没有从头开始播放的问题。
- 切换到下一集前强制清理 mpv 的全局 `start`，避免上一集恢复点继承到下一集。
- 修复自动播放进入下一集后短时间内关闭播放页，详情页“继续播放”仍定位上一集的问题。
- 播放器关闭时直接从 mpv 读取当前 `playlist-pos`、`filename`、`time-pos`、`duration` 快照，用真实播放项保存进度。
- playlist 前进时立即把跨过的上一集写成已播并清空 `lastPlayedAt`；如果用户在极短窗口内关闭，关闭流程也会补写上一集为已播。

主要文件：

- `OmniPlay/MPVPlayerManager.swift`
- `OmniPlay/PlayerScreen.swift`

## 数据库变更

新增迁移：

- `v4`

新增字段：

- `videoFile.lastPlayedAt: Double?`

用途：

- 记录最近一次退出时仍未播完的视频文件时间戳。
- 用于详情页和继续观看卡片选择真正的最近续播目标。

## 测试与验证

新增测试：

- `subtitleLanguageLabelNormalization`
- `subtitleAutoSelectionPrefersChineseTracks`

已执行并通过：

```bash
xcodebuild -project OmniPlay.xcodeproj -scheme OmniPlay -configuration Debug -sdk macosx test -only-testing:OmniPlayTests/BusinessLogicIntegrationTests
xcodebuild -project OmniPlay.xcodeproj -scheme OmniPlay -configuration Debug -sdk macosx build
```

结果：

- `TEST SUCCEEDED`
- `BUILD SUCCEEDED`

## 备注

- 根目录截图和 `.DS_Store` 等既有脏文件未纳入本次修复。
- 旁边未跟踪目录未处理。
