import os
import pathlib
import re
import sys

patterns = {
    "private-key": re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----", re.I),
    "github-token": re.compile(r"\bgh[pousr]_[A-Za-z0-9_]{20,}\b"),
    "openai-key": re.compile(r"\bsk-(?:proj-|live-|test-)?[A-Za-z0-9_-]{20,}\b"),
    "aws-key": re.compile(r"\bAKIA[0-9A-Z]{16}\b"),
    "google-api-key": re.compile(r"\bAIza[0-9A-Za-z_-]{30,}\b"),
    "slack-token": re.compile(r"\bxox[baprs]-[A-Za-z0-9-]{20,}\b"),
}
excluded = {".git", "node_modules", "build", ".dart_tool", "Pods", ".next", ".gradle", ".venv", "venv", "bin", "obj", "coverage", ".firebase", ".playwright-cli"}
for root_value in sys.argv[1:]:
    root = pathlib.Path(root_value)
    print(f"--- {root}")
    findings = {}
    for folder, dirs, files in os.walk(root):
        dirs[:] = [name for name in dirs if name not in excluded]
        for filename in files:
            path = pathlib.Path(folder) / filename
            try:
                if path.stat().st_size > 5_000_000:
                    continue
                data = path.read_bytes()
                if b"\0" in data[:8192]:
                    continue
                value = data.decode("utf-8", errors="ignore")
            except (OSError, PermissionError):
                continue
            for label, pattern in patterns.items():
                if pattern.search(value):
                    findings.setdefault(label, []).append(path.relative_to(root).as_posix())
    print(findings)
