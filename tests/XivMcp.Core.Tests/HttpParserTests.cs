using System.Buffers;
using System.Text;
using XivMcp.Core.Http;

namespace XivMcp.Core.Tests;

public class HttpParserTests
{
    private static readonly HttpLimits Limits = new() { MaxHeaderBytes = 1024, MaxHeaderCount = 10, MaxBodyBytes = 100 };

    private static (ParseStatus Status, HttpRequest? Request, int Consumed, HttpError Error) Parse(string text, HttpLimits? limits = null)
    {
        var status = HttpRequestParser.TryParseHead(Encoding.Latin1.GetBytes(text), limits ?? Limits, out var request, out var consumed, out var error);
        return (status, request, consumed, error);
    }

    [Fact]
    public void ParsesSimpleRequest()
    {
        var raw = "POST /mcp?x=1 HTTP/1.1\r\nHost: 127.0.0.1:41800\r\nContent-Length: 5\r\nX-Test:   padded value  \r\n\r\nhello";
        var (status, request, consumed, _) = Parse(raw);
        Assert.Equal(ParseStatus.Complete, status);
        Assert.NotNull(request);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/mcp", request.Path);
        Assert.Equal("x=1", request.Query);
        Assert.True(request.IsHttp11);
        Assert.True(request.KeepAlive);
        Assert.Equal(5, request.ContentLength);
        Assert.Equal("padded value", request.Headers.Get("x-test"));
        Assert.Equal(raw.Length - 5, consumed);
    }

    [Fact]
    public void AcceptsBareLineFeedsAndLeadingEmptyLine()
    {
        var (status, request, _, _) = Parse("\r\nGET /mcp HTTP/1.1\nHost: a\n\n");
        Assert.Equal(ParseStatus.Complete, status);
        Assert.Equal("GET", request!.Method);
    }

    [Theory]
    [InlineData("GET /mcp HTTP/1.1\r\nHost: a\r\n")]
    [InlineData("GET /mcp HTT")]
    [InlineData("")]
    public void IncompleteHeadNeedsMore(string raw) => Assert.Equal(ParseStatus.NeedMore, Parse(raw).Status);

    [Theory]
    [InlineData("GET /mcp HTTP/1.1\r\n\r\n", 400)] // no Host
    [InlineData("GET /mcp HTTP/1.1\r\nHost: a\r\nHost: b\r\n\r\n", 400)]
    [InlineData("GET  /mcp HTTP/1.1\r\nHost: a\r\n\r\n", 400)]
    [InlineData("G(T /mcp HTTP/1.1\r\nHost: a\r\n\r\n", 400)]
    [InlineData("GET /mcp HTTP/2.0\r\nHost: a\r\n\r\n", 505)]
    [InlineData("GET /mcp FTP/1.1\r\nHost: a\r\n\r\n", 400)]
    [InlineData("GET mcp HTTP/1.1\r\nHost: a\r\n\r\n", 400)]
    [InlineData("GET /mcp HTTP/1.1\r\nHost: a\r\n folded\r\n\r\n", 400)]
    [InlineData("GET /mcp HTTP/1.1\r\nHost : a\r\n\r\n", 400)]
    [InlineData("GET /mcp HTTP/1.1\r\nHost: a\r\nBad\x01Value: x\r\n\r\n", 400)]
    [InlineData("GET /mcp HTTP/1.1\r\nHost: a\r\nX: a\x00b\r\n\r\n", 400)]
    [InlineData("POST /mcp HTTP/1.1\r\nHost: a\r\nContent-Length: 5\r\nTransfer-Encoding: chunked\r\n\r\n", 400)]
    [InlineData("POST /mcp HTTP/1.1\r\nHost: a\r\nContent-Length: 5\r\nContent-Length: 6\r\n\r\n", 400)]
    [InlineData("POST /mcp HTTP/1.1\r\nHost: a\r\nContent-Length: -1\r\n\r\n", 400)]
    [InlineData("POST /mcp HTTP/1.1\r\nHost: a\r\nContent-Length: 1e3\r\n\r\n", 400)]
    [InlineData("POST /mcp HTTP/1.1\r\nHost: a\r\nContent-Length: 101\r\n\r\n", 413)]
    [InlineData("POST /mcp HTTP/1.1\r\nHost: a\r\nTransfer-Encoding: gzip, chunked\r\n\r\n", 501)]
    [InlineData("POST /mcp HTTP/1.0\r\nTransfer-Encoding: chunked\r\n\r\n", 400)]
    [InlineData("POST /mcp HTTP/1.1\r\nHost: a\r\nExpect: something-else\r\n\r\n", 417)]
    public void RejectsMalformedOrAmbiguousRequests(string raw, int expectedStatus)
    {
        var (status, _, _, error) = Parse(raw);
        Assert.Equal(ParseStatus.Error, status);
        Assert.Equal(expectedStatus, error.Status);
    }

    [Fact]
    public void RepeatedIdenticalContentLengthIsAccepted()
    {
        var (status, request, _, _) = Parse("POST /mcp HTTP/1.1\r\nHost: a\r\nContent-Length: 7, 7\r\n\r\n");
        Assert.Equal(ParseStatus.Complete, status);
        Assert.Equal(7, request!.ContentLength);
    }

    [Fact]
    public void HeaderSizeAndCountLimits()
    {
        var big = "GET /mcp HTTP/1.1\r\nHost: a\r\nX: " + new string('a', 2000) + "\r\n\r\n";
        Assert.Equal(431, Parse(big).Error.Status);

        var noNewline = "GET /mcp HTTP/1.1\r\nHost: a\r\nX: " + new string('a', 2000);
        Assert.Equal(431, Parse(noNewline).Error.Status);

        var many = new StringBuilder("GET /mcp HTTP/1.1\r\nHost: a\r\n");
        for (var i = 0; i < 20; i++)
            many.Append("X-").Append(i).Append(": v\r\n");
        many.Append("\r\n");
        Assert.Equal(431, Parse(many.ToString()).Error.Status);
    }

    [Fact]
    public void ConnectionSemantics()
    {
        Assert.False(Parse("GET /mcp HTTP/1.1\r\nHost: a\r\nConnection: close\r\n\r\n").Request!.KeepAlive);
        Assert.False(Parse("GET /mcp HTTP/1.0\r\n\r\n").Request!.KeepAlive);
        Assert.True(Parse("GET /mcp HTTP/1.0\r\nConnection: Keep-Alive\r\n\r\n").Request!.KeepAlive);
        Assert.True(Parse("POST /mcp HTTP/1.1\r\nHost: a\r\nExpect: 100-continue\r\nContent-Length: 1\r\n\r\n").Request!.ExpectContinue);
    }

    [Fact]
    public void AbsoluteFormTargetIsReducedToPath()
    {
        var request = Parse("POST http://127.0.0.1:41800/mcp?q HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n").Request!;
        Assert.Equal("/mcp", request.Path);
        Assert.Equal("q", request.Query);
    }

    private static (ParseStatus Status, string Body, HttpError Error) DecodeChunked(string raw, int maxBody = 100, bool byteByByte = false)
    {
        var decoder = new ChunkedDecoder(maxBody);
        var output = new ArrayBufferWriter<byte>();
        var bytes = Encoding.Latin1.GetBytes(raw);
        var pending = new List<byte>();
        var status = ParseStatus.NeedMore;
        HttpError error = default;
        foreach (var chunk in byteByByte ? bytes.Select(b => new[] { b }) : [bytes])
        {
            pending.AddRange(chunk);
            status = decoder.Decode(pending.ToArray(), output, out var consumed, out error);
            pending.RemoveRange(0, consumed);
            if (status != ParseStatus.NeedMore)
                break;
        }

        return (status, Encoding.Latin1.GetString(output.WrittenSpan), error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecodesChunkedBodies(bool byteByByte)
    {
        var raw = "5\r\nhello\r\n6;ext=1\r\n world\r\nA \r\n0123456789\r\n0\r\nTrailer: x\r\n\r\n";
        var (status, body, _) = DecodeChunked(raw, byteByByte: byteByByte);
        Assert.Equal(ParseStatus.Complete, status);
        Assert.Equal("hello world0123456789", body);
    }

    [Theory]
    [InlineData("z\r\nhello\r\n0\r\n\r\n", 400)]
    [InlineData("5\r\nhelloXX0\r\n\r\n", 400)]
    [InlineData("\r\n", 400)]
    [InlineData("FFFFFFFFFFFFFFFF\r\n", 400)]
    [InlineData("65\r\n", 413)]
    public void RejectsBadChunks(string raw, int expected)
    {
        var (status, _, error) = DecodeChunked(raw);
        Assert.Equal(ParseStatus.Error, status);
        Assert.Equal(expected, error.Status);
    }

    [Fact]
    public void ChunkedTotalSizeIsLimited()
    {
        var raw = "32\r\n" + new string('a', 50) + "\r\n32\r\n" + new string('b', 50) + "\r\n1\r\nc\r\n0\r\n\r\n";
        var (status, _, error) = DecodeChunked(raw, maxBody: 100);
        Assert.Equal(ParseStatus.Error, status);
        Assert.Equal(413, error.Status);
    }
}
