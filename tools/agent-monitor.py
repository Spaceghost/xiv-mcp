#!/usr/bin/env python3
"""Live terminal dashboard for a Claude Code workflow run (and the xiv-mcp tree it edits).

usage: agent-monitor.py <workflow transcript dir> [repo]
Meant for a tmux window viewed through the in-game Ghostty terminal.
"""
import json, os, subprocess, sys, time
from datetime import datetime, timezone

run_dir = sys.argv[1]
repo = sys.argv[2] if len(sys.argv) > 2 else os.path.expanduser("~/xiv-mcp")
state = {}  # file -> dict(offset, label, tools, last, lastTs, done)


def short(tool, inp):
    if not isinstance(inp, dict):
        return tool
    for k in ("description", "file_path", "pattern", "query", "url", "prompt"):
        if k in inp and isinstance(inp[k], str):
            v = inp[k].replace(os.path.expanduser("~") + "/", "~/").replace("\n", " ")
            return f"{tool}: {v}"
    return tool


def scan():
    for name in sorted(os.listdir(run_dir)):
        if not (name.startswith("agent-") and name.endswith(".jsonl")):
            continue
        path = os.path.join(run_dir, name)
        st = state.setdefault(path, {"offset": 0, "label": name[6:14], "tools": 0, "last": "starting", "lastTs": None, "done": False})
        meta = path[:-6] + ".meta.json"
        if st["label"] == name[6:14] and os.path.exists(meta):
            try:
                st["label"] = json.load(open(meta)).get("description") or st["label"]
            except Exception:
                pass
        with open(path, "rb") as f:
            f.seek(st["offset"])
            data = f.read()
        cut = data.rfind(b"\n") + 1
        st["offset"] += cut
        for line in data[:cut].splitlines():
            try:
                ev = json.loads(line)
            except Exception:
                continue
            ts = ev.get("timestamp")
            msg = ev.get("message") or {}
            if ev.get("type") == "assistant" and isinstance(msg.get("content"), list):
                st["lastTs"] = ts or st["lastTs"]
                for c in msg["content"]:
                    if c.get("type") == "tool_use":
                        st["tools"] += 1
                        st["last"] = short(c.get("name", "?"), c.get("input"))
                        if c.get("name") == "StructuredOutput":
                            st["done"] = True
                    elif c.get("type") == "text" and c.get("text", "").strip():
                        st["last"] = "says: " + c["text"].strip().replace("\n", " ")


def age(ts):
    if not ts:
        return "   -"
    try:
        s = (datetime.now(timezone.utc) - datetime.fromisoformat(ts.replace("Z", "+00:00"))).total_seconds()
    except Exception:
        return "   ?"
    return f"{int(s):>3}s" if s < 120 else f"{int(s // 60):>3}m"


def tree():
    try:
        out = subprocess.run(["git", "-C", repo, "status", "--porcelain", "-uall"], capture_output=True, text=True, timeout=5).stdout
    except Exception:
        return {}
    areas = {}
    for line in out.splitlines():
        p = line[3:]
        parts = p.split("/")
        key = "/".join(parts[:4]) if p.startswith("src/XivMcp.Plugin/Providers/") else "/".join(parts[:2])
        areas[key] = areas.get(key, 0) + 1
    return areas


def render():
    cols = os.get_terminal_size().columns if sys.stdout.isatty() else 120
    lines = [f"\x1b[1mxiv-mcp agents\x1b[0m  {datetime.now():%H:%M:%S}  run {os.path.basename(run_dir)}", ""]
    for st in state.values():
        mark = "\x1b[32m✔\x1b[0m" if st["done"] else "\x1b[33m●\x1b[0m"
        head = f" {mark} {st['label']:<10} {st['tools']:>4} calls {age(st['lastTs'])}  "
        lines.append(head + st["last"][: max(10, cols - len(head) + 9)])
    lines += ["", "\x1b[1mchanged files by area\x1b[0m"]
    for k, v in sorted(tree().items()):
        lines.append(f"  {v:>4}  {k}")
    sys.stdout.write("\x1b[H\x1b[2J" + "\n".join(lines) + "\n")
    sys.stdout.flush()


while True:
    try:
        scan()
        render()
    except KeyboardInterrupt:
        break
    except Exception as e:  # keep the dashboard alive
        sys.stdout.write(f"\nmonitor error: {e}\n")
    time.sleep(3)
