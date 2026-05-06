# OmniPlay 群晖 / 飞牛套件开发计划

## 1. 目标

本文档定义 `OmniPlay-群晖-飞牛` 的移植方案、推荐技术栈、工程结构、阶段计划和验收标准。

约束：

- `OmniPlay开源-mac-ARM` 只作为功能、UI、数据逻辑参考源，不在本项目中改动。
- 群晖和飞牛版本以 NAS 服务端为核心，不尝试把 SwiftUI/macOS 桌面应用直接搬到 Linux。
- 套件安装后，局域网用户可以直接通过浏览器打开套件地址完成媒体库管理和播放。
- 套件服务端要为后续 Android 播放端提供稳定 API、认证、播放流、进度同步和媒体库数据。

## 2. 当前 mac 版能力基线

参考目录：

```text
OmniPlay开源-mac-ARM/OmniPlay
```

mac 版核心能力：

- 海报墙首页、继续播放、搜索、按名称/评分/年份排序。
- 电影和剧集自动识别，多季剧集、特别篇、分集排序、下一集播放。
- 本地文件夹、WebDAV 媒体源管理。
- WebDAV 局域网发现、登录、目录浏览、星标挂载。
- TMDB 自动刮削、手动重匹配、自定义 API Key / Bearer Token、中文/英文语言。
- 海报缓存、分集剧照、分集手动编辑、本地剧照替换。
- mpv/FFmpeg 播放、本地和 WebDAV 播放、续播、音轨/字幕选择、外挂字幕、字幕大小/延迟。
- 播放画质模式、默认音轨/字幕、主题、偏好设置、开源组件信息。
- 离线缓存。

优先参考文件：

```text
AppDatabase.swift
DatabaseModels.swift
MediaLibraryManager.swift
PosterWallView.swift
MovieDetailView.swift
PlayerScreen.swift
MPVPlayerManager.swift
TMDBService.swift
PosterManager.swift
ThumbnailManager.swift
OfflineCacheManager.swift
LANScanner.swift
SettingsView.swift
```

## 3. 推荐技术方案

### 3.1 总体结论

推荐采用：

- 后端：`C# + ASP.NET Core + SQLite + Dapper/EF Core`
- Web 前端：`TypeScript + React + Vite`
- 网页播放器：原生 `HTMLVideoElement`，配合服务端直出 Range、HLS 转封装和必要时转码。
- 媒体工具：`ffprobe` 做探测，`ffmpeg` 做缩略图、HLS、转封装、转码。
- Android：`Kotlin + Jetpack Compose + AndroidX Media3/ExoPlayer`
- API：OpenAPI 作为 Web、Android 和服务端的稳定契约。
- 打包：后端自包含发布为 `linux-x64` / `linux-arm64`，再分别包成 Synology `.spk` 和飞牛 `.fpk` 或 Docker/应用中心形态。

不推荐：

- 不把 Swift/SwiftUI 迁移到 Linux。
- 不用 Electron 做 NAS 主界面，NAS 端应该是 Web UI。
- 不在第一阶段引入复杂微服务。
- 不用浏览器直接播放所有原始文件作为唯一方案，因为 MKV、PGS 字幕、TrueHD/DTS、部分 HEVC/HDR 在浏览器端兼容性不可控。

### 3.2 为什么选 C# / ASP.NET Core

理由：

- 仓库已有 `OmniPlay-win` 的 C# 移植成果，可复用媒体名解析、TMDB、SQLite、扫描器、WebDAV 等思路。
- ASP.NET Core 在 Linux NAS 上适合做长驻 HTTP 服务，Web UI、Android API、流媒体端点可以统一承载。
- .NET 10 已是 LTS，支持到 2028 年 11 月；若为了更保守的 NAS 环境，也可以先锁定 .NET 8 LTS 并在后续升级。
- 自包含发布可以减少 NAS 上预装运行时差异。

首选目标框架：

```text
net10.0
```

保守备选：

```text
net8.0
```

若继续复用 Windows 移植工程的 `net10.0` 思路，NAS 端建议直接采用 `net10.0`。

## 4. 推荐工程结构

建议在 `OmniPlay-群晖-飞牛` 下建立：

```text
OmniPlay-群晖-飞牛/
  docs/
    DEVELOPMENT_PLAN.md
    API_CONTRACT.md
    UI_RESTORE_CHECKLIST.md
    PACKAGE_GUIDE_SYNOLOGY.md
    PACKAGE_GUIDE_FNNAS.md
  server/
    OmniPlay.Server.slnx
    src/
      OmniPlay.Api/
      OmniPlay.Core/
      OmniPlay.Infrastructure/
      OmniPlay.Media/
      OmniPlay.Packaging/
    tests/
      OmniPlay.Tests/
  web/
    package.json
    src/
      app/
      features/library/
      features/player/
      features/settings/
      features/sources/
      shared/api/
      shared/ui/
  android/
    settings.gradle.kts
    app/
  packaging/
    synology/
      INFO.sh
      scripts/
      conf/
      icons/
    fnos/
      manifest/
      scripts/
      icons/
    docker/
      Dockerfile
      compose.yml
  shared/
    openapi/
      omniplay.openapi.yaml
  scripts/
    build-server.sh
    build-web.sh
    package-synology.sh
    package-fnos.sh
```

### 4.1 后端模块职责

`OmniPlay.Api`

- HTTP API、静态 Web UI 托管、认证、OpenAPI。
- 播放流端点：直出、HLS、转码任务控制。
- WebSocket/SSE：扫描进度、刮削进度、播放进度心跳。

`OmniPlay.Core`

- 领域模型、服务接口、业务规则。
- 不引用 ASP.NET、数据库、ffmpeg、平台 API。

`OmniPlay.Infrastructure`

- SQLite、文件系统、设置、缓存目录、密钥保存。
- TMDB 客户端、WebDAV 客户端、SMB/NFS 后续扩展。

`OmniPlay.Media`

- `ffprobe` 媒体探测。
- 缩略图/剧照生成。
- HLS 转封装、转码任务。
- 字幕解析和外挂字幕管理。

`OmniPlay.Packaging`

- Synology / fnOS 运行路径、权限、服务生命周期适配。
- 不放业务逻辑。

### 4.2 Web 前端模块职责

Web UI 是 NAS 套件的主 UI，目标是还原 mac 版视觉和信息架构：

- 首页海报墙：继续播放、所有影视、搜索、排序、空库态、扫描状态。
- 详情页：海报模糊背景、左海报右信息、播放/继续播放、已播/未播、剧集季选择、分集剧照网格。
- 媒体源管理：本地目录、WebDAV 登录、目录浏览、挂载、启用/停用、重命名、删除。
- 刮削编辑：影片重匹配、分集元数据、剧照替换。
- 播放器：全屏黑底、顶部标题、底部控制条、音轨、字幕、外挂字幕、下一集。
- 设置：TMDB、语言、主题、默认音轨/字幕、播放策略、缓存、开源组件。

### 4.3 Android 模块职责

Android 不复制 WebView，做原生播放端：

- 登录/绑定局域网 NAS。
- 浏览媒体库、搜索、详情页、剧集列表。
- 使用 Media3/ExoPlayer 播放服务端提供的直连或 HLS 地址。
- 播放进度心跳、续播、已播/未播。
- 后续支持离线缓存、投屏、外网访问。

## 5. 数据模型设计

后端数据库建议使用 SQLite，保留 mac 版概念但做更清晰的服务端建模。

核心表：

- `media_sources`
- `media_source_credentials`
- `library_items`
- `movies`
- `tv_shows`
- `seasons`
- `episodes`
- `video_files`
- `playback_progress`
- `scrape_overrides`
- `poster_assets`
- `thumbnail_assets`
- `transcode_jobs`
- `app_settings`
- `users`
- `api_tokens`

关键原则：

- `video_files` 保存真实文件位置、源类型、相对路径、文件大小、mtime、媒体探测结果。
- `library_items` 作为海报墙统一入口，关联电影或剧集。
- 播放进度按用户维度保存，为 Android 多端同步预留。
- 海报、剧照、HLS 临时文件都放在套件数据目录，不能污染用户媒体目录。

## 6. 播放方案

### 6.1 局域网页播放

播放端点分三层：

1. 直出原文件：
   - 支持 HTTP Range。
   - 适合 MP4/MOV + 浏览器支持的视频/音频编码。

2. HLS 转封装：
   - 不重编码，只把可兼容编码转成 HLS 分片。
   - 适合服务端 CPU 压力较小的场景。

3. HLS 转码：
   - 容器、视频编码、音频编码或字幕不兼容时启用。
   - 优先支持 H.264 + AAC + WebVTT。
   - 硬件转码能力按设备检测，不能写死。

服务端需要提供：

```text
GET  /api/playback/{fileId}/manifest
GET  /api/stream/{fileId}
GET  /api/transcode/{jobId}/index.m3u8
POST /api/playback/progress
POST /api/playback/{fileId}/stop
```

### 6.2 Android 播放

Android 使用同一套播放决策 API：

- 优先直连 Range。
- 不兼容时切换 HLS。
- Media3/ExoPlayer 负责播放、音轨、字幕和后台控制。
- 进度每 5-10 秒上报一次，暂停/退出时强制上报。

### 6.3 字幕策略

第一阶段：

- 内封文本字幕读取并通过播放器选择。
- 外挂 SRT/ASS 上传或从同目录发现。
- Web 端优先转 WebVTT。

第二阶段：

- ASS 高级样式保留。
- PGS 图形字幕在 Web 端需要烧录或转换，作为高成本功能排后。

## 7. 套件打包方案

### 7.1 Synology SPK

Synology 包结构按官方 Package Framework：

```text
OmniPlay.spk
  INFO
  package.tgz
  scripts/
    start-stop-status
    preinst
    postinst
    preuninst
    postuninst
    preupgrade
    postupgrade
  conf/
    privilege
    resource
  PACKAGE_ICON.PNG
  PACKAGE_ICON_256.PNG
  LICENSE
```

实现要求：

- `INFO` 声明 `package`、`version`、`os_min_ver`、`arch`、`maintainer`、`description`。
- DSM 7 必须提供 `conf/privilege`，默认 `run-as: package`。
- `start-stop-status` 控制服务启动、停止、状态码。
- 数据目录放在 `/var/packages/OmniPlay/home` 或用户选择的数据路径。
- 服务默认监听 `127.0.0.1` 或指定端口，再通过 DSM 反代/入口打开；内测阶段可直接监听 LAN 端口。
- 分别构建 `x86_64` 和 `aarch64` 包。

### 7.2 飞牛 fnOS / FPK

飞牛 fnOS 基于 Debian Linux，官方站点明确有 Docker 支持和应用中心生态。建议分两步：

第一步：

- 先做标准 Linux 服务和 Docker 镜像。
- 在飞牛上通过 Docker 或手动服务验证媒体扫描、网页播放、ffmpeg、权限、存储路径。

第二步：

- 按飞牛应用开放平台当前规范封装为 `.fpk`。
- 打包脚本保持薄封装，只负责安装、权限、端口、启动/停止、卸载和升级迁移。

原因：

- 飞牛应用打包规范仍需以官方开放平台文档为准，工程不能被打包格式绑死。
- 标准 Linux + Docker 先跑通，可以同时服务群晖 Docker、飞牛 Docker、普通 Debian/Ubuntu 测试机。

飞牛端默认端口建议避开系统 HTTP/HTTPS 端口：

```text
OmniPlay Web/API: 8096 或 45721
```

实际端口最终由安装向导或配置文件决定。

## 8. API 设计

优先定义 OpenAPI，Web 和 Android 都从同一份契约生成客户端。

第一批 API：

```text
GET    /api/health
GET    /api/settings
PUT    /api/settings

GET    /api/sources
POST   /api/sources/local
POST   /api/sources/webdav
POST   /api/sources/webdav/test
POST   /api/sources/webdav/browse
GET    /api/sources/local/directories
POST   /api/sources/{id}/scan
PATCH  /api/sources/{id}
DELETE /api/sources/{id}

POST   /api/library/scan
GET    /api/library/scan/status
GET    /api/library/items
GET    /api/library/items/{id}
POST   /api/library/items/{id}/rescrape
PATCH  /api/library/video-files/{id}

GET    /api/assets/posters/{id}
GET    /api/assets/thumbnails/{id}
POST   /api/assets/thumbnails/{fileId}
POST   /api/assets/cache/cleanup
GET    /api/cache/status

GET    /api/playback/decision/{fileId}
GET    /api/stream/{fileId}
GET    /api/playback/files/{fileId}/cache
POST   /api/playback/files/{fileId}/cache/prepare
POST   /api/playback/files/{fileId}/cache/cancel
POST   /api/playback/hls/cleanup
POST   /api/playback/webdav/cache/cleanup
POST   /api/playback/progress
POST   /api/playback/watched
```

认证：

- 局域网首次启动创建管理员。
- Web 使用 Cookie Session。
- Android 使用短期 Access Token + 长期 Refresh Token。
- 播放流 URL 使用短期签名 token，避免裸露文件路径。

## 9. UI 还原策略

不要先做通用后台管理界面。第一屏必须是可用的海报墙，而不是说明页。

还原顺序：

1. 海报墙首页。
2. 详情页。
3. 播放器。
4. 媒体源弹窗。
5. 设置页。
6. 刮削和剧照编辑弹窗。

需要建立 `UI_RESTORE_CHECKLIST.md`，逐项对照 mac 版：

- 布局层级。
- 间距、字号、卡片比例。
- 海报比例 2:3。
- 分集剧照比例。
- 主题色。
- 空状态。
- 扫描/刮削/缓存进度提示。
- 播放器控件显隐逻辑。
- 移动端浏览器适配。

## 10. 开发阶段

### 阶段 0：规格冻结和工程地基

目标：

- 建立 server/web/packaging/docs 工程骨架。
- 确定数据库迁移机制。
- 确定 OpenAPI 初版。
- 固定运行目录规范。

验收：

- `server` 可启动并返回 `/api/health`。
- `web` 可构建并由后端托管。
- SQLite 数据库自动创建。

### 阶段 1：本地媒体库 MVP

目标：

- 添加 NAS 本地目录。
- 扫描视频文件。
- 媒体名解析。
- 首页海报墙最小展示。

验收：

- 可以添加 `/volume1/...`、飞牛存储路径或开发机目录。
- 扫描后 Web 首页出现影片卡片。
- 搜索、排序可用。

### 阶段 2：TMDB 刮削和海报

目标：

- 移植 mac 版 TMDB 搜索评分策略。
- 下载海报、简介、评分、年份。
- 支持自定义 TMDB Key / Token。
- 支持手动重匹配。

验收：

- 常见电影和剧集能自动匹配。
- 错误匹配可手动修正并锁定。
- 海报缓存可复用。

### 阶段 3：详情页和剧集

目标：

- 电影详情页。
- 剧集季选择、分集排序、分集剧照。
- 已播/未播和续播信息。

验收：

- 电影、单季剧、多季剧、特别篇都能正确展示。
- 播放按钮选择正确文件。

### 阶段 4：网页播放

目标：

- Range 直出。
- HLS 转封装。
- 必要时转码。
- 播放进度保存。
- 音轨/字幕基础选择。

验收：

- LAN 浏览器访问套件地址可播放兼容 MP4。
- MKV 至少能通过 HLS 方案播放一批常见样本。
- 退出后能续播。

### 阶段 5：Synology SPK

目标：

- 生成 `.spk`。
- DSM 7 手动安装。
- 服务启停、升级、卸载。

验收：

- DSM 套件中心可以安装和启动。
- 浏览器可以打开 OmniPlay Web UI。
- 数据升级不丢库。

### 阶段 6：飞牛套件

目标：

- Docker 方式先通过。
- 再按飞牛开放平台规范生成 `.fpk`。

验收：

- 飞牛上可安装/启动/停止。
- 可以选择媒体目录并播放。
- 升级不丢配置和数据库。

### 阶段 7：Android 播放端

目标：

- Android 发现/绑定服务端。
- 媒体库浏览、详情页、播放。
- 进度同步。

验收：

- 同一账号在 Web 和 Android 之间续播同步。
- Android 可播放直出和 HLS。

### 阶段 8：高级功能补齐

目标：

- WebDAV 发现、登录、目录浏览、挂载。
- 离线缓存。
- 外挂字幕。
- 多用户权限。
- 硬件转码。
- 外网安全访问策略。

## 11. 测试策略

后端测试：

- 媒体名解析样本。
- 扫描去重和删除同步。
- TMDB 匹配排序。
- SQLite 迁移。
- WebDAV 路径规范化。
- 播放决策。

Web 测试：

- 首页/详情/播放器 Playwright 截图。
- 桌面和移动端 viewport。
- 空库、扫描中、刮削失败、播放失败状态。

NAS 测试：

- Synology DSM 7 x86_64。
- Synology DSM 7 arm64。
- 飞牛 x86_64。
- 飞牛 ARM64。
- 低性能 CPU 无转码场景。
- 大片库 1 万文件以上扫描。

播放样本：

- MP4 H.264/AAC。
- MKV H.264/AAC。
- MKV HEVC。
- 4K HDR。
- TrueHD/DTS。
- 内封 SRT/ASS/PGS。
- 外挂字幕。
- BDMV 目录。

## 12. 主要风险

- 浏览器播放兼容性：需要直出、转封装、转码三层兜底。
- NAS 性能差异：部分设备无法实时转码，需要提供“不转码/只转封装/自动”策略。
- 硬件转码：群晖和飞牛设备差异大，必须做能力检测。
- 权限：套件用户可能无法读取所有共享目录，需要安装向导提示和权限申请。
- 飞牛打包规范变动：先保持标准 Linux 服务，最后做薄封装。
- GPL/LGPL 合规：ffmpeg、mpv、libass 等组件分发方式和许可证说明必须在发布前单独审查。
- TMDB 网络：需要可配置 API、代理提示、失败重试和队列恢复。

## 13. 最推荐的开发顺序

1. 先写 `server + web`，把 NAS 套件本体跑通。
2. 再做本地媒体库、刮削、详情页。
3. 再做网页播放。
4. 再做 Synology SPK。
5. 再做飞牛 Docker 验证和 FPK。
6. 最后做 Android 端。

这样做的原因：

- Web UI 和 Android 都依赖同一个服务端 API。
- 播放链路是最大风险，应在套件打包前先验证。
- 群晖/飞牛打包应该是薄壳，不应该影响核心业务架构。

## 14. 参考资料

- Synology Package Introduction: https://help.synology.com/developer-guide/synology_package/introduction.html
- Synology INFO: https://help.synology.com/developer-guide/synology_package/INFO.html
- Synology scripts / start-stop-status: https://help.synology.com/developer-guide/synology_package/scripts.html
- Synology privilege: https://help.synology.com/developer-guide/privilege/preface.html
- .NET Support Policy: https://dotnet.microsoft.com/en-us/platform/support/policy
- Android Jetpack Media3: https://developer.android.com/media/media3
- 飞牛 fnOS 官网: https://www.fnnas.com/
- 飞牛 fnOS 端口说明: https://help.fnnas.com/articles/v1/settings/port-customization.md

## 15. 当前实现状态

更新时间：2026-05-03

已完成阶段 0 的最小工程地基：

- 已创建 `server/OmniPlay.Server.slnx`。
- 已创建 `OmniPlay.Api`、`OmniPlay.Core`、`OmniPlay.Infrastructure`、`OmniPlay.Media`、`OmniPlay.Packaging`。
- 后端目标框架为 `net10.0`。
- 已实现运行目录解析，支持 `OMNIPLAY_APP_ROOT`。
- 已实现 Kestrel 手动监听地址解析，支持 `OMNIPLAY_URLS`，回退读取 `ASPNETCORE_URLS`。
- 已实现 SQLite 初始化，数据库文件为 `data/omniplay.sqlite`。
- 已创建 NAS 服务端初版表结构。
- 已实现 `/api/health`、`/api/settings`、`/api/library/items` 最小端点。
- 已创建 `web/` 的 React + Vite 首屏骨架。
- 已创建 `shared/openapi/omniplay.openapi.yaml` 初版。
- 已创建 Synology、fnOS、Docker 打包目录和初始模板。
- 已添加后端单元测试，覆盖运行目录创建和数据库初始化。

已验证：

```text
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
curl -sS http://127.0.0.1:45728/api/health
```

当前限制：

- Web 前端尚未安装 npm 依赖。
- 阶段 0 只代表可启动服务端和基础数据库地基，阶段 1 状态见下文。
- TMDB、网页播放、套件打包产物尚未实现。

## 16. 阶段 1 当前实现状态

更新时间：2026-05-03

已完成本地媒体库 MVP 的第一条纵切：

- 已实现本地媒体源列表接口：`GET /api/sources`。
- 已实现添加本地媒体源接口：`POST /api/sources/local`。
- 已实现同步扫描接口：`POST /api/library/scan`。
- 已实现扫描状态接口：`GET /api/library/scan/status`。
- 已实现海报墙库列表接口：`GET /api/library/items`。
- 已实现本地目录递归扫描，支持常见视频扩展名。
- 已实现基础媒体名解析、年份提取、电影/剧集识别、SxxEyy 分集识别。
- 已实现 BDMV/STREAM 主视频片段筛选。
- 已实现稳定 ID 生成，重复扫描不会重复入库。
- 已实现缺失文件标记。
- 已实现 Web 首屏读取真实库数据，并接入添加本地目录和扫描按钮。

已验证：

```text
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
5 passed, 0 failed
```

HTTP 纵切验证已通过：

```text
POST /api/sources/local
POST /api/library/scan
GET  /api/library/items
```

临时样本扫描结果：

```text
电影：霸王别姬，1 个视频
剧集：绝命毒师，2 个视频
```

当前限制：

- Web 构建验证已在阶段 3 后补齐，当前状态见第 19 节。
- 阶段 1 目前只支持 NAS 本地目录，不支持 WebDAV/SMB。
- 当前扫描是同步接口，尚未做后台队列、进度推送和取消控制。
- 当前入库只有本地解析标题，TMDB 刮削属于阶段 2。

## 17. 阶段 2 当前实现状态

更新时间：2026-05-02

已完成 TMDB 刮削和海报缓存的第一条纵切：

- 已实现 TMDB 设置模型和持久化：`GET /api/settings`、`PUT /api/settings`。
- 已实现 TMDB 搜索客户端，支持电影/剧集搜索、中文语言、年份参数。
- 已支持自定义 API Key、Bearer Token、环境变量 `OMNIPLAY_TMDB_API_KEY` / `OMNIPLAY_TMDB_ACCESS_TOKEN`。
- 已保留内置公共 TMDB 源开关。
- 已实现海报下载到 `cache/posters`。
- 已实现海报资产表写入和海报资源接口：`GET /api/assets/posters/{id}`。
- 已实现批量刮削接口：`POST /api/library/scrape`。
- 已实现单个条目重刮接口：`POST /api/library/items/{id}/rescrape`。
- 已实现 Web 首屏海报显示和刮削按钮。
- 已补充刮削服务测试，使用假 TMDB 客户端验证元数据和海报资产写回。

已验证：

```text
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug
```

当前测试结果：

```text
6 passed, 0 failed
```

HTTP 纵切验证已通过：

```text
PUT  /api/settings
POST /api/sources/local
POST /api/library/scan
POST /api/library/scrape
```

本轮 HTTP 验证中主动关闭了 TMDB 刮削开关，因此没有访问外网 TMDB。真实刮削需要填写 TMDB API Key / Bearer Token，或保持内置公共源开启。

当前限制：

- TMDB 匹配评分仍是简化版，尚未完整移植 mac/Windows 版的多候选、语言合并、年份容错和季数辅助判断。
- 还没有手动候选列表和手动选择匹配结果。
- 还没有分集剧照下载。
- Web 构建验证已在阶段 3 后补齐，当前状态见第 19 节。

## 18. 阶段 3 当前实现状态

更新时间：2026-05-02

已完成详情页和剧集展示的第一条纵切：

- 已实现详情接口：`GET /api/library/items/{id}`。
- 详情接口返回影片基础信息、视频文件列表、剧集季列表、分集列表和播放状态。
- 已实现续播进度接口：`POST /api/playback/progress`。
- 已实现已播/未播接口：`POST /api/playback/watched`。
- 已实现电影详情的视频文件列表。
- 已实现剧集详情的季/集结构。
- 已实现 Web 端海报卡点击进入详情页。
- 已实现 Web 端电影文件列表、剧集季/集卡片和已播/未播切换。
- 已补充详情页测试，覆盖季/集读取、视频文件读取、续播进度和已播状态。

已验证：

```text
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
7 passed, 0 failed
```

HTTP 纵切验证已通过：

```text
POST /api/sources/local
POST /api/library/scan
GET  /api/library/items
GET  /api/library/items/{id}
POST /api/playback/watched
GET  /api/library/items/{id}
```

临时样本验证结果：

```text
剧集：绝命毒师
季：第 1 季
分集：第 1 集、第 2 集
已播状态：可写入并在详情接口回显
```

当前限制：

- 网页播放的第一条纵切已完成，当前状态见第 20 节。
- 还没有分集剧照、手动编辑、整季缓存入口。
- 当前播放状态使用默认本地用户 `local`，多用户权限属于后续阶段。
- Web 构建验证已补齐，当前状态见第 19 节。

## 19. Web 构建当前状态

更新时间：2026-05-03

已完成 Web 依赖安装和生产构建验证：

- 已安装 React 类型依赖：`@types/react`、`@types/react-dom`。
- 已生成并提交 npm 锁定文件：`web/package-lock.json`。
- 已通过 TypeScript 和 Vite 生产构建。
- 生产产物已输出到 `web/dist/`。
- 已引入 `hls.js`，用于 Chrome/Edge 等浏览器播放 HLS。

已验证：

```text
npm install -D @types/react @types/react-dom --fetch-timeout=120000 --fetch-retries=2
npm run build
```

构建输出：

```text
dist/index.html
dist/assets/index-DlDhvcNs.css
dist/assets/index-PzI4EVGz.js
```

## 20. 阶段 4 当前实现状态

更新时间：2026-05-02

已完成网页播放的第一条纵切：

- 已实现本地入库视频的播放文件解析：`GetPlayableVideoFileAsync`。
- 已限制播放文件必须来自启用的本地媒体源，且真实路径必须位于媒体源根目录内。
- 已实现 Range 直出接口：`GET /api/playback/files/{fileId}/stream`。
- 已扩展视频 MIME 类型识别，支持 MP4/M4V/WebM/MOV/MKV/TS/AVI 等基础类型。
- 已实现 Web 内置播放器页面，电影详情和剧集分集都可以进入播放。
- 已实现播放进度自动保存，复用 `POST /api/playback/progress`。
- 已把 `web/dist` 作为服务端 `wwwroot` 静态资源复制，套件地址根路径可以直接打开 Web UI。
- 已更新 OpenAPI 契约。
- 已补充播放文件解析测试，覆盖正常本地文件和目录穿越防护。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
9 passed, 0 failed
```

当前限制：

- HLS 转封装第一版已补齐，当前状态见第 21 节。
- 必要时视频转码第一版已补齐，当前状态见第 22 节。
- 硬件检测、档位、字幕/音轨基础选择、取消和缓存清理已补齐，当前状态见第 23 节。
- 当前播放进度使用默认本地用户 `local`，多用户权限属于后续阶段。

## 21. 阶段 4 HLS 转封装当前状态

更新时间：2026-05-02

已完成服务端 HLS 转封装的第一条纵切：

- 已实现播放决策接口：`GET /api/playback/decision/{fileId}`。
- MP4/M4V/MOV/WebM 优先返回直出播放。
- 其他容器优先创建 FFmpeg HLS 会话。
- 已实现 HLS 缓存目录管理，输出到 `cache/transcode/{sessionId}`。
- 已实现 HLS manifest/segment 读取接口：`GET /api/playback/hls/{sessionId}/{assetName}`。
- FFmpeg 参数当前使用视频流 copy、音频转 AAC、忽略内嵌字幕，输出 MPEG-TS HLS。
- Web 播放器已接入播放决策，HLS 模式使用 `hls.js`，Safari 可走原生 HLS。
- 已补充 HLS 服务测试，覆盖 FFmpeg 启动失败反馈和 HLS 资源路径防护。

已验证：

```text
npm install hls.js --fetch-timeout=120000 --fetch-retries=2
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
11 passed, 0 failed
```

当前限制：

- HLS 转码第一版已补齐，当前状态见第 22 节。
- HLS 取消、缓存清理和基础字幕/音轨选择已补齐，当前状态见第 23 节。

## 22. 阶段 4 HLS 转码当前状态

更新时间：2026-05-02

已完成必要时转码的第一条纵切：

- 已实现 `ffprobe` 探测服务：`FfprobeMediaProbeService`。
- 支持通过环境变量 `OMNIPLAY_FFPROBE_PATH` 指定 ffprobe。
- 播放决策已从单纯扩展名判断升级为容器/视频编码/音频编码判断。
- MP4/M4V/MOV 中 H.264 + AAC/MP3 走 Range 直出。
- WebM 中 VP8/VP9/AV1 + Opus/Vorbis 走 Range 直出。
- H.264 视频但音频或容器不适合直出时，走 `hls-remux`：视频 copy，音频转 AAC。
- 非 H.264 视频走 `hls-transcode`：视频转 H.264，音频转 AAC，输出 HLS。
- FFmpeg 转码参数当前使用 `libx264`、`veryfast`、`crf 23`、`yuv420p`。
- HLS 缓存 sessionId 已区分 `remux` 和 `transcode`，避免复用错误产物。
- Web 播放器已兼容 `hls-remux` 和 `hls-transcode` 两种模式。
- 已补充 ffprobe 不可用时的回退测试。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
12 passed, 0 failed
```

当前限制：

- 硬件检测、档位和基础字幕/音轨选择已补齐，当前状态见第 23 节。
- ffprobe 结果回写 `video_files` 已补齐，当前状态见第 24 节。
- HLS 会话仍是本机内存状态加缓存文件，尚未接数据库任务表。

## 23. 阶段 4 播放控制扩展当前状态

更新时间：2026-05-02

已完成硬件检测、档位、字幕/音轨基础选择、取消和缓存清理的第一条纵切：

- 已实现 FFmpeg 能力检测接口：`GET /api/playback/capabilities`。
- 能力检测会读取 `ffmpeg -encoders`，识别 `h264_videotoolbox`、`h264_vaapi`、`h264_qsv`、`h264_nvenc`、`h264_v4l2m2m`。
- 已支持播放决策参数：`quality`、`audioTrackIndex`、`subtitleMode`、`subtitleId`、`hardware`。
- 已支持转码档位：`1080p`、`720p`、`480p`、`360p`。
- 已支持手动选择音轨 index，并映射到 FFmpeg `0:a:{index}`。
- 已实现外挂字幕发现接口：`GET /api/playback/files/{fileId}/subtitles`。
- 已实现 SRT/VTT 外挂字幕 WebVTT 服务：`GET /api/playback/files/{fileId}/subtitles/{subtitleId}.vtt`。
- Web 播放器已支持外挂字幕显示模式和外挂字幕烧录模式。
- HLS 转码已支持外挂字幕烧录到视频滤镜。
- 已实现 HLS 会话停止接口：`POST /api/playback/hls/{sessionId}/stop`。
- 已实现 HLS 缓存清理接口：`POST /api/playback/hls/cleanup`。
- Web 播放器已加入质量、音轨、字幕、硬件转码和缓存清理控件。
- 已补充 FFmpeg 能力检测和缓存清理测试。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
14 passed, 0 failed
```

当前限制：

- 硬件转码默认关闭，需要前端勾选；部分 Linux 硬件编码器仍可能需要后续补设备挂载和 FFmpeg 过滤器细节。
- 音轨语言/标题结构化已补齐，当前状态见第 25 节。
- 外挂字幕支持 SRT/VTT 网页显示，ASS/SSA 目前只适合烧录；内嵌字幕选择还未做。
- 缓存清理按目录年龄清理，尚未接数据库任务表和后台定时任务。

## 24. 阶段 4 媒体探测持久化当前状态

更新时间：2026-05-02

已完成 ffprobe 结果持久化的第一条纵切：

- 已新增 `VideoFileProbeUpdate` 模型。
- 已扩展 `PlayableVideoFile`，包含 `container`、`videoCodec`、`audioCodec`、`subtitleSummary`。
- 已扩展 `VideoFileSummary`，详情接口会返回容器、视频编码、音频编码和字幕摘要。
- 已新增 `ILibraryRepository.UpdateVideoFileProbeAsync`。
- 播放决策成功拿到 ffprobe 结果后，会回写 `video_files.duration_seconds/container/video_codec/audio_codec/subtitle_summary`。
- ffprobe 暂时不可用时，播放决策会回退使用已持久化的探测字段。
- Web 文件列表已显示容器、视频编码、音频编码和字幕摘要。
- 已补充仓储测试，覆盖探测结果回写、详情读取和播放文件读取。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
14 passed, 0 failed
```

当前限制：

- 完整 ffprobe JSON 和结构化音轨/字幕流已补齐，当前状态见第 25 节。
- 扫描阶段批量探测已补齐，当前状态见第 27 节。

## 25. 阶段 4 结构化轨道当前状态

更新时间：2026-05-02

已完成完整 ffprobe JSON 和结构化轨道的第一条纵切：

- `MediaProbeSnapshot` 已扩展 `RawJson` 和 `Streams`。
- `FfprobeMediaProbeService` 会保留完整 ffprobe JSON。
- `VideoFileProbeUpdate` 已扩展 `ProbeJson`。
- `video_files.probe_json` 已在播放探测后写入数据库。
- `VideoFileSummary` 已扩展 `audioTracks` 和 `subtitleStreams`。
- 详情接口会从 `probe_json` 解析音轨/字幕流的 index、编码、语言、标题、声道、默认/强制标记。
- Web 播放器音轨下拉已优先显示真实轨道信息；未探测时回退到固定 index。
- Web 文件列表会显示音轨数量和内嵌字幕数量。
- 已扩展仓储测试，覆盖完整 JSON 解析出的音轨/字幕结构。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
14 passed, 0 failed
```

当前限制：

- 内嵌字幕烧录选择已补齐，当前状态见第 26 节。
- 音轨选择现在使用 ffprobe 的 stream index，复杂文件中后续还需要更明确区分 stream index 与 audio-only 顺序。
- 扫描阶段批量探测已补齐，当前状态见第 27 节。

## 26. 阶段 4 内嵌字幕烧录当前状态

更新时间：2026-05-02

已完成内嵌字幕选择/烧录的第一条纵切：

- Web 字幕下拉已合并外挂字幕和内嵌字幕流。
- 内嵌字幕使用 `embedded_{streamIndex}` 作为字幕选择 ID。
- 选择内嵌字幕时，播放器自动切换到“烧录”模式。
- 后端播放决策已支持识别内嵌字幕 stream index。
- `HlsPlaybackProfile` 已扩展 `EmbeddedSubtitleStreamIndex`。
- HLS 转码缓存 key 已区分内嵌字幕 stream，避免复用错误产物。
- FFmpeg 转码已支持通过 `subtitles='<input>':si={streamIndex}` 烧录内嵌字幕。
- 未选择字幕时，不会因为旧的字幕模式状态误触发转码。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
14 passed, 0 failed
```

当前限制：

- 当前内嵌字幕只能烧录，不能作为浏览器外挂 track 直接显示。
- FFmpeg `subtitles` 滤镜对复杂字幕流的 `si` 行为仍需要在真实 MKV 样本上继续验证。
- 扫描阶段批量探测已补齐，当前状态见第 27 节。

## 27. 阶段 4 扫描阶段媒体探测当前状态

更新时间：2026-05-02

已完成扫描阶段 ffprobe 批量探测的第一条纵切：

- 已将 `IMediaProbeService`、`MediaProbeSnapshot`、`MediaStreamSnapshot` 下沉到 `OmniPlay.Core`，避免 `OmniPlay.Infrastructure` 反向依赖 `OmniPlay.Media`。
- `FfprobeMediaProbeService` 保持在 `OmniPlay.Media`，通过 Core 接口注入到扫描器和播放决策。
- `LibraryScanner` 扫描本地媒体源时会在入库前调用媒体探测服务。
- 新文件会在扫描阶段写入 `duration_seconds`、`container`、`video_codec`、`audio_codec`、`subtitle_summary` 和完整 `probe_json`。
- 已有文件如果文件大小/修改时间变化，或缺少完整 `probe_json`，会重新探测。
- 已有文件如果未变化且已有 `probe_json`，会跳过探测，避免每次全库扫描都重复跑 ffprobe。
- ffprobe 单文件失败不会中断整个扫描，会写入扫描诊断信息。
- Web 详情页在扫描后即可拿到真实音轨和内嵌字幕流，不必等到首次播放决策。
- 播放决策仍会按需探测并回写，作为旧库补齐和播放前校准的兜底路径。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
15 passed, 0 failed
```

当前限制：

- 扫描后台队列、进度推送、取消控制和探测并发/限速已补齐，当前状态见第 28 节。
- ffprobe 不可用或个别文件探测失败时，文件仍会入库，但编码/轨道信息需要后续播放探测或重新扫描补齐。

## 28. 阶段 4 后台扫描任务当前状态

更新时间：2026-05-02

已完成后台扫描队列、进度推送、取消和探测限速的第一条纵切：

- 已新增 `ILibraryScanJobService`，扫描提交和扫描执行解耦。
- `POST /api/library/scan` 现在只提交后台扫描任务，立即返回 `202 Accepted` 和当前扫描状态。
- 已新增 `POST /api/library/scan/cancel`，可请求取消正在运行的扫描。
- 已扩展 `LibraryScanStatus`，包含取消状态、最后错误、是否已取消和当前进度。
- 已新增 `LibraryScanProgress`，记录阶段、媒体源、文件总数、已处理文件、待探测文件和已探测文件。
- 已新增 `GET /api/library/scan/events`，通过 Server-Sent Events 推送扫描状态变化。
- `LibraryScanner` 继续支持直接同步调用，同时支持 `IProgress<LibraryScanProgress>` 上报进度。
- ffprobe 扫描阶段探测已改为有限并发，默认并发 2，环境变量 `OMNIPLAY_SCAN_PROBE_CONCURRENCY` 可配置，范围限制为 1-4。
- Web 工具栏已支持后台扫描提交、运行状态显示、进度条和取消按钮。
- Web 会优先使用 SSE 接收扫描状态，SSE 不可用时回退到轮询状态接口。
- OpenAPI 已同步新增后台扫描、状态流和取消接口。
- 已补充后台扫描任务测试，覆盖后台运行、重复提交拒绝和取消状态。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
17 passed, 0 failed
```

当前限制：

- 后台扫描任务目前是进程内单任务队列，服务重启后不会恢复未完成扫描。
- 刮削后台队列、进度推送和取消机制已补齐，当前状态见第 29 节。
- 探测并发只限制 ffprobe 扫描阶段；后续可按 NAS 型号自动选择更细的 CPU/IO 配额。

## 29. 阶段 4 后台刮削任务当前状态

更新时间：2026-05-02

已完成后台刮削队列、进度推送和取消的第一条纵切：

- 已新增 `ILibraryMetadataEnrichmentJobService`，刮削提交和执行解耦。
- 已新增 `IMetadataEnrichmentStatusStore`，保存当前/最近一次刮削状态。
- 已新增 `LibraryMetadataEnrichmentStatus`，包含运行状态、取消状态、最后结果、最后错误和当前进度。
- 已新增 `LibraryMetadataEnrichmentProgress`，记录阶段、目标条目数、已处理条目、匹配数、更新数、海报下载数和当前条目。
- `LibraryMetadataEnricher` 保留原同步调用方式，同时支持 `IProgress<LibraryMetadataEnrichmentProgress>` 上报进度。
- `POST /api/library/scrape` 现在提交后台刮削任务，立即返回 `202 Accepted` 和当前状态。
- `POST /api/library/items/{id}/rescrape` 现在提交单条目后台重刮削任务。
- 已新增 `GET /api/library/scrape/status`、`GET /api/library/scrape/events` 和 `POST /api/library/scrape/cancel`。
- Web 工具栏已支持后台刮削提交、运行状态显示、进度条和取消按钮。
- Web 会优先使用 SSE 接收刮削状态，SSE 不可用时回退到状态轮询。
- OpenAPI 已同步新增刮削状态、状态流和取消接口。
- 已补充后台刮削任务测试，覆盖后台运行、重复提交拒绝、单条目目标记录和取消状态。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
20 passed, 0 failed
```

当前限制：

- 后台刮削任务目前是进程内单任务队列，服务重启后不会恢复未完成刮削。
- 扫描、刮削和转码缓存清理已经接入统一任务中心，当前状态见第 30 节。
- Web 详情页单条目重刮削入口已补齐，当前状态见第 31 节。

## 30. 阶段 4 统一任务中心当前状态

更新时间：2026-05-02

已完成统一任务中心和基础资源调度的第一条纵切：

- 已新增 `IBackgroundTaskCenter`，作为扫描、刮削、转码缓存清理等重任务的统一调度入口。
- 已新增 `BackgroundTaskStatus`、`BackgroundTaskProgress`、`BackgroundTaskSnapshot`。
- 已实现进程内单任务调度：同一时间只允许运行一个重任务，避免扫描、刮削、缓存清理同时挤占 NAS CPU/IO。
- 已新增 `GET /api/tasks`，返回当前和最近后台任务列表。
- 已新增 `GET /api/tasks/events`，通过 Server-Sent Events 推送统一任务快照。
- 已新增 `POST /api/tasks/{taskId}/cancel`，可按任务 ID 取消正在运行的任务。
- 扫描后台任务已接入统一任务中心，同时保留原扫描状态接口和事件接口。
- 刮削后台任务已接入统一任务中心，同时保留原刮削状态接口和事件接口。
- `POST /api/playback/hls/cleanup` 已改为提交后台缓存清理任务，返回 `202 Accepted`。
- Web 首页已新增任务中心视图，可显示最近任务、运行进度、结果/错误和取消按钮。
- 播放器里的缓存清理按钮现在提交统一后台任务，不再阻塞请求等待清理完成。
- OpenAPI 已同步新增统一任务中心接口，并更新缓存清理接口语义。
- 已补充统一任务中心测试，覆盖单任务互斥、结果保存和按 ID 取消。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
22 passed, 0 failed
```

当前限制：

- 任务中心目前是进程内状态，服务重启后不会恢复历史任务和未完成任务。
- 当前资源调度是全局互斥，后续可以细分为 CPU、磁盘 IO、网络 IO、转码等资源权重。
- HLS 播放中的 FFmpeg 转码会话仍由播放器按需启动和停止，暂未纳入任务中心；任务中心目前管理扫描、刮削、缓存清理这类后台维护任务。

## 31. 阶段 4 详情页单条目重刮削当前状态

更新时间：2026-05-03

已完成详情页单条目重刮削入口：

- Web 详情页顶部已新增重刮削按钮，调用 `POST /api/library/items/{id}/rescrape`。
- 单条目重刮削会进入统一任务中心，不会和扫描、全库刮削、缓存清理并发抢资源。
- 详情页已嵌入任务中心视图，可显示当前任务进度、结果、错误和取消按钮。
- 单条目重刮削完成后，如果仍停留在当前详情页，会自动刷新详情和海报墙数据。
- 重刮削提交失败时，会在详情页显示错误文本。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
22 passed, 0 failed
```

当前限制：

- 手动搜索候选、选择匹配项和锁定元数据流程已补齐，当前状态见第 32 节。
- 详情页任务中心复用首页任务视图，后续可做成统一侧栏或弹窗，提高多页面一致性。

## 32. 阶段 4 手动匹配和元数据锁定当前状态

更新时间：2026-05-03

已完成手动搜索 TMDB 候选、应用匹配项和锁定元数据的第一条纵切：

- `LibraryItemSummary` / `LibraryItemDetail` 已暴露 `isLocked`。
- 已扩展 `ITmdbMetadataClient.SearchCandidatesAsync`，可返回多个 TMDB 候选项。
- 已新增 `GET /api/library/items/{id}/metadata/search`，支持按 query/mediaType/year 搜索候选。
- 已新增 `POST /api/library/items/{id}/metadata/apply`，可应用用户选择的候选，下载海报，并默认锁定该条目。
- 已新增 `POST /api/library/items/{id}/metadata/lock`，支持锁定/解锁条目元数据。
- `LibraryRepository` 已支持应用手动匹配和切换锁定状态。
- Web 详情页已新增手动匹配按钮，搜索候选后在详情页内展示候选列表。
- Web 详情页可一键应用候选；应用后刷新详情/海报墙，并显示“已锁定”状态。
- Web 详情页已新增锁定/解锁按钮，锁定后自动刮削不会覆盖该条目。
- OpenAPI 已同步新增手动搜索、应用匹配和锁定接口。
- 已补充仓储测试，覆盖手动应用匹配、字段更新、锁定和解锁。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
23 passed, 0 failed
```

当前限制：

- 手动匹配候选海报缩略图和按 TMDB ID 拉取详情已补齐，当前状态见第 33 节。
- 当前手动匹配还未拉取季信息或演职员信息。

## 33. 阶段 4 候选海报缩略图和 TMDB 详情拉取当前状态

更新时间：2026-05-03

已完成手动匹配体验的第二条纵切：

- 已新增 `ITmdbMetadataClient.GetDetailsAsync`，可按 TMDB ID 拉取电影或剧集完整详情。
- `POST /api/library/items/{id}/metadata/apply` 已改为优先使用 TMDB 详情接口返回的数据，再回退到用户提交的候选数据。
- Web 手动匹配候选列表已显示 TMDB 海报缩略图，便于区分同名影片和翻拍版本。
- Web 已新增 TMDB 海报 URL 辅助函数，候选海报直接走 TMDB image CDN，不写入本地海报缓存。
- 应用候选后仍会下载正式海报到 NAS 本地缓存，并默认锁定元数据，避免后续自动刮削覆盖。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
23 passed, 0 failed
```

当前限制：

- 详情拉取目前只落库标题、简介、年份、评分和海报；季信息、演职员信息和更多图片仍待后续扩展。
- TMDB 候选海报缩略图依赖浏览器可访问 TMDB image CDN；正式应用后的海报仍会缓存到 NAS。
- TMDB 匹配来源落库和详情页回显已补齐，当前状态见第 34 节。

## 34. 阶段 4 TMDB 匹配来源落库当前状态

更新时间：2026-05-03

已完成 TMDB 匹配来源记录的第一条纵切：

- 手动应用 TMDB 候选后，会把 `tmdb_id` 写入 `movies` 或 `tv_shows`。
- 自动刮削命中 TMDB 后，同样会把 `tmdb_id` 写入对应媒体表。
- 如果旧库里缺少 `movies` 行，写入时会按 `library_item_id` 补齐；剧集继续复用扫描阶段的 `tv_shows` 行。
- 详情接口 `GET /api/library/items/{id}` 已新增 `tmdbId` 回显。
- Web 详情页会在元信息栏显示当前 TMDB ID，便于确认手动匹配结果。
- 已补充测试，覆盖手动匹配和自动刮削写入 TMDB ID 后的详情回显。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
24 passed, 0 failed
```

当前限制：

- 目前只记录 TMDB ID，尚未记录 IMDB/TVDB 等外部 ID。
- 基于已保存 TMDB ID 的精确重刮削已补齐，当前状态见第 35 节。

## 35. 阶段 4 基于 TMDB ID 的精确重刮削当前状态

更新时间：2026-05-03

已完成重刮削精确刷新纵切：

- 自动刮削候选查询已带出 `movies.tmdb_id` / `tv_shows.tmdb_id`。
- 如果条目已有 `tmdb_id`，重刮削优先调用 TMDB 详情接口按 ID 刷新，不再按标题重新搜索。
- 如果条目没有 `tmdb_id`，仍回退到原有标题 + 年份搜索流程。
- 后台任务新增 `fetching-details` 阶段，任务中心和 Web 详情页显示为“精确刷新 TMDB”。
- 精确刷新失败时会记录诊断信息，并继续处理后续条目。
- 已补充测试，覆盖已有 TMDB ID 时不调用搜索接口、只调用详情接口，并正确回写新详情。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
25 passed, 0 failed
```

当前限制：

- 剧集已有本地季/集的基础元数据刷新已补齐，当前状态见第 36 节。
- 已锁定条目仍不会被自动重刮削覆盖；需要先解锁或走手动匹配。

## 36. 阶段 4 剧集季集基础元数据刷新当前状态

更新时间：2026-05-03

已完成剧集分集信息刷新的第一条纵切：

- `ITmdbMetadataClient` 已新增 `GetSeasonAsync`，按 TMDB 剧集 ID 和季号读取季详情。
- TMDB 客户端已接入 `/tv/{id}/season/{season_number}`，解析季标题、季简介、播出日期、海报路径和分集列表。
- 刮削剧集条目时会刷新本地已存在的季和集，写入季标题、分集标题、分集简介和分集播出日期。
- 只更新 NAS 本地已有文件对应的分集，不会创建 TMDB 上有但本地没有的视频条目。
- 后台任务新增 `fetching-episodes` 阶段，任务中心和 Web 详情页显示为“刷新分集信息”。
- Web 分集卡片已显示播出日期和分集简介，仍保留文件名用于定位实际播放文件。
- 已补充测试，覆盖剧集重刮削后本地已有两集被更新、TMDB 上额外分集不会被创建。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
25 passed, 0 failed
```

当前限制：

- 分集剧照和季海报缓存已补齐，当前状态见第 37 节。
- 暂未落库演职员、预告片、外部 ID 和 TMDB 图片集。

## 37. 阶段 4 分集剧照和季海报缓存当前状态

更新时间：2026-05-03

已完成剧集图片资产缓存纵切：

- TMDB 图片下载逻辑已复用到分集剧照，分集剧照写入 `cache/thumbnails`。
- 已新增 `ThumbnailAsset`、`IThumbnailAssetRepository`、`ThumbnailAssetRepository` 和 `GET /api/assets/thumbnails/{id}`。
- 剧集刮削时会下载季海报并写入 `poster_assets`，同时回写 `seasons.poster_asset_id`。
- 剧集刮削时会下载本地已有分集的 `still_path`，写入 `thumbnail_assets`，同时回写 `episodes.still_asset_id`。
- 远端存在但本地没有视频文件的分集不会下载剧照，也不会创建本地分集。
- 详情接口已回显季海报 ID；Web 详情页会显示季海报和分集剧照。
- 已补充/扩展测试，覆盖季海报 ID、分集剧照 ID 和额外远端分集不落库。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
26 passed, 0 failed
```

当前限制：

- 缩略图和海报孤儿缓存清理已纳入统一任务中心，当前状态见第 38 节。
- 暂未下载 TMDB 图片集、演职员头像、预告片封面。

## 38. 阶段 4 图片缓存清理任务当前状态

更新时间：2026-05-03

已完成海报/缩略图缓存清理纵切：

- 已新增 `AssetCacheCleanupSummary` 和 `IAssetCacheCleanupService`。
- 已新增 `AssetCacheCleanupService`，扫描 `poster_assets`、`thumbnail_assets`、`cache/posters` 和 `cache/thumbnails`。
- 清理策略只删除孤儿缓存：仍被 `library_items`、`seasons`、`episodes` 或有效 `video_files` 引用的资产不会删除。
- 会删除不再被数据库引用的海报/缩略图资产记录和对应本地文件。
- 会清理缓存目录中没有数据库记录引用的残留图片文件。
- 已新增 `POST /api/assets/cache/cleanup`，通过统一任务中心提交 `asset-cache-cleanup` 后台任务。
- Web 首页工具栏已新增图片缓存清理按钮，任务进度和取消复用统一任务中心。
- OpenAPI 已同步新增图片缓存清理接口。
- 已补充测试，覆盖被引用资产保留、孤儿资产记录删除、孤儿文件删除和目录残留文件删除。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
27 passed, 0 failed
```

当前限制：

- 清理结果目前只返回文件数、记录数和字节数，未提供逐文件明细。
- 缓存维护面板已补齐，当前状态见第 39 节。

## 39. 阶段 4 缓存维护面板当前状态

更新时间：2026-05-03

已完成缓存容量统计和统一维护入口：

- 已新增 `CacheUsageSummary`、`CacheUsageBucket` 和 `ICacheUsageService`。
- 已新增 `CacheUsageService`，统计 `cache/posters`、`cache/thumbnails`、`cache/transcode` 的文件数和字节数。
- 已新增 `GET /api/cache/status`，返回海报、剧照、HLS 和总缓存占用。
- Web 首页已新增缓存维护区，显示总占用、图片占用、HLS 占用和缓存文件数。
- Web 缓存维护区已提供“图片”和“HLS”两个清理按钮，分别提交图片缓存清理和 HLS 转码缓存清理任务。
- 缓存清理任务完成后，Web 会自动刷新缓存容量统计。
- OpenAPI 已同步新增缓存状态接口。
- 已补充测试，覆盖海报、剧照和 HLS 缓存容量分桶统计。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
29 passed, 0 failed
```

当前限制：

- 缓存维护区仍只提供分项容量和清理入口，未显示缓存增长趋势或历史清理记录。
- 缓存保留策略设置已补齐，当前状态见第 40 节。

## 40. 阶段 4 缓存保留策略设置当前状态

更新时间：2026-05-03

已完成缓存策略配置和持久化：

- `AppSettingsSnapshot` 已新增 `cache` 节点。
- 已新增 `CacheSettings`，包含 `hlsRetentionHours` 和 `imageCleanupScope`。
- `AppSettingsRepository` 已通过 `app_settings` 持久化缓存策略，并做范围归一化：HLS 保留时间限制为 1-720 小时。
- HLS 缓存清理接口默认读取 `cache.hlsRetentionHours`；仍保留 `maxAgeHours` 查询参数用于临时覆盖。
- 图片缓存清理接口默认读取 `cache.imageCleanupScope`，支持 `orphans-and-untracked` 和 `orphans-only`。
- Web 缓存维护区已新增 HLS 保留小时输入框、图片清理范围选择器和保存按钮。
- OpenAPI 已同步更新设置接口说明。
- 已补充测试，覆盖缓存设置持久化/归一化，以及“仅孤儿”范围不会删除目录残留文件。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
29 passed, 0 failed
```

当前限制：

- 缓存维护区尚未显示历史清理记录或容量趋势。
- 图片清理范围目前只有两档，尚未细分海报和剧照分别清理。
- 独立设置面板已补齐，当前状态见第 41 节。

## 41. 阶段 4 Web 设置面板当前状态

更新时间：2026-05-03

已完成 Web 设置入口和集中配置面板：

- Web 顶部工具栏设置按钮已接入右侧设置抽屉。
- 设置面板已支持 TMDB 元数据刮削、海报下载、内置公开源、语言、API Key 和 Access Token 配置。
- 设置面板已支持 HLS 缓存保留时间和图片清理范围配置。
- 保存时复用 `PUT /api/settings`，并更新前端 `settings` 快照，缓存维护区会同步读取最新缓存策略。
- 移动端使用同一抽屉布局，表单控件使用固定高度和自适应宽度，避免文本/输入框溢出。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
29 passed, 0 failed
```

当前限制：

- 播放策略设置已开放 Web 可写配置，当前状态见第 42 节。
- 设置面板尚未拆到 `web/src/features/settings/` 独立模块。

## 42. 阶段 4 播放策略设置当前状态

更新时间：2026-05-03

已完成播放策略配置闭环：

- `AppSettingsUpdateRequest` 已新增 `playback` 节点。
- `PlaybackSettings` 默认启用 Range 直出、HLS 转封装和 HLS 转码，避免升级后破坏现有播放路径。
- `AppSettingsRepository` 已通过 `app_settings` 持久化播放策略；如果三种策略都被关闭，会归一化回默认策略。
- 播放决策接口 `GET /api/playback/decision/{fileId}` 已读取播放策略，并按配置决定直出、转封装、转码、降级直出或返回不可用原因。
- Web 设置面板已新增 Range 直出、HLS 转封装、HLS 转码三个开关，并禁止保存空策略。
- OpenAPI 已同步更新设置接口说明。
- 已补充测试，覆盖播放策略持久化、空策略归一化，以及只更新缓存时保留播放策略。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
32 passed, 0 failed
```

当前限制：

- 播放策略目前是全局设置，尚未按用户、设备或客户端类型区分。
- 播放策略变化不会主动终止已经在运行的 HLS 会话，只影响新的播放决策。
- 媒体源管理中心已补齐，当前状态见第 43 节。

## 43. 阶段 4 媒体源管理中心当前状态

更新时间：2026-05-03

已完成本地媒体源管理闭环：

- 已新增 `UpdateMediaSourceRequest`。
- `IMediaSourceRepository` / `MediaSourceRepository` 已支持重命名、启用/停用和移除媒体源。
- 新增接口 `PATCH /api/sources/{sourceId}`，用于重命名或启停媒体源。
- 新增接口 `DELETE /api/sources/{sourceId}`，用于从管理列表移除媒体源，并阻止后续扫描继续使用该源。
- 同一路径被移除后再次添加会恢复原媒体源 ID，并重新启用。
- Web 顶部媒体源按钮已打开正式媒体源管理抽屉，不再使用浏览器 prompt。
- Web 媒体源管理抽屉已支持新增本地目录、重命名、启停和移除。
- OpenAPI 已同步新增媒体源更新和删除接口。
- 已补充测试，覆盖媒体源重命名/启停、移除隐藏、同路径恢复。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
35 passed, 0 failed
```

当前限制：

- 媒体源库清理任务已补齐，当前状态见第 44 节。
- 本阶段仍只支持 NAS 本地目录；WebDAV/SMB 和 NAS 原生路径选择器尚未接入。

## 44. 阶段 4 媒体源库清理任务当前状态

更新时间：2026-05-03

已完成媒体源移除后的后台清理：

- 已新增 `IMediaSourceCleanupService` 和 `MediaSourceCleanupService`。
- 已新增 `MediaSourceCleanupSummary`，统计清理的视频文件、播放进度、转码任务和孤儿库条目数量。
- `DELETE /api/sources/{sourceId}` 已改为提交统一后台任务 `media-source-cleanup`，不再同步阻塞请求。
- 清理任务会先移除媒体源，再删除该源关联的 `video_files`、`playback_progress` 和 `transcode_jobs`；视频缩略图资产会先与视频文件解绑，交给图片缓存清理处理。
- 清理任务会删除不再有可播放视频文件引用的 `library_items`，并同步清理其 `movies`、`tv_shows`、`seasons`、`episodes` 元数据。
- 如果同一个影视条目仍有其他媒体源的视频文件，清理任务会保留该影视条目和其他源的视频/播放进度。
- Web 删除媒体源后会把清理任务放入统一任务中心；任务完成后自动刷新媒体源和海报墙数据。
- OpenAPI 已同步更新媒体源删除接口语义为后台任务。
- 已补充测试，覆盖只清理被移除源的数据，并保留其他媒体源仍引用的同一影视条目。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
36 passed, 0 failed
```

当前限制：

- 图片缓存联动清理已补齐，当前状态见第 45 节。
- 清理任务取消后可能已经完成媒体源移除但尚未完成库清理，可再次对同一 sourceId 提交删除请求重试清理。

## 45. 阶段 4 图片缓存联动清理当前状态

更新时间：2026-05-03

已完成媒体源清理后的图片缓存联动回收：

- `media-source-cleanup` 任务在库索引清理完成后，会继续调用 `AssetCacheCleanupService.CleanupOrphansAsync`。
- 联动清理复用 `cache.imageCleanupScope` 设置；如果设置为 `orphans-only`，仍会删除因媒体源移除而变成孤儿资产的海报/剧照记录和文件。
- `MediaSourceCleanupService` 在删除视频文件前会把相关 `thumbnail_assets.video_file_id` 置空，避免外键级联直接删除资产记录，从而保证图片缓存清理可以按孤儿资产回收文件本体。
- 统一任务中心仍只显示一个 `media-source-cleanup` 任务，进度会从媒体源库清理平滑过渡到图片缓存清理。
- 任务完成结果会同时包含库清理数量和图片缓存释放字节数。
- 已补充测试，覆盖媒体源清理后再执行图片缓存清理可删除孤儿海报和缩略图文件。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
37 passed, 0 failed
```

当前限制：

- 联动清理只处理 OmniPlay 自身缓存目录，不会删除用户媒体目录里的任何原始视频或字幕文件。
- NAS 路径选择器已补齐，当前状态见第 46 节。

## 46. 阶段 4 NAS 路径选择器当前状态

更新时间：2026-05-03

已完成本地目录浏览和选择：

- 已新增 `ILocalDirectoryBrowser` 和 `LocalDirectoryBrowser`。
- 已新增 `LocalDirectoryBrowseResult` / `LocalDirectoryEntry`，返回当前目录、上级目录和子目录列表。
- 新增接口 `GET /api/sources/local/directories?path=...`，用于浏览 NAS 本地目录。
- 目录浏览会只返回目录项，跳过文件；对不可读目录会标记 `isReadable=false`，前端禁止进入。
- Web 媒体源管理抽屉已新增目录浏览区，支持打开路径、返回上级目录、进入子目录、选择当前目录填入新增媒体源表单。
- OpenAPI 已同步新增目录浏览接口。
- 已补充测试，覆盖目录列表只返回目录、回显上级目录、缺失目录报错。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
39 passed, 0 failed
```

当前限制：

- 目录浏览目前不做 DSM/fnOS 原生授权目录选择，只按服务进程权限读取本机文件系统。
- 暂未隐藏系统目录，后续打包时可按群晖/飞牛运行环境增加默认入口和过滤规则。
- 媒体源扫描入口联动已补齐，当前状态见第 47 节。

## 47. 阶段 4 媒体源扫描入口联动当前状态

更新时间：2026-05-03

已完成媒体源管理里的扫描联动：

- `media_sources` 已新增 `last_scanned_at` 字段；初始化会自动为旧库补列。
- `MediaSourceSummary` 已回显 `lastScannedAt`。
- `LibraryScanner` 已支持 `ScanSourceAsync(sourceId)`，可以只扫描一个启用的本地媒体源。
- 新增接口 `POST /api/sources/{sourceId}/scan`，用于从媒体源管理里提交单个媒体源扫描。
- `LibraryScanJobService` 已支持按媒体源提交后台扫描，仍复用统一任务中心的互斥调度和扫描状态 SSE。
- 扫描完成后会更新该媒体源的 `last_scanned_at`。
- Web 媒体源管理抽屉已显示每个媒体源的上次扫描时间；扫描运行中会显示当前媒体源扫描进度。
- Web 媒体源管理抽屉已新增单个媒体源扫描按钮，添加媒体源后可直接提交扫描。
- OpenAPI 已同步新增单源扫描接口。
- 已补充测试，覆盖只扫描指定媒体源并写入 `lastScannedAt`。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
40 passed, 0 failed
```

当前限制：

- 单源扫描仍与全库扫描、刮削、缓存清理互斥执行，不支持并行扫描多个媒体源。
- 当前扫描状态仍是全局状态对象，前端通过当前媒体源名映射到媒体源行。

## 48. 阶段 4 WebDAV 媒体源第一阶段当前状态

更新时间：2026-05-03

已完成 WebDAV 媒体源的连接信息管理：

- 已新增 `AddWebDavMediaSourceRequest` 和 `POST /api/sources/webdav`。
- `MediaSourceRepository` 已支持添加或重新启用 WebDAV 媒体源。
- WebDAV 地址会规范化为 http/https 绝对地址，去掉 query、fragment 和 URL 内嵌用户信息。
- WebDAV 媒体源写入 `media_sources.kind=webdav`，启停、重命名、删除复用现有媒体源管理能力。
- WebDAV 用户名和密码会写入 `media_source_credentials`，并通过 `media_sources.auth_reference` 关联。
- 重新添加已删除的同一 WebDAV 地址会恢复原媒体源 id，并替换旧凭据。
- Web 媒体源抽屉已新增 WebDAV 地址、名称、用户名、密码表单。
- Web 媒体源列表已能显示 WebDAV 类型，当前禁止对 WebDAV 源提交扫描。
- OpenAPI 已同步新增 `/api/sources/webdav`。
- 已补充测试，覆盖 WebDAV 新增与凭据保存、恢复已删除源并更新凭据、非法 URL 拒绝。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
43 passed, 0 failed
```

当前 Web 构建产物：

```text
web/dist/assets/index-CBuA0Krz.css
web/dist/assets/index-DlNM-ala.js
```

当前限制：

- 本阶段只保存和管理 WebDAV 连接信息，尚未实现 WebDAV 目录浏览、连接测试、扫描、播放和缓存策略。
- WebDAV 凭据目前以数据库 JSON 形式保存，尚未接入 NAS 系统密钥链或本机加密密钥。

## 49. 阶段 4 WebDAV 连接测试与目录浏览当前状态

更新时间：2026-05-03

已完成 WebDAV 连接测试和目录浏览第一版：

- 已新增 `IWebDavDirectoryBrowser` 和 `WebDavDirectoryBrowser`。
- WebDAV 客户端使用标准 `PROPFIND`，不引入额外 NuGet 依赖。
- 新增接口 `POST /api/sources/webdav/test`，用 `Depth: 0` 测试 WebDAV 地址和 Basic 凭据。
- 新增接口 `POST /api/sources/webdav/browse`，用 `Depth: 1` 浏览当前 WebDAV 目录的子目录。
- 浏览结果会跳过当前目录自身和普通文件，只返回可继续进入的 collection 目录。
- WebDAV 地址解析会去掉 query、fragment 和 URL 内嵌用户信息，并把目录 URL 规范化。
- Web 媒体源抽屉已新增“测试”“浏览”按钮，浏览目录时可进入子目录或返回上级目录。
- 浏览到的当前目录会回填到 WebDAV 地址输入框，添加媒体源时可直接保存该目录。
- OpenAPI 已同步新增 `/api/sources/webdav/test` 和 `/api/sources/webdav/browse`。
- 已补充测试，覆盖 PROPFIND Depth、Basic Auth、目录过滤、父级 URL 和 401 认证失败。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
46 passed, 0 failed
```

当前 Web 构建产物：

```text
web/dist/assets/index-CoNCDBx2.css
web/dist/assets/index-Btgku2WJ.js
```

当前限制：

- WebDAV 浏览已接入扫描器，当前状态见第 50 节；媒体探测、播放和缓存仍未接入。
- 认证当前只支持 Basic 用户名/密码，尚未支持 Digest、OAuth 或 NAS 系统凭据托管。
- 部分 WebDAV 服务如果返回非标准 XML 或自定义权限状态，后续需要按真实服务适配。

## 50. 阶段 4 WebDAV 扫描第一阶段当前状态

更新时间：2026-05-03

已完成 WebDAV 媒体源扫描第一阶段：

- 已新增 `IWebDavFileEnumerator` 和 `WebDavFileEntry`。
- `WebDavDirectoryBrowser` 现在同时支持目录浏览和递归文件枚举。
- WebDAV 文件枚举使用 `Depth: 1` 逐层递归，避免一次性深度扫描拖垮服务端。
- `LibraryScanner` 已从只扫描本地源扩展为扫描启用的本地和 WebDAV 媒体源。
- 全库扫描会包含 WebDAV 源；单个媒体源扫描也支持 WebDAV 源。
- WebDAV 扫描会读取 `media_source_credentials` 中的 Basic 凭据，并传给 WebDAV 枚举器。
- WebDAV 扫描会按现有视频扩展名过滤远程文件，复用媒体名解析、电影/剧集归类、缺失文件标记和 `last_scanned_at` 写入逻辑。
- WebDAV 普通文件、说明文件等非视频文件不会入库。
- WebDAV 媒体源管理行已启用单源扫描按钮。
- 已补充测试，覆盖 WebDAV 递归文件枚举、相对路径保留、WebDAV 扫描入库、凭据传递、非视频过滤和跳过 ffprobe 探测。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
48 passed, 0 failed
```

当前 Web 构建产物：

```text
web/dist/assets/index-CoNCDBx2.css
web/dist/assets/index-DHe8WYzL.js
```

当前限制：

- WebDAV 文件已支持缓存后播放，当前状态见第 51 节。
- WebDAV 扫描暂不执行 ffprobe，列表中的时长、编码、音轨、字幕信息会在首次播放缓存后补齐。
- WebDAV 递归扫描没有资源调度细化和目录级断点续扫，超大目录后续应接统一任务中心的更细粒度进度和取消点。

## 51. 阶段 4 WebDAV 播放第一阶段当前状态

更新时间：2026-05-03

已完成 WebDAV 播放第一阶段：

- 已新增 `IPlayableFileResolver` 和 `PlayableFileResolver`。
- 播放端点不再直接从 `LibraryRepository.GetPlayableVideoFileAsync` 取本地路径，而是通过播放文件解析层统一解析。
- 本地媒体源仍按原有逻辑校验媒体根目录和文件存在性。
- WebDAV 媒体源播放时会按 `base_url + relative_path` 构造远程文件 URL。
- WebDAV 下载会使用已保存的 Basic 凭据。
- WebDAV 文件会先下载到 `cache/webdav`，再复用现有 Range 直出、HLS 转封装、HLS 转码、硬件转码、音轨选择、字幕烧录等播放链路。
- 缓存文件名按视频 id、相对路径、源地址和文件大小生成稳定哈希；缓存大小匹配时会复用本地文件，不重复下载。
- 首次播放 WebDAV 文件时，`ffprobe` 会对本地缓存执行探测，并回写时长、编码、音轨、字幕等媒体信息。
- `/api/cache/status` 已新增 `webdav` 缓存桶，用于显示远程播放缓存占用。
- 已补充测试，覆盖 WebDAV 播放下载、URL 编码、Basic Auth、缓存命中复用和 WebDAV 缓存统计桶。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
49 passed, 0 failed
```

当前 Web 构建产物：

```text
web/dist/assets/index-CoNCDBx2.css
web/dist/assets/index-DHe8WYzL.js
```

当前限制：

- WebDAV 首次播放需要先完整下载远程文件，超大文件开始播放会等待缓存完成；播放前缓存进度和边下边播仍待后续实现。
- WebDAV 缓存已接入清理任务和保留策略，当前状态见第 52 节。
- WebDAV 外挂字幕远程发现和缓存已补齐，当前状态见第 54 节。

## 52. 阶段 4 WebDAV 缓存管理当前状态

更新时间：2026-05-03

已完成 WebDAV 缓存管理第一阶段：

- 已新增 `WebDavCacheCleanupSummary` 和 `IWebDavCacheCleanupService`。
- 已新增 `WebDavCacheCleanupService`，扫描 `cache/webdav` 并按最大保留时间清理过期文件。
- `CacheSettings` 已新增 `webDavRetentionHours`，默认 72 小时，范围 1 到 720 小时。
- WebDAV 播放缓存命中或下载完成时会刷新文件访问时间，清理任务会按访问时间和修改时间中的较新值判断是否过期。
- 新增接口 `POST /api/playback/webdav/cache/cleanup`，支持可选 `maxAgeHours` 临时覆盖。
- WebDAV 缓存清理已接入统一任务中心，任务类型为 `webdav-cache-cleanup`，与扫描、刮削、HLS 清理等重任务互斥。
- Web 缓存维护条已显示 WebDAV 缓存占用，并新增 WebDAV 保留小时输入和 WebDAV 清理按钮。
- 全局设置抽屉也已新增 WebDAV 保留小时。
- OpenAPI 已同步新增 WebDAV 缓存清理接口。
- 已补充测试，覆盖 WebDAV 过期缓存删除、未过期缓存保留、缓存设置归一化和缓存统计桶。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
50 passed, 0 failed
```

当前 Web 构建产物：

```text
web/dist/assets/index-CoNCDBx2.css
web/dist/assets/index-6YwdRg8k.js
```

当前限制：

- 播放前缓存进度已接入，当前状态见第 53 节。
- WebDAV 清理目前只按时间清理，不支持按总容量上限自动淘汰。

## 53. 阶段 4 WebDAV 播放前缓存进度当前状态

更新时间：2026-05-03

已完成 WebDAV 播放前缓存进度第一阶段：

- 已新增 `PlaybackCacheStatus` 和 `IPlaybackCacheService`。
- 已新增 `WebDavPlaybackCacheService`，按视频文件维护 WebDAV 下载状态、总字节数、已下载字节数、百分比、错误和取消状态。
- 新增接口 `GET /api/playback/files/{fileId}/cache`，读取本地或 WebDAV 播放缓存状态。
- 新增接口 `POST /api/playback/files/{fileId}/cache/prepare`，启动 WebDAV 播放缓存准备；本地文件会直接返回 ready。
- 新增接口 `POST /api/playback/files/{fileId}/cache/cancel`，取消正在进行的 WebDAV 下载。
- Web 播放器现在会先启动播放缓存准备并轮询状态；WebDAV 缓存完成后再请求外挂字幕和播放决策。
- Web 播放器会在遮罩层显示 WebDAV 缓存百分比、已下载/总大小，并在下载中显示取消按钮。
- `PlayableFileResolver` 仍保留兜底下载能力，直接请求播放决策或流接口时不会失效。
- OpenAPI 已同步新增播放缓存状态、准备和取消接口。
- 已补充测试，覆盖 WebDAV 播放缓存准备、ready 状态、百分比、缓存复用不重复下载。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
51 passed, 0 failed
```

当前 Web 构建产物：

```text
web/dist/assets/index-CtoGJU0Y.css
web/dist/assets/index-C_DyqqnH.js
```

当前限制：

- WebDAV 直出播放的 Range 分段缓存已接入，当前状态见第 55 节。
- 取消按钮只取消播放前缓存下载，不会取消已经启动的 HLS 转码任务；HLS 仍通过现有停止接口处理。

## 54. 阶段 4 WebDAV 外挂字幕当前状态

更新时间：2026-05-03

已完成 WebDAV 外挂字幕第一阶段：

- 已新增 `IPlaybackSubtitleService` 和 `PlaybackSubtitleService`，统一接管本地与 WebDAV 外挂字幕发现、字幕 id 解析和字幕文件路径解析。
- `GET /api/playback/files/{fileId}/subtitles` 已不再依赖 `PlayableFileResolver`，WebDAV 字幕列表不会因为查询字幕而触发视频文件下载。
- 本地媒体源继续按视频同目录、同名前缀发现 `.srt`、`.vtt`、`.ass`、`.ssa` 外挂字幕。
- WebDAV 媒体源会对视频所在远程目录执行 `PROPFIND Depth: 1`，发现同目录、同名前缀的 `.srt`、`.vtt`、`.ass`、`.ssa` 字幕。
- WebDAV 字幕发现和下载会复用已保存的 Basic 凭据。
- WebDAV 字幕在首次使用时下载到 `cache/webdav/subtitles`，缓存 key 按视频 id、远程字幕 URL 和字幕大小生成；大小匹配时会复用缓存文件。
- `GET /api/playback/files/{fileId}/subtitles/{subtitleId}.vtt` 已支持把 WebDAV 缓存后的 `.srt/.vtt` 转为 WebVTT 输出。
- 播放决策中的外挂字幕烧录已支持 WebDAV 远程字幕：选择烧录模式时会先缓存字幕，再把本地缓存路径传给 FFmpeg。
- WebDAV 缓存清理会递归处理 `cache/webdav`，因此字幕缓存会随现有 WebDAV 缓存保留策略一起清理。
- OpenAPI 已同步更新字幕接口说明。
- 已补充测试，覆盖 WebDAV 同目录字幕发现、语言识别、WebVTT URL、Basic Auth、字幕缓存和缓存复用。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
52 passed, 0 failed
```

当前 Web 构建产物：

```text
web/dist/assets/index-CtoGJU0Y.css
web/dist/assets/index-C_DyqqnH.js
```

当前限制：

- WebDAV 外挂字幕只发现视频同目录、同名前缀字幕，不做跨目录字幕库搜索。
- WebDAV 字幕发现当前每次调用都会实时 `PROPFIND`，尚未把远程字幕清单持久化到数据库。
- `.ass/.ssa` 仍只适合烧录；网页 `<track>` 只直接支持 `.srt/.vtt` 转 WebVTT。
- WebDAV 字幕缓存只按现有 WebDAV 保留时间清理，尚未支持独立容量上限。

## 55. 阶段 4 WebDAV Range 分段直出当前状态

更新时间：2026-05-03

已完成 WebDAV Range 分段直出第一阶段：

- 已新增 `IWebDavRangeStreamService`、`WebDavRangeStreamResult` 和 `WebDavRangeStreamService`。
- WebDAV 播放决策现在会先读取数据库里的远程文件信息；当原始画质、未指定音轨、未烧录字幕且策略允许 Range 直出时，会直接返回 `/api/playback/files/{fileId}/stream`，不触发完整视频下载。
- `/api/playback/files/{fileId}/stream` 已支持 WebDAV Range 代理；本地文件仍走原有 `Results.File` Range 直出。
- WebDAV Range 请求会带上已保存的 Basic 凭据，并向远端发送 `Range` 请求。
- 对 `bytes=start-end` 请求会把远端返回的分段缓存到 `cache/webdav/ranges/{fileHash}/{start}-{end}.seg`；相同分段再次请求会直接复用本地缓存。
- 对 `bytes=start-` 开放尾部请求会按 8 MiB 分段上限裁剪，避免浏览器一次请求导致远端超大范围读取。
- `GET /api/playback/files/{fileId}/cache` 已新增 `canStreamDirect` 字段，前端可判断 WebDAV 文件是否可以不等完整缓存直接开始播放。
- Web 播放器现在先读取缓存状态：如果 `canStreamDirect=true`，会跳过完整缓存准备，直接进入字幕发现和播放决策；如果不可直出，仍按原流程完整缓存后转封装/转码。
- OpenAPI 已同步更新缓存状态和流接口说明。
- 已补充测试，覆盖 WebDAV Range 请求、Basic Auth、分段缓存命中复用，以及 MP4 WebDAV 状态可直出标记。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
53 passed, 0 failed
```

当前 Web 构建产物：

```text
web/dist/assets/index-CtoGJU0Y.css
web/dist/assets/index-l2j2bpFb.js
```

当前限制：

- Range 分段缓存已支持固定块复用和容量淘汰，当前状态见第 56 节。
- 未带 Range 的 WebDAV 直出请求会直接代理远端流，不写入分段缓存。
- 需要 HLS 转封装、HLS 转码、音轨选择或字幕烧录时，仍需要先完整缓存 WebDAV 视频文件。
- WebDAV 直出优先用于 MP4/MOV/WebM 等浏览器兼容容器；真实编码兼容性仍取决于已探测元数据或浏览器能力。

## 56. 阶段 4 WebDAV Range 缓存复用与容量上限当前状态

更新时间：2026-05-03

已完成 WebDAV Range 缓存复用与容量淘汰第一阶段：

- WebDAV Range 缓存已从精确 `start-end` 小分段升级为 8 MiB 固定块缓存。
- 同一固定块内的不同小 Range 会复用同一个本地缓存块，响应时只返回浏览器请求的字节切片。
- `bytes=start-` 这类开放尾部 Range 仍按 8 MiB 块读取，避免一次请求拉取超大远程文件。
- 显式跨块的大 Range 暂时仍按请求区间缓存，避免当前阶段引入复杂的多块拼接流。
- `CacheSettings` 已新增 `webDavMaxGb`，默认 20 GB，范围 1 到 1024 GB。
- Web 设置面板和缓存维护条已新增 WebDAV 缓存上限配置。
- WebDAV 缓存清理任务现在先按保留时间清理，再按 `webDavMaxGb` 对所有 `cache/webdav` 文件按最近使用时间淘汰旧文件。
- 容量淘汰覆盖完整 WebDAV 视频缓存、字幕缓存和 Range 分段缓存。
- 已补充测试，覆盖重叠 Range 复用同一固定块、缓存设置归一化和超过容量上限时按最旧文件淘汰。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
54 passed, 0 failed
```

当前 Web 构建产物：

```text
web/dist/assets/index-CtoGJU0Y.css
web/dist/assets/index-CtTGJluk.js
```

当前限制：

- 固定块缓存只复用单块内的重叠 Range，显式跨多个块的大 Range 尚未做多块拼接。
- Range 缓存没有数据库索引，仍通过文件系统大小和访问时间判断命中与淘汰。
- 容量上限是全 WebDAV 缓存共享上限，尚未拆分完整视频、字幕、Range 分段各自配额。

## 57. 阶段 4 播放链路诊断入口当前状态

更新时间：2026-05-03

已完成播放链路诊断入口第一阶段：

- 已新增 `PlaybackDiagnostics` 和 `PlaybackDiagnosticStep` 模型。
- 已新增接口 `GET /api/playback/diagnostics/{fileId}`，支持与播放决策相同的 `quality`、`audioTrackIndex`、`subtitleMode`、`subtitleId`、`hardware` 查询参数。
- 诊断接口会输出媒体源类型、本地/WebDAV、基础播放策略、最终策略、是否需要完整缓存、是否使用 WebDAV Range 分段代理、是否使用 HLS、是否转码、是否烧录字幕。
- 诊断接口不会启动 HLS 会话，也不会为了诊断下载 WebDAV 完整视频文件。
- WebDAV 原始直出场景会显示 Range 分段代理说明；需要 HLS 的 WebDAV 场景会显示完整缓存要求。
- `IHlsSessionService` 已新增 `PreviewCommand`，`FfmpegHlsSessionService` 会复用实际 HLS 参数生成 FFmpeg 命令预览。
- Web 播放器控制条已新增“诊断”按钮，展开后显示策略摘要、关键标记、诊断步骤和 FFmpeg 命令预览。
- OpenAPI 已同步新增诊断接口。
- 已补充测试，覆盖 FFmpeg 命令预览不会启动进程或创建转码目录。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
55 passed, 0 failed
```

当前 Web 构建产物：

```text
web/dist/assets/index-DCkbAoO4.css
web/dist/assets/index-BEIrHGKs.js
```

当前限制：

- 诊断接口只做当前参数的静态预览，不持续跟踪 HLS 会话运行日志。
- WebDAV 外挂字幕烧录诊断可能会解析并缓存被选中的远程字幕，但不会下载完整视频。
- WebDAV HLS 命令预览中的输入路径使用 `<webdav-cache-file>` 占位，实际路径会在完整缓存完成后确定。

## 58. 阶段 4 打包前运行时自检当前状态

更新时间：2026-05-03

已完成打包前运行时自检第一阶段：

- 已新增 `RuntimeSelfCheckSnapshot` 和 `RuntimeSelfCheckItem` 模型。
- 已新增 `IRuntimeSelfCheckService` 和 `RuntimeSelfCheckService`。
- 已新增接口 `GET /api/runtime/self-check`，用于 NAS 套件安装后快速检查运行环境。
- 自检项已覆盖监听端口、缓存目录写入、转码目录写入、SQLite 写入、FFmpeg 可用性、硬件 H.264 编码器和 WebDAV Range 支持。
- 监听端口检查会提示当前监听地址；如果只监听 loopback，会给出局域网不可直接访问的警告。
- SQLite 写入检查使用事务回滚探针，不会留下业务数据。
- WebDAV Range 检查会选择库中一个 WebDAV 视频文件，发送 `Range: bytes=0-0` 请求验证远端是否支持分段读取；未配置 WebDAV 视频时返回 warn。
- Web 设置抽屉已新增“运行时自检”区域，可手动触发检查并集中展示 `ok/warn/error` 结果。
- OpenAPI 已同步新增运行时自检接口。
- 已补充测试，覆盖自检服务可报告目录/SQLite/FFmpeg/硬件状态，并在没有 WebDAV 视频时给出 warn。

已验证：

```text
npm run build
dotnet build server/OmniPlay.Server.slnx -c Debug
dotnet test server/OmniPlay.Server.slnx -c Debug --no-build
```

当前测试结果：

```text
56 passed, 0 failed
```

当前 Web 构建产物：

```text
web/dist/assets/index-BbAknP68.css
web/dist/assets/index-DDlV7SOH.js
```

当前限制：

- 自检目前是手动触发，不会在服务启动时阻断运行。
- WebDAV Range 检查只抽样一个已入库 WebDAV 视频文件，不逐源逐文件全面检测。
- 硬件编码检测依赖 FFmpeg `-encoders` 输出，真实转码性能仍需在 NAS 实机样本中验证。

## 59. 阶段 5 Synology SPK 打包骨架当前状态

更新时间：2026-05-03

已完成 Synology SPK 打包骨架第一阶段：

- 已新增可执行打包入口 `scripts/package-synology.sh`，内部调用 `scripts/package-synology.mjs` 组装 SPK。
- 打包器支持 `x64` 和 `arm64` 参数，分别映射到 .NET RID `linux-x64` / `linux-arm64` 与 DSM 架构 `x86_64` / `aarch64`。
- 打包流程会先构建 Web UI，再从 Release 自包含发布目录复制服务端 payload，并用最新 `web/dist` 覆盖 payload 内的 `wwwroot`，最后生成 DSM 需要的 `INFO`、`package.tgz`、`scripts`、`conf` 和套件图标。
- SPK 安装后的服务默认监听 `http://0.0.0.0:8096`，运行数据目录为 `/var/packages/OmniPlay/home`。
- DSM 启停脚本已补齐 `start`、`stop`、`restart`、`status`、`log`，启动时会创建 `data`、`cache`、`logs`、`settings` 目录，并写入 PID 文件。
- 打包输出已加入 `dist/synology/`，构建中间产物已加入 `build/` 忽略规则。
- 已生成 x64 测试包 `dist/synology/OmniPlay-0.1.0-0001-x86_64.spk` 和 `dist/synology/OmniPlay-0.1.0-0001-x86_64.spk.sha256`。

当前本机打包方式：

```text
dotnet publish server/src/OmniPlay.Api/OmniPlay.Api.csproj -c Release -r linux-x64 --self-contained true -o build/synology/x64/publish /p:PublishSingleFile=false
./scripts/package-synology.sh x64
```

说明：

- 当前 mac 开发环境中，`dotnet publish` 从脚本子进程启动时会在 MSBuild 阶段异常卡住并无错误输出；直接由外层命令启动可以稳定完成。
- 因此打包器默认复用 `server/src/OmniPlay.Api/bin/Release/net10.0/<RID>` 中已有的自包含 Release 输出。
- 在其他环境如果脚本内 `dotnet publish` 正常，可设置 `OMNIPLAY_DOTNET_BUILD=1`；需要脚本内 restore 时再设置 `OMNIPLAY_DOTNET_RESTORE=1`。

已验证：

```text
node --check scripts/package-synology.mjs
sh -n scripts/package-synology.sh
sh -n packaging/synology/scripts/start-stop-status
./scripts/package-synology.sh x64
tar -tf dist/synology/OmniPlay-0.1.0-0001-x86_64.spk
tar -tf build/synology/x64/spk-root/package.tgz
```

当前 SPK 检查结果：

```text
dist/synology/OmniPlay-0.1.0-0001-x86_64.spk       47 MB
dist/synology/OmniPlay-0.1.0-0001-x86_64.spk.sha256
```

外层 SPK 已包含：

```text
INFO
package.tgz
scripts/start-stop-status
conf/privilege
PACKAGE_ICON.PNG
PACKAGE_ICON_256.PNG
```

内层 payload 已确认包含：

```text
OmniPlay.Api
wwwroot/index.html
wwwroot/assets/index-BbAknP68.css
wwwroot/assets/index-DDlV7SOH.js
```

当前限制：

- SPK 尚未在 DSM 7 实机安装验证，`INFO` 架构字段和 DSM 权限模型仍需实机校正。
- 尚未加入 DSM 安装向导、端口冲突提示、反向代理向导和套件签名。
- 当前只生成了 x64 包；arm64 需要先执行对应 RID 的直接发布，再运行 `./scripts/package-synology.sh arm64`。
- 当前包默认监听 8096，尚未接入 DSM 套件中心里的端口配置页面。

## 60. DSM 7.2.2 SPK 格式修复当前状态

更新时间：2026-05-04

针对 DSM 7.2.2-72806 Update 6 安装提示“套件文件格式不正确”，已做以下修复：

- `INFO` 版本号已从 `0.1.0` 改为带 build 号的 `0.1.0-0001`。
- `INFO` 已新增 `thirdparty="yes"`。
- 外层 SPK tar 已改为显式打包 `INFO`、`package.tgz`、`scripts`、`conf`、`PACKAGE_ICON.PNG`、`PACKAGE_ICON_256.PNG`，不再通过 `tar .` 生成 `./INFO` 这类前缀。
- 外层 SPK 和内层 `package.tgz` 已使用 `ustar` 格式生成。
- 打包时设置 `COPYFILE_DISABLE=1` 和 `COPY_EXTENDED_ATTRIBUTES_DISABLE=1`，避免 macOS 扩展属性写入 tar。
- 如果 `packaging/synology/icons` 下没有图标，打包器会自动生成合法的 64x64 和 256x256 PNG。
- 已补齐 DSM 生命周期脚本：`preinst`、`postinst`、`preuninst`、`postuninst`、`preupgrade`、`postupgrade`。
- 打包器现在会复制整个 `packaging/synology/scripts` 目录，并统一设置脚本可执行权限。

已重新生成：

```text
dist/synology/OmniPlay-0.1.0-0001-x86_64.spk
dist/synology/OmniPlay-0.1.0-0001-x86_64.spk.sha256
```

外层 SPK 当前结构：

```text
INFO
package.tgz
scripts/
scripts/postupgrade
scripts/preupgrade
scripts/postuninst
scripts/preinst
scripts/postinst
scripts/preuninst
scripts/start-stop-status
conf/
conf/privilege
PACKAGE_ICON.PNG
PACKAGE_ICON_256.PNG
```

已验证：

```text
node --check scripts/package-synology.mjs
for f in packaging/synology/scripts/*; do sh -n "$f"; done
./scripts/package-synology.sh x64
tar -tf dist/synology/OmniPlay-0.1.0-0001-x86_64.spk
file build/synology/x64/spk-root/PACKAGE_ICON.PNG build/synology/x64/spk-root/PACKAGE_ICON_256.PNG
```

下一步需要在 DSM 7.2.2 上重新上传 `OmniPlay-0.1.0-0001-x86_64.spk` 验证。如果仍提示格式不正确，优先检查 NAS CPU 架构是否不是 `x86_64`，以及 DSM 套件中心日志中的具体解析错误。

## 61. DSM 7.2.2 SPK 二次格式收敛当前状态

更新时间：2026-05-04

用户反馈第 60 节修复后的 x86_64 SPK 仍提示“套件文件格式不正确”。已继续做以下收敛：

- 打包器已不再依赖 macOS `tar` / `bsdtar` 生成 SPK。
- 已改为纯 Node 写入 ustar header，外层 SPK 和内层 `package.tgz` 均固定为：
  - owner/group: `root/root`
  - uid/gid: `0/0`
  - 文件权限：普通文件 `0644`，可执行脚本和 `OmniPlay.Api` 为 `0755`
  - 路径无 `./` 前缀，无 pax header，无 macOS 扩展属性条目
- `INFO` 已自动写入 `checksum="<package.tgz md5>"`。
- Synology ARM64 架构映射已从错误的 `aarch64` 修正为官方平台名 `armv8`。
- 已额外生成 `noarch` 变体，用于区分 DSM 是否因为 `arch="x86_64"` 拒绝当前 NAS：

```text
dist/synology/OmniPlay-0.1.0-0001-x86_64.spk
dist/synology/OmniPlay-0.1.0-0001-noarch.spk
```

当前 x86_64 外层 SPK 检查结果：

```text
-rw-r--r-- root root INFO
-rw-r--r-- root root package.tgz
drwxr-xr-x root root scripts/
-rwxr-xr-x root root scripts/preinst
-rwxr-xr-x root root scripts/postinst
-rwxr-xr-x root root scripts/preuninst
-rwxr-xr-x root root scripts/postuninst
-rwxr-xr-x root root scripts/preupgrade
-rwxr-xr-x root root scripts/postupgrade
-rwxr-xr-x root root scripts/start-stop-status
drwxr-xr-x root root conf/
-rw-r--r-- root root conf/privilege
-rw-r--r-- root root PACKAGE_ICON.PNG
-rw-r--r-- root root PACKAGE_ICON_256.PNG
```

建议 DSM 实机验证顺序：

1. 先上传 `OmniPlay-0.1.0-0001-x86_64.spk`。
2. 如果仍提示“套件文件格式不正确”，再上传 `OmniPlay-0.1.0-0001-noarch.spk`。
3. 如果 `noarch` 能进入安装向导，说明正式包主要是 `arch` 字段或 NAS 平台架构匹配问题。
4. 如果 `noarch` 也立即提示格式不正确，需要查看 DSM 日志里的具体解析错误。

DSM 上建议收集：

```text
uname -m
cat /etc.defaults/VERSION
tail -n 200 /var/log/messages | grep -i synopkg
tail -n 200 /var/log/synopkg.log
```
