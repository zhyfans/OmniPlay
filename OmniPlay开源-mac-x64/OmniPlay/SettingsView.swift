import SwiftUI
import GRDB
import AppKit

struct SettingsView: View {
    @Environment(\.dismiss) var dismiss
    private let focusTMDBApi: Bool
    
    // 绑定全局持久化设置
    @AppStorage("keepLocalPosters") var keepLocalPosters = true
    @AppStorage("autoScanOnStartup") var autoScanOnStartup = true
    @AppStorage("autoCheckUpdatesOnStartup") var autoCheckUpdatesOnStartup = true
    @AppStorage("enableFastTooltip") var enableFastTooltip = true
    @AppStorage("showMediaSourceRealPath") var showMediaSourceRealPath = true
    @AppStorage("removeWebDAVCredentialWhenRemovingSource") var removeWebDAVCredentialWhenRemovingSource = false
    @AppStorage("enableLocalMetadataImport") var enableLocalMetadataImport = false
    @AppStorage("enableLocalMetadataExport") var enableLocalMetadataExport = false
    @AppStorage("tmdbUsePublicSource") var tmdbUsePublicSource = true
    @AppStorage("tmdbApiKey") var tmdbApiKey = ""
    
    @AppStorage("appLanguage") var appLanguage = "zh-Hans"
    @AppStorage("defaultAudio") var defaultAudio = "auto"
    @AppStorage("defaultSub") var defaultSub = "chi"
    @AppStorage("playbackQualityMode") var playbackQualityMode = "balanced"
    @AppStorage("playBluRayExtras") var playBluRayExtras = false
    
    @AppStorage("appTheme") var appTheme = ThemeType.appleLight.rawValue
    var theme: AppTheme { ThemeType(rawValue: appTheme)?.colors ?? ThemeType.crystal.colors }
    
    @ObservedObject var cacheManager = OfflineCacheManager.shared
    
    @State private var isValidatingAPI = false
    @State private var apiValidationMessage = ""
    @State private var apiValidationColor: Color = .primary
    @State private var isCheckingForUpdate = false
    @State private var updateStatusMessage = ""
    @State private var updateStatusColor: Color = .secondary
    @FocusState private var isTMDBApiFocused: Bool

    private let githubRepositoryURL = "https://github.com/nandieling/OmniPlay"
    private let githubLatestReleaseAPI = "https://api.github.com/repos/nandieling/OmniPlay/releases/latest"

    init(focusTMDBApi: Bool = false) {
        self.focusTMDBApi = focusTMDBApi
    }
    
    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text("偏好设置").font(.title2.bold())
                Spacer()
                Button(action: { dismiss() }) {
                    Image(systemName: "xmark.circle.fill").font(.title2).foregroundColor(.secondary)
                }.buttonStyle(.plain)
            }.padding()
            Divider()
            
            Form {
                Section(header: Text("软件更新").font(.headline)) {
                    HStack(spacing: 8) {
                        Text("当前版本")
                        Text(currentAppVersion)
                            .foregroundColor(theme.textSecondary)
                        Spacer(minLength: 0)
                    }

                    Toggle("启动时自动检查更新", isOn: $autoCheckUpdatesOnStartup)

                    HStack(spacing: 10) {
                        Button(action: { checkForUpdates(install: false) }) {
                            if isCheckingForUpdate {
                                ProgressView().controlSize(.small).frame(width: 52)
                            } else {
                                Text("检查更新")
                            }
                        }
                        .disabled(isCheckingForUpdate)

                        Button("直接更新") {
                            checkForUpdates(install: true)
                        }
                        .disabled(isCheckingForUpdate)

                        Button("打开 GitHub 仓库") {
                            openExternalURL(githubRepositoryURL)
                        }
                    }
                    .buttonStyle(.bordered)
                    .tint(theme.accent)

                    if !updateStatusMessage.isEmpty {
                        Text(updateStatusMessage)
                            .font(.caption)
                            .foregroundColor(updateStatusColor)
                    }
                }

                Section(header: Text("基础设置").font(.headline)) {
                    Toggle("启动时自动扫描并同步库", isOn: $autoScanOnStartup)
                    Toggle("删除文件夹时保留本地缓存海报", isOn: $keepLocalPosters)
                    Toggle("启用快速悬停提示 (Tooltip)", isOn: $enableFastTooltip)
                    Toggle("媒体源显示真实路径", isOn: $showMediaSourceRealPath)
                    Toggle("移除 WebDAV 源时同时删除保存的登录凭据", isOn: $removeWebDAVCredentialWhenRemovingSource)
                }

                Section(header: Text("本地刮削文件").font(.headline).padding(.top, 10)) {
                    Toggle("读取本地 NFO、海报和剧照", isOn: $enableLocalMetadataImport)
                    Toggle("刮削完成后保存 NFO、海报和剧照到本地", isOn: $enableLocalMetadataExport)
                    Text("默认关闭。仅对本地文件夹源生效，支持 movie.nfo、tvshow.nfo、同名 .nfo、poster/fanart 与同名 -thumb 图片。")
                        .font(.caption)
                        .foregroundColor(theme.textSecondary)
                }
                
                Section(header: Text("离线缓存").font(.headline).padding(.top, 10)) {
                    HStack {
                        Text("保存位置")
                        Spacer()
                        
                        if let dir = cacheManager.cacheDirectory {
                            Text(dir.path)
                                .foregroundColor(.secondary)
                                .lineLimit(1)
                                .truncationMode(.middle)
                                .frame(maxWidth: 200, alignment: .trailing)
                        } else {
                            Text("未设置").foregroundColor(.red)
                        }
                        
                        Button("更改目录") {
                            cacheManager.selectCacheDirectory()
                        }
                        .buttonStyle(.bordered)
                        .tint(theme.accent)
                    }
                    Text("设置后，您可以在影视详情页将 NAS 视频缓存至此目录，方便离线观看。")
                        .font(.caption).foregroundColor(theme.textSecondary)
                }

                Section(header: Text("外观与主题").font(.headline).padding(.top, 10)) {
                    Picker("应用主题配色", selection: $appTheme) {
                        ForEach(ThemeType.allCases) { themeItem in
                            HStack {
                                Circle()
                                    .fill(themeItem.colors.accent)
                                    .frame(width: 12, height: 12)
                                Text(themeItem.displayName)
                            }.tag(themeItem.rawValue)
                        }
                    }
                }
                
                Section(header: Text("语言与播放偏好").font(.headline).padding(.top, 10)) {
                    Picker("软件与刮削语言", selection: $appLanguage) {
                        Text("简体中文").tag("zh-Hans")
                        Text("English").tag("en")
                    }.help("更改此项将改变 TMDB 刮削获取的海报和简介语言。")
                    
                    Picker("默认首选音轨", selection: $defaultAudio) {
                        Text("智能匹配 (制片国家语言)").tag("auto")
                        Text("中文").tag("chi")
                        Text("英语").tag("eng")
                        Text("日语").tag("jpn")
                    }
                    
                    Picker("默认首选字幕", selection: $defaultSub) {
                        Text("中文优先 (简/繁)").tag("chi")
                        Text("英语优先").tag("eng")
                        Text("关闭字幕").tag("no")
                    }

                    Picker("播放画质模式", selection: $playbackQualityMode) {
                        Text("流畅优先").tag("smooth")
                        Text("平衡 (推荐)").tag("balanced")
                        Text("画质优先").tag("quality")
                    }
                    .help("切换后新打开的播放窗口会生效。")

                    Toggle("播放蓝光花絮", isOn: $playBluRayExtras)
                        .help("默认关闭。关闭时蓝光 BDMV 会按 STREAM 文件大小自动选择主电影；开启后会按文件编号播放包含花絮在内的所有 STREAM 视频。")
                }
                
                Section(header: Text("刮削服务 (TMDB)").font(.headline).padding(.top, 10)) {
                    Toggle("启用公共源 TMDB", isOn: $tmdbUsePublicSource)
                        .help("默认开启。关闭后只有填写自定义 TMDB API 时才能继续刮削和获取剧照。")

                    Text("公共源使用应用内共享额度，默认会按更保守的请求频率访问，并在触发 TMDB 限流时自动退避。建议填写自己的 TMDB API Key / v4 令牌，以获得更稳定、更快的刮削。")
                        .font(.caption)
                        .foregroundColor(theme.textSecondary)

                    HStack {
                        TextField("API Key / v4 令牌", text: $tmdbApiKey)
                            .textFieldStyle(.roundedBorder)
                            .focused($isTMDBApiFocused)
                        
                        Button(action: validateTMDBApi) {
                            if isValidatingAPI {
                                ProgressView().controlSize(.small).frame(width: 40)
                            } else {
                                Text("验证")
                            }
                        }
                        .disabled(trimmedTMDBApiKey.isEmpty || isValidatingAPI)
                    }

                    Text("填写自定义 API 后会优先使用你的密钥；“验证”只检查这里填写的自定义密钥。")
                        .font(.caption)
                        .foregroundColor(theme.textSecondary)

                    Link("请往TMDB官网注册获取api：https://www.themoviedb.org", destination: URL(string: "https://www.themoviedb.org")!)
                        .font(.caption)
                        .foregroundColor(theme.accent)

                    if trimmedTMDBApiKey.isEmpty, tmdbUsePublicSource {
                        Text("当前正在使用公共源 TMDB。共享额度可能波动，长期使用更建议配置自定义 API。")
                            .font(.caption)
                            .foregroundColor(.orange)
                    } else if trimmedTMDBApiKey.isEmpty {
                        Text("当前未配置任何 TMDB 凭据。TMDB 刮削、海报与剧照获取将不可用。")
                            .font(.caption)
                            .foregroundColor(.red)
                    }
                    
                    if !apiValidationMessage.isEmpty {
                        Text(apiValidationMessage)
                            .font(.caption)
                            .foregroundColor(apiValidationColor)
                            .padding(.top, 2)
                            .transition(.opacity)
                    }
                }
            }
            .padding().formStyle(.grouped)
        }
        .frame(width: 550, height: 660)
        .environment(\.locale, .init(identifier: appLanguage))
        .onAppear {
            restoreAutomaticUpdateStatusIfAvailable()
            guard focusTMDBApi else { return }
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) {
                isTMDBApiFocused = true
            }
        }
        .onChange(of: appLanguage) { oldValue, newValue in
            UserDefaults.standard.set([newValue], forKey: "AppleLanguages")
            UserDefaults.standard.synchronize()
        }
    }
    
    private var trimmedTMDBApiKey: String {
        tmdbApiKey.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private var currentAppVersion: String {
        let info = Bundle.main.infoDictionary
        return (info?["CFBundleShortVersionString"] as? String)
            ?? (info?["CFBundleVersion"] as? String)
            ?? "dev"
    }

    private func restoreAutomaticUpdateStatusIfAvailable() {
        guard updateStatusMessage.isEmpty,
              let message = MacAppUpdateChecker.cachedAutomaticUpdateMessage(currentVersion: currentAppVersion) else {
            return
        }
        updateStatusMessage = message
        updateStatusColor = .orange
    }
    
    private func validateTMDBApi() {
        let key = trimmedTMDBApiKey
        guard !key.isEmpty else { return }
        isValidatingAPI = true
        apiValidationMessage = "正在连接 TMDB 服务器..."
        apiValidationColor = .secondary
        
        Task {
            let result = await TMDBService.shared.checkConnection(customAPIInput: key)
            await MainActor.run {
                if result.isConnected {
                    apiValidationMessage = "✅ 验证成功！API 状态正常。"
                    apiValidationColor = .green
                } else {
                    apiValidationMessage = "❌ 验证失败：\(result.message)"
                    apiValidationColor = .red
                }
                isValidatingAPI = false
            }
        }
    }

    private func checkForUpdates(install: Bool) {
        isCheckingForUpdate = true
        updateStatusMessage = install ? "正在获取最新版本..." : "正在检查 GitHub 最新版本..."
        updateStatusColor = .secondary

        Task {
            do {
                let release = try await fetchLatestRelease()
                guard !release.isEmpty else {
                    await MainActor.run { isCheckingForUpdate = false }
                    return
                }
                let tagName = (release["tag_name"] as? String) ?? ""
                let htmlURL = (release["html_url"] as? String) ?? "\(githubRepositoryURL)/releases/latest"
                let isNewer = isVersion(tagName, newerThan: currentAppVersion)

                if !install {
                    await MainActor.run {
                        updateStatusMessage = isNewer
                            ? "发现新版本 \(tagName)，可点击“直接更新”。"
                            : "当前已是最新版本（\(currentAppVersion)）。"
                        updateStatusColor = isNewer ? .orange : .green
                        isCheckingForUpdate = false
                    }
                    return
                }

                guard isNewer else {
                    await MainActor.run {
                        updateStatusMessage = "当前已是最新版本（\(currentAppVersion)）。"
                        updateStatusColor = .green
                        isCheckingForUpdate = false
                    }
                    return
                }

                guard let asset = preferredUpdateAsset(from: release["assets"] as? [[String: Any]]) else {
                    await MainActor.run {
                        updateStatusMessage = "未找到适合当前 Mac 的安装包，已打开 GitHub Release 页面。"
                        updateStatusColor = .orange
                        isCheckingForUpdate = false
                        openExternalURL(htmlURL)
                    }
                    return
                }

                let fileURL = try await downloadUpdateAsset(asset)
                await MainActor.run {
                    updateStatusMessage = "已下载 \(fileURL.lastPathComponent)，正在打开安装包。"
                    updateStatusColor = .green
                    isCheckingForUpdate = false
                    NSWorkspace.shared.open(fileURL)
                }
            } catch {
                await MainActor.run {
                    updateStatusMessage = "检查更新失败：\(error.localizedDescription)"
                    updateStatusColor = .red
                    isCheckingForUpdate = false
                }
            }
        }
    }

    private func fetchLatestRelease() async throws -> [String: Any] {
        guard let url = URL(string: githubLatestReleaseAPI) else { return [:] }
        var request = URLRequest(url: url)
        request.setValue("OmniPlay", forHTTPHeaderField: "User-Agent")
        let (data, response) = try await URLSession.shared.data(for: request)
        if let http = response as? HTTPURLResponse, http.statusCode == 404 {
            await MainActor.run {
                updateStatusMessage = "GitHub 仓库暂未发布 Release，已打开仓库页面。"
                updateStatusColor = .orange
                openExternalURL(githubRepositoryURL)
            }
            return [:]
        }
        guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw NSError(domain: "OmniPlayUpdate", code: 1, userInfo: [NSLocalizedDescriptionKey: "GitHub 返回内容无法解析。"])
        }
        return object
    }

    private func preferredUpdateAsset(from assets: [[String: Any]]?) -> (name: String, url: URL)? {
        guard let assets else { return nil }
        let candidates = assets.compactMap { asset -> (name: String, url: URL, score: Int)? in
            guard let name = asset["name"] as? String,
                  let urlString = asset["browser_download_url"] as? String,
                  let url = URL(string: urlString) else { return nil }
            let lower = name.lowercased()
            guard lower.hasSuffix(".dmg") || lower.hasSuffix(".zip") || lower.hasSuffix(".pkg") else { return nil }
            var score = 0
            if lower.contains("mac") || lower.contains("darwin") || lower.contains("osx") { score += 3 }
            if lower.contains("universal") { score += 2 }
            #if arch(arm64)
            if lower.contains("arm64") || lower.contains("aarch64") || lower.contains("apple") { score += 4 }
            if lower.contains("x64") || lower.contains("x86_64") || lower.contains("intel") { score -= 3 }
            #else
            if lower.contains("x64") || lower.contains("x86_64") || lower.contains("intel") { score += 4 }
            if lower.contains("arm64") || lower.contains("aarch64") { score -= 3 }
            #endif
            return (name, url, score)
        }
        return candidates.max { $0.score < $1.score }.map { ($0.name, $0.url) }
    }

    private func downloadUpdateAsset(_ asset: (name: String, url: URL)) async throws -> URL {
        let (temporaryURL, _) = try await URLSession.shared.download(from: asset.url)
        let downloadDirectory = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        let destination = downloadDirectory.appendingPathComponent(asset.name)
        try? FileManager.default.removeItem(at: destination)
        try FileManager.default.moveItem(at: temporaryURL, to: destination)
        return destination
    }

    private func isVersion(_ candidate: String, newerThan current: String) -> Bool {
        let lhs = versionParts(candidate)
        let rhs = versionParts(current)
        guard !lhs.isEmpty else { return false }
        let count = max(lhs.count, rhs.count)
        for index in 0..<count {
            let left = index < lhs.count ? lhs[index] : 0
            let right = index < rhs.count ? rhs[index] : 0
            if left != right { return left > right }
        }
        return false
    }

    private func versionParts(_ value: String) -> [Int] {
        value.lowercased()
            .trimmingCharacters(in: CharacterSet(charactersIn: "v "))
            .split { !$0.isNumber }
            .compactMap { Int($0) }
    }

    private func openExternalURL(_ value: String) {
        guard let url = URL(string: value) else { return }
        NSWorkspace.shared.open(url)
    }
}

enum MacAppUpdateChecker {
    private static let autoCheckKey = "autoCheckUpdatesOnStartup"
    private static let lastCheckKey = "lastAutomaticUpdateCheckAt"
    private static let detectedVersionKey = "lastDetectedUpdateVersion"
    private static let detectedReleaseURLKey = "lastDetectedUpdateReleaseURL"
    private static let automaticCheckInterval: TimeInterval = 24 * 60 * 60

    static func checkAtStartupIfNeeded() {
        let defaults = UserDefaults.standard
        let isEnabled = defaults.object(forKey: autoCheckKey) as? Bool ?? true
        guard isEnabled else { return }
        guard ProcessInfo.processInfo.environment["UITEST_MODE"] != "1" else { return }

        let now = Date().timeIntervalSince1970
        let lastCheck = defaults.double(forKey: lastCheckKey)
        guard lastCheck <= 0 || now - lastCheck >= automaticCheckInterval else { return }

        defaults.set(now, forKey: lastCheckKey)
        Task.detached(priority: .background) {
            do {
                let release = try await fetchLatestRelease()
                let currentVersion = currentAppVersion()
                await MainActor.run {
                    let defaults = UserDefaults.standard
                    if isVersion(release.tagName, newerThan: currentVersion) {
                        defaults.set(release.tagName, forKey: detectedVersionKey)
                        defaults.set(release.htmlURL, forKey: detectedReleaseURLKey)
                    } else {
                        defaults.removeObject(forKey: detectedVersionKey)
                        defaults.removeObject(forKey: detectedReleaseURLKey)
                    }
                }
            } catch {
                // 启动时检查只做静默提示，网络失败不打扰用户。
            }
        }
    }

    static func cachedAutomaticUpdateMessage(currentVersion: String) -> String? {
        let defaults = UserDefaults.standard
        guard let version = defaults.string(forKey: detectedVersionKey),
              isVersion(version, newerThan: currentVersion) else {
            return nil
        }
        return "后台自动检查发现新版本 \(version)，可点击“直接更新”。"
    }

    nonisolated private static func fetchLatestRelease() async throws -> (tagName: String, htmlURL: String) {
        guard let url = URL(string: "https://api.github.com/repos/nandieling/OmniPlay/releases/latest") else { return ("", "") }
        var request = URLRequest(url: url)
        request.setValue("OmniPlay", forHTTPHeaderField: "User-Agent")
        let (data, response) = try await URLSession.shared.data(for: request)
        if let http = response as? HTTPURLResponse, http.statusCode == 404 {
            return ("", "https://github.com/nandieling/OmniPlay")
        }
        guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return ("", "")
        }
        return (
            (object["tag_name"] as? String) ?? "",
            (object["html_url"] as? String) ?? "https://github.com/nandieling/OmniPlay/releases/latest"
        )
    }

    nonisolated private static func currentAppVersion() -> String {
        let info = Bundle.main.infoDictionary
        return (info?["CFBundleShortVersionString"] as? String)
            ?? (info?["CFBundleVersion"] as? String)
            ?? "dev"
    }

    nonisolated private static func isVersion(_ candidate: String, newerThan current: String) -> Bool {
        let lhs = versionParts(candidate)
        let rhs = versionParts(current)
        guard !lhs.isEmpty else { return false }
        let count = max(lhs.count, rhs.count)
        for index in 0..<count {
            let left = index < lhs.count ? lhs[index] : 0
            let right = index < rhs.count ? rhs[index] : 0
            if left != right { return left > right }
        }
        return false
    }

    nonisolated private static func versionParts(_ value: String) -> [Int] {
        value.lowercased()
            .trimmingCharacters(in: CharacterSet(charactersIn: "v "))
            .split { !$0.isNumber }
            .compactMap { Int($0) }
    }
}
