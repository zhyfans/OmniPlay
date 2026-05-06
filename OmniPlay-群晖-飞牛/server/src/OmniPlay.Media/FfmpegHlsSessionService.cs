using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;

namespace OmniPlay.Media;

public sealed partial class FfmpegHlsSessionService : IHlsSessionService
{
    private const string ManifestFileName = "index.m3u8";
    private readonly ConcurrentDictionary<string, HlsSessionState> sessions = new(StringComparer.Ordinal);
    private readonly IStoragePaths storagePaths;
    private readonly string ffmpegPath;
    private readonly TimeSpan manifestWaitTimeout;
    private readonly SemaphoreSlim capabilitiesLock = new(1, 1);
    private FfmpegTranscodeCapabilities? cachedCapabilities;

    public FfmpegHlsSessionService(IStoragePaths storagePaths)
        : this(storagePaths, ResolveFfmpegPath(), TimeSpan.FromSeconds(8))
    {
    }

    public FfmpegHlsSessionService(
        IStoragePaths storagePaths,
        string ffmpegPath,
        TimeSpan? manifestWaitTimeout = null)
    {
        this.storagePaths = storagePaths;
        this.ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath;
        this.manifestWaitTimeout = manifestWaitTimeout ?? TimeSpan.FromSeconds(8);
    }

    public async Task<HlsPlaybackSession> EnsureSessionAsync(
        PlayableVideoFile file,
        HlsPlaybackProfile profile,
        CancellationToken cancellationToken = default)
    {
        var sessionId = NormalizeSessionId($"{file.Id}_{profile.CacheKey}");
        var outputDirectory = Path.Combine(storagePaths.TranscodeDirectory, sessionId);
        var manifestPath = Path.Combine(outputDirectory, ManifestFileName);
        Directory.CreateDirectory(outputDirectory);

        var state = sessions.GetOrAdd(
            sessionId,
            static (_, directory) => new HlsSessionState(directory),
            outputDirectory);

        lock (state.SyncRoot)
        {
            if (File.Exists(manifestPath))
            {
                return CreateSession(sessionId, outputDirectory, manifestPath, state);
            }

            if (state.Process is null || state.Process.HasExited)
            {
                ClearSessionDirectory(outputDirectory);
                state.ErrorMessage = null;
                state.Process = TryStartFfmpeg(file, profile, manifestPath, outputDirectory, state);
            }
        }

        await WaitForManifestAsync(manifestPath, state, cancellationToken);
        return CreateSession(sessionId, outputDirectory, manifestPath, state);
    }

    public HlsPlaybackAsset? GetAsset(string sessionId, string assetName)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(assetName))
        {
            return null;
        }

        var normalizedSessionId = NormalizeSessionId(sessionId);
        if (!string.Equals(normalizedSessionId, sessionId, StringComparison.Ordinal))
        {
            return null;
        }

        if (assetName.Contains('/') || assetName.Contains('\\') || assetName.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var extension = Path.GetExtension(assetName).ToLowerInvariant();
        var contentType = extension switch
        {
            ".m3u8" => "application/vnd.apple.mpegurl",
            ".ts" => "video/mp2t",
            _ => null
        };

        if (contentType is null)
        {
            return null;
        }

        var sessionDirectory = Path.Combine(storagePaths.TranscodeDirectory, normalizedSessionId);
        var fullPath = Path.GetFullPath(Path.Combine(sessionDirectory, assetName));
        if (!IsPathInsideRoot(sessionDirectory, fullPath) || !File.Exists(fullPath))
        {
            return null;
        }

        return new HlsPlaybackAsset(fullPath, contentType, extension == ".ts");
    }

    public async Task<FfmpegTranscodeCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        if (cachedCapabilities is not null)
        {
            return cachedCapabilities;
        }

        await capabilitiesLock.WaitAsync(cancellationToken);
        try
        {
            if (cachedCapabilities is not null)
            {
                return cachedCapabilities;
            }

            cachedCapabilities = await DetectCapabilitiesAsync(cancellationToken);
            return cachedCapabilities;
        }
        finally
        {
            capabilitiesLock.Release();
        }
    }

    public string PreviewCommand(PlayableVideoFile file, HlsPlaybackProfile profile)
    {
        var sessionId = NormalizeSessionId($"{file.Id}_{profile.CacheKey}");
        var outputDirectory = Path.Combine(storagePaths.TranscodeDirectory, sessionId);
        var manifestPath = Path.Combine(outputDirectory, ManifestFileName);
        var arguments = BuildFfmpegArguments(file.AbsolutePath, profile, manifestPath, outputDirectory);
        return string.Join(' ', new[] { QuoteShellArgument(ffmpegPath) }.Concat(arguments.Select(QuoteShellArgument)));
    }

    public bool StopSession(string sessionId)
    {
        var normalizedSessionId = NormalizeSessionId(sessionId);
        if (!sessions.TryRemove(normalizedSessionId, out var state))
        {
            return false;
        }

        lock (state.SyncRoot)
        {
            TryKill(state.Process);
            state.Process = null;
            state.ErrorMessage = "转码已停止。";
        }

        return true;
    }

    public HlsCacheCleanupSummary CleanupCache(TimeSpan maxAge)
    {
        var threshold = DateTimeOffset.UtcNow - maxAge;
        var removedSessions = 0;
        long removedBytes = 0;

        if (!Directory.Exists(storagePaths.TranscodeDirectory))
        {
            return new HlsCacheCleanupSummary(0, 0);
        }

        foreach (var directory in Directory.EnumerateDirectories(storagePaths.TranscodeDirectory))
        {
            var sessionId = Path.GetFileName(directory);
            if (sessions.TryGetValue(sessionId, out var state) && state.Process is { HasExited: false })
            {
                continue;
            }

            var info = new DirectoryInfo(directory);
            if (info.LastWriteTimeUtc > threshold.UtcDateTime)
            {
                continue;
            }

            removedBytes += DirectorySize(info);
            Directory.Delete(directory, recursive: true);
            sessions.TryRemove(sessionId, out _);
            removedSessions++;
        }

        return new HlsCacheCleanupSummary(removedSessions, removedBytes);
    }

    private Process? TryStartFfmpeg(
        PlayableVideoFile file,
        HlsPlaybackProfile profile,
        string manifestPath,
        string outputDirectory,
        HlsSessionState state)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        AddFfmpegArguments(process.StartInfo, file.AbsolutePath, profile, manifestPath, outputDirectory);
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                AppendLimited(stderr, args.Data);
            }
        };
        process.Exited += (_, _) =>
        {
            lock (state.SyncRoot)
            {
                if (process.ExitCode != 0 && !File.Exists(manifestPath))
                {
                    state.ErrorMessage = string.IsNullOrWhiteSpace(stderr.ToString())
                        ? $"FFmpeg exited with code {process.ExitCode}."
                        : stderr.ToString();
                }

                if (ReferenceEquals(state.Process, process))
                {
                    state.Process = null;
                }
            }

            process.Dispose();
        };

        try
        {
            process.Start();
            process.BeginErrorReadLine();
            return process;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            state.ErrorMessage = $"无法启动 FFmpeg：{ex.Message}";
            return null;
        }
    }

    private static void AddFfmpegArguments(
        ProcessStartInfo startInfo,
        string inputPath,
        HlsPlaybackProfile profile,
        string manifestPath,
        string outputDirectory)
    {
        var arguments = BuildFfmpegArguments(inputPath, profile, manifestPath, outputDirectory);

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    private static List<string> BuildFfmpegArguments(
        string inputPath,
        HlsPlaybackProfile profile,
        string manifestPath,
        string outputDirectory)
    {
        var segmentPattern = Path.Combine(outputDirectory, "segment_%05d.ts");
        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-y",
            "-i", inputPath,
            "-map", "0:v:0",
            "-map", profile.AudioTrackIndex.HasValue ? $"0:a:{profile.AudioTrackIndex.Value}?" : "0:a:0?",
        };

        if (profile.TranscodeVideo)
        {
            var videoFilters = BuildVideoFilters(inputPath, profile);
            if (videoFilters.Count > 0)
            {
                arguments.AddRange(["-vf", string.Join(",", videoFilters)]);
            }

            if (!string.IsNullOrWhiteSpace(profile.HardwareEncoder))
            {
                arguments.AddRange(["-c:v", profile.HardwareEncoder]);
                if (profile.VideoBitrateKbps.HasValue)
                {
                    arguments.AddRange(["-b:v", $"{profile.VideoBitrateKbps.Value}k"]);
                }
            }
            else
            {
                arguments.AddRange([
                    "-c:v", "libx264",
                    "-preset", "veryfast",
                    "-crf", "23",
                    "-pix_fmt", "yuv420p",
                    "-profile:v", "high"
                ]);
                if (profile.VideoBitrateKbps.HasValue)
                {
                    arguments.AddRange(["-maxrate", $"{profile.VideoBitrateKbps.Value}k"]);
                    arguments.AddRange(["-bufsize", $"{profile.VideoBitrateKbps.Value * 2}k"]);
                }
            }
        }
        else
        {
            arguments.AddRange(["-c:v", "copy"]);
        }

        arguments.AddRange([
            "-c:a", "aac",
            "-ac", "2",
            "-b:a", $"{profile.AudioBitrateKbps}k",
            "-sn",
            "-max_muxing_queue_size", "1024",
            "-f", "hls",
            "-hls_time", "6",
            "-hls_list_size", "0",
            "-hls_flags", "independent_segments+temp_file",
            "-hls_segment_filename", segmentPattern,
            manifestPath
        ]);

        return arguments;
    }

    private async Task WaitForManifestAsync(
        string manifestPath,
        HlsSessionState state,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        while (!File.Exists(manifestPath)
               && state.ErrorMessage is null
               && Stopwatch.GetElapsedTime(startedAt) < manifestWaitTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(250, cancellationToken);
        }
    }

    private static HlsPlaybackSession CreateSession(
        string sessionId,
        string outputDirectory,
        string manifestPath,
        HlsSessionState state)
    {
        bool isRunning;
        string? errorMessage;
        lock (state.SyncRoot)
        {
            isRunning = state.Process is { HasExited: false };
            errorMessage = state.ErrorMessage;
        }

        return new HlsPlaybackSession(
            sessionId,
            manifestPath,
            outputDirectory,
            File.Exists(manifestPath),
            isRunning,
            errorMessage);
    }

    private static void ClearSessionDirectory(string outputDirectory)
    {
        foreach (var file in Directory.EnumerateFiles(outputDirectory))
        {
            File.Delete(file);
        }
    }

    private async Task<FfmpegTranscodeCapabilities> DetectCapabilitiesAsync(CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-hide_banner");
        process.StartInfo.ArgumentList.Add("-encoders");

        try
        {
            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                return new FfmpegTranscodeCapabilities(
                    false,
                    ffmpegPath,
                    [],
                    null,
                    string.IsNullOrWhiteSpace(stderr) ? $"FFmpeg exited with code {process.ExitCode}." : stderr,
                    DateTimeOffset.UtcNow);
            }

            var encoders = DetectHardwareEncoders(stdout);
            return new FfmpegTranscodeCapabilities(
                true,
                ffmpegPath,
                encoders,
                encoders.FirstOrDefault(),
                null,
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new FfmpegTranscodeCapabilities(
                false,
                ffmpegPath,
                [],
                null,
                ex.Message,
                DateTimeOffset.UtcNow);
        }
    }

    private static IReadOnlyList<string> DetectHardwareEncoders(string encoderOutput)
    {
        var preferred = new[]
        {
            "h264_videotoolbox",
            "h264_vaapi",
            "h264_qsv",
            "h264_nvenc",
            "h264_v4l2m2m"
        };

        return preferred
            .Where(encoder => encoderOutput.Contains(encoder, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static List<string> BuildVideoFilters(string inputPath, HlsPlaybackProfile profile)
    {
        List<string> filters = [];
        if (profile.MaxHeight.HasValue)
        {
            filters.Add($"scale=-2:min(ih\\,{profile.MaxHeight.Value})");
        }

        if (string.Equals(profile.SubtitleMode, "burn", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(profile.ExternalSubtitlePath))
        {
            filters.Add($"subtitles='{EscapeSubtitlePathForFilter(profile.ExternalSubtitlePath)}'");
        }
        else if (string.Equals(profile.SubtitleMode, "burn", StringComparison.OrdinalIgnoreCase)
                 && profile.EmbeddedSubtitleStreamIndex.HasValue)
        {
            filters.Add($"subtitles='{EscapeSubtitlePathForFilter(inputPath)}':si={profile.EmbeddedSubtitleStreamIndex.Value}");
        }

        return filters;
    }

    private static string EscapeSubtitlePathForFilter(string path)
    {
        return path
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
    }

    private static string NormalizeSessionId(string value)
    {
        var normalized = SessionIdRegex().Replace(value.Trim(), "_");
        return normalized.Length == 0 ? "session" : normalized;
    }

    private static void TryKill(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Stopping a best-effort transcode session should not fail the API.
        }
    }

    private static long DirectorySize(DirectoryInfo directory)
    {
        return directory.EnumerateFiles("*", SearchOption.AllDirectories).Sum(static file => file.Length);
    }

    private static string ResolveFfmpegPath()
    {
        return Environment.GetEnvironmentVariable("OMNIPLAY_FFMPEG_PATH") ?? "ffmpeg";
    }

    private static string QuoteShellArgument(string argument)
    {
        return string.IsNullOrEmpty(argument) || argument.Any(char.IsWhiteSpace) || argument.Contains('\'', StringComparison.Ordinal)
            ? $"'{argument.Replace("'", "'\\''", StringComparison.Ordinal)}'"
            : argument;
    }

    private static bool IsPathInsideRoot(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.GetFullPath(candidate);

        return normalizedCandidate.Equals(normalizedRoot, comparison)
               || normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static void AppendLimited(StringBuilder builder, string line)
    {
        if (builder.Length > 4000)
        {
            return;
        }

        builder.AppendLine(line);
    }

    [GeneratedRegex("[^A-Za-z0-9_.-]")]
    private static partial Regex SessionIdRegex();

    private sealed class HlsSessionState
    {
        public HlsSessionState(string outputDirectory)
        {
            OutputDirectory = outputDirectory;
        }

        public string OutputDirectory { get; }

        public object SyncRoot { get; } = new();

        public Process? Process { get; set; }

        public string? ErrorMessage { get; set; }
    }
}
