// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Text;

namespace MirrorDL.Tests;

public class LinkFilesTests
{
    const string Hex1 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    const string Hex2 = "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1";
    const string Hex3 = "cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0";

    [Theory]
    [InlineData("https://a.example/file.iso", "https://a.example/file.iso")]
    [InlineData("[file.iso](https://a.example/file.iso)", "https://a.example/file.iso")]
    [InlineData("<https://a.example/file.iso>", "https://a.example/file.iso")]
    [InlineData("**https://a.example/file.iso**", "https://a.example/file.iso")]
    [InlineData("https://a.example/file.iso.", "https://a.example/file.iso")]                 // the end of a sentence
    [InlineData("(https://a.example/file.iso),", "https://a.example/file.iso")]
    [InlineData("href=\"https://a.example/file.iso\">Download</a>", "https://a.example/file.iso")]
    [InlineData("text=https://a.example/file.iso", "https://a.example/file.iso")]             // a JDownloader .crawljob
    [InlineData("https://en.wikipedia.org/wiki/Foo_(bar)", "https://en.wikipedia.org/wiki/Foo_(bar)")]
    [InlineData("https://a.example/get?files[]=1", "https://a.example/get?files[]=1")]
    [InlineData("http://[::1]:8080/file.iso", "http://[::1]:8080/file.iso")]
    [InlineData("HTTPS://A.example/file.iso", "HTTPS://A.example/file.iso")]
    public void LinksIn_finds_the_link_inside_the_marks_around_it(string word, string expected) =>
        Assert.Equal([new Uri(expected)], Input.LinksIn(word));

    [Fact]
    public void LinksIn_finds_both_links_of_a_markdown_link_whose_text_is_a_link()
    {
        Assert.Equal([new Uri("https://a.example/x.iso"), new Uri("https://b.example/x.iso")],
            Input.LinksIn("[https://a.example/x.iso](https://b.example/x.iso)"));
    }

    [Theory]
    [InlineData("word")]
    [InlineData("https://")]
    [InlineData("ftp://a.example/file.iso")]
    [InlineData(Hex1)]
    public void LinksIn_finds_nothing_in_a_word_without_a_link(string word) => Assert.Empty(Input.LinksIn(word));

    [Fact]
    public void A_markdown_list_gives_its_links_and_checksums_and_leaves_its_words_pictures_and_paths()
    {
        using var folder = new TempFolder();
        string list = Path.Combine(folder.Path, "Game X.md");
        File.WriteAllText(list, $"""
            # Game X

            Download all parts, then check them. Mirror B is slow.

            - [Part 1](https://a.example/game.part1.rar "mirror A")
            - <https://a.example/game.part2.rar>
            - `https://b.example/game.part1.rar`
            1. **https://b.example/game.part2.rar**, the second mirror.

            | File | Link | SHA-256 |
            |---|---|---|
            | game.part3.rar | https://a.example/game.part3.rar | `{Hex3}` |

            ![screenshot](https://img.example/shot.png)
            [![badge](https://img.example/badge.svg)](https://a.example/game)

            > - SHA256 (game.part1.rar) = {Hex1}
            - my\_game.part2.rar: {Hex2}

            {folder.Path}
            """);
        var job = new Job();
        var ignored = new List<string>();

        Input.Add(list, job, ignored);

        Assert.Equal(
            ["https://a.example/game.part1.rar", "https://a.example/game.part2.rar", "https://b.example/game.part1.rar",
             "https://b.example/game.part2.rar", "https://a.example/game.part3.rar", "https://a.example/game"],
            job.Urls.Select(u => u.OriginalString));
        Assert.Equal(
            [new FileChecksum(Hex3, Link: new Uri("https://a.example/game.part3.rar")), new FileChecksum(Hex1, "game.part1.rar"),
             new FileChecksum(Hex2, "my_game.part2.rar")],
            job.FileChecksums);
        Assert.Empty(job.Checksums);
        Assert.Empty(ignored);       // the words around the links are the list's notes
        Assert.Null(job.OutDir);     // a folder named in a list is a note, not where to save
        Assert.Empty(job.PartFiles);
    }

    [Fact]
    public void A_txt_list_gives_the_links_written_among_words_and_markdown()
    {
        using var folder = new TempFolder();
        string list = Path.Combine(folder.Path, "links.txt");
        File.WriteAllText(list, $"Mirror 1: https://a.example/x.iso\r\n[mirror 2](https://b.example/x.iso) {Hex1}\r\n");
        var job = new Job();

        Input.Add(list, job, []);

        Assert.Equal([new Uri("https://a.example/x.iso"), new Uri("https://b.example/x.iso")], job.Urls);
        Assert.Equal([new FileChecksum(Hex1, Link: new Uri("https://b.example/x.iso"))], job.FileChecksums);
    }

    [Theory]
    [InlineData("links.txt", true)]
    [InlineData("LINKS.TXT", true)]
    [InlineData("links.md", true)]
    [InlineData("links.markdown", true)]
    [InlineData("links.csv", false)]
    [InlineData("game.iso", false)]
    [InlineData("links.md.part", false)]
    public void Only_txt_and_md_files_are_lists_when_given(string name, bool isList)
    {
        using var folder = new TempFolder();
        Assert.Equal(isList, LinkFiles.Is(folder.File(name)));
    }

    [Theory]
    [InlineData(".aria2")]
    [InlineData(".mirrordl")]
    public void A_txt_file_being_downloaded_is_an_unfinished_download_not_a_list(string progress)
    {
        using var folder = new TempFolder();
        string download = folder.File("notes.txt", 100);
        folder.File("notes.txt" + progress, 10);
        Assert.False(LinkFiles.Is(download));
    }

    [Fact]
    public void A_list_open_in_an_editor_is_read_and_its_encoding_found()
    {
        using var folder = new TempFolder();
        string list = Path.Combine(folder.Path, "links.txt");
        File.WriteAllText(list, "https://a.example/ünï.iso", Encoding.Unicode); // UTF-16, as older Notepad saved
        using var editor = new FileStream(list, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        Assert.Equal("https://a.example/ünï.iso", LinkFiles.Read(list));
    }

    [Fact]
    public void A_file_too_big_for_a_list_or_not_text_is_not_read()
    {
        using var folder = new TempFolder();
        string big = folder.File("big.txt", LinkFiles.MaxBytes + 1);
        string binary = Path.Combine(folder.Path, "links.md");
        File.WriteAllBytes(binary, [(byte)'h', 0, 1, 2]);

        Assert.Contains("too big", Assert.Throws<InvalidDataException>(() => LinkFiles.Read(big)).Message);
        Assert.Contains("isn't text", Assert.Throws<InvalidDataException>(() => LinkFiles.Read(binary)).Message);
    }

    [Fact]
    public void A_list_that_can_t_be_read_is_said_in_the_terminal_not_taken_for_words()
    {
        using var folder = new TempFolder();
        string binary = Path.Combine(folder.Path, "links.txt");
        File.WriteAllBytes(binary, [0, 0, 0]);

        var error = Assert.Throws<UserError>(() => Input.Add(binary, new Job(), []));
        Assert.Contains(binary, error.Message);
        Assert.Contains("isn't text", error.Message);
    }
}
