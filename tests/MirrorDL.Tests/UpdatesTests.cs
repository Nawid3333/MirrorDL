// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Net;
using System.Text;

namespace MirrorDL.Tests;

public class UpdatesTests
{
    const string Latest = "https://api.github.com/repos/Nawid3333/MirrorDL/releases/latest";

    static async Task<(string Version, Uri Page)?> Ask(string current, string answer, Action<FakeLink>? setup = null)
    {
        using var github = new FakeServer();
        var link = github.Add(Latest, Encoding.UTF8.GetBytes(answer));
        link.Ranges = false;
        setup?.Invoke(link);
        return await Updates.FindNewerAsync(current, github, TestContext.Current.CancellationToken);
    }

    static string Release(string tag, string page = "https://github.com/Nawid3333/MirrorDL/releases/tag/v9") =>
        $$"""{"tag_name":"{{tag}}","html_url":"{{page}}"}""";

    [Fact]
    public async Task A_newer_release_is_found_with_its_page()
    {
        var found = await Ask("1.2.0", Release("v1.3.0"));
        Assert.Equal("1.3.0", found?.Version);
        Assert.Equal("github.com", found?.Page.Host);
    }

    [Theory]
    [InlineData("1.3.0", "v1.3.0")] // the same
    [InlineData("1.3.0", "v1.2.9")] // older
    [InlineData("1.3.0", "nightly")] // no version
    public async Task Not_newer_is_nothing(string current, string tag) => Assert.Null(await Ask(current, Release(tag)));

    [Fact]
    public async Task A_local_build_never_asks() => Assert.Null(await Ask("0.0.0-dev", Release("v9.0.0")));

    [Fact]
    public async Task A_page_that_isnt_on_GitHub_is_not_trusted() =>
        Assert.Null(await Ask("1.0.0", Release("v2.0.0", "https://evil.example/download")));

    [Theory]
    [InlineData("<html>captive portal</html>")]   // not JSON
    [InlineData("""{"message":"API rate limit exceeded"}""")] // no tag
    [InlineData("""{"tag_name":5,"html_url":[]}""")] // wrong types
    public async Task An_unexpected_answer_is_nothing(string answer) => Assert.Null(await Ask("1.0.0", answer));

    [Fact]
    public async Task A_server_error_is_nothing() =>
        Assert.Null(await Ask("1.0.0", "", link => link.Answer = (_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

    [Fact]
    public async Task An_answer_far_too_big_for_a_release_is_nothing() =>
        Assert.Null(await Ask("1.0.0", Release("v2.0.0") + new string(' ', 2 << 20)));
}
