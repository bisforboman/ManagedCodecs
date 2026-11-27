#!/usr/bin/env python3
"""ONVIF discovery against an independent WS-Discovery responder (docs/decisions/2026-11-27-live-onvif.md).

Publishes a target service with ONVIF's types and scopes through Python's WSDiscovery package (2.x, written apart
from this library, in the same WS-Discovery 2005/04 namespaces ONVIF names), then runs `mcdec --onvif` and needs it
to list that service once, with its name, hardware, endpoint and address. Run twice: one target listing both types,
and one listing only dn:NetworkVideoTransmitter (older cameras), which only a probe of that type alone matches.

    python3 -m venv /tmp/venv && /tmp/venv/bin/pip install WSDiscovery
    dotnet build tools/ManagedCodecs.Cli -c Release
    /tmp/venv/bin/python3 tools/onvif-discovery-check.py

Exits 0 when both runs pass.
"""

import subprocess
import sys
import time
import uuid
from pathlib import Path

from wsdiscovery import QName, Scope
from wsdiscovery.publishing import ThreadedWSPublishing

ROOT = Path(__file__).resolve().parent.parent
MCDEC = ROOT / "tools/ManagedCodecs.Cli/bin/Release/net10.0/mcdec.dll"
DEVICE = "http://www.onvif.org/ver10/device/wsdl"
NETWORK = "http://www.onvif.org/ver10/network/wsdl"


def run(types, label):
    endpoint = f"urn:uuid:{uuid.uuid4()}"
    address = "http://192.0.2.10/onvif/device_service"
    publisher = ThreadedWSPublishing(uuid_=endpoint)
    publisher.start()
    try:
        publisher.publishService(
            types=types,
            scopes=[Scope("onvif://www.onvif.org/name/Check%20camera"), Scope("onvif://www.onvif.org/hardware/WSD-2"),
                    Scope("onvif://www.onvif.org/Profile/Streaming")],
            xAddrs=[address])
        time.sleep(0.5)
        out = subprocess.run(["dotnet", str(MCDEC), "--onvif", "3"], capture_output=True, text=True, timeout=60).stdout
    finally:
        publisher.stop()

    lines = [line for line in out.splitlines() if endpoint in line]
    ok = len(lines) == 1 and lines[0].startswith("Check camera | WSD-2 | ") and address in lines[0]
    print(f"{'PASS' if ok else 'FAIL'} {label}: {lines if lines else out.strip()}")
    return ok


def main():
    both = run([QName(NETWORK, "NetworkVideoTransmitter", "dn"), QName(DEVICE, "Device", "tds")], "tds:Device and dn:NetworkVideoTransmitter")
    older = run([QName(NETWORK, "NetworkVideoTransmitter", "dn")], "dn:NetworkVideoTransmitter only")
    return 0 if both and older else 1


if __name__ == "__main__":
    sys.exit(main())
