// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Net.Http;

namespace MirrorDL.Tests;

public class FormatTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.00 KB")]
    [InlineData(1536L, "1.50 KB")]
    [InlineData(1L << 20, "1.00 MB")]
    [InlineData(97_268_672_604L, "90.59 GB")]
    [InlineData(5L << 40, "5.00 TB")]
    [InlineData(1L << 50, "1024.00 TB")] // TB is the biggest unit
    public void Bytes_picks_the_unit(long bytes, string text) => Assert.Equal(text, Format.Bytes(bytes));

    [Theory]
    [InlineData(1048576.0, "1.00 MB/s")]
    [InlineData(double.NaN, "--")]
    [InlineData(double.PositiveInfinity, "--")]
    [InlineData(-1.0, "--")]
    public void Speed_is_bytes_per_second_and_never_garbage(double speed, string text) => Assert.Equal(text, Format.Speed(speed));

    [Theory]
    [InlineData(0.0, "0s")]
    [InlineData(59.2, "1m00s")]   // rounded up: never shows less time than is left
    [InlineData(61.0, "1m01s")]
    [InlineData(3600.0, "1h00m")]
    [InlineData(3661.0, "1h01m")]
    [InlineData(100.0 * 3600, "--")] // too long to be worth saying
    [InlineData(double.NaN, "--")]
    [InlineData(double.PositiveInfinity, "--")]
    public void Duration_is_short(double seconds, string text) => Assert.Equal(text, Format.Duration(seconds));

    [Fact]
    public void Percent_has_one_decimal_with_a_dot_in_any_language() => Assert.Equal("12.3%", Format.Percent(12.345));

    [Theory]
    [InlineData("abc", 4, "abc")]
    [InlineData("abcdef", 4, "abc…")]
    public void Clip_shortens_with_an_ellipsis(string text, int width, string clipped) => Assert.Equal(clipped, Format.Clip(text, width));

    [Theory]
    [InlineData(1, "1 link")]
    [InlineData(0, "0 links")]
    [InlineData(2, "2 links")]
    public void Plural_adds_an_s_unless_one(int n, string text) => Assert.Equal(text, Format.Plural(n, "link"));

    [Fact]
    public void Short_gives_one_line_of_the_error_that_matters()
    {
        var wrapped = new HttpRequestException("An error occurred while sending the request.",
            new IOException("Connection reset.\r\nBy the other side."));
        Assert.Equal("Connection reset. By the other side", Format.Short(wrapped));
        Assert.Equal("HTTP 503", Format.Short(new SourceError("HTTP 503.", 503)));
        Assert.Equal(70, Format.Short(new IOException(new string('x', 200))).Length);
    }

    [Fact]
    public void AppInfo_has_a_version() => Assert.False(string.IsNullOrWhiteSpace(AppInfo.Version));
}
