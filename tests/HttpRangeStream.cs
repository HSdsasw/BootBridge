using System.Net;
using System.Net.Http.Headers;

// Development-only integration helper. Rejects full responses: never downloads a whole ISO.
internal sealed class HttpRangeStream : Stream
{
    private readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(25) };
    private readonly string url;
    private long position;
    private readonly long length;
    internal long BytesRead { get; private set; }
    internal HttpRangeStream(string url)
    {
        this.url = url;
        using var response = Request(0, 1);
        length = response.Content.Headers.ContentRange?.Length ?? throw new IOException("No ISO length in range response");
    }
    private HttpResponseMessage Request(long offset, int count)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(offset, offset + count - 1);
        var response = client.Send(request, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.From != offset)
        { var status = response.StatusCode; response.Dispose(); throw new IOException("Server did not return requested byte range: " + status); }
        return response;
    }
    public override int Read(byte[] buffer, int offset, int count)
    {
        count = (int)Math.Min(count, Length - position);
        if (count == 0) return 0;
        if (count > 16 * 1024 * 1024) throw new IOException("Range exceeds test limit");
        using var response = Request(position, count);
        using var stream = response.Content.ReadAsStream();
        stream.ReadExactly(buffer.AsSpan(offset, count)); position += count; BytesRead += count; return count;
    }
    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => position + offset, _ => length + offset };
    public override long Position { get => position; set { if (value < 0 || value > length) throw new IOException(); position = value; } }
    public override long Length => length;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) client.Dispose(); base.Dispose(disposing); }
}
