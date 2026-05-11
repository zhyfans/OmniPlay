# 2026-05-06 扫描刮削标题提取修正

## 问题

扫描入库和自动刮削链路存在直接使用完整文件名或媒体服务器播放端点作为搜索词的风险。典型表现是未刮削卡片显示 `2160p / Blu-ray / HEVC / TrueHD` 等资源发布信息，或 Plex / Emby / Jellyfin 源把 `Items/.../Download` 传入 TMDB 查询构造器。

## 修正

- `LibraryScanner` 判断剧集时同时参考媒体服务器声明类型、相对路径和真实 `fileName`。
- 扫描生成电影/剧集占位标题时，优先使用 `MediaNameParser` 提取出的中文名、外文名和清洗标题，再回退到原始路径。
- `LibraryMetadataEnricher` 查询候选增加 `videoFile.fileName`，自动刮削时把真实文件名传给 `LibraryLookupTitleBuilder`。
- `LibraryLookupTitleBuilder` 对 Plex / Emby / Jellyfin 源不再从 `Items/.../Download` 生成搜索词，改为优先解析真实文件名。
- raw 发布名只作为解析输入，不再作为优先 TMDB 查询词。
- 剧集缩略图补全同样传入真实文件名，避免媒体服务器端点污染剧集搜索。

## 验证点

- `[勇士]Warrior.2011.2160p.UHD.Blu-ray...` 应生成 `勇士`、`Warrior` 等候选，不应生成 `Download` 或包含 `2160p` 的查询候选。
- `Disc 2 - Gone with the Wind - Bonus` 应清洗出 `Gone with the Wind`，而不是用完整发布名刮削。
