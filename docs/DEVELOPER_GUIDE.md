# NetSynq Developer Guide

<p align="center"><img src="../assets/logo.png" alt="NetSynq" width="260"></p>

This guide explains how NetSynq is built: the technology choices, the architecture, the network protocol, the algorithms, and how to extend or test the code. To *use* the program, read the [User Guide](USER_GUIDE.md).

**Contents**

1. [Overview and design goals](#1-overview-and-design-goals)
2. [Technology stack](#2-technology-stack)
3. [Building](#3-building)
4. [Repository layout](#4-repository-layout)
5. [Architecture](#5-architecture)
6. [The Core library, file by file](#6-the-core-library-file-by-file)
7. [Network protocol](#7-network-protocol)
8. [Internationalization (i18n)](#8-internationalization-i18n)
9. [The UI layer](#9-the-ui-layer)
10. [Compaction internals](#10-compaction-internals)
11. [Settings and data files](#11-settings-and-data-files)
12. [Security model](#12-security-model)
13. [Testing](#13-testing)
14. [How to...](#14-how-to)
15. [Conventions and known limitations](#15-conventions-and-known-limitations)
16. [Release checklist](#16-release-checklist)

---

## 1. Overview and design goals

NetSynq is a Windows desktop application with two features:

- **Sync**: one-way mirroring of a folder from a *master* PC to a *slave* PC over the LAN, driven by the slave. The master runs a tiny read-only HTTP server; the slave compares and pulls.
- **Compact**: in-place packing of folders with many small files into zip archives, with verified, lossless restore.

The goals that shaped the code:

| Goal | Consequence |
|---|---|
| **Zero dependencies** | No NuGet packages. Everything is the .NET base class library plus WinForms. The build works offline. |
| **No installation, no admin rights** | Single-file `.exe`. The server is a hand-written HTTP/1.1 loop on `TcpListener`, so no `HttpListener` URL reservation (`netsh http add urlacl`) is needed. |
| **Never lose data** | Destructive operations follow *write → verify → re-check → commit → delete*. Every sync and compaction step is previewable. |
| **LAN only** | The server binds to one chosen LAN IP, never to `0.0.0.0`. |
| **Testable core** | All logic lives in `Core/`, which does not reference WinForms and is tested end-to-end on loopback. |

## 2. Technology stack

| Area | Choice | Why |
|---|---|---|
| Language / runtime | **C# 12, .NET 8** (`net8.0-windows`) | Current LTS; `SocketsHttpHandler`, `Parallel.ForEachAsync`, `Convert.ToHexString` and other APIs used here need a modern runtime. |
| UI | **Windows Forms**, restyled in code | A custom theme (`Theme.cs`) gives a modern look without WPF and without third-party controls. All windows are C# code, no designer files. |
| Networking | `System.Net.Sockets` (`TcpListener`, `UdpClient`) for the server, `HttpClient` + `SocketsHttpHandler` for the client | See [section 7](#7-network-protocol). |
| Hashing | `System.Security.Cryptography` (SHA-256) | Used for content comparison and for zip verification. |
| Compression | `System.IO.Compression` (`ZipArchive`, `GZipStream`) | Zip creation/verification; manifest gzip. Multi-volume zips are implemented by hand (`ZipVolumes`). |
| Serialization | `System.Text.Json` | Manifest, settings, hash cache, zip manifest, i18n dictionaries. |
| Text diff | Own implementation of **Myers' O(ND)** algorithm (`LineDiff`) | No diff library needed. |
| Tests | Plain console project with a `Check()` helper | No test framework dependency. |

Project settings worth knowing (`src/NetSynq/NetSynq.csproj`): `OutputType=WinExe`, `Nullable` and `ImplicitUsings` enabled, `InvariantGlobalization=true`, `SatelliteResourceLanguages=en`, `EnableWindowsTargeting=true` (lets you build the Windows project from a non-Windows machine), `ApplicationManifest=app.manifest` (Windows 10/11 compatibility and `longPathAware`).

## 3. Building

Requirements: the **.NET 8 SDK** (or newer). Older SDKs, such as 5.0, can coexist but cannot build the project; the build scripts detect this and print the `winget install Microsoft.DotNet.SDK.8` command.

| Script | Command it runs | Output |
|---|---|---|
| `build.bat` | `dotnet publish … -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o dist` | `dist\NetSynq.exe`, about 200 KB, needs the .NET Desktop Runtime 8 on the target |
| `build-standalone.bat` | `dotnet publish … -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true … -o dist-standalone` | `dist-standalone\NetSynq.exe`, 70-100 MB, needs nothing on the target. Internet is needed once to download runtime packs |
| `test.bat` | `dotnet run --project tests\CoreTests\CoreTests.csproj -c Release` | runs the Core tests |

Both build scripts first check that `dotnet` exists and that an SDK 8+ is installed, then delete the output folder (failing with a clear message if the exe is still running), then publish.

Do **not** enable `PublishTrimmed` for the WinForms project without extensive testing: trimming WinForms is risky.

To work in Visual Studio / VS Code / Rider, open `src/NetSynq/NetSynq.csproj` (the app) or `tests/CoreTests/CoreTests.csproj` (the tests); there is no solution file.

## 4. Repository layout

```
NetSynq/
├── assets/logo.png            logo used by the documentation
├── docs/                      USER_GUIDE.md, DEVELOPER_GUIDE.md
├── src/NetSynq/
│   ├── NetSynq.csproj         app project (WinExe, net8.0-windows)
│   ├── app.manifest           Windows compatibility, long paths
│   ├── app.ico, logo.png      icon and wordmark (logo.png is embedded)
│   ├── Core/                  all logic, no WinForms dependency
│   ├── Lang/it.json, en.json  UI and engine texts (embedded)
│   └── UI/                    WinForms interface
├── tests/CoreTests/           end-to-end tests of Core
├── build.bat, build-standalone.bat, test.bat
├── LICENSE                    MIT
└── README.md
```

`Core/` files (about 3,000 lines) and `UI/` files (about 2,400 lines) are in the same assembly but are strictly layered: **`Core` never references `UI`, and never references `System.Windows.Forms`.** That rule is what lets the tests compile the Core sources directly (see [Testing](#13-testing)).

## 5. Architecture

### 5.1 Components

```
                       ┌───────────────────────── MASTER PC ──────────────────────────┐
                       │  UI: Master tab (MainForm.Server.cs)                         │
                       │      │                                                       │
                       │      ▼                                                       │
                       │  SyncServer ── Scanner ── ExcludeMatcher                     │
                       │      │  └──── HashCache (hash-master.json)                   │
                       │  Discovery.Responder (UDP 48766)                             │
                       └──────┬───────────────────────────▲───────────────────────────┘
                              │ TCP 48765  HTTP/1.1 GET   │ UDP broadcast
                              │ header X-Token            │ "NETSYNQ?1"
                       ┌──────▼───────────────────────────┴───────────────────────────┐
                       │  RemoteClient          Discovery.FindAsync                   │
                       │      │                                                       │
                       │  Planner ──► SyncPlan ──► Simulator (dry run)                │
                       │      │            │                                          │
                       │      │            └────► Syncer ──► files on disk           │
                       │  Scanner, HashCache (hash-slave.json), LineDiff              │
                       │  UI: Slave tab (MainForm.Client.cs), DiffViewForm            │
                       └───────────────────────── SLAVE PC ───────────────────────────┘

   Independent of the above (single PC):
   UI: Compact tab ─► CompactAnalyzer ─► CompactPlan ─► CompactPacker ─► .zip / .zip.001…
                                                    └► CompactRestorer ─► original folders
```

The same executable plays either role. Sync is **pull-based**: the slave asks, the master answers. This keeps the master simple (read-only, stateless) and means the slave decides everything.

### 5.2 Sync data flow

1. **Scan.** The master (`SyncServer`, on `/manifest`) and the slave (`Planner`, locally) each build a flat list of `Entry` objects with `Scanner`.
2. **Plan.** `Planner.BuildAsync` joins the two lists by relative path and assigns each item a `DiffStatus`. Where content must be verified it asks both sides for SHA-256 hashes in parallel. The result is a `SyncPlan`.
3. **Review.** The UI shows the plan. The user can toggle `DiffItem.Selected` per item (`SyncPlan.SetSelected` propagates to children of a folder). `Simulator` can render the plan as text without touching anything.
4. **Apply.** `Syncer.ApplyAsync` executes the **selected** part of the plan.
5. **Verify.** The UI runs a new comparison.

Nothing in steps 1-3 and 5 writes to disk (other than the hash cache).

### 5.3 Concurrency model

- The UI thread never blocks on I/O: long operations are `async` methods in the UI that `await` Core calls, with a `CancellationTokenSource` (`_cts`) stored in the form and `SetBusy(bool)` toggling the controls.
- Progress is reported through `IProgress<ProgressInfo>`; the UI throttles updates to roughly every 150 ms.
- File scanning and hashing run in `Task.Run`. Hashing uses `Parallel.ForEachAsync` (3 workers); copying uses a configurable degree of parallelism (1-8, default 3).
- The server handles each TCP connection in its own task; shared counters use `Interlocked`.

## 6. The Core library, file by file

| File | Responsibility |
|---|---|
| **`Models.cs`** | Data types: `Entry` (file/folder record), `Manifest`, `CompareMode`, `DiffStatus`, `DiffItem`, `SyncPlan` (with totals computed only over *selected* items), `SyncOptions`, `SyncResult`, `ProgressInfo`. `Entry` uses short JSON names (`p`, `d`, `s`, `m`, `a`) to keep the manifest small. |
| **`Scanner.cs`** | Iterative (stack-based, not recursive) directory walk producing `Entry` lists. Skips reparse points (symlinks and junctions), skips and reports `*.nspart` leftovers, applies `ExcludeMatcher`, records only the `ReadOnly/Hidden/System` attributes. |
| **`PathUtil.cs`** | `Resolve()` (the path-traversal guard), relative-path helpers, `SameTime()` (2 s tolerance), `ClearReadOnly`, `ForceDeleteDirectory`, `FormatBytes`; plus **`ExcludeMatcher`**, the `;`-separated glob matcher. |
| **`SyncServer.cs`** | The read-only HTTP/1.1 server. See [section 7](#7-network-protocol). |
| **`RemoteClient.cs`** | `HttpClient` wrapper: `PingAsync`, `GetManifestAsync`, `GetHashAsync`, `DownloadAsync` (resumable). |
| **`Discovery.cs`** | UDP broadcast discovery: `Responder` on the master, `FindAsync` on the slave. |
| **`NetworkHelper.cs`** | Lists usable IPv4 interfaces, flags VPN/virtual ones (`NicInfo.Suspect`), picks the best interface (`Pick`), subnet and broadcast arithmetic. |
| **`HashCache.cs`** | Persistent SHA-256 cache keyed by `(full path, size, mtime ticks)`. |
| **`Planner.cs`** | Builds a `SyncPlan` from master manifest + local scan. |
| **`Simulator.cs`** | Dry run: applies the same filters and logic as `Syncer` but only produces a `SimulationReport`. |
| **`Syncer.cs`** | Applies a plan: free-space check, folder creation, date fixes, parallel downloads with retry, deletions, cleanup. |
| **`LineDiff.cs`** | Line-based diff (Myers), common prefix/suffix trimmed first for speed; returns `null` if the files are too different (`maxD`). |
| **`CompactModels.cs`** | Types for compaction: options, plan, items, results, reports, and the zip's internal manifest. |
| **`CompactAnalyzer.cs`** | Chooses which folders to zip (top-down analysis, then a bottom-up merge pass). |
| **`CompactPacker.cs`** | Creates, verifies and commits zips; simulation. |
| **`CompactRestorer.cs`** | Finds NetSynq zips and restores folders. |
| **`ZipVolumes.cs`** | Byte-split volumes (`.zip.001`…), a read stream across volumes. |
| **`AppSettings.cs`** | Persisted user settings and token generation/validation. |
| **`I18n.cs`** | Dictionary loading and the `Tr()` lookup. |

### 6.1 `Planner`: how a status is chosen

For each master entry `r` and matching slave entry `l` (paths compared case-insensitively):

| Condition | Status |
|---|---|
| no slave entry | `New` |
| one is a folder, the other a file | `Modified` + `TypeConflict` (the slave's item is removed first) |
| both folders | `Same` |
| sizes differ | `Modified` |
| sizes equal, mode **Fast** | `Same` if dates match (±2 s), else `Modified` |
| sizes equal, mode **Smart** | `Same` if dates match, else queued for hashing |
| sizes equal, mode **Full** | always queued for hashing |
| after hashing: hashes equal | `Same` if dates match, else **`TouchOnly`** |
| after hashing: hashes differ | `Modified` |
| hashing failed | `Modified` with an explanatory note |
| slave entry with no master counterpart | `Delete` |

`TouchOnly` is the key optimization: the content is identical, so only the modification time is corrected, and nothing is transferred.

Safety check: if the master list is empty while the slave is not, `Planner` throws instead of planning to delete everything.

The 2-second date tolerance exists because different file systems (NTFS, FAT, network shares) store timestamps with different resolution.

### 6.2 `Syncer.ApplyAsync`: the order of operations

1. **Type conflicts:** delete the slave item that is the wrong kind (file vs folder).
2. **Create folders** (including empty ones), shortest path first, applying attributes.
3. **Touch:** for `TouchOnly` files, set the modification time and attributes.
4. **Copy** `New` and `Modified` files in parallel. Each file:
   - downloads into `<name>.<mtime-hex>.nspart` (resuming if a partial exists),
   - checks the length equals the expected size,
   - removes a conflicting folder if present, clears read-only on an existing file,
   - `File.Move(part, final, overwrite: true)`, then sets the modification time and attributes.
   - On failure it retries up to **3 attempts** with `1000 * attempt` ms back-off, except for authentication errors and missing files, which fail immediately.
5. **Delete** (only if `DeleteExtra`): files first, then folders deepest-first, and only if empty.
6. **Cleanup** of stale `*.nspart` files, only if there were no errors.

Cancellation sets `SyncResult.Cancelled` and leaves partial files on disk for the next run to resume.

`Syncer.CheckSpace` estimates the space needed as `download − replaced − (deleted if enabled) + 200 MB` and compares it to the free space on the destination drive (skipped for UNC paths).

## 7. Network protocol

### 7.1 TCP: the file server

`SyncServer` is a minimal HTTP/1.1 implementation over `TcpListener`.

- **Binding:** only to the `IPAddress` chosen in the UI (`new TcpListener(_bind, _port)`), default port **48765**. Port `0` is allowed in tests (OS picks a free port, exposed as `Port`).
- **Methods:** `GET` only; anything else returns `405`.
- **Keep-alive:** a connection serves many requests; request headers are limited to a 16 KB buffer and an idle timeout of 60 s applies while waiting for a request.
- **Authentication:** every request must carry `X-Token: <token>`. Comparison is done with `CryptographicOperations.FixedTimeEquals` (constant time). Failure returns `401` and logs the remote address.

| Route | Response |
|---|---|
| `GET /ping` | JSON `{ "name": <machine>, "proto": 1, "root": <folder name> }` |
| `GET /manifest` | JSON `Manifest` (`name`, `proto`, `entries[]`); gzip-compressed when the client sends `Accept-Encoding: gzip` |
| `GET /hash?path=<rel>` | JSON `{ "sha256": <hex>, "size": <n> }`, served from the server's `HashCache` |
| `GET /file?path=<rel>` | File bytes. Supports `Range: bytes=start-[end]` → `206 Partial Content` with `Content-Range`; invalid range → `416` |

Other status codes: `404` (unknown route or file), `423` (file locked by another process; the server opens files with `FileShare.ReadWrite | FileShare.Delete`).

`<rel>` is the URL-escaped relative path with `/` separators. The server rejects it (as not found) when `PathUtil.Resolve` fails, when it matches the exclusion list, or when it ends with `.nspart`.

Manifest entry (`Entry`):

```json
{ "p": "src/a.txt", "d": false, "s": 1234, "m": 639000000000000000, "a": 0 }
```

`m` is the last write time in UTC **ticks**; `a` is the `ReadOnly|Hidden|System` subset of `FileAttributes`. Folders have `s = 0` and `m = 0`.

The protocol version is `SyncServer.ProtocolVersion = 1`. The manifest carries it, though the client does not currently enforce a match.

### 7.2 Client behavior (`RemoteClient`)

`SocketsHttpHandler` with `AutomaticDecompression = GZip`, up to 16 connections, connect timeout 8 s, idle pool timeout 30 s. The `HttpClient` timeout is infinite (large files); `/ping` applies its own 10 s timeout through a linked cancellation token.

`DownloadAsync(path, partFile, expectedSize, onBytes, ct)`:

- if `partFile` exists and is larger than expected, it is discarded; otherwise its length is used as the resume offset (`Range: bytes=<existing>-`);
- if the server answers `206` the file is appended to, otherwise it is rewritten from zero;
- `onBytes` also receives the already-present byte count so progress stays consistent;
- after the transfer the length must equal `expectedSize`, otherwise `IOException`.

### 7.3 UDP: discovery

| Item | Value |
|---|---|
| Port | **48766** (`Discovery.UdpPort`) |
| Query (slave → broadcast) | UTF-8 `NETSYNQ?1` |
| Reply (master → slave, unicast) | UTF-8 `NETSYNQ!1|<name>|<tcpPort>` |

`Discovery.FindAsync` sends the query from every candidate interface to its subnet broadcast and to `255.255.255.255`, and collects replies for a time window. `Discovery.Responder` binds `0.0.0.0:48766` (with `ReuseAddress`) but **answers only if the sender is in the same subnet** as the LAN interface chosen for the server (`NetworkHelper.SameSubnet`). Requests arriving from the VPN subnet are silently ignored.

Discovery carries no secret: it only announces name and port. The token is still required for anything useful.

### 7.4 Choosing the right network interface

`NetworkHelper.GetCandidates()` returns active IPv4 interfaces (no loopback, no `169.254.x.x`). An interface is flagged `Suspect` (probable VPN or virtual adapter) if its type is tunnel/PPP/SLIP or its name/description matches a keyword regex (`vpn`, `tap`, `wireguard`, `openvpn`, `cisco`, `anyconnect`, `fortinet`, `tailscale`, `zerotier`, `vmware`, `vbox`, `hyper-v`, `docker`, `wsl`…).

`NetworkHelper.Pick` scores each candidate: **+100** if it is on the same subnet as the known peer IP, **+50** if not suspect, **+20** if it has a private (RFC 1918) address, **+1** if it has no gateway. The highest score wins.

## 8. Internationalization (i18n)

**Design:** every user-visible string, in both the UI and the Core engine messages, goes through `I18n.Tr(key, args...)`. There are no literal user-facing strings in the code.

| Item | Detail |
|---|---|
| Dictionaries | `src/NetSynq/Lang/it.json`, `en.json`: flat JSON objects, `"key": "text"`. Embedded in the assembly with logical names `lang.it.json` / `lang.en.json`. |
| Base language | **Italian** is the fallback: lookup order is *current language → Italian → the key itself*. |
| Override at runtime | A `lang` folder next to the exe with `it.json` / `en.json` overrides only the keys it contains. Comments and trailing commas are allowed in these files. |
| Placeholders | `{0}`, `{1}`… with optional format (`{0:N0}`). Literal braces must be doubled, only in texts that have arguments. |
| API | `I18n.Tr`, `I18n.SetLanguage(code)`, `I18n.Lang`, `I18n.Languages`, event `I18n.Changed` |
| Key naming | Prefix by area: `app.` (shell), `srv.` (Master tab), `cli.` (Slave tab), `cp.` (Compact tab), `cmp.` (compaction engine), `sync.` (sync engine), `sim.` (simulation window), `diff.` (content diff window), `net.` (network), `file.` (settings/validation) |

**Live switching in the UI.** `UI/Loc.cs` keeps a list of bindings. Instead of `button.Text = Tr("key")`, the UI calls `Loc.Text(button, "key")`, which sets the text and registers a closure; `Loc.Refresh()` (called on `I18n.Changed`) re-applies all of them. Helpers: `Loc.Page`, `Loc.Tip`, `Loc.Combo`, `Loc.Header`, `Loc.Add`. Dialogs created on demand read their strings at construction and are not re-translated while open.

Text already generated before a language change (log lines, per-row notes) stays in its original language until regenerated.

## 9. The UI layer

### 9.1 Structure

| File | Role |
|---|---|
| `Program.cs` | Entry point (`[STAThread]`). Loads settings, sets language and theme **before** creating the window, installs a thread-exception handler. |
| `MainForm.cs` | The main window: header, tab strip, three tabs, log panel; language/theme handlers; saving settings on close. |
| `MainForm.Server.cs` | Master tab (`partial class MainForm`). |
| `MainForm.Client.cs` | Slave tab: connection, options, comparison grid, sync, simulation, content diff. |
| `MainForm.Compact.cs` | Compact tab. |
| `SimulationForm.cs`, `DiffViewForm.cs` | Dialogs. |
| `Theme.cs` | Light/dark palettes, binding, custom header bar, tab strip, buttons, grids, cards. |
| `NativeTheme.cs` | P/Invoke (`DwmSetWindowAttribute`, `SetWindowTheme`) for the dark title bar and scrollbars. |
| `Loc.cs` | i18n bindings (see above). |
| `AppIcon.cs` | Applies the embedded icon. |

`MainForm` is one `partial class` split by area, so each tab is in its own file but shares fields and helpers.

### 9.2 Conventions

- **No designer.** Controls are constructed in `Build…Tab` methods.
- **The comparison grid is virtual** (`CellValueNeeded`), so it stays responsive with tens of thousands of rows. Filtering and sorting produce a view list (`_view`) that the grid reads from, instead of manipulating grid rows.
- **Busy state.** `SetBusy(true)` disables inputs while an operation runs and enables *Cancel*; `_cts` is created per operation.
- **The title bar** shows icon, "NetSynq" and `v<Major>.<Minor>` read from the assembly version, which comes from `<Version>` in the csproj.
- **Destructive flows** always go *Analyze → Preview → Execute*, with per-item checkboxes, a *Simulate* option where applicable and a final report with numbers.

### 9.3 Theming

`Theme` holds two `Palette` objects (light, dark) and static color properties that read the active one. Controls are styled through `Theme.ApplyTo(root)`, and `Theme.Bind(control, apply)` registers a callback re-run on `Theme.Changed`. Controls can carry a `Tag` such as `"theme:log"` to select a special style. `Theme.Attach(form)` makes dialogs follow the theme as well. Tooltips, context menus, numeric up-down arrows and the common file dialogs are drawn by Windows and stay light.

## 10. Compaction internals

### 10.1 Analysis (`CompactAnalyzer.Analyze`)

Top-down walk from the chosen root with an explicit stack. For each folder, in order:

1. **`AlwaysZip` name** (default `node_modules`, not applied to the root): the folder is walked once without size limits and, if it is zippable, becomes a unit (`Forced = true`); its contents are not otherwise inspected and N/K and the size limits do not apply. Reparse points or unreadable files still block it, in which case it is listed as *Skipped* and analysis continues inside it as for any other folder.
2. **`ZipSubfolders` name** (default `profiles`): the folder itself is *not* zipped; each direct subfolder becomes a forced unit (no size limits, no descent). Empty or non-zippable subfolders are pushed back on the stack and analyzed normally. If `PartialPacks` is on and the folder has more than N direct small files, a `SmallFiles` unit (`_piccoli.zip`) is added. The analysis then moves on without merging.
3. **Otherwise**, count direct small files (size < K, ignoring `.zip`, `.zip.NNN`, temp files and excluded names). If there are **more than N**, the whole subtree is walked (`Walk`) and checked with `Subtree.Problem(options)`:
   - no problem → the folder becomes a candidate and the walk does **not** descend further;
   - a problem and `PartialPacks` on → a `SmallFiles` candidate (carrying the reason) is added, and the subfolders are analyzed;
   - a problem and `PartialPacks` off → the folder is listed as *Skipped* with the reason, and the subfolders are analyzed.
4. If N/K is not met, the folder is not a candidate and its subfolders are analyzed.

`Walk(…, stopOn: options)` stops early as soon as a folder is known not to be zippable (limits exceeded, symlinks, excluded or unreadable items), to avoid reading enormous trees pointlessly; counts are then partial.

A **second, bottom-up pass** (if `MergeParents`): a folder whose content would be only the zips of its subfolders (more than N entries) is replaced by a single merged unit whose `Children` are the units it replaces. If the user unchecks the merged item, `CompactPlan.SelectedCandidates` expands it back into its children.

The starting folder is never zipped.

### 10.2 Packing (`CompactPacker.PackOne`)

```
snapshot ─► write <name>.zip.nstmp (each file hashed while copied in)
         ─► split into .001/.002… if > VolumeBytes
         ─► re-open and verify every entry's SHA-256 against the manifest
         ─► snapshot again; if different → abort, nothing deleted
         ─► rename to final name (never overwrite: "name (2).zip")
         ─► delete archived files (only those still unchanged), then empty folders
```

- The zip contains an extra entry `.netsynq-manifest.json` (`ZipManifest`): tool name, version, root folder name, root mtime/attributes, `partial` flag, creation time, and for every file and folder its relative path, size, mtime ticks, attributes and (for files) SHA-256. This is what makes verification and exact restore possible, and how NetSynq recognizes its own archives.
- Zip entry timestamps are clamped to the valid zip range (1980-2107).
- Compression level per file: `Auto` stores known-compressed extensions (`.jpg`, `.mp4`, `.zip`, `.pdf`, `.docx`, …) and uses `Fastest` for the rest; `None` and `Max` apply to all.
- Free space is checked before writing (`source size + 32 MB`) and again before splitting.
- Files modified or new after the snapshot are **left in place** (`CompactItemResult.Leftover`).

### 10.3 Volumes (`ZipVolumes`, `CompactPacker.SplitFile`)

`System.IO.Compression` cannot create or read multi-volume zips, and the project avoids dependencies, so oversized archives are cut at **raw byte boundaries** into `name.zip.001`, `.002`, … The concatenation is byte-identical to the original zip, which any tool (and `copy /b`) can rebuild; 7-Zip opens the set from `.001`. `VolumeReadStream` presents the volumes as one seekable stream so `ZipArchive` can verify and restore from them without rebuilding the file.

The threshold `CompactPacker.VolumeBytes` defaults to `0xFFFFFFFF` (4 GB − 1, the FAT32 limit). It is a static property only so tests can lower it; it is not exposed as a user setting.

### 10.4 Restore (`CompactRestorer`)

`Analyze` scans for `*.zip` and `*.zip.001`, reads each manifest, and builds `RestoreItem`s (including whether the target already exists and how many volumes are present). `Restore` extracts into a temporary directory, verifies every file's hash, restores timestamps and attributes (files first, then folders bottom-up, then the root, so parent timestamps are not disturbed), refuses manifest entries whose paths would escape the folder, renames the temp folder into place, and only then deletes the zip (and all volumes). For partial (`_piccoli`) archives it places files directly in the folder without overwriting. A missing volume marks the archive incomplete and changes nothing.

## 11. Settings and data files

`AppSettings` is a plain class serialized with `System.Text.Json` to **`%LOCALAPPDATA%\NetSynq\settings.json`**. `Load()` falls back to defaults on any error; `Save()` is best-effort. It stores the selected tab, language (default `"it"`), theme, and all Master, Slave and Compact fields.

| File | Written by | Purpose |
|---|---|---|
| `settings.json` | `AppSettings.Save()` on close | user settings (**tokens in plain text**) |
| `hash-master.json` | master's `HashCache` | cached hashes of the published folder |
| `hash-slave.json` | slave's `HashCache` | cached hashes of the destination folder |

Tokens: `NewToken()` draws 6 bytes from `RandomNumberGenerator` and maps each to a 32-symbol alphabet (`ABCDEFGHJKLMNPQRSTUVWXYZ23456789`, no `0/O/1/I`); because 32 divides 256 there is no modulo bias. `ValidateToken` requires printable ASCII without spaces (the token travels in an HTTP header) and optionally a minimum length of 4.

`HashCache` is a `ConcurrentDictionary` keyed by full path with `(size, mtime, hash)` values; an entry is trusted only if size and mtime are unchanged. It saves to a temp file and moves it into place, and only when something changed.

## 12. Security model

NetSynq is designed for **trusted home or office LANs**, not hostile networks.

**What is protected**

- *Read-only master:* the server has only `GET` handlers; there is no write, delete or execute capability.
- *Path traversal:* `PathUtil.Resolve` rejects empty paths, NUL, leading `/`, `:` and `\` on Windows (drive letters, NTFS alternate streams), any `.` or `..` segment, and finally verifies the resolved full path is inside the root. It is used by the server (requested file) and by the client side (`Syncer` destination) alike.
- *Authentication:* constant-time token comparison on every request; a rejected request is logged and the connection is closed.
- *Reachability:* the TCP server binds to one chosen LAN address, and discovery replies are limited to the same subnet.
- *Local safety:* refuses a drive root as destination and an empty master; ignores reparse points; re-checks state before destructive steps.

**What is not protected**

- Traffic is **plain HTTP**: no encryption and no integrity protection against an active attacker. Anyone who can sniff the LAN can read the token and the files.
- The token is stored **unencrypted** in `settings.json`.
- There is no rate limiting or lockout on token guesses (a 6-character token is about 30 bits).
- Executables are unsigned.

Possible hardening (not implemented): TLS with a pinned self-signed certificate, a challenge-response instead of a static header token, DPAPI-protected token storage, longer default tokens, code signing.

## 13. Testing

`tests/CoreTests` is a console app that **compiles the Core sources directly** (`<Compile Include="../../src/NetSynq/Core/*.cs" LinkBase="Core" />`) and embeds the two dictionaries. Because Core has no WinForms dependency, the tests build and run on any OS with the .NET 8 SDK.

```
test.bat                      # Windows
dotnet run --project tests/CoreTests/CoreTests.csproj -c Release    # anywhere
VERBOSE=1 dotnet run …        # also prints engine log lines
```

The program prints `ok:` / `FAIL:` per check and exits with code `0` if all pass, `1` otherwise.

It is an end-to-end suite: it creates temporary master and slave folders, starts a real `SyncServer` on loopback (port 0), and drives it with `RemoteClient`, `Planner` and `Syncer`. Areas covered:

- planning in Fast/Smart/Full modes, date-only differences, new/modified/deleted items, type conflicts;
- real synchronization, deletions, attribute and date preservation, read-only files;
- resume of an interrupted download (`Range`); authentication failure; path-traversal attempts against the server (`../`, encoded variants, absolute paths);
- exclusion patterns (name and path patterns); simulation changes nothing and equals the real execution; selection propagation to folders and deselected totals;
- network interface selection logic and discovery on loopback;
- line diff, including randomized comparisons and large files;
- compaction: analysis rules (N/K, limits, merge, `node_modules`, `profiles`), packing, hash verification, changed-source abort, restore with dates and attributes, a malicious zip manifest with `../` paths (zip-slip), 4 GB splitting (using a lowered `VolumeBytes`, including a name collision with an existing volume), missing volumes;
- i18n: both dictionaries have the same keys and the same placeholders, every key used in code exists, no unused keys, language switching, external override.

> Test descriptions and many code comments are in Italian (the project's original language). They do not affect behavior.

When you change a feature, add checks to `tests/CoreTests/Program.cs` using `Check(condition, "what is being verified")`. The suite deliberately has no framework so it remains zero-dependency.

## 14. How to...

### Add or change a UI text
1. Add the key to **both** `Lang/it.json` and `Lang/en.json` with the same placeholders.
2. In code use `Tr("key", args)` (engine and dialogs) or `Loc.Text(control, "key")` (main window).
3. Run `test.bat`: it fails if a key is missing in one language, has different placeholders, or is unused.

### Add a new language
1. Copy `Lang/en.json` to `Lang/<code>.json` and translate it; embed it in `NetSynq.csproj` with `LogicalName="lang.<code>.json"` (and in `CoreTests.csproj`).
2. Add the code to `I18n.Languages`.
3. The selector in `Theme.Header` is filled from `I18n.Languages` (shown upper-case), so no UI change is needed.
4. Extend the i18n tests to include the new file.

### Add a persistent setting
1. Add a property with a default to `AppSettings`.
2. Bind the control in the relevant `Build…Tab` and load the value from `_cfg` at startup.
3. Write it back in the matching `Save…Cfg` method (called on close and before operations).

### Add a server endpoint
1. Add a `case` in `SyncServer.HandleRequest` (it runs after authentication). Reuse `ResolveQuery` for any `path=` parameter, and `SendJson` / `SendText` / `SendHead` for responses.
2. Add the matching method to `RemoteClient`.
3. If the change is not backward compatible, bump `SyncServer.ProtocolVersion`, and consider checking it in `Planner`.
4. Add an end-to-end test.

### Add a comparison mode
1. Add a value to `CompareMode` (keep numeric values stable, they are persisted in `settings.json`).
2. Handle it in the `switch` in `Planner.BuildAsync`.
3. Add its label and tooltip keys, and the entry in the mode combo (`Loc.Combo` call in `MainForm.Client.cs`).

### Add a Compact rule
The rule belongs in `CompactAnalyzer.Analyze` (selection) and, if it changes what a unit contains, in `CompactPacker.PackOne`. Add an option in `CompactOptions` and `WithoutSizeLimits()`, persist it in `AppSettings`, surface it in `MainForm.Compact.cs`, and cover it in the tests.

### Debug the server without the UI
Start the app, press *Start server*, and call it with any HTTP client from another machine:

```
curl -H "X-Token: ABC123" http://192.168.1.20:48765/ping
curl -H "X-Token: ABC123" --compressed http://192.168.1.20:48765/manifest
curl -H "X-Token: ABC123" -H "Range: bytes=0-99" "http://192.168.1.20:48765/file?path=src/a.txt"
```

## 15. Conventions and known limitations

**Conventions**

- Nullable reference types are on; keep the code warning-free.
- `Core` is UI-free. Anything needing `System.Windows.Forms` goes in `UI`.
- No NuGet packages. Think twice before adding any.
- Relative paths in manifests and plans always use `/` and are compared case-insensitively (Windows semantics). Convert to the OS separator only at the file-system boundary.
- Destructive code paths must be reachable only after an explicit user action, be previewable, and re-check state just before acting.
- All user-facing text goes through i18n.
- Errors that should not stop a whole operation are collected (`SyncResult.Errors`, `CompactItemResult.Message`) rather than thrown.

**Known limitations**

- Windows only (WinForms). The Core compiles elsewhere but is not used outside tests.
- One-way, pull-based sync only; no conflict resolution, no merge.
- No encryption or integrity protection in transit; see [section 12](#12-security-model).
- IPv4 only.
- Case-insensitive path matching: two paths that differ only by case cannot coexist.
- Symbolic links and junctions are skipped.
- Text diff is limited to 8 MB files and a bounded edit distance (`maxD = 4000`).
- `HashCache` trusts `(size, mtime)`; a file changed without altering either is not re-hashed.
- `ProtocolVersion` is transmitted but not yet enforced.
- Windows Forms scaling is `SystemAware`, not per-monitor.

## 16. Release checklist

1. Update `<Version>` in `src/NetSynq/NetSynq.csproj` (the window title shows `v<Major>.<Minor>`).
2. Run `test.bat`; all checks must pass.
3. Run `build.bat` and `build-standalone.bat`, and smoke-test both exes on a clean machine: start the server on one PC, connect from another, compare, simulate, synchronize, and run an Analyze → Compact → Restore cycle on a scratch folder.
4. Tag the commit (`git tag v1.0.0`) and push the tag.
5. Create a GitHub Release and attach the two executables (never commit binaries to the repository; `dist/`, `dist-standalone/` and `*.exe` are git-ignored).
6. Update the README and these guides if behavior changed.
