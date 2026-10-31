// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

namespace MirrorDL.Tests;

public class RangesTests
{
    [Fact]
    public void Normalize_sorts_and_merges_touching_and_overlapping_ranges_and_drops_empty_ones()
    {
        List<(long Start, long End)> expected = [(0, 30), (50, 60)];
        Assert.Equal(expected, Ranges.Normalize([(50, 60), (0, 10), (10, 20), (15, 30), (40, 40)]));
    }

    [Fact]
    public void Invert_gives_the_gaps_between_and_around_the_ranges()
    {
        List<(long Start, long End)> expected = [(0, 10), (20, 30), (40, 50)];
        Assert.Equal(expected, Ranges.Invert([(10, 20), (30, 40)], 50));
    }

    [Fact]
    public void Invert_of_nothing_is_everything_and_of_everything_is_nothing()
    {
        List<(long Start, long End)> everything = [(0, 100)];
        Assert.Equal(everything, Ranges.Invert([], 100));
        Assert.Empty(Ranges.Invert([(0, 100)], 100));
    }

    [Fact]
    public void Sum_adds_up_the_lengths() => Assert.Equal(25, Ranges.Sum([(0, 10), (20, 35)]));

    [Fact]
    public void Intersect_keeps_what_both_cover()
    {
        List<(long Start, long End)> expected = [(5, 10), (20, 25), (30, 32), (34, 40)];
        Assert.Equal(expected, Ranges.Intersect([(0, 10), (20, 32), (34, 50)], [(5, 25), (30, 40)]));
        Assert.Empty(Ranges.Intersect([(0, 10)], [(10, 20)]));
        Assert.Empty(Ranges.Intersect([], [(0, 10)]));
    }

    [Fact]
    public void Subtract_removes_what_the_second_covers()
    {
        List<(long Start, long End)> expected = [(0, 5), (25, 30), (40, 50)];
        Assert.Equal(expected, Ranges.Subtract([(0, 10), (20, 30), (35, 50)], [(5, 25), (30, 40)]));
        List<(long Start, long End)> all = [(0, 10)];
        Assert.Equal(all, Ranges.Subtract(all, []));
    }
}
