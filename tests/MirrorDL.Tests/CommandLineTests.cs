// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

namespace MirrorDL.Tests;

public class CommandLineTests
{
    static (CommandLine.Request Request, Job Job, List<string> Ignored) Parse(params string[] args)
    {
        var job = new Job();
        var ignored = new List<string>();
        return (CommandLine.Parse(args, job, ignored), job, ignored);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    [InlineData("/?")]
    public void Help(string option) => Assert.Equal(CommandLine.Request.Help, Parse(option).Request);

    [Fact]
    public void Version() => Assert.Equal(CommandLine.Request.Version, Parse("--version").Request);

    [Fact]
    public void Options_and_inputs_fill_the_job()
    {
        string hex = new('a', 64);
        var (request, job, ignored) = Parse("-o", "downloads", "-c", "8", "-m", "4", "--delete-leftovers",
            "https://a.example/x.iso", "https://b.example/x.iso", hex, "nonsense");

        Assert.Equal(CommandLine.Request.Download, request);
        Assert.Equal(Path.GetFullPath("downloads"), job.OutDir);
        Assert.Equal(8, job.Connections);
        Assert.Equal(4, job.PerServer);
        Assert.True(job.DeleteLeftovers);
        Assert.Equal(2, job.Urls.Count);
        Assert.Equal([hex], job.Checksums);
        Assert.Equal(["nonsense"], ignored);
    }

    [Fact]
    public void Long_option_names_work_too()
    {
        var (_, job, _) = Parse("--out", "x", "--connections", "3", "--max-per-server", "2");
        Assert.Equal(Path.GetFullPath("x"), job.OutDir);
        Assert.Equal(3, job.Connections);
        Assert.Equal(2, job.PerServer);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65")]
    [InlineData("many")]
    public void A_connection_limit_is_a_number_from_1_to_64(string value) =>
        Assert.Contains("needs a number from 1 to 64", Assert.Throws<UserError>(() => Parse("-c", value)).Message);

    [Fact]
    public void An_option_without_its_value_says_so() =>
        Assert.Contains("-o needs a value", Assert.Throws<UserError>(() => Parse("-o")).Message);

    [Fact]
    public void A_folder_that_cant_be_one_says_so() =>
        Assert.Contains("Can't use", Assert.Throws<UserError>(() => Parse("-o", "a\0b")).Message);

    [Fact]
    public void A_mistyped_option_is_an_error_instead_of_being_ignored() =>
        Assert.Contains("Unknown option --outdir", Assert.Throws<UserError>(() => Parse("--outdir", "D:\\")).Message);
}
