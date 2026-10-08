#!/usr/bin/env python3
"""Wait for the packaged Codev window and verify its key controls via AT-SPI."""

from __future__ import annotations

import sys
import time
import argparse

try:
    import pyatspi
except Exception as exc:  # pragma: no cover - exercised by the Linux CI runner
    raise SystemExit(f"Could not import system pyatspi: {exc}") from exc


REQUIRED_NAMES = {
    "Message Codev",
    "Send or queue prompt; stop when the composer is empty",
    "Search conversations",
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
    focused_names = set()
    for node in accessible_nodes(desktop):
        try:
            name = node.name or ""
            role = node.getRoleName()
            focused = node.getState().contains(pyatspi.STATE_FOCUSED)
        except Exception:
            continue
        if name:
            names.add(name)
            if name in REQUIRED_NAMES:
                matches.append((role, name))
                if focused:
                    focused_names.add(name)
    return names, matches, focused_names


parser = argparse.ArgumentParser()
parser.add_argument("--focused", help="Require this accessible control to report focused state.")
arguments = parser.parse_args()

deadline = time.monotonic() + DEADLINE_SECONDS
last_names = set()
last_matches = []
last_focused_names = set()
last_error = None
while time.monotonic() < deadline:
    try:
        last_names, last_matches, last_focused_names = snapshot()
        last_error = None
        if REQUIRED_NAMES.issubset(last_names) and (
            arguments.focused is None or arguments.focused in last_focused_names
        ):
            for role, name in last_matches:
                print(f"AT-SPI exposed {role}: {name}")
            if arguments.focused:
                print(f"AT-SPI reports focused: {arguments.focused}")
            raise SystemExit(0)
    except SystemExit:
        raise
    except Exception as exc:
        last_error = exc
    time.sleep(0.25)

missing = sorted(REQUIRED_NAMES - last_names)
if arguments.focused and arguments.focused not in last_focused_names:
    missing.append(f"focused state on {arguments.focused!r}")
print(f"AT-SPI did not expose required Codev controls; missing: {missing}", file=sys.stderr)
print(f"Last accessible names: {sorted(last_names)[:80]}", file=sys.stderr)
print(f"Last focused names: {sorted(last_focused_names)}", file=sys.stderr)
if last_error is not None:
    print(f"Last AT-SPI error: {last_error!r}", file=sys.stderr)
raise SystemExit(1)
