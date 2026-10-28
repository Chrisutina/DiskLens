# DiskLens

**English** · [简体中文](README.zh-CN.md) · [日本語](README.ja.md)

DiskLens is a disk space analyzer for Windows. It scans a drive or folder, shows where the space goes on a treemap colored by what the data is for (apps, games, caches, dev projects, media…), and helps you clean up without deleting the wrong thing.

- **Fast scans.** A regular scan needs no administrator rights. On local NTFS volumes, an MFT deep scan (administrator) reads the master file table directly and reconciles to the byte with the volume's used space.
- **Honest numbers.** Hard links are counted once. Space the scan can't explain (folders without access, shadow copies, NTFS metadata) is shown separately as "Not counted" or "Unexplained space" instead of being blamed on some file.
- **Purpose, role and owner.** Every item is classified by purpose and data role (program, settings, cache, saves, model weights…) and, where there is evidence, by the app, game or project it belongs to — always with the basis shown, and never as a reason to delete.
- **Safe cleanup.** Suggestions are grouped into *Safe to clean*, *Check first* and *Don't delete · Use the right way*. Deletions list every target and its protection status first; the Recycle Bin is the default, and permanent deletion asks again. Protected system locations can't be deleted at all.
- **Cleanup script.** "Copy script" produces a PowerShell script that deletes only what existed at scan time and hasn't changed since: each target's file identity is checked at run time, and anything created, changed or moved in after the scan started is kept.
- **Live tracking.** Files that other programs delete after the scan disappear from the map, lists and search without a rescan.
- **Search.** Names, wildcards, `ext:mp4,mkv`, `size:>1gb`, `type:file`, exclusions like `-cache`, `a | b` for alternatives, pinyin initials (`wd` finds 文档), and close matches when nothing matches exactly (`dwnld` finds *download*).
- **Three languages.** English, Simplified Chinese and Japanese. Switch from the language button at the top left of the start page; the choice is remembered.

## Download

When a release is available, download `DiskLens-<version>-win-x64.zip` from [Releases](https://github.com/Chrisutina/DiskLens/releases), **extract it** to any folder, and run `DiskLens.exe` inside. Running the exe straight from the zip window does not work: the zip tool copies out only the exe, without the files next to it. If no release is listed, build from source below.

## Requirements

- Windows 10 or 11 (x64)
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (preinstalled on current Windows)

No account or network connection is needed. The only network access is a web search you start yourself, which sends the item's name to the search engine you picked, in your default browser.

## Build and run

You need the .NET 10 SDK, Node.js and pnpm. In PowerShell:

```powershell
.\build.ps1          # checks and builds the web UI, then builds the Release desktop app
.\build.ps1 -Check   # also runs the backend sandbox checks and a real WebView2 integration check
```

Then double-click `Start-DiskLens.cmd` (or run `.\Start-DiskLens.ps1` in PowerShell). The first run builds DiskLens and takes a few minutes; after that it starts right away. You can also open the build directly:

```text
src\DiskLens\bin\Release\net10.0-windows\DiskLens.exe
```

Pick **Scan** on a drive card; the scan method is chosen automatically. To use the MFT deep scan, restart as administrator from the top right of the start page.

Command line: `DiskLens.exe [--scan <folder>] [--mode auto|mft|walk]`.

## Where data is stored

Everything stays on your PC, in a `data` folder next to `DiskLens.exe` (or `%LOCALAPPDATA%\DiskLens` when the program folder isn't writable):

- `settings.json` — the interface language
- `scan-summaries-user.json` / `scan-summaries-admin.json` — a summary of the latest scan of each place (top items and recognized apps), used to compare consecutive scans
- `webview2-user` / `webview2-admin` — the WebView2 profile

The scanned file tree itself lives only in memory.

## Development

```powershell
pnpm --dir web check     # svelte-check
pnpm --dir web test      # unit and contract tests
pnpm --dir web dev       # browser preview with simulated data ("Demo mode")
dotnet run --project tools/DiskLens.Check -- all   # backend checks, in .sandbox only
dotnet run --project tools/DiskLens.UiCheck        # opens the real window and drives it
```

The browser preview never touches real files; it says *Demo mode · Simulated data*. To run the desktop app against the dev server, set `DISKLENS_DEV_URL=http://localhost:5173` (local HTTP/HTTPS addresses only).

Tests create and delete files only inside `.sandbox`. Interface text lives in `web/src/lib/locales` (the Chinese source text is the message ID) and in `L.T(...)` calls in the backend; see [docs/DESIGN.md](docs/DESIGN.md) §8.8. The full design and bridge contract are in [docs/DESIGN.md](docs/DESIGN.md) (in Chinese).

## Keyboard

| Keys | Action |
|---|---|
| `Ctrl+F` | Search |
| `Alt+←` / `Alt+→` | Back / forward |
| `Alt+↑` | Up one level |
| `F5` | Rescan |
| `Delete` | Move to Recycle Bin |
| `Shift+Delete` | Delete permanently |
| `Esc` | Close the open panel or menu |

## License

[MIT](LICENSE)
