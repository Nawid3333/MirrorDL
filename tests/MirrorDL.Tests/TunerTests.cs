// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

namespace MirrorDL.Tests;

public class TunerTests
{
    const double MB = 1_000_000;

    /// <summary>
    /// A made-up network to try the tuner on, half a second at a time: every connection of a link gets the
    /// same speed, up to what its server sends in all, and all links together up to what the line takes.
    /// </summary>
    sealed class Network
    {
        public required double[] PerConnection;
        public double[]? ServerLimit;
        /// <summary>Connections each server allows; the engine then lowers the link's cap for good.</summary>
        public int[]? ServerConnections;
        public double Line = double.PositiveInfinity;
        /// <summary>The speeds go up or down by up to this share, at random.</summary>
        public double Noise;
        public long Remaining = long.MaxValue;
        public int MaxPerLink = Tuner.MaxPerLink, MaxTotal = int.MaxValue;
        /// <summary>The most connections each link had at once.</summary>
        public int[] Peak = [];

        public Mirror[] Run(int seconds, Action<int>? everySecond = null)
        {
            var mirrors = PerConnection.Select((_, i) => new Mirror(i + 1, new Uri($"http://mirror{i + 1}.test/file"))).ToArray();
            var tuner = new Tuner(mirrors, MaxPerLink, MaxTotal);
            var random = new Random(1);
            Peak = new int[mirrors.Length];
            long now = 1_000_000;
            for (int tick = 0; tick < seconds * 2; tick++, now += 500)
            {
                if (tick % 2 == 0) everySecond?.Invoke(tick / 2);
                tuner.Update(now, Remaining);

                // What the engine does: start the connections a link may have, up to the total limit.
                int total = 0;
                for (int i = 0; i < mirrors.Length; i++)
                {
                    var m = mirrors[i];
                    if (ServerConnections is { } allowed && m.Cap > allowed[i])
                        (m.Cap, m.Ceiling, m.LastErrorAt) = (allowed[i], allowed[i], now);
                    m.Workers = Math.Min(m.Cap, MaxTotal - total);
                    total += m.Workers;
                    Peak[i] = Math.Max(Peak[i], m.Workers);
                }

                // What the network lets through.
                var speeds = mirrors.Select((m, i) => Math.Min(m.Workers * PerConnection[i], ServerLimit?[i] ?? double.PositiveInfinity)).ToArray();
                double share = Math.Min(1, Line / speeds.Sum());
                for (int i = 0; i < mirrors.Length; i++)
                {
                    double speed = speeds[i] * share * (1 + Noise * (2 * random.NextDouble() - 1));
                    mirrors[i].Bytes += (long)(speed / 2);
                    mirrors[i].LastData = now;
                }
            }
            return mirrors;
        }
    }

    [Fact]
    public void Adds_connections_while_each_one_makes_it_faster_up_to_the_most_per_link()
    {
        var net = new Network { PerConnection = [1 * MB] };
        var link = net.Run(120)[0];
        Assert.Equal(Tuner.MaxPerLink, link.Cap);
        Assert.Equal(Tuner.MaxPerLink, net.Peak[0]);
    }

    [Fact]
    public void Doubles_the_connections_while_each_brings_its_full_share()
    {
        var net = new Network { PerConnection = [1 * MB] };
        Assert.Equal(Tuner.MaxPerLink, net.Run(20)[0].Cap); // 4, 8, 16
    }

    [Fact]
    public void Stops_where_the_server_sends_no_more()
    {
        var net = new Network { PerConnection = [1 * MB], ServerLimit = [6 * MB] };
        var link = net.Run(300)[0];
        Assert.Equal(6, link.Cap);
        Assert.True(net.Peak[0] <= 9, $"tried {net.Peak[0]} connections");
    }

    [Fact]
    public void Stops_at_the_server_limit_also_when_the_speed_jumps_around()
    {
        var net = new Network { PerConnection = [1 * MB], ServerLimit = [6 * MB], Noise = 0.1 };
        var link = net.Run(300)[0];
        Assert.InRange(link.Cap, 6, 9);
    }

    [Fact]
    public void Doesnt_add_connections_that_only_take_speed_from_another_link_on_a_full_line()
    {
        var net = new Network { PerConnection = [1 * MB, 1 * MB], Line = 10 * MB };
        var links = net.Run(300);
        Assert.Equal(10, links.Sum(m => m.Cap));
    }

    [Fact]
    public void Never_asks_a_server_that_refused_connections_for_more()
    {
        var net = new Network { PerConnection = [1 * MB], ServerConnections = [3] };
        var link = net.Run(300)[0];
        Assert.Equal(3, link.Cap);
        Assert.Equal(3, net.Peak[0]);
    }

    [Fact]
    public void Tries_again_later_and_finds_a_server_that_got_faster()
    {
        var net = new Network { PerConnection = [1 * MB], ServerLimit = [6 * MB] };
        var link = net.Run(400, second => { if (second == 100) net.ServerLimit = null; })[0];
        Assert.Equal(Tuner.MaxPerLink, link.Cap);
    }

    [Fact]
    public void Keeps_to_the_limits_given()
    {
        var perLink = new Network { PerConnection = [1 * MB], MaxPerLink = 8 };
        Assert.Equal(8, perLink.Run(120)[0].Cap);
        Assert.Equal(8, perLink.Peak[0]);

        var total = new Network { PerConnection = [1 * MB, 1 * MB], MaxTotal = 6 };
        total.Run(120);
        Assert.True(total.Peak.Sum() <= 6, $"used {total.Peak.Sum()} connections");
    }

    [Fact]
    public void Adds_nothing_when_the_download_is_nearly_done()
    {
        var net = new Network { PerConnection = [1 * MB], Remaining = 10 * (long)MB };
        Assert.Equal(Tuner.StartPerLink, net.Run(120)[0].Cap);
    }
}
