# NearbyShare (Windows)

A LAN file-sharing app for Windows that talks the same wire protocol as its
Android counterpart (`NearbyShareAndroid`). Two devices on the same local
network discover each other over mDNS, negotiate a TLS connection with
trust-on-first-use certificate pinning, and transfer files directly — no
internet, cloud relay, or account required.

The wire protocol — discovery, handshake, trust model, and message set — is
specified in [`PROTOCOL.md`](PROTOCOL.md). It is the source of truth shared
byte-for-byte with the Android repository; read it before changing anything
under `Core/Protocol`, `Core/Discovery`, or `Core/Networking`.

## Project layout

| Project      | What it is                                                                 |
|--------------|------------------------------------------------------------------------------|
| `Core`       | Platform-agnostic class library: protocol framing, mDNS discovery, TLS/TOFU networking, the transfer state machine, and settings/history persistence. Builds and tests on any OS (Windows, Linux, macOS). |
| `Core.Tests` | xunit tests for `Core`, including the cross-implementation JSON fixtures shared in spirit with the Android side (`Core.Tests/Fixtures/*.json`). |
| `App`        | The WinUI 3 desktop application. **Windows-only** — see below. |

```
NearbyShareWindows/
├── PROTOCOL.md                 # wire protocol spec (source of truth)
├── Core/
│   ├── Protocol/                # Message envelope, payloads, framing codec
│   ├── Discovery/                # IDiscoveryService + mDNS implementation
│   ├── Networking/               # Certificates, TLS listener/client, TOFU, TransferSession
│   ├── Persistence/               # Settings + transfer-history repositories
│   └── Services/                  # INearbyShareService: the app-facing façade
├── Core.Tests/
└── App/
    ├── Services/                  # AppPaths, DialogService, file pickers, UI dispatcher, TransferListenerService
    ├── ViewModels/                # DeviceListViewModel, ProgressViewModel, SettingsViewModel, IncomingTransferViewModel, PeerItemViewModel
    ├── Views/                     # DeviceListPage, ProgressPage, SettingsPage, IncomingTransferDialog
    ├── MainWindow.xaml(.cs)        # NavigationView hosting the three pages
    └── App.xaml(.cs)                # Composition root (DI container) and app lifecycle
```

## The App's UI flow

`MainWindow` hosts a `NavigationView` with three pages, switched through a
`Frame`:

- **Devices** (`DeviceListPage`) — the discovered-peer list (live via mDNS),
  a "Send files…" button that opens a file picker and sends an `OFFER` to the
  selected peer, and a **manual IP/port fallback** for connecting straight to
  an address, bypassing discovery entirely. PROTOCOL.md recommends this as a
  debug/testing escape hatch, useful for proving an end-to-end transfer works
  before mDNS is confirmed working across two particular devices/routers.
- **Transfer** (`ProgressPage`) — progress for whichever transfer is
  currently active, in either direction (outbound send or inbound receive),
  backed by a single `ProgressViewModel` instance shared across the app.
- **Settings** (`SettingsPage`) — the device name advertised to peers, and
  the folder received files are saved to.

An inbound `OFFER` from a peer shows `IncomingTransferDialog` (a
`ContentDialog`) with the offered file names/sizes and Accept/Reject buttons,
regardless of which page is currently open — the TLS listener and mDNS
advertising run for the app's whole lifetime (`TransferListenerService`,
started from `App.OnLaunched`), not just while a particular page is visible.

## Building and running

### `Core` / `Core.Tests` — any OS

```
dotnet build Core/Core.csproj
dotnet test Core.Tests/Core.Tests.csproj
```

These have no Windows dependency and are the right place to verify protocol,
discovery, and networking logic changes.

### `App` — **Windows only**

The `App` project targets `net8.0-windows10.0.19041.0` and uses WinUI 3 (the
Windows App SDK), which requires an actual Windows machine — it cannot be
built or run on Linux or macOS, including in this repository's CI/sandbox
environments. Attempting to build it elsewhere fails during restore/build
with errors like `NETSDK1100` (missing Windows targeting pack) or, if that's
worked around, an inability to execute the native `XamlCompiler.exe`.

To build and run `App` on Windows:

1. Install **Visual Studio 2022** (17.8 or later) with the **".NET desktop
   development"** and **"Windows application development"** workloads, which
   bring in the Windows App SDK tooling and the `net8.0-windows` targeting
   pack. (Alternatively: the standalone **.NET 8 SDK** plus the **Windows App
   SDK** — `dotnet workload install` does not currently carry the Windows App
   SDK, so the Visual Studio workload is the simpler path.)
2. Open `NearbyShare.sln` (or just `App/App.csproj`) in Visual Studio.
3. Restore NuGet packages — `Microsoft.WindowsAppSDK` (pinned to
   `1.7.260224002` in `App/App.csproj`) and `Microsoft.Windows.SDK.BuildTools`
   are the Windows-specific dependencies; everything else is ordinary NuGet.
4. Build/run `App` for your architecture (`x64`, `x86`, or `ARM64` are all
   configured in `App.csproj`).

The app runs unpackaged (`WindowsPackageType=None`) — a plain `.exe`, no MSIX
installation needed for development. It stores its settings, device
certificate, and pinned-peer store under
`%LOCALAPPDATA%\NearbyShare`, and writes received files to
`%USERPROFILE%\Downloads\NearbyShare` by default (configurable from the
Settings page, though a restart is currently needed for a new download
folder to take effect).

Windows Firewall may prompt to allow the app on first run (it opens a TCP
listener and sends/receives mDNS multicast); allow it on private networks for
discovery and transfers to work.

See [`TESTING.md`](TESTING.md) for how to exercise both the automated test
suite and the parts of the app that can only be checked by hand on real
Windows machines.
