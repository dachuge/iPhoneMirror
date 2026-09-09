using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Services;

/// <summary>
/// Optional loopback-only command bridge for trusted local automation.
/// The server is disabled unless IPHONE_MIRROR_CONTROL_TOKEN is set.
/// </summary>
internal sealed class LocalControlServer : IAsyncDisposable
{
    private const string TokenEnvironmentVariable = "IPHONE_MIRROR_CONTROL_TOKEN";
    private const string PortEnvironmentVariable = "IPHONE_MIRROR_CONTROL_PORT";
    private const int DefaultPort = 17321;
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 64 * 1024;
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(5);

    private readonly MainViewModel _target;
    private readonly string _token;
    private readonly int _port;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private Task? _acceptLoop;
    private int _disposed;

    private LocalControlServer(MainViewModel target, string token, int port)
    {
        _target = target;
        _token = token;
        _port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    internal static LocalControlServer? TryStart(MainViewModel target)
    {
        var token = Environment.GetEnvironmentVariable(TokenEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(token)) return null;
        token = token.Trim();
        if (Encoding.UTF8.GetByteCount(token) > 256)
        {
            target.AddDiagnosticLog(AppLog.Event("local_control_start_failed",
                ("error", $"{TokenEnvironmentVariable} is longer than 256 UTF-8 bytes.")));
            return null;
        }

        var port = DefaultPort;
        var configuredPort = Environment.GetEnvironmentVariable(PortEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredPort) &&
            (!int.TryParse(configuredPort, out port) || port is < 1024 or > 65535))
        {
            target.AddDiagnosticLog(AppLog.Event("local_control_start_failed",
                ("error", $"{PortEnvironmentVariable} must be between 1024 and 65535.")));
            return null;
        }

        var server = new LocalControlServer(target, token, port);
        try
        {
            server._listener.Start();
            server._acceptLoop = server.AcceptLoopAsync();
            target.AddDiagnosticLog(AppLog.Event("local_control_started",
                ("address", $"127.0.0.1:{port}")));
            return server;
        }
        catch (Exception error)
        {
            server._listener.Stop();
            target.AddDiagnosticLog(AppLog.Event("local_control_start_failed",
                ("error", AppLog.Error(error)), ("port", port)));
            return null;
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_shutdown.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                _target.AddDiagnosticLog(AppLog.Event("local_control_accept_failed",
                    ("error", AppLog.Error(error))));
                if (_shutdown.IsCancellationRequested) return;
                continue;
            }

            using (client)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                       _shutdown.Token))
            {
                timeout.CancelAfter(ClientTimeout);
                try { await HandleClientAsync(client, timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception error)
                {
                    _target.AddDiagnosticLog(AppLog.Event("local_control_request_failed",
                        ("error", AppLog.Error(error))));
                }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        client.NoDelay = true;
        await using var stream = client.GetStream();
        var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            await WriteJsonAsync(stream, 400, new { ok = false, error = "invalid_request" },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!TokenMatches(request.Headers))
        {
            await WriteJsonAsync(stream, 401, new { ok = false, error = "unauthorized" },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && request.Path == "/v1/status")
        {
            await WriteJsonAsync(stream, 200, new
            {
                ok = true,
                ready = _target.BluetoothControlIsInputEnabled,
                enabled = _target.IsBluetoothControlEnabled,
                connected = _target.BluetoothControlIsConnected,
                hasTarget = !string.IsNullOrWhiteSpace(_target.BluetoothControlTargetUdid),
                port = _port,
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method != "POST")
        {
            await WriteJsonAsync(stream, 405, new { ok = false, error = "method_not_allowed" },
                cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!_target.BluetoothControlIsInputEnabled)
        {
            await WriteJsonAsync(stream, 409, new
            {
                ok = false,
                error = "bluetooth_control_not_ready",
                hint = "Enable Bluetooth reverse control and finish pairing/binding first.",
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        JsonDocument? body = null;
        try
        {
            body = request.Body.Length == 0
                ? JsonDocument.Parse("{}")
                : JsonDocument.Parse(request.Body);
            await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await DispatchAsync(request.Path, body.RootElement, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally { _commandGate.Release(); }
            await WriteJsonAsync(stream, 200, new { ok = true }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await WriteJsonAsync(stream, 400, new { ok = false, error = "invalid_json" },
                cancellationToken).ConfigureAwait(false);
        }
        catch (LocalControlException error)
        {
            await WriteJsonAsync(stream, error.StatusCode,
                new { ok = false, error = error.Code, message = error.Message }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _target.AddDiagnosticLog(AppLog.Event("local_control_command_failed",
                ("path", request.Path), ("error", AppLog.Error(error))));
            await WriteJsonAsync(stream, 500,
                new { ok = false, error = "command_failed" }, cancellationToken)
                .ConfigureAwait(false);
        }
        finally { body?.Dispose(); }
    }

    private async Task DispatchAsync(string path, JsonElement body,
        CancellationToken cancellationToken)
    {
        switch (path)
        {
            case "/v1/mouse/move":
            {
                var dx = RequiredInt(body, "dx", short.MinValue + 1, short.MaxValue);
                var dy = RequiredInt(body, "dy", short.MinValue + 1, short.MaxValue);
                await _target.SendBluetoothMouseAsync(dx, dy).ConfigureAwait(false);
                return;
            }
            case "/v1/mouse/click":
            {
                var buttonName = OptionalString(body, "button") ?? "left";
                var button = buttonName.ToLowerInvariant() switch
                {
                    "left" => (byte)1,
                    "right" => (byte)2,
                    "middle" => (byte)4,
                    _ => throw BadRequest("invalid_button", "button must be left, right, or middle."),
                };
                var count = OptionalInt(body, "count", 1, 3) ?? 1;
                var holdMs = OptionalInt(body, "holdMs", 10, 1000) ?? 40;
                for (var i = 0; i < count; i++)
                {
                    await _target.SendBluetoothMouseAsync(0, 0, button).ConfigureAwait(false);
                    await Task.Delay(holdMs, cancellationToken).ConfigureAwait(false);
                    await _target.SendBluetoothMouseAsync(0, 0).ConfigureAwait(false);
                    if (i + 1 < count)
                        await Task.Delay(80, cancellationToken).ConfigureAwait(false);
                }
                return;
            }
            case "/v1/mouse/scroll":
            {
                var remaining = RequiredInt(body, "delta", -1200, 1200);
                while (remaining != 0)
                {
                    var chunk = Math.Clamp(remaining, -127, 127);
                    await _target.SendBluetoothMouseAsync(0, 0, 0, chunk)
                        .ConfigureAwait(false);
                    remaining -= chunk;
                }
                return;
            }
            case "/v1/keyboard/report":
            {
                var modifiers = (byte)(OptionalInt(body, "modifiers", 0, 255) ?? 0);
                var usages = UsageArray(body);
                await _target.SendBluetoothKeyboardAsync(modifiers, usages).ConfigureAwait(false);
                return;
            }
            case "/v1/keyboard/key":
            {
                var modifiers = (byte)(OptionalInt(body, "modifiers", 0, 255) ?? 0);
                var usage = (byte)RequiredInt(body, "usage", 1, 255);
                var holdMs = OptionalInt(body, "holdMs", 5, 1000) ?? 30;
                await _target.SendBluetoothKeyboardAsync(modifiers, [usage]).ConfigureAwait(false);
                await Task.Delay(holdMs, cancellationToken).ConfigureAwait(false);
                await _target.SendBluetoothKeyboardAsync(0, []).ConfigureAwait(false);
                return;
            }
            case "/v1/keyboard/text":
            {
                var text = RequiredString(body, "text");
                if (text.Length > 4096)
                    throw BadRequest("text_too_long", "text is limited to 4096 characters.");
                var keys = new HidTextKey[text.Length];
                for (var i = 0; i < text.Length; i++)
                {
                    if (!HidKeyboardTextEncoder.TryEncode(text[i], out keys[i]))
                        throw BadRequest("unsupported_character",
                            $"Character at index {i} is not supported by the HID text endpoint. " +
                            "Use ASCII text or send raw HID usages through /v1/keyboard/report.");
                }
                foreach (var key in keys)
                {
                    await _target.SendBluetoothKeyboardAsync(key.Modifiers, [key.Usage])
                        .ConfigureAwait(false);
                    await _target.SendBluetoothKeyboardAsync(0, []).ConfigureAwait(false);
                }
                return;
            }
            case "/v1/system":
            {
                var action = RequiredString(body, "action").ToLowerInvariant();
                if (action == "app-switcher")
                {
                    await _target.SendBluetoothAppSwitcherAsync().ConfigureAwait(false);
                    return;
                }
                var usage = action switch
                {
                    "home" => (byte)0x0B,
                    "control-center" => (byte)0x06,
                    "notification-center" => (byte)0x11,
                    "dock" => (byte)0x04,
                    "siri" => (byte)0x16,
                    _ => throw BadRequest("invalid_system_action",
                        "action must be home, app-switcher, control-center, " +
                        "notification-center, dock, or siri."),
                };
                await _target.SendBluetoothSystemShortcutAsync(usage).ConfigureAwait(false);
                return;
            }
            default:
                throw new LocalControlException(404, "not_found", "Unknown control endpoint.");
        }
    }

    private bool TokenMatches(IReadOnlyDictionary<string, string> headers)
    {
        headers.TryGetValue("X-iPhoneMirror-Token", out var supplied);
        if (headers.TryGetValue("Authorization", out var authorization) &&
            authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            supplied = authorization[7..];
        if (supplied is null) return false;
        var expectedBytes = Encoding.UTF8.GetBytes(_token);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream,
        CancellationToken cancellationToken)
    {
        using var received = new MemoryStream();
        var buffer = new byte[4096];
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return null;
            received.Write(buffer, 0, read);
            if (received.Length > MaxHeaderBytes) return null;
            headerEnd = FindHeaderEnd(received.GetBuffer(), (int)received.Length);
        }

        var all = received.GetBuffer();
        var headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.",
                StringComparison.Ordinal)) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) return null;
            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        var contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var lengthText) &&
            (!int.TryParse(lengthText, out contentLength) || contentLength < 0 ||
             contentLength > MaxBodyBytes)) return null;
        var bodyOffset = headerEnd + 4;
        var body = new byte[contentLength];
        var copied = Math.Min(contentLength, (int)received.Length - bodyOffset);
        if (copied > 0) Buffer.BlockCopy(all, bodyOffset, body, 0, copied);
        while (copied < contentLength)
        {
            var read = await stream.ReadAsync(body.AsMemory(copied, contentLength - copied),
                cancellationToken).ConfigureAwait(false);
            if (read == 0) return null;
            copied += read;
        }

        var path = requestLine[1].Split('?', 2)[0];
        return new HttpRequest(requestLine[0].ToUpperInvariant(), path, headers, body);
    }

    private static int FindHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 0; i <= length - 4; i++)
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' &&
                buffer[i + 2] == '\r' && buffer[i + 3] == '\n') return i;
        return -1;
    }

    private static async Task WriteJsonAsync(NetworkStream stream, int statusCode,
        object value, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value);
        var reason = statusCode switch
        {
            200 => "OK", 400 => "Bad Request", 401 => "Unauthorized",
            404 => "Not Found", 405 => "Method Not Allowed", 409 => "Conflict",
            500 => "Internal Server Error",
            _ => "Error",
        };
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {reason}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    private static int RequiredInt(JsonElement body, string name, int minimum, int maximum) =>
        OptionalInt(body, name, minimum, maximum) ??
        throw BadRequest("missing_field", $"{name} is required.");

    private static int? OptionalInt(JsonElement body, string name, int minimum, int maximum)
    {
        if (!body.TryGetProperty(name, out var value)) return null;
        if (!value.TryGetInt32(out var result) || result < minimum || result > maximum)
            throw BadRequest("invalid_field", $"{name} must be an integer from {minimum} to {maximum}.");
        return result;
    }

    private static string RequiredString(JsonElement body, string name) =>
        OptionalString(body, name) ?? throw BadRequest("missing_field", $"{name} is required.");

    private static string? OptionalString(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw BadRequest("invalid_field", $"{name} must be a string.");
        return value.GetString();
    }

    private static byte[] UsageArray(JsonElement body)
    {
        if (!body.TryGetProperty("usages", out var value))
            return [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 6)
            throw BadRequest("invalid_field", "usages must be an array of at most 6 HID usage bytes.");
        var usages = new List<byte>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            if (!item.TryGetByte(out var usage))
                throw BadRequest("invalid_field", "Every usage must be an integer from 0 to 255.");
            usages.Add(usage);
        }
        return usages.ToArray();
    }

    private static LocalControlException BadRequest(string code, string message) =>
        new(400, code, message);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _commandGate.Dispose();
        _shutdown.Dispose();
        _target.AddDiagnosticLog(AppLog.Event("local_control_stopped"));
    }

    private sealed record HttpRequest(string Method, string Path,
        IReadOnlyDictionary<string, string> Headers, byte[] Body);

    private sealed class LocalControlException(int statusCode, string code, string message)
        : Exception(message)
    {
        internal int StatusCode { get; } = statusCode;
        internal string Code { get; } = code;
    }
}

internal readonly record struct HidTextKey(byte Modifiers, byte Usage);

internal static class HidKeyboardTextEncoder
{
    private const byte LeftShift = 0x02;

    internal static bool TryEncode(char character, out HidTextKey key)
    {
        if (character is >= 'a' and <= 'z')
        {
            key = new HidTextKey(0, (byte)(character - 'a' + 4));
            return true;
        }
        if (character is >= 'A' and <= 'Z')
        {
            key = new HidTextKey(LeftShift, (byte)(character - 'A' + 4));
            return true;
        }
        if (character is >= '1' and <= '9')
        {
            key = new HidTextKey(0, (byte)(character - '1' + 30));
            return true;
        }
        if (character == '0')
        {
            key = new HidTextKey(0, 39);
            return true;
        }

        key = character switch
        {
            ' ' => new(0, 0x2C), '\n' or '\r' => new(0, 0x28), '\t' => new(0, 0x2B),
            '\b' => new(0, 0x2A), '-' => new(0, 0x2D), '_' => new(LeftShift, 0x2D),
            '=' => new(0, 0x2E), '+' => new(LeftShift, 0x2E), '[' => new(0, 0x2F),
            '{' => new(LeftShift, 0x2F), ']' => new(0, 0x30), '}' => new(LeftShift, 0x30),
            '\\' => new(0, 0x31), '|' => new(LeftShift, 0x31), ';' => new(0, 0x33),
            ':' => new(LeftShift, 0x33), '\'' => new(0, 0x34), '"' => new(LeftShift, 0x34),
            '`' => new(0, 0x35), '~' => new(LeftShift, 0x35), ',' => new(0, 0x36),
            '<' => new(LeftShift, 0x36), '.' => new(0, 0x37), '>' => new(LeftShift, 0x37),
            '/' => new(0, 0x38), '?' => new(LeftShift, 0x38), '!' => new(LeftShift, 30),
            '@' => new(LeftShift, 31), '#' => new(LeftShift, 32), '$' => new(LeftShift, 33),
            '%' => new(LeftShift, 34), '^' => new(LeftShift, 35), '&' => new(LeftShift, 36),
            '*' => new(LeftShift, 37), '(' => new(LeftShift, 38), ')' => new(LeftShift, 39),
            _ => default,
        };
        return key.Usage != 0;
    }
}
