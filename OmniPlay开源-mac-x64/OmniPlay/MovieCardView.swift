import SwiftUI
import GRDB
import Combine

struct MovieCardView: View {
    let movie: Movie
    var isContinueWatchingContext: Bool = false
    var isHomeCacheModeActive: Bool = false
    
    @AppStorage("appTheme") var appTheme = ThemeType.appleLight.rawValue
    var theme: AppTheme { ThemeType(rawValue: appTheme)?.colors ?? ThemeType.appleLight.colors }
    @AppStorage("enableFastTooltip") var enableFastTooltip = true
    
    @ObservedObject var cacheManager = OfflineCacheManager.shared
    
    @State private var showSearchModal = false
    @State private var showEditModal = false
    @State private var hasMissingFiles = false
    
    @State private var isHovering = false
    @State private var isFullyWatched = false
    @State private var movieFiles: [VideoFile] = []
    @State private var sourcePairs: [(VideoFile, MediaSource?)] = []
    
    private let availabilityTimer = Timer.publish(every: 5.0, on: .main, in: .common).autoconnect()
    
    // 🌟 删除了原有的 posterURLString，交由底层的 CachedPosterView 智能处理
    
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            ZStack(alignment: .topTrailing) {
                ZStack {
                    
                    // 🌟 核心升级：替换为智能本地缓存海报组件
                    CachedPosterView(posterPath: movie.posterPath)
                        .frame(width: 160, height: 240)
                        .cornerRadius(12)
                        .shadow(color: .black.opacity(0.1), radius: 8, y: 4)
                    
                    VStack {
                        Spacer()
                        HStack {
                            if let vote = movie.voteAverage, vote > 0 {
                                Text(String(format: "%.1f", vote))
                                    .font(.caption.bold())
                                    .padding(.horizontal, 6).padding(.vertical, 4)
                                    .background(Color.black.opacity(0.7))
                                    .foregroundColor(Color(hex: "FFD700"))
                                    .cornerRadius(6)
                                    .padding(8)
                            }
                            Spacer()
                        }
                    }

                    if isHomeCacheModeActive {
                        Button(action: { fetchFilesAndStartCache() }) {
                            posterCacheOverlayContent
                        }
                        .buttonStyle(.plain)
                        .disabled(!isCacheSupportedForAnyFile || isDownloadingAnyFile || allCacheableFilesCached)
                        .conditionalHelp(cacheOverlayHelpText, show: enableFastTooltip)
                    }
                }
                
                // 搜索与编辑按钮 (悬停显示)
                if isHovering {
                    HStack(spacing: 8) {
                        Button(action: { showSearchModal = true }) {
                            Image(systemName: "magnifyingglass.circle.fill").font(.title2).foregroundColor(.white).shadow(radius: 3)
                        }.buttonStyle(.plain)
                        
                        Button(action: { showEditModal = true }) {
                            Image(systemName: "pencil.circle.fill").font(.title2).foregroundColor(.white).shadow(radius: 3)
                        }.buttonStyle(.plain)
                    }
                    .padding(8)
                    .transition(.opacity)
                }
            }
            .onHover { isHovering = $0 }
            
            Text(movie.title)
                .font(.headline)
                .foregroundColor(theme.textPrimary)
                .lineLimit(1)
            
            // 底部状态栏
            HStack(spacing: 8) {
                if let date = movie.releaseDate, date.count >= 4 {
                    Text(String(date.prefix(4))).font(.caption).foregroundColor(theme.textSecondary)
                }
                
                Spacer(minLength: 0)
                
                if hasMissingFiles {
                    Image(systemName: "exclamationmark.triangle.fill")
                        .font(.system(size: 12))
                        .foregroundColor(.orange)
                        .conditionalHelp("部分源文件不存在，需重新连接外置硬盘/NAS 或使用本地缓存播放", show: enableFastTooltip)
                }
                // 标记已播/未播按钮
                Button(action: toggleWatched) {
                    Image(systemName: isFullyWatched ? "checkmark.circle.fill" : "circle")
                        .font(.system(size: 13))
                        .foregroundColor(isFullyWatched ? theme.accent : theme.textSecondary.opacity(0.5))
                }.buttonStyle(.plain)
            }
        }
        .frame(width: 160)
        .onAppear { checkWatchedStatus(); checkFileAvailability() }
        .onReceive(NotificationCenter.default.publisher(for: .libraryUpdated)) { _ in checkWatchedStatus(); checkFileAvailability() }
        .onReceive(cacheManager.$cachedFileKeys) { _ in checkFileAvailability() }
        .onReceive(cacheManager.$cachedFileNames) { _ in checkFileAvailability() }
        .onReceive(availabilityTimer) { _ in refreshAvailabilityWithoutDB() }
        .sheet(isPresented: $showSearchModal) { MovieSearchModalView(movie: movie) }
        .sheet(isPresented: $showEditModal) { MovieEditModalView(movie: movie) }
    }
    
    private var isDownloadingAnyFile: Bool {
        movieFiles.contains { cacheManager.downloadProgress[$0.id] != nil }
    }

    private var isCacheSupportedForAnyFile: Bool {
        sourcePairs.contains { _, source in cacheManager.supportsCaching(mediaSource: source) }
    }

    private var allCacheableFilesCached: Bool {
        let cacheableFiles = sourcePairs.filter { _, source in cacheManager.supportsCaching(mediaSource: source) }.map(\.0)
        return !cacheableFiles.isEmpty && cacheableFiles.allSatisfy { cacheManager.isCached($0) }
    }

    private var aggregateCacheProgress: Double? {
        guard isDownloadingAnyFile else { return nil }
        let cacheableFiles = sourcePairs.filter { _, source in cacheManager.supportsCaching(mediaSource: source) }.map(\.0)
        guard !cacheableFiles.isEmpty else { return nil }
        let total = cacheableFiles.reduce(0.0) { partial, file in
            if cacheManager.isCached(file) { return partial + 1.0 }
            return partial + (cacheManager.downloadProgress[file.id] ?? 0.0)
        }
        return total / Double(cacheableFiles.count)
    }

    private var cacheOverlayHelpText: String {
        if allCacheableFilesCached { return "已缓存到本地" }
        if !isCacheSupportedForAnyFile { return "该媒体源暂不支持离线缓存" }
        if isDownloadingAnyFile { return "正在离线缓存" }
        return "离线缓存整部影片或整部剧集"
    }

    @ViewBuilder
    private var posterCacheOverlayContent: some View {
        if let progress = aggregateCacheProgress {
            OfflineCacheProgressBadge(progress: progress, tint: theme.accent)
        } else {
            ZStack {
                Circle()
                    .fill(Color.black.opacity(0.62))
                    .frame(width: 52, height: 52)
                Image(systemName: allCacheableFilesCached ? "checkmark.circle.fill" : (!isCacheSupportedForAnyFile ? "icloud.slash" : "arrow.down.circle.fill"))
                    .font(.system(size: 25, weight: .bold))
                    .foregroundColor(allCacheableFilesCached ? theme.accent : .white)
            }
        }
    }
    
    // ======== 私有数据库操作方法 ========
    private func checkWatchedStatus() {
        Task {
            do {
                let movieId = movie.id
                let files = try await AppDatabase.shared.dbQueue.read { db in
                    try Self.fetchVisibleFiles(movieId: movieId, in: db)
                }
                let allFilesWatched = !files.isEmpty && files.allSatisfy { file in
                    file.duration > 0 && (file.playProgress / file.duration) >= 0.95
                }
                await MainActor.run { self.isFullyWatched = allFilesWatched }
            } catch {}
        }
    }
    
    private func toggleWatched() {
        Task {
            do {
                let movieId = movie.id
                try await AppDatabase.shared.dbQueue.write { db in
                    let files = try Self.fetchVisibleFiles(movieId: movieId, in: db)
                    let shouldMarkWatched = files.contains { file in
                        !(file.duration > 0 && (file.playProgress / file.duration) >= 0.95)
                    }
                    for var file in files {
                        if shouldMarkWatched {
                            let duration = file.duration > 0 ? file.duration : 100
                            file.duration = duration
                            file.playProgress = duration
                        } else {
                            file.playProgress = 0
                        }
                        file.lastPlayedAt = nil
                        try file.update(db)
                    }
                }
                await MainActor.run { NotificationCenter.default.post(name: .libraryUpdated, object: nil) }
            } catch {}
        }
    }
    
    private func fetchFilesAndStartCache() {
        Task {
            do {
                let movieId = movie.id
                let pairs = try await AppDatabase.shared.dbQueue.read { db in
                    try Self.fetchVisibleSourcePairs(movieId: movieId, in: db)
                }
                let cacheableFiles = pairs.filter { file, source in
                    cacheManager.supportsCaching(mediaSource: source) && !cacheManager.isCached(file)
                }.map(\.0)
                await MainActor.run {
                    if cacheableFiles.isEmpty {
                        cacheManager.cacheStatusMessage = "该媒体源暂不支持离线缓存"
                    } else {
                        cacheManager.startDownloads(files: cacheableFiles, groupTitle: movie.title)
                    }
                }
            } catch {}
        }
    }
    
    private func checkFileAvailability() {
        Task {
            do {
                let movieId = movie.id
                let pairs = try await AppDatabase.shared.dbQueue.read { db in
                    try Self.fetchVisibleSourcePairs(movieId: movieId, in: db)
                }
                await MainActor.run {
                    self.movieFiles = pairs.map(\.0)
                    self.sourcePairs = pairs
                    self.hasMissingFiles = evaluateMissingState(with: pairs)
                }
            } catch {}
        }
    }
    
    private func refreshAvailabilityWithoutDB() {
        guard !sourcePairs.isEmpty else { return }
        let latestMissing = evaluateMissingState(with: sourcePairs)
        if latestMissing != hasMissingFiles {
            hasMissingFiles = latestMissing
        }
    }
    
    private func evaluateMissingState(with pairs: [(VideoFile, MediaSource?)]) -> Bool {
        pairs.contains { file, source in
            cacheManager.hasMissingSource(for: file, mediaSource: source)
        }
    }

    nonisolated private static func fetchVisibleFiles(movieId: Int64?, in db: Database) throws -> [VideoFile] {
        guard let movieId else { return [] }
        return try VideoFile.fetchAll(
            db,
            sql: """
            SELECT videoFile.*
            FROM videoFile
            JOIN mediaSource ON mediaSource.id = videoFile.sourceId
            WHERE videoFile.movieId = ?
              AND COALESCE(mediaSource.isEnabled, 1) = 1
            """,
            arguments: [movieId]
        )
    }

    nonisolated private static func fetchVisibleSourcePairs(movieId: Int64?, in db: Database) throws -> [(VideoFile, MediaSource?)] {
        let files = try fetchVisibleFiles(movieId: movieId, in: db)
        return try files.map { file in
            (file, try file.request(for: VideoFile.mediaSource).fetchOne(db))
        }
    }
}
