using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniPlay.Core.Interfaces;
using OmniPlay.Core.Models;

namespace OmniPlay.Media;

public sealed class FfprobeMediaProbeService : IMediaProbeService
{
    private readonly string ffprobePath;
    private readonly TimeSpan timeout;

    public FfprobeMediaProbeService()
        : this(ResolveFfprobePath(), TimeSpan.FromSeconds(20))
    {
    }

    public FfprobeMediaProbeService(string ffprobePath, TimeSpan? timeout = null)
    {
        this.ffprobePath = string.IsNullOrWhiteSpace(ffprobePath) ? "ffprobe" : ffprobePath;
        this.timeout = timeout ?? TimeSpan.FromSeconds(20);
    }

    public async Task<MediaProbeSnapshot?> ProbeAsync(string filePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var argument in new[]
                 {
                     "-v", "error",
                     "-print_format", "json",
                     "-show_format",
                     "-show_streams",
                     filePath
                 })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
            var stdout = await stdoutTask;
            _ = await stderrTask;
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                return null;
            }

            var result = JsonSerializer.Deserialize<FfprobeResult>(
                stdout,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return result is null ? null : ToSnapshot(filePath, stdout, result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MediaProbeSnapshot ToSnapshot(string filePath, string rawJson, FfprobeResult result)
    {
        var streams = result.Streams ?? [];
        var video = streams.FirstOrDefault(static stream => stream.CodecType == "video");
        var audio = streams.FirstOrDefault(static stream => stream.CodecType == "audio");
        var subtitles = streams
            .Where(static stream => stream.CodecType == "subtitle")
            .Select(static stream => stream.CodecName)
            .Where(static codec => !string.IsNullOrWhiteSpace(codec))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new MediaProbeSnapshot(
            filePath,
            ParseDouble(result.Format?.Duration) ?? ParseDouble(video?.Duration) ?? 0,
            result.Format?.FormatName,
            video?.CodecName,
            audio?.CodecName,
            subtitles.Length == 0 ? null : string.Join(",", subtitles),
            rawJson,
            streams.Select(ToStreamSnapshot).ToArray());
    }

    private static MediaStreamSnapshot ToStreamSnapshot(FfprobeStream stream)
    {
        return new MediaStreamSnapshot(
            stream.Index,
            stream.CodecType ?? string.Empty,
            stream.CodecName,
            ReadTag(stream.Tags, "language"),
            ReadTag(stream.Tags, "title"),
            stream.Channels,
            stream.ChannelLayout,
            ReadDisposition(stream.Disposition, "default"),
            ReadDisposition(stream.Disposition, "forced"));
    }

    private static double? ParseDouble(string? value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Probing failure should not fail playback decision fallback.
        }
    }

    private static string ResolveFfprobePath()
    {
        return Environment.GetEnvironmentVariable("OMNIPLAY_FFPROBE_PATH") ?? "ffprobe";
    }

    private sealed record FfprobeResult(
        [property: JsonPropertyName("streams")] IReadOnlyList<FfprobeStream>? Streams,
        [property: JsonPropertyName("format")] FfprobeFormat? Format);

    private sealed record FfprobeStream(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("codec_type")] string? CodecType,
        [property: JsonPropertyName("codec_name")] string? CodecName,
        [property: JsonPropertyName("duration")] string? Duration,
        [property: JsonPropertyName("channels")] int? Channels,
        [property: JsonPropertyName("channel_layout")] string? ChannelLayout,
        [property: JsonPropertyName("tags")] IReadOnlyDictionary<string, string>? Tags,
        [property: JsonPropertyName("disposition")] IReadOnlyDictionary<string, int>? Disposition);

    private sealed record FfprobeFormat(
        [property: JsonPropertyName("format_name")] string? FormatName,
        [property: JsonPropertyName("duration")] string? Duration);

    private static string? ReadTag(IReadOnlyDictionary<string, string>? tags, string key)
    {
        if (tags is null)
        {
            return null;
        }

        return tags.TryGetValue(key, out var value)
               || tags.TryGetValue(key.ToUpperInvariant(), out value)
            ? value
            : null;
    }

    private static bool ReadDisposition(IReadOnlyDictionary<string, int>? disposition, string key)
    {
        return disposition is not null
               && disposition.TryGetValue(key, out var value)
               && value == 1;
    }
}
