# Live camera viewing, step 6: RTSPS, RTSP over TLS (2026-11-15)

A plan, decided on by the owner (Decided, below), and built in the same pull request. Issue #525, split from #462. Steps 2 to 5
(docs/decisions/2026-11-10-live-rtsp-h264.md to 2026-11-15-live-rtsp-audio.md) take only `rtsp://` URLs:
`RtspSource` refuses `rtsps://`, and the RTSP connection is a plain `TcpClient` stream. Some cameras, most NVRs' remote
access and most cloud relays serve only `rtsps://`.

## What TLS changes

- **The connection.** The RTSP requests, the answers and the RTP interleaved on the connection (`$` frames) go through
  a TLS stream (`SslStream`) around the TCP one. Nothing above the stream changes: the same requests, authentication,
  depacketizers and keep-alives. The default port is 322 (IANA's for rtsps), not 554.
- **RTP over UDP can't simply follow.** Over UDP beside a TLS connection the packets would go in the clear, which is
  what the camera's owner chose TLS to avoid. Encrypted UDP is SRTP (RFC 3711), keyed by MIKEY (RFC 3830) in the SDP and
  in SETUP (RFC 4567): a protocol of its own, which FFmpeg's client doesn't do either.
- **The certificate.** A camera's certificate is nearly always self-signed, or names the device rather than the
  address it is reached at, so the system's validation (a trusted chain and a matching name) refuses it. A cloud relay's
  usually passes. The caller needs a way to accept one camera's certificate without turning validation off for every
  server.
- **Where it runs.** `SslStream` uses the platform's TLS: SChannel on Windows, Apple's on macOS and iOS, and OpenSSL on
  Linux (a system library, as for HTTPS, not one this library ships; Native AOT loads it the same way). Unity's Mono
  has its own. WebAssembly has no sockets at all, as for RTSP today.

## Measured (2026-11-15)

mediamtx 1.21.3 (tools/rtsp-nightly.py's pinned build) with `rtspEncryption: strict`, a self-signed certificate
(openssl, CN and subjectAltName `localhost` and 127.0.0.1) and FFmpeg 6.1.1 publishing h264-main-slices.mp4 to it:

| Client asks for | mediamtx answers |
|---|---|
| `RTP/AVP/TCP;unicast;interleaved=0-1` | 200, plain RTP inside the TLS connection |
| `RTP/AVP;unicast;client_port=...` (plain UDP) | 461 Unsupported Transport |
| `RTP/SAVP;unicast;client_port=...` without MIKEY | 400 Bad Request |
| `RTP/SAVP/TCP;...` | 400 Bad Request |

- It listens on 8322 (RTSPS) and on 8004/8005 for SRTP/SRTCP. Its SDP's media line says `RTP/SAVP` with an
  `a=key-mgmt:mikey` key even to a client that then asks for TCP; our SDP parser ignores the profile field, so that
  changes nothing.
- **FFmpeg's client** plays `rtsps://` over TCP, and asked for UDP it falls back to TCP: it has no SRTP with MIKEY. It
  doesn't check the certificate at all by default (its `tls_verify` is 0), which is how it accepted the self-signed one.

## Proposal

1. **`rtsps://` URLs** accepted by `RtspSource`, port 322 by default; the connection wrapped in an `SslStream`, the
   server name from the URL (SNI and the name checked), the protocol version the platform's default (TLS 1.2 or 1.3).
2. **The certificate** (decision 1): validated by the system by default, with a way to accept a given camera's.
3. **RTP over the TLS connection only** (decision 2): a 461 to it ends the attempt with an error that says the server
   wants SRTP over UDP, which isn't supported, instead of the plain-UDP fallback `rtsp://` has. SRTP (MIKEY) would be a
   later step if a camera needs it.
4. **Everything else as for `rtsp://`**: authentication (Basic too, now inside TLS), keep-alives, stall timeout and
   reconnects (a new TLS handshake each time), audio, statistics.
5. **Not in this step**: client certificates (mutual TLS), SRTP, RTSP over HTTP(S) tunnelling.

## Checks

- `RtspTestServer` grows TLS: the same recorded packets through an `SslStream` with a certificate the test makes
  (`CertificateRequest`, self-signed). Lockstep frames as for TCP, H.264 and JPEG, with and without audio.
- The certificate rules: a self-signed certificate refused by default, accepted when the caller accepts it (decision
  1), a wrong name refused, an expired one refused; a 461 over TLS fails with the SRTP error and no UDP ports opened.
- Mutation entries for the rules no recording reaches: the default port, the refusal of plain UDP over TLS, and the
  certificate check.
- Nightly: tools/rtsp-nightly.py gains a run against mediamtx with `rtspEncryption: strict` and a certificate it makes,
  accepted as decision 1 says.

## Decided

Asked with the multiple-choice prompt; the owner took each recommendation (2026-11-15).

1. **The certificate API. Decided: (a), a pinned certificate.**
   - **(a) A pinned certificate**: `RtspSource.CertificateHash`, the SHA-256 of the certificate the camera presents
     (what a browser or `openssl x509 -fingerprint -sha256` shows). With it, that certificate is accepted whatever its
     chain or name, and no other; without it, the system validates. Small, and hard to misuse. Recommended.
   - (b) A callback, `RemoteCertificateValidationCallback` from System.Net.Security as a property: any rule the caller
     likes (a company CA, logging), but `(_, _, _, _) => true` is one line away and accepts any server.
   - (c) Both.
2. **A 461 over TLS. Decided: (a), fail with an error naming SRTP.**
   - **(a) Fail with an error naming SRTP** (recommended): media stays inside TLS; SRTP is a later step.
   - (b) Fall back to plain UDP as `rtsp://` does: plays more servers, but the media goes in the clear, unknown to the
     caller who chose `rtsps://`.

A consumer-visible change, so a CHANGELOG line under "Unreleased" when it is built; new public API with 1 (a), (b) or
so a minor version.
