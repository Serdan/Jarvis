#!/usr/bin/env python3
"""Disposable job canary. Does not start, stop or reconnect Jarvis itself."""
import argparse
import json
import os
from pathlib import Path
import sys
import time
import uuid

from qualify import receipt


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--output", required=True, type=Path)
    p.add_argument("--nonce", required=True, type=uuid.UUID)
    p.add_argument("--deadline", type=int, default=180, choices=range(1, 301),
                   metavar="1..300")
    a = p.parse_args()
    # An existing directory is evidence of an original invocation, not consent
    # to replay it. A second invocation never changes the original evidence.
    a.output.mkdir(mode=0o700)
    nonce = str(a.nonce)
    receipt(a.output / "probe-started.json", {
        "nonce": nonce, "pid_diagnostic": os.getpid(), "started_unix": time.time(),
        "deadline_seconds": a.deadline,
    })
    print("probe-start " + nonce, flush=True)
    deadline = time.monotonic() + a.deadline
    code, disposition = 23, "deadline"
    while time.monotonic() < deadline:
        # Only the isolated fixture operator can provide the matching release.
        try:
            if (a.output / "release.json").is_file():
                marker = json.loads((a.output / "release.json").read_text())
                if marker == {"nonce": nonce}:
                    code, disposition = 17, "released"
                    break
        except (OSError, ValueError):
            pass  # A partial or invalid marker is not a release.
        time.sleep(0.05)
    receipt(a.output / "probe-exit-intent.json", {
        "nonce": nonce, "intended_exit_code": code, "disposition": disposition,
        "at_unix": time.time(),
    })
    print("probe-exit-intent " + nonce + " " + str(code), flush=True)
    # This file is *intent*, not observed OS exit evidence. A host kill between
    # write and exit is possible. The original JobManager receipt must be checked.
    return code


if __name__ == "__main__":
    sys.exit(main())
