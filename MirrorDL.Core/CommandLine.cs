// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

namespace MirrorDL;

/// <summary>The terminal program's arguments: options, and inputs as <see cref="Input"/> sorts them.</summary>
public static class CommandLine
{
    public enum Request { Download, Help, Version }

    /// <summary>Fills <paramref name="job"/> from the arguments; what the program is asked to do.</summary>
    public static Request Parse(IReadOnlyList<string> args, Job job, List<string> ignored)
    {
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help" or "/?":
                    return Request.Help;
                case "--version":
                    return Request.Version;
                case "-o" or "--out":
                    string folder = Value(args, ref i);
                    try { job.OutDir = Path.GetFullPath(folder); }
                    catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
                    {
                        throw new UserError($"Can't use \"{folder}\" as the folder to save in: {e.Message}");
                    }
                    break;
                case "-c" or "--connections":
                    job.Connections = Number(args, ref i);
                    break;
                case "-m" or "--max-per-server":
                    job.PerServer = Number(args, ref i);
                    break;
                case "--delete-leftovers":
                    job.DeleteLeftovers = true;
                    break;
                case "--one-file":
                    job.OneFile = true;
                    break;
                case "--no-pages":
                    job.LookIntoPages = false;
                    break;
                default:
                    // A mistyped option would otherwise be ignored, and the value after it taken for an input.
                    if (args[i].Length > 1 && args[i][0] == '-' && !File.Exists(args[i]) && !Directory.Exists(args[i]))
                        throw new UserError($"Unknown option {args[i]}. See mirrordl --help.");
                    Input.Add(args[i], job, ignored);
                    break;
            }
        }
        return Request.Download;
    }

    static string Value(IReadOnlyList<string> args, ref int i) =>
        ++i < args.Count ? args[i] : throw new UserError($"{args[i - 1]} needs a value.");

    static int Number(IReadOnlyList<string> args, ref int i)
    {
        string option = args[i];
        return int.TryParse(Value(args, ref i), out int n) && n is >= 1 and <= 64
            ? n
            : throw new UserError($"{option} needs a number from 1 to 64.");
    }
}
