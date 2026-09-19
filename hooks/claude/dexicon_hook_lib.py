#!/usr/bin/env python3
# dexicon-hook-version: 1
"""Shared by the Dexicon hooks: the config file, one HTTP call, and a warning channel.

Installed beside the hooks, so `sys.path` gets the hook's own directory rather than relying
on where Claude Code happens to run it from.

Nothing here raises. A hook that fails has to fail quietly enough to leave the session
working, and loudly enough on stderr that someone can tell it from a hook that ran and found
nothing. Silence on both is how a broken hook survives for months.
"""
import json
import os
import sys
import urllib.error
import urllib.request

DEFAULT_URL = "http://localhost:8477"
DEFAULT_CONNECT_TIMEOUT = 2.0
DEFAULT_TIMEOUT = 5.0

CONFIG_ENV = "DEXICON_HOOKS_ENV"
DEFAULT_CONFIG = os.path.join(os.path.expanduser("~"), ".claude", "dexicon-hooks.env")


def _use_utf8() -> None:
    """Make the streams take any text the index holds.

    A console on Windows is cp1252, and printing a corpus description containing anything
    outside it raises UnicodeEncodeError. The hook's own catch-all would turn that into a
    clean exit and no output, which reads exactly like a corpus with nothing in it.
    """
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except Exception:  # noqa: BLE001 - older Python, or a stream that cannot
            pass


_use_utf8()


def warn(message: str) -> None:
    """Say what went wrong on stderr, where it does not become model context."""
    try:
        sys.stderr.write("[dexicon] %s\n" % message)
    except Exception:  # noqa: BLE001
        pass


def load_config() -> dict:
    """Read `KEY=value` lines. Missing file is not an error: the hooks then do nothing.

    The process environment wins over the file, so a single run can be pointed elsewhere
    without editing anything.
    """
    path = os.environ.get(CONFIG_ENV) or DEFAULT_CONFIG
    cfg = {}
    try:
        with open(path, "r", encoding="utf-8") as fh:
            for raw in fh:
                line = raw.strip()
                if not line or line.startswith("#") or "=" not in line:
                    continue
                key, _, value = line.partition("=")
                cfg[key.strip()] = value.strip().strip('"').strip("'")
    except FileNotFoundError:
        warn("no config at %s; run install-mcp.ps1 -What hooks" % path)
    except OSError as exc:
        warn("cannot read %s: %s" % (path, exc))

    for key, value in os.environ.items():
        if key.startswith("DEXICON_"):
            cfg[key] = value
    return cfg


def _timeout(cfg: dict) -> float:
    try:
        return float(cfg.get("DEXICON_TIMEOUT") or DEFAULT_TIMEOUT)
    except (TypeError, ValueError):
        return DEFAULT_TIMEOUT


def _request(cfg: dict, path: str, payload=None):
    url = (cfg.get("DEXICON_URL") or DEFAULT_URL).rstrip("/") + path
    data = json.dumps(payload).encode("utf-8") if payload is not None else None

    req = urllib.request.Request(url, data=data, method="POST" if data else "GET")
    req.add_header("Authorization", "Bearer " + cfg["DEXICON_TOKEN"])
    if data:
        req.add_header("Content-Type", "application/json")

    # One timeout covering connect and read. The failure worth guarding against is a server
    # that accepts the connection and then does not answer, which a connect timeout misses.
    with urllib.request.urlopen(req, timeout=_timeout(cfg)) as response:
        return json.load(response)


def get(cfg: dict, path: str):
    """GET and decode, or None with a reason on stderr. Never raises."""
    return _call(cfg, path, None)


def post(cfg: dict, path: str, payload: dict):
    """POST and decode, or None with a reason on stderr. Never raises."""
    return _call(cfg, path, payload)


def _call(cfg: dict, path: str, payload):
    try:
        return _request(cfg, path, payload)
    except urllib.error.HTTPError as exc:
        if exc.code == 401:
            warn("401 from %s: the key in the config file is not accepted" % path)
        elif exc.code == 403:
            warn("403 from %s: the key lacks the scope, or reaches no corpus" % path)
        else:
            warn("%s returned HTTP %s" % (path, exc.code))
    except urllib.error.URLError as exc:
        warn("cannot reach Dexicon at %s (%s)" % (cfg.get("DEXICON_URL") or DEFAULT_URL, exc.reason))
    except (ValueError, TypeError) as exc:
        warn("%s did not return usable JSON: %s" % (path, exc))
    except Exception as exc:  # noqa: BLE001
        warn("%s failed: %s" % (path, exc))
    return None
