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
    private const int MinimumPlayableSegmentCount = 1;
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
                if ((state.Process is { HasExited: false } && IsManifestReadyForPlayback(manifestPath))
                    || IsCompleteManifest(manifestPath))
                {
                    return CreateSession(sessionId, outputDirectory, manifestPath, state);
                }

                ClearSessionDirectory(outputDirectory);
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
            ".mp4" => "video/mp4",
            ".m4s" => "video/iso.segment",
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

        return new HlsPlaybackAsset(fullPath, contentType, extension is ".ts" or ".mp4" or ".m4s");
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
        if (profile.TranscodeVideo && string.IsNullOrWhiteSpace(profile.HardwareEncoder))
        {
            return "当前请求需要视频转码，但没有检测到可用硬件编码器。";
        }

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
        if (profile.TranscodeVideo && string.IsNullOrWhiteSpace(profile.HardwareEncoder))
        {
            state.ErrorMessage = "当前请求需要视频转码，但没有检测到可用硬件编码器。";
            return null;
        }

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

        ConfigureSubtitleFontEnvironment(process.StartInfo);
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

    private static void ConfigureSubtitleFontEnvironment(ProcessStartInfo startInfo)
    {
        var packagedFontconfigPath = Path.Combine(AppContext.BaseDirectory, "tools", "fontconfig");
        var packagedFontconfigFile = Path.Combine(packagedFontconfigPath, "fonts.conf");
        if (File.Exists(packagedFontconfigFile))
        {
            startInfo.Environment.TryAdd("FONTCONFIG_FILE", packagedFontconfigFile);
            startInfo.Environment.TryAdd("FONTCONFIG_PATH", packagedFontconfigPath);
        }

        var appRoot = Environment.GetEnvironmentVariable("OMNIPLAY_APP_ROOT");
        if (!string.IsNullOrWhiteSpace(appRoot))
        {
            var fontconfigCache = Path.Combine(appRoot, "cache", "fontconfig");
            Directory.CreateDirectory(fontconfigCache);
            startInfo.Environment.TryAdd("FONTCONFIG_CACHE", fontconfigCache);
        }
    }

    private static List<string> BuildFfmpegArguments(
        string inputPath,
        HlsPlaybackProfile profile,
        string manifestPath,
        string outputDirectory)
    {
        var segmentPattern = Path.Combine(outputDirectory, "segment_%05d.m4s");
        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-y",
            "-fflags", "+genpts",
        };

        if (profile.TranscodeVideo && !string.IsNullOrWhiteSpace(profile.HardwareEncoder))
        {
            AddHardwareInputArguments(arguments, profile);
        }

        var usesBitmapSubtitleFilterGraph = profile.TranscodeVideo && BurnsEmbeddedBitmapSubtitle(profile);
        var audioMap = profile.AudioTrackIndex.HasValue ? $"0:a:{profile.AudioTrackIndex.Value}?" : "0:a:0?";
        arguments.AddRange(["-i", inputPath]);
        if (usesBitmapSubtitleFilterGraph)
        {
            arguments.AddRange([
                "-filter_complex", BuildEmbeddedBitmapSubtitleFilterComplex(inputPath, profile),
                "-map", "[vout]",
                "-map", audioMap
            ]);
        }
        else
        {
            arguments.AddRange([
                "-map", "0:v:0",
                "-map", audioMap,
            ]);
        }

        if (profile.TranscodeVideo)
        {
            if (!usesBitmapSubtitleFilterGraph)
            {
                var videoFilters = BuildVideoFilters(inputPath, profile);
                if (videoFilters.Count > 0)
                {
                    arguments.AddRange(["-vf", string.Join(",", videoFilters)]);
                }
            }

            if (!string.IsNullOrWhiteSpace(profile.HardwareEncoder))
            {
                arguments.AddRange(["-c:v", profile.HardwareEncoder]);
                if (profile.VideoBitrateKbps.HasValue)
                {
                    arguments.AddRange(["-b:v", $"{profile.VideoBitrateKbps.Value}k"]);
                }

                if (profile.ToneMapToSdr)
                {
                    arguments.AddRange([
                        "-color_primaries", "bt709",
                        "-color_trc", "bt709",
                        "-colorspace", "bt709"
                    ]);
                }
            }
            else
            {
                throw new InvalidOperationException("当前请求需要视频转码，但没有检测到可用硬件编码器。");
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
            "-hls_time", "2",
            "-hls_list_size", "0",
            "-hls_playlist_type", "event",
            "-hls_segment_type", "fmp4",
            "-hls_fmp4_init_filename", "init.mp4",
            "-hls_flags", "independent_segments+temp_file",
            "-hls_segment_filename", segmentPattern,
            manifestPath
        ]);

        return arguments;
    }

    private static void AddHardwareInputArguments(List<string> arguments, HlsPlaybackProfile profile)
    {
        var acceleration = profile.HardwareAcceleration ?? ResolveHardwareKind(profile.HardwareEncoder);
        var hasHardwareDecoder = !string.IsNullOrWhiteSpace(profile.HardwareDecoder);

        if (string.Equals(acceleration, "vaapi", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ResolveHardwareKind(profile.HardwareEncoder), "vaapi", StringComparison.OrdinalIgnoreCase))
        {
            arguments.AddRange(["-init_hw_device", $"vaapi=va:{ResolveVaapiDevicePath()}", "-filter_hw_device", "va"]);
            if (hasHardwareDecoder)
            {
                arguments.AddRange(["-hwaccel", "vaapi", "-hwaccel_device", "va", "-hwaccel_output_format", "vaapi"]);
            }
        }
        else if (string.Equals(acceleration, "qsv", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(ResolveHardwareKind(profile.HardwareEncoder), "qsv", StringComparison.OrdinalIgnoreCase))
        {
            arguments.AddRange([
                "-init_hw_device",
                $"qsv=qs:hw,child_device={ResolveVaapiDevicePath()},child_device_type=vaapi",
                "-filter_hw_device",
                "qs"
            ]);
            if (hasHardwareDecoder)
            {
                arguments.AddRange(["-hwaccel", "qsv", "-hwaccel_device", "qs", "-hwaccel_output_format", "qsv"]);
            }
        }
        else if (hasHardwareDecoder && string.Equals(acceleration, "cuda", StringComparison.OrdinalIgnoreCase))
        {
            arguments.AddRange(["-hwaccel", "cuda", "-hwaccel_output_format", "cuda"]);
        }
        else if (hasHardwareDecoder && string.Equals(acceleration, "videotoolbox", StringComparison.OrdinalIgnoreCase))
        {
            arguments.AddRange(["-hwaccel", "videotoolbox"]);
        }

        if (hasHardwareDecoder
            && profile.HardwareDecoder!.EndsWith("_v4l2m2m", StringComparison.OrdinalIgnoreCase))
        {
            arguments.AddRange(["-c:v", profile.HardwareDecoder]);
        }
    }

    private static string ResolveVaapiDevicePath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("OMNIPLAY_VAAPI_DEVICE");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        try
        {
            var renderDevice = Directory.Exists("/dev/dri")
                ? Directory.EnumerateFiles("/dev/dri", "renderD*").Order(StringComparer.Ordinal).FirstOrDefault()
                : null;
            if (!string.IsNullOrWhiteSpace(renderDevice))
            {
                return renderDevice;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall back to the common Linux render node below.
        }

        return "/dev/dri/renderD128";
    }

    private async Task WaitForManifestAsync(
        string manifestPath,
        HlsSessionState state,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        while (!IsManifestReadyForPlayback(manifestPath)
               && state.ErrorMessage is null
               && Stopwatch.GetElapsedTime(startedAt) < manifestWaitTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(250, cancellationToken);
        }
    }

    private static bool IsCompleteManifest(string manifestPath)
    {
        try
        {
            return File.ReadLines(manifestPath).Any(static line =>
                line.Trim().Equals("#EXT-X-ENDLIST", StringComparison.Ordinal));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsManifestReadyForPlayback(string manifestPath)
    {
        try
        {
            var segmentCount = 0;
            foreach (var line in File.ReadLines(manifestPath))
            {
                var trimmed = line.Trim();
                if (trimmed.Equals("#EXT-X-ENDLIST", StringComparison.Ordinal))
                {
                    return true;
                }

                if (trimmed.StartsWith("#EXTINF:", StringComparison.Ordinal))
                {
                    segmentCount++;
                    if (segmentCount >= MinimumPlayableSegmentCount)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
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
            IsManifestReadyForPlayback(manifestPath),
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
                    [],
                    null,
                    [],
                    string.IsNullOrWhiteSpace(stderr) ? $"FFmpeg exited with code {process.ExitCode}." : stderr,
                    DateTimeOffset.UtcNow);
            }

            var decodersOutput = await RunFfmpegListAsync("-decoders", cancellationToken);
            var hwaccelsOutput = await RunFfmpegListAsync("-hwaccels", cancellationToken);
            var detectedEncoders = DetectHardwareEncoders(stdout);
            var detectedAccelerators = DetectHardwareAccelerators(hwaccelsOutput);
            var detectedDecoders = DetectHardwareDecoders(decodersOutput, detectedAccelerators);
            var probeResult = await FilterUsableHardwareEncodersAsync(detectedEncoders, cancellationToken);
            var encoders = probeResult.UsableEncoders;
            var hardwareKinds = encoders
                .Select(ResolveHardwareKind)
                .Where(static kind => !string.IsNullOrWhiteSpace(kind))
                .Select(static kind => kind!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var accelerators = FilterHardwareCapabilities(detectedAccelerators, hardwareKinds);
            var decoders = FilterHardwareCapabilities(detectedDecoders, hardwareKinds);
            return new FfmpegTranscodeCapabilities(
                true,
                ffmpegPath,
                encoders,
                SelectPreferredHardwareEncoder(encoders),
                decoders,
                decoders.FirstOrDefault(),
                accelerators,
                null,
                DateTimeOffset.UtcNow,
                detectedEncoders,
                detectedDecoders,
                detectedAccelerators,
                probeResult.Errors);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new FfmpegTranscodeCapabilities(
                false,
                ffmpegPath,
                [],
                null,
                [],
                null,
                [],
                ex.Message,
                DateTimeOffset.UtcNow);
        }
    }

    private async Task<string> RunFfmpegListAsync(string argument, CancellationToken cancellationToken)
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
        process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return stdout;
    }

    private static IReadOnlyList<string> DetectHardwareEncoders(string encoderOutput)
    {
        var preferred = new[]
        {
            "h264_vaapi",
            "hevc_vaapi",
            "av1_vaapi",
            "h264_qsv",
            "hevc_qsv",
            "av1_qsv",
            "h264_nvenc",
            "hevc_nvenc",
            "av1_nvenc",
            "h264_v4l2m2m",
            "hevc_v4l2m2m",
            "h264_videotoolbox",
            "hevc_videotoolbox"
        };

        return preferred
            .Where(encoder => encoderOutput.Contains(encoder, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static string? SelectPreferredHardwareEncoder(IReadOnlyList<string> encoders)
    {
        return encoders.FirstOrDefault(static encoder => encoder.StartsWith("h264_", StringComparison.OrdinalIgnoreCase))
               ?? encoders.FirstOrDefault(static encoder => encoder.StartsWith("hevc_", StringComparison.OrdinalIgnoreCase))
               ?? encoders.FirstOrDefault();
    }

    private static IReadOnlyList<string> DetectHardwareDecoders(
        string decoderOutput,
        IReadOnlyList<string> accelerators)
    {
        var preferred = new[]
        {
            "h264_vaapi",
            "hevc_vaapi",
            "vp9_vaapi",
            "av1_vaapi",
            "mpeg2_vaapi",
            "h264_qsv",
            "hevc_qsv",
            "vp9_qsv",
            "av1_qsv",
            "mpeg2_qsv",
            "h264_cuvid",
            "hevc_cuvid",
            "vp9_cuvid",
            "av1_cuvid",
            "mpeg2_cuvid",
            "h264_v4l2m2m",
            "hevc_v4l2m2m",
            "mpeg2_v4l2m2m",
            "h264_videotoolbox",
            "hevc_videotoolbox"
        };

        var detected = preferred
            .Where(decoder => decoderOutput.Contains(decoder, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var expanded = ExpandGenericHardwareDecoders(accelerators);
        return detected
            .Concat(expanded)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ExpandGenericHardwareDecoders(IReadOnlyList<string> accelerators)
    {
        List<string> decoders = [];
        if (accelerators.Contains("vaapi", StringComparer.OrdinalIgnoreCase))
        {
            decoders.AddRange(["h264_vaapi", "hevc_vaapi", "vp9_vaapi", "av1_vaapi", "mpeg2_vaapi"]);
        }

        if (accelerators.Contains("qsv", StringComparer.OrdinalIgnoreCase))
        {
            decoders.AddRange(["h264_qsv", "hevc_qsv", "vp9_qsv", "av1_qsv", "mpeg2_qsv"]);
        }

        if (accelerators.Contains("cuda", StringComparer.OrdinalIgnoreCase))
        {
            decoders.AddRange(["h264_cuvid", "hevc_cuvid", "vp9_cuvid", "av1_cuvid", "mpeg2_cuvid"]);
        }

        if (accelerators.Contains("videotoolbox", StringComparer.OrdinalIgnoreCase))
        {
            decoders.AddRange(["h264_videotoolbox", "hevc_videotoolbox", "vp9_videotoolbox", "av1_videotoolbox"]);
        }

        return decoders;
    }

    private static IReadOnlyList<string> DetectHardwareAccelerators(string hwaccelsOutput)
    {
        var preferred = new[]
        {
            "vaapi",
            "qsv",
            "cuda",
            "videotoolbox",
            "v4l2m2m",
            "drm",
            "opencl"
        };

        return preferred
            .Where(accelerator => hwaccelsOutput.Contains(accelerator, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private async Task<HardwareEncoderProbeSummary> FilterUsableHardwareEncodersAsync(
        IReadOnlyList<string> encoders,
        CancellationToken cancellationToken)
    {
        if (ShouldSkipHardwareEncoderProbe())
        {
            return new HardwareEncoderProbeSummary(encoders, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        List<string> usable = [];
        Dictionary<string, string> errors = new(StringComparer.OrdinalIgnoreCase);
        foreach (var encoder in encoders)
        {
            if (!IsHardwareEncoderCandidateForCurrentHost(encoder, out var unavailableReason))
            {
                var kind = ResolveHardwareKind(encoder);
                if (string.Equals(kind, "vaapi", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kind, "qsv", StringComparison.OrdinalIgnoreCase))
                {
                    errors[encoder] = unavailableReason;
                }

                continue;
            }

            var error = await ProbeHardwareEncoderAsync(encoder, cancellationToken);
            if (error is null)
            {
                usable.Add(encoder);
                continue;
            }

            errors[encoder] = error;
        }

        return new HardwareEncoderProbeSummary(usable, errors);
    }

    private static bool IsHardwareEncoderCandidateForCurrentHost(string encoder, out string unavailableReason)
    {
        var kind = ResolveHardwareKind(encoder);
        unavailableReason = string.Empty;
        if (string.Equals(kind, "cuda", StringComparison.OrdinalIgnoreCase))
        {
            if (OperatingSystem.IsLinux() && !File.Exists("/dev/nvidiactl"))
            {
                unavailableReason = "未发现 /dev/nvidiactl，当前环境没有可用 NVIDIA 编码设备";
                return false;
            }
        }

        if (string.Equals(kind, "v4l2m2m", StringComparison.OrdinalIgnoreCase))
        {
            if (OperatingSystem.IsLinux() && !HasDeviceFile("/dev", "video*"))
            {
                unavailableReason = "未发现 /dev/video*，当前环境没有可用 V4L2 M2M 编码设备";
                return false;
            }
        }

        if (string.Equals(kind, "vaapi", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "qsv", StringComparison.OrdinalIgnoreCase))
        {
            if (OperatingSystem.IsLinux() && !HasDeviceFile("/dev/dri", "renderD*"))
            {
                unavailableReason = "未发现 /dev/dri/renderD*，当前环境没有可用 DRI 渲染设备";
                return false;
            }
        }

        if (string.Equals(kind, "videotoolbox", StringComparison.OrdinalIgnoreCase) && !OperatingSystem.IsMacOS())
        {
            unavailableReason = "当前系统不是 macOS，不能使用 VideoToolbox";
            return false;
        }

        return true;
    }

    private static bool HasDeviceFile(string directory, string pattern)
    {
        try
        {
            return Directory.Exists(directory) && Directory.EnumerateFiles(directory, pattern).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<string?> ProbeHardwareEncoderAsync(string encoder, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        Process? currentProcess = null;

        try
        {
            string? lastError = null;
            foreach (var arguments in BuildHardwareEncoderProbeArgumentVariants(encoder))
            {
                using var variantProcess = new Process
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
                currentProcess = variantProcess;
                foreach (var argument in arguments)
                {
                    variantProcess.StartInfo.ArgumentList.Add(argument);
                }

                variantProcess.Start();
                var stdoutTask = variantProcess.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderrTask = variantProcess.StandardError.ReadToEndAsync(timeout.Token);
                await variantProcess.WaitForExitAsync(timeout.Token);
                await Task.WhenAll(stdoutTask, stderrTask);
                if (variantProcess.ExitCode == 0)
                {
                    return null;
                }

                var stderr = await stderrTask;
                var stdout = await stdoutTask;
                var output = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                lastError = LimitProbeError(string.IsNullOrWhiteSpace(output)
                    ? $"FFmpeg 探测退出码 {variantProcess.ExitCode}"
                    : output);
                currentProcess = null;
            }

            return lastError ?? "FFmpeg 硬件编码器探测失败";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKillProcess(currentProcess);
            return "硬件编码器探测超时";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            TryKillProcess(currentProcess);
            return LimitProbeError(ex.Message);
        }
    }

    private static IReadOnlyList<IReadOnlyList<string>> BuildHardwareEncoderProbeArgumentVariants(string encoder)
    {
        var kind = ResolveHardwareKind(encoder);
        if (string.Equals(kind, "vaapi", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                BuildHardwareEncoderProbeArguments(encoder, "vaapi-init-device"),
                BuildHardwareEncoderProbeArguments(encoder, "vaapi-device")
            ];
        }

        if (string.Equals(kind, "qsv", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                BuildHardwareEncoderProbeArguments(encoder, "qsv-child-device"),
                BuildHardwareEncoderProbeArguments(encoder, "qsv-derived-vaapi"),
                BuildHardwareEncoderProbeArguments(encoder, "qsv-device")
            ];
        }

        return [BuildHardwareEncoderProbeArguments(encoder, null)];
    }

    private static IReadOnlyList<string> BuildHardwareEncoderProbeArguments(string encoder, string? variant)
    {
        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-y"
        };

        var kind = ResolveHardwareKind(encoder);
        if (string.Equals(kind, "vaapi", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(variant, "vaapi-init-device", StringComparison.OrdinalIgnoreCase))
            {
                arguments.AddRange(["-init_hw_device", $"vaapi=va:{ResolveVaapiDevicePath()}", "-filter_hw_device", "va"]);
            }
            else
            {
                arguments.AddRange(["-vaapi_device", ResolveVaapiDevicePath()]);
            }
        }
        else if (string.Equals(kind, "qsv", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(variant, "qsv-derived-vaapi", StringComparison.OrdinalIgnoreCase))
            {
                arguments.AddRange([
                    "-init_hw_device", $"vaapi=va:{ResolveVaapiDevicePath()}",
                    "-init_hw_device", "qsv=qs@va",
                    "-filter_hw_device", "qs"
                ]);
            }
            else if (string.Equals(variant, "qsv-device", StringComparison.OrdinalIgnoreCase))
            {
                arguments.AddRange(["-qsv_device", ResolveVaapiDevicePath()]);
            }
            else
            {
                arguments.AddRange([
                    "-init_hw_device",
                    $"qsv=qs:hw,child_device={ResolveVaapiDevicePath()},child_device_type=vaapi",
                    "-filter_hw_device",
                    "qs"
                ]);
            }
        }

        arguments.AddRange([
            "-f", "lavfi",
            "-i", "testsrc2=size=64x64:rate=1"
        ]);

        if (string.Equals(kind, "vaapi", StringComparison.OrdinalIgnoreCase))
        {
            arguments.AddRange(["-vf", "format=nv12,hwupload"]);
        }
        else if (string.Equals(kind, "qsv", StringComparison.OrdinalIgnoreCase))
        {
            arguments.AddRange(["-vf", "format=nv12,hwupload=extra_hw_frames=64,format=qsv"]);
        }

        arguments.AddRange([
            "-frames:v", "1",
            "-an",
            "-c:v", encoder,
            "-f", "null",
            "-"
        ]);

        return arguments;
    }

    private static IReadOnlyList<string> FilterHardwareCapabilities(
        IReadOnlyList<string> capabilities,
        IReadOnlySet<string> allowedHardwareKinds)
    {
        if (allowedHardwareKinds.Count == 0)
        {
            return [];
        }

        return capabilities
            .Where(capability =>
            {
                var kind = ResolveHardwareKind(capability) ?? NormalizeHardwareKindName(capability);
                return kind is not null && allowedHardwareKinds.Contains(kind);
            })
            .ToArray();
    }

    private static bool ShouldSkipHardwareEncoderProbe()
    {
        var value = Environment.GetEnvironmentVariable("OMNIPLAY_SKIP_HARDWARE_ENCODER_PROBE");
        return value is not null
               && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeHardwareKindName(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is "vaapi" or "qsv" or "cuda" or "v4l2m2m" or "videotoolbox"
            ? normalized
            : null;
    }

    private static string LimitProbeError(string value)
    {
        var normalized = Regex.Replace(value.Trim(), @"\s+", " ");
        return normalized.Length <= 240 ? normalized : $"{normalized[..240]}...";
    }

    private static void TryKillProcess(Process? process)
    {
        try
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // The probe has already failed; there is nothing useful to surface here.
        }
    }

    private sealed record HardwareEncoderProbeSummary(
        IReadOnlyList<string> UsableEncoders,
        IReadOnlyDictionary<string, string> Errors);

    private static List<string> BuildVideoFilters(string inputPath, HlsPlaybackProfile profile)
    {
        return BuildVideoFilters(inputPath, profile, includeSubtitleFilters: true, includeEncoderUpload: true);
    }

    private static List<string> BuildVideoFilters(
        string inputPath,
        HlsPlaybackProfile profile,
        bool includeSubtitleFilters,
        bool includeEncoderUpload)
    {
        List<string> filters = [];
        var burnsSubtitle = BurnsSubtitle(profile);
        var usesVaapiEncoder = string.Equals(ResolveHardwareKind(profile.HardwareEncoder), "vaapi", StringComparison.OrdinalIgnoreCase);
        var usesQsvEncoder = string.Equals(ResolveHardwareKind(profile.HardwareEncoder), "qsv", StringComparison.OrdinalIgnoreCase);
        var usesVaapiDecoderFrames = string.Equals(ResolveHardwareKind(profile.HardwareDecoder), "vaapi", StringComparison.OrdinalIgnoreCase)
                                     && string.Equals(profile.HardwareAcceleration, "vaapi", StringComparison.OrdinalIgnoreCase);

        if (profile.ToneMapToSdr)
        {
            var usesHardwareToneMap =
                string.Equals(profile.ToneMapMode, "hardware", StringComparison.OrdinalIgnoreCase)
                && usesVaapiEncoder
                && usesVaapiDecoderFrames
                && !burnsSubtitle;
            if (usesHardwareToneMap)
            {
                filters.Add("tonemap_vaapi=format=nv12");
                if (profile.MaxHeight.HasValue)
                {
                    filters.Add($"scale_vaapi=w=-2:h={profile.MaxHeight.Value}:format=nv12");
                }

                return filters;
            }

            if (usesVaapiDecoderFrames)
            {
                filters.Add("hwdownload");
                filters.Add("format=p010le");
            }

            if (profile.MaxHeight.HasValue)
            {
                filters.Add($"scale=-2:min(ih\\,{profile.MaxHeight.Value})");
            }

            AddSdrToneMapFilters(filters, profile.ToneMapMode);
            filters.Add("format=yuv420p");
            if (includeSubtitleFilters)
            {
                AddSubtitleFilters(filters, inputPath, profile);
            }

            if (includeEncoderUpload)
            {
                AddEncoderUploadFilters(filters, profile);
            }

            return filters;
        }

        if (usesVaapiEncoder)
        {
            if (usesVaapiDecoderFrames)
            {
                filters.Add("hwdownload");
                filters.Add("format=nv12");
            }

            if (profile.MaxHeight.HasValue)
            {
                filters.Add($"scale=-2:min(ih\\,{profile.MaxHeight.Value})");
            }

            if (includeSubtitleFilters)
            {
                AddSubtitleFilters(filters, inputPath, profile);
            }

            if (includeEncoderUpload)
            {
                AddEncoderUploadFilters(filters, profile);
            }

            return filters;
        }

        if (usesQsvEncoder)
        {
            if (profile.MaxHeight.HasValue)
            {
                filters.Add($"scale=-2:min(ih\\,{profile.MaxHeight.Value})");
            }

            if (includeSubtitleFilters)
            {
                AddSubtitleFilters(filters, inputPath, profile);
            }

            if (includeEncoderUpload)
            {
                AddEncoderUploadFilters(filters, profile);
            }

            return filters;
        }

        if (profile.MaxHeight.HasValue)
        {
            filters.Add($"scale=-2:min(ih\\,{profile.MaxHeight.Value})");
        }

        if (includeSubtitleFilters)
        {
            AddSubtitleFilters(filters, inputPath, profile);
        }

        return filters;
    }

    private static string BuildEmbeddedBitmapSubtitleFilterComplex(string inputPath, HlsPlaybackProfile profile)
    {
        var subtitleStreamIndex = profile.EmbeddedSubtitleStreamIndex ?? 0;
        var preSubtitleFilters = BuildVideoFilters(
            inputPath,
            profile,
            includeSubtitleFilters: false,
            includeEncoderUpload: false);
        if (preSubtitleFilters.Count == 0)
        {
            preSubtitleFilters.Add("null");
        }

        var postSubtitleFilters = BuildEncoderUploadFilters(profile);
        var builder = new StringBuilder();
        builder.Append("[0:v:0]");
        builder.Append(string.Join(",", preSubtitleFilters));
        builder.Append("[vbase];");
        builder.Append("[vbase][0:s:");
        builder.Append(subtitleStreamIndex);
        builder.Append("]overlay=eof_action=pass:shortest=0");
        if (postSubtitleFilters.Count > 0)
        {
            builder.Append(',');
            builder.Append(string.Join(",", postSubtitleFilters));
        }

        builder.Append("[vout]");
        return builder.ToString();
    }

    private static void AddEncoderUploadFilters(List<string> filters, HlsPlaybackProfile profile)
    {
        if (string.Equals(ResolveHardwareKind(profile.HardwareEncoder), "qsv", StringComparison.OrdinalIgnoreCase))
        {
            filters.Add("format=nv12");
            filters.Add("hwupload=extra_hw_frames=64");
            filters.Add("format=qsv");
        }
        else if (string.Equals(ResolveHardwareKind(profile.HardwareEncoder), "vaapi", StringComparison.OrdinalIgnoreCase))
        {
            filters.Add("format=nv12");
            filters.Add("hwupload");
        }
    }

    private static List<string> BuildEncoderUploadFilters(HlsPlaybackProfile profile)
    {
        List<string> filters = [];
        AddEncoderUploadFilters(filters, profile);
        return filters;
    }

    private static void AddSdrToneMapFilters(List<string> filters, string toneMapMode)
    {
        if (string.Equals(toneMapMode, "dolby-vision", StringComparison.OrdinalIgnoreCase))
        {
            filters.Add("zscale=matrixin=ictcp:transferin=smpte2084:primariesin=bt2020:rangein=tv:matrix=gbr:transfer=linear:primaries=bt2020:npl=100");
            filters.Add("format=gbrpf32le");
            filters.Add("tonemap=tonemap=hable:desat=0");
            filters.Add("zscale=primaries=bt709:transfer=bt709:matrix=bt709:range=tv");
            return;
        }

        filters.Add("zscale=matrixin=bt2020nc:transferin=smpte2084:primariesin=bt2020:rangein=tv:matrix=bt2020nc:transfer=linear:primaries=bt2020:npl=100");
        filters.Add("format=gbrpf32le");
        filters.Add("tonemap=tonemap=hable:desat=0");
        filters.Add("zscale=primaries=bt709:transfer=bt709:matrix=bt709:range=tv");
    }

    private static void AddSubtitleFilters(List<string> filters, string inputPath, HlsPlaybackProfile profile)
    {
        if (!BurnsSubtitle(profile) || BurnsEmbeddedBitmapSubtitle(profile))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(profile.ExternalSubtitlePath))
        {
            filters.Add($"subtitles='{EscapeSubtitlePathForFilter(profile.ExternalSubtitlePath)}'");
        }
        else if (profile.EmbeddedSubtitleStreamIndex.HasValue)
        {
            filters.Add($"subtitles='{EscapeSubtitlePathForFilter(inputPath)}':si={profile.EmbeddedSubtitleStreamIndex.Value}");
        }
    }

    private static bool BurnsSubtitle(HlsPlaybackProfile profile)
    {
        return profile.SubtitleMode.StartsWith("burn", StringComparison.OrdinalIgnoreCase)
               && (!string.IsNullOrWhiteSpace(profile.ExternalSubtitlePath)
                   || profile.EmbeddedSubtitleStreamIndex.HasValue);
    }

    private static bool BurnsEmbeddedBitmapSubtitle(HlsPlaybackProfile profile)
    {
        return profile.EmbeddedSubtitleStreamIndex.HasValue
               && BurnsSubtitle(profile)
               && (string.Equals(profile.SubtitleMode, "burn-bitmap", StringComparison.OrdinalIgnoreCase)
                   || IsBitmapSubtitleCodec(profile.EmbeddedSubtitleCodec));
    }

    private static bool IsBitmapSubtitleCodec(string? codec)
    {
        var normalized = codec?.Trim().ToLowerInvariant().Replace('-', '_');
        return normalized is "hdmv_pgs_subtitle" or "pgs" or "pgssub" or "dvd_subtitle" or "dvdsub" or "dvb_subtitle" or "dvbsub" or "xsub";
    }

    private static string? ResolveHardwareKind(string? codecName)
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
                process.WaitForExit(2000);
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
        return MediaToolPathResolver.ResolveFfmpegPath();
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
