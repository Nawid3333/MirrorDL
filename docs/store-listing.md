# Microsoft Store listing

The texts and answers for the app's Partner Center submission, ready to copy. The images are in
`assets/store/`. Setup and automation: [microsoft-store.md](microsoft-store.md).

## Pricing and availability

- **Markets:** all markets
- **Visibility:** public audience, discoverable in the Store
- **Schedule:** release as soon as possible
- **Pricing:** free, no free trial

## Properties

- **Category:** Utilities & tools
- **Privacy policy URL:** `https://github.com/Nawid3333/MirrorDL/blob/main/PRIVACY.md`
- **Website:** `https://github.com/Nawid3333/MirrorDL`
- **Support contact info:** `https://github.com/Nawid3333/MirrorDL/issues`
- **Display mode, product declarations, system requirements:** leave the defaults

## Age ratings

The IARC questionnaire. App type: **All other app types** (MirrorDL is neither a game nor a social or
communication app). Then every question is **No**:

| Question | Answer |
|---|---|
| Rating-relevant content (sex, violence, language) in the app package | No |
| Users can talk to each other or share pictures or audio | No |
| Offers or promotes content that is not part of the download (like films, products, songs, AI content, news) | No: it offers no content of its own, it only downloads what the user links to |
| Promotes or sells age-restricted products or activities | No |
| Shares the user's current location with other users | No |
| Users can buy digital goods | No |
| Payouts, gift cards, play-to-earn, crypto rewards, NFTs | No |
| Is a web browser or a search engine | No: it is a download manager and searches nothing. It has a small browser window (WebView2) only to open a download page whose link needs a captcha or a click; if the questionnaire asks about unrestricted internet access, that window counts |
| Mainly a news or education product | No |
| Uses ratings from a rating board directly, or ships on physical media | No |

## Packages

Upload `MirrorDL-x64.msix`. Device family: **Windows 10/11 Desktop**.

## Store listing (English)

**Description**

```
MirrorDL downloads files from several links (mirrors) at the same time, so big downloads finish faster - and it first checks that every link really serves the same file.

- Many files at once: paste the links to several files, also several mirrors of each, or add a .txt or .md file that lists them. MirrorDL sorts them into files by name, size and content, shows you what it found, and downloads what you tick.
- A download list: start new downloads while others run, pause and resume each one. Paused downloads are still there after closing MirrorDL or restarting the PC.
- Links from web pages: a file host's page is searched for its download link; a page with a captcha opens in MirrorDL's browser, and MirrorDL takes the link once you click its download button. Click'n'Load takes links from link sites.
- All mirrors at once: every link gets several connections, and fast mirrors keep taking work over from slow ones until the end.
- Checks before it combines: links with another size or other content are skipped, so you never get a file stitched together from two versions.
- Continues unfinished downloads, also those of other programs: Firefox .part files and aria2 downloads.
- Checks the finished files against MD5, SHA-1, SHA-256 or SHA-512 checksums you paste with the links.
- History and settings: finished downloads are listed with their links, and every extra can be switched off.

Paste the links, press Check links, then Download. MirrorDL is free and open source (GPL-3.0): https://github.com/Nawid3333/MirrorDL
```

**What's new in this version**

```
First version in the Microsoft Store.
```

That was the first submission's. Since then every release fills it in itself, with the changes listed
on its GitHub page and a link to it ([microsoft-store.md](microsoft-store.md#whats-new-in-this-version)).

**Product features** (one per field)

```
Downloads files from several mirrors at once
Sorts pasted links, or a .txt or .md list of them, into files and shows them before downloading
Starts new downloads while others run; keeps paused ones after a restart
Takes download links from web pages and Click'n'Load
Continues unfinished downloads of Firefox and aria2
Checks the results against MD5, SHA-1, SHA-256 or SHA-512 checksums
History of finished downloads; every extra can be switched off
```

**Screenshots** (Desktop), in this order, with these captions

| File | Caption |
|---|---|
| `assets/store/screenshot-downloading.png` | Four mirrors at once; a link to another version is skipped. |
| `assets/store/screenshot-done.png` | Done: the checksum matches. |

**Store logos**

| Field | File |
|---|---|
| 9:16 poster art (1440 x 2160), the main logo for Windows | `assets/store/poster-1440x2160.png` |
| 1:1 box art (1080 x 1080) | `assets/store/box-art-1080.png` |
| 1:1 app tile icon (300 x 300) | `assets/store/app-tile-300.png` |

Trailers, the 16:9 super hero art, the Xbox images, the short title and the voice title: leave empty.

**Short description** (at most 270 characters)

```
Download files from several mirrors at once: faster, and checked first, so every link really serves the same file. Paste links to many files; MirrorDL sorts them and shows what it found. Continues unfinished downloads from Firefox and aria2.
```

**Keywords** (at most 7, 21 words in all)

```
Download manager
parallel download
resume download
aria2
multi-source downloader
segmented downloading
checksum verification
```

**Copyright and trademark info:** `Copyright (C) 2026 Nawid Salehie`

**Additional license terms:** `GNU General Public License v3.0 or later: https://github.com/Nawid3333/MirrorDL/blob/main/LICENSE`

**Developed by:** `Nawid Salehie`

## Submission options

- **Publishing hold:** publish as soon as it passes certification
- **Restricted capabilities (runFullTrust), why the app needs it:**

  ```
  MirrorDL is a desktop download manager (WinUI 3, Windows App SDK) packaged as MSIX. It saves downloads in folders the user chooses and continues unfinished downloads of other programs (the .part files of Firefox and aria2) in place, which needs normal access to the file system. runFullTrust is the standard capability for such desktop apps.
  ```

## Additional testing info

The notes for certification are not under Submission options: they are on their own page,
Zusätzliche Testinformationen (Additional testing info) in the product's menu.

```
MirrorDL is a download manager: it downloads files from several mirror links at the same time. No account or sign-in is needed; it needs an internet connection.

To test:
1. Start MirrorDL.
2. Paste these two links (the same Linux kernel source file, 141 MB, on two kernel.org mirrors) into the Links box (pasting both at once puts each into a box of its own):
https://cdn.kernel.org/pub/linux/kernel/v6.x/linux-6.12.tar.xz
https://mirrors.edge.kernel.org/pub/linux/kernel/v6.x/linux-6.12.tar.xz
3. Press Check links. The app checks that both links serve the same file and shows it (1 file, 2 links). Press Download: it downloads parts of it from both at once and shows percent, size and speed per mirror.
4. The file is saved in the Downloads folder (another folder can be picked in "Save to").

Resume: press the pause button of the download, then the resume button; it goes on where it stopped. History lists finished downloads; Settings (the gear) has a switch for each extra.

runFullTrust: MirrorDL is a WinUI 3 desktop app. It writes downloads to folders the user picks and continues unfinished .part files of Firefox and aria2 in place, so it needs normal file system access. For Click'n'Load it listens on 127.0.0.1 port 9666 (this PC only), and it opens download pages in a WebView2 window.

Source code (GPL-3.0): https://github.com/Nawid3333/MirrorDL
```
