import pathlib
import re
import subprocess
import sys

projects = [pathlib.Path(value) for value in sys.argv[1:]]
patterns = {
    "private-key": re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----", re.I),
    "github-token": re.compile(r"\bgh[pousr]_[A-Za-z0-9_]{20,}\b"),
    "openai-key": re.compile(r"\bsk-(?:proj-|live-|test-)?[A-Za-z0-9_-]{20,}\b"),
    "aws-key": re.compile(r"\bAKIA[0-9A-Z]{16}\b"),
    "google-api-key": re.compile(r"\bAIza[0-9A-Za-z_-]{30,}\b"),
    "slack-token": re.compile(r"\bxox[baprs]-[A-Za-z0-9-]{20,}\b"),
}

def run(repo, *args):
    return subprocess.run(["git", "-C", str(repo), *args], capture_output=True, text=True)

for repo in projects:
    print(f"--- {repo}")
    files = run(repo, "ls-files", "-co", "--exclude-standard", "-z").stdout.split("\0")
    current = {}
    for relative in filter(None, files):
        path = repo / relative
        try:
            data = path.read_bytes()
            if b"\0" in data[:8192]:
                continue
            content = data.decode("utf-8", errors="ignore")
        except (OSError, PermissionError):
            continue
        for label, pattern in patterns.items():
            if pattern.search(content):
                current.setdefault(label, set()).add(relative.replace("\\", "/"))
    print("current-worktree:", {key: sorted(value) for key, value in current.items()})
    commits = run(repo, "rev-list", "--all").stdout.splitlines()
    findings = {}
    objects = run(repo, "rev-list", "--objects", "--all").stdout.splitlines()
    paths_by_object = {}
    for line in objects:
        parts = line.split(" ", 1)
        if len(parts) == 2 and parts[1]:
            paths_by_object.setdefault(parts[0], set()).add(parts[1].replace("\\", "/"))
    if paths_by_object:
        input_data = "".join(object_id + "\n" for object_id in paths_by_object).encode("ascii")
        output = subprocess.run(
            ["git", "-C", str(repo), "cat-file", "--batch"],
            input=input_data,
            capture_output=True,
            check=True,
        ).stdout
        offset = 0
        for object_id in paths_by_object:
            end = output.index(b"\n", offset)
            header = output[offset:end].decode("ascii", errors="replace").split()
            offset = end + 1
            if len(header) < 3:
                continue
            size = int(header[2])
            data = output[offset:offset + size]
            offset += size + 1
            if header[1] != "blob":
                continue
            if b"\0" in data[:8192]:
                continue
            content = data.decode("utf-8", errors="ignore")
            matched = [label for label, pattern in patterns.items() if pattern.search(content)]
            for label in matched:
                for filename in paths_by_object[object_id]:
                    findings.setdefault(filename, set()).add(label)
    print(f"history commits={len(commits)} matching-files={findings}")
