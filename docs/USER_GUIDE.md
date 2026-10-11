# NetSynq User Guide

<p align="center"><img src="../assets/logo.png" alt="NetSynq" width="260"></p>

This guide explains how to use NetSynq, step by step. If you want to build the program or change the code, see the [Developer Guide](DEVELOPER_GUIDE.md). For a quick overview, see the [README](../README.md).

**Contents**

1. [What NetSynq does](#1-what-netsynq-does)
2. [Getting the program](#2-getting-the-program)
3. [Key concepts](#3-key-concepts)
4. [Your first synchronization](#4-your-first-synchronization)
5. [The Master tab](#5-the-master-tab)
6. [The Slave tab](#6-the-slave-tab)
7. [The Compact tab](#7-the-compact-tab)
8. [Exclusion patterns](#8-exclusion-patterns)
9. [Settings, language and theme](#9-settings-language-and-theme)
10. [Safety: what NetSynq will and will not do](#10-safety-what-netsynq-will-and-will-not-do)
11. [Troubleshooting](#11-troubleshooting)
12. [FAQ](#12-faq)

---

## 1. What NetSynq does

NetSynq has two independent functions.

**Synchronization (Master and Slave tabs).** It makes a folder on one computer (the *slave*) identical to a folder on another computer (the *master*), over your local network. The copy goes in **one direction only**: master → slave. The master is never modified.

It was designed for this situation: computer B can reach a resource (for example an SVN repository) only through a VPN, computer A cannot, but A and B are on the same local network. NetSynq copies B's working folder to A over the LAN, `.svn` folder included, so A has a working copy without needing the VPN.

**Compaction (Compact tab).** It packs folders that contain many small files into zip archives, in place. Copying thousands of tiny files to a slow drive (a USB disk, a network share) is very slow; copying one zip is fast. A Restore function brings the original folders back exactly as they were.

## 2. Getting the program

NetSynq is a single `.exe` file. There is no installer and it needs no administrator rights.

| Version | Size | Needs on the PC that runs it |
|---|---|---|
| Light (`build.bat`) | ~200 KB | the [.NET Desktop Runtime 8](https://dotnet.microsoft.com/download/dotnet/8.0) |
| Standalone (`build-standalone.bat`) | ~70-100 MB | nothing |

Download a release from the [GitHub Releases page](https://github.com/atlantidezign/NetSynq/releases) if one is published, or build it yourself (see the [Developer Guide](DEVELOPER_GUIDE.md#3-building)).

> **"Windows protected your PC" / unknown publisher.** The executable is not code-signed. On a second PC, Windows may block it. Right-click the file → *Properties* → tick **Unblock** → *OK*. Or, in the SmartScreen dialog, choose *More info* → *Run anyway*.

Copy the exe to **both** computers. The same program acts as master or slave depending on the tab you use.

## 3. Key concepts

| Term | Meaning |
|---|---|
| **Master** | The computer that has the up-to-date folder. It *publishes* the folder, read-only. |
| **Slave** | The computer that receives the copy. Its folder is made identical to the master's. |
| **Token** | A short password shared by master and slave. The slave must present it with every request. |
| **LAN IP** | The address of the master on your local network (usually `192.168.x.x` or `10.x.x.x`). **Not** the VPN address. |
| **Manifest** | The list of files and folders (with sizes and dates) that the master sends to the slave for comparison. |
| **Hash** | A fingerprint (SHA-256) of a file's content. Two files with the same hash are identical. |

A typical sync always has three steps, and nothing changes on disk until the last one:

```
1. Compare     → shows what would change (no file touched)
2. (Simulate)  → optional: lists the exact operations (still no file touched)
3. Synchronize → makes the changes
```

## 4. Your first synchronization

You need both computers on the same network, and NetSynq on both.

**On the master computer**

1. Open NetSynq and go to the **Master** tab.
2. Click **Browse…** next to *Master folder* and choose the folder to publish.
3. Check *Network interface*: it must be your **LAN** address, not the VPN one. NetSynq picks it automatically and marks interfaces that look like a VPN or virtual adapter.
4. Note the **Token** (6 characters are generated for you).
5. Click **Start server**. If Windows Firewall asks, allow access on **private networks**.

**On the slave computer**

1. Open NetSynq and go to the **Slave** tab.
2. Click **Find on LAN**. The master should appear; select it. (Otherwise type its IP and port by hand.)
3. Type the **Token** shown on the master.
4. Click **Test connection**: you should see *Connection successful* and the master's name.
5. Choose the **local folder**: the destination that will be made identical to the master's.
6. Leave the mode on **Smart (recommended)** and click **1 · Compare**.
7. Look at the grid. When you are happy with it, click **2 · Synchronize** and confirm.

When it finishes, NetSynq automatically runs a second comparison to verify the result. If everything is identical, the grid shows only *Identical* rows.

## 5. The Master tab

| Field | What it does |
|---|---|
| **Master folder** | The folder to publish. It is shared **read-only**. |
| **Network interface** | The address the server listens on. The server listens **only** on this one, so it is not reachable from the VPN. Use *Refresh* if you changed network. |
| **Slave IP (opt.)** | Optional. If you enter the slave's IP, NetSynq picks the interface that is on the same subnet as the slave. |
| **Port** | TCP port of the server. Default `48765`. A UDP port, `48766`, is also used for *Find on LAN*. |
| **Token** | Generated automatically (6 characters, no ambiguous characters like `0/O` or `1/I`). *Generate new* makes another one, *Copy* copies it to the clipboard. You can type your own: at least 4 characters, ASCII letters, digits and symbols, **no spaces**. |
| **Exclusions** | Patterns for files and folders that must not be published (see [Exclusion patterns](#8-exclusion-patterns)). |
| **Start server / Stop server** | Starts or stops publishing. |

While the server runs, the status line shows the address, port and token, and the log shows every request from the slave (who connected, which files were sent, rejected requests with a wrong token).

Things worth knowing:

- You can keep working in the master folder while the server runs, but do not run `svn` commands on it during a synchronization (see [Safety](#10-safety-what-netsynq-will-and-will-not-do)).
- The server only answers requests from clients that know the token.
- The list of files is computed each time the slave presses *Compare*, so it always reflects the current state of the master.

## 6. The Slave tab

### Connection

| Field | What it does |
|---|---|
| **IP / Port** | Address of the master on the LAN, and its port. |
| **Find on LAN** | Sends a broadcast on your local networks and lists the masters that answer. Masters answer **only** to computers on their own LAN subnet. |
| **Token** | The token shown on the master. |
| **Test connection** | Checks that the master is reachable and the token is valid. |
| **Local folder (slave)** | The destination. It will be made identical to the master's folder. A drive root (such as `D:\`) is refused. |

### Options

| Option | What it does |
|---|---|
| **Mode** | How files are compared. See below. |
| **Delete files no longer on the master** | When on (default), files and folders that exist only on the slave are deleted. When off, they are kept ("Slave only (kept)"). |
| **Simulate (no changes)** | The sync button becomes *2 · Simulate* and only lists what would happen. |
| **Parallelism** | How many files are copied at the same time (1-8, default 3). |
| **Exclusions** | Patterns for items to ignore on both sides (not compared, not deleted). |

### Comparison modes

| Mode | How it decides two files are the same | When to use it |
|---|---|---|
| **Fast** | Same size and same modification date (2-second tolerance). | Repeated syncs where both sides are known to be consistent. Quickest. |
| **Smart** *(recommended)* | Like Fast. If the size is equal but the date differs, it compares the content (hash). Identical files are **not copied again**; only their date is fixed. | The first synchronization, or any time dates may have drifted. Avoids re-transferring tens of GB. |
| **Full** | Hash of every file on both sides. | When you do not trust dates at all. Slow the first time; hashes are then cached and only changed files are re-read. |

> The hash cache trusts *size + date*. A file edited so that neither changes would not be re-read in Full mode. This is very rare in practice.

### Reading the grid

After **1 · Compare**, each row is a file or folder. The **Status** column says what will happen:

| Status | Meaning | Action on Synchronize |
|---|---|---|
| **Identical** | Same on both sides. | Nothing. |
| **New (master only)** / **New folder** | Exists only on the master. | Copied / created. |
| **Different → will be replaced** | Exists on both, content differs. | The slave's version is replaced. |
| **Date differs only** | Content is identical, only the modification date is different. | The date is corrected; no copy. |
| **Slave only → will be deleted** | Exists only on the slave. | Deleted (if *Delete* is on). |
| **Slave only (kept)** | Same, but *Delete* is off. | Left alone. |

Other columns show the master and slave sizes and dates, and a note explaining the decision (for example *identical content, only the date differs*).

Controls above and below the grid:

- **View**: *Only differences* or *Show all*.
- **Search**: filters rows by path.
- **Column headers**: click to sort.
- **Select all / Select none**: act on the **visible** rows only, so you can combine them with the filter and the search.
- **Summary line**: counts and sizes, **only for included rows**.

### Choosing what to include

Every row has a checkbox (the ✔ column). After a comparison all differences are included.

- **Uncheck** a row to skip it: it will not be copied, replaced or deleted.
- **Unchecking a folder unchecks everything inside it**; checking it again re-includes the content.
- Shortcuts: **space bar** on a row, a click on the **✔ header** (all/none of the visible rows), or the right-click menu.
- Excluded rows turn grey and do not count in the totals.

### Simulate (dry run)

Tick **Simulate (no changes)** and the sync button becomes **2 · Simulate**. It opens a window listing the exact operations: folders created, files copied or replaced, dates fixed, files deleted, items you excluded, and whether free space is sufficient.

- **Save report…** stores the list as a `.txt` file.
- **Run for real…** closes the simulation and performs the synchronization.

### Content differences

Double-click a file (or right-click → *View content differences…*) to see a line-by-line comparison of the master's and the slave's version. It works on text files up to 8 MB. For binary files it only tells you whether they are identical or different. A checkbox lets you show the whole file instead of just the changes.

### Synchronize

1. NetSynq checks free disk space.
2. It shows a summary (what will be copied, replaced, deleted) and asks you to confirm. If a large share of the slave would be deleted, the warning is stronger.
3. It copies files with a progress bar, speed and time remaining.
4. It runs a verification comparison.

You can press **Cancel** at any time; files already completed stay, and partially copied files are kept as `*.nspart` so the next run can **resume** them.

## 7. The Compact tab

### What and why

Copying a folder with 50,000 tiny files can take far longer than copying one 500 MB file. Compact packs such folders into zip files **in place**, so a later copy is fast. It works on a single folder tree, on the computer where you run it. It is independent from the Master and Slave tabs.

> **Do not run Compact on a folder that is kept in sync with the Slave tab**, because it changes the folder structure.

### The workflow

The work is always in three steps, so you can see what will happen before anything changes:

```
1 · Analyze folder  →  2 · Preview  →  3 · Compact
```

- **Analyze** scans the working folder and lists the candidate folders in a table. Each row has an explanation and a checkbox (all on by default). The summary shows how many files and bytes would be reduced.
- **Preview** changes nothing and produces a report you can save.
- **Compact** does the work, then shows real figures: files and size before and after.

### The criteria

A folder is chosen if it directly contains **more than N** files that are **smaller than K KB**.

| Setting | Default | Notes |
|---|---|---|
| **N** (number of small files) | 5 | Only files directly in the folder are counted, not those in subfolders. |
| **K** (small file size, KB) | 10 | "Small" means strictly smaller than K. `.zip` files never count as small. |
| **Folder max** (MB) | 4096 | A folder larger than this is not zipped whole. `0` = no limit. |
| **File max** (MB) | 50 | A folder containing a file larger than this is not zipped whole. `0` = no limit. |
| **Compression** | Automatic | *Automatic*: already-compressed files (jpg, mp4, pdf, zip, docx…) are only stored, the rest is compressed quickly. *None*: fastest. *Maximum*: slowest, smallest. |
| **Exclusions** | `.git; .svn` | A folder containing a matching item is never zipped. |
| **Always zipped** | `node_modules` | Folders with these names are zipped whole, always, without looking inside and ignoring N, K and size limits. |
| **Zip per subfolder** | `profiles` | A folder with this name is not zipped itself, but **each of its direct subfolders** is, whole, without size limits. |
| **Merge folders made up only of zips** | on | If a folder would end up containing only the zips of its subfolders, it is zipped once instead. |
| **In folders that cannot be zipped, pack only the small files** | on | A folder that meets N/K but cannot be zipped whole still gets its small direct files grouped into `_piccoli.zip` inside it. |

How the analysis proceeds: it works **top-down**. If a folder is selected, NetSynq does not look inside it; otherwise it examines its subfolders. The starting folder is never zipped itself.

Folders that qualify but cannot be zipped whole (they exceed a size limit, contain excluded items, symbolic links or unreadable files) are **never zipped whole**. With *In folders that cannot be zipped, pack only the small files* on, the table shows an item that packs just their small direct files into `_piccoli.zip`, with the reason in the note; with it off, the folder is listed as skipped, with the reason. Either way their subfolders are still analyzed one by one.

### Large archives (over 4 GB)

A zip that would exceed 4 GB (4,294,967,295 bytes, the FAT32 file-size limit) is automatically split into volumes: `name.zip.001`, `name.zip.002`, …

- To rebuild the original zip: `copy /b name.zip.001+name.zip.002 name.zip`.
- 7-Zip can open the set directly from the `.001` file.
- Splitting needs temporary free space equal to the zip size.
- This limit is fixed in the program; it is not a setting.

### Nothing is lost

For each folder NetSynq follows a strict sequence:

1. Writes the zip to a temporary file (`.nstmp`) and records a SHA-256 hash of every file.
2. Re-opens the zip and verifies every file against its hash.
3. Re-scans the folder. If anything changed in the meantime, **nothing is deleted**.
4. Renames the zip to its final name. An existing zip is **never overwritten**: `name (2).zip` is created instead.
5. Only now removes the archived files and the folders left empty.

Free disk space is checked before starting. If any step fails, the original folder is left untouched.

### Restore

Restore is a separate path, on the second row of buttons:

```
1 · Find zips  →  2 · Preview  →  3 · Restore
```

- **Find zips** locates the zips created by NetSynq (they carry an internal manifest, `.netsynq-manifest.json`). Zips made by other tools are ignored.
- **Preview** shows what would be restored, changing nothing.
- **Restore** extracts into a temporary folder, verifies hashes, restores dates and attributes, renames the folder into place, and only then deletes the zip.
- For `_piccoli.zip`, the files are put back into the folder without overwriting anything.
- If a folder with that name already exists, the item is **skipped**.
- Split archives (`.001`, `.002`…) are recognized: all volumes are read and are deleted only after a successful restore. If a volume is missing, the archive is reported as incomplete and nothing is touched.

## 8. Exclusion patterns

Several fields accept a list of patterns separated by `;` (or by line breaks).

| Pattern | Matches |
|---|---|
| `node_modules` | any file or folder named `node_modules`, at any depth |
| `*.tmp` | any name ending in `.tmp` |
| `Thumbs.db; .DS_Store` | two names |
| `build/obj*` | a pattern containing `/` is matched against the **full relative path** (e.g. `build/obj`, `build/objects`) |

Rules:

- `*` stands for any number of characters, `?` for exactly one.
- Matching is **case-insensitive**.
- Backslashes are treated as `/`.
- A pattern without `/` is tested against **every segment** of the path, so excluding `build` excludes everything below any folder called `build`.

Excluded items are neither compared nor copied nor deleted.

## 9. Settings, language and theme

**Language.** At the top right, on the logo row, the **IT / EN** selector changes every text of the program immediately. Lines already written in the log stay in the language they were created in. Your choice is remembered.

**Theme.** Next to it, the **Light / Dark** selector switches the whole interface. Some Windows elements remain light even in dark mode: tooltips, the grid's context menu, the arrows of numeric fields and the folder / save dialogs.

**Customizing texts without recompiling.** Create a folder named `lang` next to the exe, containing `en.json` and/or `it.json` with only the keys you want to change. They take precedence over the built-in ones. See the [Developer Guide](DEVELOPER_GUIDE.md#8-internationalization-i18n) for the format.

**Where your data is stored**

| File | Location | Contents |
|---|---|---|
| `settings.json` | `%LOCALAPPDATA%\NetSynq\` | Last-used folders, ports, options, language, theme. **Includes the tokens in plain text.** |
| `hash-master.json`, `hash-slave.json` | `%LOCALAPPDATA%\NetSynq\` | Cache of file hashes, so unchanged files are not re-read. Safe to delete. |

To reset the program to its defaults, close it and delete the `%LOCALAPPDATA%\NetSynq` folder.

## 10. Safety: what NetSynq will and will not do

**The master is read-only.** It can only list files, compute hashes and send file contents. It cannot receive, write or delete anything. Requests must carry the token, and paths are validated so a request can never leave the published folder.

**The slave changes only what you approve.** Nothing is touched before you press *Synchronize*. You can review the grid, exclude items and run a simulation first. Deleting is optional.

**Transfers are crash-safe.** A file is first written as `*.nspart` and renamed only when complete and correct in size. If the network drops, the transfer resumes from where it stopped, with 3 automatic attempts per file.

**Dates and attributes are preserved**, including read-only, hidden and system attributes (important for `.svn`, whose `pristine` files are read-only).

**Protections.** NetSynq refuses to use a drive root as destination, refuses to sync from an **empty master** (to avoid wiping the slave by mistake), checks free disk space before copying, and ignores symbolic links and junctions. Paths longer than 260 characters are supported.

**Network security.** The token and the data travel over plain HTTP on your LAN. The token prevents accidental or casual access; it does **not** protect against someone who can capture traffic on your network, and the files are not encrypted in transit. Use NetSynq only on networks you trust. The server listens only on the LAN address you chose, so it is not exposed through the VPN.

**Subversion.** The `.svn` folder is synchronized like any other. Do not run `svn` commands on the master during a synchronization, because `wc.db` may be being written.

## 11. Troubleshooting

| Problem | Likely cause and fix |
|---|---|
| **"No master found on the LAN"** | The server is not started, the firewall blocks it, or the two PCs are on different subnets or Wi-Fi/guest networks. Check on the master that the server is running, allow NetSynq on **private** networks in Windows Firewall, or type the IP by hand. |
| **"Could not connect to the master"** | Wrong IP or port, server stopped, firewall. Make sure you use the **LAN** IP, not the VPN one. |
| **"Invalid token"** | The token differs from the one on the master. Tokens are case-sensitive. |
| **The server picked the wrong network interface** | On the Master tab choose the LAN interface by hand, or enter the slave's IP so the matching subnet is chosen. |
| **"Unable to start the server"** | The port is already in use. Change the *Port* number (1024-65535). |
| **"The master appears to be empty"** | Protection against wiping the slave. Check the master folder and its exclusions. |
| **"A drive root is not allowed as a destination"** | Choose a folder, not `D:\`. |
| **"Not enough space"** | Free space on the slave, or tick fewer items. The estimate includes a 200 MB safety margin. |
| **A file is reported as locked** | Another program holds it open. Close it on the master or the slave, then run Compare and Synchronize again. |
| **Windows blocks the exe** | See [Getting the program](#2-getting-the-program): right-click → Properties → Unblock. |
| **The standalone exe is slow to start the first time** | It unpacks itself on first run. This is normal. |
| **A synchronization was interrupted** | Run **Compare** and **Synchronize** again: finished files are skipped, partial files resume. Leftover `*.nspart` files are cleaned up after a clean run. |

## 12. FAQ

**Does it work over the VPN or the Internet?**
It is meant for a local network. The master deliberately listens only on the LAN address. The data is not encrypted, so it should not be exposed to the Internet.

**Can it synchronize in both directions?**
No. It is strictly one-way, master → slave. Changes made on the slave are overwritten (or deleted, if *Delete* is on).

**Will it copy 50 GB again every time?**
No. After the first synchronization only new and changed files are transferred. In Smart mode even the first run avoids re-copying files that are already identical.

**What if I only want to update a few files?**
Run Compare, then uncheck everything you do not want (*Select none*, then check the rows you need), and Synchronize.

**Can I run it from a USB stick?**
Yes, the standalone build runs without installation. Settings are still stored in `%LOCALAPPDATA%` on that PC.

**Is the Compact function safe on my only copy of the data?**
It never deletes a file before the zip has been written, re-read and verified by hash, and it re-checks the folder right before deleting. Even so, as with any bulk file operation, keep a backup of data you cannot replace.

**Why are zip files named `_piccoli.zip`?**
*Piccoli* means "small" in Italian. The name is kept for compatibility with archives already created by earlier versions.

**Where do I report a bug or ask for a feature?**
Open an issue on <https://github.com/atlantidezign/NetSynq/issues>.
