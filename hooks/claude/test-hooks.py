#!/usr/bin/env python3
"""Tests for the Dexicon hooks, against a fake server.

Needs no Dexicon instance: it stands up an HTTP server on a loopback port and points the
hooks at it. `--live` runs them against a real one using ~/.claude/dexicon-hooks.env
instead, which checks the shape of the real responses rather than these fixtures.

The property under test throughout is the one that matters most: a hook never exits
non-zero, and never stays silent about a fault. On UserPromptSubmit an exit code of 2
blocks the prompt and erases it.
"""
import argparse
import json
import os
import subprocess
import sys
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer

HERE = os.path.dirname(os.path.abspath(__file__))
CORPORA_HOOK = os.path.join(HERE, "dexicon-corpora.py")
CONTEXT_HOOK = os.path.join(HERE, "dexicon-context.py")

CORPORA_OK = [
    {"name": "docs", "fileCount": 16, "chunkCount": 145, "state": "ready",
     "description": "Dexicon's own documentation, " + ("long " * 60) + "end"},
    {"name": "books", "fileCount": 96, "chunkCount": 15068, "state": "ready"},
]

failures = []


class Handler(BaseHTTPRequestHandler):
    routes = {}

    def log_message(self, *_args):
        pass

    def _respond(self):
        status, body = self.routes.get(self.path, (404, {"error": "no route"}))
        if self.headers.get("Authorization") != "Bearer test-key":
            status, body = 401, {"error": "unauthorized"}
        payload = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def do_GET(self):
        self._respond()

    def do_POST(self):
        length = int(self.headers.get("Content-Length") or 0)
        self.rfile.read(length)
        self._respond()


def serve(routes):
    Handler.routes = routes
    server = HTTPServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server


def run(hook, config, stdin="", extra_env=None):
    env = {k: v for k, v in os.environ.items() if not k.startswith("DEXICON_")}
    env["DEXICON_HOOKS_ENV"] = config
    env.update(extra_env or {})
    proc = subprocess.run([sys.executable, hook], input=stdin, env=env,
                          capture_output=True, text=True, timeout=60)
    return proc


def check(name, condition, detail=""):
    print("  %-58s %s" % (name, "ok" if condition else "FAIL"))
    if not condition:
        failures.append("%s %s" % (name, detail))


def write_config(path, url, token="test-key", extra=""):
    with open(path, "w", encoding="utf-8") as fh:
        fh.write("DEXICON_URL=%s\nDEXICON_TOKEN=%s\nDEXICON_TIMEOUT=10\n%s" % (url, token, extra))
    return path


def main(tmp):
    prompt = json.dumps({"prompt": "how does the chunker split a markdown document",
                         "hook_event_name": "UserPromptSubmit"})

    server = serve({
        "/api/corpora": (200, CORPORA_OK),
        "/api/context": (200, {"context": "04-ingestion.md:1-9 (corpus: docs)\n> a passage",
                               "citations": [{"filePath": "04-ingestion.md"}],
                               "degraded": False, "truncated": False, "droppedHits": 0}),
    })
    url = "http://127.0.0.1:%d" % server.server_port
    good = write_config(os.path.join(tmp, "good.env"), url)

    print("\nSessionStart")
    p = run(CORPORA_HOOK, good)
    check("exits 0", p.returncode == 0, str(p.returncode))
    check("names each corpus", "docs:" in p.stdout and "books:" in p.stdout, p.stdout[:80])
    check("formats large counts", "15,068" in p.stdout, p.stdout[:80])
    check("caps a long description", "..." in p.stdout and len(p.stdout) < 900, str(len(p.stdout)))

    p = run(CORPORA_HOOK, good, extra_env={"DEXICON_CORPORA_DESCRIPTION_CHARS": "0"})
    check("description cap of 0 drops the text", "long long" not in p.stdout)

    print("\nUserPromptSubmit")
    p = run(CONTEXT_HOOK, good, prompt)
    check("exits 0", p.returncode == 0, str(p.returncode))
    check("prints the passage", "a passage" in p.stdout, p.stdout[:80])
    check("says it was retrieved, not authored", "similarity" in p.stdout, p.stdout[:80])

    p = run(CONTEXT_HOOK, good, json.dumps({"prompt": "ok thanks"}))
    check("ignores a short prompt", p.stdout == "" and p.returncode == 0)

    p = run(CONTEXT_HOOK, good, json.dumps({"prompt": "/dexicon-search something or other"}))
    check("ignores a slash command", p.stdout == "" and p.returncode == 0)

    p = run(CONTEXT_HOOK, good, "not json at all")
    check("survives junk on stdin", p.returncode == 0 and p.stdout == "", str(p.returncode))

    p = run(CONTEXT_HOOK, good, json.dumps({"nothing": "here"}))
    check("survives a payload with no prompt", p.returncode == 0 and p.stdout == "")

    print("\nDegradation, and saying so")
    missing = os.path.join(tmp, "absent.env")
    for hook, label in ((CORPORA_HOOK, "SessionStart"), (CONTEXT_HOOK, "UserPromptSubmit")):
        stdin = prompt if hook is CONTEXT_HOOK else ""
        p = run(hook, missing, stdin)
        check("%s: no config -> exit 0, warns" % label,
              p.returncode == 0 and p.stdout == "" and "dexicon" in p.stderr, p.stderr[:80])

    bad_token = write_config(os.path.join(tmp, "bad.env"), url, token="wrong")
    p = run(CONTEXT_HOOK, bad_token, prompt)
    check("401 -> exit 0, warns, no output",
          p.returncode == 0 and p.stdout == "" and "401" in p.stderr, p.stderr[:80])

    dead = write_config(os.path.join(tmp, "dead.env"), "http://127.0.0.1:1")
    p = run(CONTEXT_HOOK, dead, prompt)
    check("unreachable -> exit 0, warns, no output",
          p.returncode == 0 and p.stdout == "" and "reach" in p.stderr.lower(), p.stderr[:80])

    print("\nThe budget, and the server's note")
    note = "No result fitted a budget of 1,500 characters; the smallest is 2,109."
    server2 = serve({
        "/api/context": (200, {"context": "", "citations": [], "degraded": False,
                               "truncated": True, "droppedHits": 10, "note": note}),
        "/api/corpora": (200, CORPORA_OK),
    })
    small = write_config(os.path.join(tmp, "small.env"), "http://127.0.0.1:%d" % server2.server_port)
    p = run(CONTEXT_HOOK, small, prompt)
    check("empty passage -> the server's note, on stderr",
          p.returncode == 0 and p.stdout == "" and "2,109" in p.stderr, p.stderr[:90])

    server3 = serve({
        "/api/context": (200, {"context": "a keyword-only passage", "citations": [{"a": 1}],
                               "degraded": True, "truncated": False, "droppedHits": 0,
                               "note": "Corpus 'books' is still indexing."}),
        "/api/corpora": (200, CORPORA_OK),
    })
    degraded = write_config(os.path.join(tmp, "degraded.env"),
                            "http://127.0.0.1:%d" % server3.server_port)
    p = run(CONTEXT_HOOK, degraded, prompt)
    check("degraded is disclosed in the context", "degraded" in p.stdout, p.stdout[:90])
    check("a note beside a passage reaches the model", "still indexing" in p.stdout, p.stdout[:90])

    print("\nNothing matched is not a fault")
    server4 = serve({
        "/api/context": (200, {"context": "", "citations": [], "degraded": False,
                               "truncated": False, "droppedHits": 0}),
        "/api/corpora": (200, []),
    })
    empty = write_config(os.path.join(tmp, "empty.env"), "http://127.0.0.1:%d" % server4.server_port)
    p = run(CONTEXT_HOOK, empty, prompt)
    check("no hits -> silent, and no warning", p.stdout == "" and p.stderr.strip() == "", p.stderr[:80])
    p = run(CORPORA_HOOK, empty)
    check("no corpora -> silent", p.stdout == "" and p.returncode == 0)


def live():
    config = os.path.join(os.path.expanduser("~"), ".claude", "dexicon-hooks.env")
    if not os.path.exists(config):
        print("no %s; run install-mcp.ps1 -What hooks first" % config)
        return
    print("\nAgainst the real instance")
    p = run(CORPORA_HOOK, config)
    check("SessionStart exits 0", p.returncode == 0)
    check("SessionStart says something", bool(p.stdout.strip()), p.stderr[:120])
    p = run(CONTEXT_HOOK, config,
            json.dumps({"prompt": "how does the chunker split a markdown document"}),
            extra_env={"DEXICON_TIMEOUT": "60"})
    check("UserPromptSubmit exits 0", p.returncode == 0)
    check("UserPromptSubmit answers or explains",
          bool(p.stdout.strip()) or bool(p.stderr.strip()), "silent on both streams")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--live", action="store_true")
    args = parser.parse_args()

    import tempfile
    with tempfile.TemporaryDirectory() as tmp:
        main(tmp)
        if args.live:
            live()

    print()
    if failures:
        print("%d failed:" % len(failures))
        for f in failures:
            print("  " + f)
        sys.exit(1)
    print("all passed")
