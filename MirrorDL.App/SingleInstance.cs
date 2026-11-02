// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Nawid Salehie

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace MirrorDL.App;

/// <summary>
/// One MirrorDL window at a time, as download managers do: a second start hands its arguments (links, files,
/// --start) to the window that is open, and ends. Two windows would restore the same download list and write over
/// each other's saved list and settings. Through a named pipe only this user's programs can open.
/// </summary>
static class SingleInstance
{
    // The Store version and the one from the zip keep their lists apart, so each has its own.
    static readonly string Pipe = $"MirrorDL-{(Native.IsPackaged() ? "store" : "zip")}-{WindowsIdentity.GetCurrent().User?.Value}";

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>Hands the arguments to a MirrorDL that runs; false when none does.</summary>
    public static bool HandOver(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Pipe, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(300);
            AllowSetForegroundWindow(-1); // the open window may come to the front: the user just started MirrorDL
            using var writer = new StreamWriter(client);
            writer.Write(JsonSerializer.Serialize(args));
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Takes the arguments of later starts, for as long as MirrorDL runs. <paramref name="received"/> runs on a background thread.</summary>
    public static void Listen(Action<string[]> received) => Task.Run(async () =>
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(Pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync();
                using var reader = new StreamReader(server);
                received(JsonSerializer.Deserialize<string[]>(await reader.ReadToEndAsync()) ?? []);
            }
            catch (Exception)
            {
                // Another MirrorDL started at the same moment holds the name, or a start sent something broken.
                await Task.Delay(2000);
            }
        }
    });
}
