#!/usr/bin/env python3
"""Sign a browser in to the local instance without a human retyping the password.

WHY THIS EXISTS
---------------
Testing the UI end to end means being signed in. The two ways to get there are both
bad: a person types the password into the form every session, or it is read out of
.env and typed by whatever is driving the browser — which puts a live credential into
a transcript, a log, or a model's context.

This is the third way. The password goes from .env to /api/session inside this
script, and only the SHORT-LIVED SESSION it returns is handed to the page, over
loopback. Nothing in between ever renders either value; the operator sees a nonce and
a byte count.

The password itself never leaves this process, which is the part worth keeping. What
reaches the browser expires on its own and can be revoked, where the bootstrap token
this used to hand over was long-lived and could not.

WHAT IT DOES
------------
Serves the session exactly once, to exactly one origin, behind a single-use nonce, and
exits. Loopback unless --bind says otherwise. Then the page stores it the same way
the sign-in form would: sessionStorage['dexicon.token'].

    python scripts/dev-token.py

It prints a one-line snippet to run in the browser's console. Run it on a Dexicon tab.

An API key would authenticate but is not enough: a key can never carry the `admin`
scope (docs/decisions.md D-28), so a browser holding one loads the shell and then 403s
on every screen that matters. That failure looks like a broken app rather than a wrong
credential, which is exactly what the verify step below exists to prevent.

THIS IS A DEVELOPMENT TOOL. It is not in the image, it binds to loopback unless told
otherwise, it holds the session in memory for at most --timeout seconds, and it
refuses to run if the password does not actually sign in. It is not a login mechanism
and must never become one: production signs in through the form, deliberately.
"""

from __future__ import annotations

import argparse
import http.server
import json
import os
import pathlib
import secrets
import sys
import threading
import urllib.error
import urllib.request

REPO = pathlib.Path(__file__).resolve().parent.parent


def password_from_env() -> str:
    """Environment first, then .env — the same precedence the bench harness uses.

    Deliberately not a CLI argument: an argument lands in shell history and in the
    process list, which is most of what this script exists to avoid.
    """
    if value := os.environ.get("DEXICON_ADMIN_PASSWORD"):
        return value.strip()

    env = REPO / ".env"
    if not env.exists():
        sys.exit(
            "No DEXICON_ADMIN_PASSWORD in the environment and no .env file. See .env.example."
        )

    for line in env.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line.startswith("DEXICON_ADMIN_PASSWORD=") and not line.startswith("#"):
            value = line.split("=", 1)[1].strip().strip("'\"")
            if value:
                return value

    sys.exit(
        "DEXICON_ADMIN_PASSWORD is blank in .env, which means the server generated one\n"
        "on first run and logged it once:\n"
        "    docker compose logs dexicon | grep 'admin password'\n"
        "Set it in .env to make this repeatable; a configured value is applied on every start."
    )


def sign_in(base: str, password: str) -> str:
    """Exchange the password for a session, here rather than in the browser.

    The password never leaves this process. What comes back expires on its own, which
    is why it is the thing worth handing over.

    A wrong password is answered slowly on purpose: the endpoint throttles by a
    doubling delay counted across the whole install, so a failure here may take up to
    30 seconds to come back. That is the feature working, not the script hanging.
    """
    body = json.dumps({"password": password}).encode()
    request = urllib.request.Request(
        f"{base}/api/session", data=body,
        headers={"Content-Type": "application/json"}, method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            return json.load(response)["token"]
    except urllib.error.HTTPError as e:
        if e.code == 401:
            sys.exit(
                f"{base} did not accept the password (HTTP 401).\n"
                "If the catalogue was recreated, the server printed a new one:\n"
                "    docker compose logs dexicon | grep 'admin password'"
            )
        sys.exit(f"{base} answered HTTP {e.code}. Is this the right instance?")
    except urllib.error.URLError as e:
        sys.exit(f"Cannot reach {base}: {e.reason}\n    docker compose ps")


def verify(base: str, session: str) -> int:
    """Prove the session carries admin before handing it to a browser.

    An admin-only endpoint on purpose. Checking something a plain key could also read
    would pass for a credential that then fails on every screen this exists to drive.
    """
    request = urllib.request.Request(
        f"{base}/api/tokens", headers={"Authorization": f"Bearer {session}"}
    )
    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            return len(json.load(response))
    except urllib.error.HTTPError as e:
        sys.exit(f"The session was issued but {base}/api/tokens answered HTTP {e.code}.")
    except urllib.error.URLError as e:
        sys.exit(f"Cannot reach {base}: {e.reason}\n    docker compose ps")


def serve(session: str, origin: str, port: int, timeout: float, bind: str = "127.0.0.1") -> None:
    nonce = secrets.token_urlsafe(16)
    served = threading.Event()

    class Handler(http.server.BaseHTTPRequestHandler):
        def do_GET(self):  # noqa: N802 - http.server's naming
            # The nonce is compared in constant time and consumed on first use, so a
            # page that guesses the port still gets nothing, and a replay gets nothing.
            if served.is_set() or not secrets.compare_digest(self.path, f"/{nonce}"):
                # The refusal carries the CORS header too. Without it the browser cannot
                # read the status and reports a bare "Failed to fetch", which looks like
                # the server is down rather than like the nonce is wrong.
                self.send_response(404)
                self.send_header("Access-Control-Allow-Origin", origin)
                self.send_header("Content-Length", "0")
                self.end_headers()
                return

            body = session.encode()
            self.send_response(200)
            # Exactly one origin. A wildcard here would let any page the browser has
            # open read the session for as long as this is listening.
            self.send_header("Access-Control-Allow-Origin", origin)
            self.send_header("Content-Type", "text/plain")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            served.set()

        def log_message(self, *_):
            pass  # the default logs the request line, which is the nonce

    class OneServerOnly(http.server.HTTPServer):
        # http.server sets SO_REUSEADDR, and on Windows that lets a SECOND instance bind
        # the same port rather than failing. Two of these running at once means two
        # nonces, one port, and requests landing on whichever socket the OS picks — which
        # presents as a working script that hands over nothing. Fail loudly instead.
        allow_reuse_address = False

    try:
        server = OneServerOnly((bind, port), Handler)
    except OSError as e:
        sys.exit(f"Cannot listen on {bind}:{port}: {e}\nAnother dev-token.py is probably still running.")

    threading.Thread(target=server.serve_forever, daemon=True).start()

    if bind != "127.0.0.1":
        # Said out loud, because the loopback bind is most of what makes this safe.
        print(f"! Listening on {bind}, not loopback. Anything that can reach this port can\n"
              f"  take the session — once, with the nonce, within {timeout:.0f}s.\n", file=sys.stderr)

    print(f"Listening on {bind}:{port} for one request. Run this in the console of a {origin} tab:\n")
    # The host the BROWSER should call, which is not always the one we bound to: a
    # browser in another namespace cannot reach 127.0.0.1 on this machine.
    reachable = "127.0.0.1" if bind in ("127.0.0.1", "localhost") else bind
    print(
        f"await fetch('http://{reachable}:{port}/{nonce}')"
        f".then(r=>r.text()).then(t=>sessionStorage.setItem('dexicon.token',t)),"
        f"location.reload()\n"
    )

    if served.wait(timeout):
        print(f"Handed over {len(session)} bytes. The tab is signed in; this is now closed.")
    else:
        print(f"Nobody asked within {timeout:.0f}s. Nothing was served.", file=sys.stderr)
    server.shutdown()
    sys.exit(0 if served.is_set() else 1)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--base", default="http://127.0.0.1:8477", help="the Dexicon origin to sign in to")
    parser.add_argument("--port", type=int, default=8478, help="loopback port to serve the session on")
    parser.add_argument("--timeout", type=float, default=120, help="seconds to wait before giving up")
    parser.add_argument(
        "--bind", default="127.0.0.1",
        help="address to listen on. Loopback by default, and it should stay that way: widen it "
             "only when the browser is in another network namespace (a container, WSL) and cannot "
             "reach loopback on this host. The nonce and the single-shot still apply.")
    args = parser.parse_args()

    base = args.base.rstrip("/")
    session = sign_in(base, password_from_env())
    keys = verify(base, session)
    print(f"{base} is up, the password signed in, and the session carries admin "
          f"({keys} API {'key' if keys == 1 else 'keys'} issued).")
    serve(session, base, args.port, args.timeout, args.bind)


if __name__ == "__main__":
    main()
