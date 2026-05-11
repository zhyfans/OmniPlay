import SwiftUI
import GRDB
import AppKit
import Network

private struct RecentWebDAVHistoryItem: Identifiable, Codable, Equatable {
    var id: UUID
    var name: String
    var baseURL: String
    var username: String
    var credentialID: String?
    var lastUsed: Date

    init(id: UUID = UUID(), name: String, baseURL: String, username: String, credentialID: String?, lastUsed: Date = Date()) {
        self.id = id
        self.name = name
        self.baseURL = baseURL
        self.username = username
        self.credentialID = credentialID
        self.lastUsed = lastUsed
    }
}

private struct WebDAVBrowserStarredFolder: Identifiable, Equatable {
    let key: String
    let protocolKind: MediaSourceProtocol
    let url: String
    let name: String
    let authConfig: String?

    var id: String { key }
}

private enum WebDAVCredentialFlow: Sendable {
    case manual
    case discovered
}

private struct MediaServerSourceDraft {
    let protocolKind: MediaSourceProtocol
    let normalizedURL: String
    let token: String
    let userId: String
    let finalName: String
    let authConfig: String?
}

private struct PlexPINAuthSession {
    let id: String
    let code: String
    let authorizationURL: URL
}

private final class PlexPINAuthClient {
    static let shared = PlexPINAuthClient()
    private let clientIdentifierKey = "OmniPlayPlexClientIdentifier"
    private let session: URLSession

    private init() {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 12
        configuration.timeoutIntervalForResource = 12
        configuration.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        self.session = URLSession(configuration: configuration)
    }

    func begin() async throws -> PlexPINAuthSession {
        let pin = try await createPIN()
        return PlexPINAuthSession(
            id: pin.id,
            code: pin.code,
            authorizationURL: authorizationURL(for: pin.code)
        )
    }

    func waitForToken(pinID: String, timeoutSeconds: Int = 120) async throws -> String {
        for _ in 0..<timeoutSeconds {
            try Task.checkCancellation()
            try await Task.sleep(nanoseconds: 1_000_000_000)
            if let token = try await checkPIN(pinID: pinID) {
                return token
            }
        }
        throw PlexPINAuthError.timeout
    }

    private func createPIN() async throws -> (id: String, code: String) {
        guard let url = URL(string: "https://plex.tv/api/v2/pins") else {
            throw PlexPINAuthError.invalidResponse
        }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        applyHeaders(to: &request)

        let (data, response) = try await session.data(for: request)
        try validate(response: response)
        guard let id = value(named: "id", in: data),
              let code = value(named: "code", in: data),
              !id.isEmpty,
              !code.isEmpty else {
            throw PlexPINAuthError.invalidResponse
        }
        return (id, code)
    }

    private func checkPIN(pinID: String) async throws -> String? {
        guard let url = URL(string: "https://plex.tv/api/v2/pins/\(pinID)") else {
            throw PlexPINAuthError.invalidResponse
        }
        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        applyHeaders(to: &request)

        let (data, response) = try await session.data(for: request)
        try validate(response: response)
        return value(named: "authToken", in: data)
    }

    private func authorizationURL(for code: String) -> URL {
        var components = URLComponents(string: "https://plex.tv/link/")!
        components.queryItems = [URLQueryItem(name: "pin", value: code)]
        return components.url!
    }

    private var clientIdentifier: String {
        if let existing = UserDefaults.standard.string(forKey: clientIdentifierKey), !existing.isEmpty {
            return existing
        }
        let identifier = "omniplay-mac-\(UUID().uuidString)"
        UserDefaults.standard.set(identifier, forKey: clientIdentifierKey)
        return identifier
    }

    private func applyHeaders(to request: inout URLRequest) {
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue("OmniPlay", forHTTPHeaderField: "X-Plex-Product")
        request.setValue("1.0", forHTTPHeaderField: "X-Plex-Version")
        request.setValue(clientIdentifier, forHTTPHeaderField: "X-Plex-Client-Identifier")
        request.setValue("OmniPlay", forHTTPHeaderField: "X-Plex-Device-Name")
        request.setValue("macOS", forHTTPHeaderField: "X-Plex-Platform")
    }

    private func validate(response: URLResponse) throws {
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw PlexPINAuthError.requestFailed
        }
    }

    private func value(named name: String, in data: Data) -> String? {
        if let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
            return object[name] as? String ?? (object[name] as? NSNumber)?.stringValue
        }
        if let document = try? XMLDocument(data: data, options: [.nodeLoadExternalEntitiesNever]) {
            return document.rootElement()?.attribute(forName: name)?.stringValue
        }
        return nil
    }
}

private enum PlexPINAuthError: LocalizedError {
    case invalidResponse
    case requestFailed
    case timeout

    var errorDescription: String? {
        switch self {
        case .invalidResponse:
            return "Plex 授权响应无效。"
        case .requestFailed:
            return "无法连接 Plex 授权服务。"
        case .timeout:
            return "Plex 授权超时，请重新点击登录。"
        }
    }
}

extension MediaSource: Hashable {
    public static func == (lhs: MediaSource, rhs: MediaSource) -> Bool { return lhs.id == rhs.id }
    public func hash(into hasher: inout Hasher) { hasher.combine(id) }
}

extension View { @ViewBuilder func conditionalHelp(_ text: String, show: Bool) -> some View { if show { self.help(text) } else { self } } }

enum MovieSortOption: String, CaseIterable { case name = "名称"; case rating = "评分"; case year = "上映年份" }

private actor LocalNetworkPermissionRequester {
    static let shared = LocalNetworkPermissionRequester()
    private var hasRequested = false

    func requestIfNeeded() async {
        guard !hasRequested else { return }
        hasRequested = true

        let params = NWParameters.tcp
        params.includePeerToPeer = true
        let browser = NWBrowser(for: .bonjour(type: "_http._tcp", domain: nil), using: params)
        let queue = DispatchQueue(label: "nan.omniplay.localnetwork.permission")
        browser.start(queue: queue)
        try? await Task.sleep(nanoseconds: 1_200_000_000)
        browser.cancel()
    }
}

struct PosterWallView: View {
    private static let recentWebDAVHistoryKey = "PosterWallRecentWebDAVHistory"
    @State private var movies: [Movie] = []
    @State private var continueWatchingMovies: [Movie] = []
    let libraryManager = MediaLibraryManager()
    
    @State private var currentScanTask: Task<Void, Never>? = nil
    @State private var currentScanRunID = UUID()
    @State private var isProcessing = false
    @State private var processingMessage = ""
    @State private var showSettings = false
    @State private var settingsFocusTMDBApi = false
    @State private var mediaSources: [MediaSource] = []
    
    // 删除了关于网络别名的复杂状态变量
    @State private var isShowingManageSources = false
    @State private var needsRescanAfterCurrentRun = false
    @State private var isShowingRenameSourceSheet = false
    @State private var sourceToRename: MediaSource? = nil
    @State private var renamingSourceName: String = ""
    @State private var isShowingManualRemoteSourceSheet = false
    @State private var isShowingAddWebDAVSheet = false
    @State private var webDAVName: String = ""
    @State private var webDAVBaseURL: String = ""
    @State private var webDAVUsername: String = ""
    @State private var webDAVPassword: String = ""
    @State private var webDAVValidationMessage: String? = nil
    @State private var webDAVValidationIsError: Bool = false
    @State private var webDAVIsTestingConnection: Bool = false
    @State private var webDAVLastPreflight: WebDAVPreflightResult? = nil
    @State private var isShowingMediaServerSheet = false
    @State private var mediaServerProtocol: MediaSourceProtocol = .plex
    @State private var mediaServerName = ""
    @State private var mediaServerBaseURL = ""
    @State private var mediaServerToken = ""
    @State private var mediaServerUserId = ""
    @State private var mediaServerMessage: String? = nil
    @State private var mediaServerIsPreScanning = false
    
    @AppStorage("keepLocalPosters") var keepLocalPosters = true
    @AppStorage("autoScanOnStartup") var autoScanOnStartup = true
    @AppStorage("enableFastTooltip") var enableFastTooltip = true
    @AppStorage("showMediaSourceRealPath") var showMediaSourceRealPath = true
    @AppStorage("removeWebDAVCredentialWhenRemovingSource") var removeWebDAVCredentialWhenRemovingSource = false
    
    @AppStorage("appTheme") var appTheme = ThemeType.crystal.rawValue
    var theme: AppTheme { ThemeType(rawValue: appTheme)?.colors ?? ThemeType.crystal.colors }
    @Environment(\.colorScheme) private var colorScheme
    
    @ObservedObject var thumbManager = ThumbnailManager.shared
    @ObservedObject var cacheManager = OfflineCacheManager.shared
    @StateObject private var lanScanner = LANScanner()
    
    @State private var searchText: String = ""
    @State private var isSearchActive: Bool = false
    @State private var selectedSortOption: MovieSortOption = .year
    @State private var isAscending: Bool = false
    @State private var pendingLibraryReloadTask: Task<Void, Never>? = nil
    @State private var lastScanResults: [MediaSourceScanResult] = []
    @State private var lastScanDiagnosticsText: String = ""
    @State private var lastPreflightDiagnosticsText: String = ""
    @State private var scanSummaryMessage: String = ""
    @State private var isShowingScanSummaryAlert: Bool = false
    @State private var activeScanningSourceID: Int64? = nil
    @State private var removedSourceIDsDuringRun: Set<Int64> = []
    @State private var recentWebDAVHistory: [RecentWebDAVHistoryItem] = PosterWallView.loadRecentWebDAVHistory()
    @State private var isShowingRemoveSourceSheet = false
    @State private var sourcePendingRemoval: MediaSource? = nil
    @State private var hasPerformedStartupTMDBCheck = false
    @State private var isShowingTMDBConnectionAlert = false
    @State private var tmdbConnectionMessage = ""
    @State private var isHomeCacheModeActive = false
    @State private var isShowingDiscoveredWebDAVLoginSheet = false
    @State private var isShowingWebDAVFolderPickerSheet = false
    @State private var webDAVBrowserBaseURL: String = ""
    @State private var webDAVBrowserDisplayName: String = ""
    @State private var webDAVBrowserUsername: String = ""
    @State private var webDAVBrowserPassword: String = ""
    @State private var webDAVBrowserCredentialID: String? = nil
    @State private var webDAVBrowserProtocol: MediaSourceProtocol = .webdav
    @State private var webDAVSharedFolders: [WebDAVDirectoryItem] = []
    @State private var webDAVFolderListMessage: String? = nil
    @State private var webDAVFolderListIsError: Bool = false
    @State private var webDAVIsLoadingFolders: Bool = false
    @State private var webDAVIsBatchMountingFolders: Bool = false
    @State private var webDAVStarredFolders: [WebDAVBrowserStarredFolder] = []
    
    // 🌟 彻底删除了所有与 network 相关的 State
    
    let columns = [GridItem(.adaptive(minimum: 160), spacing: 20)]
    init() {
        let isFast = UserDefaults.standard.bool(forKey: "enableFastTooltip")
        UserDefaults.standard.set(isFast ? 50 : 1000, forKey: "NSInitialToolTipDelay")
    }
    
    var displayedMovies: [Movie] { var result = movies; if !searchText.isEmpty { result = result.filter { $0.title.localizedCaseInsensitiveContains(searchText) } }; result.sort { m1, m2 in let isLess: Bool; switch selectedSortOption { case .name: isLess = m1.title.localizedStandardCompare(m2.title) == .orderedAscending; case .year: isLess = (m1.releaseDate ?? "") < (m2.releaseDate ?? ""); case .rating: isLess = (m1.voteAverage ?? 0.0) < (m2.voteAverage ?? 0.0) }; return isAscending ? isLess : !isLess }; return result }
    private var scannableProtocolValues: [String] {
        [
            MediaSourceProtocol.local.rawValue,
            MediaSourceProtocol.webdav.rawValue,
            MediaSourceProtocol.plex.rawValue,
            MediaSourceProtocol.emby.rawValue,
            MediaSourceProtocol.jellyfin.rawValue
        ]
    }
    private var discoveredNetworkDevices: [DiscoveredDevice] {
        lanScanner.discoveredDevices.filter { $0.type.isWebDAV || $0.type.isMediaServer }
    }
    private var topToolbarInactiveIconColor: Color {
        colorScheme == .dark ? theme.textPrimary.opacity(0.78) : .secondary
    }
    private var topToolbarDisabledIconColor: Color {
        colorScheme == .dark ? theme.textPrimary.opacity(0.32) : theme.textSecondary.opacity(0.5)
    }
    private var topToolbarStatusTextColor: Color {
        colorScheme == .dark ? theme.textPrimary.opacity(0.86) : theme.textSecondary
    }
    private var webDAVStarredFolderKeys: Set<String> {
        Set(webDAVStarredFolders.map(\.key))
    }
    private var unifiedDiagnosticsText: String {
        [lastPreflightDiagnosticsText, lastScanDiagnosticsText]
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
            .joined(separator: "\n\n----------------\n\n")
    }

    var body: some View {
        NavigationStack {
            ZStack(alignment: .topTrailing) {
                theme.background.ignoresSafeArea()
                
                ScrollView {
                    if movies.isEmpty && continueWatchingMovies.isEmpty {
                        VStack(spacing: 20) {
                            Image(systemName: "film").font(.system(size: 60)).foregroundColor(.secondary)
                            Text("媒体库空空如也，快去添加文件夹吧！").font(.title2).foregroundColor(.secondary)
                        }.padding(.top, 150).frame(maxWidth: .infinity)
                    } else {
                        VStack(alignment: .leading, spacing: 30) {
                            if !continueWatchingMovies.isEmpty {
                                Text("继续播放")
                                    .font(.title2).fontWeight(.bold)
                                    .foregroundColor(theme.textPrimary)
                                    .padding(.horizontal, 25).padding(.top, 20)
                                ScrollView(.horizontal, showsIndicators: false) {
                                    HStack(spacing: 20) {
                                        ForEach(continueWatchingMovies, id: \.id) { movie in
                                            if isHomeCacheModeActive {
                                                MovieCardView(
                                                    movie: movie,
                                                    isContinueWatchingContext: true,
                                                    isHomeCacheModeActive: isHomeCacheModeActive
                                                )
                                                .frame(width: 160)
                                            } else {
                                                NavigationLink(destination: MovieDetailView(movie: movie)) {
                                                    MovieCardView(movie: movie, isContinueWatchingContext: true).frame(width: 160)
                                                }
                                                .buttonStyle(.plain)
                                                .focusable(false)
                                            }
                                        }
                                    }.padding(.horizontal, 25)
                                }
                                Divider().background(theme.textSecondary.opacity(0.3)).padding(.vertical, 10)
                            }
                            HStack(spacing: 12) {
                                Text("所有影视")
                                    .font(.title2).fontWeight(.bold)
                                    .foregroundColor(theme.textPrimary)
                                Spacer().frame(width: 15)
                                if isSearchActive {
                                    TextField("搜索...", text: $searchText)
                                        .textFieldStyle(.roundedBorder).frame(width: 160).transition(.opacity.combined(with: .move(edge: .trailing)))
                                }
                                Button(action: { withAnimation(.easeInOut(duration: 0.2)) { isSearchActive.toggle(); if !isSearchActive { searchText = "" } } }) {
                                    Image(systemName: "magnifyingglass").font(.system(size: 15, weight: .semibold)).foregroundColor(isSearchActive ? theme.accent : .secondary)
                                }.buttonStyle(.plain)
                                Menu {
                                    Picker("", selection: $selectedSortOption) {
                                        ForEach(MovieSortOption.allCases, id: \.self) { option in Text(option.rawValue).tag(option) }
                                    }.labelsHidden().pickerStyle(.inline)
                                } label: {
                                    Image(systemName: "line.3.horizontal.decrease.circle").font(.system(size: 16, weight: .semibold)).foregroundColor(.secondary)
                                }.menuStyle(.borderlessButton).fixedSize()
                                Button(action: { withAnimation { isAscending.toggle() } }) {
                                    Image(systemName: isAscending ? "arrow.up" : "arrow.down").font(.system(size: 14, weight: .bold)).foregroundColor(.secondary)
                                }.buttonStyle(.plain)
                                Spacer()
                            }.padding(.horizontal, 25).padding(.top, 15)
                            
                            LazyVGrid(columns: columns, spacing: 30) {
                                ForEach(displayedMovies, id: \.id) { movie in
                                    if isHomeCacheModeActive {
                                        MovieCardView(movie: movie, isHomeCacheModeActive: isHomeCacheModeActive)
                                    } else {
                                        NavigationLink(destination: MovieDetailView(movie: movie)) {
                                            MovieCardView(movie: movie)
                                        }
                                        .buttonStyle(.plain)
                                        .focusable(false)
                                    }
                                }
                            }.padding(.horizontal, 25).padding(.bottom, 25).animation(.easeInOut, value: displayedMovies.count)
                        }
                    }
                }.frame(maxWidth: .infinity, maxHeight: .infinity)
                
                if let cacheMessage = cacheManager.cacheStatusMessage {
                    Text(cacheMessage)
                        .font(.subheadline.weight(.semibold))
                        .foregroundColor(theme.textPrimary)
                        .padding(.horizontal, 16)
                        .padding(.vertical, 10)
                        .background(.ultraThinMaterial)
                        .clipShape(Capsule())
                        .shadow(radius: 8)
                        .padding(.top, 18)
                        .padding(.trailing, 18)
                        .transition(.move(edge: .top).combined(with: .opacity))
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity).navigationTitle("我的觅影库")
            .toolbar {
                ToolbarItemGroup(placement: .navigation) {
                    if isProcessing || !thumbManager.progressMessage.isEmpty {
                        let statusMessage = isProcessing
                            ? (processingMessage.isEmpty ? "正在扫描媒体源..." : processingMessage)
                            : thumbManager.progressMessage
                        HStack {
                            ProgressView().controlSize(.small)
                                .tint(topToolbarStatusTextColor)
                            Text(statusMessage)
                                .font(.caption)
                                .foregroundColor(topToolbarStatusTextColor)
                                .lineLimit(1)
                                .frame(maxWidth: 200, alignment: .leading)
                        }
                    }
                }
                ToolbarItemGroup(placement: .primaryAction) {
                    if !unifiedDiagnosticsText.isEmpty {
                        Button(action: { copyLastDiagnosticsToPasteboard() }) {
                            Label("复制诊断", systemImage: "doc.on.doc")
                                .foregroundColor(topToolbarInactiveIconColor)
                        }
                        .accessibilityIdentifier("toolbar.copyDiagnostics")
                        .conditionalHelp("复制最近一次扫描失败或 WebDAV 预检失败的诊断信息", show: enableFastTooltip)
                    }
                    
                    Button(action: { isShowingManageSources.toggle() }) {
                        Label("媒体源管理", systemImage: "externaldrive.badge.plus")
                            .foregroundColor(topToolbarInactiveIconColor)
                    }
                        .accessibilityIdentifier("toolbar.addSource")
                        .conditionalHelp("管理已挂载目录、添加本地文件夹或 WebDAV 媒体源", show: enableFastTooltip)
                        .popover(isPresented: $isShowingManageSources, arrowEdge: .top) { folderMenuPanel }
                    Button(action: { triggerScanAndScrape() }) {
                        Label("同步", systemImage: "arrow.triangle.2.circlepath")
                            .foregroundColor(isProcessing ? topToolbarDisabledIconColor : topToolbarInactiveIconColor)
                    }
                        .accessibilityIdentifier("toolbar.sync")
                        .disabled(isProcessing)
                        .conditionalHelp("重新扫描目录并刷新刮削结果", show: enableFastTooltip)
                    Button(action: { withAnimation { isHomeCacheModeActive.toggle() } }) {
                        Label("缓存模式", systemImage: isHomeCacheModeActive ? "icloud.fill" : "icloud")
                            .foregroundColor(isHomeCacheModeActive ? theme.accent : topToolbarInactiveIconColor)
                    }
                    .conditionalHelp("切换离线缓存编辑模式", show: enableFastTooltip)
                    Button(action: {
                        settingsFocusTMDBApi = false
                        showSettings = true
                    }) {
                        Label("设置", systemImage: "gearshape")
                            .foregroundColor(topToolbarInactiveIconColor)
                    }
                        .accessibilityIdentifier("toolbar.settings")
                        .conditionalHelp("打开偏好设置", show: enableFastTooltip)
                }
            }
            .alert("同步提示", isPresented: $isShowingScanSummaryAlert) {
                Button("复制诊断信息") { copyLastDiagnosticsToPasteboard() }
                Button("我知道了", role: .cancel) {}
            } message: {
                Text(scanSummaryMessage)
            }
            .sheet(isPresented: $showSettings) {
                SettingsView(focusTMDBApi: settingsFocusTMDBApi)
                    .onDisappear { settingsFocusTMDBApi = false }
            }
            .sheet(isPresented: $isShowingRenameSourceSheet) {
                renameSourceSheet()
            }
            .sheet(isPresented: $isShowingManualRemoteSourceSheet) {
                manualRemoteSourceSheet()
            }
            .sheet(isPresented: $isShowingAddWebDAVSheet) {
                webDAVSourceSheet()
            }
            .sheet(isPresented: $isShowingDiscoveredWebDAVLoginSheet) {
                discoveredWebDAVLoginSheet()
            }
            .sheet(isPresented: $isShowingWebDAVFolderPickerSheet) {
                webDAVFolderPickerSheet()
            }
            .sheet(isPresented: $isShowingMediaServerSheet) {
                mediaServerSourceSheet()
            }
            .sheet(isPresented: $isShowingRemoveSourceSheet) {
                removeSourceSheet()
            }
            .alert("TMDB API 无法连接", isPresented: $isShowingTMDBConnectionAlert) {
                Button("不添加直接扫描") {
                    triggerScanAndScrape(skipTMDBScrape: true)
                }
                Button("关闭", role: .cancel) {
                    settingsFocusTMDBApi = true
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) {
                        showSettings = true
                    }
                }
            } message: {
                Text("建议添加自己的 TMDB API 后再刮削海报与影视信息。\n\(tmdbConnectionMessage)")
            }
            // 🌟 彻底删除了那个恶心的 SMB/WebDAV Sheet 弹窗！
        }
        .onAppear {
            loadData()
            if ProcessInfo.processInfo.environment["UITEST_OPEN_WEBDAV_SHEET"] == "1" {
                resetWebDAVDraft(useRecentHistory: false)
                isShowingAddWebDAVSheet = true
            }
            if autoScanOnStartup {
                scheduleStartupTMDBPreflight()
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: .libraryUpdated)) { _ in
            scheduleDebouncedLibraryReload()
        }
        .onReceive(NotificationCenter.default.publisher(for: NSNotification.Name("TriggerScanAndScrape"))) { _ in triggerScanAndScrape() }
        .onChange(of: enableFastTooltip) { _, isFast in
            UserDefaults.standard.set(isFast ? 50 : 1000, forKey: "NSInitialToolTipDelay")
        }
        .onReceive(cacheManager.$cacheStatusMessage) { message in
            guard message != nil else { return }
            DispatchQueue.main.asyncAfter(deadline: .now() + 2.2) {
                if self.cacheManager.cacheStatusMessage == message {
                    withAnimation { self.cacheManager.cacheStatusMessage = nil }
                }
            }
        }
        .onDisappear {
            pendingLibraryReloadTask?.cancel()
        }
    }
    
    // ==========================================
    // 🌟 原生 Finder 添加目录逻辑 (无沙盒精简版)
    // ==========================================
    private func promptAndSaveLocalFolder() {
        let panel = NSOpenPanel()
        panel.message = "选择包含视频的本地或 NAS 文件夹"
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = false
        panel.allowsMultipleSelection = false
        
        if panel.runModal() == .OK, let selectedURL = panel.url {
            Task {
                do {
                    let absolutePath = MediaSourceProtocol.local.normalizedBaseURL(selectedURL.path)
                    // 直接存入数据库，丢弃了所有的 authConfig 和协议判断
                    let mountedSourceID = try await AppDatabase.shared.dbQueue.write { db -> Int64? in
                        if let existingID = try Int64.fetchOne(
                            db,
                            sql: "SELECT id FROM mediaSource WHERE baseUrl = ? AND protocolType = ? LIMIT 1",
                            arguments: [absolutePath, MediaSourceProtocol.local.rawValue]
                        ) {
                            try db.execute(
                                sql: "UPDATE mediaSource SET name = ?, isEnabled = 1, disabledAt = NULL WHERE id = ?",
                                arguments: [selectedURL.lastPathComponent, existingID]
                            )
                            return existingID
                        } else {
                            try db.execute(sql: "INSERT INTO mediaSource (name, baseUrl, protocolType) VALUES (?, ?, ?)",
                                           arguments: [selectedURL.lastPathComponent, MediaSourceProtocol.local.normalizedBaseURL(absolutePath), MediaSourceProtocol.local.rawValue])
                            return db.lastInsertedRowID
                        }
                    }
                    await MainActor.run {
                        loadData()
                        if isProcessing {
                            // 当前扫描中不打断任务，标记下一轮自动增量扫描。
                            needsRescanAfterCurrentRun = true
                            processingMessage = "扫描中，新增文件夹已加入下一轮队列..."
                        } else {
                            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { triggerScanAndScrape(onlySourceID: mountedSourceID) }
                        }
                    }
                } catch { print("❌ 添加媒体源失败: \(error)") }
            }
        }
    }
    
    private var folderMenuPanel: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                Text("媒体源管理")
                    .font(.headline)
                
                VStack(alignment: .leading, spacing: 10) {
                    Text(mediaSources.isEmpty ? "暂无已挂载媒体源" : "已挂载媒体源")
                        .font(.subheadline.bold())
                        .foregroundColor(.secondary)
                    if mediaSources.isEmpty {
                        Text("添加本地文件夹或 WebDAV 文件夹后会显示在这里。")
                            .font(.caption)
                            .foregroundColor(.secondary)
                    } else {
                        ScrollView {
                            VStack(alignment: .leading, spacing: 10) {
                                ForEach(mediaSources, id: \.id) { source in
                                    mountedSourceRow(source)
                                }
                            }
                        }
                        .frame(maxHeight: 260)
                    }
                }
                
                Divider()
                
                VStack(alignment: .leading, spacing: 8) {
                    Text("添加媒体源")
                        .font(.subheadline.bold())
                        .foregroundColor(.secondary)
                    VStack(alignment: .leading, spacing: 10) {
                        Button {
                            isShowingManageSources = false
                            promptAndSaveLocalFolder()
                        } label: {
                            Label("添加本地文件夹", systemImage: "folder.badge.plus")
                        }
                        .accessibilityIdentifier("menu.addLocalFolder")
                        .frame(maxWidth: .infinity, alignment: .leading)
                        
                        Button {
                            isShowingManageSources = false
                            isShowingManualRemoteSourceSheet = true
                        } label: {
                            Label("WebDAV/媒体服务器", systemImage: "network.badge.shield.half.filled")
                        }
                        .accessibilityIdentifier("menu.addRemoteSource")
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                }
                
                Divider()
                
                VStack(alignment: .leading, spacing: 8) {
                    HStack {
                        Text("预扫描出的 WebDAV / 媒体服务器")
                            .font(.subheadline.bold())
                            .foregroundColor(.secondary)
                        Spacer()
                        Button(lanScanner.isScanning ? "扫描中..." : "重新扫描") {
                            startManagedWebDAVScan(force: true)
                        }
                        .disabled(lanScanner.isScanning)
                    }
                    
                    if discoveredNetworkDevices.isEmpty {
                        Text(lanScanner.isScanning ? "正在扫描局域网 WebDAV / Plex / Emby / Jellyfin 服务..." : "暂未发现可连接的 WebDAV 或媒体服务器。")
                            .font(.caption)
                            .foregroundColor(.secondary)
                    } else {
                        ScrollView {
                            VStack(alignment: .leading, spacing: 8) {
                                ForEach(discoveredNetworkDevices) { device in
                                    Button {
                                        openDiscoveredNetworkLogin(device)
                                    } label: {
                                        HStack(spacing: 8) {
                                            Image(systemName: device.type.isMediaServer ? "server.rack" : "network")
                                                .foregroundColor(.blue)
                                            VStack(alignment: .leading, spacing: 2) {
                                                Text(discoveredDeviceTitle(device))
                                                    .foregroundColor(.primary)
                                                    .lineLimit(1)
                                                Text(discoveredWebDAVURLString(for: device))
                                                    .font(.caption)
                                                    .foregroundColor(.secondary)
                                                    .lineLimit(nil)
                                                    .fixedSize(horizontal: false, vertical: true)
                                            }
                                            .frame(maxWidth: .infinity, alignment: .leading)
                                            Spacer()
                                        }
                                        .frame(maxWidth: .infinity, alignment: .leading)
                                    }
                                    .buttonStyle(.plain)
                                }
                            }
                        }
                        .frame(maxHeight: 320)
                    }
                }
            }
            .padding(16)
        }
        .frame(width: 540, alignment: .topLeading)
        .frame(minHeight: 560, maxHeight: 720, alignment: .topLeading)
        .onAppear {
            startManagedWebDAVScan(force: false)
        }
    }

    private func mountedSourceRow(_ source: MediaSource) -> some View {
        HStack(spacing: 10) {
            Image(systemName: sourceIconName(source)).foregroundColor(.blue)
            VStack(alignment: .leading, spacing: 2) {
                Text(source.name)
                    .font(.body)
                    .fontWeight(.semibold)
                    .foregroundColor(.primary)
                    .lineLimit(1)
                Text(sourceProtocolLabel(source))
                    .font(.caption2)
                    .foregroundColor(.secondary)
                if showMediaSourceRealPath {
                    Text(source.displayBaseURL())
                        .font(.caption)
                        .foregroundColor(.secondary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
            }
            .frame(maxWidth: 300, alignment: .leading)
            Spacer()
            Button(action: {
                renamingSourceName = source.name
                sourceToRename = source
                isShowingRenameSourceSheet = true
            }) {
                Image(systemName: "pencil.circle.fill")
                    .foregroundColor(.secondary)
            }.buttonStyle(.plain)
            Button(action: {
                sourcePendingRemoval = source
                isShowingRemoveSourceSheet = true
            }) {
                Image(systemName: "minus.circle.fill").foregroundColor(.red.opacity(0.8))
            }.buttonStyle(.plain)
        }
    }

    private func sourceProtocolLabel(_ source: MediaSource) -> String {
        switch source.protocolKind {
        case .webdav: return "WebDAV"
        case .plex: return "Plex"
        case .emby: return "Emby"
        case .jellyfin: return "Jellyfin"
        case .direct: return "直连"
        case .local, .none: return "本地目录"
        }
    }

    private func sourceIconName(_ source: MediaSource) -> String {
        switch source.protocolKind {
        case .webdav: return "network"
        case .plex, .emby, .jellyfin: return "server.rack"
        default: return "folder.fill"
        }
    }

    private func mediaServerProtocolLabel(_ value: MediaSourceProtocol) -> String {
        switch value {
        case .plex: return "Plex"
        case .emby: return "Emby"
        case .jellyfin: return "Jellyfin"
        default: return "媒体服务器"
        }
    }

    private var mediaServerAddressPlaceholder: String {
        switch mediaServerProtocol {
        case .plex:
            return "Plex 地址，例如 http://127.0.0.1:32400"
        case .emby:
            return "Emby 地址，例如 http://127.0.0.1:8096"
        case .jellyfin:
            return "Jellyfin 地址，例如 http://127.0.0.1:8096"
        default:
            return "服务器地址，例如 http://127.0.0.1:8096"
        }
    }

    private var mediaServerTokenPlaceholder: String {
        mediaServerProtocol == .plex ? "Plex 访问令牌（X-Plex-Token）" : "API Key / 访问令牌"
    }

    private func defaultMediaServerBaseURL(for protocolKind: MediaSourceProtocol) -> String {
        switch protocolKind {
        case .plex:
            return "http://127.0.0.1:32400"
        case .emby, .jellyfin:
            return "http://127.0.0.1:8096"
        default:
            return ""
        }
    }

    private func isDefaultMediaServerBaseURL(_ value: String) -> Bool {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed == defaultMediaServerBaseURL(for: .plex)
            || trimmed == defaultMediaServerBaseURL(for: .emby)
            || trimmed == defaultMediaServerBaseURL(for: .jellyfin)
    }

    private func mediaServerConnectionHint(for protocolKind: MediaSourceProtocol) -> String {
        switch protocolKind {
        case .plex:
            return "Plex 默认地址为 http://127.0.0.1:32400，点击“登录 Plex”会自动获取访问令牌。"
        case .emby, .jellyfin:
            return "\(mediaServerProtocolLabel(protocolKind)) 默认地址为 http://127.0.0.1:8096，请填写 API Key 或访问令牌。"
        default:
            return "保存后会先预扫描媒体库列表；可给多个库点星标，关闭列表时统一挂载并扫描刮削。"
        }
    }

    private func mediaServerMissingTokenMessage(for protocolKind: MediaSourceProtocol) -> String {
        protocolKind == .plex
            ? "Plex 需要访问令牌（X-Plex-Token）才能读取媒体库。"
            : "\(mediaServerProtocolLabel(protocolKind)) 需要 API Key 或访问令牌才能读取媒体列表和生成播放地址。"
    }

    private func mediaServerProtocolDidChange(_ newValue: MediaSourceProtocol) {
        let currentBaseURL = mediaServerBaseURL.trimmingCharacters(in: .whitespacesAndNewlines)
        if currentBaseURL.isEmpty || isDefaultMediaServerBaseURL(currentBaseURL) {
            mediaServerBaseURL = defaultMediaServerBaseURL(for: newValue)
        }
        if newValue == .plex {
            mediaServerUserId = ""
        }
        mediaServerMessage = mediaServerConnectionHint(for: newValue)
    }

    private func mediaServerSourceSheet() -> some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("添加媒体服务器")
                .font(.title3.bold())
            Picker("类型", selection: $mediaServerProtocol) {
                Text("Plex").tag(MediaSourceProtocol.plex)
                Text("Emby").tag(MediaSourceProtocol.emby)
                Text("Jellyfin").tag(MediaSourceProtocol.jellyfin)
            }
            .pickerStyle(.segmented)
            TextField("显示名称（可选）", text: $mediaServerName)
                .textFieldStyle(.roundedBorder)
            TextField(mediaServerAddressPlaceholder, text: $mediaServerBaseURL)
                .textFieldStyle(.roundedBorder)
            if mediaServerProtocol != .plex {
                TextField("用户名 / 用户 ID（可选）", text: $mediaServerUserId)
                    .textFieldStyle(.roundedBorder)
            }
            if mediaServerProtocol == .plex {
                Button(mediaServerIsPreScanning ? "等待 Plex 授权..." : "登录 Plex") {
                    authorizePlex()
                }
                .disabled(mediaServerIsPreScanning)
            }
            SecureField(mediaServerTokenPlaceholder, text: $mediaServerToken)
                .textFieldStyle(.roundedBorder)
            if let mediaServerMessage {
                Text(mediaServerMessage)
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            HStack {
                Spacer()
                Button("取消") { isShowingMediaServerSheet = false }
                Button(mediaServerIsPreScanning ? "读取中..." : "保存") { saveMediaServerSource() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(mediaServerIsPreScanning)
            }
        }
        .padding(20)
        .frame(width: 520)
        .onChange(of: mediaServerProtocol) { _, newValue in
            mediaServerProtocolDidChange(newValue)
        }
    }

    private func renameSourceSheet() -> some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("重命名媒体源")
                .font(.title3.bold())
            Text("仅修改应用内显示名称，不会改动真实文件夹。")
                .font(.caption)
                .foregroundColor(.secondary)
            TextField("输入新的显示名称", text: $renamingSourceName)
                .textFieldStyle(.roundedBorder)
            HStack {
                Spacer()
                Button("取消") {
                    isShowingRenameSourceSheet = false
                    sourceToRename = nil
                    renamingSourceName = ""
                }
                Button("保存") {
                    guard let source = sourceToRename else { return }
                    renameMediaSource(source, to: renamingSourceName)
                }
                .keyboardShortcut(.defaultAction)
                .disabled(renamingSourceName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
        .padding(20)
        .frame(width: 420)
    }

    private func manualRemoteSourceSheet() -> some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("WebDAV/媒体服务器")
                .font(.title3.bold())
            Text("选择要手动连接的远程媒体源类型。")
                .font(.caption)
                .foregroundColor(.secondary)

            Button {
                isShowingManualRemoteSourceSheet = false
                resetWebDAVDraft(useRecentHistory: true)
                isShowingAddWebDAVSheet = true
            } label: {
                Label("WebDAV", systemImage: "network")
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .buttonStyle(.bordered)

            Button {
                isShowingManualRemoteSourceSheet = false
                resetMediaServerForm()
                isShowingMediaServerSheet = true
            } label: {
                Label("Plex/Emby/Jellyfin", systemImage: "server.rack")
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .buttonStyle(.bordered)

            HStack {
                Spacer()
                Button("取消") {
                    isShowingManualRemoteSourceSheet = false
                }
            }
        }
        .padding(20)
        .frame(width: 420)
    }

    private func removeSourceSheet() -> some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("移除媒体源")
                .font(.title3.bold())
            if let source = sourcePendingRemoval {
                Text("将移除“\(source.name)”及其影片索引。")
                    .font(.subheadline)
                    .foregroundColor(.secondary)
                let hasCredential = source.protocolKind == .webdav && WebDAVCredentialStore.shared.credentialID(from: source.authConfig) != nil
                if hasCredential {
                    Text(removeWebDAVCredentialWhenRemovingSource
                         ? "将按设置同时删除保存的 WebDAV 登录凭据。"
                         : "将按设置保留已保存的 WebDAV 登录凭据。")
                        .font(.caption)
                        .foregroundColor(.secondary)
                } else if source.protocolKind == .webdav {
                    Text("此 WebDAV 源未保存凭据。")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            } else {
                Text("未找到待移除的媒体源。")
                    .font(.caption)
                    .foregroundColor(.secondary)
            }
            HStack {
                Spacer()
                Button("取消") {
                    isShowingRemoveSourceSheet = false
                    sourcePendingRemoval = nil
                }
                Button("确认移除", role: .destructive) {
                    if let source = sourcePendingRemoval {
                        let hasCredential = source.protocolKind == .webdav && WebDAVCredentialStore.shared.credentialID(from: source.authConfig) != nil
                        removeMediaSource(
                            source,
                            removeCredential: hasCredential ? removeWebDAVCredentialWhenRemovingSource : false
                        )
                    }
                    isShowingRemoveSourceSheet = false
                    sourcePendingRemoval = nil
                }
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding(22)
        .frame(width: 420)
    }

    private func resetWebDAVDraft(useRecentHistory: Bool = true) {
        recentWebDAVHistory = PosterWallView.loadRecentWebDAVHistory()
        if useRecentHistory, let latest = recentWebDAVHistory.sorted(by: { $0.lastUsed > $1.lastUsed }).first {
            applyRecentWebDAVHistory(latest, shouldBumpUsage: false)
        } else if useRecentHistory, let snapshot = fetchLatestWebDAVSourceSnapshot() {
            applyRecentWebDAVHistory(snapshot, shouldBumpUsage: false)
        } else {
            webDAVName = ""
            webDAVBaseURL = ""
            webDAVUsername = ""
            webDAVPassword = ""
        }
        webDAVValidationMessage = nil
        webDAVValidationIsError = false
        webDAVIsTestingConnection = false
        webDAVLastPreflight = nil
        webDAVFolderListMessage = nil
        webDAVFolderListIsError = false
        webDAVIsLoadingFolders = false
        webDAVSharedFolders = []
        webDAVBrowserCredentialID = nil
        webDAVBrowserProtocol = .webdav
    }

    private func applyRecentWebDAVHistory(_ item: RecentWebDAVHistoryItem, shouldBumpUsage: Bool) {
        if item.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
           let host = URL(string: item.baseURL)?.host {
            webDAVName = host
        } else {
            webDAVName = item.name
        }
        webDAVBaseURL = readableWebDAVURL(from: item.baseURL)
        webDAVUsername = item.username
        if let credentialID = item.credentialID,
           let credential = WebDAVCredentialStore.shared.loadCredential(id: credentialID) {
            webDAVUsername = credential.username
            webDAVPassword = credential.password
        } else {
            webDAVPassword = ""
        }
        if shouldBumpUsage { bumpHistoryUsage(for: item.id) }
    }

    private func bumpHistoryUsage(for id: UUID) {
        guard let index = recentWebDAVHistory.firstIndex(where: { $0.id == id }) else { return }
        recentWebDAVHistory[index].lastUsed = Date()
        recentWebDAVHistory.sort(by: { $0.lastUsed > $1.lastUsed })
        PosterWallView.saveRecentWebDAVHistory(recentWebDAVHistory)
    }

    private func recordRecentWebDAVHistory(name: String, baseURL: String, username: String, credentialID: String?) {
        let trimmedURL = baseURL.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedURL.isEmpty else { return }
        let trimmedName = name.trimmingCharacters(in: .whitespacesAndNewlines)
        let trimmedUser = username.trimmingCharacters(in: .whitespacesAndNewlines)
        var updated = recentWebDAVHistory.filter { $0.baseURL != trimmedURL }
        let newItem = RecentWebDAVHistoryItem(name: trimmedName, baseURL: trimmedURL, username: trimmedUser, credentialID: credentialID, lastUsed: Date())
        updated.insert(newItem, at: 0)
        if updated.count > 5 { updated = Array(updated.prefix(5)) }
        recentWebDAVHistory = updated
        PosterWallView.saveRecentWebDAVHistory(updated)
    }

    private func clearRecentWebDAVHistory() {
        recentWebDAVHistory = []
        PosterWallView.saveRecentWebDAVHistory([])
    }

    private func stripCredentialFromHistory(withID credentialID: String) {
        let filtered = recentWebDAVHistory.filter { $0.credentialID != credentialID }
        if filtered.count != recentWebDAVHistory.count {
            recentWebDAVHistory = filtered
            PosterWallView.saveRecentWebDAVHistory(filtered)
        }
    }

    private func ensureHistoryHasSource(source: MediaSource, credentialID: String) {
        let storedCredential = WebDAVCredentialStore.shared.loadCredential(id: credentialID)
        recordRecentWebDAVHistory(
            name: source.name,
            baseURL: source.baseUrl,
            username: storedCredential?.username ?? "",
            credentialID: credentialID
        )
    }
    
    private func fetchLatestWebDAVSourceSnapshot() -> RecentWebDAVHistoryItem? {
        guard let queue = AppDatabase.shared.dbQueue else { return nil }
        return try? queue.read { db -> RecentWebDAVHistoryItem? in
            guard let source = try MediaSource
                .filter(Column("protocolType") == MediaSourceProtocol.webdav.rawValue)
                .order(Column("id").desc)
                .fetchOne(db) else { return nil }
            let credentialID = WebDAVCredentialStore.shared.credentialID(from: source.authConfig)
            let username: String = {
                if let credentialID,
                   let stored = WebDAVCredentialStore.shared.loadCredential(id: credentialID) {
                    return stored.username
                }
                return ""
            }()
            return RecentWebDAVHistoryItem(
                name: source.name,
                baseURL: source.baseUrl,
                username: username,
                credentialID: credentialID,
                lastUsed: Date()
            )
        }
    }
    
    private func readableWebDAVURL(from raw: String) -> String {
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return "" }
        return trimmed.removingPercentEncoding ?? trimmed
    }

    nonisolated private func remoteBrowserProtocolLabel(_ value: MediaSourceProtocol) -> String {
        switch value {
        case .webdav: return "WebDAV"
        case .plex: return "Plex"
        case .emby: return "Emby"
        case .jellyfin: return "Jellyfin"
        default: return "媒体源"
        }
    }

    nonisolated private func normalizedRemoteBrowserURL(protocolKind: MediaSourceProtocol, raw: String) -> String {
        if protocolKind == .webdav {
            return MediaSourceProtocol.webdav.normalizedBaseURL(raw)
        }
        return protocolKind.normalizedBaseURL(raw)
    }

    nonisolated private func remoteBrowserKey(protocolKind: MediaSourceProtocol, baseURL: String, authConfig: String?) -> String {
        let normalizedURL = normalizedRemoteBrowserURL(protocolKind: protocolKind, raw: baseURL)
        if protocolKind == .plex || protocolKind == .emby || protocolKind == .jellyfin {
            let libraryId = MediaServerAuthConfig.decode(authConfig)?.libraryId?.trimmingCharacters(in: .whitespacesAndNewlines)
            return "\(protocolKind.rawValue):\(normalizedURL):\(libraryId?.isEmpty == false ? libraryId! : "all")"
        }
        return "\(protocolKind.rawValue):\(normalizedURL)"
    }

    nonisolated private func remoteBrowserKey(for folder: WebDAVDirectoryItem) -> String {
        remoteBrowserKey(protocolKind: folder.protocolKind, baseURL: folder.url.absoluteString, authConfig: folder.authConfig)
    }

    private static func loadRecentWebDAVHistory() -> [RecentWebDAVHistoryItem] {
        guard let data = UserDefaults.standard.data(forKey: recentWebDAVHistoryKey) else { return [] }
        return (try? JSONDecoder().decode([RecentWebDAVHistoryItem].self, from: data)) ?? []
    }

    private static func saveRecentWebDAVHistory(_ items: [RecentWebDAVHistoryItem]) {
        guard let data = try? JSONEncoder().encode(items) else {
            UserDefaults.standard.removeObject(forKey: recentWebDAVHistoryKey)
            return
        }
        UserDefaults.standard.set(data, forKey: recentWebDAVHistoryKey)
    }

    private func webDAVSourceSheet() -> some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("连接 WebDAV")
                .font(.title3.bold())
                .accessibilityIdentifier("webdav.sheet.title")

            VStack(alignment: .leading, spacing: 8) {
                TextField("WebDAV 地址，例如 https://nas:5006 或 https://nas:5006/dav", text: $webDAVBaseURL)
                    .textFieldStyle(.roundedBorder)
                    .accessibilityIdentifier("webdav.baseURL")
                Text("手动输入 WebDAV 服务地址。保存后会读取该服务暴露的共享文件夹列表。")
                    .font(.caption)
                    .foregroundColor(.secondary)
            }
            TextField("用户名（可选）", text: $webDAVUsername)
                .textFieldStyle(.roundedBorder)
                .accessibilityIdentifier("webdav.username")
            SecureField("密码（可选）", text: $webDAVPassword)
                .textFieldStyle(.roundedBorder)
                .accessibilityIdentifier("webdav.password")
            Text("保存后会读取该 WebDAV 服务暴露的共享文件夹列表。")
                .font(.caption)
                .foregroundColor(.secondary)

            if webDAVIsLoadingFolders {
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text("正在读取共享文件夹...")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            }

            if let message = webDAVFolderListMessage {
                Text(message)
                    .font(.caption)
                    .foregroundColor(webDAVFolderListIsError ? .red : .green)
            }

            HStack {
                Spacer()
                Button("取消") {
                    isShowingAddWebDAVSheet = false
                }
                .accessibilityIdentifier("webdav.cancel")
                Button(webDAVIsLoadingFolders ? "读取中..." : "保存") {
                    saveManualWebDAVCredentialAndLoadFolders()
                }
                .accessibilityIdentifier("webdav.save")
                .keyboardShortcut(.defaultAction)
                .disabled(webDAVIsLoadingFolders)
            }
        }
        .padding(22)
        .frame(width: 440)
        .onDisappear {
            webDAVIsLoadingFolders = false
        }
    }

    private func applyDiscoveredDevice(_ device: DiscoveredDevice) {
        switch device.type {
        case .webdavHTTP:
            webDAVBaseURL = discoveredWebDAVURLString(for: device)
            if webDAVName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                webDAVName = device.name.isEmpty ? "WebDAV \(device.ipAddress)" : device.name
            }
            webDAVValidationMessage = nil
        case .webdavHTTPS:
            webDAVBaseURL = discoveredWebDAVURLString(for: device)
            if webDAVName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                webDAVName = device.name.isEmpty ? "WebDAV \(device.ipAddress)" : device.name
            }
            webDAVValidationMessage = nil
        case .plex, .emby, .jellyfin:
            prepareMediaServerLogin(for: device)
            isShowingAddWebDAVSheet = false
            isShowingMediaServerSheet = true
        case .smb:
            break
        }
    }

    private func discoveredWebDAVURLString(for device: DiscoveredDevice) -> String {
        let scheme = (device.type == .webdavHTTPS || device.port == 443 || device.port == 5006 || device.port == 8920) ? "https" : "http"
        return "\(scheme)://\(device.ipAddress):\(device.port)"
    }

    private func startManagedWebDAVScan(force: Bool) {
        guard force || (!lanScanner.isScanning && discoveredNetworkDevices.isEmpty) else { return }
        Task {
            await LocalNetworkPermissionRequester.shared.requestIfNeeded()
            await MainActor.run {
                if force || !lanScanner.isScanning {
                    lanScanner.startScanning()
                }
            }
        }
    }

    private func openDiscoveredNetworkLogin(_ device: DiscoveredDevice) {
        if device.type.isMediaServer {
            prepareMediaServerLogin(for: device)
            lanScanner.stopScanning()
            isShowingManageSources = false
            isShowingMediaServerSheet = true
            return
        }

        let baseURL = discoveredWebDAVURLString(for: device)
        let matchedHistory = recentWebDAVHistory
            .sorted(by: { $0.lastUsed > $1.lastUsed })
            .first { item in
                let normalizedHistory = MediaSourceProtocol.webdav.normalizedBaseURL(item.baseURL)
                return normalizedHistory.hasPrefix(MediaSourceProtocol.webdav.normalizedBaseURL(baseURL))
            }

        webDAVBrowserBaseURL = baseURL
        webDAVBrowserDisplayName = device.name.isEmpty ? "WebDAV \(device.ipAddress)" : device.name
        webDAVBrowserUsername = matchedHistory?.username ?? ""
        webDAVBrowserPassword = ""
        webDAVBrowserProtocol = .webdav
        if let credentialID = matchedHistory?.credentialID,
           let credential = WebDAVCredentialStore.shared.loadCredential(id: credentialID) {
            webDAVBrowserUsername = credential.username
            webDAVBrowserPassword = credential.password
        }
        webDAVBrowserCredentialID = nil
        webDAVSharedFolders = []
        webDAVFolderListMessage = nil
        webDAVFolderListIsError = false
        webDAVIsLoadingFolders = false
        webDAVIsBatchMountingFolders = false
        webDAVStarredFolders = []
        isShowingManageSources = false
        isShowingDiscoveredWebDAVLoginSheet = true
    }

    private func prepareMediaServerLogin(for device: DiscoveredDevice) {
        switch device.type {
        case .plex:
            mediaServerProtocol = .plex
        case .emby:
            mediaServerProtocol = .emby
        case .jellyfin:
            mediaServerProtocol = .jellyfin
        default:
            mediaServerProtocol = .plex
        }
        mediaServerName = device.name.isEmpty ? "\(mediaServerProtocolLabel(mediaServerProtocol)) \(device.ipAddress)" : device.name
        mediaServerBaseURL = discoveredWebDAVURLString(for: device)
        mediaServerToken = ""
        mediaServerUserId = ""
        mediaServerIsPreScanning = false
        mediaServerMessage = mediaServerConnectionHint(for: mediaServerProtocol)
    }

    private func discoveredDeviceTitle(_ device: DiscoveredDevice) -> String {
        if !device.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return device.name
        }
        return "\(device.type.rawValue) \(device.ipAddress)"
    }

    private func discoveredWebDAVLoginSheet() -> some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("连接 WebDAV")
                .font(.title3.bold())
            VStack(alignment: .leading, spacing: 4) {
                Text(webDAVBrowserDisplayName)
                    .font(.subheadline.bold())
                Text(webDAVBrowserBaseURL)
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .textSelection(.enabled)
            }
            TextField("用户名（可选）", text: $webDAVBrowserUsername)
                .textFieldStyle(.roundedBorder)
                .accessibilityIdentifier("webdav.discovery.username")
            SecureField("密码（可选）", text: $webDAVBrowserPassword)
                .textFieldStyle(.roundedBorder)
                .accessibilityIdentifier("webdav.discovery.password")
            Text("保存后会读取该 WebDAV 服务暴露的共享文件夹列表。")
                .font(.caption)
                .foregroundColor(.secondary)

            if webDAVIsLoadingFolders {
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text("正在读取共享文件夹...")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            }

            if let webDAVFolderListMessage {
                Text(webDAVFolderListMessage)
                    .font(.caption)
                    .foregroundColor(webDAVFolderListIsError ? .red : .green)
            }

            HStack {
                Spacer()
                Button("取消") {
                    isShowingDiscoveredWebDAVLoginSheet = false
                }
                Button(webDAVIsLoadingFolders ? "读取中..." : "保存") {
                    saveDiscoveredWebDAVCredentialAndLoadFolders()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(webDAVIsLoadingFolders)
            }
        }
        .padding(22)
        .frame(width: 440)
    }

    private func webDAVFolderPickerSheet() -> some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                Text("选择 \(remoteBrowserProtocolLabel(webDAVBrowserProtocol)) 共享文件夹")
                    .font(.title3.bold())
                Spacer()
                if webDAVIsLoadingFolders || webDAVIsBatchMountingFolders {
                    ProgressView().controlSize(.small)
                }
                Button {
                    closeWebDAVFolderPicker()
                } label: {
                    Image(systemName: "xmark.circle.fill")
                        .font(.title3)
                        .foregroundColor(.secondary)
                }
                .buttonStyle(.plain)
                .disabled(webDAVIsLoadingFolders || webDAVIsBatchMountingFolders)
                .help(webDAVStarredFolders.isEmpty ? "关闭" : "关闭并挂载标星文件夹")
            }
            Text("可给多个目标文件夹点星标，关闭列表时统一挂载并扫描刮削。")
                .font(.caption)
                .foregroundColor(.secondary)

            if !webDAVStarredFolders.isEmpty {
                Text("已标星 \(webDAVStarredFolders.count) 个文件夹，关闭列表后会加入挂载媒体源。")
                    .font(.caption)
                    .foregroundColor(.secondary)
            }

            if let webDAVFolderListMessage {
                Text(webDAVFolderListMessage)
                    .font(.caption)
                    .foregroundColor(webDAVFolderListIsError ? .red : .green)
            }

            if webDAVSharedFolders.isEmpty {
                Text("未发现可挂载的共享文件夹。")
                    .foregroundColor(.secondary)
                    .frame(maxWidth: .infinity, minHeight: 120)
            } else {
                ScrollView {
                    VStack(alignment: .leading, spacing: 10) {
                        ForEach(webDAVSharedFolders) { folder in
                            HStack(spacing: 10) {
                                Image(systemName: folder.protocolKind == .webdav ? "folder.fill" : "rectangle.stack.fill")
                                    .foregroundColor(.blue)
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(folder.name)
                                        .font(.body.weight(.semibold))
                                        .lineLimit(1)
                                    Text(folder.displayURL)
                                        .font(.caption)
                                        .foregroundColor(.secondary)
                                        .lineLimit(1)
                                        .truncationMode(.middle)
                                }
                                Spacer()
                                let mounted = isRemoteFolderMounted(folder)
                                let key = remoteBrowserKey(for: folder)
                                let starred = webDAVStarredFolderKeys.contains(key)
                                let isBusy = webDAVIsLoadingFolders || webDAVIsBatchMountingFolders
                                Button {
                                    toggleRemoteFolderStar(folder)
                                } label: {
                                    Image(systemName: (mounted || starred) ? "star.fill" : "star")
                                        .foregroundColor((mounted || starred) ? .yellow : .secondary)
                                }
                                .buttonStyle(.plain)
                                .disabled(mounted || isBusy)
                                .help(mounted ? "已挂载" : (starred ? "取消标星" : "标星此文件夹"))
                            }
                            .padding(10)
                            .background(.ultraThinMaterial)
                            .clipShape(RoundedRectangle(cornerRadius: 10))
                        }
                    }
                }
                .frame(minHeight: 180, maxHeight: 320)
            }

            HStack {
                Spacer()
                Button(webDAVStarredFolders.isEmpty ? "关闭" : "关闭并挂载 \(webDAVStarredFolders.count) 个文件夹") {
                    closeWebDAVFolderPicker()
                }
                .disabled(webDAVIsLoadingFolders || webDAVIsBatchMountingFolders)
            }
        }
        .padding(22)
        .frame(width: 560)
    }

    private func saveManualWebDAVCredentialAndLoadFolders() {
        let normalizedBaseURL = MediaSourceProtocol.webdav.normalizedBaseURL(webDAVBaseURL)
        guard MediaSourceProtocol.webdav.isValidBaseURL(normalizedBaseURL) else {
            webDAVFolderListMessage = "WebDAV 地址无效，请输入 http(s):// 开头且包含主机名的地址。"
            webDAVFolderListIsError = true
            return
        }

        webDAVBrowserBaseURL = normalizedBaseURL
        webDAVBrowserDisplayName = URL(string: normalizedBaseURL)?.host ?? "WebDAV 媒体源"
        webDAVBrowserUsername = webDAVUsername.trimmingCharacters(in: .whitespacesAndNewlines)
        webDAVBrowserPassword = webDAVPassword
        webDAVBrowserCredentialID = nil
        webDAVBrowserProtocol = .webdav
        webDAVSharedFolders = []
        webDAVStarredFolders = []
        webDAVValidationMessage = nil
        webDAVValidationIsError = false
        loadWebDAVFoldersFromBrowserDraft(flow: .manual)
    }

    private func saveDiscoveredWebDAVCredentialAndLoadFolders() {
        loadWebDAVFoldersFromBrowserDraft(flow: .discovered)
    }

    private func loadWebDAVFoldersFromBrowserDraft(flow: WebDAVCredentialFlow) {
        let normalizedBaseURL = MediaSourceProtocol.webdav.normalizedBaseURL(webDAVBrowserBaseURL)
        guard MediaSourceProtocol.webdav.isValidBaseURL(normalizedBaseURL) else {
            webDAVFolderListMessage = "WebDAV 地址无效。"
            webDAVFolderListIsError = true
            return
        }

        let username = webDAVBrowserUsername.trimmingCharacters(in: .whitespacesAndNewlines)
        let password = webDAVBrowserPassword
        webDAVIsLoadingFolders = true
        webDAVFolderListMessage = nil
        webDAVFolderListIsError = false

        Task {
            do {
                await LocalNetworkPermissionRequester.shared.requestIfNeeded()
                let credentialID: String?
                if username.isEmpty {
                    credentialID = nil
                } else {
                    credentialID = try WebDAVCredentialStore.shared.saveCredential(username: username, password: password)
                }

                let folders = try await WebDAVDirectoryBrowser().listSharedFolders(
                    baseURL: normalizedBaseURL,
                    username: username,
                    password: password
                )

                await MainActor.run {
                    webDAVBrowserCredentialID = credentialID
                    webDAVBrowserProtocol = .webdav
                    webDAVSharedFolders = folders
                    webDAVStarredFolders = []
                    webDAVFolderListMessage = folders.isEmpty ? "连接成功，但服务端没有返回共享文件夹。" : "连接成功，请选择要挂载的文件夹。"
                    webDAVFolderListIsError = false
                    webDAVIsLoadingFolders = false
                    switch flow {
                    case .manual:
                        isShowingAddWebDAVSheet = false
                    case .discovered:
                        isShowingDiscoveredWebDAVLoginSheet = false
                    }
                    isShowingWebDAVFolderPickerSheet = true
                }
            } catch {
                await MainActor.run {
                    webDAVFolderListMessage = error.localizedDescription
                    webDAVFolderListIsError = true
                    webDAVIsLoadingFolders = false
                }
            }
        }
    }

    private func isRemoteFolderMounted(_ folder: WebDAVDirectoryItem) -> Bool {
        let key = remoteBrowserKey(for: folder)
        return mediaSources.contains { source in
            guard let protocolKind = source.protocolKind else { return false }
            return remoteBrowserKey(protocolKind: protocolKind, baseURL: source.baseUrl, authConfig: source.authConfig) == key
        }
    }

    private func toggleRemoteFolderStar(_ folder: WebDAVDirectoryItem) {
        let normalizedURL = normalizedRemoteBrowserURL(protocolKind: folder.protocolKind, raw: folder.url.absoluteString)
        guard folder.protocolKind.isValidBaseURL(normalizedURL) else {
            webDAVFolderListMessage = "该文件夹地址无效，无法挂载。"
            webDAVFolderListIsError = true
            return
        }
        guard !isRemoteFolderMounted(folder) else { return }
        let key = remoteBrowserKey(for: folder)
        if let index = webDAVStarredFolders.firstIndex(where: { $0.key == key }) {
            webDAVStarredFolders.remove(at: index)
        } else {
            let rawName = folder.name.trimmingCharacters(in: .whitespacesAndNewlines)
            let name = folder.protocolKind == .webdav || rawName.isEmpty
                ? rawName
                : "\(remoteBrowserProtocolLabel(folder.protocolKind)) · \(rawName)"
            webDAVStarredFolders.append(
                WebDAVBrowserStarredFolder(
                    key: key,
                    protocolKind: folder.protocolKind,
                    url: normalizedURL,
                    name: name.isEmpty ? "\(remoteBrowserProtocolLabel(folder.protocolKind)) 媒体源" : name,
                    authConfig: folder.authConfig
                )
            )
        }
    }

    private func closeWebDAVFolderPicker() {
        guard !webDAVIsLoadingFolders, !webDAVIsBatchMountingFolders else { return }
        guard !webDAVStarredFolders.isEmpty else {
            isShowingWebDAVFolderPickerSheet = false
            webDAVStarredFolders = []
            return
        }
        mountStarredWebDAVFoldersAndClose()
    }

    private func mountStarredWebDAVFoldersAndClose() {
        let folders = webDAVStarredFolders
        guard !folders.isEmpty else { return }
        let authConfig = webDAVBrowserCredentialID.map { WebDAVCredentialStore.shared.authReference(for: $0) }
        let username = webDAVBrowserUsername.trimmingCharacters(in: .whitespacesAndNewlines)

        webDAVIsBatchMountingFolders = true
        webDAVFolderListMessage = nil
        webDAVFolderListIsError = false

        Task {
            do {
                let mountedSourceIDs = try await AppDatabase.shared.dbQueue.write { db -> [Int64] in
                    var sourceIDs: [Int64] = []
                    for folder in folders {
                        let protocolValue = folder.protocolKind.rawValue
                        let folderAuthConfig = folder.protocolKind == .webdav ? authConfig : folder.authConfig
                        let existing: MediaSource?
                        if folder.protocolKind == .webdav {
                            existing = try MediaSource.fetchOne(
                                db,
                                sql: "SELECT * FROM mediaSource WHERE protocolType = ? AND baseUrl = ? LIMIT 1",
                                arguments: [protocolValue, folder.url]
                            )
                        } else {
                            existing = try MediaSource.fetchAll(
                                db,
                                sql: "SELECT * FROM mediaSource WHERE protocolType = ? AND baseUrl = ?",
                                arguments: [protocolValue, folder.url]
                            ).first { source in
                                remoteBrowserKey(protocolKind: folder.protocolKind, baseURL: source.baseUrl, authConfig: source.authConfig) == folder.key
                            }
                        }
                        if let existing {
                            try db.execute(
                                sql: "UPDATE mediaSource SET name = ?, authConfig = COALESCE(?, authConfig), isEnabled = 1, disabledAt = NULL WHERE id = ?",
                                arguments: [folder.name, folderAuthConfig, existing.id]
                            )
                            if let id = existing.id {
                                sourceIDs.append(id)
                            }
                            continue
                        }

                        let source = MediaSource(
                            id: nil,
                            name: folder.name,
                            protocolType: protocolValue,
                            baseUrl: folder.url,
                            authConfig: folderAuthConfig
                        )
                        try source.insert(db)
                        sourceIDs.append(db.lastInsertedRowID)
                    }
                    return sourceIDs
                }

                await MainActor.run {
                    for folder in folders {
                        if folder.protocolKind == .webdav {
                            recordRecentWebDAVHistory(
                                name: folder.name,
                                baseURL: folder.url,
                                username: username,
                                credentialID: webDAVBrowserCredentialID
                            )
                        }
                    }
                    webDAVIsBatchMountingFolders = false
                    webDAVStarredFolders = []
                    webDAVFolderListMessage = nil
                    webDAVFolderListIsError = false
                    loadData()
                    isShowingWebDAVFolderPickerSheet = false
                    if isProcessing {
                        needsRescanAfterCurrentRun = true
                        processingMessage = "扫描中，标星的媒体源已加入下一轮队列..."
                    } else {
                        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
                            triggerScanAndScrape(sourceIDs: mountedSourceIDs)
                        }
                    }
                }
            } catch {
                await MainActor.run {
                    webDAVIsBatchMountingFolders = false
                    webDAVFolderListMessage = error.localizedDescription
                    webDAVFolderListIsError = true
                }
            }
        }
    }

    private func mountWebDAVFolder(_ folder: WebDAVDirectoryItem) {
        let normalizedURL = MediaSourceProtocol.webdav.normalizedBaseURL(folder.url.absoluteString)
        guard MediaSourceProtocol.webdav.isValidBaseURL(normalizedURL) else {
            webDAVFolderListMessage = "该 WebDAV 文件夹地址无效，无法挂载。"
            webDAVFolderListIsError = true
            return
        }

        let authConfig = webDAVBrowserCredentialID.map { WebDAVCredentialStore.shared.authReference(for: $0) }
        let username = webDAVBrowserUsername.trimmingCharacters(in: .whitespacesAndNewlines)
        webDAVIsLoadingFolders = true
        webDAVFolderListMessage = nil
        webDAVFolderListIsError = false

        Task {
            do {
                let mountedSourceID = try await AppDatabase.shared.dbQueue.write { db -> Int64? in
                    if let existing = try MediaSource.fetchOne(
                        db,
                        sql: "SELECT * FROM mediaSource WHERE protocolType = ? AND baseUrl = ? LIMIT 1",
                        arguments: [MediaSourceProtocol.webdav.rawValue, normalizedURL]
                    ) {
                        try db.execute(
                            sql: "UPDATE mediaSource SET name = ?, authConfig = ?, isEnabled = 1, disabledAt = NULL WHERE id = ?",
                            arguments: [folder.name, authConfig, existing.id]
                        )
                        return existing.id
                    } else {
                        let source = MediaSource(
                            id: nil,
                            name: folder.name,
                            protocolType: MediaSourceProtocol.webdav.rawValue,
                            baseUrl: normalizedURL,
                            authConfig: authConfig
                        )
                        try source.insert(db)
                        return db.lastInsertedRowID
                    }
                }

                await MainActor.run {
                    recordRecentWebDAVHistory(
                        name: folder.name,
                        baseURL: normalizedURL,
                        username: username,
                        credentialID: webDAVBrowserCredentialID
                    )
                    webDAVIsLoadingFolders = false
                    webDAVFolderListMessage = "已挂载“\(folder.name)”。"
                    webDAVFolderListIsError = false
                    loadData()
                    isShowingWebDAVFolderPickerSheet = false
                    if isProcessing {
                        needsRescanAfterCurrentRun = true
                        processingMessage = "扫描中，新增 WebDAV 文件夹已加入下一轮队列..."
                    } else {
                        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { triggerScanAndScrape(onlySourceID: mountedSourceID) }
                    }
                }
            } catch {
                await MainActor.run {
                    webDAVIsLoadingFolders = false
                    webDAVFolderListMessage = error.localizedDescription
                    webDAVFolderListIsError = true
                }
            }
        }
    }

    private func saveWebDAVSource() {
        saveManualWebDAVCredentialAndLoadFolders()
    }

    private func resetMediaServerForm() {
        mediaServerProtocol = .plex
        mediaServerName = ""
        mediaServerBaseURL = defaultMediaServerBaseURL(for: .plex)
        mediaServerToken = ""
        mediaServerUserId = ""
        mediaServerIsPreScanning = false
        mediaServerMessage = mediaServerConnectionHint(for: .plex)
    }

    private func authorizePlex() {
        guard mediaServerProtocol == .plex else { return }
        if mediaServerBaseURL.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            mediaServerBaseURL = defaultMediaServerBaseURL(for: .plex)
        }
        mediaServerIsPreScanning = true
        mediaServerMessage = "正在创建 Plex 授权请求..."

        Task {
            do {
                let authSession = try await PlexPINAuthClient.shared.begin()
                await MainActor.run {
                    NSWorkspace.shared.open(authSession.authorizationURL)
                    mediaServerMessage = "已打开 Plex Link 页面，请确认 PIN：\(authSession.code)"
                }
                let token = try await PlexPINAuthClient.shared.waitForToken(pinID: authSession.id)
                await MainActor.run {
                    mediaServerToken = token
                    mediaServerIsPreScanning = false
                    mediaServerMessage = "Plex 授权成功，已自动填入访问令牌。"
                }
            } catch {
                await MainActor.run {
                    mediaServerIsPreScanning = false
                    mediaServerMessage = "Plex 授权失败：\(error.localizedDescription)"
                }
            }
        }
    }

    private func currentMediaServerDraft() -> MediaServerSourceDraft? {
        let normalizedURL = mediaServerProtocol.normalizedBaseURL(mediaServerBaseURL)
        guard mediaServerProtocol.isValidBaseURL(normalizedURL) else {
            mediaServerMessage = "服务器地址无效，请输入 http(s):// 开头且包含主机名的地址。"
            return nil
        }

        let token = mediaServerToken.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !token.isEmpty else {
            mediaServerMessage = mediaServerMissingTokenMessage(for: mediaServerProtocol)
            return nil
        }

        let finalName: String = {
            let trimmed = mediaServerName.trimmingCharacters(in: .whitespacesAndNewlines)
            if !trimmed.isEmpty { return trimmed }
            let host = URL(string: normalizedURL)?.host ?? "媒体服务器"
            return "\(mediaServerProtocolLabel(mediaServerProtocol)) · \(host)"
        }()
        let userId = mediaServerProtocol == .plex ? "" : mediaServerUserId.trimmingCharacters(in: .whitespacesAndNewlines)
        let authConfig = MediaServerAuthConfig.encode(token: token, userId: userId)
        return MediaServerSourceDraft(
            protocolKind: mediaServerProtocol,
            normalizedURL: normalizedURL,
            token: token,
            userId: userId,
            finalName: finalName,
            authConfig: authConfig
        )
    }

    private func preScanMediaServerSource() {
        guard let draft = currentMediaServerDraft() else { return }
        Task {
            _ = await performMediaServerPreScan(draft: draft)
        }
    }

    private func performMediaServerPreScan(draft: MediaServerSourceDraft) async -> Bool {
        await MainActor.run {
            mediaServerIsPreScanning = true
            mediaServerMessage = "正在预扫描 \(mediaServerProtocolLabel(draft.protocolKind)) 媒体列表..."
        }
        let result = await MediaServerPreflightChecker().check(
            protocolKind: draft.protocolKind,
            baseURL: draft.normalizedURL,
            token: draft.token,
            userId: draft.userId
        )
        await MainActor.run {
            mediaServerIsPreScanning = false
            mediaServerMessage = result.message
        }
        return result.isReachable
    }

    private func saveMediaServerSource() {
        guard let draft = currentMediaServerDraft() else { return }

        Task {
            do {
                await MainActor.run {
                    mediaServerIsPreScanning = true
                    mediaServerMessage = "正在预扫描 \(mediaServerProtocolLabel(draft.protocolKind)) 共享文件夹..."
                }
                let folders = try await MediaServerLibraryBrowser().listLibraries(
                    protocolKind: draft.protocolKind,
                    baseURL: draft.normalizedURL,
                    token: draft.token,
                    userId: draft.userId
                )
                await MainActor.run {
                    mediaServerIsPreScanning = false
                    isShowingMediaServerSheet = false
                    webDAVBrowserProtocol = draft.protocolKind
                    webDAVBrowserBaseURL = draft.normalizedURL
                    webDAVBrowserDisplayName = draft.finalName
                    webDAVBrowserUsername = ""
                    webDAVBrowserPassword = ""
                    webDAVBrowserCredentialID = nil
                    webDAVSharedFolders = folders
                    webDAVStarredFolders = []
                    webDAVFolderListMessage = folders.isEmpty ? "连接成功，但当前服务器没有可挂载的媒体库。" : "预扫描成功：选择要挂载的媒体库并点星标。"
                    webDAVFolderListIsError = false
                    isShowingWebDAVFolderPickerSheet = true
                }
            } catch {
                await MainActor.run {
                    mediaServerIsPreScanning = false
                    mediaServerMessage = "媒体服务器预扫描失败：\(error.localizedDescription)"
                }
            }
        }
    }

    private func performWebDAVPreflight(showSuccessMessage: Bool) async -> Bool {
        let normalizedURL = MediaSourceProtocol.webdav.normalizedBaseURL(webDAVBaseURL)
        guard MediaSourceProtocol.webdav.isValidBaseURL(normalizedURL) else {
            let message = "WebDAV 地址无效，请输入 http(s):// 开头且包含主机名的地址。"
            let result = WebDAVPreflightResult(
                isReachable: false,
                category: .config,
                message: message,
                httpStatusCode: nil,
                urlErrorCode: nil,
                sanitizedEndpoint: MediaSourceScanDiagnosticsFormatter.sanitizedEndpoint(from: normalizedURL)
            )
            await MainActor.run {
                webDAVValidationMessage = message
                webDAVValidationIsError = true
                webDAVIsTestingConnection = false
                webDAVLastPreflight = result
                lastPreflightDiagnosticsText = WebDAVPreflightDiagnosticsFormatter.diagnosticsReport(
                    result: result,
                    sourceName: currentWebDAVDraftSourceName()
                )
            }
            return false
        }

        await MainActor.run {
            webDAVIsTestingConnection = true
            webDAVValidationMessage = nil
            webDAVValidationIsError = false
        }

        await LocalNetworkPermissionRequester.shared.requestIfNeeded()

        let checker = WebDAVPreflightChecker()
        let result = await checker.check(
            baseURL: webDAVBaseURL,
            username: webDAVUsername,
            password: webDAVPassword
        )

        await MainActor.run {
            webDAVIsTestingConnection = false
            webDAVLastPreflight = result
            if result.isReachable {
                lastPreflightDiagnosticsText = ""
                if showSuccessMessage {
                    if let code = result.httpStatusCode {
                        webDAVValidationMessage = "连接测试成功（HTTP \(code)）。"
                    } else {
                        webDAVValidationMessage = "连接测试成功。"
                    }
                    webDAVValidationIsError = false
                }
            } else {
                webDAVValidationMessage = result.message
                webDAVValidationIsError = true
                lastPreflightDiagnosticsText = WebDAVPreflightDiagnosticsFormatter.diagnosticsReport(
                    result: result,
                    sourceName: currentWebDAVDraftSourceName()
                )
            }
        }

        return result.isReachable
    }

    private func currentWebDAVDraftSourceName() -> String {
        let trimmed = webDAVName.trimmingCharacters(in: .whitespacesAndNewlines)
        if !trimmed.isEmpty { return trimmed }
        if let host = URL(string: webDAVBaseURL)?.host, !host.isEmpty { return host }
        return "WebDAV 媒体源"
    }

    private func loadData() {
        DispatchQueue.global(qos: .utility).async {
            do {
                let fetchedMovies = try libraryManager.fetchAllMovies()
                let fetchedSources = try AppDatabase.shared.dbQueue.read { db in
                    try MediaSource.fetchAll(
                        db,
                        sql: "SELECT * FROM mediaSource WHERE protocolType IN (?, ?, ?, ?, ?) ORDER BY id DESC",
                        arguments: StatementArguments(scannableProtocolValues)
                    )
                }
                let fetchedContinueWatching = try AppDatabase.shared.dbQueue.read { db in
                    let sql = """
                    SELECT DISTINCT movie.*
                    FROM movie
                    JOIN videoFile ON videoFile.movieId = movie.id
                    JOIN mediaSource ON mediaSource.id = videoFile.sourceId
                    WHERE videoFile.playProgress > 5
                      AND COALESCE(mediaSource.isEnabled, 1) = 1
                      AND (videoFile.duration = 0 OR (videoFile.playProgress / videoFile.duration) < 0.95)
                    GROUP BY movie.id
                    ORDER BY MAX(COALESCE(videoFile.lastPlayedAt, 0)) DESC
                    """
                    return try Movie.fetchAll(db, sql: sql)
                }
                DispatchQueue.main.async {
                    self.movies = fetchedMovies
                    self.mediaSources = fetchedSources
                    self.continueWatchingMovies = fetchedContinueWatching
                }
            } catch {}
        }
    }

    private func scheduleDebouncedLibraryReload() {
        pendingLibraryReloadTask?.cancel()
        pendingLibraryReloadTask = Task {
            try? await Task.sleep(nanoseconds: 300_000_000)
            if Task.isCancelled { return }
            await MainActor.run { loadData() }
        }
    }

    private func cancelCurrentScanRunForSource(_ sourceID: Int64) {
        removedSourceIDsDuringRun.insert(sourceID)
        ThumbnailManager.shared.cancelTasks(forSourceID: sourceID)
        currentScanTask?.cancel()
        currentScanTask = nil
        currentScanRunID = UUID()
        activeScanningSourceID = nil
        needsRescanAfterCurrentRun = false
        withAnimation {
            isProcessing = false
            processingMessage = ""
        }
    }
    
    private func removeMediaSource(_ source: MediaSource, removeCredential: Bool) {
        let removingSourceID = source.id
        if let sid = removingSourceID {
            if isProcessing {
                cancelCurrentScanRunForSource(sid)
            } else {
                removedSourceIDsDuringRun.insert(sid)
                ThumbnailManager.shared.cancelTasks(forSourceID: sid)
            }
        }
        if source.protocolKind == .webdav,
           let credentialID = WebDAVCredentialStore.shared.credentialID(from: source.authConfig) {
            if removeCredential {
                WebDAVCredentialStore.shared.removeCredential(id: credentialID)
                stripCredentialFromHistory(withID: credentialID)
            } else {
                ensureHistoryHasSource(source: source, credentialID: credentialID)
            }
        }
        Task {
            do {
                try await AppDatabase.shared.dbQueue.write { db in
                    if let sid = source.id {
                        try db.execute(sql: "DELETE FROM videoFile WHERE sourceId = ?", arguments: [sid])
                        try db.execute(sql: "DELETE FROM mediaSource WHERE id = ?", arguments: [sid])
                    }
                    try db.execute(sql: "DELETE FROM movie WHERE id NOT IN (SELECT DISTINCT movieId FROM videoFile WHERE movieId IS NOT NULL)")
                }
                if !keepLocalPosters { PosterManager.shared.clearCache() }
                await MainActor.run { loadData() }
            } catch { print("大清洗失败: \(error)") }
        }
    }

    private func renameMediaSource(_ source: MediaSource, to newName: String) {
        let trimmed = newName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, let sid = source.id else { return }
        Task {
            do {
                try await AppDatabase.shared.dbQueue.write { db in
                    try db.execute(
                        sql: "UPDATE mediaSource SET name = ? WHERE id = ?",
                        arguments: [trimmed, sid]
                    )
                }
                await MainActor.run {
                    isShowingRenameSourceSheet = false
                    sourceToRename = nil
                    renamingSourceName = ""
                    loadData()
                }
            } catch {
                print("❌ 重命名媒体源失败: \(error)")
            }
        }
    }
    
    private func scheduleStartupTMDBPreflight() {
        guard !hasPerformedStartupTMDBCheck else { return }
        hasPerformedStartupTMDBCheck = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) {
            guard !isProcessing else { return }
            Task {
                let result = await TMDBService.shared.checkConnection()
                await MainActor.run {
                    guard !isProcessing else { return }
                    if result.isConnected {
                        triggerScanAndScrape()
                    } else {
                        tmdbConnectionMessage = result.message
                        isShowingTMDBConnectionAlert = true
                    }
                }
            }
        }
    }

    private func triggerScanAndScrape(onlySourceID: Int64? = nil, sourceIDs: [Int64]? = nil, skipTMDBScrape: Bool = false) {
        guard !isProcessing else { return }
        currentScanTask?.cancel()
        let runID = UUID()
        currentScanRunID = runID
        withAnimation {
            isProcessing = true
            processingMessage = "准备扫描..."
            scanSummaryMessage = ""
            isShowingScanSummaryAlert = false
        }
        lastScanResults = []
        lastScanDiagnosticsText = ""
        removedSourceIDsDuringRun = []
        activeScanningSourceID = nil
        
        currentScanTask = Task(priority: .utility) {
            do {
                if Task.isCancelled {
                    await MainActor.run {
                        guard self.currentScanRunID == runID else { return }
                        activeScanningSourceID = nil
                        removedSourceIDsDuringRun = []
                        currentScanTask = nil
                        isProcessing = false
                    }
                    return
                }
                let scanProtocols = scannableProtocolValues
                let validSources = try await AppDatabase.shared.dbQueue.read { db in
                    let allSources = try MediaSource.fetchAll(
                        db,
                        sql: "SELECT * FROM mediaSource WHERE protocolType IN (?, ?, ?, ?, ?) AND COALESCE(isEnabled, 1) = 1 ORDER BY id ASC",
                        arguments: StatementArguments(scanProtocols)
                    )
                    if let sourceIDs, !sourceIDs.isEmpty {
                        let targetIDs = Set(sourceIDs)
                        return allSources.filter { source in
                            guard let id = source.id else { return false }
                            return targetIDs.contains(id)
                        }
                    } else if let onlySourceID {
                        return allSources.filter { source in
                            guard let id = source.id else { return false }
                            return id == onlySourceID
                        }
                    }
                    return allSources
                }
                
                if validSources.isEmpty {
                    await MainActor.run {
                        guard self.currentScanRunID == runID else { return }
                        activeScanningSourceID = nil
                        removedSourceIDsDuringRun = []
                        currentScanTask = nil
                        isProcessing = false
                        loadData()
                    }
                    return
                }
                
                await MainActor.run {
                    guard self.currentScanRunID == runID else { return }
                    withAnimation { self.processingMessage = "扫描目录中..." }
                }
                var sourceResults: [MediaSourceScanResult] = []
                for (index, source) in validSources.enumerated() {
                    if Task.isCancelled {
                        await MainActor.run {
                            guard self.currentScanRunID == runID else { return }
                            activeScanningSourceID = nil
                            removedSourceIDsDuringRun = []
                            currentScanTask = nil
                            isProcessing = false
                        }
                        return
                    }
                    if let sid = source.id, removedSourceIDsDuringRun.contains(sid) { continue }
                    await MainActor.run {
                        guard self.currentScanRunID == runID else { return }
                        activeScanningSourceID = source.id
                    }
                    await MainActor.run {
                        guard self.currentScanRunID == runID else { return }
                        withAnimation {
                            self.processingMessage = "扫描中 (\(index + 1)/\(validSources.count))：\(source.name)"
                        }
                    }
                    let result: MediaSourceScanResult
                    if skipTMDBScrape {
                        result = await libraryManager.scanLocalSourceWithResult(
                            source,
                            deferUnidentifiedGroups: true
                        )
                    } else {
                        result = await libraryManager.scanLocalSourceWithResult(
                            source,
                            deferUnidentifiedGroups: true
                        ) { placeholderMovieID in
                            guard let sid = source.id, !Task.isCancelled else { return }
                            let isCurrentRun = await MainActor.run { self.currentScanRunID == runID }
                            guard isCurrentRun else { return }
                            await MainActor.run {
                                withAnimation {
                                    self.processingMessage = "刮削元数据和海报：\(source.name)"
                                }
                            }
                            try? await libraryManager.processUnmatchedFiles(
                                sourceID: sid,
                                placeholderMovieID: placeholderMovieID,
                                exposeFailures: false
                            )
                            await MainActor.run {
                                guard self.currentScanRunID == runID else { return }
                                loadData()
                            }
                        }
                    }
                    let isCurrentRun = await MainActor.run { self.currentScanRunID == runID }
                    guard isCurrentRun else { return }
                    await MainActor.run { activeScanningSourceID = nil }
                    sourceResults.append(result)
                    await MainActor.run {
                        guard self.currentScanRunID == runID else { return }
                        withAnimation {
                            self.processingMessage = result.isSuccess
                                ? "完成：\(source.name)（新增\(result.insertedCount) 移除\(result.removedCount)）"
                                : "失败：\(source.name)（\(result.errorCategory?.displayName ?? "未知错误")）"
                        }
                    }
                    if result.isSuccess, let sid = source.id, !removedSourceIDsDuringRun.contains(sid) {
                        try Task.checkCancellation()
                        if skipTMDBScrape {
                            await MainActor.run {
                                guard self.currentScanRunID == runID else { return }
                                withAnimation {
                                    self.processingMessage = "显示未刮削海报：\(source.name)"
                                }
                            }
                            continue
                        }
                        await MainActor.run {
                            guard self.currentScanRunID == runID else { return }
                            withAnimation {
                                self.processingMessage = "检查未完成刮削：\(source.name)"
                            }
                        }
                        try await libraryManager.processUnmatchedFiles(sourceID: sid)
                        await MainActor.run {
                            guard self.currentScanRunID == runID else { return }
                            loadData()
                        }
                    }
                }
                await MainActor.run {
                    guard self.currentScanRunID == runID else { return }
                    self.lastScanResults = sourceResults
                    self.lastScanDiagnosticsText = MediaSourceScanDiagnosticsFormatter.diagnosticsReport(results: sourceResults)
                }
                
                if Task.isCancelled {
                    await MainActor.run {
                        guard self.currentScanRunID == runID else { return }
                        activeScanningSourceID = nil
                        removedSourceIDsDuringRun = []
                        currentScanTask = nil
                        isProcessing = false
                    }
                    return
                }
                await MainActor.run {
                    guard self.currentScanRunID == runID else { return }
                    withAnimation { self.processingMessage = "显示未识别视频..." }
                }
                let unidentifiedInsertedCount = await libraryManager.insertDeferredUnidentifiedMedia(from: sourceResults)
                if unidentifiedInsertedCount > 0 {
                    await MainActor.run {
                        guard self.currentScanRunID == runID else { return }
                        loadData()
                    }
                }
                if skipTMDBScrape {
                    await MainActor.run {
                        guard self.currentScanRunID == runID else { return }
                        withAnimation { self.processingMessage = "等待 TMDB API 连通后刮削..." }
                    }
                    await MainActor.run {
                        guard self.currentScanRunID == runID else { return }
                        activeScanningSourceID = nil
                        removedSourceIDsDuringRun = []
                        currentScanTask = nil
                        loadData()
                        let shouldChainRescan = needsRescanAfterCurrentRun
                        needsRescanAfterCurrentRun = false
                        withAnimation { isProcessing = false }
                        if shouldChainRescan {
                            DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) {
                                triggerScanAndScrape()
                            }
                        }
                    }
                    return
                }
                if Task.isCancelled {
                    await MainActor.run {
                        guard self.currentScanRunID == runID else { return }
                        activeScanningSourceID = nil
                        removedSourceIDsDuringRun = []
                        currentScanTask = nil
                        isProcessing = false
                    }
                    return
                }
                await MainActor.run {
                    guard self.currentScanRunID == runID else { return }
                    withAnimation { self.processingMessage = "批量补抓分集剧照中..." }
                }
                let orderedMovieIDs = await MainActor.run {
                    self.displayedMovies.compactMap(\.id)
                }
                thumbManager.enqueueMissingEpisodeThumbnailsForLibrary(retryFailed: true, orderedMovieIDs: orderedMovieIDs)
                
                if Task.isCancelled {
                    await MainActor.run {
                        guard self.currentScanRunID == runID else { return }
                        activeScanningSourceID = nil
                        removedSourceIDsDuringRun = []
                        currentScanTask = nil
                        isProcessing = false
                    }
                    return
                }
                await MainActor.run {
                    guard self.currentScanRunID == runID else { return }
                    activeScanningSourceID = nil
                    removedSourceIDsDuringRun = []
                    currentScanTask = nil
                    loadData()
                    let failedResults = self.lastScanResults.filter { !$0.isSuccess }
                    if !failedResults.isEmpty {
                        let names = failedResults.map(\.sourceName).joined(separator: "、")
                        let firstMessage = failedResults.first?.userMessage ?? "部分源扫描失败。"
                        self.scanSummaryMessage = "本次同步有 \(failedResults.count) 个源失败：\(names)\n\(firstMessage)"
                        self.isShowingScanSummaryAlert = true
                    }
                    let shouldChainRescan = needsRescanAfterCurrentRun
                    needsRescanAfterCurrentRun = false
                    withAnimation { isProcessing = false }
                    if shouldChainRescan {
                        DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) {
                            triggerScanAndScrape()
                        }
                    }
                }
            } catch {
                await MainActor.run {
                    guard self.currentScanRunID == runID else { return }
                    activeScanningSourceID = nil
                    removedSourceIDsDuringRun = []
                    currentScanTask = nil
                    isProcessing = false
                }
            }
        }
    }

    private func copyLastDiagnosticsToPasteboard() {
        guard !unifiedDiagnosticsText.isEmpty else { return }
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString(unifiedDiagnosticsText, forType: .string)
        scanSummaryMessage = "诊断信息已复制到剪贴板。"
        isShowingScanSummaryAlert = true
    }
    
}

// 🌟 彻底删除了原先几百行的内网目录浏览器 (SourceFileBrowserView)
// 因为我们现在全部交给原生的 Finder 来选了！
