# Testing

## Automated: `Core.Tests`

Runs anywhere the .NET 8 SDK is available (this repository's sandbox
included) — no Windows dependency:

```
dotnet test Core.Tests/Core.Tests.csproj
```

This covers:

- **Protocol** (`Core.Tests/Protocol`) — message envelope encode/decode via
  `MessageCodec`, the length-prefix framing rules from PROTOCOL.md §3
  (including the 1 MiB max-payload rejection), the `UNSUPPORTED_TYPE`
  forward-compatibility behavior from §4, and the fixture contract tests that
  decode every JSON file under `Core.Tests/Fixtures/` into the expected
  in-memory message.
- **Networking** (`Core.Tests/Networking`) — certificate generation and
  fingerprinting, the TLS handshake, TOFU pinning (first-use, match, and
  identity-changed cases), the `TransferSession` state machine end to end
  over an in-memory pipe, and file-name sanitization for received files.
- **Discovery** (`Core.Tests/Discovery`) — DNS-SD instance-name sanitization
  and collision handling.
- **Persistence** (`Core.Tests/Persistence`) — the JSON-backed settings and
  pinned-peer stores.

There is no test project for `App` — WinUI 3 ViewModels here are thin
adapters over `Core`, and the interesting logic (discovery, trust, the
transfer state machine) already has direct `Core.Tests` coverage. The
`App`-specific behavior below has to be checked by hand on Windows.

## Cross-implementation fixtures

`Core.Tests/Fixtures/*.json` — one file per PROTOCOL.md §5 message type — is
meant to be **byte-identical** to the equivalent fixtures in the Android
repository. If you change a payload's JSON shape, update both repositories'
copies together and re-run both test suites; `FixtureContractTests` is what
would catch a drift here.

## Manual: the `App` UI (Windows only)

`App` can only be built and exercised on Windows — see `README.md` for setup.
The following needs two devices (or two copies of the app on the same
machine, or a Windows machine plus the Android app) on the same LAN/Wi-Fi
segment. Windows Firewall must allow the app on the private network profile.

### 1. Discovery and identity

1. Launch the app on both devices. On the **Devices** page, confirm the
   header line shows a device name, listening port, and an 8-character
   fingerprint prefix (`{name} · port {port} · fingerprint {hex}`) — this
   means the TLS listener and mDNS advertiser both started.
2. Within a few seconds each device should appear in the other's peer list,
   showing its name, platform (`Windows`/`Android`), and address.
3. Go to **Settings**, change the device name, and save. Confirm the header
   on **Devices** picks up the new name, and that the *other* device's peer
   list updates the name for this device (mDNS re-advertisement).
4. Use **Refresh** on the Devices page and confirm it doesn't duplicate
   entries or drop the currently selected peer unnecessarily.

### 2. Manual IP/port fallback

Useful to isolate whether a problem is in mDNS discovery or in the transfer
itself.

1. Find the *listening port* from a device's own header line (or from its
   Windows Firewall prompt).
2. On the other device's Devices page, type its IP and that port into the
   manual-connection fields and click **Send to address**.
3. Confirm this drives the same offer/accept/transfer flow as a discovered
   peer, without requiring the peer to appear in the discovered list first.

### 3. Sending and accepting a transfer

1. Select a discovered peer, click **Send files…**, and pick one or more
   files of mixed sizes (include at least one large enough to take a few
   seconds over Wi-Fi, to observe progress rather than an instant finish).
2. On the receiving device, confirm `IncomingTransferDialog` appears showing
   the sender's name and every offered file's name and size, and that
   **Accept**/**Decline** map to the dialog's primary/close buttons.
3. Accept. On both devices' **Transfer** page, confirm:
   - the peer name and a status line update through the phases (waiting for
     accept → transferring → completed);
   - the progress bar and byte counter move as bytes arrive/are sent;
   - after completion, the page returns to "No transfer in progress" and
     shows the last result text.
4. Confirm the received files land in the configured download folder
   (`%USERPROFILE%\Downloads\NearbyShare` by default) with their original
   names, and that a `.part` file never remains after a successful transfer.
5. Repeat with **Decline** instead of Accept, and confirm the sender's
   Devices page reports the peer declined, with no partial file left behind
   on the receiver.

### 4. Cancellation

1. Start an outbound send of a large file, then use **Cancel transfer** on
   the Transfer page while bytes are still moving.
2. Confirm the sender reports "Transfer cancelled" and the receiver discards
   the partial file rather than keeping a truncated one.

(Cancelling an *inbound* receive from the UI is not wired up yet — Core's
inbound loop is driven by the listener's own lifetime token, not one the UI
currently has a handle to. Declining the initial offer is the way to refuse
an inbound transfer today.)

### 5. Identity-changed warning (TOFU)

This simulates PROTOCOL.md §2's "device reinstalled" scenario:

1. Complete at least one transfer with a peer, so its certificate fingerprint
   gets pinned (`known-peers.json` under `%LOCALAPPDATA%\NearbyShare`).
2. On the *peer*, delete its certificate file
   (`%LOCALAPPDATA%\NearbyShare\device-certificate.pfx`) and restart it — this
   is the easiest way to simulate a reinstall, since it forces a fresh
   self-signed certificate (and thus a new fingerprint) without changing the
   persisted device id.
3. Try sending to that peer again. Confirm the sender shows the "identity
   changed" warning (not a generic error) and offers to forget the old
   pinned fingerprint; confirm declining leaves the old pin in place and
   accepting lets a subsequent send succeed.

### 6. Error surfaces

- Try sending a file mid-transfer while disconnecting the network on one
  side; confirm both ends report a failure rather than hanging indefinitely.
- Try connecting to a manual address that isn't running the app at all
  (nothing listening on that port); confirm a clear connection-failure
  message rather than a silent hang.
