# 2026-05-06 扫描刮削标题提取修正

## 问题

x64 mac 版和 ARM mac 版存在相同风险：未匹配卡片标题可能直接显示完整发布文件名，自动刮削也可能只解析 `relativePath`。对根目录散放电影和 Plex / Emby / Jellyfin 源尤其容易出现刮削词污染。

## 修正

- 新增 `MediaNameParser.combinedSearchMetadata(relativePath:fileName:)`，统一从路径和真实文件名提取可用影视名。
- 媒体服务器端点路径优先解析真实 `fileName`，跳过 `Items/.../Download`、`library/parts/...` 这类非片名路径。
- 自动刮削流程改用合并后的中文名、父目录中文名、外文名、完整清洗名和年份。
- 剧集/电影类型预判同时检查 `relativePath` 和 `fileName`。
- 扫描插入未匹配卡片时使用清洗后的占位标题，不再直接使用完整发布名。
- TV 自动复用缓存支持从文件名识别剧集。

## 验证点

- `疤面煞星 Scarface 1983 2160p...` 应提取 `疤面煞星` / `Scarface` / `1983`。
- `Kingdom.of.Heaven.2005.2in1.2160p...` 应优先使用 `Kingdom of Heaven`。
- Plex / Emby / Jellyfin 源不应把下载端点作为 TMDB 查询词。
