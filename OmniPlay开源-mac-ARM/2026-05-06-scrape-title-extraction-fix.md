# 2026-05-06 扫描刮削标题提取修正

## 问题

ARM mac 版扫描插入未匹配影片时，卡片标题直接使用去扩展名后的完整文件名；自动刮削也主要解析 `relativePath`。当文件名包含 `2160p / Blu-ray / HEVC / Atmos` 等发布信息，或媒体服务器源的 `relativePath` 是播放/下载端点时，刮削搜索词会被污染。

## 修正

- 新增 `MediaNameParser.combinedSearchMetadata(relativePath:fileName:)`，把相对路径和真实文件名合并成搜索元数据。
- Plex / Emby / Jellyfin 这类媒体服务器端点路径优先解析真实 `fileName`，不再把 `Items/.../Download`、`library/parts/...` 当片名。
- 自动刮削的中文名、父目录中文名、外文名、完整清洗名和年份统一来自合并后的搜索元数据。
- 剧集/电影类型预判同时检查 `relativePath` 和 `fileName`。
- 新增未匹配卡片占位标题使用清洗后的影视名，避免显示完整发布名。
- TV 自动复用缓存同时支持文件名中的 `SxxExx` 识别。

## 验证点

- `[勇士]Warrior.2011.2160p.UHD.Blu-ray...` 入库显示和刮削候选应优先为 `勇士` / `Warrior`。
- `Requiem.for.a.Dream.2000.2160p...` 应提取 `Requiem for a Dream` 和 `2000`。
- 媒体服务器源不应使用 `Items/.../Download` 作为 TMDB 查询词。
