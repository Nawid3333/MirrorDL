// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.Text;

namespace MirrorDL;

static class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        // Started by double-click or by dropping a file on the exe: keep the window open at the end.
        bool pause = Native.OwnsConsole();
        try
        {
            return await RunAsync(args);
        }
        catch (UserError e)
        {
            Console.WriteLine();
            Ui.Error(e.Message);
            return 1;
        }
        finally
        {
            if (pause)
            {
                Console.Write("\nPress Enter to close.");
                Console.ReadLine();
            }
        }
    }

    static async Task<int> RunAsync(string[] args)
    {
        var job = new Job();
        var ignored = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help" or "/?":
                    Console.WriteLine(Help);
                    return 0;
                case "-o" or "--out":
                    job.OutDir = Path.GetFullPath(Value(args, ref i));
                    break;
                case "-c" or "--connections":
                    job.Connections = Number(args, ref i);
                    break;
                case "-m" or "--max-per-server":
                    job.PerServer = Number(args, ref i);
                    break;
                default:
                    Input.Add(args[i], job, ignored);
                    break;
            }
        }

        if (job.Urls.Count == 0)
        {
            if (Console.IsInputRedirected)
                while (Console.ReadLine() is { } line) Input.Add(line, job, ignored);
            else
                Ui.ReadInteractive(job, ignored);
        }
        if (ignored.Count > 0)
            Ui.Dim("Ignored: " + string.Join(", ", ignored.Take(5).Select(s => Format.Clip(s, 40))) +
                   (ignored.Count > 5 ? $" and {ignored.Count - 5} more" : ""));
        if (job.Urls.Count == 0)
            throw new UserError("No download links given.");

        var session = new Session(job) { ConfirmAsync = question => Task.FromResult(Ui.Confirm(question)) };
        var gate = new object(); // one writer to the console at a time
        ProgressView? view = null;
        Phase viewPhase = Phase.Starting;

        void EndView()
        {
            if (view == null) return;
            view.Draw(Ui.StatusLines(session.GetStatus(), viewPhase, session.Job));
            view.End();
            view = null;
        }

        session.Log += (text, kind) => { lock (gate) Ui.Line(text, kind); };
        session.LinksChecked += () => { lock (gate) Ui.PrintLinks(session.Mirrors); };
        session.PhaseChanged += phase =>
        {
            lock (gate)
            {
                EndView();
                if (phase is Phase.Downloading or Phase.Verifying)
                {
                    Console.WriteLine();
                    view = new ProgressView();
                    viewPhase = phase;
                }
            }
        };

        var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCtrlC = (_, e) =>
        {
            if (cts.IsCancellationRequested) return; // pressed twice: quit right away
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += onCtrlC;
        try
        {
            Console.WriteLine();
            var run = session.RunAsync(cts.Token);
            while (await Task.WhenAny(run, Task.Delay(500)) != run)
                lock (gate) view?.Draw(Ui.StatusLines(session.GetStatus(), viewPhase, session.Job));
            lock (gate) EndView();
            return await run;
        }
        finally
        {
            Console.CancelKeyPress -= onCtrlC;
        }
    }

    static string Value(string[] args, ref int i) =>
        ++i < args.Length ? args[i] : throw new UserError($"{args[i - 1]} needs a value.");

    static int Number(string[] args, ref int i)
    {
        string option = args[i];
        return int.TryParse(Value(args, ref i), out int n) && n is >= 1 and <= 64
            ? n
            : throw new UserError($"{option} needs a number from 1 to 64.");
    }

    const string Help = """
        mirrordl - download one file from several links at once

        Usage:
          mirrordl                       asks you to paste the links
          mirrordl [options] <input>...  inputs: links, a .part file to continue, a .txt file with
                                         links, a checksum (MD5/SHA-1/SHA-256/SHA-512), a folder

        Options:
          -o, --out <folder>         save here (default: Downloads, or next to the .part file)
          -c, --connections <n>      connections in total (default 16)
          -m, --max-per-server <n>   connections per link (default 8)

        All links must lead to the same file: mirrordl compares their size and a few pieces of
        content and skips links that differ. It continues unfinished downloads of mirrordl,
        Firefox (.part) and aria2 (.aria2). Ctrl+C pauses; run it again to continue.
        """;
}
