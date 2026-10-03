# MyTunnel tunnel protocol, version 1

The CLI opens a WebSocket to `GET /tunnel` on the gateway (`wss://` in production; `ws://` is accepted by the CLI only for loopback gateways). Everything after that travels over this one connection.

Source of truth: `MyTunnel.Contracts` (`TunnelMessages.cs`, `BodyFrame.cs`, `TunnelProtocol.cs`).

## Framing

| WebSocket message | Carries | Size limit |
|---|---|---|
| Text | One JSON control message | 64 KiB after reassembly |
| Binary | One body frame | 18-byte header + 32 KiB payload |

Fragmented WebSocket messages are reassembled before parsing. A message over the limit closes the connection with status 1009 (Message Too Big). Malformed JSON, unknown message types, missing required fields and semantically invalid values (bad method token, absolute-form target, CR/LF in a header value, etc.) produce an `error` message and close the connection with status 1008 (Policy Violation).

### Control messages

Flat JSON objects with a `type` discriminator. Unknown extra properties are ignored, so a peer can add optional fields without a version bump.

| type | Direction | Fields |
|---|---|---|
| `ping` | either | `message?` |
| `pong` | either | `message?` |
| `register` | CLI → GW | `protocolVersion`, `authToken?`, `subdomain?`, `clientVersion?` |
| `registered` | GW → CLI | `protocolVersion`, `tunnelId`, `subdomain`, `publicUrl`, `maxRequestBodyBytes`, `maxResponseBodyBytes` |
| `registration_rejected` | GW → CLI | `code`, `message` (connection then closes) |
| `http_request` | GW → CLI | `requestId`, `method`, `target` (origin-form `/path?query`), `headers[]`, `hasBody` |
| `http_response` | CLI → GW | `requestId`, `statusCode` (200–599), `headers[]`, `hasBody` |
| `cancel` | either | `requestId`, `reason?` |
| `window` | either | `requestId`, `bytes` |
| `error` | either | `code`, `message`, `requestId?` |

`headers` is an array of `{ "name": ..., "value": ... }`. Repeated headers (for example `Set-Cookie`) are repeated entries. At most 128 headers and 32 KiB in total.

The original proof-of-concept exchange `{"type":"ping","message":"Hello from MyTunnel CLI!"}` → `{"type":"pong","message":"Hello from MyTunnel Gateway!"}` is still valid in v1, before and after registration. A regression test pins it.

### Body frames

```
byte 0      kind     (1 = body)
byte 1      flags    (bit 0 = end of stream; other bits must be 0)
bytes 2-17  requestId (UUID, RFC 4122 big-endian byte order)
bytes 18-   payload  (0..32768 bytes)
```

Bodies are raw bytes, not Base64. A body is a sequence of frames for one `requestId` ending with a frame whose end-of-stream flag is set (the payload may be empty).

## Lifecycle

1. CLI connects, sends `register`. It may send `ping` first.
2. Gateway checks the version range, authenticates the token, validates or allocates the subdomain and claims it in the registry. It replies `registered` or `registration_rejected` and closes.
   * No `register` within `Gateway:RegistrationTimeout` (10 s) → `registration_rejected`/`registration_timeout`.
   * Anything else before registration → `error`/`not_registered` and close.
3. For each public request the gateway sends `http_request`, then the request body frames if `hasBody`.
4. The CLI answers with `http_response`, then the response body frames if `hasBody`.
5. When the connection closes, the gateway removes the tunnel from the registry immediately and fails all in-flight requests with 502.

Rejection codes: `unauthorized`, `unsupported_protocol_version`, `invalid_subdomain`, `subdomain_unavailable`, `tunnel_limit_reached`, `registration_timeout`.

## Concurrency and correlation

Any number of requests may be in flight on one connection (the gateway caps them per tunnel). `requestId` is a random UUID chosen by the gateway, and responses may arrive in any order. Frames or responses for an unknown `requestId` (for example after a timeout) are dropped. A second `http_response` for the same id, a body frame before the `http_response`, a body frame for a request without a body, or an `http_request` sent by the CLI are protocol violations.

## Flow control

Each body stream (one request body, one response body) has its own credit window, starting at 256 KiB. The sender may only have that many unacknowledged payload bytes outstanding. As the receiver's consumer reads data, the receiver returns credit with `window` messages (in batches of at least 128 KiB). A sender that exceeds its window violates the protocol.

This bounds buffered memory per request to one window, and it stops a slow visitor or slow local app from stalling other requests on the same connection.

## Cancellation and timeouts

* The gateway sends `cancel` when the visitor disconnects, when the response-header timeout expires (`Gateway:ResponseHeadersTimeout`, 504 to the visitor), or when a size limit is exceeded.
* The CLI sends `cancel` if the local response fails mid-stream or exceeds the gateway's announced `maxResponseBodyBytes`.
* The CLI answers 502 (`local_unreachable`) if the local app refuses the connection and 504 (`local_timeout`) if it does not return headers in time.

Liveness is handled by WebSocket protocol-level ping/pong (`KeepAliveInterval` 15 s, `KeepAliveTimeout` 30 s on both ends). A silent peer is detected and its tunnel removed.

## Versioning

`TunnelProtocol.CurrentVersion` and `MinSupportedVersion` define the supported range. Additive changes (new optional fields, new message types guarded by version) keep the version. A breaking change bumps it, and the gateway rejects out-of-range clients with a message telling the user to update.
