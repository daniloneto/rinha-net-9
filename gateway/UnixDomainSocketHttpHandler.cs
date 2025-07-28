using System.Net.Sockets;
using System.Text;

namespace Gateway;

public class UnixDomainSocketHttpHandler : HttpMessageHandler
{
    private readonly string _socketPath;

    public UnixDomainSocketHttpHandler(string socketPath)
    {
        _socketPath = socketPath;
    }    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var endpoint = new UnixDomainSocketEndPoint(_socketPath);
        await socket.ConnectAsync(endpoint, cancellationToken);

        using var networkStream = new NetworkStream(socket, ownsSocket: true);

        var httpRequest = await BuildHttpRequestAsync(request);
        var requestBytes = Encoding.UTF8.GetBytes(httpRequest);
        await networkStream.WriteAsync(requestBytes, cancellationToken);

        using var memoryStream = new MemoryStream();
        var buffer = new byte[1024];
        while (true)
        {
            var bytesRead = await networkStream.ReadAtLeastAsync(buffer, 1, false, cancellationToken);
            if (bytesRead == 0) break;
            memoryStream.Write(buffer, 0, bytesRead);

            var responseText = Encoding.UTF8.GetString(memoryStream.ToArray());
            if (responseText.Contains("\r\n\r\n") || responseText.Contains("\n\n"))
            {
                var jsonStart = responseText.IndexOf('{');
                if (jsonStart >= 0)
                {
                    var jsonPart = responseText.Substring(jsonStart);
                    var jsonEnd = jsonPart.LastIndexOf('}');
                    if (jsonEnd >= 0)
                    {
                        break;
                    }
                }
            }
            if (memoryStream.Length > 8192) break;
        }
        var fullResponse = Encoding.UTF8.GetString(memoryStream.ToArray());
        return ParseHttpResponse(fullResponse);
    }private async Task<string> BuildHttpRequestAsync(HttpRequestMessage request)
    {
        var sb = new StringBuilder();
        sb.Append($"{request.Method} {request.RequestUri?.PathAndQuery ?? "/"} HTTP/1.1\r\n");
        sb.Append("Host: localhost\r\n");
        sb.Append("Connection: close\r\n");
        
        if (request.Content != null)
        {
            var content = await request.Content.ReadAsStringAsync();
            sb.Append($"Content-Type: {request.Content.Headers.ContentType}\r\n");
            sb.Append($"Content-Length: {Encoding.UTF8.GetByteCount(content)}\r\n");
            sb.Append("\r\n");
            sb.Append(content);
        }
        else
        {
            sb.Append("\r\n");
        }
        
        return sb.ToString();
    }    private HttpResponseMessage ParseHttpResponse(string responseText)
    {
        ReadOnlySpan<char> span = responseText.AsSpan();
        int lineEnd = span.IndexOf("\r\n");
        if (lineEnd < 0) lineEnd = span.IndexOf('\n');
        var statusLine = lineEnd > 0 ? span.Slice(0, lineEnd) : span;

        int firstSpace = statusLine.IndexOf(' ');
        int secondSpace = firstSpace >= 0 ? statusLine.Slice(firstSpace + 1).IndexOf(' ') : -1;
        int statusCode = 0;
        if (firstSpace >= 0 && secondSpace >= 0)
        {
            var codeSpan = statusLine.Slice(firstSpace + 1, secondSpace);
            statusCode = int.Parse(codeSpan);
        }
        else if (firstSpace >= 0)
        {
            var codeSpan = statusLine.Slice(firstSpace + 1);
            int nextSpace = codeSpan.IndexOf(' ');
            if (nextSpace > 0)
                codeSpan = codeSpan.Slice(0, nextSpace);
            statusCode = int.Parse(codeSpan);
        }

        var response = new HttpResponseMessage((System.Net.HttpStatusCode)statusCode);

        int jsonStart = span.IndexOf('{');
        int jsonEnd = span.LastIndexOf('}');
        if (jsonStart >= 0 && jsonEnd >= jsonStart)
        {
            var jsonSpan = span.Slice(jsonStart, jsonEnd - jsonStart + 1);
            var jsonContent = new string(jsonSpan).Replace("\r", "").Replace("\n", "");
            if (!string.IsNullOrWhiteSpace(jsonContent))
            {
                response.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            }
        }
        return response;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }
}
