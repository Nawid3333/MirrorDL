// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MirrorDL;

/// <summary>
/// Click'n'Load, as JDownloader and pyLoad take it: link sites such as filecrypt have a button that sends their links
/// to a download program on this PC (127.0.0.1, port 9666) once the user solved their captcha in the browser. Only
/// this PC can reach it. The links are only handed over (<see cref="Received"/>), never downloaded by themselves.
/// </summary>
public sealed class ClickNLoad : IDisposable
{
    public const int DefaultPort = 9666;
    const int MaxRequest = 4 << 20;
    const int MaxLinks = 1000; // more from one click is a page flooding the list, not a link site
    static readonly Regex Key = new(@"['""](?<hex>(?:[0-9a-fA-F]{2}){16,32})['""]");

    readonly TcpListener _listener;
    readonly CancellationTokenSource _stop = new();

    ClickNLoad(TcpListener listener) => _listener = listener;

    /// <summary>The links a site sent, and the page they came from if it said.</summary>
    public event Action<IReadOnlyList<string>, string?>? Received;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Listens on 127.0.0.1; null when the port is taken (JDownloader runs, say).</summary>
    public static ClickNLoad? Start(int port = DefaultPort)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch (SocketException)
        {
            return null;
        }
        var server = new ClickNLoad(listener);
        _ = server.AcceptAsync();
        return server;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }

    async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = HandleAsync(client);
        }
    }

    async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var stream = client.GetStream();
            var (method, path, body) = await ReadRequestAsync(stream, timeout.Token);
            var (status, type, text) = Answer(method, path, body);
            byte[] content = Encoding.UTF8.GetBytes(text);
            string head = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {content.Length}\r\n" +
                          "Access-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), timeout.Token);
            await stream.WriteAsync(content, timeout.Token);
        }
        catch (Exception)
        {
            // A broken, slow or hostile request: dropped. Nothing of it may end the listener or the program.
        }
    }

    (string Status, string Type, string Text) Answer(string method, string path, string body)
    {
        string route = path.Split('?')[0].TrimEnd('/');
        // Sites first ask whether a program listens, the way JDownloader answers.
        if (method == "GET" && route == "/jdcheck.js") return ("200 OK", "text/javascript", "jdownloader=true;\nvar version='18507';\n");
        if (method == "GET" && route == "/crossdomain.xml")
            return ("200 OK", "text/xml", "<?xml version=\"1.0\"?><cross-domain-policy><allow-access-from domain=\"*\" /></cross-domain-policy>");
        if (method == "GET" && route is "" or "/flash") return ("200 OK", "text/plain", "JDownloader\r\n");
        if (method != "POST") return ("404 Not Found", "text/plain", "");

        var form = ParseForm(body);
        string? source = form.GetValueOrDefault("source");
        List<string>? links = route switch
        {
            "/flash/add" => Lines(form.GetValueOrDefault("urls") ?? ""),
            "/flash/addcrypted2" => Decrypt(form.GetValueOrDefault("jk") ?? "", form.GetValueOrDefault("crypted") ?? ""),
            _ => null,
        };
        if (links is not { Count: > 0 and <= MaxLinks }) return ("200 OK", "text/plain", "failed\r\n");
        Received?.Invoke(links, string.IsNullOrWhiteSpace(source) ? null : source);
        return ("200 OK", "text/plain", "success\r\n");
    }

    /// <summary>
    /// The links of an "addcrypted2" request: <paramref name="crypted"/> is AES-128-CBC in base64, the key - also the IV -
    /// is the hex string the script in <paramref name="jk"/> returns. Null when it can't be read.
    /// </summary>
    public static List<string>? Decrypt(string jk, string crypted)
    {
        try
        {
            var match = Key.Match(jk);
            if (!match.Success) return null;
            byte[] key = Convert.FromHexString(match.Groups["hex"].Value)[..16];
            byte[] data = Convert.FromBase64String(crypted.Replace(' ', '+'));
            if (data.Length == 0 || data.Length % 16 != 0) return null;
            using var aes = Aes.Create();
            aes.Key = key;
            return Lines(Encoding.UTF8.GetString(aes.DecryptCbc(data, key, PaddingMode.None)).TrimEnd('\0'));
        }
        catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    static List<string> Lines(string text) =>
        [.. text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || l.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .Distinct()];

    static Dictionary<string, string> ParseForm(string body)
    {
        var form = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string name = WebUtility.UrlDecode(eq < 0 ? pair : pair[..eq]);
            form[name] = eq < 0 ? "" : WebUtility.UrlDecode(pair[(eq + 1)..]);
        }
        return form;
    }

    /// <summary>Reads an HTTP request: its method, path and body (a form, as browsers send it).</summary>
    static async Task<(string Method, string Path, string Body)> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int read = await stream.ReadAsync(chunk, ct);
            if (read == 0) throw new IOException("closed");
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxRequest) throw new IOException("too big");
            headerEnd = buffer.GetBuffer().AsSpan(0, (int)buffer.Length).IndexOf("\r\n\r\n"u8);
        }
        string head = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, headerEnd);
        string[] lines = head.Split("\r\n");
        string[] first = lines[0].Split(' ');
        if (first.Length < 2) throw new FormatException("bad request line");
        long length = 0;
        foreach (string line in lines.Skip(1))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                if (!long.TryParse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture, out length))
                    throw new FormatException("bad Content-Length");
        if (length < 0 || length > MaxRequest) throw new IOException("too big");
        int bodyStart = headerEnd + 4;
        while (buffer.Length - bodyStart < length)
        {
            int read = await stream.ReadAsync(chunk, ct);
            if (read == 0) break;
            buffer.Write(chunk, 0, read);
        }
        string body = Encoding.UTF8.GetString(buffer.GetBuffer(), bodyStart, (int)Math.Min(length, buffer.Length - bodyStart));
        return (first[0].ToUpperInvariant(), first[1], body);
    }
}
