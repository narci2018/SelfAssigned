using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AltServer.Windows.Services;

/// <summary>
/// 本地HTTP服务器：运行在127.0.0.1:27000，供iOS端AltStore客户端通信
/// 使用原始Socket实现，避免HttpListener的URL ACL管理员权限要求
/// </summary>
public class HttpServer : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private bool _running;

    public event Func<string, string, string?, string>? RequestReceived;

    public Action<string, Func<dynamic>>? OnRequest { get; set; }

    public int Port { get; }

    public HttpServer(int port = 27000)
    {
        Port = port;
    }

    /// <summary>启动服务器</summary>
    public void Start()
    {
        if (_running) return;

        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();

        _cts = new CancellationTokenSource();
        _running = true;

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        LogService.Info($"HTTP服务器已启动，监听端口 {Port}");
    }

    public void Stop()
    {
        _running = false;
        _cts?.Cancel();
        _listener?.Stop();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (_running && !ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogService.Error($"接受连接失败: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

                // 读取HTTP请求
                var request = await ReadRequestAsync(stream, timeoutCts.Token);
                if (request is null) return;

                LogService.Info($"{request.Method} {request.Path}");

                // 路由处理
                var response = Route(request);

                // 发送响应
                var responseBytes = response.ToBytes();
                await stream.WriteAsync(responseBytes, timeoutCts.Token);
                await stream.FlushAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogService.Error($"处理请求失败: {ex.Message}");
            }
        }
    }

    private HttpJsonRequest? ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        // 简单HTTP请求读取：解析请求行、头、body
        var sb = new StringBuilder();
        var buffer = new byte[4096];
        int headerEnd = -1;
        byte[]? bodyBytes = null;

        var ms = new MemoryStream();

        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ms.Write(buffer, 0, read);

            // 查找 header/body 分隔符 \r\n\r\n
            var headEnd = FindPattern(ms.GetBuffer(), 0, (int)ms.Length, "\r\n\r\n"u8.ToArray());
            if (headEnd >= 0)
            {
                headerEnd = headEnd;
                break;
            }
        }

        if (headerEnd < 0) return null;

        var headerBytes = new byte[headerEnd];
        Array.Copy(ms.GetBuffer(), headerBytes, headerEnd);
        var headerText = Encoding.UTF8.GetString(headerBytes);
        var headerLines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        if (headerLines.Length == 0) return null;

        var requestLine = headerLines[0].Split(' ');
        if (requestLine.Length < 2) return null;

        var method = requestLine[0];
        var path = requestLine[1];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int contentLength = 0;

        for (int i = 1; i < headerLines.Length; i++)
        {
            var parts = headerLines[i].Split(':', 2);
            if (parts.Length != 2) continue;

            var key = parts[0].Trim();
            var value = parts[1].Trim();
            headers[key] = value;

            if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(value, out contentLength);
            }
        }

        // 读取Body
        if (contentLength > 0)
        {
            var totalBodyLength = (int)ms.Length - headerEnd - 4; // 减去 \r\n\r\n
            if (totalBodyLength < contentLength)
            {
                var remaining = contentLength - totalBodyLength;
                var rest = new byte[remaining];
                var toRead = remaining;
                while (toRead > 0)
                {
                    read = stream.Read(rest, remaining - toRead, toRead);
                    if (read <= 0) break;
                    ms.Write(rest, 0, read);
                    toRead -= read;
                }
            }

            var bodyStart = headerEnd + 4;
            bodyBytes = new byte[contentLength];
            var totalAvailable = (int)ms.Length - bodyStart;
            if (totalAvailable > 0)
            {
                Array.Copy(ms.GetBuffer(), bodyStart, bodyBytes, 0, Math.Min(contentLength, totalAvailable));
            }
        }

        return new HttpJsonRequest(method, path, headers, bodyBytes);
    }

    private static int FindPattern(byte[] buffer, int start, int length, byte[] pattern)
    {
        for (int i = start; i <= length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (buffer[i + j] != pattern[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    private HttpJsonResponse Route(HttpJsonRequest request)
    {
        var path = request.Path;
        var queryIndex = path.IndexOf('?');
        if (queryIndex >= 0) path = path.Substring(0, queryIndex);

        return path switch
        {
            "/status" => HandleStatus(),
            "/devices" => HandleDevices(),
            "/install" => HandleInstall(request),
            "/uninstall" => HandleUninstall(request),
            "/refresh" => HandleRefresh(request),
            "/apps" => HandleApps(request),
            "/certificates" => HandleCertificates(),
            _ => HttpJsonResponse.Error(404, "Not Found")
        };
    }

    // MARK: - 处理器注入

    public Func<HttpJsonResponse>? StatusHandler { get; set; }
    public Func<HttpJsonResponse>? DevicesHandler { get; set; }
    public Func<HttpJsonRequest, HttpJsonResponse>? InstallHandler { get; set; }
    public Func<HttpJsonRequest, HttpJsonResponse>? UninstallHandler { get; set; }
    public Func<HttpJsonRequest, HttpJsonResponse>? RefreshHandler { get; set; }
    public Func<HttpJsonRequest, HttpJsonResponse>? AppsHandler { get; set; }
    public Func<HttpJsonResponse>? CertificatesHandler { get; set; }

    private HttpJsonResponse HandleStatus()
    {
        if (StatusHandler is not null) return StatusHandler();
        return HttpJsonResponse.Ok(new { version = AppVersion.Version, status = "running" });
    }

    private HttpJsonResponse HandleDevices()
    {
        if (DevicesHandler is not null) return DevicesHandler();
        return HttpJsonResponse.Ok(new { devices = Array.Empty<object>() });
    }

    private HttpJsonResponse HandleInstall(HttpJsonRequest request)
    {
        if (InstallHandler is not null) return InstallHandler(request);
        return HttpJsonResponse.Error(400, "Install handler not configured");
    }

    private HttpJsonResponse HandleUninstall(HttpJsonRequest request)
    {
        if (UninstallHandler is not null) return UninstallHandler(request);
        return HttpJsonResponse.Error(400, "Uninstall handler not configured");
    }

    private HttpJsonResponse HandleRefresh(HttpJsonRequest request)
    {
        if (RefreshHandler is not null) return RefreshHandler(request);
        return HttpJsonResponse.Error(400, "Refresh handler not configured");
    }

    private HttpJsonResponse HandleApps(HttpJsonRequest request)
    {
        if (AppsHandler is not null) return AppsHandler(request);
        return HttpJsonResponse.Ok(new { apps = Array.Empty<object>() });
    }

    private HttpJsonResponse HandleCertificates()
    {
        if (CertificatesHandler is not null) return CertificatesHandler();
        return HttpJsonResponse.Ok(new { certificates = Array.Empty<object>() });
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _listener?.Dispose();
    }
}

// MARK: - HTTP 消息模型

public class HttpJsonRequest
{
    public string Method { get; }
    public string Path { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public byte[]? Body { get; }

    public HttpJsonRequest(string method, string path, IReadOnlyDictionary<string, string> headers, byte[]? body)
    {
        Method = method;
        Path = path;
        Headers = headers;
        Body = body;
    }

    public JsonElement? GetBodyJson()
    {
        if (Body is null || Body.Length == 0) return null;

        try
        {
            using var doc = JsonDocument.Parse(Body);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }
}

public class HttpJsonResponse
{
    public int StatusCode { get; set; } = 200;
    public string ContentType { get; set; } = "application/json";
    public byte[]? Body { get; set; }
    public Dictionary<string, string> Headers { get; } = new();

    public static HttpJsonResponse Ok(object? obj = null)
    {
        return new HttpJsonResponse
        {
            StatusCode = 200,
            Body = obj is null ? Encoding.UTF8.GetBytes("{\"status\":\"ok\"}") : JsonSerializer.SerializeToUtf8Bytes(obj)
        };
    }

    public static HttpJsonResponse Error(int statusCode, string message)
    {
        return new HttpJsonResponse
        {
            StatusCode = statusCode,
            Body = JsonSerializer.SerializeToUtf8Bytes(new { error = message })
        };
    }

    public byte[] ToBytes()
    {
        var statusText = StatusCode switch
        {
            200 => "OK",
            201 => "Created",
            204 => "No Content",
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            405 => "Method Not Allowed",
            500 => "Internal Server Error",
            _ => "Unknown"
        };

        var sb = new StringBuilder();
        sb.AppendLine($"HTTP/1.1 {StatusCode} {statusText}");
        sb.AppendLine($"Content-Type: {ContentType}");
        sb.AppendLine($"Content-Length: {Body?.Length ?? 0}");
        sb.AppendLine("Connection: keep-alive");
        foreach (var (key, value) in Headers)
        {
            sb.AppendLine($"{key}: {value}");
        }
        sb.AppendLine();

        var headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
        if (Body is null || Body.Length == 0)
        {
            return headerBytes;
        }

        var result = new byte[headerBytes.Length + Body.Length];
        Array.Copy(headerBytes, 0, result, 0, headerBytes.Length);
        Array.Copy(Body, 0, result, headerBytes.Length, Body.Length);
        return result;
    }
}

public static class AppVersion
{
    public const string Version = "1.0.0";
}