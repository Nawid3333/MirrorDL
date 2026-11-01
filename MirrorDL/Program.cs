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
        catch (Exception e)
        {
            // A bug: still the exit code scripts know for an error, with what is needed to report it.
            Console.WriteLine();
            Ui.Error("Unexpected error: " + e);
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
        switch (CommandLine.Parse(args, job, ignored))
        {
            case CommandLine.Request.Help:
                Console.WriteLine(Help.Replace("{version}", AppInfo.Version));
                return 0;
            case CommandLine.Request.Version:
                Console.WriteLine($"mirrordl {AppInfo.Version}");
                return 0;
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

        var batch = new Batch(job) { ConfirmAsync = (_, question) => Task.FromResult(Ui.Confirm(question)) };
        var gate = new object(); // one writer to the console at a time
        ProgressView? view = null;
        Phase viewPhase = Phase.Starting;
        BatchFile? shown = null; // the file being downloaded

        List<string> Lines()
        {
            var status = shown!.Session!.GetStatus();
            var lines = Ui.StatusLines(status, viewPhase, shown.Job);
            var files = batch.Files.Where(f => f.State != FileState.Removed).ToList();
            if (files.Count > 1) lines.Insert(0, Ui.BatchLine(files, shown, status));
            return lines;
        }

        void EndView()
        {
            if (view == null) return;
            view.Draw(Lines());
            view.End();
            view = null;
        }

        batch.Log += (_, text, kind) =>
        {
            lock (gate)
            {
                // A message in the middle of a progress block goes below it, and the block starts again under
                // the message - otherwise the next redraw would write over it.
                bool live = view != null;
                EndView();
                Ui.Line(text, kind);
                if (live) view = new ProgressView();
            }
        };
        batch.Sorted += () => { lock (gate) if (batch.Files.Count > 1) Ui.PrintFiles(batch.Files); };
        batch.FileStarted += file =>
        {
            lock (gate)
            {
                EndView();
                shown = file;
                if (batch.Files.Count > 1) Ui.FileHeader(file, batch.Files.Count);
            }
        };
        batch.LinksChecked += file => { lock (gate) Ui.PrintLinks(file.Links); };
        batch.PhaseChanged += (_, phase) =>
        {
            lock (gate)
            {
                EndView();
                if (phase is Phase.Copying or Phase.Downloading or Phase.Verifying)
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
            var run = batch.RunAsync(cts.Token);
            while (await Task.WhenAny(run, Task.Delay(500)) != run)
                lock (gate) view?.Draw(Lines());
            lock (gate) EndView();
            int code = await run;
            if (batch.Files.Count > 1) Ui.PrintSummary(batch.Files);
            return code;
        }
        finally
        {
            Console.CancelKeyPress -= onCtrlC;
        }
    }

    const string Help = """
        mirrordl {version} - download files from several links at once

        Usage:
          mirrordl                       asks you to paste the links
          mirrordl [options] <input>...  inputs: links, unfinished files to continue (any name), a .txt
                                         file with links, a checksum (MD5/SHA-1/SHA-256/SHA-384/SHA-512),
                                         also with its file name ("<checksum>  <name>"), a folder

        Options:
          -o, --out <folder>         save here (default: Downloads, or next to the unfinished file given)
          -c, --connections <n>      at most n connections in total, 1 to 64 (default: no limit)
          -m, --max-per-server <n>   at most n connections per link, 1 to 64 (default: 16)
          --delete-leftovers         delete leftovers that can't go to the Recycle Bin for good,
                                     without asking (they are asked about otherwise)
          --one-file                 all links are mirrors of one file: don't sort them into files
          --no-pages                 don't look for links to files on pasted web pages

        mirrordl finds the number of connections by itself: it adds more as long as they make the
        download faster, and stops where a server or the internet line is the limit.

        Links to the same file - the same name, or the same size and content - are its mirrors and
        are used together; links to other files are downloaded one after another. A checksum goes
        with the file named on its line or the link on its line; others are matched by computing.
        Of the mirrors of a file, links with another size or content are skipped. A link to a web
        page (a file host's page with a download button) is read for links to files on it; one
        that only appears after a captcha can't be seen - open the page in a browser for it.

        It continues unfinished downloads of mirrordl, Firefox (.part) and aria2 (.aria2); with
        several of the same file, it checks each one and puts together what they hold. A file that
        is in the save folder already, complete, is only checked, not downloaded again. Ctrl+C
        pauses; run the same command again to continue.
        """;
}
