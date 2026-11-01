// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Security.Cryptography;
using System.Text;

namespace MirrorDL.Tests;

/// <summary>Links found on web pages, and links sent by Click'n'Load.</summary>
public class PageTests
{
    static readonly Uri Page = new("https://host.example/f/abc123");

    [Fact]
    public void Links_to_files_and_download_buttons_are_found_the_most_likely_first()
    {
        const string html = """
            <html><head><link href="/style.css" rel="stylesheet"><script src="/app.js"></script></head><body>
            <a href="/">Home</a> <a href="https://social.example/us">Follow us</a>
            <a class="btn" href="/get/abc123">Download</a>
            <a href="https://cdn.example/files/Movie.Part1.rar?token=x&amp;e=1">Movie.Part1.rar</a>
            <a download href='/files/notes.pdf'>notes</a>
            <img src="/logo.png"><a href="#top">top</a>
            <script>var url = "https:\/\/cdn.example\/files\/Movie.Part2.rar";</script>
            </body></html>
            """;

        var found = PageLinks.Find(html, Page);

        Assert.Equal(
        [
            new Uri("https://host.example/files/notes.pdf"), // marked as a download, and a file
            new Uri("https://cdn.example/files/Movie.Part1.rar?token=x&e=1"),
            new Uri("https://cdn.example/files/Movie.Part2.rar"),
            new Uri("https://host.example/get/abc123"),
        ], found);
    }

    [Fact]
    public void A_page_whose_button_only_gets_its_link_from_a_script_shows_none()
    {
        // As a file host whose link appears only after a captcha: the button has no address yet.
        const string html = """<a id="download-link" class="button hidden">Generating download link</a><a href="/premium">Premium</a>""";
        Assert.Empty(PageLinks.Find(html, Page));
    }

    [Theory]
    [InlineData("https://a.example/x.7z", true)]
    [InlineData("https://a.example/archive.part03.rar", true)]
    [InlineData("https://a.example/archive.r07", true)]
    [InlineData("https://a.example/archive.7z.001", true)]
    [InlineData("https://a.example/index.php", false)]
    [InlineData("https://a.example/download", false)]
    public void Addresses_of_files_are_told_from_others(string url, bool file) => Assert.Equal(file, PageLinks.LooksLikeFile(new Uri(url)));

    [Fact]
    public async Task A_pasted_page_gives_way_to_the_files_found_on_it()
    {
        using var d = new Download();
        var content = Download.Bytes(300_000, 5);
        d.Server.Add("http://cdn.test/files/game.rar", content);
        var page = d.Link(Encoding.UTF8.GetBytes("""<p>Your file</p><a class="button" href="http://cdn.test/files/game.rar">Download game.rar</a>"""), "f/xyz");
        page.ContentType = "text/html";
        page.Ranges = false;

        Assert.Equal(0, await d.RunBatchAsync());
        var file = Assert.Single(d.Batch.Files);
        Assert.Equal("game.rar", file.Name);
        Assert.Equal(new Uri("http://mirror1.test/f/xyz"), file.Links[0].FoundOn);
        Assert.Equal(2, file.Links[0].Index); // after the pasted page
        Assert.Contains("a web page: 1 link to files found on it", d.Batch.Mirrors[0].Problem);
        Assert.Equal(content, File.ReadAllBytes(d.PathOf("game.rar")));
    }

    [Fact]
    public async Task A_page_without_a_link_to_a_file_is_said_and_nothing_is_downloaded()
    {
        using var d = new Download();
        var page = d.Link(Encoding.UTF8.GetBytes("""<a id="download-link" class="hidden">Generating download link</a>"""), "f/xyz");
        page.ContentType = "text/html";
        page.Ranges = false;

        Assert.Equal(1, await d.RunBatchAsync());
        var file = Assert.Single(d.Batch.Files);
        Assert.True(file.IsPage);
        Assert.Equal(FileState.Failed, file.State);
        Assert.Contains("Open it in the browser", file.Message);
        Assert.Empty(d.Files);
        Assert.Equal(2, page.Requests); // looked at, read once - not downloaded
    }

    // The way link sites send links: AES-128-CBC, the key also the IV, given as hex in a little script.
    static (string Jk, string Crypted) Encrypt(string text)
    {
        byte[] key = "1234567890123456"u8.ToArray();
        byte[] plain = Encoding.UTF8.GetBytes(text);
        Array.Resize(ref plain, (plain.Length + 15) / 16 * 16);
        using var aes = Aes.Create();
        aes.Key = key;
        return ($"function f(){{ return '{Convert.ToHexString(key)}';}}", Convert.ToBase64String(aes.EncryptCbc(plain, key, PaddingMode.None)));
    }

    [Fact]
    public void ClickNLoad_reads_the_links_a_site_sends()
    {
        var (jk, crypted) = Encrypt("http://a.example/x.rar\r\nhttps://b.example/y.rar\r\n");
        Assert.Equal(["http://a.example/x.rar", "https://b.example/y.rar"], ClickNLoad.Decrypt(jk, crypted));
        Assert.Null(ClickNLoad.Decrypt("function f(){ return compute(); }", crypted));
        Assert.Null(ClickNLoad.Decrypt(jk, "not base64!"));
    }

    [Fact]
    public async Task ClickNLoad_answers_as_JDownloader_and_hands_over_the_links()
    {
        using var server = ClickNLoad.Start(0); // any free port
        Assert.NotNull(server);
        var received = new TaskCompletionSource<(IReadOnlyList<string> Links, string? Source)>();
        server.Received += (links, source) => received.TrySetResult((links, source));
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        var ct = TestContext.Current.CancellationToken;

        Assert.Contains("jdownloader=true", await http.GetStringAsync("/jdcheck.js", ct));
        var (jk, crypted) = Encrypt("http://a.example/x.rar");
        var answer = await http.PostAsync("/flash/addcrypted2", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["jk"] = jk, ["crypted"] = crypted, ["source"] = "https://links.example/c/1", ["passwords"] = "",
        }), ct);

        Assert.Equal("success", (await answer.Content.ReadAsStringAsync(ct)).Trim());
        var (got, from) = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(["http://a.example/x.rar"], got);
        Assert.Equal("https://links.example/c/1", from);
        Assert.Null(ClickNLoad.Start(server.Port)); // taken: a second one can't listen there
    }

    [Fact]
    public async Task ClickNLoad_survives_broken_requests()
    {
        using var server = ClickNLoad.Start(0);
        Assert.NotNull(server);
        var ct = TestContext.Current.CancellationToken;
        foreach (string request in new[]
        {
            "POST /flash/add HTTP/1.1\r\nContent-Length: 99999999999999999999\r\n\r\n",
            "POST /flash/add HTTP/1.1\r\nContent-Length: -5\r\n\r\n",
            "garbage\r\n\r\n",
        })
        {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync("127.0.0.1", server.Port, ct);
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(request), ct);
            var buffer = new byte[256];
            try { _ = await client.GetStream().ReadAsync(buffer, ct); } catch (IOException) { } // closed without an answer: fine
        }
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        Assert.Contains("jdownloader=true", await http.GetStringAsync("/jdcheck.js", ct)); // still listening
    }

    [Fact]
    public void A_huge_broken_page_is_read_in_moments()
    {
        string html = string.Concat(Enumerable.Repeat("<a <a href='x' ", 300_000)) + new string('"', 100_000);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        PageLinks.Find(html, Page);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }
}
