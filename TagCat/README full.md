# TagCat (v0.59b.19)

A native Windows desktop app (WPF, .NET 9) for tagging and sorting photos and videos.

Tags live **in the filename**, in the format:

```
myphoto [beach sunset family].jpg
```

## Features

- Browse a folder (optionally including subfolders) and see real Windows-Explorer-style
  thumbnails for both images and videos — no ffmpeg or extra tools needed.
- Click any file to edit its tags in a text box; saving renames the file on disk.
- Sort the file list by **name**, **date modified**, or **date created**, ascending or descending.
- Filter by tags with a checklist: check one tag and the list narrows to files with that tag;
  the remaining checkboxes update to show only tags that still occur among the narrowed-down
  files (with a live count), so you can keep drilling down. Checked tags act as an AND filter.
- "Organize Visible Files" moves whatever is currently shown (after your filters) into
  subfolders — grouped by the selected tags, by year, by year-month, or by first letter of
  the filename.
- **Tag Library** (bottom of the Tags panel) is a vocabulary of tags kept independently of
  any folder, so autocomplete can suggest tags from your whole collection. Manage it from
  "Tag Library..." - add tags used in the current folder, or type new ones in directly,
  and remove ones you no longer want suggested.
- **Folder Options** (toolbar) holds the "Include subfolders" checkbox, exclude-specific-subfolders,
  and "Show Folders in Thumbnail Pane" - which puts folder icons in the file grid so you can click
  into a subfolder one level at a time, with an up-arrow to go back, like a simple file browser.
  While that's on, Include Subfolders doesn't apply, since you're navigating by hand instead.
  Click Apply to reload with the new settings.
- **Advanced Filtering** (below Filter by tags) holds a **Match All / Any** toggle, an
  **Exclude tags** checklist, and a **Boolean Tags** popup for expressions the checklist can't
  express, e.g. `beach AND (sunset OR sunrise) AND NOT blurry`. Unknown tag names are flagged
  as errors rather than silently matching nothing. All of it composes with the plain checklist
  above rather than replacing it.
- **Filter by type and size** — tick file types, and set a minimum and/or maximum file size.
- **Search box** above the filter pane: multi-word, any order, case-insensitive, matched against
  the filename.
- **Folder bookmarks** in the Select Folder(s) dropdown, with Ctrl+click to select and load
  several bookmarked folders at once.
- **Media viewer popup** — double-click or press Enter on a tile to open images, video or audio
  full-window, with arrow keys and the scroll wheel to move between files, a Loop toggle, and
  F11 for full screen. The thumbnail pane follows along as you navigate. Settings chooses
  between this and the system default app.
- **Thumbnail cache** so reopening a folder doesn't regenerate every thumbnail. Configurable
  folder, clear-on-exit, and an off switch in Settings; safe to delete at any time.
- **Undo** (Ctrl+Z) for tag operations, session-scoped.
- **Duplicate Finder** (right-hand panel → Duplicate Tools) finds duplicate videos, images and
  audio. Opens in its own window and scans folders you pick there, so you can carry on tagging
  while it runs. See "Duplicate Finder" below.

## Requirements to build

- Windows 10/11
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (the SDK installer on Windows
  includes everything needed for WPF/WinForms — no separate workload install required). Bumped
  from .NET 8 in v0.12 because multi-folder select in the folder picker relies on
  `FolderBrowserDialog.Multiselect`, which only shipped starting in .NET 9. If `dotnet run`
  complains it can't find a matching SDK, this is almost certainly why — install the .NET 9 SDK
  from the link above (existing .NET 8 apps on your machine are unaffected).
- The target framework is `net9.0-windows10.0.19041.0`, not plain `net9.0-windows`. The Windows
  SDK version is pinned because the Duplicate Finder decodes frames through WinRT
  (`Windows.Media.Editing`, `Windows.Graphics.Imaging`, `Windows.Storage`), and those namespaces
  are only projected into the project when an SDK version is specified.
- An internet connection the first time you build, so NuGet can download the LibVLC engine used
  for video preview (~90 MB) — see "Video preview" below.

## Build & run

Open a terminal in this folder and run:

```
dotnet run
```

That will restore, build, and launch the app in one step for quick testing.

## Build a standalone .exe

To produce a single portable `.exe` you can copy anywhere (no .NET install required on the
target machine, since the project is already configured as self-contained):

```
dotnet publish -c Release -r win-x64 --self-contained true
```

The executable will be at:

```
bin\Release\net9.0-windows10.0.19041.0\win-x64\publish\TagCat.exe
```

## Video preview

Video playback in the app uses [LibVLC](https://www.videolan.org/vlc/libvlc.html) (the engine
behind VLC), via the LibVLCSharp NuGet packages, rather than Windows' built-in `MediaElement`.
This is a deliberate choice: `MediaElement` depends on codecs bundled with Windows Media Player,
which often aren't installed on modern Windows (especially the "N"/"KN" editions) and never
support formats like `.webm` at all. LibVLC brings its own decoders, so playback works
consistently regardless of what's installed on the machine, across mp4, webm, mkv, mov, avi, wmv,
and more.

This does mean the first `dotnet build`/`dotnet run` will download the LibVLC native binaries
(~90 MB) from NuGet, and a self-contained publish will bundle them into your output folder — the
published app is noticeably larger as a result, but doesn't require anything extra to be
installed on the machine you run it on. LibVLC is LGPL-2.1-or-later licensed; that applies if you
plan to redistribute this app.

## Versioning

TagCat and the Duplicate Finder are versioned **separately**, since DF started life as its own
app and still moves at its own pace. Both are shown in Settings > About, each with its own
changelog, and both appear in the release zip's filename. The current versions are
**TagCat v0.59b** and **Duplicate Finder v0.19**.

A release only bumps the version of whichever actually changed - a DF-only fix leaves TagCat's
version alone, and vice versa.

When bumping:
- `private const string AppVersion` in `MainWindow.xaml.cs`, the `<Run Text="v..."/>` footer in
  `MainWindow.xaml`, `<Version>`/`<AssemblyVersion>`/`<FileVersion>`/`<InformationalVersion>` in
  `TagCat.csproj`, `#define MyAppVersion` in `TagCat.iss`, and this README's title line.
- For DF: `internal const string DedupeVersion` in `VideoDedupe/DuplicateFinderWindow.xaml.cs`,
  and `#define MyDedupeVersion` in `TagCat.iss`.
- Release filenames use the combined form `v0.<TagCat>.<DF>` - so v0.58 with DF v0.19 gives
  `TagCat_v0.58.19.zip` and `TagCat-Setup v0.58.19.exe`. The installer builds its own name from
  the two `#define`s, so it stays correct as long as those are bumped.
- Add an entry at the top of the relevant changelog - `TagCatChangelogText` or
  `DuplicateFinderChangelogText`, both near the bottom of `MainWindow.xaml.cs`. Keep entries
  short: what changed, not why.

Check the existing value before bumping rather than assuming what it is - they have drifted
apart before.

## Troubleshooting: "the exe doesn't do anything"

The app just rebuilt with global error handlers, so update your copy (see Build steps above)
and try again — if it fails now, you should get a message box explaining why instead of nothing
happening. A few other things to check:

1. **Run it from a terminal, not by double-clicking**, so you can see any output:
   ```
   cd bin\Release\net9.0-windows10.0.19041.0\win-x64\publish
   .\TagCat.exe
   ```
2. **Try the non-published version first** to rule out a packaging issue:
   ```
   dotnet run
   ```
   If this works but the published `.exe` doesn't, try publishing without single-file packaging,
   which is sometimes flaky with WPF resource loading:
   ```
   dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
   ```
   Then run `TagCat.exe` from that publish folder (it'll come with a bunch of `.dll` files
   alongside it — that's expected).
3. **Check Windows Event Viewer**: `Windows Logs > Application`, look for a red error from
   source `.NET Runtime` or `Application Error` around the time you ran it — the message usually
   names the exact exception.
4. **SmartScreen / antivirus**: since this isn't a signed, publicly-distributed exe, Windows
   Defender or SmartScreen may silently quarantine or block it on first run. Check your
   antivirus's recent-activity/quarantine list.


- Supported extensions are listed in `Services/LibraryService.cs` (`SupportedExt`) — add more
  if you shoot in a format not covered (e.g. RAW formats).
- The tag delimiter inside the brackets is a single space; if you'd rather use commas, that's
  a one-line change in `Models/TagParser.cs` and `MainWindow.xaml.cs`.
- "Organize" **moves** files (not copies). Try it on a test folder first if you want to be safe.

## Duplicate Finder

Merged in at v0.20 from what was previously a separate app (its own history ran to v0.05, kept
in `VideoDedupe/`). Open it from the right-hand panel under **Duplicate Tools**.

Scans one or more folders (Browse is multi-select; the box takes a semicolon-separated list you
can edit by hand), and opens pre-filled with whatever folders TagCat currently has loaded.
A file reachable from two listed folders is only scanned once.

Use the **Look for duplicate** checkboxes to choose videos, images, audio, or a combination.
Audio is only available in Express mode - see below.

There are four scan modes. **Express** works differently from the other three: it checksums
file content, so it only finds byte-for-byte identical copies, but a match is provable rather
than a judgement call, it works on any file type including audio, and it is by far the fastest.
Files are only read at all if another file already shares their exact size, which is most of
why. Its own options panel adds optional restrictions by filename and created/modified
timestamps, plus a file size range - all off by default, since the checksum alone is already a
complete answer.

**Quick / Balanced / Thorough** compare files by content rather than by hash, so they still
match a copy that has been re-encoded, resized, cropped, letterboxed or trimmed:

1. Frames are sampled and reduced to greyscale, then normalised to a fixed grid — this is what
   makes resolution, aspect ratio and letterboxing irrelevant.
2. Each frame becomes a 64-bit DCT perceptual hash.
3. A locality-sensitive hash index picks which pairs are worth comparing properly, so a large
   library doesn't turn into an all-pairs comparison.
4. A sliding sequence aligner catches trimmed or offset copies.

Notes:

- **Presets.** Quick / Balanced / Thorough set sampling density and strictness; the advanced
  panel shows the real values behind whichever preset is selected.
- **Fingerprint cache** lives in `%LocalAppData%\TagCat\fingerprints.db`, so repeat scans
  are fast. Changing sampling settings invalidates cached entries automatically (they are keyed
  by a profile key), so you never compare fingerprints built under different settings.
- **Deletion always goes through the Recycle Bin**, and only for files you tick. There is a guard
  against emptying an entire group.
- **Partial matches** (a trim vs. the full version) are flagged, because a clip is not
  interchangeable with the video it came from.
- **Images** are handled by treating a still as a one-frame video, so the same hashing that
  ignores resolution, compression and cropping applies to photos. JPEG, PNG, BMP, GIF and TIFF
  always work; HEIC and WebP work when the relevant Windows codec is installed.
- **Audio works in Express only**, and the checkbox greys out in the other three modes rather
  than being hidden, so the gap stays visible. Express never decodes anything - it compares raw
  bytes - so file type is irrelevant to it. *Perceptual* audio matching, the equivalent of what
  Quick/Balanced/Thorough do for video, is still not implemented: perceptual video hashing works
  on the *picture* in a frame, and audio has no frames. Doing it properly means decoding to PCM,
  building a spectrogram, and hashing spectral bands over time (the Haitsma-Kalker approach, or
  something chromaprint-like). The rest of the machinery — the LSH candidate index, the sliding
  sequence aligner, the fingerprint cache, the review UI — is format-agnostic and would be
  reused as-is; it is the decode-and-fingerprint front end that would have to be written.
- **Suggested keeper.** For content matches, the highest resolution then longest then largest
  wins. For Express matches every one of those ties by definition, so the filename decides
  instead: a clean name beats "- Copy", which beats "name_2", which beats "name (2)", with the
  oldest file breaking any remaining tie.
- **More Options** on each file in a group: open its folder, open the folder in TagCat with that
  file selected, open every folder in the group at once, exclude the folder for this session or
  permanently, exclude the file permanently, or copy its path.
- **Exclusions.** Folders and files excluded from scanning are reviewed and edited in Settings,
  in two separate lists. Each scan mode's advanced options has its own checkbox to include them
  anyway, just for that scan.
- **Could not read** files are listed under the results; clicking through shows every one with
  its reason.
- **Codec support is whatever Windows provides.** Files that can't be decoded are reported, not
  silently skipped — press **What happened?** after any scan for the per-file detail, and
  "See the full log." at the bottom of that window for the complete log. ffmpeg is used if it
  happens to be on PATH, but nothing is bundled, so there is no licensing implication for
  redistribution.

## Scripts

All of these live in this `TagCat` folder. There is also a `RUN.bat` at the zip's top level
that simply calls the one in here, so you can start the app without opening the folder first.

- `RUN.bat` — build and run. Checks for a .NET 9 SDK first and explains what to install if it's
  missing, since that's the most likely failure after the v0.20 framework change.
- `CLEAN-REBUILD.bat` — deletes `bin\` and `obj\` then builds and runs. Use this when you've
  copied a new version over an old folder; stale build output is the usual reason a change
  appears not to have applied.
- `MAKE-EXE.bat` — publishes a self-contained single-file `TagCat.exe` into `StandaloneApp\`
  that runs without the .NET runtime installed. Large (~480MB), because it bundles the whole
  .NET runtime and all of LibVLC.

### Building the installer

For a small download instead of the ~480MB standalone exe:

- `MAKE-INSTALLER-PACKAGE.bat` — publishes a framework-dependent build into `InstallerPackage\app`
  with the .NET runtime and LibVLC deliberately stripped out, then reports the resulting size.
- `TagCat.iss` — an [Inno Setup](https://jrsoftware.org/isinfo.php) script that packages that
  folder into a normal `TagCat-Setup.exe`, with a Start Menu entry and an uninstaller. Open it
  in Inno Setup 6.1+ and press Compile. Nothing else to install - it uses Inno Setup's own
  built-in download support.
- `Install.bat` / `Install.ps1` — a plainer alternative to the Inno installer, for the same
  `InstallerPackage` folder. Same detect-and-fetch behaviour, no compiler needed, but no
  Start Menu entry or uninstaller.

Either way, the resulting installer checks the target machine for the .NET 9 Desktop Runtime
and for VLC, asks before downloading, and fetches only what's missing. An existing VLC install
is detected and reused.

**Run `MAKE-INSTALLER-PACKAGE.bat` before compiling `TagCat.iss`** — the script packages
whatever is currently in `InstallerPackage\app`, so skipping that step ships stale files.
