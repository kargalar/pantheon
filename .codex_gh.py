import os
import subprocess
import sys

credential = subprocess.run(
    ["git", "credential", "fill"],
    input="protocol=https\nhost=github.com\n\n",
    text=True,
    capture_output=True,
    check=True,
)
fields = dict(line.split("=", 1) for line in credential.stdout.splitlines() if "=" in line)
environment = os.environ.copy()
environment["GH_TOKEN"] = fields.get("password", "")
raise SystemExit(subprocess.run(["gh", *sys.argv[1:]], env=environment).returncode)
