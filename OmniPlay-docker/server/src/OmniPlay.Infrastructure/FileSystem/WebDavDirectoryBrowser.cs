using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;

namespace OmniPlay.Infrastructure.FileSystem;

public sealed class WebDavDirectoryBrowser : IWebDavDirectoryBrowser, IWebDavFileEnumerator
{
    private static readonly HttpMethod PropFind = new("PROPFIND");
    private readonly HttpClient httpClient;

    public WebDavDirectoryBrowser(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    public async Task<WebDavConnectionTestResult> TestConnectionAsync(
        string url,
        string? username,
        string? password,
        CancellationToken cancellationToken = default)
    {
        var uri = ResolveDirectoryUri(url);
        using var request = CreatePropFindRequest(uri, username, password, depth: "0");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var statusCode = (int)response.StatusCode;
        var isReachable = IsWebDavSuccess(response.StatusCode);
        var message = isReachable
            ? "WebDAV 连接可用。"
            : response.StatusCode == HttpStatusCode.Unauthorized
                ? "WebDAV 认证失败。"
                : $"WebDAV 返回 HTTP {statusCode}。";

        return new WebDavConnectionTestResult(isReachable, NormalizeUrl(uri), statusCode, message);
    }

    public async Task<WebDavDirectoryBrowseResult> BrowseAsync(
        string url,
        string? username,
        string? password,
        CancellationToken cancellationToken = default)
    {
        var uri = ResolveDirectoryUri(url);
        using var request = CreatePropFindRequest(uri, username, password, depth: "1");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new UnauthorizedAccessException("WebDAV 认证失败。");
        }

        if (!IsWebDavSuccess(response.StatusCode))
        {
            throw new InvalidOperationException($"WebDAV 目录浏览失败：HTTP {(int)response.StatusCode}。");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var currentUrl = NormalizeUrl(uri);
        var entries = ParseResources(content, uri)
            .Where(entry => entry.IsCollection && !string.Equals(entry.Url, currentUrl, StringComparison.OrdinalIgnoreCase))
            .Select(static entry => new WebDavDirectoryEntry(
                entry.Name,
                entry.Url,
                IsReadable: true,
                entry.LastModified))
            .OrderBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new WebDavDirectoryBrowseResult(
            NormalizeUrl(uri),
            ResolveParentUrl(uri),
            entries);
    }

    public async Task<IReadOnlyList<WebDavFileEntry>> EnumerateFilesAsync(
        string rootUrl,
        string? username,
        string? password,
        CancellationToken cancellationToken = default)
    {
        var rootUri = ResolveDirectoryUri(rootUrl);
        var rootPrefix = NormalizeUrl(rootUri) + "/";
        Queue<Uri> pending = new();
        HashSet<string> visitedDirectories = new(StringComparer.OrdinalIgnoreCase);
        List<WebDavFileEntry> files = [];
        pending.Enqueue(rootUri);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Dequeue();
            var currentUrl = NormalizeUrl(current);
            if (!visitedDirectories.Add(currentUrl))
            {
                continue;
            }

            using var request = CreatePropFindRequest(current, username, password, depth: "1");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new UnauthorizedAccessException("WebDAV 认证失败。");
            }

            if (!IsWebDavSuccess(response.StatusCode))
            {
                throw new InvalidOperationException($"WebDAV 文件枚举失败：HTTP {(int)response.StatusCode}。");
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            foreach (var resource in ParseResources(content, current))
            {
                if (string.Equals(resource.Url, currentUrl, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!resource.Url.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (resource.IsCollection)
                {
                    pending.Enqueue(new Uri(resource.Url + "/", UriKind.Absolute));
                    continue;
                }

                var relativePath = Uri.UnescapeDataString(resource.Url[rootPrefix.Length..]).Trim('/');
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    continue;
                }

                files.Add(new WebDavFileEntry(
                    resource.Name,
                    resource.Url,
                    relativePath,
                    resource.ContentLength,
                    resource.LastModified));
            }
        }

        return files
            .OrderBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static HttpRequestMessage CreatePropFindRequest(
        Uri uri,
        string? username,
        string? password,
        string depth)
    {
        var request = new HttpRequestMessage(PropFind, uri);
        request.Headers.Add("Depth", depth);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        if (!string.IsNullOrWhiteSpace(username) || !string.IsNullOrEmpty(password))
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{username?.Trim() ?? string.Empty}:{password ?? string.Empty}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        request.Content = new StringContent(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <D:propfind xmlns:D="DAV:">
              <D:prop>
                <D:displayname/>
                <D:resourcetype/>
                <D:getcontentlength/>
                <D:getlastmodified/>
              </D:prop>
            </D:propfind>
            """,
            Encoding.UTF8,
            "application/xml");
        return request;
    }

    private static IReadOnlyList<WebDavResourceEntry> ParseResources(string content, Uri requestedUri)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var document = XDocument.Parse(content);
        var currentUrl = NormalizeUrl(requestedUri);
        List<WebDavResourceEntry> entries = [];
        foreach (var response in document.Descendants().Where(static element => element.Name.LocalName == "response"))
        {
            var href = response.Elements().FirstOrDefault(static element => element.Name.LocalName == "href")?.Value;
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            var entryUri = ResolveHref(requestedUri, href);
            var entryUrl = NormalizeUrl(entryUri);
            if (string.Equals(entryUrl, currentUrl, StringComparison.OrdinalIgnoreCase))
            {
                entries.Add(new WebDavResourceEntry(
                    GuessName(entryUri),
                    entryUrl,
                    IsCollection: true,
                    ContentLength: null,
                    LastModified: null));
                continue;
            }

            var prop = response.Descendants().FirstOrDefault(static element => element.Name.LocalName == "prop");
            var isCollection = prop?.Descendants().Any(static element => element.Name.LocalName == "collection") == true;
            var displayName = prop?.Elements().FirstOrDefault(static element => element.Name.LocalName == "displayname")?.Value;
            entries.Add(new WebDavResourceEntry(
                string.IsNullOrWhiteSpace(displayName) ? GuessName(entryUri) : WebUtility.HtmlDecode(displayName.Trim()),
                entryUrl,
                isCollection,
                ParseContentLength(prop),
                ParseLastModified(prop)));
        }

        return entries;
    }

    private static Uri ResolveHref(Uri requestedUri, string href)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out var absoluteUri)
            && (absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps))
        {
            return absoluteUri;
        }

        return new Uri(requestedUri, href);
    }

    private static string GuessName(Uri uri)
    {
        var path = uri.AbsolutePath.TrimEnd('/');
        var lastSlash = path.LastIndexOf('/');
        var name = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
        return string.IsNullOrWhiteSpace(name) ? uri.Host : Uri.UnescapeDataString(name);
    }

    private static long? ParseContentLength(XElement? prop)
    {
        var value = prop?.Elements().FirstOrDefault(static element => element.Name.LocalName == "getcontentlength")?.Value;
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length)
            ? length
            : null;
    }

    private static DateTimeOffset? ParseLastModified(XElement? prop)
    {
        var value = prop?.Elements().FirstOrDefault(static element => element.Name.LocalName == "getlastmodified")?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value.Trim(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static Uri ResolveDirectoryUri(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("WebDAV 地址不能为空。", nameof(url));
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("WebDAV 地址格式不正确。", nameof(url));
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("WebDAV 地址只支持 http 或 https。", nameof(url));
        }

        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty,
            Query = string.Empty,
            UserName = string.Empty,
            Password = string.Empty
        };
        if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }

    private static string NormalizeUrl(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty,
            Query = string.Empty,
            UserName = string.Empty,
            Password = string.Empty
        };

        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    private static string? ResolveParentUrl(Uri uri)
    {
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .ToArray();
        if (segments.Length == 0)
        {
            return null;
        }

        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty,
            Query = string.Empty,
            UserName = string.Empty,
            Password = string.Empty,
            Path = segments.Length == 1
                ? "/"
                : "/" + string.Join('/', segments.Take(segments.Length - 1)) + "/"
        };

        return NormalizeUrl(builder.Uri);
    }

    private static bool IsWebDavSuccess(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.MultiStatus or HttpStatusCode.OK;
    }

    private sealed record WebDavResourceEntry(
        string Name,
        string Url,
        bool IsCollection,
        long? ContentLength,
        DateTimeOffset? LastModified);
}
