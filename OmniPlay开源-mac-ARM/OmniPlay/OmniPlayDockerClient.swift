import Foundation

struct OmniPlayDockerAuthConfig: Codable {
    var username: String?
    var sessionCookie: String?

    static func encode(username: String?, sessionCookie: String?) -> String? {
        let config = OmniPlayDockerAuthConfig(
            username: username?.trimmingCharacters(in: .whitespacesAndNewlines),
            sessionCookie: sessionCookie?.trimmingCharacters(in: .whitespacesAndNewlines)
        )
        guard config.username?.isEmpty == false || config.sessionCookie?.isEmpty == false else { return nil }
        guard let data = try? JSONEncoder().encode(config) else { return nil }
        return String(data: data, encoding: .utf8)
    }

    static func decode(_ value: String?) -> OmniPlayDockerAuthConfig? {
        guard let value,
              let data = value.data(using: .utf8) else {
            return nil
        }
        return try? JSONDecoder().decode(OmniPlayDockerAuthConfig.self, from: data)
    }
}

struct OmniPlayDockerLibraryItem: Decodable {
    let id: String
    let itemKind: String
    let title: String
    let releaseDate: String?
    let overview: String?
    let posterAssetId: String?
    let voteAverage: Double?
    let doubanRating: Double?
    let maxProgressSeconds: Double
    let maxDurationSeconds: Double
    let updatedAt: String
}

struct OmniPlayDockerLibraryDetail: Decodable {
    let id: String
    let itemKind: String
    let title: String
    let releaseDate: String?
    let overview: String?
    let posterAssetId: String?
    let voteAverage: Double?
    let doubanRating: Double?
    let douban: OmniPlayDockerDoubanMetadata?
    let videoFiles: [OmniPlayDockerVideoFile]
    let seasons: [OmniPlayDockerSeason]
}

struct OmniPlayDockerDoubanMetadata: Codable {
    let subjectId: String
    let subjectUrl: String
    let title: String
    let originalTitle: String?
    let year: String?
    let rating: Double?
    let ratingCount: Int?
    let summary: String?
    let genres: String?
    let countries: String?
    let posterUrl: String?
    let fetchedAt: String
}

struct OmniPlayDockerDoubanMetadataImportRequest: Encodable {
    let subjectId: String
    let subjectUrl: String
    let title: String
    let originalTitle: String?
    let year: String?
    let rating: Double?
    let ratingCount: Int?
    let summary: String?
    let genres: String?
    let countries: String?
    let posterUrl: String?
    let fetchedAt: String
}

struct OmniPlayDockerSeason: Decodable {
    let episodes: [OmniPlayDockerEpisode]
}

struct OmniPlayDockerEpisode: Decodable {
    let videoFile: OmniPlayDockerVideoFile?
}

struct OmniPlayDockerVideoFile: Decodable {
    let id: String
    let relativePath: String
    let fileName: String
    let mediaKind: String
    let fileSizeBytes: Int64?
    let durationSeconds: Double
    let positionSeconds: Double
    let isWatched: Bool
}

struct OmniPlayDockerPlaybackTicket: Decodable {
    let streamUrl: String
}

enum OmniPlayDockerClientError: LocalizedError {
    case invalidBaseURL
    case invalidResponse
    case authenticationRequired
    case requestFailed(Int, String?)

    var errorDescription: String? {
        switch self {
        case .invalidBaseURL:
            return "Docker 服务地址无效。"
        case .invalidResponse:
            return "Docker 服务响应无效。"
        case .authenticationRequired:
            return "Docker 服务需要登录，请填写用户名和密码。"
        case .requestFailed(let status, let message):
            if let message, !message.isEmpty {
                return "Docker 服务请求失败：HTTP \(status)，\(message)"
            }
            return "Docker 服务请求失败：HTTP \(status)。"
        }
    }
}

final class OmniPlayDockerClient {
    private let baseURL: URL
    private let session: URLSession
    private(set) var sessionCookie: String?

    init(baseURLString: String, sessionCookie: String? = nil) throws {
        let normalized = MediaSourceProtocol.omniplayDocker.normalizedBaseURL(baseURLString)
        guard let url = URL(string: normalized) else { throw OmniPlayDockerClientError.invalidBaseURL }
        self.baseURL = url
        self.sessionCookie = sessionCookie?.trimmingCharacters(in: .whitespacesAndNewlines)

        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 20
        configuration.timeoutIntervalForResource = 60
        configuration.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        configuration.httpCookieStorage = nil
        configuration.httpShouldSetCookies = false
        configuration.urlCache = nil
        // Docker 服务通常在局域网内，禁用系统代理/PAC，避免局域网 HTTP 请求被代理接管后报离线。
        configuration.connectionProxyDictionary = [:]
        self.session = URLSession(configuration: configuration, delegate: LocalNetworkTrustSessionDelegate.shared, delegateQueue: nil)
    }

    func login(username: String, password: String) async throws {
        var request = try makeRequest(path: "/api/auth/login", method: "POST")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: [
            "username": username,
            "password": password
        ])

        let (data, response) = try await session.data(for: request)
        let http = try validate(response: response, data: data, allowUnauthorized: false)
        sessionCookie = Self.sessionCookie(from: http) ?? sessionCookie
    }

    func libraryItems() async throws -> [OmniPlayDockerLibraryItem] {
        try await get(path: "/api/library/items")
    }

    func libraryDetail(id: String) async throws -> OmniPlayDockerLibraryDetail {
        try await get(path: "/api/library/items/\(Self.escapePath(id))")
    }

    func playbackURL(videoFileId: String) async throws -> URL {
        let ticket: OmniPlayDockerPlaybackTicket = try await post(
            path: "/api/playback/files/\(Self.escapePath(videoFileId))/ticket",
            json: nil
        )
        guard let url = absoluteURL(ticket.streamUrl) else {
            throw OmniPlayDockerClientError.invalidResponse
        }
        return url
    }

    func updateProgress(videoFileId: String, positionSeconds: Double, durationSeconds: Double) async throws {
        try await postEmpty(path: "/api/playback/progress", json: [
            "videoFileId": videoFileId,
            "positionSeconds": max(0, positionSeconds),
            "durationSeconds": max(0, durationSeconds),
            "userId": "local"
        ])
    }

    @discardableResult
    func importDoubanMetadata(libraryItemId: String, metadata: DoubanMetadata) async throws -> OmniPlayDockerLibraryDetail {
        let request = OmniPlayDockerDoubanMetadataImportRequest(
            subjectId: metadata.subjectId,
            subjectUrl: metadata.subjectURL,
            title: metadata.title,
            originalTitle: metadata.originalTitle,
            year: metadata.year,
            rating: metadata.rating,
            ratingCount: metadata.ratingCount,
            summary: metadata.summary,
            genres: metadata.genres,
            countries: metadata.countries,
            posterUrl: metadata.posterURL,
            fetchedAt: ISO8601DateFormatter().string(from: Date(timeIntervalSince1970: metadata.fetchedAt))
        )
        return try await postEncoded(path: "/api/library/items/\(Self.escapePath(libraryItemId))/douban/import", body: request)
    }

    nonisolated func posterURL(assetId: String) -> String {
        absoluteString(path: "/api/assets/posters/\(Self.escapePath(assetId))")
    }

    func posterData(assetId: String) async throws -> Data {
        let request = try makeRequest(path: "/api/assets/posters/\(Self.escapePath(assetId))", method: "GET")
        let (data, response) = try await session.data(for: request)
        _ = try validate(response: response, data: data)
        return data
    }

    private func get<T: Decodable>(path: String) async throws -> T {
        let request = try makeRequest(path: path, method: "GET")
        let (data, response) = try await session.data(for: request)
        _ = try validate(response: response, data: data)
        return try JSONDecoder().decode(T.self, from: data)
    }

    private func post<T: Decodable>(path: String, json: [String: Any]?) async throws -> T {
        var request = try makeRequest(path: path, method: "POST")
        if let json {
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try JSONSerialization.data(withJSONObject: json)
        }
        let (data, response) = try await session.data(for: request)
        _ = try validate(response: response, data: data)
        return try JSONDecoder().decode(T.self, from: data)
    }

    private func postEmpty(path: String, json: [String: Any]?) async throws {
        var request = try makeRequest(path: path, method: "POST")
        if let json {
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try JSONSerialization.data(withJSONObject: json)
        }
        let (data, response) = try await session.data(for: request)
        _ = try validate(response: response, data: data)
    }

    private func postEncodedEmpty<T: Encodable>(path: String, body: T) async throws {
        var request = try makeRequest(path: path, method: "POST")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONEncoder().encode(body)
        let (data, response) = try await session.data(for: request)
        _ = try validate(response: response, data: data)
    }

    private func postEncoded<Response: Decodable, Body: Encodable>(path: String, body: Body) async throws -> Response {
        var request = try makeRequest(path: path, method: "POST")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONEncoder().encode(body)
        let (data, response) = try await session.data(for: request)
        _ = try validate(response: response, data: data)
        return try JSONDecoder().decode(Response.self, from: data)
    }

    private func makeRequest(path: String, method: String) throws -> URLRequest {
        guard let url = absoluteURL(path) else { throw OmniPlayDockerClientError.invalidBaseURL }
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if let sessionCookie, !sessionCookie.isEmpty {
            request.setValue("omniplay_session=\(sessionCookie)", forHTTPHeaderField: "Cookie")
        }
        return request
    }

    @discardableResult
    private func validate(response: URLResponse, data: Data, allowUnauthorized: Bool = false) throws -> HTTPURLResponse {
        guard let http = response as? HTTPURLResponse else {
            throw OmniPlayDockerClientError.invalidResponse
        }
        if (200...299).contains(http.statusCode) {
            return http
        }
        if http.statusCode == 401 && !allowUnauthorized {
            throw OmniPlayDockerClientError.authenticationRequired
        }
        throw OmniPlayDockerClientError.requestFailed(http.statusCode, Self.errorMessage(from: data))
    }

    nonisolated private func absoluteURL(_ value: String) -> URL? {
        if let url = URL(string: value), url.scheme != nil {
            return url
        }
        return absoluteURL(path: value)
    }

    nonisolated private func absoluteURL(path: String) -> URL? {
        guard var components = URLComponents(url: baseURL, resolvingAgainstBaseURL: false) else { return nil }
        let split = path.split(separator: "?", maxSplits: 1, omittingEmptySubsequences: false)
        let pathPart = split.first.map(String.init) ?? path
        let queryPart = split.count > 1 ? String(split[1]) : nil
        let basePath = components.path.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        let relative = pathPart.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        components.path = "/" + [basePath, relative].filter { !$0.isEmpty }.joined(separator: "/")
        components.percentEncodedQuery = queryPart
        return components.url
    }

    nonisolated private func absoluteString(path: String) -> String {
        absoluteURL(path: path)?.absoluteString ?? path
    }

    private static func sessionCookie(from response: HTTPURLResponse) -> String? {
        let setCookie = response.allHeaderFields.first { key, _ in
            String(describing: key).caseInsensitiveCompare("Set-Cookie") == .orderedSame
        }?.value as? String
        guard let setCookie else { return nil }
        for segment in setCookie.components(separatedBy: ";") {
            let trimmed = segment.trimmingCharacters(in: .whitespacesAndNewlines)
            guard trimmed.hasPrefix("omniplay_session=") else { continue }
            let token = String(trimmed.dropFirst("omniplay_session=".count))
            return token.isEmpty ? nil : token
        }
        return nil
    }

    nonisolated private static func escapePath(_ value: String) -> String {
        value.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? value
    }

    private static func errorMessage(from data: Data) -> String? {
        guard !data.isEmpty,
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return nil
        }
        return object["error"] as? String ?? object["message"] as? String
    }
}
