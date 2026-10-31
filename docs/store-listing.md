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
| Is a web browser or a search engine | No: it shows no web pages and searches nothing |
| Mainly a news or education product | No |
| Uses ratings from a rating board directly, or ships on physical media | No |

## Packages

Upload `MirrorDL-x64.msix`. Device family: **Windows 10/11 Desktop**.

## Store listing (English)

**Description**

```
MirrorDL downloads one file from several links (mirrors) at the same time, so big downloads finish faster - and it first checks that every link really serves the same file.

- All mirrors at once: every link gets several connections, and fast mirrors keep taking work over from slow ones until the end.
- Checks before it combines: links with another size or other content are skipped, so you never get a file stitched together from two versions.
- Continues unfinished downloads, also those of other programs: Firefox .part files and aria2 downloads, after checking that what is on disk matches the links.
- Pause any time: the progress is saved, also when you close the window or the computer turns off.
- Checks the finished file against an MD5, SHA-1, SHA-256 or SHA-512 checksum you paste with the links.
- Shows everything: percent, speed, time left, the speed of every mirror, and a map of which parts of the file are done.

Paste the links, press Start. MirrorDL is free and open source (GPL-3.0): https://github.com/Nawid3333/MirrorDL
```

**What's new in this version**

```
First version in the Microsoft Store.
```

**Product features** (one per field)

```
Downloads one file from several mirrors at once
Skips links whose size or content differs from the others
Continues unfinished downloads of Firefox and aria2
Saves the progress when you pause or close the window
Checks the result against an MD5, SHA-1, SHA-256 or SHA-512 checksum
Shows the speed of every mirror and a map of the finished parts
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
Download one file from several mirrors at once: faster, and checked first, so every link really serves the same file. Continues unfinished downloads from Firefox and aria2.
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
MirrorDL is a download manager: it downloads one file from several mirror links at the same time. No account or sign-in is needed; it needs an internet connection.

To test:
1. Start MirrorDL.
2. Paste these two links (the same Linux kernel source file, 141 MB, on two kernel.org mirrors) into the box:
https://cdn.kernel.org/pub/linux/kernel/v6.x/linux-6.12.tar.xz
https://mirrors.edge.kernel.org/pub/linux/kernel/v6.x/linux-6.12.tar.xz
3. Press Start. The app checks that both links serve the same file, then downloads parts of it from both at once and shows percent, size and speed per mirror.
4. The file is saved in the Downloads folder (another folder can be picked in "Save to").

Resume: press Pause during the download, then Continue; it goes on where it stopped.

runFullTrust: MirrorDL is a WinUI 3 desktop app. It writes downloads to folders the user picks and continues unfinished .part files of Firefox and aria2 in place, so it needs normal file system access.

Source code (GPL-3.0): https://github.com/Nawid3333/MirrorDL
```
