import SwiftUI
import GRDB

// 🌟 高级剧集菜单的数据模型
struct EpisodeItem: Identifiable {
    let id: String
    let file: VideoFile
    let season: Int
    let episode: Int
    let displayName: String
}

struct MovieDetailView: View {
    let movie: Movie
    @Environment(\.dismiss) var dismiss
    @AppStorage("seekDuration") var seekDuration: Int = 10
    
    @AppStorage("appTheme") var appTheme = ThemeType.appleLight.rawValue
    var theme: AppTheme { ThemeType(rawValue: appTheme)?.colors ?? ThemeType.appleLight.colors }
    @AppStorage("enableFastTooltip") var enableFastTooltip = true
    
    // 🌟 修复：在这里声明管家，让详情页全局都能认识它！
    @ObservedObject var cacheManager = OfflineCacheManager.shared
    
    @State private var videoFiles: [VideoFile] = []
    @State private var currentVideoFileId: String? = nil
    
    @State private var allEpisodes: [EpisodeItem] = []
    @State private var availableSeasons: [Int] = []
    @State private var selectedSeason: Int = 1
    @State private var isTVShow: Bool = false
    @State private var isDetailCacheModeActive = false
    @State private var cacheSupportByFileId: [String: Bool] = [:]
    @State private var loadFileDetailsTask: Task<Void, Never>? = nil
    
    @State private var localPoster: NSImage? = nil
    @State private var displayMovieTitle: String
    @State private var playbackAlertMessage = ""
    @State private var showPlaybackAlert = false
    
    init(movie: Movie) {
        self.movie = movie
        _displayMovieTitle = State(initialValue: movie.title)
    }
    
    // 删除了原有的 posterURLString 变量，因为我们现在有了超强的 CachedPosterView
    
    var mainFile: VideoFile? {
        videoFiles.first(where: { $0.id == currentVideoFileId }) ?? videoFiles.first
    }
    
    var allEpisodesForSelectedSeason: [EpisodeItem] {
        allEpisodes.filter { $0.season == selectedSeason }
    }
    
    private var episodeGridColumns: [GridItem] {
        [GridItem(.adaptive(minimum: 260, maximum: 320), spacing: 24, alignment: .top)]
    }
    
    var body: some View {
        ZStack(alignment: .topLeading) {
            // 1. 毛玻璃背景
            GeometryReader { geo in
                Group {
                    if let img = localPoster {
                        Image(nsImage: img).resizable().aspectRatio(contentMode: .fill)
                    } else {
                        theme.background
                    }
                }
                .frame(width: geo.size.width, height: geo.size.height).clipped()
                .blur(radius: 80, opaque: true)
                .overlay(.regularMaterial)
                .overlay(theme.background.opacity(0.6))
            }.ignoresSafeArea()
            
            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    // 2. 左右分栏的高级信息区
                    HStack(alignment: .top, spacing: 45) {
                        
                        ZStack {
                            // 🌟 核心升级：详情页的左侧海报也使用具有智能自愈功能的组件！
                            CachedPosterView(posterPath: movie.posterPath)
                                .frame(width: 260, height: 390) // 🌟 修复：明确给定 2:3 比例的高度 (260 * 1.5)
                                .clipped() // 🌟 修复：防止内部的 fill 模式溢出
                                .cornerRadius(12)
                                .shadow(color: theme.textSecondary.opacity(0.15), radius: 25, y: 15)

                            if isDetailCacheModeActive {
                                Button(action: { cacheEntireTitle() }) {
                                    cacheOverlayContent(for: videoFiles, downloadTitle: "离线缓存整部影片或整部剧集")
                                }
                                .buttonStyle(.plain)
                                .disabled(isCacheActionDisabled(for: videoFiles))
                                .conditionalHelp(cacheHelpText(for: videoFiles, defaultText: "离线缓存整部影片或整部剧集"), show: enableFastTooltip)
                            }
                        }
                        
                        VStack(alignment: .leading, spacing: 16) {
                            Text(displayMovieTitle).font(.system(size: 46, weight: .heavy)).foregroundColor(theme.textPrimary).lineLimit(2)
                            
                            HStack(spacing: 16) {
                                if let date = movie.releaseDate, date.count >= 4 { Text(String(date.prefix(4))) }
                                if let vote = movie.voteAverage, vote > 0 { HStack(spacing: 4) { Image(systemName: "star.fill"); Text(String(format: "%.1f", vote)) }.foregroundColor(Color(hex: "FFAC00")) }
                                Text("TV-14").padding(.horizontal, 6).padding(.vertical, 2).background(theme.surface).cornerRadius(4)
                            }.font(.title3.bold()).foregroundColor(theme.textSecondary)
                            
                            Text(movie.overview ?? "暂无简介").font(.body).foregroundColor(theme.textPrimary.opacity(0.85)).lineSpacing(8).padding(.top, 10).padding(.bottom, 10)
                            
                            // 3. 播放控制条
                            if let file = mainFile {
                                let mainPlayProgress = file.playProgress
                                let mainVideoDuration = file.duration
                                let isWatched = mainVideoDuration > 0 && (mainPlayProgress / mainVideoDuration) >= 0.95
                                let currentBtnLabel = getPlayButtonLabel(fileId: file.id, progress: mainPlayProgress)
                                
                                mainPlaybackControls(
                                    file: file,
                                    isWatched: isWatched,
                                    buttonLabel: currentBtnLabel,
                                    progress: mainPlayProgress,
                                    duration: mainVideoDuration
                                )
                            }
                        }
                    }
                    .padding(.top, 120).padding(.horizontal, 60)
                    
                    // 4. 分集选择器
                    if isTVShow && !allEpisodes.isEmpty {
                        VStack(alignment: .leading, spacing: 20) {
                            HStack(spacing: 15) {
                                Menu {
                                    ForEach(availableSeasons, id: \.self) { s in
                                        Button(s == 0 ? "特别篇" : "第 \(s) 季") { selectedSeason = s }
                                    }
                                } label: {
                                    HStack(spacing: 8) {
                                        Text(selectedSeason == 0 ? "特别篇" : "第 \(selectedSeason) 季").font(.title2.bold())
                                        Image(systemName: "chevron.down").font(.body.bold())
                                    }.foregroundColor(theme.textPrimary)
                                }
                                .menuStyle(.borderlessButton)
                                .menuIndicator(.hidden)
                                .fixedSize()
                                
                                // 整季缓存专属按钮
                                if isDetailCacheModeActive {
                                    HStack(spacing: 10) {
                                        Button(action: { cacheSelectedSeason() }) {
                                            cacheOverlayContent(for: allEpisodesForSelectedSeason.map(\.file), downloadTitle: "缓存当前选择的整季", compact: true)
                                        }
                                        .buttonStyle(.plain)
                                        .disabled(isCacheActionDisabled(for: allEpisodesForSelectedSeason.map(\.file)))
                                        .conditionalHelp(cacheHelpText(for: allEpisodesForSelectedSeason.map(\.file), defaultText: "缓存当前选择的整季"), show: enableFastTooltip)

                                        if let progress = aggregateCacheProgress(for: allEpisodesForSelectedSeason.map(\.file)) {
                                            ProgressView(value: progress)
                                                .progressViewStyle(.linear)
                                                .tint(theme.accent)
                                                .frame(width: 120)
                                        }
                                    }
                                }
                            }
                            .padding(.horizontal, 60)
                            LazyVGrid(columns: episodeGridColumns, alignment: .leading, spacing: 24) {
                                ForEach(allEpisodesForSelectedSeason) { ep in
                                    EpisodeCardView(
                                        movieId: movie.id,
                                        movieTitle: $displayMovieTitle,
                                        ep: ep,
                                        currentVideoFileId: $currentVideoFileId,
                                        localPoster: localPoster,
                                        isCacheSupported: cacheSupportByFileId[ep.file.id] ?? false,
                                        isDetailCacheModeActive: isDetailCacheModeActive
                                    )
                                }
                            }
                            .padding(.horizontal, 60)
                            .padding(.bottom, 60)
                        }
                        .padding(.top, 60)
                    } else {
                        Spacer().frame(height: 100) // 修复：完美闭合，解决大括号报错！
                    }
                }
            }
            
            // 返回按钮
            Button(action: { dismiss() }) {
                Image(systemName: "chevron.left").font(.body.bold()).padding(14).background(theme.background.opacity(0.8)).foregroundColor(theme.textPrimary).clipShape(Circle()).shadow(color: theme.textSecondary.opacity(0.1), radius: 5, y: 3)
            }
            .buttonStyle(.plain).padding(.top, 25).padding(.leading, 30)
            
            // 详情页右上角快捷切换缓存模式
            VStack {
                HStack {
                    Spacer()
                    Button(action: { withAnimation { isDetailCacheModeActive.toggle() } }) {
                        Image(systemName: isDetailCacheModeActive ? "icloud.fill" : "icloud")
                            .font(.title3.bold())
                            .padding(14)
                            .background(theme.background.opacity(0.8))
                            .foregroundColor(isDetailCacheModeActive ? theme.accent : theme.textPrimary)
                            .clipShape(Circle())
                            .shadow(color: theme.textSecondary.opacity(0.1), radius: 5, y: 3)
                    }
                    .buttonStyle(.plain).padding(.top, 25).padding(.trailing, 30)
                }
                Spacer()
            }
        }
        .navigationBarBackButtonHidden(true)
        .alert("无法播放", isPresented: $showPlaybackAlert) {
            Button("知道了", role: .cancel) { }
        } message: {
            Text(playbackAlertMessage)
        }
        .onAppear {
            // 🌟 核心修复：使用新的无沙盒抓取逻辑作为毛玻璃背景
            if let path = movie.posterPath,
               let localURL = PosterManager.shared.getLocalPosterURL(for: path),
               let data = try? Data(contentsOf: localURL),
               let img = NSImage(data: data) {
                self.localPoster = img
            }
            loadFileDetails()
        }
        // 🌟 监听海报下载完成，刷新毛玻璃背景
        .onReceive(NotificationCenter.default.publisher(for: NSNotification.Name("PosterUpdated_\((movie.posterPath ?? "").replacingOccurrences(of: "/", with: ""))"))) { _ in
            if let path = movie.posterPath,
               let localURL = PosterManager.shared.getLocalPosterURL(for: path),
               let data = try? Data(contentsOf: localURL),
               let img = NSImage(data: data) {
                self.localPoster = img
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: .libraryUpdated)) { notification in
            let preferredFileId = notification.userInfo?[LibraryUpdateUserInfoKey.preferredVideoFileId] as? String
            loadFileDetails(preservingCurrentSelection: true, preferredFileId: preferredFileId)
        }
        .onDisappear {
            loadFileDetailsTask?.cancel()
            loadFileDetailsTask = nil
        }
    }
    
    private func getPlayButtonLabel(fileId: String, progress: Double) -> String {
        let prefix = progress > 5.0 ? "继续播放" : "开始播放"
        if isTVShow, let ep = allEpisodes.first(where: { $0.id == fileId }) {
            return "\(prefix) \(ep.displayName)"
        }
        return prefix
    }

    private func seasonSortPriority(_ season: Int) -> Int {
        season == 0 ? Int.max : season
    }

    private func episodeBaseDisplayName(season: Int, episode: Int) -> String {
        season == 0 ? "特别篇 第 \(episode) 集" : "第 \(season) 季 第 \(episode) 集"
    }

    private func episodeDetailSuffix(from descriptor: MediaNameParser.EpisodeDescriptor) -> String? {
        let pieces = [descriptor.variantTitle, descriptor.segmentLabel]
            .compactMap { $0?.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
        guard !pieces.isEmpty else { return nil }
        return pieces.joined(separator: " · ")
    }

    private func applyingDuplicateEpisodeSuffixes(
        to episodes: [EpisodeItem],
        eligibleEpisodeIDs: Set<String>,
        explicitSubtitleFileIDs: Set<String>,
        detailSuffixesByFileID: [String: String]
    ) -> [EpisodeItem] {
        let duplicateCounts = Dictionary(
            grouping: episodes.filter { eligibleEpisodeIDs.contains($0.id) },
            by: { "\($0.season)#\($0.episode)" }
        ).mapValues(\.count)

        return episodes.map { episode in
            let key = "\(episode.season)#\(episode.episode)"
            guard duplicateCounts[key, default: 0] > 1,
                  !explicitSubtitleFileIDs.contains(episode.id),
                  let suffix = detailSuffixesByFileID[episode.id],
                  !suffix.isEmpty else {
                return episode
            }
            return EpisodeItem(
                id: episode.id,
                file: episode.file,
                season: episode.season,
                episode: episode.episode,
                displayName: "\(episodeBaseDisplayName(season: episode.season, episode: episode.episode)) · \(suffix)"
            )
        }
    }

    private func hasUnfinishedPlaybackProgress(_ file: VideoFile) -> Bool {
        guard file.playProgress > 5.0 else { return false }
        guard file.duration > 0 else { return true }
        return (file.playProgress / file.duration) < 0.95
    }

    private func isNotFullyWatched(_ file: VideoFile) -> Bool {
        guard file.duration > 0 else { return true }
        return (file.playProgress / file.duration) < 0.95
    }

    private func mostRecentUnfinishedEpisode(in episodes: [EpisodeItem]) -> EpisodeItem? {
        let unfinishedEpisodes = episodes.filter { hasUnfinishedPlaybackProgress($0.file) }
        return unfinishedEpisodes.max { lhs, rhs in
            (lhs.file.lastPlayedAt ?? 0) < (rhs.file.lastPlayedAt ?? 0)
        }
    }

    private func nextUpEpisode(in season: Int, from episodes: [EpisodeItem]) -> EpisodeItem? {
        let seasonEpisodes = episodes.filter { $0.season == season }
        return mostRecentUnfinishedEpisode(in: seasonEpisodes)
            ?? seasonEpisodes.first { isNotFullyWatched($0.file) }
            ?? seasonEpisodes.first
    }
    
    private func loadFileDetails(preservingCurrentSelection: Bool = false, preferredFileId: String? = nil) {
        let preservedSeason = preservingCurrentSelection ? selectedSeason : nil
        let preservedFileId = preservingCurrentSelection ? currentVideoFileId : nil
        let explicitPreferredFileId = preferredFileId?.trimmingCharacters(in: .whitespacesAndNewlines)
        loadFileDetailsTask?.cancel()
        loadFileDetailsTask = Task {
            do {
                let sourcePairs = try await AppDatabase.shared.dbQueue.read { db in
                    try VideoFile.fetchVisibleSourcePairs(movieId: movie.id, in: db)
                }
                if Task.isCancelled { return }
                let files = sourcePairs.map(\.0)
                let sortedFiles = files.enumerated().sorted {
                    let lhsDetailKey = MediaNameParser.episodeSortKey(for: $0.element.fileName, fallbackIndex: $0.offset).2
                    let rhsDetailKey = MediaNameParser.episodeSortKey(for: $1.element.fileName, fallbackIndex: $1.offset).2
                    let lhsResolved = EpisodeMetadataOverrideStore.shared.resolvedEpisodeInfo(
                        fileId: $0.element.id,
                        fileName: $0.element.fileName,
                        fallbackIndex: $0.offset
                    )
                    let rhsResolved = EpisodeMetadataOverrideStore.shared.resolvedEpisodeInfo(
                        fileId: $1.element.id,
                        fileName: $1.element.fileName,
                        fallbackIndex: $1.offset
                    )
                    return (seasonSortPriority(lhsResolved.season), lhsResolved.episode, lhsDetailKey) <
                        (seasonSortPriority(rhsResolved.season), rhsResolved.episode, rhsDetailKey)
                }.map(\.element)
                var episodes: [EpisodeItem] = []
                var eligibleEpisodeIDs = Set<String>()
                var explicitSubtitleFileIDs = Set<String>()
                var detailSuffixesByFileID: [String: String] = [:]
                var isShow = false
                
                for (index, file) in sortedFiles.enumerated() {
                    let parsed = MediaNameParser.parseEpisodeDescriptor(from: file.fileName, fallbackIndex: index)
                    let override = EpisodeMetadataOverrideStore.shared.override(for: file.id)
                    let preferredSeason = override == nil
                        ? MediaNameParser.resolvePreferredSeason(
                            from: file.relativePath,
                            fileName: file.fileName,
                            fallbackIndex: index
                        )
                        : nil
                    let s = override?.season ?? preferredSeason ?? parsed.season
                    let e = override?.episode ?? parsed.episode
                    var dName = episodeBaseDisplayName(season: s, episode: e)
                    if parsed.isTVShow {
                        isShow = true
                        eligibleEpisodeIDs.insert(file.id)
                        if let subtitle = override?.subtitle?.trimmingCharacters(in: .whitespacesAndNewlines), !subtitle.isEmpty {
                            dName += " · \(subtitle)"
                            explicitSubtitleFileIDs.insert(file.id)
                        } else if let suffix = episodeDetailSuffix(from: parsed) {
                            detailSuffixesByFileID[file.id] = suffix
                        }
                    } else if displayMovieTitle.contains("季") || displayMovieTitle.contains("集") {
                        isShow = true
                        eligibleEpisodeIDs.insert(file.id)
                    } else {
                        dName = sortedFiles.count > 1 ? "部分 \(index + 1)" : "正片"
                    }
                    episodes.append(EpisodeItem(id: file.id, file: file, season: s, episode: e, displayName: dName))
                }
                
                let seasons = Array(Set(episodes.map { $0.season })).sorted {
                    seasonSortPriority($0) < seasonSortPriority($1)
                }
                let sortedEpisodes = applyingDuplicateEpisodeSuffixes(
                    to: episodes,
                    eligibleEpisodeIDs: eligibleEpisodeIDs,
                    explicitSubtitleFileIDs: explicitSubtitleFileIDs,
                    detailSuffixesByFileID: detailSuffixesByFileID
                )
                let resumeEp = mostRecentUnfinishedEpisode(in: sortedEpisodes)
                let nextUnwatchedEp = sortedEpisodes.first { isNotFullyWatched($0.file) }
                let nextUpEp = resumeEp ?? nextUnwatchedEp ?? sortedEpisodes.first
                
                await MainActor.run {
                    guard !Task.isCancelled else { return }
                    self.videoFiles = sortedEpisodes.map(\.file); self.allEpisodes = sortedEpisodes; self.availableSeasons = seasons; self.isTVShow = isShow
                    self.cacheSupportByFileId = Dictionary(
                        uniqueKeysWithValues: sourcePairs.map { pair in
                            (pair.0.id, cacheManager.supportsCaching(mediaSource: pair.1))
                        }
                    )
                    if let explicitPreferredFileId,
                       !explicitPreferredFileId.isEmpty,
                       let preferredEpisode = sortedEpisodes.first(where: { $0.id == explicitPreferredFileId }) {
                        self.selectedSeason = preferredEpisode.season
                        self.currentVideoFileId = preferredEpisode.id
                    } else if let preservedSeason, seasons.contains(preservedSeason) {
                        self.selectedSeason = preservedSeason
                        if let preservedFileId,
                           let preservedEpisode = sortedEpisodes.first(where: { $0.id == preservedFileId && $0.season == preservedSeason }) {
                            self.currentVideoFileId = preservedEpisode.id
                        } else {
                            self.currentVideoFileId = nextUpEpisode(in: preservedSeason, from: sortedEpisodes)?.id
                        }
                    } else if let preservedFileId, let preservedEpisode = sortedEpisodes.first(where: { $0.id == preservedFileId }) {
                        self.selectedSeason = preservedEpisode.season
                        self.currentVideoFileId = preservedEpisode.id
                    } else if let target = nextUpEp {
                        self.selectedSeason = target.season
                        self.currentVideoFileId = target.id
                    } else if let first = sortedEpisodes.first {
                        self.selectedSeason = first.season
                        self.currentVideoFileId = first.id
                    } else {
                        self.selectedSeason = 1
                        self.currentVideoFileId = nil
                    }
                }
            } catch { }
        }
    }
    
    private func toggleWatchedStatus(for fileId: String) {
        Task { do { try await AppDatabase.shared.dbQueue.write { db in if var file = try VideoFile.fetchOne(db, key: fileId) { let isWatched = file.duration > 0 && (file.playProgress / file.duration) >= 0.95; if isWatched { file.playProgress = 0 } else { file.playProgress = file.duration > 0 ? file.duration : 100; if file.duration == 0 { file.duration = 100 } }; file.lastPlayedAt = nil; try file.update(db) } }; DispatchQueue.main.async { NotificationCenter.default.post(name: .libraryUpdated, object: nil) } } catch { } }
    }
    
    private func cacheSelectedSeason() {
        let episodesToCache = allEpisodes.filter { $0.season == selectedSeason }
        let supportedFiles = episodesToCache.map(\.file).filter { cacheSupportByFileId[$0.id] ?? false }
        var hasUnsupported = false
        for ep in episodesToCache {
            if !(cacheSupportByFileId[ep.file.id] ?? false) {
                hasUnsupported = true
            }
        }
        let filesToCache = supportedFiles.filter { !OfflineCacheManager.shared.isCached($0) }
        if !filesToCache.isEmpty {
            OfflineCacheManager.shared.startDownloads(files: filesToCache, groupTitle: "\(movie.title) 第 \(selectedSeason) 季")
        }
        if hasUnsupported {
            OfflineCacheManager.shared.cacheStatusMessage = "部分剧集的媒体源暂不支持离线缓存，已跳过"
        }
    }

    private func cacheEntireTitle() {
        let supportedFiles = videoFiles.filter { cacheSupportByFileId[$0.id] ?? false }
        let filesToCache = supportedFiles.filter { !OfflineCacheManager.shared.isCached($0) }
        guard !filesToCache.isEmpty else {
            OfflineCacheManager.shared.cacheStatusMessage = supportedFiles.isEmpty ? "该媒体源暂不支持离线缓存" : "所选内容已在本地缓存中"
            return
        }
        OfflineCacheManager.shared.startDownloads(files: filesToCache, groupTitle: movie.title)
    }
    
    private func attemptPlayback(for file: VideoFile) {
        Task {
            do {
                let source = try await AppDatabase.shared.dbQueue.read { db in
                    try file.request(for: VideoFile.mediaSource).fetchOne(db)
                }
                let isMissing = OfflineCacheManager.shared.hasMissingSource(for: file, mediaSource: source)
                await MainActor.run {
                    if source?.isEnabled == false {
                        playbackAlertMessage = "该媒体源已关闭，请重新开启后再播放。"
                        showPlaybackAlert = true
                    } else if isMissing {
                        playbackAlertMessage = "文件不存在。请重新连接外置硬盘/NAS，或先将该视频缓存到本机后再播放。"
                        showPlaybackAlert = true
                    } else {
                        DirectPlaybackWindowManager.shared.open(
                            .init(
                                movie: effectiveMovie,
                                fileId: file.id,
                                initialSourceBasePath: source?.baseUrl,
                                initialSourceProtocolType: source?.protocolType,
                                initialSourceAuthConfig: source?.authConfig,
                                initialPlaylistFiles: videoFiles
                            )
                        )
                    }
                }
            } catch {
                await MainActor.run {
                    playbackAlertMessage = "读取文件信息失败：\(error.localizedDescription)"
                    showPlaybackAlert = true
                }
            }
        }
    }
    
    private func formatTime(_ time: Double) -> String { if time.isNaN || time < 0 { return "00:00" }; let t = Int(time); return t / 3600 > 0 ? String(format: "%02d:%02d:%02d", t/3600, (t%3600)/60, t%60) : String(format: "%02d:%02d", (t%3600)/60, t%60) }

    @ViewBuilder
    private func mainPlaybackControls(
        file: VideoFile,
        isWatched: Bool,
        buttonLabel: String,
        progress: Double,
        duration: Double
    ) -> some View {
        let hasProgress = duration > 0 && progress > 0 && !isWatched
        if hasProgress {
            ViewThatFits(in: .horizontal) {
                HStack(spacing: 12) {
                    mainPlaybackButtons(file: file, isWatched: isWatched, buttonLabel: buttonLabel)
                    mainPlaybackProgress(progress: progress, duration: duration)
                        .frame(width: 320, alignment: .leading)
                }
                VStack(alignment: .leading, spacing: 12) {
                    mainPlaybackButtons(file: file, isWatched: isWatched, buttonLabel: buttonLabel)
                    mainPlaybackProgress(progress: progress, duration: duration)
                        .frame(maxWidth: 360, alignment: .leading)
                }
            }
        } else {
            mainPlaybackButtons(file: file, isWatched: isWatched, buttonLabel: buttonLabel)
        }
    }

    private func mainPlaybackButtons(file: VideoFile, isWatched: Bool, buttonLabel: String) -> some View {
        HStack(spacing: 12) {
            Button(action: { attemptPlayback(for: file) }) {
                HStack(spacing: 8) {
                    Image(systemName: "play.fill").font(.title3)
                    Text(buttonLabel)
                        .font(.title3.bold())
                        .lineLimit(1)
                        .minimumScaleFactor(0.86)
                }
                .padding(.horizontal, 24)
                .padding(.vertical, 12)
                .foregroundColor(theme.accent)
                .background(Capsule().fill(theme.accent.opacity(0.1)))
                .overlay(Capsule().stroke(theme.accent, lineWidth: 1.5))
            }
            .buttonStyle(.plain)

            Button(action: { toggleWatchedStatus(for: file.id) }) {
                HStack(spacing: 8) {
                    Image(systemName: isWatched ? "checkmark.circle.fill" : "circle").font(.title3)
                    Text(isWatched ? "已播" : "未播")
                        .font(.title3.bold())
                        .lineLimit(1)
                        .minimumScaleFactor(0.86)
                }
                .padding(.horizontal, 20)
                .padding(.vertical, 12)
                .foregroundColor(theme.textPrimary)
                .background(Capsule().fill(theme.surface.opacity(0.5)))
                .overlay(Capsule().stroke(theme.textSecondary.opacity(0.2), lineWidth: 1))
            }
            .buttonStyle(.plain)
        }
        .fixedSize(horizontal: true, vertical: false)
    }

    private func mainPlaybackProgress(progress: Double, duration: Double) -> some View {
        HStack(spacing: 12) {
            GeometryReader { geo in
                ZStack(alignment: .leading) {
                    Rectangle()
                        .fill(theme.surface)
                        .frame(height: 4)
                        .cornerRadius(2)
                    Rectangle()
                        .fill(theme.accent)
                        .frame(width: max(0, min(geo.size.width, geo.size.width * CGFloat(progress / duration))), height: 4)
                        .cornerRadius(2)
                }
            }
            .frame(minWidth: 120, maxWidth: 220, minHeight: 4, maxHeight: 4)
            Text("\(formatTime(progress)) / \(formatTime(duration))")
                .font(.caption.monospacedDigit())
                .foregroundColor(theme.textSecondary)
                .lineLimit(1)
                .minimumScaleFactor(0.86)
        }
    }

    private func aggregateCacheProgress(for files: [VideoFile]) -> Double? {
        let cacheableFiles = files.filter { cacheSupportByFileId[$0.id] ?? false }
        guard !cacheableFiles.isEmpty else { return nil }
        guard cacheableFiles.contains(where: { cacheManager.downloadProgress[$0.id] != nil }) else { return nil }
        let total = cacheableFiles.reduce(0.0) { partial, file in
            if cacheManager.isCached(file) { return partial + 1.0 }
            return partial + (cacheManager.downloadProgress[file.id] ?? 0.0)
        }
        return total / Double(cacheableFiles.count)
    }

    private func allCacheableFilesCached(_ files: [VideoFile]) -> Bool {
        let cacheableFiles = files.filter { cacheSupportByFileId[$0.id] ?? false }
        return !cacheableFiles.isEmpty && cacheableFiles.allSatisfy { cacheManager.isCached($0) }
    }

    private func isCacheActionDisabled(for files: [VideoFile]) -> Bool {
        let cacheableFiles = files.filter { cacheSupportByFileId[$0.id] ?? false }
        return cacheableFiles.isEmpty ||
            cacheableFiles.contains(where: { cacheManager.downloadProgress[$0.id] != nil }) ||
            cacheableFiles.allSatisfy { cacheManager.isCached($0) }
    }

    private func cacheHelpText(for files: [VideoFile], defaultText: String) -> String {
        let cacheableFiles = files.filter { cacheSupportByFileId[$0.id] ?? false }
        if cacheableFiles.isEmpty { return "该媒体源暂不支持离线缓存" }
        if cacheableFiles.contains(where: { cacheManager.downloadProgress[$0.id] != nil }) { return "正在离线缓存" }
        if cacheableFiles.allSatisfy({ cacheManager.isCached($0) }) { return "已缓存到本地" }
        return defaultText
    }

    @ViewBuilder
    private func cacheOverlayContent(for files: [VideoFile], downloadTitle: String, compact: Bool = false) -> some View {
        if let progress = aggregateCacheProgress(for: files) {
            OfflineCacheProgressBadge(progress: progress, tint: theme.accent)
                .scaleEffect(compact ? 0.72 : 1.0)
        } else {
            let cacheableFiles = files.filter { cacheSupportByFileId[$0.id] ?? false }
            let allCached = !cacheableFiles.isEmpty && cacheableFiles.allSatisfy { cacheManager.isCached($0) }
            let unsupported = cacheableFiles.isEmpty
            ZStack {
                Circle()
                    .fill(Color.black.opacity(compact ? 0.0 : 0.62))
                    .frame(width: compact ? 34 : 56, height: compact ? 34 : 56)
                Image(systemName: allCached ? "checkmark.circle.fill" : (unsupported ? "icloud.slash" : "arrow.down.circle.fill"))
                    .font(.system(size: compact ? 24 : 28, weight: .bold))
                    .foregroundColor(allCached ? theme.accent : (compact ? theme.accent : .white))
                    .accessibilityLabel(downloadTitle)
            }
        }
    }

    private var effectiveMovie: Movie {
        var currentMovie = movie
        currentMovie.title = displayMovieTitle
        return currentMovie
    }
}

struct EpisodeCardView: View {
    let movieId: Int64?
    @Binding var movieTitle: String
    let ep: EpisodeItem
    @Binding var currentVideoFileId: String?
    let localPoster: NSImage?
    let isCacheSupported: Bool
    let isDetailCacheModeActive: Bool
    
    @AppStorage("appTheme") var appTheme = ThemeType.appleLight.rawValue
    var theme: AppTheme { ThemeType(rawValue: appTheme)?.colors ?? ThemeType.appleLight.colors }
    @ObservedObject var cacheManager = OfflineCacheManager.shared
    @State private var isHovering = false
    @State private var showEditModal = false
    
    private let cardWidth: CGFloat = 260
    private let thumbnailHeight: CGFloat = 156
    
    var body: some View {
        let duration = ep.file.duration
        let progress = duration > 0 ? (ep.file.playProgress / duration) : 0
        let isEpWatched = duration > 0 && progress >= 0.95
        let isPartiallyWatched = ep.file.playProgress > 5.0 && progress < 0.95
        let isSelected = currentVideoFileId == ep.file.id
        
        let maskOpacity: Double = isEpWatched ? 0.06 : (isSelected ? 0.12 : (isPartiallyWatched ? 0.2 : 0.3))
        let cardBrightness: Double = isEpWatched ? 0.12 : -0.12
        let isCached = cacheManager.isCached(ep.file)
        let downloadProgress = cacheManager.downloadProgress[ep.file.id]
        let isDownloading = downloadProgress != nil
        
        VStack(alignment: .leading, spacing: 10) {
            ZStack {
                Button(action: { currentVideoFileId = ep.file.id }) {
                    ZStack {
                        EpisodeThumbnailView(
                            fileId: ep.file.id,
                            fallbackImage: localPoster,
                            width: cardWidth,
                            height: thumbnailHeight
                        )
                            .brightness(cardBrightness)
                        Color.black.opacity(maskOpacity)
                        if isSelected { RoundedRectangle(cornerRadius: 10).stroke(theme.accent, lineWidth: 1.5) }
                    }
                    .frame(width: cardWidth, height: thumbnailHeight)
                    .clipShape(RoundedRectangle(cornerRadius: 10))
                }.buttonStyle(.plain)

                if isDetailCacheModeActive {
                    Button(action: { cacheEpisode() }) {
                        episodeCacheOverlayContent
                    }
                    .buttonStyle(.plain)
                    .disabled(!isCacheSupported || isDownloading || isCached)
                }

                VStack {
                    HStack(spacing: 8) {
                        Spacer()
                        if isHovering {
                            Button(action: { showEditModal = true }) {
                                Image(systemName: "pencil.circle.fill")
                                    .font(.title2)
                                    .foregroundColor(.white)
                                    .shadow(radius: 3)
                            }
                            .buttonStyle(.plain)
                            .transition(.opacity)
                        }
                    }
                    .padding(8)
                    Spacer()
                }
                .frame(width: cardWidth, height: thumbnailHeight)
            }
            .onHover { isHovering = $0 }
            
            Text(ep.displayName)
                .font(.subheadline.bold())
                .foregroundColor(theme.textPrimary.opacity(isSelected ? 1.0 : 0.8))
                .lineLimit(2)
                .frame(width: cardWidth, alignment: .center)
                .multilineTextAlignment(.center)
            
            if duration > 0 && !isEpWatched && ep.file.playProgress > 0 {
                GeometryReader { geo in
                    ZStack(alignment: .leading) {
                        Rectangle().fill(theme.surface).frame(height: 4).cornerRadius(2)
                        Rectangle().fill(theme.accent).frame(width: max(0, min(geo.size.width, geo.size.width * CGFloat(progress))), height: 4).cornerRadius(2)
                    }
                }.frame(width: cardWidth, height: 4)
            } else { Spacer().frame(height: 4) }
        }
        .sheet(isPresented: $showEditModal) {
            EpisodeThumbnailEditModalView(movieId: movieId, movieTitle: $movieTitle, episode: ep)
        }
    }

    @ViewBuilder
    private var episodeCacheOverlayContent: some View {
        if let progress = cacheManager.downloadProgress[ep.file.id] {
            OfflineCacheProgressBadge(progress: progress, tint: theme.accent)
        } else {
            ZStack {
                Circle()
                    .fill(Color.black.opacity(0.62))
                    .frame(width: 52, height: 52)
                Image(systemName: cacheManager.isCached(ep.file) ? "checkmark.circle.fill" : (!isCacheSupported ? "icloud.slash" : "arrow.down.circle.fill"))
                    .font(.system(size: 25, weight: .bold))
                    .foregroundColor(cacheManager.isCached(ep.file) ? theme.accent : .white)
            }
        }
    }

    private func cacheEpisode() {
        guard isCacheSupported else {
            cacheManager.cacheStatusMessage = "该媒体源暂不支持离线缓存"
            return
        }
        guard !cacheManager.isCached(ep.file) else {
            cacheManager.cacheStatusMessage = "《\(ep.file.fileName)》已在本地缓存中"
            return
        }
        cacheManager.startDownload(file: ep.file)
    }
}

struct EpisodeThumbnailView: View {
    let fileId: String
    let fallbackImage: NSImage?
    let width: CGFloat
    let height: CGFloat
    @State private var thumbnail: NSImage? = nil
    
    @AppStorage("appTheme") var appTheme = ThemeType.appleLight.rawValue
    var theme: AppTheme { ThemeType(rawValue: appTheme)?.colors ?? ThemeType.appleLight.colors }
    
    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 10)
                .fill(theme.surface.opacity(0.32))

            if let img = thumbnail {
                Image(nsImage: img)
                    .resizable()
                    .scaledToFill()
                    .frame(width: width, height: height)
                    .clipped()
            }
            else if let img = fallbackImage {
                Image(nsImage: img)
                    .resizable()
                    .scaledToFill()
                    .frame(width: width, height: height)
                    .blur(radius: 10)
                    .overlay(theme.background.opacity(0.5))
                    .clipped()
            }
            else { Rectangle().fill(theme.surface).overlay(Image(systemName: "photo").foregroundColor(theme.textSecondary.opacity(0.5))) }
        }
        .frame(width: width, height: height)
        .clipped()
        .onAppear { loadThumbnail() }
        .onReceive(NotificationCenter.default.publisher(for: NSNotification.Name("ThumbnailGenerated_\(fileId)"))) { _ in loadThumbnail() }
    }
    
    private func loadThumbnail() {
        let url = ThumbnailManager.shared.thumbnailURL(for: fileId)
        if let img = NSImage(contentsOf: url) { self.thumbnail = img }
    }
}
