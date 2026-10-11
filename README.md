<p align="center">
  <img src="assets/logo.png" alt="NetSynq" width="320">
</p>

<p align="center">
  <b>One-way folder synchronization over the LAN, plus in-place compaction of small files.</b><br>
  A single-file Windows app. No NuGet packages, no services, no administrator rights.
</p>

<p align="center">
  <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8-512BD4">
  <img alt="Windows" src="https://img.shields.io/badge/platform-Windows-0078D6">
  <img alt="Zero NuGet dependencies" src="https://img.shields.io/badge/NuGet-0%20dependencies-brightgreen">
  <img alt="License: MIT" src="https://img.shields.io/badge/license-MIT-blue">
</p>

---

NetSynq mirrors a folder from a **master** computer to a **slave** computer in **one direction only**, using the **local network (LAN)** address and never the VPN one. It was built for the case where the master can reach a resource (for example an SVN repository) through a VPN and the slave cannot: the slave gets an identical copy of the working folder, `.svn` included.

```
MASTER                                           SLAVE
folder to publish    ──  LAN (LAN IP only)  ──►  folder made identical
[Master tab → Start server]                      [Slave tab → Compare → Synchronize]
```

## Documentation

| Guide | For | Contents |
|---|---|---|
| **[User Guide](docs/USER_GUIDE.md)** | people who use NetSynq | every tab and option, step-by-step walkthroughs, safety, troubleshooting, FAQ |
| **[Developer Guide](docs/DEVELOPER_GUIDE.md)** | people who build or modify it | technology, architecture, network protocol, algorithms, tests, how to extend, release checklist |

## Features

- **Read-only master.** The master only lists files, computes hashes and sends data. Every request needs a token and paths are validated.
- **Compare before you sync.** A diff grid shows status, sizes and dates on both sides, with filter, search and sorting. Nothing is touched until you press *Synchronize*.
- **Per-item selection.** Every row has a checkbox; excluding a folder excludes its whole content.
- **Dry run.** *Simulate* lists the exact operations without performing them.
- **Smart comparison.** Files already identical are not copied again, so the first sync does not re-transfer tens of GB.
- **Resumable transfers** with progress, speed and time remaining.
- **Line-by-line content diff** for text files.
- **Compact tab.** Packs folders full of small files into zips in place, with hash verification and a lossless Restore.
- **Bilingual UI (IT / EN)** with live switching and editable dictionaries, and a **light / dark** theme.

## Quick start

1. **Get the exe**: download it from the [Releases](https://github.com/atlantidezign/NetSynq/releases) page, or [build it](#build). Copy it to both PCs. If Windows blocks it as an unknown publisher, right-click → *Properties* → *Unblock*.
2. **Master PC**: *Master* tab → choose the folder → check the network interface is the LAN one → note the token → **Start server** (allow *private* networks in the firewall prompt).
3. **Slave PC**: *Slave* tab → **Find on LAN** (or type IP and port) → enter the token → **Test connection** → choose the local folder → **1 · Compare** → review → **2 · Synchronize**.

The [User Guide](docs/USER_GUIDE.md) explains each option in detail.

## Requirements

| To... | You need |
|---|---|
| Build | .NET 8 SDK (or newer): <https://dotnet.microsoft.com/download/dotnet/8.0> |
| Run the light build | .NET Desktop Runtime 8 |
| Run the standalone build | nothing |

## Build

| Script | Output | Notes |
|---|---|---|
| `build.bat` | `dist\NetSynq.exe` (~200 KB) | Works offline. Needs the .NET Desktop Runtime 8 on the machines that run it. |
| `build-standalone.bat` | `dist-standalone\NetSynq.exe` (~70-100 MB) | Needs nothing on the target machines. Internet is needed only during the build. |
| `test.bat` | — | Runs the automated end-to-end tests of the core. |

More detail in the [Developer Guide](docs/DEVELOPER_GUIDE.md#3-building).

## Safety at a glance

- Nothing on the slave changes before you confirm; you can exclude any item and simulate first.
- Files are copied to `*.nspart` and renamed only when complete; interrupted transfers resume.
- Compaction never deletes a file before its zip has been written, re-read and verified by SHA-256.
- Refuses an empty master and a drive root as destination.
- **Network note:** traffic is plain HTTP on your LAN and the token protects against casual access only, so use NetSynq on networks you trust. Details in the [User Guide](docs/USER_GUIDE.md#10-safety-what-netsynq-will-and-will-not-do) and the [Developer Guide](docs/DEVELOPER_GUIDE.md#12-security-model).

## Project layout

```
src/NetSynq/Core/   GUI-free logic (HTTP server, client, compare, sync, diff, compaction)
src/NetSynq/UI/     WinForms interface
src/NetSynq/Lang/   it.json, en.json
tests/CoreTests/    end-to-end tests of the Core
docs/               User Guide and Developer Guide
```

## License

Released under the [MIT License](LICENSE).

## Author

**Alessandro Di Michele** — [Atlantide Design](https://www.atlantide-design.it) — <info@atlantide-design.it>

Repository: <https://github.com/atlantidezign/NetSynq>
