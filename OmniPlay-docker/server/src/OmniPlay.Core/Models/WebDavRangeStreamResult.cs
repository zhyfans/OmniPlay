namespace OmniPlay.Core.Models;

public sealed class WebDavRangeStreamResult : IAsyncDisposable, IDisposable
{
    public WebDavRangeStreamResult(
        int statusCode,
        Stream? content,
        string contentType,
        long? contentLength,
        string? contentRange,
        string? errorMessage,
        IDisposable? owner = null)
    {
        StatusCode = statusCode;
        Content = content;
        ContentType = contentType;
        ContentLength = contentLength;
        ContentRange = contentRange;
        ErrorMessage = errorMessage;
        Owner = owner;
    }

    public int StatusCode { get; }

    public Stream? Content { get; }

    public string ContentType { get; }

    public long? ContentLength { get; }

    public string? ContentRange { get; }

    public string? ErrorMessage { get; }

    private IDisposable? Owner { get; }

    public void Dispose()
    {
        Content?.Dispose();
        Owner?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Content is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else
        {
            Content?.Dispose();
        }

        Owner?.Dispose();
    }
}
