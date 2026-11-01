// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

namespace MirrorDL.Tests;

public class MirrorTests
{
    [Theory]
    [InlineData("ubuntu.iso", "ubuntu.iso")]
    [InlineData("\"quoted.zip\"", "quoted.zip")]
    [InlineData("../../evil.exe", "evil.exe")]
    [InlineData("dir\\name.txt", "name.txt")]
    [InlineData("what?.zip", "what_.zip")]
    [InlineData("trailing...", "trailing")]
    [InlineData("report .", "report")]        // Windows drops dots and spaces at the end
    [InlineData("report. .", "report")]
    [InlineData("..", "download")]
    [InlineData("nul.iso", "_nul.iso")]
    [InlineData("CON", "_CON")]
    [InlineData("com1.part", "_com1.part")]
    [InlineData("Lpt9.tar.gz", "_Lpt9.tar.gz")]
    [InlineData("console.iso", "console.iso")]
    [InlineData("nullsoft.exe", "nullsoft.exe")]
    public void CleanFileName_makes_a_server_name_safe(string sent, string expected) =>
        Assert.Equal(expected, Mirror.CleanFileName(sent));
}
