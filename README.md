<p align="center"><img src="assets/logo.svg" width="96" alt="MirrorDL logo"></p>

<h1 align="center">MirrorDL</h1>

<p align="center">Download one file from several mirrors at once, after checking that they all serve the same file.</p>

<p align="center">
  <a href="https://github.com/Nawid3333/MirrorDL/actions/workflows/ci.yml"><img src="https://github.com/Nawid3333/MirrorDL/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://github.com/Nawid3333/MirrorDL/actions/workflows/github-code-scanning/codeql"><img src="https://github.com/Nawid3333/MirrorDL/actions/workflows/github-code-scanning/codeql/badge.svg" alt="CodeQL"></a>
  <a href="https://github.com/Nawid3333/MirrorDL/releases/latest"><img src="https://img.shields.io/github/v/release/Nawid3333/MirrorDL" alt="Latest release"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/Nawid3333/MirrorDL" alt="License: GPL-3.0"></a>
</p>

MirrorDL can also continue unfinished downloads that Firefox or aria2 left behind. It runs on
Windows 10 and 11, as a window app and as a terminal program.

![MirrorDL downloading the Linux kernel from four mirrors at once; a fifth link to another version is skipped](assets/screenshot-downloading.png)

## Features

- **All links at once.** Each link gets several connections. Faster links keep taking over work
  from slower ones until the end.
- **Checks the links first.** Links with a different size are skipped. MirrorDL also compares
  64 KB pieces from several places in the file across all links and skips links whose content
  differs, so you never get a file stitched together from two different versions.
- **Continues unfinished downloads**, also ones from other programs:
  - Firefox `.part` files, including Firefox's random names (`setup.Ab3-xY_z.exe.part`)
  - aria2 downloads: reads the `.aria2` control file to find the missing pieces
  - its own downloads, even after a crash or a power cut

  Before continuing, it compares what is already on disk with the links. A file that doesn't
  match is never changed.
- **Verifies the result** against a checksum (MD5, SHA-1, SHA-256, SHA-384 or SHA-512) if you
  paste one with the links.
- **Shows the details:** percent, downloaded and remaining size, current and average speed, time
  spent and left, connections, a line per link, and a map of which parts of the file are done.
- **Copes with real servers:** retries dropped connections, uses fewer connections on servers that
  allow only a few, and saves the progress every 10 seconds and when you pause or close the window.

## Download

From the [latest release](https://github.com/Nawid3333/MirrorDL/releases/latest):
`MirrorDL-win-x64.zip` is the window app, `mirrordl-terminal-win-x64.zip` the terminal program.
Unzip and start; nothing gets installed.

They need Windows 10 (1809 or newer) or Windows 11, 64-bit, and the
[.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). The window app brings the
Windows App SDK along.

**Updates:** the window app tells you when a newer release is out, with a link to it. It asks
GitHub once at start and sends nothing else ([privacy policy](PRIVACY.md)).

## Use

### Window app

Start `MirrorDL.exe`, paste the links (one per line), and press **Start**.

![A finished download: the checksum matched and the file was saved](assets/screenshot-done.png)

- Paste a checksum into the same box to have the file checked at the end.
- Drag links, a folder (where to save) or an unfinished file (to continue it) onto the window.
- **Pause** saves the progress. Closing the window during a download does the same.
  Press **Start** again later to continue.

`MirrorDL.exe --start <links…>` fills in the links and starts at once.

### Terminal

```
mirrordl                                      asks you to paste the links
mirrordl "https://a/file.iso" "https://b/file.iso" -o D:\Downloads
mirrordl links.txt
mirrordl "C:\Users\me\Downloads\file.Ab12-xYz.iso.part" "https://a/file.iso"
```

| Option | Meaning |
|---|---|
| `-o <folder>` | where to save (default: Downloads, or next to the unfinished file) |
| `-c <n>` | connections in total (default 16) |
| `-m <n>` | connections per link (default 8) |

Ctrl+C pauses. Run the same command again to continue.

## Continuing downloads from Firefox and aria2

MirrorDL looks in the save folder for an unfinished download of the same file. You can also
choose one yourself.

- **Firefox** saves `name.Ab12-xYz.ext.part` and an empty `name.ext` next to it. MirrorDL continues
  the `.part` file and puts the finished file in place of the empty one. Pause the download in
  Firefox or close Firefox first. **Don't cancel it in Firefox: that deletes the `.part` file.**
- **aria2** writes pieces all over the file, so the file size says nothing about how much is done.
  MirrorDL reads the `.aria2` file to find the missing pieces. Stop aria2 first.

## Limitations

- Direct download links only (HTTP and HTTPS). Pages that need a login, cookies or a captcha
  before the download starts won't work.
- The servers must support range requests to use more than one connection and to resume. For
  servers that don't, MirrorDL falls back to one connection.

## Build

Needs the .NET 10 SDK. Open `MirrorDL.slnx` in Visual Studio, or:

```
.\build.ps1
```

This builds both programs into `publish\app` and `publish\terminal` and zips them.

| Project | What it is |
|---|---|
| `MirrorDL.Core` | the downloader: link checks, segmented download, resume formats |
| `MirrorDL.App` | the window app (WinUI 3) |
| `MirrorDL` | the terminal program |
| `tests/MirrorDL.Tests` | unit tests |

### Tests

```
dotnet test --project tests/MirrorDL.Tests/MirrorDL.Tests.csproj
pwsh tools/e2e-test.ps1 -Exe MirrorDL/bin/Debug/net10.0/win-x64/mirrordl.exe
```

The end-to-end test starts local test mirrors (`tools/testserver.py`, which needs Python) that are
slow, drop connections, limit connections, serve another file or ignore range requests, downloads
from them, and checks every result by its SHA-256. With aria2 installed, it also continues a
download that aria2 started.

### Releases

Releases are automatic. Once CI has passed on a push to `main`, the commit messages since the last
release decide, written as [Conventional Commits](https://www.conventionalcommits.org): `fix:` makes
a patch release, `feat:` a minor one, `feat!:` a major one, and `docs:`, `test:`, `ci:` and the like
release nothing. The release gets the zips, and the Microsoft Store gets the same version
([how that is set up](docs/microsoft-store.md)).

## License

Copyright (C) 2026 Nawid Salehie

This program is free software: you can redistribute it and/or modify it under the terms of the
GNU General Public License as published by the Free Software Foundation, either version 3 of the
License, or (at your option) any later version. See [LICENSE](LICENSE).

This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without
even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.

**Additional permission under GNU GPL version 3 section 7:** If you modify this program, or any
covered work, by linking or combining it with the Windows App SDK (or a modified version of that
library), containing parts covered by the terms of the Microsoft Software License Terms for the
Windows App SDK, the licensors of this program grant you additional permission to convey the
resulting work.
