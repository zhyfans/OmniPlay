using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;

namespace OmniPlay.Infrastructure.Douban;

public sealed class DoubanMetadataClient : IDoubanMetadataClient
{
    private readonly HttpClient httpClient;
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private DateTimeOffset nextAllowedRequestAt = DateTimeOffset.MinValue;
    private DateTimeOffset cooldownUntil = DateTimeOffset.MinValue;

    public DoubanMetadataClient(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    public DoubanMetadata CreateSubjectPlaceholder(
        string libraryItemId,
        string subject,
        string fallbackTitle,
        string? fallbackYear = null)
    {
        var subjectId = ResolveSubjectId(subject);
        return new DoubanMetadata(
            libraryItemId.Trim(),
            subjectId,
            $"https://movie.douban.com/subject/{subjectId}/",
            string.IsNullOrWhiteSpace(fallbackTitle) ? $"豆瓣 {subjectId}" : fallbackTitle.Trim(),
            null,
            NormalizeYear(fallbackYear),
            null,
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow);
    }

    public async Task<DoubanMetadata> FetchSubjectAsync(
        string libraryItemId,
        string subject,
        string fallbackTitle,
        string? fallbackYear = null,
        CancellationToken cancellationToken = default)
    {
        var subjectId = ResolveSubjectId(subject);
        await AcquireRequestSlotAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://m.douban.com/movie/subject/{subjectId}/");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1");
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        request.Headers.AcceptLanguage.ParseAdd("zh-CN,zh-Hans;q=0.9,en;q=0.8");
        request.Headers.Referrer = new Uri("https://m.douban.com/movie/");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.RequestMessage?.RequestUri?.Host.Contains("sec.douban.com", StringComparison.OrdinalIgnoreCase) == true)
        {
            PauseForCooldown();
            throw new InvalidOperationException(BuildCooldownMessage("豆瓣返回了验证页面"));
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or (HttpStatusCode)429)
        {
            PauseForCooldown();
            throw new InvalidOperationException(BuildCooldownMessage($"豆瓣返回 HTTP {(int)response.StatusCode}"));
        }

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (html.Contains("sec.douban.com", StringComparison.OrdinalIgnoreCase)
            || html.Contains("captcha", StringComparison.OrdinalIgnoreCase)
            || html.Contains("检测到有异常请求", StringComparison.OrdinalIgnoreCase))
        {
            PauseForCooldown();
            throw new InvalidOperationException(BuildCooldownMessage("豆瓣返回了验证或异常请求页面"));
        }

        var jsonLd = ParseJsonLd(html);
        var title = CleanTitle(FirstNonEmpty(
            JsonString(jsonLd, "name"),
            MetaContent(html, "og:title"),
            Match(html, @"<h1[^>]*>(.*?)</h1>"),
            Match(html, @"<title[^>]*>(.*?)</title>")));
        if (string.IsNullOrWhiteSpace(title) || string.Equals(title, "豆瓣", StringComparison.Ordinal) || string.Equals(title, "豆瓣电影", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("未能从豆瓣页面解析到有效影视名称。");
        }

        var ratingObject = JsonObject(jsonLd, "aggregateRating");
        var rating = ParseDouble(FirstNonEmpty(
            JsonString(ratingObject, "ratingValue"),
            JsonString(ratingObject, "rating"),
            Match(html, @"class=[""'][^""']*rating_num[^""']*[""'][^>]*>\s*([0-9]+(?:\.[0-9]+)?)"),
            Match(html, @"""ratingValue""\s*:\s*""?([0-9]+(?:\.[0-9]+)?)")));
        var ratingCount = ParseInt(FirstNonEmpty(
            JsonString(ratingObject, "ratingCount"),
            Match(html, @"""ratingCount""\s*:\s*""?([0-9,]+)"),
            Match(html, @"([0-9,]+)\s*人评分")));
        var summary = CleanSummary(FirstNonEmpty(
            JsonString(jsonLd, "description"),
            MetaContent(html, "description"),
            MetaContent(html, "og:description"),
            Match(html, @"<section[^>]*class=[""'][^""']*subject-intro[^""']*[""'][^>]*>.*?<p[^>]*>(.*?)</p>")));

        return new DoubanMetadata(
            libraryItemId.Trim(),
            subjectId,
            $"https://movie.douban.com/subject/{subjectId}/",
            title.Trim(),
            JsonString(jsonLd, "alternateName"),
            FirstNonEmpty(ParseYear(html), NormalizeYear(fallbackYear)),
            rating,
            ratingCount,
            summary,
            JoinJsonStringArray(jsonLd, "genre"),
            ParseCountries(html),
            NormalizePosterUrl(FirstNonEmpty(
                JsonString(jsonLd, "image"),
                MetaContent(html, "og:image"),
                Match(html, @"https?://img[0-9]?\.doubanio\.com/view/photo/[^""'\s<>]+"))),
            DateTimeOffset.UtcNow);
    }

    private async Task AcquireRequestSlotAsync(CancellationToken cancellationToken)
    {
        await requestGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (now < cooldownUntil)
            {
                throw new InvalidOperationException($"豆瓣请求已暂停到 {cooldownUntil.ToLocalTime():yyyy-MM-dd HH:mm}。");
            }

            if (now < nextAllowedRequestAt)
            {
                await Task.Delay(nextAllowedRequestAt - now, cancellationToken);
            }

            var jitterSeconds = Random.Shared.Next(3, 9);
            nextAllowedRequestAt = DateTimeOffset.UtcNow.AddSeconds(12 + jitterSeconds);
        }
        finally
        {
            requestGate.Release();
        }
    }

    private void PauseForCooldown()
    {
        cooldownUntil = DateTimeOffset.UtcNow.AddHours(12);
        nextAllowedRequestAt = cooldownUntil;
    }

    private string BuildCooldownMessage(string reason)
    {
        return $"{reason}，已暂停豆瓣请求到 {cooldownUntil.ToLocalTime():yyyy-MM-dd HH:mm}。";
    }

    private static string ResolveSubjectId(string input)
    {
        var value = WebUtility.UrlDecode(input ?? string.Empty).Trim();
        var match = Regex.Match(value, @"(?:movie\.douban\.com/subject|m\.douban\.com/movie/subject)/(\d+)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        match = Regex.Match(value, @"^\d{3,}$");
        if (match.Success)
        {
            return value;
        }

        throw new ArgumentException("请填写有效的豆瓣影视链接或 subject ID。");
    }

    private static string? NormalizeYear(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = Regex.Match(value, @"\d{4}");
        return match.Success ? match.Value : null;
    }

    private static JsonElement? ParseJsonLd(string html)
    {
        foreach (Match match in Regex.Matches(html, @"<script[^>]+type=[""']application/ld\+json[""'][^>]*>(.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var json = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
            try
            {
                using var document = JsonDocument.Parse(json);
                return document.RootElement.Clone();
            }
            catch
            {
                // Try next JSON-LD block.
            }
        }

        return null;
    }

    private static JsonElement? JsonObject(JsonElement? element, string name)
    {
        if (element is { ValueKind: JsonValueKind.Object } objectElement
            && objectElement.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }

        return null;
    }

    private static string? JsonString(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } objectElement
            || !objectElement.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }

    private static string? JoinJsonStringArray(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } objectElement
            || !objectElement.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = value.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()?.Trim())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
        return values.Length == 0 ? null : string.Join(" / ", values);
    }

    private static string? MetaContent(string html, string property)
    {
        return FirstNonEmpty(
            Match(html, $@"<meta[^>]+(?:property|name|itemprop)=[""']{Regex.Escape(property)}[""'][^>]+content=[""']([^""']+)[""']"),
            Match(html, $@"<meta[^>]+content=[""']([^""']+)[""'][^>]+(?:property|name|itemprop)=[""']{Regex.Escape(property)}[""']"));
    }

    private static string? Match(string html, string pattern)
    {
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? WebUtility.HtmlDecode(StripTags(match.Groups[1].Value)).Trim() : null;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }

    private static double? ParseDouble(string? value)
    {
        return double.TryParse(value?.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    private static int? ParseInt(string? value)
    {
        return int.TryParse(value?.Replace(",", string.Empty).Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    private static string? ParseYear(string html)
    {
        return FirstNonEmpty(
            Match(html, @"""datePublished""\s*:\s*""(\d{4})"),
            Match(html, @"year[""']?\s*[:=]\s*[""']?(\d{4})"),
            Match(html, @"\((\d{4})\)"));
    }

    private static string? ParseCountries(string html)
    {
        return FirstNonEmpty(
            Match(html, @"制片国家/地区[：:]\s*</span>\s*([^<]+)"),
            Match(html, @"地区[：:]\s*([^<\n]+)"));
    }

    private static string CleanTitle(string? value)
    {
        var title = WebUtility.HtmlDecode(value ?? string.Empty).Trim();
        title = Regex.Replace(title, @"\s*\(豆瓣\)\s*$", string.Empty, RegexOptions.IgnoreCase);
        title = Regex.Replace(title, @"\s*-\s*豆瓣.*$", string.Empty, RegexOptions.IgnoreCase);
        return title.Trim();
    }

    private static string? CleanSummary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var summary = WebUtility.HtmlDecode(StripTags(value)).Trim();
        summary = Regex.Replace(summary, @"\s+", " ");
        return string.IsNullOrWhiteSpace(summary) ? null : summary;
    }

    private static string StripTags(string value)
    {
        return Regex.Replace(value, "<.*?>", string.Empty, RegexOptions.Singleline);
    }

    private static string? NormalizePosterUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = WebUtility.HtmlDecode(value.Trim());
        normalized = normalized.Replace(@"\u002F", "/", StringComparison.OrdinalIgnoreCase);
        if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            normalized = "https:" + normalized;
        }

        return normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : null;
    }
}
