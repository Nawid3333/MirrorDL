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

Choose **All other app types** (MirrorDL is neither a game nor a social or communication app), then
answer the questionnaire:

| Question | Answer |
|---|---|
| Violence, sexual content, bad language, drugs, gambling, fear | No |
| Users can talk to each other or share content (chat, voice, uploads) | No |
| Shares the user's location | No |
| Digital purchases | No |
| Unrestricted internet access | **Yes**: it downloads from any link the user enters. This adds the notice "Unrestricted Internet" to the listing; it doesn't raise the age rating. |

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

**Store logo:** 1:1 app tile icon (300 x 300): `assets/store/app-tile-300.png`

**Search terms**

```
download manager
mirror
parallel download
resume download
checksum
aria2
```

**Copyright and trademark info:** `Copyright (C) 2026 Nawid Salehie`

**Additional license terms:** `GNU General Public License v3.0 or later: https://github.com/Nawid3333/MirrorDL/blob/main/LICENSE`

**Developed by:** `Nawid Salehie`

## Submission options

- **Publishing hold:** publish as soon as it passes certification
- **Notes for certification:**

  ```
  MirrorDL is a download manager. To test: paste these two links (the Linux kernel source on two mirrors) into the box and press Start; the file is saved in the Downloads folder.
  https://cdn.kernel.org/pub/linux/kernel/v6.x/linux-6.12.tar.xz
  https://mirrors.edge.kernel.org/pub/linux/kernel/v6.x/linux-6.12.tar.xz
  ```

- **Restricted capabilities (runFullTrust), why the app needs it:**

  ```
  MirrorDL is a desktop download manager. It saves downloads in folders the user chooses and continues unfinished downloads of other programs (the .part files of Firefox and aria2) in place, which needs normal access to the file system.
  ```
