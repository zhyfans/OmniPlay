import Foundation
import GRDB
import Security

enum MediaSourceProtocol: String, Codable, CaseIterable {
    case local
    case webdav
    case direct
    case plex
    case emby
    case jellyfin

    nonisolated func normalizedBaseURL(_ value: String) -> String {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return trimmed }

        switch self {
        case .local:
            if trimmed == "/" { return trimmed }
            var value = trimmed
            while value.count > 1 && value.hasSuffix("/") {
                value.removeLast()
            }
            return value
        case .webdav, .plex, .emby, .jellyfin:
            guard let url = URL(string: trimmed) else { return trimmed }
            var components = URLComponents(url: url, resolvingAgainstBaseURL: false)
            if components?.host?.caseInsensitiveCompare("localhost") == .orderedSame {
                components?.host = "127.0.0.1"
            }
            if components?.scheme?.lowercased() == "http", components?.port == nil {
                switch self {
                case .plex:
                    components?.port = 32400
                case .emby, .jellyfin:
                    components?.port = 8096
                default:
                    break
                }
            }
            var normalized = components?.url?.absoluteString ?? url.absoluteString
            if normalized.hasSuffix("/") {
                normalized.removeLast()
            }
            return normalized
        case .direct:
            return "/"
        }
    }

    nonisolated func isValidBaseURL(_ value: String) -> Bool {
        let normalized = normalizedBaseURL(value)
        switch self {
        case .local:
            return !normalized.isEmpty
        case .webdav, .plex, .emby, .jellyfin:
            guard let url = URL(string: normalized), let scheme = url.scheme?.lowercased() else { return false }
            return (scheme == "http" || scheme == "https") && (url.host?.isEmpty == false)
        case .direct:
            return normalized == "/"
        }
    }

    nonisolated func webDAVPathValidationError(_ value: String) -> String? {
        guard self == .webdav else { return nil }
        let normalized = normalizedBaseURL(value)
        guard let url = URL(string: normalized), let host = url.host, !host.isEmpty else {
            return "WebDAV 地址无效，请输入 http(s):// 开头且包含主机名的地址。"
        }
        let segments = url.path.split(separator: "/").map(String.init)
        if segments.isEmpty {
            return "请填写 NAS 里的具体媒体文件夹路径，例如 /电影 或 /dav/Movies。"
        }
        if segments.count == 1 {
            let root = segments[0].lowercased()
            if root == "dav" || root == "webdav" {
                return "当前地址看起来是 WebDAV 服务根目录，请继续指定媒体文件夹，例如 /dav/Movies。"
            }
        }
        return nil
    }
}

nonisolated struct MediaServerAuthConfig: Codable {
    var token: String
    var userId: String?
    var libraryId: String?
    var libraryName: String?
    var libraryType: String?

    nonisolated static func encode(
        token: String,
        userId: String?,
        libraryId: String? = nil,
        libraryName: String? = nil,
        libraryType: String? = nil
    ) -> String? {
        let normalized = MediaServerAuthConfig(
            token: token.trimmingCharacters(in: .whitespacesAndNewlines),
            userId: userId?.trimmingCharacters(in: .whitespacesAndNewlines),
            libraryId: libraryId?.trimmingCharacters(in: .whitespacesAndNewlines),
            libraryName: libraryName?.trimmingCharacters(in: .whitespacesAndNewlines),
            libraryType: libraryType?.trimmingCharacters(in: .whitespacesAndNewlines)
        )
        guard !normalized.token.isEmpty || normalized.userId?.isEmpty == false || normalized.libraryId?.isEmpty == false else { return nil }
        guard let data = try? JSONEncoder().encode(normalized) else { return nil }
        return String(data: data, encoding: .utf8)
    }

    nonisolated static func decode(_ value: String?) -> MediaServerAuthConfig? {
        guard let value, let data = value.data(using: .utf8) else { return nil }
        return try? JSONDecoder().decode(MediaServerAuthConfig.self, from: data)
    }
}

// 1. 媒体源
struct MediaSource: Codable, FetchableRecord, PersistableRecord {
    var id: Int64?
    var name: String
    var protocolType: String
    var baseUrl: String
    var authConfig: String?
    var isEnabled: Bool
    var disabledAt: Double?

    init(
        id: Int64? = nil,
        name: String,
        protocolType: String,
        baseUrl: String,
        authConfig: String? = nil,
        isEnabled: Bool = true,
        disabledAt: Double? = nil
    ) {
        self.id = id
        self.name = name
        self.protocolType = protocolType
        self.baseUrl = baseUrl
        self.authConfig = authConfig
        self.isEnabled = isEnabled
        self.disabledAt = disabledAt
    }

    nonisolated var protocolKind: MediaSourceProtocol? {
        MediaSourceProtocol(rawValue: protocolType)
    }

    nonisolated func normalizedBaseURL() -> String {
        guard let kind = protocolKind else { return baseUrl }
        return kind.normalizedBaseURL(baseUrl)
    }

    nonisolated func isValidConfiguration() -> Bool {
        guard let kind = protocolKind else { return false }
        return kind.isValidBaseURL(baseUrl)
    }

    nonisolated func displayBaseURL() -> String {
        guard protocolKind == .webdav else { return baseUrl }
        let normalized = normalizedBaseURL()
        guard var components = URLComponents(string: normalized) else {
            return normalized.removingPercentEncoding ?? normalized
        }

        // UI 展示层不显示凭据，避免泄露。
        components.user = nil
        components.password = nil

        let decodedPath = components.percentEncodedPath.removingPercentEncoding ?? components.percentEncodedPath
        components.percentEncodedPath = decodedPath.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? components.percentEncodedPath

        guard var display = components.string else {
            return normalized.removingPercentEncoding ?? normalized
        }
        if let range = display.range(of: components.percentEncodedPath) {
            display.replaceSubrange(range, with: decodedPath)
        }
        return display
    }
}

final class WebDAVCredentialStore {
    static let shared = WebDAVCredentialStore()

    struct Credential {
        let username: String
        let password: String
    }

    private let service = "nan.omniplay.webdav.credential"
    private let prefix = "keychain:webdav:"

    private init() {}

    func authReference(for credentialID: String) -> String {
        "\(prefix)\(credentialID)"
    }

    func credentialID(from authConfig: String?) -> String? {
        guard let authConfig else { return nil }
        let trimmed = authConfig.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.hasPrefix(prefix) else { return nil }
        let id = String(trimmed.dropFirst(prefix.count))
        return id.isEmpty ? nil : id
    }

    func decodeLegacyCredential(from authConfig: String?) -> Credential? {
        guard let authConfig else { return nil }
        let trimmed = authConfig.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }

        if let data = trimmed.data(using: .utf8),
           let object = try? JSONSerialization.jsonObject(with: data),
           let dict = object as? [String: Any] {
            let username = (dict["username"] as? String) ?? (dict["user"] as? String) ?? (dict["account"] as? String)
            let password = (dict["password"] as? String) ?? (dict["pass"] as? String) ?? (dict["pwd"] as? String) ?? ""
            if let username, !username.isEmpty {
                return Credential(username: username, password: password)
            }
        }

        if let colon = trimmed.firstIndex(of: ":") {
            let user = String(trimmed[..<colon]).trimmingCharacters(in: .whitespacesAndNewlines)
            let pass = String(trimmed[trimmed.index(after: colon)...]).trimmingCharacters(in: .whitespacesAndNewlines)
            if !user.isEmpty {
                return Credential(username: user, password: pass)
            }
        }
        return nil
    }

    func saveCredential(username: String, password: String, credentialID: String = UUID().uuidString) throws -> String {
        let credential = Credential(username: username, password: password)
        let data = try serialize(credential: credential)

        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: credentialID
        ]

        let attributes: [String: Any] = [
            kSecValueData as String: data
        ]

        let status: OSStatus
        if existsCredential(id: credentialID) {
            status = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
        } else {
            var addQuery = query
            addQuery[kSecValueData as String] = data
            status = SecItemAdd(addQuery as CFDictionary, nil)
        }

        guard status == errSecSuccess else {
            throw NSError(
                domain: "WebDAVCredentialStore",
                code: Int(status),
                userInfo: [NSLocalizedDescriptionKey: "Keychain 保存失败，状态码：\(status)"]
            )
        }
        return credentialID
    }

    func loadCredential(id: String) -> Credential? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: id,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne
        ]

        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        guard status == errSecSuccess,
              let data = result as? Data,
              let object = try? JSONSerialization.jsonObject(with: data),
              let dict = object as? [String: String],
              let username = dict["username"] else {
            return nil
        }
        let password = dict["password"] ?? ""
        return Credential(username: username, password: password)
    }

    func removeCredential(id: String) {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: id
        ]
        _ = SecItemDelete(query as CFDictionary)
    }

    private func serialize(credential: Credential) throws -> Data {
        let payload: [String: String] = [
            "username": credential.username,
            "password": credential.password
        ]
        return try JSONSerialization.data(withJSONObject: payload, options: [])
    }

    private func existsCredential(id: String) -> Bool {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: id,
            kSecMatchLimit as String: kSecMatchLimitOne
        ]
        let status = SecItemCopyMatching(query as CFDictionary, nil)
        return status == errSecSuccess
    }
}

// 2. 电影元数据 (TMDB)
struct Movie: Codable, FetchableRecord, PersistableRecord {
    var id: Int64?
    var title: String
    var releaseDate: String?
    var overview: String?
    var posterPath: String?
    var voteAverage: Double?  // 🌟 新增：TMDB 官方评分
    var isLocked: Bool = false
    
    static let videoFiles = hasMany(VideoFile.self)
}

// 3. 剧集元数据
struct TVShow: Codable, FetchableRecord, PersistableRecord {
    var id: Int64
    var title: String
    var posterPath: String?
    var voteAverage: Double?  // 🌟 新增：TMDB 官方评分
    var isLocked: Bool = false
}

// 4. 视频文件
struct VideoFile: Codable, FetchableRecord, PersistableRecord {
    var id: String
    var sourceId: Int64
    var relativePath: String
    var fileName: String
    var mediaType: String
    var movieId: Int64?
    var episodeId: Int64?
    
    // 🌟 记录播放进度和总时长
    var playProgress: Double
    var duration: Double = 0.0
    var customSubtitle: String? = nil
    var fileSize: Int64 = 0
    var lastPlayedAt: Double?
    
    static let mediaSource = belongsTo(MediaSource.self)
}

private enum LibraryVisibilitySQL {
    static let enabledSourcePredicate = "COALESCE(mediaSource.isEnabled, 1) = 1"
}

extension VideoFile {
    static func fetchVisibleFiles(movieId: Int64?, in db: Database) throws -> [VideoFile] {
        guard let movieId else { return [] }
        return try VideoFile.fetchAll(
            db,
            sql: """
            SELECT videoFile.*
            FROM videoFile
            JOIN mediaSource ON mediaSource.id = videoFile.sourceId
            WHERE videoFile.movieId = ?
              AND \(LibraryVisibilitySQL.enabledSourcePredicate)
            """,
            arguments: [movieId]
        )
    }

    static func fetchVisibleSourcePairs(movieId: Int64?, in db: Database) throws -> [(VideoFile, MediaSource?)] {
        let files = try fetchVisibleFiles(movieId: movieId, in: db)
        return try files.map { file in
            (file, try file.request(for: VideoFile.mediaSource).fetchOne(db))
        }
    }
}
