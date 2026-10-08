#!/usr/bin/env python3
"""Wait for the packaged Codev window and verify its key controls via AT-SPI."""

from __future__ import annotations

import sys
import time

try:
    import pyatspi
except Exception as exc:  # pragma: no cover - exercised by the Linux CI runner
    raise SystemExit(f"Could not import system pyatspi: {exc}") from exc


REQUIRED_NAMES = {
    "Message Codev",
    "Send or queue prompt; stop when the composer is empty",
}
DEADLINE_SECONDS = 20


def accessible_nodes(root):
    """Traverse a bounded portion of the desktop tree, tolerating transient nodes."""
    stack = [(root, 0)]
    visited = 0
    while stack and visited < 5000:
        node, depth = stack.pop()
        visited += 1
        yield node
        if depth >= 12:
            continue
        try:
            children = [node.getChildAtIndex(i) for i in range(node.childCount)]
        except Exception:
            continue
        stack.extend((child, depth + 1) for child in reversed(children) if child is not None)


def snapshot():
    desktop = pyatspi.Registry.getDesktop(0)
    names = set()
    matches = []
    for node in accessible_nodes(desktop):
        try:
            name = node.name or ""
            role = node.getRoleName()
        except Exception:
            continue
        if name:
            names.add(name)
            if name in REQUIRED_NAMES:
                matches.append((role, name))
    return names, matches


deadline = time.monotonic() + DEADLINE_SECONDS
last_names = set()
last_matches = []
last_error = None
while time.monotonic() < deadline:
    try:
        last_names, last_matches = snapshot()
        last_error = None
        if REQUIRED_NAMES.issubset(last_names):
            for role, name in last_matches:
                print(f"AT-SPI exposed {role}: {name}")
            raise SystemExit(0)
    except SystemExit:
        raise
    except Exception as exc:
        last_error = exc
    time.sleep(0.25)

missing = sorted(REQUIRED_NAMES - last_names)
print(f"AT-SPI did not expose required Codev controls; missing: {missing}", file=sys.stderr)
print(f"Last accessible names: {sorted(last_names)[:80]}", file=sys.stderr)
if last_error is not None:
    print(f"Last AT-SPI error: {last_error!r}", file=sys.stderr)
raise SystemExit(1)
