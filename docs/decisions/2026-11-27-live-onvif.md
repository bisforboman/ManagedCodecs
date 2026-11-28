# Live camera viewing: finding ONVIF cameras and their streams (2026-11-27)

Issue #527 lists ONVIF discovery among what the live-camera support doesn't do: a user had to know each camera's RTSP
URL, which differs by vendor and model (`/Streaming/Channels/101`, `/cam/realmonitor?channel=1&subtype=0`,
`/onvif1`, ...). ONVIF cameras answer a multicast probe with their ONVIF device service's address, and that service
names each media profile's RTSP URL. Taken from #527's list on the owner's choice (2026-11-27).

## Decided (the owner, 2026-11-27)

1. **Scope: discovery and stream URIs.** Not discovery alone, which gives only the ONVIF service's address and leaves
   the RTSP path to the user. PTZ, events, imaging settings and talking back stay out.
2. **API: a task that returns the cameras found within a wait**, not an `IAsyncEnumerable` yielding them as they
   answer: simpler to call from a button, and answers come within half a second anyway (below).
3. **Package: ManagedCodecs.Playback**, beside `RtspSource` and `LivePlayer`, which the stream URIs feed. No new
   package ID to release.

## The API

```csharp
IReadOnlyList<OnvifDevice> cameras = await OnvifDiscovery.FindAsync(TimeSpan.FromSeconds(3));
var camera = cameras[0];                       // camera.Name, camera.Hardware, camera.Addresses
IReadOnlyList<OnvifStream> streams = await camera.GetStreamsAsync(new NetworkCredential("admin", "secret"));
var player = new LivePlayer(new RtspSource(streams[0].Uri, credential));
```

- `OnvifDiscovery.FindAsync(TimeSpan wait, CancellationToken)`: the cameras that answered within `wait`, each once.
- `OnvifDevice`: what a camera's ProbeMatch said (its endpoint, device service addresses, scopes, and the scopes'
  name and hardware), and `new OnvifDevice(Uri)` for a camera at a known address (another subnet, across a VPN or a
  NAT, where no multicast reaches it).
- `OnvifDevice.GetStreamsAsync(NetworkCredential?, CancellationToken)`: each media profile's name, token, RTSP URI,
  video encoding and size. The encoding says which streams the library can play (H264 or JPEG, not H265).

## From the specifications

Implemented from ONVIF Core Specification 26.06 (section 7, Device discovery; 5.9, Security; 8.1.2, GetServices and
GetCapabilities), ONVIF Media Service Specification 24.12 (5.2.2 GetProfiles, 5.15.1 GetStreamUri), WS-Discovery 1.1
(OASIS) for the message rules, and the OASIS WS-Security UsernameToken Profile 1.0. No implementation's source was read.

- **The probe** is SOAP 1.2 over UDP to 239.255.255.250:3702, in the namespaces ONVIF names (Core, Table 2):
  WS-Discovery 2005/04 (`http://schemas.xmlsoap.org/ws/2005/04/discovery`) and WS-Addressing 2004/08, not
  WS-Discovery 1.1's own, sent to `urn:schemas-xmlsoap-org:ws:2005:04:discovery`. Answers are unicast to the
  probe's source address and port, after a random delay of up to APP_MAX_DELAY (500 ms).
- **Two probes, one a type:** `tds:Device` (which Core 7.3.2.1 says every device shall list) and
  `dn:NetworkVideoTransmitter` (which older cameras list instead). A probe with both would match only devices listing
  both; one with neither would bring printers and PCs too. Each is sent three times (SOAP-over-UDP's repetition: the
  same message ID, at growing random delays), from every IPv4 interface that is up, so a camera on any of the
  machine's networks hears it.
- **Merged by endpoint:** a camera on two networks, or answering both probes, is one device, its addresses gathered.
  Its addresses are ordered with the one on the host it answered from first.
- **The media service's address** from GetServices (the service with namespace
  `http://www.onvif.org/ver10/media/wsdl`), else from GetCapabilities (older devices), else the device service's own.
  Media (ver10) only: every Profile S camera has it; Media2 (ver20) alone, without ver10, is not handled.
- **Stream URIs** by GetProfiles, then GetStreamUri for each profile with `RTP-Unicast` over `RTSP`.
- **Authentication:** HTTP Digest (RFC 2617) first, which Core 5.9.1 makes the rule, through the HTTP handler's
  credentials. A device answering 400 or 401 with a SOAP fault (`NotAuthorized`) gets the WS-Security
  UsernameToken instead, which Core 5.9.1 allows for legacy devices: `Base64(SHA-1(nonce + created + password))`,
  `created` taken from the device's own clock (GetSystemDateAndTime, which needs no authentication), since a camera
  without NTP is often hours off and rejects a token outside its window. SHA-1 is the profile's, not a choice: the
  security analyzer's finding is suppressed there with that justification.
- **Untrusted input:** answers are parsed with DTDs prohibited, no resolver and a size limit; a datagram that doesn't
  parse, or doesn't answer this probe's message ID, is ignored.

## Checks

docs/conformance.md, ONVIF. No IP camera is in reach of the sandbox (#527, Not tried).

- A simulated device in the tests: a WS-Discovery responder and an HTTP server that answers the SOAP calls, with Digest
  and with UsernameToken only, and its clock hours off.
- An independent WS-Discovery responder: Python's `wsdiscovery` package publishing a service with ONVIF's types and
  scopes, which mcdec finds.
- What is not tried: a real camera, Media2-only devices, IPv6 discovery and HTTPS device services with
  self-signed certificates (`GetStreamsAsync` uses the platform's certificate checks).
