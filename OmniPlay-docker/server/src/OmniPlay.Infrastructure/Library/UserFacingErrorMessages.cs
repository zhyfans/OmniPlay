namespace OmniPlay.Infrastructure.Library;

internal static class UserFacingErrorMessages
{
    public static string FromException(Exception exception)
    {
        return FromMessage(exception.Message);
    }

    public static string FromMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "任务执行失败，请稍后重试。";
        }

        if (message.Contains("Received an unexpected EOF or 0 bytes from the transport stream", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unexpected EOF", StringComparison.OrdinalIgnoreCase))
        {
            return "网络连接提前断开（EOF）：通常是代理、TMDB/WebDAV 目标服务或中间网络中断导致，请检查代理、DNS/Host 和目标服务后重试。";
        }

        if (message.Contains("The SSL connection could not be established", StringComparison.OrdinalIgnoreCase))
        {
            return "SSL/TLS 连接建立失败：请检查系统时间、证书、代理或目标站点连通性。";
        }

        if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || message.Contains("The operation has timed out", StringComparison.OrdinalIgnoreCase)
            || message.Contains("TaskCanceledException", StringComparison.OrdinalIgnoreCase))
        {
            return "网络请求超时：请检查 TMDB/WebDAV 连通性、代理速度或稍后重试。";
        }

        return message;
    }
}
