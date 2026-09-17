#!/usr/bin/env python3
"""stdio <-> Streamable HTTP bridge for MCP clients that only speak stdio.

Configure your client to launch:
    python3 /path/to/xiv-mcp-stdio.py [--url http://127.0.0.1:41800/mcp] [--token-file FILE]

Environment (flags win): XIV_MCP_URL, XIV_MCP_TOKEN, XIV_MCP_TOKEN_FILE.

The bridge is transparent: it forwards whatever protocol revision the stdio client speaks.
  * 2025-03-26 .. 2025-11-25 clients: `initialize` creates an HTTP session (Mcp-Session-Id),
    later messages carry it, a GET event stream relays server notifications (resuming with
    Last-Event-ID after drops), and the session is DELETEd when stdin closes.
  * 2026-07-28 clients: each request carries its own _meta; the bridge adds the required
    MCP-Protocol-Version / Mcp-Method / Mcp-Name headers. `notifications/cancelled` closes the
    HTTP response stream of the referenced request, which is how cancellation works over HTTP.
Requests run concurrently; stdout writes are serialized, one JSON-RPC message per line.
Diagnostics go to stderr. No third-party dependencies.
"""

from __future__ import annotations

import argparse
import base64
import http.client
import json
import os
import socket
import sys
import threading
import time
import urllib.parse

def log(*parts):
    print("[xiv-mcp-stdio]", *parts, file=sys.stderr, flush=True)


class Bridge:
    def __init__(self, url, token, verbose=False, timeout=300):
        parsed = urllib.parse.urlparse(url)
        if parsed.scheme not in ("http", "https"):
            raise SystemExit(f"unsupported URL: {url}")
        self.url = url
        self.scheme = parsed.scheme
        self.host = parsed.hostname
        self.port = parsed.port or (443 if parsed.scheme == "https" else 80)
        self.path = (parsed.path or "/") + (f"?{parsed.query}" if parsed.query else "")
        self.token = token
        self.verbose = verbose
        self.timeout = timeout
        self.out_lock = threading.Lock()
        self.state_lock = threading.Lock()
        self.session_id = None
        self.legacy_version = None
        self.inflight = {}  # request id key -> HTTPConnection (modern requests and listen streams)
        self.listener = None
        self.listener_conn = None
        self.workers = set()  # request threads to drain when stdin closes (listen streams excluded)
        self.closing = threading.Event()

    # ---- output ---------------------------------------------------------------------------

    def emit(self, message):
        line = json.dumps(message, ensure_ascii=False, separators=(",", ":"))
        with self.out_lock:
            sys.stdout.write(line + "\n")
            sys.stdout.flush()

    def emit_error(self, request_id, code, message, data=None):
        if request_id is None:
            return
        err = {"code": code, "message": message}
        if data is not None:
            err["data"] = data
        self.emit({"jsonrpc": "2.0", "id": request_id, "error": err})

    # ---- HTTP -----------------------------------------------------------------------------

    def connect(self, timeout=None):
        cls = http.client.HTTPSConnection if self.scheme == "https" else http.client.HTTPConnection
        return cls(self.host, self.port, timeout=timeout or self.timeout)

    def base_headers(self):
        h = {}
        if self.token:
            h["Authorization"] = f"Bearer {self.token}"
        return h

    @staticmethod
    def header_value(value):
        if all(0x20 <= ord(c) <= 0x7E for c in value) and value == value.strip() and not (value.startswith("=?base64?") and value.endswith("?=")):
            return value
        return "=?base64?" + base64.b64encode(value.encode()).decode() + "?="

    def headers_for(self, message):
        h = self.base_headers()
        h["Content-Type"] = "application/json"
        h["Accept"] = "application/json, text/event-stream"
        single = message if isinstance(message, dict) else None
        meta = ((single or {}).get("params") or {}).get("_meta") or {}
        modern_version = meta.get("io.modelcontextprotocol/protocolVersion") if isinstance(meta, dict) else None
        with self.state_lock:
            session_id, legacy_version = self.session_id, self.legacy_version
        is_initialize = single is not None and single.get("method") == "initialize"
        if isinstance(modern_version, str) and not is_initialize:
            h["MCP-Protocol-Version"] = modern_version
            method = single.get("method")
            if isinstance(method, str):
                h["Mcp-Method"] = method
                params = single.get("params") or {}
                name = params.get("name") if method in ("tools/call", "prompts/get") else params.get("uri") if method == "resources/read" else None
                if isinstance(name, str):
                    h["Mcp-Name"] = self.header_value(name)
            return h, False
        if session_id and not is_initialize:
            h["Mcp-Session-Id"] = session_id
            if legacy_version:
                h["MCP-Protocol-Version"] = legacy_version
        return h, True

    def post(self, message):
        """Sends one POST and relays everything that comes back. Runs on a worker thread."""
        request_id = message.get("id") if isinstance(message, dict) and "method" in message else None
        key = json.dumps(request_id) if request_id is not None else None
        headers, legacy = self.headers_for(message)
        conn = self.connect()
        if key is not None and not legacy:
            with self.state_lock:
                self.inflight[key] = conn
        try:
            body = json.dumps(message).encode()
            conn.request("POST", self.path, body=body, headers=headers)
            response = conn.getresponse()
            ctype = (response.getheader("Content-Type") or "").split(";")[0].strip().lower()

            if isinstance(message, dict) and message.get("method") == "initialize" and response.status == 200:
                sid = response.getheader("Mcp-Session-Id")
                with self.state_lock:
                    old = self.session_id
                    self.session_id = sid
                if old and old != sid:
                    threading.Thread(target=self.delete_session, args=(old,), daemon=True).start()

            if response.status == 202:
                response.read()
                return
            if ctype == "text/event-stream":
                self.relay_sse(response, message)
                return

            data = response.read()
            if not data:
                if response.status >= 400:
                    self.emit_error(request_id, -32603, f"HTTP {response.status} {response.reason} from {self.url}")
                return
            try:
                payload = json.loads(data)
            except ValueError:
                self.emit_error(request_id, -32603, f"HTTP {response.status}: {data[:300].decode('utf-8', 'replace')}")
                return
            self.after_response(message, payload)
            if isinstance(payload, dict) and "error" in payload and "id" not in payload and request_id is not None:
                payload["id"] = request_id
            if isinstance(payload, (dict, list)) and (request_id is not None or isinstance(payload, list)):
                self.emit(payload)
            elif response.status >= 400 and request_id is None and self.verbose:
                log(f"notification rejected: HTTP {response.status} {data[:200]!r}")
        except (OSError, http.client.HTTPException) as e:
            if key is not None and not legacy and self.is_cancelled(key):
                return
            self.emit_error(request_id, -32603, f"xiv-mcp bridge: cannot reach {self.url}: {e}")
        finally:
            if key is not None:
                with self.state_lock:
                    self.inflight.pop(key, None)
            conn.close()

    def is_cancelled(self, key):
        with self.state_lock:
            return key not in self.inflight

    def after_response(self, message, payload):
        if isinstance(message, dict) and message.get("method") == "initialize" and isinstance(payload, dict):
            version = (payload.get("result") or {}).get("protocolVersion")
            if version:
                with self.state_lock:
                    self.legacy_version = version

    def relay_sse(self, response, message):
        data_lines = []
        while True:
            try:
                raw = response.readline()
            except (OSError, http.client.HTTPException):
                return
            if not raw:
                return
            line = raw.decode("utf-8").rstrip("\r\n")
            if line == "":
                if data_lines:
                    text = "\n".join(data_lines)
                    data_lines = []
                    if text.strip():
                        try:
                            payload = json.loads(text)
                        except ValueError:
                            continue
                        self.after_response(message, payload)
                        self.emit(payload)
                continue
            if line.startswith("data:"):
                value = line[5:]
                data_lines.append(value[1:] if value.startswith(" ") else value)

    # ---- legacy session helpers -------------------------------------------------------------

    def start_listener(self):
        with self.state_lock:
            if self.listener is not None or not self.session_id:
                return
            self.listener = threading.Thread(target=self.listen_loop, args=(self.session_id,), daemon=True)
            self.listener.start()

    def listen_loop(self, session_id):
        last_event_id = None
        backoff = 0.5
        while not self.closing.is_set():
            with self.state_lock:
                if self.session_id != session_id:
                    break
                version = self.legacy_version
            conn = self.connect(timeout=None)
            with self.state_lock:
                self.listener_conn = conn
            try:
                headers = self.base_headers()
                headers.update({"Accept": "text/event-stream", "Mcp-Session-Id": session_id})
                if version:
                    headers["MCP-Protocol-Version"] = version
                if last_event_id:
                    headers["Last-Event-ID"] = last_event_id
                conn.request("GET", self.path, headers=headers)
                response = conn.getresponse()
                if response.status in (404, 405):
                    if self.verbose:
                        log(f"GET stream unavailable (HTTP {response.status}); server notifications will not be relayed")
                    break
                if response.status != 200:
                    raise OSError(f"HTTP {response.status}")
                backoff = 0.5
                data_lines, event_id = [], None
                while not self.closing.is_set():
                    raw = response.readline()
                    if not raw:
                        break
                    line = raw.decode("utf-8").rstrip("\r\n")
                    if line == "":
                        if event_id:
                            last_event_id = event_id
                        if data_lines and "\n".join(data_lines).strip():
                            try:
                                self.emit(json.loads("\n".join(data_lines)))
                            except ValueError:
                                pass
                        data_lines, event_id = [], None
                    elif line.startswith("id:"):
                        event_id = line[3:].strip()
                    elif line.startswith("data:"):
                        value = line[5:]
                        data_lines.append(value[1:] if value.startswith(" ") else value)
            except (OSError, http.client.HTTPException) as e:
                if self.verbose:
                    log(f"GET stream dropped: {e}; reconnecting in {backoff:.1f}s")
            finally:
                with self.state_lock:
                    self.listener_conn = None
                conn.close()
            if self.closing.wait(backoff):
                break
            backoff = min(backoff * 2, 15)
        with self.state_lock:
            if self.listener is threading.current_thread():
                self.listener = None

    def delete_session(self, session_id):
        conn = self.connect(timeout=5)
        try:
            headers = self.base_headers()
            headers["Mcp-Session-Id"] = session_id
            conn.request("DELETE", self.path, headers=headers)
            conn.getresponse().read()
        except (OSError, http.client.HTTPException):
            pass
        finally:
            conn.close()

    # ---- main loop ------------------------------------------------------------------------

    def cancel_modern(self, params):
        key = json.dumps((params or {}).get("requestId"))
        with self.state_lock:
            conn = self.inflight.pop(key, None)
        if conn is None:
            return False
        self.abort(conn)
        return True

    @staticmethod
    def abort(conn):
        """Unblocks a reader on another thread. Never call conn.close() cross-thread: it deadlocks on the buffer lock."""
        try:
            if conn is not None and conn.sock is not None:
                conn.sock.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass

    def handle_line(self, line):
        try:
            message = json.loads(line)
        except ValueError as e:
            self.emit({"jsonrpc": "2.0", "error": {"code": -32700, "message": f"Parse error: {e}"}})
            return

        if isinstance(message, dict) and message.get("method") == "notifications/cancelled" and "id" not in message:
            if self.cancel_modern(message.get("params")):
                return
        worker = threading.Thread(target=self.post, args=(message,), daemon=True)
        if not (isinstance(message, dict) and message.get("method") == "subscriptions/listen"):
            with self.state_lock:
                self.workers = {w for w in self.workers if w.is_alive()}
                self.workers.add(worker)
        worker.start()
        if isinstance(message, dict) and message.get("method") == "notifications/initialized":
            # Give the notification a moment to reach the server before opening the stream.
            threading.Timer(0.2, self.start_listener).start()

    def run(self):
        stdin = sys.stdin.buffer
        while True:
            raw = stdin.readline()
            if not raw:
                break
            line = raw.decode("utf-8").strip()
            if line:
                self.handle_line(line)
        # stdin closed: let ordinary requests finish (bounded), then tear down streams and the session.
        deadline = time.monotonic() + 30
        with self.state_lock:
            workers = list(self.workers)
        for worker in workers:
            worker.join(max(0.0, deadline - time.monotonic()))
        self.closing.set()
        with self.state_lock:
            session_id = self.session_id
            conns = list(self.inflight.values())
            self.inflight.clear()
            if self.listener_conn is not None:
                conns.append(self.listener_conn)
        for conn in conns:
            self.abort(conn)
        if session_id:
            self.delete_session(session_id)
        # Let in-flight writers finish their last lines.
        time.sleep(0.05)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--url", default=os.environ.get("XIV_MCP_URL", "http://127.0.0.1:41800/mcp"))
    parser.add_argument("--token-file", default=os.environ.get("XIV_MCP_TOKEN_FILE"))
    parser.add_argument("--timeout", type=float, default=300, help="per-request HTTP timeout in seconds")
    parser.add_argument("-v", "--verbose", action="store_true")
    args = parser.parse_args()

    token = os.environ.get("XIV_MCP_TOKEN")
    if args.token_file:
        try:
            with open(os.path.expanduser(args.token_file), encoding="utf-8") as f:
                token = f.read().strip()
        except OSError as e:
            log(f"cannot read token file: {e}")
            return 2

    bridge = Bridge(args.url, token, verbose=args.verbose, timeout=args.timeout)
    if args.verbose:
        log(f"bridging stdio to {args.url} ({'with' if token else 'without'} token)")
    bridge.run()
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)
