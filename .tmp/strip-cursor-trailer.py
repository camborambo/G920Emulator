#!/usr/bin/env python3
"""Strip Cursor attribution trailers from a commit message (stdin -> stdout)."""
import re
import sys

msg = sys.stdin.read()
# Remove Co-authored-by / Made-with Cursor lines (any casing).
msg = re.sub(
    r"(?im)^(?:Co-authored-by:\s*Cursor(?:\s*<[^>\n]*>)?|Made-with:\s*Cursor)\s*\n?",
    "",
    msg,
)
# Collapse trailing blank lines left by trailer removal.
msg = re.sub(r"\n{3,}\Z", "\n\n", msg)
sys.stdout.write(msg)
