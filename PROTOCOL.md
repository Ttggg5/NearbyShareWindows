# NearbyShare Wire Protocol (v1)

This document is the shared contract between the Android app (`NearbyShareAndroid`)
and the Windows app (`NearbyShareWindows`). It is kept byte-identical in both
repositories. Any change to this file must be applied to both repos in the same
change set, and the protocol `v` field bumped for any breaking change.

## 1. Discovery (mDNS / DNS-SD)

- **Service type**: `_nearbyshare._tcp.local.`
- **Instance name**: the user's chosen device name (sanitized for DNS-SD: max 63
  bytes, illegal characters stripped, de-duplicated with a numeric suffix if a
  name collision is detected on the local network).
- **TXT record fields** (all keys lowercase, values UTF-8):
  | Key    | Meaning                                                        |
  |--------|-----------------------------------------------------------------|
  | `v`    | Protocol version, integer as decimal string (e.g. `"1"`)        |
  | `id`   | Stable device UUID (v4), generated once and persisted locally   |
  | `fp`   | SHA-256 fingerprint of the device's TLS certificate, lowercase hex, no separators |
  | `port` | TCP port the device's TLS server is listening on                |
  | `os`   | Platform identifier: `"android"` or `"windows"`                 |

  Each TXT entry must stay within the 255-byte DNS-SD limit.

- Devices both **advertise** their own service and **browse** for others while
  the app is foregrounded (Android) or running (Windows). Discovered peers are
  kept in a live list, refreshed as advertisements are seen/lost.

## 2. Connection & Trust (TLS, trust-on-first-use)

1. Each device generates **one long-lived self-signed certificate/keypair** the
   first time it runs, and persists it (Android Keystore on Android; a
   DPAPI-protected file or the Windows certificate store on Windows). This
   keeps the `fp` fingerprint stable across app restarts.
2. To start a transfer, the initiating device resolves the peer's mDNS record
   to get its IP, `port`, and advertised `fp`.
3. It opens a plain TCP connection to `ip:port`, then performs a TLS handshake
   as the client. Sockets act as a TLS *server* while listening for inbound
   connections, and as a TLS *client* when initiating one.
4. Certificate validation is **not** chain-of-trust based (these are
   self-signed certs, there is no CA). Instead, each side implements
   trust-on-first-use (TOFU) pinning:
   - Compute the SHA-256 fingerprint of the peer's presented leaf certificate.
   - Compare it against the `fp` value advertised via mDNS for that peer's
     `id`.
   - If this is the first time this device has connected to that peer `id`,
     store the fingerprint locally, keyed by `id`.
   - If a fingerprint was previously stored for that `id` and it does **not**
     match the one just seen, treat this as a security event (comparable to
     an SSH host-key change): abort the connection and surface a clear
     "this device's identity changed" warning to the user instead of silently
     proceeding or silently overwriting the stored fingerprint.
5. Immediately after the TLS handshake completes, the connecting side sends a
   `HELLO` message (see §4) as the first framed message on the socket. The
   accepting side verifies the `deviceId` in `HELLO` matches the `id` it
   expects for that peer (defense against IP address reuse/spoofing on the
   LAN). A mismatch aborts the connection with an `ERROR`.

## 3. Message Framing

All control messages are sent as **length-prefixed JSON** over the TLS
stream:

```
[4 bytes: big-endian uint32 length N] [N bytes: UTF-8 JSON payload]
```

- Maximum control-message payload size is **1 MiB**. A length prefix
  exceeding this must cause the receiver to abort the connection with an
  `ERROR` (protects against a malformed/malicious peer requesting an
  unbounded allocation).
- Raw file bytes are **not** JSON-encoded or base64-encoded. Once a transfer
  is accepted (see §5), file bytes are streamed directly on the same TLS
  connection as a raw byte stream, with lengths already known from the
  `OFFER` message's declared file sizes.

## 4. Message Envelope

Every control message is a JSON object of this shape:

```json
{
  "v": 1,
  "type": "OFFER",
  "id": "5c1b1e2a-...-uuid-of-this-message-or-transfer",
  "payload": { "...": "..." }
}
```

- `v` — protocol version this message was constructed under. A receiver that
  does not support the given major version should reply with
  `ERROR{code: "UNSUPPORTED_VERSION"}` and close the connection.
- `type` — string identifying the message kind (see §5 for the MVP set).
- `id` — for transfer-scoped messages, the transfer's UUID (assigned by the
  offering side when it sends `OFFER`); for `HELLO`, an arbitrary per-message
  UUID.
- `payload` — type-specific body, described per message type below.

### Forward compatibility rule

`type` is an open, extensible string set — new values will be added in the
future (e.g. a hypothetical `CLIPBOARD_OFFER`) without incrementing the major
protocol version, as long as the new message is optional to support. A
receiver that encounters a `type` it does not recognize **must not** crash or
disconnect abruptly. It must reply with:

```json
{ "v": 1, "type": "ERROR", "id": "<same id>", "payload": { "code": "UNSUPPORTED_TYPE", "message": "..." } }
```

and otherwise leave the connection in a valid state for further messages
(unless the unsupported message was required to proceed, in which case the
connection may then be closed cleanly). Both implementations must have a test
proving this behavior.

## 5. MVP Message Types

| `type`     | Sent by            | `payload`                                                                 |
|------------|---------------------|----------------------------------------------------------------------------|
| `HELLO`    | connecting side     | `{ "deviceId": "...", "deviceName": "...", "protocolVersion": 1 }`         |
| `OFFER`    | sender              | `{ "transferId": "...", "files": [{ "name": "...", "size": 12345, "mime": "...", "sha256": "..." }] }` |
| `ACCEPT`   | receiver            | `{ "transferId": "..." }`                                                  |
| `REJECT`   | receiver            | `{ "transferId": "...", "reason": "..." }`                                 |
| `PROGRESS` | sender or receiver  | `{ "transferId": "...", "fileIndex": 0, "bytesTransferred": 4096, "totalBytes": 12345 }` |
| `DONE`     | sender              | `{ "transferId": "...", "fileIndex": 0 }` (or `"fileIndex": "all"` once every file is complete) |
| `ERROR`    | either side         | `{ "transferId": "...", "code": "...", "message": "..." }`                 |
| `CANCEL`   | either side         | `{ "transferId": "..." }`                                                  |

`sha256` in `OFFER` is optional; when present it lets the receiver verify
integrity after writing the file to disk.

### MVP flow

1. Connection established and `HELLO` exchanged (§2 step 5).
2. Sender sends `OFFER` describing one or more files.
3. Receiver's user is prompted to accept/reject. Receiver replies `ACCEPT` or
   `REJECT`.
4. On `ACCEPT`, sender streams each file's raw bytes, in the order listed in
   `OFFER`, back-to-back, with no additional framing (the receiver already
   knows each file's declared size). Either side may emit periodic `PROGRESS`
   messages to drive UI (not required to be acknowledged).

   **Clarification (v1):** "no additional framing" applies for the entire span
   from the first byte of the first file to the last byte of the last file. The
   sender must not write *any* framed message into that span — not a `PROGRESS`,
   and not a per-file `DONE` — because the receiver reads exactly
   `files[i].size` bytes and then begins reading `files[i+1]` immediately, so an
   interleaved frame would be consumed as file content and desynchronize the
   remainder of the stream. The sender's `PROGRESS` allowance therefore applies
   only before the first byte and after the last; a receiver may send `PROGRESS`
   at any time, since the sender is not reading while it streams. Because the
   MVP drives its progress UI locally on each side, neither implementation is
   required to put `PROGRESS` on the wire at all — but both must accept and
   ignore one wherever they are reading control messages.
5. After the last byte of the last file, sender sends `DONE`
   (`fileIndex: "all"`). Receiver verifies checksums if provided, and the
   connection may then be closed.
6. Either side may send `CANCEL` at any point before `DONE` to abort the
   transfer; the other side should stop, discard partial output, and close
   the connection.
7. Any protocol violation, I/O error, disk-full condition, or checksum
   mismatch is reported via `ERROR` before closing the connection.

## 6. Versioning

- `v` at the envelope level tracks **breaking** wire-format changes only
  (e.g. changing the framing scheme, renaming/removing required fields).
  Additive changes (new optional fields, new `type` values) do not require a
  version bump, per the forward-compatibility rule in §4.
- Both implementations must reject a message whose major version they do not
  understand rather than attempting to interpret it.

## 7. Cross-implementation testing

To guard against field-naming/casing drift between the Kotlin
(`kotlinx.serialization`) and C# (`System.Text.Json`) implementations, both
repositories check in an identical set of example JSON fixtures — one file
per message type in §5 — under their respective test resources. Each side's
test suite asserts it can decode every fixture into the correct in-memory
message type with the correct field values.
