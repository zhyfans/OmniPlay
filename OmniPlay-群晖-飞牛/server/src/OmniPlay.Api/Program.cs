using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;
using OmniPlay.Core.Runtime;
using OmniPlay.Infrastructure.Data;
using OmniPlay.Infrastructure.FileSystem;
using OmniPlay.Infrastructure.Library;
using OmniPlay.Infrastructure.Network;
using OmniPlay.Infrastructure.SystemChecks;
using OmniPlay.Infrastructure.Tmdb;
using OmniPlay.Media;

const string SessionCookieName = "omniplay_session";

Console.WriteLine("[OmniPlay] Bootstrapping...");
var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
Console.WriteLine("[OmniPlay] Builder created.");

var listenUri = ResolveListenUri(
    Environment.GetEnvironmentVariable(AppRuntime.ListenUrlsEnvironmentVariable)
    ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS"));

builder.WebHost.UseKestrelCore();
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(ToListenAddress(listenUri.Host), listenUri.Port);
});
builder.Services.AddRoutingCore();
builder.Services.AddSingleton<IStoragePaths, StoragePaths>();
builder.Services.AddSingleton<SqliteDatabase>();
builder.Services.AddSingleton<IMediaSourceRepository, MediaSourceRepository>();
builder.Services.AddSingleton<ILocalDirectoryBrowser, LocalDirectoryBrowser>();
builder.Services.AddSingleton<IWebDavDirectoryBrowser, WebDavDirectoryBrowser>();
builder.Services.AddSingleton<IWebDavFileEnumerator, WebDavDirectoryBrowser>();
builder.Services.AddSingleton<IPlayableFileResolver, PlayableFileResolver>();
builder.Services.AddSingleton<IPlaybackCacheService, WebDavPlaybackCacheService>();
builder.Services.AddSingleton<IPlaybackSubtitleService, PlaybackSubtitleService>();
builder.Services.AddSingleton<IWebDavRangeStreamService, WebDavRangeStreamService>();
builder.Services.AddSingleton<ILibraryRepository, LibraryRepository>();
builder.Services.AddSingleton<ILibraryScanner, LibraryScanner>();
builder.Services.AddSingleton<IAppSettingsRepository, AppSettingsRepository>();
builder.Services.AddSingleton<IUserRepository, UserRepository>();
builder.Services.AddSingleton<IPosterAssetRepository, PosterAssetRepository>();
builder.Services.AddSingleton<IThumbnailAssetRepository, ThumbnailAssetRepository>();
builder.Services.AddSingleton<IAssetCacheCleanupService, AssetCacheCleanupService>();
builder.Services.AddSingleton<IMediaSourceCleanupService, MediaSourceCleanupService>();
builder.Services.AddSingleton<ICacheUsageService, CacheUsageService>();
builder.Services.AddSingleton<IWebDavCacheCleanupService, WebDavCacheCleanupService>();
builder.Services.AddSingleton<IBackgroundTaskCenter, InMemoryBackgroundTaskCenter>();
builder.Services.AddSingleton<ILibraryMetadataEnricher, LibraryMetadataEnricher>();
builder.Services.AddSingleton<IScanStatusStore, InMemoryScanStatusStore>();
builder.Services.AddSingleton<ILibraryScanJobService, LibraryScanJobService>();
builder.Services.AddSingleton<IMetadataEnrichmentStatusStore, InMemoryMetadataEnrichmentStatusStore>();
builder.Services.AddSingleton<ILibraryMetadataEnrichmentJobService, LibraryMetadataEnrichmentJobService>();
builder.Services.AddSingleton<IMediaProbeService, FfprobeMediaProbeService>();
builder.Services.AddSingleton<IHlsSessionService, FfmpegHlsSessionService>();
builder.Services.AddSingleton<HttpClient>(serviceProvider =>
{
    var settingsRepository = serviceProvider.GetRequiredService<IAppSettingsRepository>();
    var httpClient = new HttpClient(new SettingsProxyHttpMessageHandler(settingsRepository), disposeHandler: true)
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
    httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("OmniPlay.NAS/0.1");
    return httpClient;
});
builder.Services.AddSingleton<ITmdbMetadataClient, TmdbMetadataClient>();
builder.Services.AddSingleton<IProxyConnectionTester, ProxyConnectionTester>();
builder.Services.AddSingleton<IRuntimeSelfCheckService>(serviceProvider => new RuntimeSelfCheckService(
    serviceProvider.GetRequiredService<IStoragePaths>(),
    serviceProvider.GetRequiredService<SqliteDatabase>(),
    serviceProvider.GetRequiredService<IHlsSessionService>(),
    serviceProvider.GetRequiredService<HttpClient>(),
    listenUri));

Console.WriteLine("[OmniPlay] Building app...");
var app = builder.Build();
Console.WriteLine("[OmniPlay] App built.");

Console.WriteLine("[OmniPlay] Preparing runtime directories...");
var storagePaths = app.Services.GetRequiredService<IStoragePaths>();
storagePaths.EnsureCreated();

Console.WriteLine("[OmniPlay] Initializing database...");
var database = app.Services.GetRequiredService<SqliteDatabase>();
database.EnsureInitialized();
Console.WriteLine("[OmniPlay] Database ready.");

var webRoot = ResolveWebRoot();
if (Directory.Exists(webRoot))
{
    var webFileProvider = new PhysicalFileProvider(webRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webFileProvider });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = webFileProvider });
    Console.WriteLine($"[OmniPlay] Serving Web UI from {webRoot}");
}

app.Use(async (context, next) =>
{
    if (!RequiresAuthenticatedApi(context.Request.Path))
    {
        await next();
        return;
    }

    var users = context.RequestServices.GetRequiredService<IUserRepository>();
    if (!context.Request.Cookies.TryGetValue(SessionCookieName, out var token) ||
        await users.GetUserBySessionTokenAsync(token, context.RequestAborted) is not { } user)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "请先登录 OmniPlay。" }, context.RequestAborted);
        return;
    }

    context.Items["auth.user"] = user;
    await next();
});

app.MapGet("/api/auth/status", async (
    HttpContext context,
    IUserRepository users,
    CancellationToken cancellationToken) =>
{
    var hasUsers = await users.HasUsersAsync(cancellationToken);
    AuthUser? user = null;
    if (context.Request.Cookies.TryGetValue(SessionCookieName, out var token))
    {
        user = await users.GetUserBySessionTokenAsync(token, cancellationToken);
    }

    return Results.Ok(new AuthStatus(!hasUsers, user is not null, user?.Username, user?.Role));
});

app.MapPost("/api/auth/register", async (
    HttpContext context,
    HttpRequest request,
    IUserRepository users,
    CancellationToken cancellationToken) =>
{
    var authRequest = await ReadJsonBodyAsync<AuthRequest>(request, cancellationToken);
    if (authRequest is null)
    {
        return Results.BadRequest(new { error = "请提供用户名和密码。" });
    }

    try
    {
        var session = await users.RegisterFirstAdminAsync(authRequest, cancellationToken);
        SetSessionCookie(context, session);
        return Results.Ok(new AuthStatus(false, true, session.User.Username, session.User.Role));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
});

app.MapPost("/api/auth/login", async (
    HttpContext context,
    HttpRequest request,
    IUserRepository users,
    CancellationToken cancellationToken) =>
{
    var authRequest = await ReadJsonBodyAsync<AuthRequest>(request, cancellationToken);
    if (authRequest is null)
    {
        return Results.BadRequest(new { error = "请提供用户名和密码。" });
    }

    var session = await users.LoginAsync(authRequest, cancellationToken);
    if (session is null)
    {
        return Results.Json(new { error = "用户名或密码错误。" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    SetSessionCookie(context, session);
    return Results.Ok(new AuthStatus(false, true, session.User.Username, session.User.Role));
});

app.MapPost("/api/auth/logout", async (
    HttpContext context,
    IUserRepository users,
    CancellationToken cancellationToken) =>
{
    if (context.Request.Cookies.TryGetValue(SessionCookieName, out var token))
    {
        await users.RevokeSessionAsync(token, cancellationToken);
    }

    ClearSessionCookie(context);
    return Results.NoContent();
});

app.MapGet("/api/health", (
    IHostEnvironment environment,
    IStoragePaths paths,
    SqliteDatabase db) =>
{
    var dbStatus = db.GetStatus();
    return Results.Ok(new HealthStatus(
        AppRuntime.ServiceName,
        typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
        environment.EnvironmentName,
        paths.RootDirectory,
        dbStatus.Path,
        dbStatus.Exists,
        DateTimeOffset.UtcNow));
});

app.MapGet("/api/runtime/self-check", async (
    IRuntimeSelfCheckService selfCheckService,
    CancellationToken cancellationToken) =>
{
    return Results.Ok(await selfCheckService.CheckAsync(cancellationToken));
});

app.MapGet("/api/settings", async (
    IAppSettingsRepository repository,
    CancellationToken cancellationToken) =>
{
    return Results.Ok(await repository.GetAsync(cancellationToken));
});

app.MapPut("/api/settings", async (
    HttpRequest httpRequest,
    IAppSettingsRepository repository,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<AppSettingsUpdateRequest>(httpRequest, cancellationToken);
    if (request is null)
    {
        return Results.BadRequest(new { error = "请提供设置内容。" });
    }

    return Results.Ok(await repository.UpdateAsync(request, cancellationToken));
});

app.MapPost("/api/settings/tmdb/test", async (
    HttpRequest httpRequest,
    IAppSettingsRepository settingsRepository,
    ITmdbMetadataClient tmdbClient,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<TmdbSettings>(httpRequest, cancellationToken);
    var settings = request ?? (await settingsRepository.GetAsync(cancellationToken)).Tmdb;
    return Results.Ok(await tmdbClient.TestConnectionAsync(settings, cancellationToken));
});

app.MapPost("/api/settings/proxy/test", async (
    HttpRequest httpRequest,
    IAppSettingsRepository settingsRepository,
    IProxyConnectionTester proxyTester,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<ProxySettings>(httpRequest, cancellationToken);
    var settings = request ?? (await settingsRepository.GetAsync(cancellationToken)).Proxy;
    return Results.Ok(await proxyTester.TestAsync(settings, cancellationToken));
});

app.MapGet("/api/sources", async (
    IMediaSourceRepository repository,
    CancellationToken cancellationToken) =>
{
    var sources = await repository.GetAllAsync(cancellationToken);
    return Results.Ok(sources);
});

app.MapGet("/api/sources/local/directories", async (
    string? path,
    ILocalDirectoryBrowser browser,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await browser.BrowseAsync(path, cancellationToken));
    }
    catch (DirectoryNotFoundException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (UnauthorizedAccessException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/sources/local", async (
    HttpRequest httpRequest,
    IMediaSourceRepository repository,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<AddLocalMediaSourceRequest>(httpRequest, cancellationToken);
    if (request is null || string.IsNullOrWhiteSpace(request.Path))
    {
        return Results.BadRequest(new { error = "请提供本地媒体目录 path。" });
    }

    try
    {
        var source = await repository.AddLocalAsync(request.Name ?? string.Empty, request.Path, cancellationToken);
        return Results.Ok(source);
    }
    catch (DirectoryNotFoundException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/sources/webdav", async (
    HttpRequest httpRequest,
    IMediaSourceRepository repository,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<AddWebDavMediaSourceRequest>(httpRequest, cancellationToken);
    if (request is null || string.IsNullOrWhiteSpace(request.Url))
    {
        return Results.BadRequest(new { error = "请提供 WebDAV 地址 url。" });
    }

    try
    {
        var source = await repository.AddWebDavAsync(
            request.Name ?? string.Empty,
            request.Url,
            request.Username,
            request.Password,
            cancellationToken);
        return Results.Ok(source);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/sources/webdav/test", async (
    HttpRequest httpRequest,
    IWebDavDirectoryBrowser browser,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<WebDavConnectionRequest>(httpRequest, cancellationToken);
    if (request is null || string.IsNullOrWhiteSpace(request.Url))
    {
        return Results.BadRequest(new { error = "请提供 WebDAV 地址 url。" });
    }

    try
    {
        return Results.Ok(await browser.TestConnectionAsync(
            request.Url,
            request.Username,
            request.Password,
            cancellationToken));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (HttpRequestException ex)
    {
        return Results.BadRequest(new { error = $"WebDAV 连接失败：{ex.Message}" });
    }
});

app.MapPost("/api/sources/webdav/browse", async (
    HttpRequest httpRequest,
    IWebDavDirectoryBrowser browser,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<WebDavConnectionRequest>(httpRequest, cancellationToken);
    if (request is null || string.IsNullOrWhiteSpace(request.Url))
    {
        return Results.BadRequest(new { error = "请提供 WebDAV 地址 url。" });
    }

    try
    {
        return Results.Ok(await browser.BrowseAsync(
            request.Url,
            request.Username,
            request.Password,
            cancellationToken));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (UnauthorizedAccessException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (HttpRequestException ex)
    {
        return Results.BadRequest(new { error = $"WebDAV 连接失败：{ex.Message}" });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/sources/{sourceId:long}/scan", async (
    long sourceId,
    HttpRequest httpRequest,
    ILibraryScanJobService scanJobService,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<LibraryRefreshRequest>(httpRequest, cancellationToken)
                  ?? new LibraryRefreshRequest();
    return scanJobService.TryStartSourceScan(sourceId, request, out var status)
        ? Results.Accepted("/api/library/scan/status", status)
        : Results.Conflict(new { error = "扫描已经在运行。", status });
});

app.MapPatch("/api/sources/{sourceId:long}", async (
    long sourceId,
    HttpRequest httpRequest,
    IMediaSourceRepository repository,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<UpdateMediaSourceRequest>(httpRequest, cancellationToken);
    if (request is null)
    {
        return Results.BadRequest(new { error = "请提供媒体源更新内容。" });
    }

    try
    {
        var source = await repository.UpdateAsync(sourceId, request, cancellationToken);
        return source is null ? Results.NotFound() : Results.Ok(source);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapDelete("/api/sources/{sourceId:long}", async (
    long sourceId,
    IMediaSourceRepository repository,
    IMediaSourceCleanupService cleanupService,
    IAssetCacheCleanupService assetCacheCleanupService,
    IAppSettingsRepository settingsRepository,
    IBackgroundTaskCenter taskCenter,
    CancellationToken cancellationToken) =>
{
    var removed = await repository.RemoveAsync(sourceId, cancellationToken);
    if (!removed)
    {
        return Results.NotFound();
    }

    return taskCenter.TryStartExclusive(
        "media-source-cleanup",
        $"清理媒体源 {sourceId}",
        async (_, progress, taskCancellationToken) =>
        {
            progress.Report(new BackgroundTaskProgress("cleanup-source", "正在清理媒体源索引", 5, sourceId.ToString()));
            var summary = await cleanupService.CleanupRemovedSourceAsync(
                sourceId,
                ScaleProgress(progress, 5, 65),
                taskCancellationToken);
            var settings = await settingsRepository.GetAsync(taskCancellationToken);
            var assetOptions = new AssetCacheCleanupOptions(
                IncludeUntrackedFiles: !string.Equals(
                    settings.Cache.ImageCleanupScope,
                    "orphans-only",
                    StringComparison.OrdinalIgnoreCase));
            var assetSummary = await assetCacheCleanupService.CleanupOrphansAsync(
                assetOptions,
                ScaleProgress(progress, 70, 28),
                taskCancellationToken);
            progress.Report(new BackgroundTaskProgress("completed", "媒体源和图片缓存清理完成", 100, sourceId.ToString()));
            return $"清理 {summary.RemovedVideoFileCount} 个视频文件，{summary.RemovedLibraryItemCount} 个影视条目，释放 {assetSummary.RemovedBytes} 字节图片缓存";
        },
        onAccepted: null,
        out var status)
        ? Results.Accepted("/api/tasks", status)
        : Results.Ok(CompletedMediaSourceRemovedStatus(sourceId, "媒体源已移除；当前有后台任务，索引清理稍后可再次触发。"));
});

app.MapGet("/api/library/items", async (
    ILibraryRepository repository,
    CancellationToken cancellationToken) =>
{
    var items = await repository.GetItemsAsync(cancellationToken);
    return Results.Ok(items);
});

app.MapGet("/api/library/items/{id}", async (
    string id,
    ILibraryRepository repository,
    CancellationToken cancellationToken) =>
{
    var detail = await repository.GetItemDetailAsync(id, cancellationToken);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
});

app.MapGet("/api/tasks", (IBackgroundTaskCenter taskCenter) =>
{
    return Results.Ok(taskCenter.GetSnapshot());
});

app.MapGet("/api/tasks/events", async (
    HttpContext context,
    IBackgroundTaskCenter taskCenter) =>
{
    await WriteTaskEventsAsync(context, taskCenter);
});

app.MapPost("/api/tasks/{taskId}/cancel", (
    string taskId,
    IBackgroundTaskCenter taskCenter) =>
{
    return taskCenter.TryCancel(taskId, out var status)
        ? Results.Ok(status)
        : Results.Conflict(new { error = "任务不存在或未运行。", status });
});

app.MapGet("/api/library/scan/status", (IScanStatusStore scanStatusStore) =>
{
    return Results.Ok(scanStatusStore.Get());
});

app.MapGet("/api/library/scan/events", async (
    HttpContext context,
    IScanStatusStore scanStatusStore) =>
{
    await WriteScanStatusEventsAsync(context, scanStatusStore);
});

app.MapPost("/api/library/scan", async (
    HttpRequest httpRequest,
    ILibraryScanJobService scanJobService,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<LibraryRefreshRequest>(httpRequest, cancellationToken)
                  ?? new LibraryRefreshRequest();
    return scanJobService.TryStartScan(request, out var status)
        ? Results.Accepted("/api/library/scan/status", status)
        : Results.Conflict(new { error = "扫描已经在运行。", status });
});

app.MapPost("/api/library/scan/cancel", (ILibraryScanJobService scanJobService) =>
{
    if (!scanJobService.RequestCancel(out var status))
    {
        return Results.Conflict(new { error = "当前没有正在运行的扫描。", status });
    }

    return Results.Ok(status);
});

app.MapGet("/api/library/scrape/status", (IMetadataEnrichmentStatusStore statusStore) =>
{
    return Results.Ok(statusStore.Get());
});

app.MapGet("/api/library/scrape/events", async (
    HttpContext context,
    IMetadataEnrichmentStatusStore statusStore) =>
{
    await WriteMetadataEnrichmentStatusEventsAsync(context, statusStore);
});

app.MapPost("/api/library/scrape", (ILibraryMetadataEnrichmentJobService enrichmentJobService) =>
{
    return enrichmentJobService.TryStartMissing(out var status)
        ? Results.Accepted("/api/library/scrape/status", status)
        : Results.Conflict(new { error = "刮削已经在运行。", status });
});

app.MapPost("/api/library/scrape/cancel", (ILibraryMetadataEnrichmentJobService enrichmentJobService) =>
{
    if (!enrichmentJobService.RequestCancel(out var status))
    {
        return Results.Conflict(new { error = "当前没有正在运行的刮削。", status });
    }

    return Results.Ok(status);
});

app.MapPost("/api/library/items/{id}/rescrape", (
    string id,
    ILibraryMetadataEnrichmentJobService enrichmentJobService) =>
{
    return enrichmentJobService.TryStartItem(id, out var status)
        ? Results.Accepted("/api/library/scrape/status", status)
        : Results.Conflict(new { error = "刮削已经在运行。", status });
});

app.MapGet("/api/library/items/{id}/metadata/search", async (
    string id,
    string? query,
    string? mediaType,
    string? year,
    ILibraryRepository repository,
    IAppSettingsRepository settingsRepository,
    ITmdbMetadataClient tmdbClient,
    CancellationToken cancellationToken) =>
{
    var item = await repository.GetItemDetailAsync(id, cancellationToken);
    if (item is null)
    {
        return Results.NotFound();
    }

    var sourceMetadata = ResolveSourceSearchMetadata(item.VideoFiles);
    var searchQuery = ResolveMetadataSearchQuery(item, sourceMetadata, query);
    var secondaryQuery = BuildMetadataSecondarySearchQuery(
        searchQuery,
        sourceMetadata?.ForeignTitle,
        sourceMetadata?.ChineseTitle,
        sourceMetadata?.ParentChineseTitle,
        sourceMetadata?.FullCleanTitle,
        item.Title);
    var searchTypes = ResolveMetadataSearchTypes(mediaType, item.ItemKind);
    var searchYear = string.IsNullOrWhiteSpace(year)
        ? NormalizeMetadataSearchYear(item.ReleaseDate) ?? NormalizeMetadataSearchYear(sourceMetadata?.Year)
        : year.Trim();
    var settings = (await settingsRepository.GetAsync(cancellationToken)).Tmdb;
    List<TmdbMetadataMatch> candidates = [];
    HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
    foreach (var searchType in searchTypes)
    {
        var matches = await tmdbClient.SearchCandidatesAsync(
            searchType,
            searchQuery,
            searchYear,
            settings,
            secondaryQuery,
            limit: 8,
            cancellationToken: cancellationToken);
        foreach (var match in matches)
        {
            var key = $"{match.MediaType}#{match.Id}";
            if (seen.Add(key))
            {
                candidates.Add(match);
            }
        }
    }

    return Results.Ok(candidates.Take(8).ToArray());
});

app.MapPost("/api/library/items/{id}/metadata/apply", async (
    string id,
    HttpRequest httpRequest,
    ILibraryRepository repository,
    IAppSettingsRepository settingsRepository,
    ITmdbMetadataClient tmdbClient,
    CancellationToken cancellationToken) =>
{
    var match = await ReadJsonBodyAsync<TmdbMetadataMatch>(httpRequest, cancellationToken);
    if (match is null || match.Id <= 0 || string.IsNullOrWhiteSpace(match.Title))
    {
        return Results.BadRequest(new { error = "请提供有效的 TMDB 匹配项。" });
    }

    var settings = (await settingsRepository.GetAsync(cancellationToken)).Tmdb;
    var selectedMatch = await tmdbClient.GetDetailsAsync(
        match.MediaType,
        match.Id,
        settings,
        cancellationToken) ?? match;
    string? posterLocalPath = null;
    if (settings.EnablePosterDownloads && !string.IsNullOrWhiteSpace(selectedMatch.PosterPath))
    {
        posterLocalPath = await tmdbClient.DownloadPosterAsync(
            selectedMatch.PosterPath,
            selectedMatch.MediaType,
            selectedMatch.Id,
            cancellationToken);
    }

    var updated = await repository.ApplyMetadataMatchAsync(
        new LibraryItemMetadataApplyRequest(
            id,
            selectedMatch.Id,
            selectedMatch.MediaType,
            selectedMatch.Title,
            selectedMatch.Overview,
            selectedMatch.ReleaseDate,
            selectedMatch.PosterPath,
            selectedMatch.VoteAverage,
            posterLocalPath,
            LockMetadata: true),
        cancellationToken);
    if (!updated)
    {
        return Results.NotFound();
    }

    var detail = await repository.GetItemDetailAsync(id, cancellationToken);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
});

app.MapPatch("/api/library/items/{id}/metadata/custom", async (
    string id,
    HttpRequest httpRequest,
    ILibraryRepository repository,
    IStoragePaths storagePaths,
    CancellationToken cancellationToken) =>
{
    LibraryItemCustomMetadataUpdateRequest? request;
    try
    {
        request = await ReadCustomMetadataRequestAsync(id, httpRequest, storagePaths, cancellationToken);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    if (request is null || string.IsNullOrWhiteSpace(request.Title))
    {
        return Results.BadRequest(new { error = "请填写影视名称。" });
    }

    if (request.VoteAverage is < 0 or > 10)
    {
        return Results.BadRequest(new { error = "评分需要在 0 到 10 之间。" });
    }

    var updated = await repository.UpdateCustomMetadataAsync(request with { LibraryItemId = id }, cancellationToken);
    if (!updated)
    {
        return Results.NotFound();
    }

    var detail = await repository.GetItemDetailAsync(id, cancellationToken);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
});

app.MapPost("/api/library/items/{id}/metadata/lock", async (
    string id,
    HttpRequest httpRequest,
    ILibraryRepository repository,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<LibraryItemLockUpdateRequest>(httpRequest, cancellationToken);
    if (request is null)
    {
        return Results.BadRequest(new { error = "请提供锁定状态。" });
    }

    var updated = await repository.SetLibraryItemLockedAsync(
        new LibraryItemLockUpdateRequest(id, request.IsLocked),
        cancellationToken);
    if (!updated)
    {
        return Results.NotFound();
    }

    var detail = await repository.GetItemDetailAsync(id, cancellationToken);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
});

app.MapPost("/api/playback/progress", async (
    HttpRequest httpRequest,
    ILibraryRepository repository,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<PlaybackProgressUpdateRequest>(httpRequest, cancellationToken);
    if (request is null || string.IsNullOrWhiteSpace(request.VideoFileId))
    {
        return Results.BadRequest(new { error = "请提供 videoFileId。" });
    }

    var updated = await repository.UpdatePlaybackProgressAsync(request, cancellationToken);
    return updated ? Results.NoContent() : Results.NotFound();
});

app.MapPost("/api/playback/watched", async (
    HttpRequest httpRequest,
    ILibraryRepository repository,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<WatchedStatusUpdateRequest>(httpRequest, cancellationToken);
    if (request is null || string.IsNullOrWhiteSpace(request.VideoFileId))
    {
        return Results.BadRequest(new { error = "请提供 videoFileId。" });
    }

    var updated = await repository.SetWatchedAsync(request, cancellationToken);
    return updated ? Results.NoContent() : Results.NotFound();
});

app.MapPost("/api/library/items/{id}/watched", async (
    string id,
    HttpRequest httpRequest,
    ILibraryRepository repository,
    CancellationToken cancellationToken) =>
{
    var request = await ReadJsonBodyAsync<LibraryItemWatchedStatusUpdateRequest>(httpRequest, cancellationToken);
    if (request is null)
    {
        return Results.BadRequest(new { error = "请提供已看状态。" });
    }

    var updated = await repository.SetLibraryItemWatchedAsync(
        new LibraryItemWatchedStatusUpdateRequest(id, request.IsWatched, request.UserId),
        cancellationToken);
    if (!updated)
    {
        return Results.NotFound();
    }

    var detail = await repository.GetItemDetailAsync(id, cancellationToken);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
});

app.MapGet("/api/playback/capabilities", async (
    IHlsSessionService hlsSessionService,
    CancellationToken cancellationToken) =>
{
    return Results.Ok(await hlsSessionService.GetCapabilitiesAsync(cancellationToken));
});

app.MapGet("/api/playback/decision/{fileId}", async (
    string fileId,
    string? quality,
    int? audioTrackIndex,
    string? subtitleMode,
    string? subtitleId,
    bool? hardware,
    ILibraryRepository repository,
    IPlayableFileResolver playableFileResolver,
    IPlaybackSubtitleService playbackSubtitleService,
    IWebDavRangeStreamService webDavRangeStreamService,
    IPlaybackCacheService cacheService,
    IAppSettingsRepository settingsRepository,
    IHlsSessionService hlsSessionService,
    IMediaProbeService mediaProbeService,
    CancellationToken cancellationToken) =>
{
    var playbackSettings = (await settingsRepository.GetAsync(cancellationToken)).Playback;
    var remoteFile = await webDavRangeStreamService.GetFileInfoAsync(fileId, cancellationToken);
    if (remoteFile is not null)
    {
        var remoteProbe = CreateProbeFromStoredMetadata(remoteFile);
        var remotePlan = ResolvePlaybackPlan(remoteFile.FileName, remoteProbe);
        var remoteEmbeddedSubtitleStreamIndex = ResolveEmbeddedSubtitleStreamIndex(subtitleId);
        var remoteSubtitlePath = remoteEmbeddedSubtitleStreamIndex.HasValue
            ? null
            : await playbackSubtitleService.ResolveSubtitlePathAsync(remoteFile.Id, subtitleId, cancellationToken);
        var remoteRequestedSubtitleMode = NormalizeSubtitleMode(subtitleMode, remoteSubtitlePath, remoteEmbeddedSubtitleStreamIndex);
        var remoteProfile = await ResolveRequestedProfileAsync(
            remotePlan.Profile,
            quality,
            audioTrackIndex,
            remoteRequestedSubtitleMode,
            remoteSubtitlePath,
            remoteEmbeddedSubtitleStreamIndex,
            remoteProbe?.VideoCodec,
            hardware,
            hlsSessionService,
            cancellationToken);
        var remoteMode = ResolveRequestedMode(remotePlan.Mode, remoteProfile, quality, audioTrackIndex, remoteRequestedSubtitleMode);
        var remotePolicy = await ApplyPlaybackSettingsAsync(
            remoteFile.Id,
            remotePlan,
            remoteProfile,
            remoteMode,
            playbackSettings,
            quality,
            audioTrackIndex,
            remoteRequestedSubtitleMode,
            remoteSubtitlePath,
            remoteEmbeddedSubtitleStreamIndex,
            remoteProbe?.VideoCodec,
            hardware,
            hlsSessionService,
            cancellationToken);
        if (remotePolicy.Decision is not null)
        {
            return Results.Ok(remotePolicy.Decision);
        }

        if (remotePolicy.Mode == "direct")
        {
            return Results.Ok(new PlaybackDecision(
                remoteFile.Id,
                "direct",
                $"/api/playback/files/{Uri.EscapeDataString(remoteFile.Id)}/stream",
                null,
                null,
                true,
                $"{remotePolicy.Reason} WebDAV 使用 Range 分段代理。"));
        }

        var cachedPath = await cacheService.EnsureCachedAsync(remoteFile.Id, cancellationToken);
        if (string.IsNullOrWhiteSpace(cachedPath))
        {
            return Results.Ok(new PlaybackDecision(
                remoteFile.Id,
                "unavailable",
                null,
                null,
                null,
                false,
                "WebDAV 文件需要完整缓存后才能进入 HLS 播放，请等待缓存完成后重试。"));
        }

        var remoteCachedFile = remoteFile with { AbsolutePath = cachedPath };
        var remoteSession = await hlsSessionService.EnsureSessionAsync(remoteCachedFile, remotePolicy.Profile, cancellationToken);
        if (!remoteSession.IsReady && !string.IsNullOrWhiteSpace(remoteSession.ErrorMessage))
        {
            return Results.Ok(new PlaybackDecision(
                remoteFile.Id,
                "unavailable",
                null,
                null,
                remoteSession.SessionId,
                false,
                remoteSession.ErrorMessage));
        }

        return Results.Ok(new PlaybackDecision(
            remoteFile.Id,
            remotePolicy.Mode,
            null,
            $"/api/playback/hls/{Uri.EscapeDataString(remoteSession.SessionId)}/index.m3u8",
            remoteSession.SessionId,
            remoteSession.IsReady,
            remoteSession.IsReady ? ResolveProfileReason(remotePolicy.Reason, remotePolicy.Profile) : "HLS 正在准备。"));
    }

    var file = await playableFileResolver.ResolveAsync(fileId, cancellationToken);
    if (file is null)
    {
        return Results.NotFound();
    }

    var probe = await mediaProbeService.ProbeAsync(file.AbsolutePath, cancellationToken);
    if (probe is not null)
    {
        await repository.UpdateVideoFileProbeAsync(
            new VideoFileProbeUpdate(
                file.Id,
                probe.DurationSeconds,
                probe.Container,
                probe.VideoCodec,
                probe.AudioCodec,
                probe.SubtitleSummary,
                probe.RawJson),
            cancellationToken);
    }
    else
    {
        probe = CreateProbeFromStoredMetadata(file);
    }

    var plan = ResolvePlaybackPlan(file.FileName, probe);
    var embeddedSubtitleStreamIndex = ResolveEmbeddedSubtitleStreamIndex(subtitleId);
    var subtitlePath = embeddedSubtitleStreamIndex.HasValue
        ? null
        : await playbackSubtitleService.ResolveSubtitlePathAsync(file.Id, subtitleId, cancellationToken);
    var requestedSubtitleMode = NormalizeSubtitleMode(subtitleMode, subtitlePath, embeddedSubtitleStreamIndex);
    var profile = await ResolveRequestedProfileAsync(
        plan.Profile,
        quality,
        audioTrackIndex,
        requestedSubtitleMode,
        subtitlePath,
        embeddedSubtitleStreamIndex,
        probe?.VideoCodec,
        hardware,
        hlsSessionService,
        cancellationToken);
    var mode = ResolveRequestedMode(plan.Mode, profile, quality, audioTrackIndex, requestedSubtitleMode);
    var policy = await ApplyPlaybackSettingsAsync(
        file.Id,
        plan,
        profile,
        mode,
        playbackSettings,
        quality,
        audioTrackIndex,
        requestedSubtitleMode,
        subtitlePath,
        embeddedSubtitleStreamIndex,
        probe?.VideoCodec,
        hardware,
        hlsSessionService,
        cancellationToken);
    if (policy.Decision is not null)
    {
        return Results.Ok(policy.Decision);
    }

    mode = policy.Mode;
    profile = policy.Profile;
    if (mode == "direct")
    {
        return Results.Ok(new PlaybackDecision(
            file.Id,
            "direct",
            $"/api/playback/files/{Uri.EscapeDataString(file.Id)}/stream",
            null,
            null,
            true,
            policy.Reason));
    }

    var session = await hlsSessionService.EnsureSessionAsync(file, profile, cancellationToken);
    if (!session.IsReady && !string.IsNullOrWhiteSpace(session.ErrorMessage))
    {
        return Results.Ok(new PlaybackDecision(
            file.Id,
            "unavailable",
            null,
            null,
            session.SessionId,
            false,
            session.ErrorMessage));
    }

    return Results.Ok(new PlaybackDecision(
        file.Id,
        mode,
        null,
        $"/api/playback/hls/{Uri.EscapeDataString(session.SessionId)}/index.m3u8",
        session.SessionId,
        session.IsReady,
        session.IsReady ? ResolveProfileReason(policy.Reason, profile) : "HLS 正在准备。"));
});

app.MapGet("/api/playback/diagnostics/{fileId}", async (
    string fileId,
    string? quality,
    int? audioTrackIndex,
    string? subtitleMode,
    string? subtitleId,
    bool? hardware,
    IPlayableFileResolver playableFileResolver,
    IPlaybackSubtitleService playbackSubtitleService,
    IWebDavRangeStreamService webDavRangeStreamService,
    IPlaybackCacheService cacheService,
    IAppSettingsRepository settingsRepository,
    IHlsSessionService hlsSessionService,
    IMediaProbeService mediaProbeService,
    CancellationToken cancellationToken) =>
{
    var diagnostics = await BuildPlaybackDiagnosticsAsync(
        fileId,
        quality,
        audioTrackIndex,
        subtitleMode,
        subtitleId,
        hardware,
        playableFileResolver,
        playbackSubtitleService,
        webDavRangeStreamService,
        cacheService,
        settingsRepository,
        hlsSessionService,
        mediaProbeService,
        cancellationToken);
    return diagnostics is null ? Results.NotFound() : Results.Ok(diagnostics);
});

app.MapGet("/api/playback/files/{fileId}/subtitles", async (
    string fileId,
    IPlaybackSubtitleService playbackSubtitleService,
    CancellationToken cancellationToken) =>
{
    var subtitles = await playbackSubtitleService.DiscoverAsync(fileId, cancellationToken);
    if (subtitles is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(subtitles);
});

app.MapGet("/api/playback/files/{fileId}/cache", async (
    string fileId,
    IPlaybackCacheService cacheService,
    CancellationToken cancellationToken) =>
{
    var status = await cacheService.GetStatusAsync(fileId, cancellationToken);
    return status is null ? Results.NotFound() : Results.Ok(status);
});

app.MapPost("/api/playback/files/{fileId}/cache/prepare", async (
    string fileId,
    IPlaybackCacheService cacheService,
    CancellationToken cancellationToken) =>
{
    var status = await cacheService.StartAsync(fileId, cancellationToken);
    return status is null ? Results.NotFound() : Results.Accepted($"/api/playback/files/{Uri.EscapeDataString(fileId)}/cache", status);
});

app.MapPost("/api/playback/files/{fileId}/cache/cancel", async (
    string fileId,
    IPlaybackCacheService cacheService,
    CancellationToken cancellationToken) =>
{
    var status = await cacheService.CancelAsync(fileId, cancellationToken);
    return status is null ? Results.NotFound() : Results.Ok(status);
});

app.MapGet("/api/playback/files/{fileId}/stream", async (
    string fileId,
    HttpContext httpContext,
    IWebDavRangeStreamService webDavRangeStreamService,
    IPlayableFileResolver playableFileResolver,
    CancellationToken cancellationToken) =>
{
    var remoteStream = await webDavRangeStreamService.OpenReadAsync(
        fileId,
        httpContext.Request.Headers.Range.ToString(),
        cancellationToken);
    if (remoteStream is not null)
    {
        await WriteWebDavRangeStreamAsync(httpContext, remoteStream, cancellationToken);
        return;
    }

    var file = await playableFileResolver.ResolveAsync(fileId, cancellationToken);
    if (file is null)
    {
        httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await Results.File(
            file.AbsolutePath,
            ResolveContentType(file.AbsolutePath),
            enableRangeProcessing: true)
        .ExecuteAsync(httpContext);
});

app.MapGet("/api/playback/hls/{sessionId}/{assetName}", (
    string sessionId,
    string assetName,
    IHlsSessionService hlsSessionService) =>
{
    var asset = hlsSessionService.GetAsset(sessionId, assetName);
    return asset is null
        ? Results.NotFound()
        : Results.File(asset.FullPath, asset.ContentType, enableRangeProcessing: asset.EnableRangeProcessing);
});

app.MapPost("/api/playback/hls/{sessionId}/stop", (
    string sessionId,
    IHlsSessionService hlsSessionService) =>
{
    return hlsSessionService.StopSession(sessionId) ? Results.NoContent() : Results.NotFound();
});

app.MapPost("/api/playback/hls/cleanup", (
    int? maxAgeHours,
    IHlsSessionService hlsSessionService,
    IAppSettingsRepository settingsRepository,
    IBackgroundTaskCenter taskCenter) =>
{
    return taskCenter.TryStartExclusive(
        "hls-cache-cleanup",
        "清理转码缓存",
        async (_, progress, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var settings = await settingsRepository.GetAsync(cancellationToken);
            var retentionHours = Math.Clamp(maxAgeHours ?? settings.Cache.HlsRetentionHours, 1, 24 * 30);
            var maxAge = TimeSpan.FromHours(retentionHours);
            progress.Report(new BackgroundTaskProgress("cleanup", "正在清理转码缓存", null, null));
            var summary = hlsSessionService.CleanupCache(maxAge);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            return $"清理 {summary.RemovedSessionCount} 个会话，释放 {summary.RemovedBytes} 字节";
        },
        onAccepted: null,
        out var status)
        ? Results.Accepted("/api/tasks", status)
        : Results.Conflict(new { error = "已有后台任务正在运行，请稍后再试。", status });
});

app.MapPost("/api/playback/webdav/cache/cleanup", (
    int? maxAgeHours,
    IWebDavCacheCleanupService cleanupService,
    IAppSettingsRepository settingsRepository,
    IBackgroundTaskCenter taskCenter) =>
{
    return taskCenter.TryStartExclusive(
        "webdav-cache-cleanup",
        "清理 WebDAV 缓存",
        async (_, progress, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var settings = await settingsRepository.GetAsync(cancellationToken);
            var retentionHours = Math.Clamp(maxAgeHours ?? settings.Cache.WebDavRetentionHours, 1, 24 * 30);
            var maxAge = TimeSpan.FromHours(retentionHours);
            var maxBytes = settings.Cache.WebDavMaxGb * 1024L * 1024L * 1024L;
            progress.Report(new BackgroundTaskProgress("cleanup", "正在清理 WebDAV 缓存", null, null));
            var summary = await cleanupService.CleanupAsync(maxAge, progress, cancellationToken, maxBytes);
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new BackgroundTaskProgress("completed", "WebDAV 缓存清理完成", 100, null));
            return $"清理 {summary.RemovedFileCount} 个文件，释放 {summary.RemovedBytes} 字节";
        },
        onAccepted: null,
        out var status)
        ? Results.Accepted("/api/tasks", status)
        : Results.Conflict(new { error = "已有后台任务正在运行，请稍后再试。", status });
});

app.MapPost("/api/assets/cache/cleanup", (
    IAssetCacheCleanupService cleanupService,
    IAppSettingsRepository settingsRepository,
    IBackgroundTaskCenter taskCenter) =>
{
    return taskCenter.TryStartExclusive(
        "asset-cache-cleanup",
        "清理图片缓存",
        async (_, progress, cancellationToken) =>
        {
            var settings = await settingsRepository.GetAsync(cancellationToken);
            var options = new AssetCacheCleanupOptions(
                IncludeUntrackedFiles: !string.Equals(
                    settings.Cache.ImageCleanupScope,
                    "orphans-only",
                    StringComparison.OrdinalIgnoreCase));
            var summary = await cleanupService.CleanupOrphansAsync(options, progress, cancellationToken);
            return $"清理 {summary.RemovedFileCount} 个文件，{summary.RemovedAssetRecordCount} 条记录，释放 {summary.RemovedBytes} 字节";
        },
        onAccepted: null,
        out var status)
        ? Results.Accepted("/api/tasks", status)
        : Results.Conflict(new { error = "已有后台任务正在运行，请稍后再试。", status });
});

app.MapGet("/api/cache/status", (ICacheUsageService cacheUsageService) =>
{
    return Results.Ok(cacheUsageService.GetUsage());
});

app.MapGet("/api/playback/files/{fileId}/subtitles/{subtitleId}.vtt", async (
    string fileId,
    string subtitleId,
    IPlaybackSubtitleService playbackSubtitleService,
    CancellationToken cancellationToken) =>
{
    var subtitlePath = await playbackSubtitleService.ResolveSubtitlePathAsync(fileId, subtitleId, cancellationToken);
    if (subtitlePath is null)
    {
        return Results.NotFound();
    }

    var webVtt = await ReadSubtitleAsWebVttAsync(subtitlePath, cancellationToken);
    return webVtt is null
        ? Results.NotFound()
        : Results.Text(webVtt, "text/vtt; charset=utf-8");
});

app.MapGet("/api/assets/posters/{id}", async (
    string id,
    IPosterAssetRepository repository,
    CancellationToken cancellationToken) =>
{
    var asset = await repository.GetAsync(id, cancellationToken);
    if (asset is null || !System.IO.File.Exists(asset.LocalPath))
    {
        return Results.NotFound();
    }

    return Results.File(
        asset.LocalPath,
        ResolveContentType(asset.LocalPath),
        enableRangeProcessing: true);
});

app.MapGet("/api/assets/thumbnails/{id}", async (
    string id,
    IThumbnailAssetRepository repository,
    CancellationToken cancellationToken) =>
{
    var asset = await repository.GetAsync(id, cancellationToken);
    if (asset is null || !System.IO.File.Exists(asset.LocalPath))
    {
        return Results.NotFound();
    }

    return Results.File(
        asset.LocalPath,
        ResolveContentType(asset.LocalPath),
        enableRangeProcessing: true);
});

if (Directory.Exists(webRoot))
{
    app.MapFallback((HttpContext context) =>
    {
        return context.Request.Path.StartsWithSegments("/api")
            ? Results.NotFound()
            : Results.File(Path.Combine(webRoot, "index.html"), "text/html; charset=utf-8");
    });
}
else
{
    app.MapGet("/", () => Results.Redirect("/api/health"));
}

Console.WriteLine("[OmniPlay] Starting HTTP server...");
Console.WriteLine($"[OmniPlay] Listening on http://{listenUri.Host}:{listenUri.Port}");
app.Run();

static bool RequiresAuthenticatedApi(PathString path)
{
    if (!path.StartsWithSegments("/api"))
    {
        return false;
    }

    return !path.StartsWithSegments("/api/auth");
}

static void SetSessionCookie(HttpContext context, AuthSession session)
{
    context.Response.Cookies.Append(
        SessionCookieName,
        session.Token,
        new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            Expires = session.ExpiresAt,
            Path = "/"
        });
}

static void ClearSessionCookie(HttpContext context)
{
    context.Response.Cookies.Delete(
        SessionCookieName,
        new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            Path = "/"
        });
}

static Uri ResolveListenUri(string? configuredUrls)
{
    var firstUrl = configuredUrls?
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault();

    if (Uri.TryCreate(firstUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp)
    {
        return uri;
    }

    return new Uri("http://0.0.0.0:8096");
}

static string ResolveWebRoot()
{
    var configuredRoot = Environment.GetEnvironmentVariable("OMNIPLAY_WEB_ROOT");
    if (!string.IsNullOrWhiteSpace(configuredRoot))
    {
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredRoot));
    }

    return Path.Combine(AppContext.BaseDirectory, "wwwroot");
}

static IPAddress ToListenAddress(string host)
{
    if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
    {
        return IPAddress.Loopback;
    }

    if (string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "*", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "+", StringComparison.OrdinalIgnoreCase))
    {
        return IPAddress.Any;
    }

    return IPAddress.TryParse(host, out var address) ? address : IPAddress.Any;
}

static async Task<T?> ReadJsonBodyAsync<T>(HttpRequest request, CancellationToken cancellationToken)
{
    if (request.ContentLength == 0)
    {
        return default;
    }

    return await JsonSerializer.DeserializeAsync<T>(
        request.Body,
        new JsonSerializerOptions(JsonSerializerDefaults.Web),
        cancellationToken);
}

static async Task<LibraryItemCustomMetadataUpdateRequest?> ReadCustomMetadataRequestAsync(
    string libraryItemId,
    HttpRequest request,
    IStoragePaths storagePaths,
    CancellationToken cancellationToken)
{
    if (!request.HasFormContentType)
    {
        var json = await ReadJsonBodyAsync<LibraryItemCustomMetadataUpdateRequest>(request, cancellationToken);
        return json is null ? null : json with { LibraryItemId = libraryItemId };
    }

    var form = await request.ReadFormAsync(cancellationToken);
    var posterFile = form.Files.GetFile("poster");
    var posterLocalPath = posterFile is { Length: > 0 }
        ? await SaveCustomPosterAsync(libraryItemId, posterFile, storagePaths, cancellationToken)
        : null;

    return new LibraryItemCustomMetadataUpdateRequest(
        libraryItemId,
        ReadFormString(form, "title") ?? string.Empty,
        ReadFormString(form, "releaseDate"),
        ReadFormString(form, "overview"),
        ReadFormDouble(form, "voteAverage"),
        posterLocalPath,
        posterFile is { Length: > 0 } ? posterFile.FileName : null,
        LockMetadata: true);
}

static async Task<string> SaveCustomPosterAsync(
    string libraryItemId,
    IFormFile posterFile,
    IStoragePaths storagePaths,
    CancellationToken cancellationToken)
{
    const long maxPosterBytes = 20 * 1024 * 1024;
    if (posterFile.Length > maxPosterBytes)
    {
        throw new InvalidOperationException("海报图片不能超过 20 MB。");
    }

    storagePaths.EnsureCreated();
    var extension = NormalizePosterExtension(Path.GetExtension(posterFile.FileName));
    var hashInput = $"{libraryItemId}:{posterFile.FileName}:{Guid.NewGuid():N}";
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput)))[..16].ToLowerInvariant();
    var destination = Path.Combine(storagePaths.PostersDirectory, $"custom-{hash}{extension}");
    await using var output = System.IO.File.Create(destination);
    await posterFile.CopyToAsync(output, cancellationToken);
    return destination;
}

static string NormalizePosterExtension(string? extension)
{
    return extension?.ToLowerInvariant() switch
    {
        ".png" => ".png",
        ".webp" => ".webp",
        ".jpg" or ".jpeg" => ".jpg",
        _ => ".jpg"
    };
}

static string? ReadFormString(IFormCollection form, string key)
{
    if (!form.TryGetValue(key, out var values))
    {
        return null;
    }

    var value = values.ToString().Trim();
    return string.IsNullOrWhiteSpace(value) ? null : value;
}

static double? ReadFormDouble(IFormCollection form, string key)
{
    var value = ReadFormString(form, key);
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    if (double.TryParse(
            value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var invariantValue))
    {
        return invariantValue;
    }

    return double.TryParse(value, out var localValue) ? localValue : null;
}

static async Task WriteWebDavRangeStreamAsync(
    HttpContext httpContext,
    WebDavRangeStreamResult result,
    CancellationToken cancellationToken)
{
    await using (result)
    {
        httpContext.Response.StatusCode = result.StatusCode;
        httpContext.Response.ContentType = result.ContentType;
        httpContext.Response.Headers["Accept-Ranges"] = "bytes";
        if (result.ContentRange is not null)
        {
            httpContext.Response.Headers["Content-Range"] = result.ContentRange;
        }

        if (result.ContentLength.HasValue)
        {
            httpContext.Response.ContentLength = result.ContentLength.Value;
        }

        if (result.Content is null)
        {
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                await httpContext.Response.WriteAsync(result.ErrorMessage, cancellationToken);
            }

            return;
        }

        await result.Content.CopyToAsync(httpContext.Response.Body, cancellationToken);
    }
}

static string ResolveContentType(string path)
{
    return Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        ".ts" or ".m2ts" or ".m2t" => "video/mp2t",
        ".avi" => "video/x-msvideo",
        _ => "application/octet-stream"
    };
}

static async Task WriteScanStatusEventsAsync(HttpContext context, IScanStatusStore scanStatusStore)
{
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream; charset=utf-8";

    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var lastPayload = string.Empty;
    var cancellationToken = context.RequestAborted;
    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var status = scanStatusStore.Get();
            var payload = JsonSerializer.Serialize(status, jsonOptions);
            if (!string.Equals(payload, lastPayload, StringComparison.Ordinal))
            {
                await context.Response.WriteAsync("event: status\n", cancellationToken);
                await context.Response.WriteAsync($"data: {payload}\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
                lastPayload = payload;
            }

            await Task.Delay(status.IsRunning ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(3), cancellationToken);
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        // Client closed the EventSource connection.
    }
}

static async Task WriteTaskEventsAsync(HttpContext context, IBackgroundTaskCenter taskCenter)
{
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream; charset=utf-8";

    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var lastPayload = string.Empty;
    var cancellationToken = context.RequestAborted;
    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var snapshot = taskCenter.GetSnapshot();
            var payload = JsonSerializer.Serialize(snapshot, jsonOptions);
            if (!string.Equals(payload, lastPayload, StringComparison.Ordinal))
            {
                await context.Response.WriteAsync("event: status\n", cancellationToken);
                await context.Response.WriteAsync($"data: {payload}\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
                lastPayload = payload;
            }

            await Task.Delay(snapshot.ActiveTask is not null ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(3), cancellationToken);
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        // Client closed the EventSource connection.
    }
}

static async Task WriteMetadataEnrichmentStatusEventsAsync(
    HttpContext context,
    IMetadataEnrichmentStatusStore statusStore)
{
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream; charset=utf-8";

    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var lastPayload = string.Empty;
    var cancellationToken = context.RequestAborted;
    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var status = statusStore.Get();
            var payload = JsonSerializer.Serialize(status, jsonOptions);
            if (!string.Equals(payload, lastPayload, StringComparison.Ordinal))
            {
                await context.Response.WriteAsync("event: status\n", cancellationToken);
                await context.Response.WriteAsync($"data: {payload}\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
                lastPayload = payload;
            }

            await Task.Delay(status.IsRunning ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(3), cancellationToken);
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        // Client closed the EventSource connection.
    }
}

static BackgroundTaskStatus CompletedMediaSourceRemovedStatus(long sourceId, string message)
{
    var now = DateTimeOffset.UtcNow;
    return new BackgroundTaskStatus(
        $"media-source-remove_{sourceId}_{now:yyyyMMddHHmmssfff}",
        "media-source-remove",
        $"移除媒体源 {sourceId}",
        "completed",
        IsRunning: false,
        IsCancellationRequested: false,
        CanCancel: false,
        CreatedAt: now,
        StartedAt: now,
        CompletedAt: now,
        Phase: "completed",
        ProgressText: message,
        ProgressPercent: 100,
        CurrentItem: sourceId.ToString(),
        ResultText: message,
        ErrorMessage: null);
}

static async Task<PlaybackDiagnostics?> BuildPlaybackDiagnosticsAsync(
    string fileId,
    string? quality,
    int? audioTrackIndex,
    string? subtitleMode,
    string? subtitleId,
    bool? hardware,
    IPlayableFileResolver playableFileResolver,
    IPlaybackSubtitleService playbackSubtitleService,
    IWebDavRangeStreamService webDavRangeStreamService,
    IPlaybackCacheService cacheService,
    IAppSettingsRepository settingsRepository,
    IHlsSessionService hlsSessionService,
    IMediaProbeService mediaProbeService,
    CancellationToken cancellationToken)
{
    var settings = await settingsRepository.GetAsync(cancellationToken);
    var cacheStatus = await cacheService.GetStatusAsync(fileId, cancellationToken);
    var remoteFile = await webDavRangeStreamService.GetFileInfoAsync(fileId, cancellationToken);
    var isRemote = remoteFile is not null;
    var file = remoteFile;
    MediaProbeSnapshot? probe = null;
    if (file is null)
    {
        file = await playableFileResolver.ResolveAsync(fileId, cancellationToken);
        if (file is null)
        {
            return null;
        }

        probe = await mediaProbeService.ProbeAsync(file.AbsolutePath, cancellationToken);
    }

    probe ??= CreateProbeFromStoredMetadata(file);
    var plan = ResolvePlaybackPlan(file.FileName, probe);
    var embeddedSubtitleStreamIndex = ResolveEmbeddedSubtitleStreamIndex(subtitleId);
    var subtitlePath = embeddedSubtitleStreamIndex.HasValue
        ? null
        : await playbackSubtitleService.ResolveSubtitlePathAsync(file.Id, subtitleId, cancellationToken);
    var requestedSubtitleMode = NormalizeSubtitleMode(subtitleMode, subtitlePath, embeddedSubtitleStreamIndex);
    var profile = await ResolveRequestedProfileAsync(
        plan.Profile,
        quality,
        audioTrackIndex,
        requestedSubtitleMode,
        subtitlePath,
        embeddedSubtitleStreamIndex,
        probe?.VideoCodec,
        hardware,
        hlsSessionService,
        cancellationToken);
    var requestedMode = ResolveRequestedMode(plan.Mode, profile, quality, audioTrackIndex, requestedSubtitleMode);
    var policy = await ApplyPlaybackSettingsAsync(
        file.Id,
        plan,
        profile,
        requestedMode,
        settings.Playback,
        quality,
        audioTrackIndex,
        requestedSubtitleMode,
        subtitlePath,
        embeddedSubtitleStreamIndex,
        probe?.VideoCodec,
        hardware,
        hlsSessionService,
        cancellationToken);

    var effectiveMode = policy.Decision?.Mode ?? policy.Mode;
    profile = policy.Profile;
    var usesDirect = string.Equals(effectiveMode, "direct", StringComparison.OrdinalIgnoreCase);
    var usesHls = effectiveMode.StartsWith("hls", StringComparison.OrdinalIgnoreCase);
    var usesTranscode = string.Equals(effectiveMode, "hls-transcode", StringComparison.OrdinalIgnoreCase)
                        || profile.TranscodeVideo;
    var burnsSubtitle = string.Equals(requestedSubtitleMode, "burn", StringComparison.OrdinalIgnoreCase)
                        && (subtitlePath is not null || embeddedSubtitleStreamIndex.HasValue);
    var usesRangeProxy = isRemote && usesDirect;
    var requiresFullCache = isRemote && !usesRangeProxy;
    var capabilities = usesHls || hardware == true
        ? await hlsSessionService.GetCapabilitiesAsync(cancellationToken)
        : null;
    var previewFile = requiresFullCache
        ? file with { AbsolutePath = "<webdav-cache-file>" }
        : file;
    var ffmpegCommandPreview = usesHls && policy.Decision is null
        ? hlsSessionService.PreviewCommand(previewFile, profile)
        : null;
    var reason = policy.Decision?.Reason
                 ?? (usesRangeProxy ? $"{policy.Reason} WebDAV 使用 Range 分段代理。" : policy.Reason);

    return new PlaybackDiagnostics(
        file.Id,
        file.FileName,
        isRemote,
        isRemote ? "webdav" : "local",
        NormalizeQuality(quality),
        audioTrackIndex,
        requestedSubtitleMode,
        subtitleId,
        hardware == true,
        plan.Mode,
        effectiveMode,
        reason,
        requiresFullCache,
        usesRangeProxy,
        usesDirect,
        usesHls,
        usesTranscode,
        burnsSubtitle,
        usesDirect ? $"/api/playback/files/{Uri.EscapeDataString(file.Id)}/stream" : null,
        null,
        usesHls ? profile : null,
        ffmpegCommandPreview,
        cacheStatus,
        capabilities,
        BuildDiagnosticSteps(
            file,
            isRemote,
            probe,
            plan,
            requestedMode,
            policy,
            requestedSubtitleMode,
            subtitlePath,
            embeddedSubtitleStreamIndex,
            cacheStatus,
            usesRangeProxy,
            requiresFullCache,
            usesHls,
            usesTranscode,
            burnsSubtitle,
            ffmpegCommandPreview,
            capabilities));
}

static IReadOnlyList<PlaybackDiagnosticStep> BuildDiagnosticSteps(
    PlayableVideoFile file,
    bool isRemote,
    MediaProbeSnapshot? probe,
    PlaybackPlan plan,
    string requestedMode,
    PlaybackPolicyResult policy,
    string requestedSubtitleMode,
    string? subtitlePath,
    int? embeddedSubtitleStreamIndex,
    PlaybackCacheStatus? cacheStatus,
    bool usesRangeProxy,
    bool requiresFullCache,
    bool usesHls,
    bool usesTranscode,
    bool burnsSubtitle,
    string? ffmpegCommandPreview,
    FfmpegTranscodeCapabilities? capabilities)
{
    List<PlaybackDiagnosticStep> steps = [];
    steps.Add(new PlaybackDiagnosticStep(
        "source",
        "媒体源",
        "ok",
        isRemote ? "WebDAV 媒体源。" : "NAS 本地媒体源。"));
    steps.Add(new PlaybackDiagnosticStep(
        "probe",
        "媒体探测",
        probe is null ? "warn" : "ok",
        probe is null
            ? "没有可用探测信息，将按扩展名选择保守播放策略。"
            : $"容器 {probe.Container ?? "未知"}，视频 {probe.VideoCodec ?? "未知"}，音频 {probe.AudioCodec ?? "未知"}。"));
    steps.Add(new PlaybackDiagnosticStep(
        "base-plan",
        "基础策略",
        "ok",
        $"{plan.Mode}：{plan.Reason}"));
    steps.Add(new PlaybackDiagnosticStep(
        "request",
        "请求参数",
        "ok",
        $"档位 {policy.Profile.QualityId}，音轨 {(policy.Profile.AudioTrackIndex?.ToString() ?? "默认")}，字幕 {requestedSubtitleMode}。"));
    if (isRemote)
    {
        steps.Add(new PlaybackDiagnosticStep(
            "webdav-cache",
            "WebDAV 缓存",
            requiresFullCache ? "warn" : "ok",
            usesRangeProxy
                ? "可使用 WebDAV Range 分段代理，不需要完整缓存。"
                : cacheStatus is null
                    ? "需要完整缓存后进入 HLS/烧录链路。"
                    : $"需要完整缓存，当前状态 {cacheStatus.State}，{cacheStatus.Percent?.ToString("0.#") ?? "未知"}%。"));
    }

    steps.Add(new PlaybackDiagnosticStep(
        "policy",
        "策略裁决",
        policy.Decision?.Mode == "unavailable" ? "error" : "ok",
        policy.Decision?.Reason ?? $"{policy.Mode}：{policy.Reason}"));
    if (burnsSubtitle)
    {
        steps.Add(new PlaybackDiagnosticStep(
            "subtitle-burn",
            "字幕烧录",
            "warn",
            subtitlePath is not null
                ? $"外挂字幕烧录：{Path.GetFileName(subtitlePath)}。"
                : $"内嵌字幕烧录：stream {embeddedSubtitleStreamIndex}."));
    }

    if (usesHls)
    {
        steps.Add(new PlaybackDiagnosticStep(
            "ffmpeg",
            usesTranscode ? "FFmpeg 转码" : "FFmpeg 转封装",
            capabilities?.IsAvailable == false ? "error" : "ok",
            ffmpegCommandPreview is null
                ? capabilities?.ErrorMessage ?? "FFmpeg 命令将在启动 HLS 会话时生成。"
                : ffmpegCommandPreview));
    }

    if (usesTranscode && string.IsNullOrWhiteSpace(policy.Profile.HardwareEncoder))
    {
        steps.Add(new PlaybackDiagnosticStep(
            "hardware-transcode",
            "硬件转码",
            "error",
            "当前请求需要视频转码，但没有检测到可用硬件编码器。已禁止软件转码。"));
    }
    else if (usesTranscode && !HasRequiredHardwareDecoder(policy.Profile, probe?.VideoCodec))
    {
        steps.Add(new PlaybackDiagnosticStep(
            "hardware-decode",
            "硬件解码",
            "error",
            "当前请求需要视频转码，但没有检测到源视频对应的硬件解码路径。已禁止软解。"));
    }

    if (!usesHls && usesRangeProxy)
    {
        steps.Add(new PlaybackDiagnosticStep(
            "range-proxy",
            "Range 代理",
            "ok",
            "浏览器请求 Range 时，服务端会按固定块读取 WebDAV 并写入 Range 缓存。"));
    }

    if (!usesHls && !usesRangeProxy)
    {
        steps.Add(new PlaybackDiagnosticStep(
            "direct",
            "Range 直出",
            "ok",
            $"直接读取 {file.FileName}。"));
    }

    return steps;
}

static PlaybackPlan ResolvePlaybackPlan(string fileName, MediaProbeSnapshot? probe)
{
    var extension = Path.GetExtension(fileName).ToLowerInvariant();
    if (probe is null)
    {
        return extension is ".mp4" or ".m4v" or ".mov" or ".webm"
            ? new PlaybackPlan("direct", HlsPlaybackProfile.Remux, "媒体探测不可用，兼容容器先使用 Range 直出。")
            : new PlaybackPlan("hls-remux", HlsPlaybackProfile.Remux, "媒体探测不可用，先使用 FFmpeg HLS 转封装。");
    }

    var videoCodec = NormalizeCodec(probe.VideoCodec);
    var audioCodec = NormalizeCodec(probe.AudioCodec);

    if (extension is ".webm"
        && videoCodec is "vp8" or "vp9" or "av1"
        && IsWebAudioCompatible(audioCodec))
    {
        return new PlaybackPlan("direct", HlsPlaybackProfile.Remux, "WebM 编码兼容，使用 Range 直出。");
    }

    if (extension is ".mp4" or ".m4v" or ".mov"
        && IsH264(videoCodec)
        && IsMp4AudioCompatible(audioCodec))
    {
        return new PlaybackPlan("direct", HlsPlaybackProfile.Remux, "MP4/MOV 编码兼容，使用 Range 直出。");
    }

    if (IsH264(videoCodec) || string.IsNullOrWhiteSpace(videoCodec))
    {
        return new PlaybackPlan("hls-remux", HlsPlaybackProfile.Remux, "视频编码兼容，使用 FFmpeg HLS 转封装并转 AAC 音频。");
    }

    return new PlaybackPlan("hls-transcode", HlsPlaybackProfile.Transcode, $"当前 HLS 播放需要将视频编码 {videoCodec} 转为硬件输出流/AAC。");
}

static MediaProbeSnapshot? CreateProbeFromStoredMetadata(PlayableVideoFile file)
{
    if (string.IsNullOrWhiteSpace(file.Container)
        && string.IsNullOrWhiteSpace(file.VideoCodec)
        && string.IsNullOrWhiteSpace(file.AudioCodec)
        && string.IsNullOrWhiteSpace(file.SubtitleSummary)
        && file.DurationSeconds <= 0)
    {
        return null;
    }

    return new MediaProbeSnapshot(
        file.AbsolutePath,
        file.DurationSeconds,
        file.Container,
        file.VideoCodec,
        file.AudioCodec,
        file.SubtitleSummary,
        null,
        []);
}

static async Task<HlsPlaybackProfile> ResolveRequestedProfileAsync(
    HlsPlaybackProfile baseProfile,
    string? quality,
    int? audioTrackIndex,
    string subtitleMode,
    string? externalSubtitlePath,
    int? embeddedSubtitleStreamIndex,
    string? sourceVideoCodec,
    bool? hardware,
    IHlsSessionService hlsSessionService,
    CancellationToken cancellationToken)
{
    var normalizedQuality = NormalizeQuality(quality);
    var shouldBurnSubtitle = string.Equals(subtitleMode, "burn", StringComparison.OrdinalIgnoreCase)
                             && (externalSubtitlePath is not null || embeddedSubtitleStreamIndex.HasValue);
    var requiresTranscode = baseProfile.TranscodeVideo
                            || normalizedQuality != "original"
                            || shouldBurnSubtitle;

    if (!requiresTranscode)
    {
        return HlsPlaybackProfile.CreateRemux(audioTrackIndex);
    }

    var capabilities = await hlsSessionService.GetCapabilitiesAsync(cancellationToken);
    var hardwareEncoder = hardware == false ? null : capabilities.PreferredHardwareEncoder;
    var hardwareDecoder = hardware == false ? null : SelectHardwareDecoder(capabilities, sourceVideoCodec);
    var hardwareAcceleration = SelectHardwareAcceleration(capabilities, hardwareDecoder);

    return HlsPlaybackProfile.CreateTranscode(
        normalizedQuality == "original" ? "auto" : normalizedQuality,
        audioTrackIndex,
        shouldBurnSubtitle ? subtitleMode : "off",
        shouldBurnSubtitle ? externalSubtitlePath : null,
        shouldBurnSubtitle ? embeddedSubtitleStreamIndex : null,
        hardwareEncoder,
        hardwareDecoder,
        hardwareAcceleration);
}

static string ResolveRequestedMode(
    string baseMode,
    HlsPlaybackProfile profile,
    string? quality,
    int? audioTrackIndex,
    string subtitleMode)
{
    if (baseMode == "direct"
        && NormalizeQuality(quality) == "original"
        && !audioTrackIndex.HasValue
        && !string.Equals(subtitleMode, "burn", StringComparison.OrdinalIgnoreCase))
    {
        return "direct";
    }

    return profile.TranscodeVideo ? "hls-transcode" : "hls-remux";
}

static async Task<PlaybackPolicyResult> ApplyPlaybackSettingsAsync(
    string fileId,
    PlaybackPlan plan,
    HlsPlaybackProfile profile,
    string mode,
    PlaybackSettings settings,
    string? quality,
    int? audioTrackIndex,
    string subtitleMode,
    string? externalSubtitlePath,
    int? embeddedSubtitleStreamIndex,
    string? sourceVideoCodec,
    bool? hardware,
    IHlsSessionService hlsSessionService,
    CancellationToken cancellationToken)
{
    if (RejectSoftwareTranscode(fileId, profile, sourceVideoCodec) is { } rejection)
    {
        return rejection;
    }

    if (mode == "direct")
    {
        if (settings.DirectStream)
        {
            return new PlaybackPolicyResult(mode, profile, plan.Reason, null);
        }

        if (settings.HlsRemux)
        {
            return new PlaybackPolicyResult(
                "hls-remux",
                HlsPlaybackProfile.CreateRemux(audioTrackIndex),
                "Range 直出策略已关闭，改用 HLS 转封装。",
                null);
        }

        if (settings.Transcode)
        {
            var transcodeProfile = await ResolveRequestedProfileAsync(
                HlsPlaybackProfile.Transcode,
                quality,
                audioTrackIndex,
                subtitleMode,
                externalSubtitlePath,
                embeddedSubtitleStreamIndex,
                sourceVideoCodec,
                hardware,
                hlsSessionService,
                cancellationToken);
            if (RejectSoftwareTranscode(fileId, transcodeProfile, sourceVideoCodec) is { } transcodeRejection)
            {
                return transcodeRejection;
            }

            return new PlaybackPolicyResult(
                "hls-transcode",
                transcodeProfile,
                "Range 直出策略已关闭，改用 HLS 转码。",
                null);
        }

        return Unavailable(fileId, "没有启用可用的播放策略。");
    }

    if (mode == "hls-remux")
    {
        if (settings.HlsRemux)
        {
            return new PlaybackPolicyResult(mode, profile, plan.Reason, null);
        }

        if (settings.Transcode)
        {
            var transcodeProfile = await ResolveRequestedProfileAsync(
                HlsPlaybackProfile.Transcode,
                quality,
                audioTrackIndex,
                subtitleMode,
                externalSubtitlePath,
                embeddedSubtitleStreamIndex,
                sourceVideoCodec,
                hardware,
                hlsSessionService,
                cancellationToken);
            if (RejectSoftwareTranscode(fileId, transcodeProfile, sourceVideoCodec) is { } transcodeRejection)
            {
                return transcodeRejection;
            }

            return new PlaybackPolicyResult(
                "hls-transcode",
                transcodeProfile,
                "HLS 转封装策略已关闭，改用 HLS 转码。",
                null);
        }

        if (settings.DirectStream)
        {
            return new PlaybackPolicyResult(
                "direct",
                profile,
                "HLS 转封装策略已关闭，尝试 Range 直出。",
                null);
        }

        return Unavailable(fileId, "当前文件需要 HLS 转封装或转码，但相关播放策略已关闭。");
    }

    if (mode == "hls-transcode")
    {
        if (RejectSoftwareTranscode(fileId, profile, sourceVideoCodec) is { } transcodeRejection)
        {
            return transcodeRejection;
        }

        if (settings.Transcode)
        {
            return new PlaybackPolicyResult(mode, profile, plan.Reason, null);
        }

        if (settings.DirectStream)
        {
            return new PlaybackPolicyResult(
                "direct",
                profile,
                "HLS 转码策略已关闭，尝试 Range 直出。",
                null);
        }

        return Unavailable(fileId, "当前请求需要 HLS 转码，但转码策略已关闭。");
    }

    return new PlaybackPolicyResult(mode, profile, plan.Reason, null);
}

static PlaybackPolicyResult? RejectSoftwareTranscode(string fileId, HlsPlaybackProfile profile, string? sourceVideoCodec)
{
    if (!profile.TranscodeVideo)
    {
        return null;
    }

    if (string.IsNullOrWhiteSpace(profile.HardwareEncoder))
    {
        return Unavailable(
            fileId,
            "当前请求需要视频转码，但未检测到可用硬件编码器。已禁止软件转码，请开启 NAS 硬件转码能力，或改用可直出/转封装的媒体。");
    }

    if (!HasRequiredHardwareDecoder(profile, sourceVideoCodec))
    {
        var codec = NormalizeHardwareDecodeCodec(sourceVideoCodec);
        var reason = string.IsNullOrWhiteSpace(codec)
            ? "当前请求需要视频转码，但缺少媒体探测结果，无法选择硬件解码路径。已禁止软解。"
            : $"当前请求需要视频转码，但未检测到 {codec} 对应的硬件解码路径。已禁止软解。";
        return Unavailable(fileId, reason);
    }

    return null;
}

static PlaybackPolicyResult Unavailable(string fileId, string reason)
{
    return new PlaybackPolicyResult(
        "unavailable",
        HlsPlaybackProfile.Remux,
        reason,
        new PlaybackDecision(fileId, "unavailable", null, null, null, false, reason));
}

static IProgress<BackgroundTaskProgress> ScaleProgress(
    IProgress<BackgroundTaskProgress> progress,
    double offset,
    double span)
{
    return new InlineProgress<BackgroundTaskProgress>(value =>
    {
        double? percent = value.Percent is null
            ? null
            : offset + Math.Clamp(value.Percent.Value, 0, 100) / 100 * span;
        progress.Report(value with { Percent = percent });
    });
}

static string ResolveProfileReason(string baseReason, HlsPlaybackProfile profile)
{
    if (!profile.TranscodeVideo)
    {
        return baseReason;
    }

    var decoder = string.IsNullOrWhiteSpace(profile.HardwareDecoder)
        ? "自动"
        : profile.HardwareDecoder;
    return $"{baseReason} 使用硬件编码 {profile.HardwareEncoder}，硬件解码 {decoder}，档位 {profile.QualityId}。";
}

static string NormalizeQuality(string? quality)
{
    var normalized = quality?.Trim().ToLowerInvariant();
    return string.IsNullOrWhiteSpace(normalized) || normalized == "auto" ? "original" : normalized;
}

static string NormalizeSubtitleMode(string? subtitleMode, string? externalSubtitlePath, int? embeddedSubtitleStreamIndex)
{
    var hasSelectedSubtitle = externalSubtitlePath is not null || embeddedSubtitleStreamIndex.HasValue;
    if (!hasSelectedSubtitle)
    {
        return "off";
    }

    return subtitleMode?.Trim().ToLowerInvariant() switch
    {
        "web" when externalSubtitlePath is not null => "web",
        "burn" => "burn",
        _ => "off"
    };
}

static string NormalizeCodec(string? codec)
{
    return codec?.Trim().ToLowerInvariant() ?? string.Empty;
}

static string NormalizeHardwareDecodeCodec(string? codec)
{
    return NormalizeCodec(codec) switch
    {
        "avc1" => "h264",
        "h265" => "hevc",
        "mpeg2video" => "mpeg2",
        "mpeg2_videotoolbox" => "mpeg2",
        "vp09" => "vp9",
        var value => value
    };
}

static string? SelectHardwareDecoder(FfmpegTranscodeCapabilities capabilities, string? sourceVideoCodec)
{
    if (!capabilities.IsAvailable
        || (capabilities.HardwareDecoders.Count == 0 && capabilities.HardwareAccelerators.Count == 0))
    {
        return null;
    }

    var normalizedCodec = NormalizeHardwareDecodeCodec(sourceVideoCodec);
    var preferredHardwareKind = ResolveHardwareKind(capabilities.PreferredHardwareEncoder);
    var candidates = normalizedCodec switch
    {
        "h264" => new[]
        {
            "h264_vaapi",
            "h264_qsv",
            "h264_cuvid",
            "h264_v4l2m2m",
            "h264_videotoolbox"
        },
        "hevc" => new[]
        {
            "hevc_vaapi",
            "hevc_qsv",
            "hevc_cuvid",
            "hevc_v4l2m2m",
            "hevc_videotoolbox"
        },
        "vp9" => new[]
        {
            "vp9_vaapi",
            "vp9_qsv",
            "vp9_cuvid"
        },
        "av1" => new[]
        {
            "av1_vaapi",
            "av1_qsv",
            "av1_cuvid"
        },
        "mpeg2" => new[]
        {
            "mpeg2_vaapi",
            "mpeg2_qsv",
            "mpeg2_cuvid",
            "mpeg2_v4l2m2m"
        },
        _ => Array.Empty<string>()
    };
    candidates = PrioritizeHardwareDecoderCandidates(candidates, preferredHardwareKind);

    var decoder = candidates.FirstOrDefault(candidate => capabilities.HardwareDecoders.Contains(candidate, StringComparer.OrdinalIgnoreCase));
    if (!string.IsNullOrWhiteSpace(decoder))
    {
        return decoder;
    }

    if (string.IsNullOrWhiteSpace(normalizedCodec))
    {
        return null;
    }

    foreach (var hardwareKind in PrioritizeHardwareKinds(["vaapi", "qsv", "cuda", "videotoolbox"], preferredHardwareKind))
    {
        if (!capabilities.HardwareAccelerators.Contains(hardwareKind, StringComparer.OrdinalIgnoreCase))
        {
            continue;
        }

        return hardwareKind switch
        {
            "cuda" => normalizedCodec == "hevc" ? "hevc_cuvid" : $"{normalizedCodec}_cuvid",
            _ => $"{normalizedCodec}_{hardwareKind}"
        };
    }

    return null;
}

static string[] PrioritizeHardwareDecoderCandidates(string[] candidates, string? preferredHardwareKind)
{
    if (string.IsNullOrWhiteSpace(preferredHardwareKind))
    {
        return candidates;
    }

    return candidates
        .OrderBy(candidate => string.Equals(ResolveHardwareKind(candidate), preferredHardwareKind, StringComparison.OrdinalIgnoreCase)
            ? 0
            : 1)
        .ToArray();
}

static IEnumerable<string> PrioritizeHardwareKinds(IEnumerable<string> hardwareKinds, string? preferredHardwareKind)
{
    if (string.IsNullOrWhiteSpace(preferredHardwareKind))
    {
        return hardwareKinds;
    }

    return hardwareKinds
        .OrderBy(kind => string.Equals(kind, preferredHardwareKind, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
}

static bool HasRequiredHardwareDecoder(HlsPlaybackProfile profile, string? sourceVideoCodec)
{
    if (!profile.TranscodeVideo)
    {
        return true;
    }

    var normalizedCodec = NormalizeHardwareDecodeCodec(sourceVideoCodec);
    if (string.IsNullOrWhiteSpace(normalizedCodec))
    {
        return false;
    }

    return !string.IsNullOrWhiteSpace(profile.HardwareDecoder);
}

static string? SelectHardwareAcceleration(
    FfmpegTranscodeCapabilities capabilities,
    string? hardwareDecoder)
{
    var decoderKind = ResolveHardwareKind(hardwareDecoder);
    if (IsHardwareKindAvailable(capabilities, decoderKind))
    {
        return decoderKind;
    }

    var encoderKind = ResolveHardwareKind(capabilities.PreferredHardwareEncoder);
    if (IsHardwareKindAvailable(capabilities, encoderKind))
    {
        return encoderKind;
    }

    return capabilities.HardwareAccelerators.FirstOrDefault();
}

static bool IsHardwareKindAvailable(FfmpegTranscodeCapabilities capabilities, string? kind)
{
    if (string.IsNullOrWhiteSpace(kind))
    {
        return false;
    }

    return capabilities.HardwareAccelerators.Contains(kind, StringComparer.OrdinalIgnoreCase)
           || capabilities.HardwareDecoders.Any(decoder => string.Equals(ResolveHardwareKind(decoder), kind, StringComparison.OrdinalIgnoreCase))
           || capabilities.HardwareEncoders.Any(encoder => string.Equals(ResolveHardwareKind(encoder), kind, StringComparison.OrdinalIgnoreCase));
}

static string? ResolveHardwareKind(string? codecName)
{
    if (string.IsNullOrWhiteSpace(codecName))
    {
        return null;
    }

    var normalized = codecName.Trim().ToLowerInvariant();
    if (normalized.EndsWith("_vaapi", StringComparison.Ordinal))
    {
        return "vaapi";
    }

    if (normalized.EndsWith("_qsv", StringComparison.Ordinal))
    {
        return "qsv";
    }

    if (normalized.EndsWith("_cuvid", StringComparison.Ordinal)
        || normalized.EndsWith("_nvenc", StringComparison.Ordinal))
    {
        return "cuda";
    }

    if (normalized.EndsWith("_v4l2m2m", StringComparison.Ordinal))
    {
        return "v4l2m2m";
    }

    if (normalized.EndsWith("_videotoolbox", StringComparison.Ordinal))
    {
        return "videotoolbox";
    }

    return null;
}

static bool IsH264(string codec)
{
    return codec is "h264" or "avc1";
}

static bool IsMp4AudioCompatible(string codec)
{
    return string.IsNullOrWhiteSpace(codec) || codec is "aac" or "mp3";
}

static bool IsWebAudioCompatible(string codec)
{
    return string.IsNullOrWhiteSpace(codec) || codec is "opus" or "vorbis";
}

static int? ResolveEmbeddedSubtitleStreamIndex(string? subtitleId)
{
    const string prefix = "embedded_";
    if (string.IsNullOrWhiteSpace(subtitleId)
        || !subtitleId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }

    return int.TryParse(subtitleId[prefix.Length..], out var streamIndex) && streamIndex >= 0
        ? streamIndex
        : null;
}

static async Task<string?> ReadSubtitleAsWebVttAsync(string subtitlePath, CancellationToken cancellationToken)
{
    var extension = Path.GetExtension(subtitlePath).ToLowerInvariant();
    if (extension == ".vtt")
    {
        var text = await File.ReadAllTextAsync(subtitlePath, cancellationToken);
        return text.TrimStart().StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)
            ? text
            : $"WEBVTT\n\n{text}";
    }

    if (extension != ".srt")
    {
        return null;
    }

    var srt = await File.ReadAllTextAsync(subtitlePath, cancellationToken);
    var builder = new StringBuilder("WEBVTT\n\n");
    foreach (var line in srt.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
    {
        builder.AppendLine(line.Contains("-->", StringComparison.Ordinal)
            ? line.Replace(',', '.')
            : line);
    }

    return builder.ToString();
}

static MediaNameParser.CombinedSearchMetadataResult? ResolveSourceSearchMetadata(
    IReadOnlyList<VideoFileSummary> videoFiles)
{
    var sourceFile = videoFiles
        .OrderBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault();
    if (sourceFile is null)
    {
        return null;
    }

    return MediaNameParser.CombinedSearchMetadata(sourceFile.RelativePath, sourceFile.FileName);
}

static string ResolveMetadataSearchQuery(
    LibraryItemDetail item,
    MediaNameParser.CombinedSearchMetadataResult? sourceMetadata,
    string? manualQuery)
{
    if (!string.IsNullOrWhiteSpace(manualQuery))
    {
        return manualQuery.Trim();
    }

    return sourceMetadata?.ChineseTitle
           ?? sourceMetadata?.ParentChineseTitle
           ?? sourceMetadata?.ForeignTitle
           ?? sourceMetadata?.FullCleanTitle
           ?? item.Title;
}

static string? BuildMetadataSecondarySearchQuery(string primaryQuery, params string?[] candidates)
{
    var normalizedPrimary = NormalizeMetadataSearchKey(primaryQuery);
    foreach (var candidate in candidates)
    {
        var trimmed = candidate?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            continue;
        }

        if (!string.Equals(NormalizeMetadataSearchKey(trimmed), normalizedPrimary, StringComparison.Ordinal))
        {
            return trimmed;
        }
    }

    return null;
}

static string? NormalizeMetadataSearchYear(string? value)
{
    var trimmed = value?.Trim();
    if (string.IsNullOrWhiteSpace(trimmed))
    {
        return null;
    }

    return trimmed.Length >= 4 ? trimmed[..4] : trimmed;
}

static string NormalizeMetadataSearchKey(string value)
{
    var builder = new StringBuilder(value.Length);
    foreach (var character in value.Trim())
    {
        if (char.IsLetterOrDigit(character))
        {
            builder.Append(char.ToLowerInvariant(character));
        }
    }

    return builder.ToString();
}

static IReadOnlyList<string> ResolveMetadataSearchTypes(string? requestedMediaType, string itemKind)
{
    var normalizedItemKind = string.Equals(itemKind, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
    var requested = requestedMediaType?.Trim().ToLowerInvariant();
    if (requested is "movie" or "tv")
    {
        return [requested];
    }

    if (requested is "all" or "multi" or "any" or "*")
    {
        return normalizedItemKind == "tv" ? ["tv", "movie"] : ["movie", "tv"];
    }

    return [normalizedItemKind];
}

sealed record PlaybackPlan(string Mode, HlsPlaybackProfile Profile, string Reason);

sealed record PlaybackPolicyResult(
    string Mode,
    HlsPlaybackProfile Profile,
    string Reason,
    PlaybackDecision? Decision);

sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> report;

    public InlineProgress(Action<T> report)
    {
        this.report = report;
    }

    public void Report(T value)
    {
        report(value);
    }
}

public partial class Program;
