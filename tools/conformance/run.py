#!/usr/bin/env python3
"""Dependency-free MCP conformance checks for any Streamable HTTP endpoint.

Usage:
  python3 tools/conformance/run.py --url http://127.0.0.1:41800/mcp --token-file FILE [--live]

Protocol checks cover the stateless 2026-07-28 revision (server/discover, per-request _meta,
header validation, subscriptions/listen) and the session-based revisions 2025-11-25,
2025-06-18 and 2025-03-26 (initialize, Mcp-Session-Id, GET stream, DELETE, batching).

--live additionally calls every tool whose annotations say readOnlyHint=true (and not
destructiveHint) with schema-valid minimal arguments, validates each CallToolResult and its
structuredContent against the tool's outputSchema, and reads every listed static resource.
Nothing that changes client state is invoked.

Exit status: 0 when no check failed, 1 otherwise, 2 on usage errors.
"""

from __future__ import annotations

import argparse
import base64
import datetime as _dt
import http.client
import json
import os
import re
import socket
import sys
import time
import urllib.parse

MODERN = "2026-07-28"
LEGACY = ["2025-11-25", "2025-06-18", "2025-03-26"]
CLIENT_INFO = {"name": "xiv-mcp-conformance", "version": "1.0.0"}


# --------------------------------------------------------------------------------------------
# HTTP / SSE plumbing
# --------------------------------------------------------------------------------------------


class Response:
    def __init__(self, status, headers, conn, raw):
        self.status = status
        self.headers = {k.lower(): v for k, v in headers}
        self._conn = conn
        self._raw = raw
        self._body = None

    @property
    def content_type(self):
        return (self.headers.get("content-type") or "").split(";")[0].strip().lower()

    def body(self):
        if self._body is None:
            self._body = self._raw.read()
            self.close()
        return self._body

    def json(self):
        data = self.body()
        return json.loads(data) if data else None

    def events(self, timeout=None):
        """Yields (event_id, data) for each SSE event; comments are skipped."""
        if timeout is not None:
            self._conn.sock.settimeout(timeout)
        event_id, data = None, None
        while True:
            try:
                line = self._raw.readline()
            except (socket.timeout, TimeoutError):
                raise TimeoutError("no SSE data within timeout")
            if not line:
                return
            line = line.decode("utf-8").rstrip("\r\n")
            if line == "":
                if data is not None:
                    yield event_id, data
                event_id, data = None, None
                continue
            if line.startswith(":"):
                continue
            field, _, value = line.partition(":")
            if value.startswith(" "):
                value = value[1:]
            if field == "id":
                event_id = value
            elif field == "data":
                data = value if data is None else data + "\n" + value

    def messages(self, timeout=None):
        for _, data in self.events(timeout):
            if data.strip():
                yield json.loads(data)

    def rpc(self, timeout=30):
        """Returns (response, notifications) for a JSON or SSE body."""
        if self.content_type == "text/event-stream":
            notes = []
            for msg in self.messages(timeout):
                if "id" in msg and ("result" in msg or "error" in msg):
                    self.close()
                    return msg, notes
                notes.append(msg)
            raise AssertionError("SSE stream ended without a JSON-RPC response")
        return self.json(), []

    def close(self):
        try:
            self._conn.close()
        except Exception:
            pass


class Client:
    def __init__(self, url, token, timeout=30):
        parsed = urllib.parse.urlparse(url)
        if parsed.scheme not in ("http", "https"):
            raise SystemExit(f"unsupported URL scheme: {url}")
        self.scheme = parsed.scheme
        self.host = parsed.hostname
        self.port = parsed.port or (443 if parsed.scheme == "https" else 80)
        self.path = parsed.path or "/"
        self.token = token
        self.timeout = timeout
        self._id = 0

    def next_id(self):
        self._id += 1
        return self._id

    def send(self, method="POST", body=None, headers=None, auth=True, raw_body=None, path=None, timeout=None):
        cls = http.client.HTTPSConnection if self.scheme == "https" else http.client.HTTPConnection
        conn = cls(self.host, self.port, timeout=timeout or self.timeout)
        h = {}
        if auth and self.token:
            h["Authorization"] = f"Bearer {self.token}"
        if body is not None or raw_body is not None:
            h["Content-Type"] = "application/json"
        if method == "POST":
            h["Accept"] = "application/json, text/event-stream"
        h.update(headers or {})
        payload = raw_body if raw_body is not None else (json.dumps(body).encode() if body is not None else None)
        conn.request(method, path or self.path, body=payload, headers=h)
        r = conn.getresponse()
        return Response(r.status, r.getheaders(), conn, r)

    # --- modern -------------------------------------------------------------------------------

    @staticmethod
    def meta(extra=None):
        m = {
            "io.modelcontextprotocol/protocolVersion": MODERN,
            "io.modelcontextprotocol/clientInfo": CLIENT_INFO,
            "io.modelcontextprotocol/clientCapabilities": {},
        }
        m.update(extra or {})
        return m

    @staticmethod
    def header_value(value):
        if all(0x20 <= ord(c) <= 0x7E for c in value) and value == value.strip() and not (value.startswith("=?base64?") and value.endswith("?=")):
            return value
        return "=?base64?" + base64.b64encode(value.encode()).decode() + "?="

    def modern(self, method, params=None, extra_meta=None, headers=None, rid=None, timeout=None):
        p = dict(params or {})
        p["_meta"] = self.meta(extra_meta)
        rid = self.next_id() if rid is None else rid
        body = {"jsonrpc": "2.0", "id": rid, "method": method, "params": p}
        h = {"MCP-Protocol-Version": MODERN, "Mcp-Method": method}
        name = p.get("name") if method in ("tools/call", "prompts/get") else p.get("uri") if method == "resources/read" else None
        if isinstance(name, str):
            h["Mcp-Name"] = self.header_value(name)
        h.update(headers or {})
        return self.send(body=body, headers=h, timeout=timeout)

    # --- legacy -------------------------------------------------------------------------------

    def initialize(self, version):
        body = {
            "jsonrpc": "2.0",
            "id": self.next_id(),
            "method": "initialize",
            "params": {"protocolVersion": version, "capabilities": {}, "clientInfo": CLIENT_INFO},
        }
        r = self.send(body=body)
        return r

    def legacy(self, session, version, method, params=None, notification=False, timeout=None):
        body = {"jsonrpc": "2.0", "method": method}
        if not notification:
            body["id"] = self.next_id()
        if params is not None:
            body["params"] = params
        return self.send(body=body, headers={"Mcp-Session-Id": session, "MCP-Protocol-Version": version}, timeout=timeout)


# --------------------------------------------------------------------------------------------
# Minimal JSON Schema (2020-12 subset) validation and example generation
# --------------------------------------------------------------------------------------------

_TYPE_CHECKS = {
    "object": lambda v: isinstance(v, dict),
    "array": lambda v: isinstance(v, list),
    "string": lambda v: isinstance(v, str),
    "boolean": lambda v: isinstance(v, bool),
    "null": lambda v: v is None,
    "integer": lambda v: isinstance(v, int) and not isinstance(v, bool) or (isinstance(v, float) and v.is_integer()),
    "number": lambda v: isinstance(v, (int, float)) and not isinstance(v, bool),
}


def _resolve(schema, root):
    seen = 0
    while isinstance(schema, dict) and "$ref" in schema and seen < 32:
        ref = schema["$ref"]
        if not ref.startswith("#"):
            raise ValueError(f"non-local $ref not supported: {ref}")
        node = root
        for part in [p for p in ref[1:].split("/") if p]:
            node = node[part.replace("~1", "/").replace("~0", "~")]
        schema = node
        seen += 1
    return schema


def validate(value, schema, root=None, path="$", errors=None, depth=0):
    errors = [] if errors is None else errors
    root = schema if root is None else root
    if depth > 64 or schema is True or schema is None or schema == {}:
        return errors
    if schema is False:
        errors.append(f"{path}: schema is false")
        return errors
    schema = _resolve(schema, root)
    t = schema.get("type")
    if t is not None:
        types = t if isinstance(t, list) else [t]
        if not any(_TYPE_CHECKS.get(x, lambda v: True)(value) for x in types):
            errors.append(f"{path}: expected {t}, got {type(value).__name__}")
            return errors
    if "enum" in schema and value not in schema["enum"]:
        errors.append(f"{path}: {value!r} not in enum {schema['enum']}")
    if "const" in schema and value != schema["const"]:
        errors.append(f"{path}: expected const {schema['const']!r}")
    if isinstance(value, (int, float)) and not isinstance(value, bool):
        if "minimum" in schema and value < schema["minimum"]:
            errors.append(f"{path}: {value} < minimum {schema['minimum']}")
        if "maximum" in schema and value > schema["maximum"]:
            errors.append(f"{path}: {value} > maximum {schema['maximum']}")
        if "exclusiveMinimum" in schema and value <= schema["exclusiveMinimum"]:
            errors.append(f"{path}: {value} <= exclusiveMinimum")
        if "exclusiveMaximum" in schema and value >= schema["exclusiveMaximum"]:
            errors.append(f"{path}: {value} >= exclusiveMaximum")
    if isinstance(value, str):
        if "minLength" in schema and len(value) < schema["minLength"]:
            errors.append(f"{path}: shorter than minLength")
        if "maxLength" in schema and len(value) > schema["maxLength"]:
            errors.append(f"{path}: longer than maxLength")
        if "pattern" in schema and not re.search(schema["pattern"], value):
            errors.append(f"{path}: does not match pattern")
    if isinstance(value, dict):
        props = schema.get("properties", {})
        for req in schema.get("required", []):
            if req not in value:
                errors.append(f"{path}: missing required property '{req}'")
        for k, v in value.items():
            if k in props:
                validate(v, props[k], root, f"{path}.{k}", errors, depth + 1)
            elif "additionalProperties" in schema:
                ap = schema["additionalProperties"]
                if ap is False:
                    errors.append(f"{path}: unexpected property '{k}'")
                elif isinstance(ap, dict):
                    validate(v, ap, root, f"{path}.{k}", errors, depth + 1)
    if isinstance(value, list):
        if "items" in schema and isinstance(schema["items"], dict):
            for i, item in enumerate(value):
                validate(item, schema["items"], root, f"{path}[{i}]", errors, depth + 1)
        if "minItems" in schema and len(value) < schema["minItems"]:
            errors.append(f"{path}: fewer than minItems")
        if "maxItems" in schema and len(value) > schema["maxItems"]:
            errors.append(f"{path}: more than maxItems")
    for sub in schema.get("allOf", []):
        validate(value, sub, root, path, errors, depth + 1)
    if "anyOf" in schema and not any(not validate(value, s, root, path, [], depth + 1) for s in schema["anyOf"]):
        errors.append(f"{path}: matches none of anyOf")
    if "oneOf" in schema and sum(1 for s in schema["oneOf"] if not validate(value, s, root, path, [], depth + 1)) != 1:
        errors.append(f"{path}: must match exactly one of oneOf")
    return errors


def minimal_value(schema, root=None, depth=0):
    root = schema if root is None else root
    if not isinstance(schema, dict) or depth > 16:
        return None
    schema = _resolve(schema, root)
    if "default" in schema:
        return schema["default"]
    if "const" in schema:
        return schema["const"]
    if schema.get("enum"):
        return schema["enum"][0]
    for key in ("anyOf", "oneOf"):
        if schema.get(key):
            return minimal_value(schema[key][0], root, depth + 1)
    t = schema.get("type")
    if isinstance(t, list):
        t = next((x for x in t if x != "null"), "null")
    if t == "object" or (t is None and "properties" in schema):
        props = schema.get("properties", {})
        return {k: minimal_value(props.get(k, {}), root, depth + 1) for k in schema.get("required", [])}
    if t == "array":
        n = schema.get("minItems", 0)
        return [minimal_value(schema.get("items", {}), root, depth + 1) for _ in range(n)]
    if t == "string":
        fmt = schema.get("format")
        if fmt == "date-time":
            return _dt.datetime.now(_dt.timezone.utc).isoformat()
        if fmt == "uuid":
            return "00000000-0000-0000-0000-000000000000"
        return "a" * schema.get("minLength", 0)
    if t in ("integer", "number"):
        if "minimum" in schema:
            return schema["minimum"]
        if "exclusiveMinimum" in schema:
            return schema["exclusiveMinimum"] + 1
        if "maximum" in schema and schema["maximum"] < 1:
            return schema["maximum"]
        return 1
    if t == "boolean":
        return False
    return None


# --------------------------------------------------------------------------------------------
# Check runner
# --------------------------------------------------------------------------------------------


class Skip(Exception):
    pass


class Runner:
    def __init__(self, verbose=False):
        self.results = []
        self.verbose = verbose

    def check(self, name, fn):
        started = time.monotonic()
        try:
            detail = fn()
            status = "PASS"
        except Skip as e:
            status, detail = "SKIP", str(e)
        except AssertionError as e:
            status, detail = "FAIL", str(e) or "assertion failed"
        except Exception as e:  # network errors, JSON errors, timeouts
            status, detail = "FAIL", f"{type(e).__name__}: {e}"
        ms = (time.monotonic() - started) * 1000
        self.results.append({"name": name, "status": status, "detail": detail, "ms": round(ms, 1)})
        line = f"{status:4}  {name}"
        if detail and (status != "PASS" or self.verbose):
            line += f"  -- {detail}"
        print(line, flush=True)
        return status == "PASS"

    @property
    def failed(self):
        return [r for r in self.results if r["status"] == "FAIL"]


def expect(cond, message):
    if not cond:
        raise AssertionError(message)


def rpc_error_code(msg):
    return (msg or {}).get("error", {}).get("code")


def check_call_tool_result(result, legacy_version=None):
    expect(isinstance(result, dict), "result is not an object")
    content = result.get("content")
    expect(isinstance(content, list), "content must be an array")
    for i, block in enumerate(content):
        expect(isinstance(block, dict) and isinstance(block.get("type"), str), f"content[{i}] lacks a type")
        kind = block["type"]
        if kind == "text":
            expect(isinstance(block.get("text"), str), f"content[{i}].text must be a string")
        elif kind in ("image", "audio"):
            expect(isinstance(block.get("data"), str) and isinstance(block.get("mimeType"), str), f"content[{i}] needs data+mimeType")
            base64.b64decode(block["data"], validate=True)
        elif kind == "resource_link":
            expect(isinstance(block.get("uri"), str) and isinstance(block.get("name"), str), f"content[{i}] needs uri+name")
        elif kind == "resource":
            expect(isinstance(block.get("resource"), dict), f"content[{i}].resource must be an object")
    if "isError" in result:
        expect(isinstance(result["isError"], bool), "isError must be a boolean")
    if legacy_version is None:
        expect(result.get("resultType") == "complete", "2026-07-28 results need resultType=complete")


# --------------------------------------------------------------------------------------------
# Checks
# --------------------------------------------------------------------------------------------


def transport_checks(run, client):
    def unauthenticated():
        if not client.token:
            raise Skip("no token supplied")
        r = client.modern.__func__(Client(f"{client.scheme}://{client.host}:{client.port}{client.path}", None), "server/discover")
        expect(r.status == 401, f"expected 401 without a token, got {r.status}")
        expect("bearer" in r.headers.get("www-authenticate", "").lower(), "401 must carry WWW-Authenticate: Bearer")
        r.close()
        bad = Client(f"{client.scheme}://{client.host}:{client.port}{client.path}", "definitely-wrong-token")
        r = bad.modern("server/discover")
        expect(r.status == 401, f"expected 401 with a wrong token, got {r.status}")
        r.close()

    def bad_origin():
        r = client.modern("server/discover", headers={"Origin": "http://evil.invalid"})
        expect(r.status == 403, f"expected 403 for a foreign Origin, got {r.status}")
        r.close()
        ok = client.modern("server/discover", headers={"Origin": "http://localhost:6274"})
        expect(ok.status == 200, f"loopback Origin should be accepted, got {ok.status}")
        ok.close()

    def parse_error():
        r = client.send(raw_body=b"{nope", headers={"MCP-Protocol-Version": MODERN, "Mcp-Method": "tools/list"})
        expect(r.status == 400, f"expected 400 for invalid JSON, got {r.status}")
        body = r.json()
        expect(rpc_error_code(body) == -32700, f"expected -32700, got {body}")

    def content_type():
        r = client.send(raw_body=b"{}", headers={"Content-Type": "text/plain"})
        expect(r.status in (400, 415), f"expected 415 for text/plain, got {r.status}")
        r.close()

    def get_without_session():
        r = client.send(method="GET", headers={"Accept": "text/event-stream"})
        expect(r.status in (400, 405), f"expected 400/405, got {r.status}")
        r.close()

    run.check("transport: authentication (401 + WWW-Authenticate)", unauthenticated)
    run.check("transport: Origin validation (403)", bad_origin)
    run.check("transport: parse error (400/-32700)", parse_error)
    run.check("transport: non-JSON content type rejected", content_type)
    run.check("transport: GET without session (400/405)", get_without_session)


def modern_checks(run, client, state):
    def discover():
        r = client.modern("server/discover")
        if r.status in (400, 404) and rpc_error_code(r.json()) not in (-32602, -32020, -32022, -32021):
            state["modern"] = False
            raise Skip(f"server does not speak {MODERN} (HTTP {r.status})")
        expect(r.status == 200, f"HTTP {r.status}")
        msg, _ = r.rpc()
        result = msg.get("result")
        expect(result is not None, f"error: {msg.get('error')}")
        expect(result.get("resultType") == "complete", "resultType must be complete")
        expect(MODERN in result.get("supportedVersions", []), "supportedVersions lacks 2026-07-28")
        expect(isinstance(result.get("capabilities"), dict), "capabilities missing")
        expect(isinstance(result.get("ttlMs"), int) and result["ttlMs"] >= 0, "ttlMs must be an integer >= 0")
        expect(result.get("cacheScope") in ("public", "private"), "cacheScope must be public|private")
        info = (result.get("_meta") or {}).get("io.modelcontextprotocol/serverInfo")
        expect(isinstance(info, dict) and "name" in info, "serverInfo missing from _meta")
        state["modern"] = True
        state["capabilities"] = result["capabilities"]
        state["versions"] = result["supportedVersions"]
        return f"{info.get('name')} {info.get('version')}; versions {result['supportedVersions']}"

    def need_modern():
        if not state.get("modern"):
            raise Skip("modern protocol unavailable")

    def list_all(method, key):
        items, cursor, pages = [], None, 0
        while True:
            r = client.modern(method, {"cursor": cursor} if cursor is not None else None)
            expect(r.status == 200, f"HTTP {r.status}")
            msg, _ = r.rpc()
            result = msg.get("result")
            expect(result is not None, f"error: {msg.get('error')}")
            expect(result.get("resultType") == "complete", "resultType must be complete")
            expect(isinstance(result.get("ttlMs"), int) and result["ttlMs"] >= 0, "ttlMs must be an integer >= 0")
            expect(result.get("cacheScope") in ("public", "private"), "cacheScope must be public|private")
            expect(isinstance(result.get(key), list), f"{key} must be an array")
            items.extend(result[key])
            cursor = result.get("nextCursor")
            pages += 1
            if cursor is None or pages >= 100:
                return items, pages

    def tools_list():
        need_modern()
        tools, pages = list_all("tools/list", "tools")
        names = [t.get("name") for t in tools]
        expect(len(names) == len(set(names)), "tool names are not unique")
        for t in tools:
            expect(isinstance(t.get("name"), str) and re.fullmatch(r"[A-Za-z0-9_.\-]{1,128}", t["name"]), f"bad tool name {t.get('name')!r}")
            schema = t.get("inputSchema")
            expect(isinstance(schema, dict) and schema.get("type") == "object", f"{t['name']}: inputSchema.type must be object")
            if "outputSchema" in t:
                expect(isinstance(t["outputSchema"], dict), f"{t['name']}: outputSchema must be an object")
            ann = t.get("annotations") or {}
            for hint in ("readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint"):
                if hint in ann:
                    expect(isinstance(ann[hint], bool), f"{t['name']}: {hint} must be boolean")
        again, _ = list_all("tools/list", "tools")
        expect([t["name"] for t in again] == names, "tools/list order is not deterministic")
        state["tools"] = tools
        return f"{len(tools)} tools in {pages} page(s)"

    def other_lists():
        need_modern()
        caps = state.get("capabilities", {})
        out = []
        if "resources" in caps:
            resources, _ = list_all("resources/list", "resources")
            for res in resources:
                expect(isinstance(res.get("uri"), str) and isinstance(res.get("name"), str), f"resource lacks uri/name: {res}")
            templates, _ = list_all("resources/templates/list", "resourceTemplates")
            for tpl in templates:
                expect(isinstance(tpl.get("uriTemplate"), str) and isinstance(tpl.get("name"), str), f"template lacks uriTemplate/name: {tpl}")
            state["resources"] = resources
            out.append(f"{len(resources)} resources, {len(templates)} templates")
        if "prompts" in caps:
            prompts, _ = list_all("prompts/list", "prompts")
            for p in prompts:
                expect(isinstance(p.get("name"), str), f"prompt lacks name: {p}")
            state["prompts"] = prompts
            out.append(f"{len(prompts)} prompts")
        if not out:
            raise Skip("no resources/prompts capability")
        return ", ".join(out)

    def missing_meta():
        need_modern()
        body = {"jsonrpc": "2.0", "id": client.next_id(), "method": "tools/list", "params": {}}
        r = client.send(body=body, headers={"MCP-Protocol-Version": MODERN, "Mcp-Method": "tools/list"})
        expect(r.status == 400, f"expected 400, got {r.status}")
        expect(rpc_error_code(r.json()) == -32602, "expected -32602")

    def unsupported_version():
        need_modern()
        meta = client.meta({"io.modelcontextprotocol/protocolVersion": "1999-01-01"})
        body = {"jsonrpc": "2.0", "id": client.next_id(), "method": "tools/list", "params": {"_meta": meta}}
        r = client.send(body=body, headers={"MCP-Protocol-Version": "1999-01-01", "Mcp-Method": "tools/list"})
        expect(r.status == 400, f"expected 400, got {r.status}")
        msg = r.json()
        expect(rpc_error_code(msg) == -32022, f"expected -32022, got {msg}")
        data = msg["error"].get("data") or {}
        expect(isinstance(data.get("supported"), list) and data.get("requested") == "1999-01-01", "data.supported/requested missing")

    def header_mismatch():
        need_modern()
        r = client.modern("tools/list", headers={"Mcp-Method": "prompts/list"})
        expect(r.status == 400 and rpc_error_code(r.json()) == -32020, f"Mcp-Method mismatch: HTTP {r.status}")
        body = {"jsonrpc": "2.0", "id": client.next_id(), "method": "tools/list", "params": {"_meta": client.meta()}}
        r = client.send(body=body, headers={"Mcp-Method": "tools/list"})
        expect(r.status == 400 and rpc_error_code(r.json()) == -32020, f"missing MCP-Protocol-Version: HTTP {r.status}")
        r = client.modern("tools/call", {"name": "__conformance_probe__", "arguments": {}}, headers={"Mcp-Name": "something_else"})
        expect(r.status == 400 and rpc_error_code(r.json()) == -32020, f"Mcp-Name mismatch: HTTP {r.status}")

    def unknown_method():
        need_modern()
        r = client.modern("conformance/does-not-exist")
        expect(r.status == 404, f"expected 404, got {r.status}")
        expect(rpc_error_code(r.json()) == -32601, "expected -32601")
        r = client.modern("ping")
        expect(r.status == 404, f"ping was removed in {MODERN}; expected 404, got {r.status}")
        r.close()

    def unknown_resource():
        need_modern()
        if "resources" not in state.get("capabilities", {}):
            raise Skip("no resources capability")
        r = client.modern("resources/read", {"uri": "conformance://definitely/missing"})
        msg, _ = r.rpc()
        code = rpc_error_code(msg)
        expect(code in (-32602, -32002), f"expected -32602, got {msg}")
        return "uses legacy -32002" if code == -32002 else None

    def unknown_tool():
        need_modern()
        r = client.modern("tools/call", {"name": "__conformance_probe__", "arguments": {}})
        msg, _ = r.rpc()
        if "error" in msg:
            expect(msg["error"]["code"] == -32602, f"unknown tool should be -32602, got {msg['error']}")
        else:
            expect(msg["result"].get("isError") is True, "unknown tool must fail")

    def invalid_arguments_are_tool_errors():
        need_modern()
        tools = [t for t in state.get("tools", []) if (t.get("annotations") or {}).get("readOnlyHint") is True
                 and (t.get("inputSchema") or {}).get("additionalProperties") is False]
        if not tools:
            raise Skip("no read-only tool with additionalProperties:false")
        tool = tools[0]
        args = minimal_value(tool["inputSchema"]) or {}
        args["__conformance_unknown_argument__"] = 1
        r = client.modern("tools/call", {"name": tool["name"], "arguments": args})
        msg, _ = r.rpc()
        expect("result" in msg, f"input validation errors should be tool results, got {msg.get('error')}")
        expect(msg["result"].get("isError") is True, "expected isError=true")
        return f"via {tool['name']}"

    def listen():
        need_modern()
        rid = client.next_id()
        r = client.modern("subscriptions/listen", {"notifications": {"toolsListChanged": True, "resourcesListChanged": True}}, rid=rid, timeout=10)
        expect(r.status == 200, f"HTTP {r.status}")
        expect(r.content_type == "text/event-stream", f"expected text/event-stream, got {r.content_type}")
        try:
            first = next(r.messages(timeout=10))
        finally:
            r.close()
        expect(first.get("method") == "notifications/subscriptions/acknowledged", f"first message must be the acknowledgement, got {first}")
        sub = first.get("params", {}).get("_meta", {}).get("io.modelcontextprotocol/subscriptionId")
        expect(sub == rid, f"subscriptionId {sub!r} != request id {rid!r}")

    run.check(f"{MODERN}: server/discover", discover)
    run.check(f"{MODERN}: tools/list (shape, pagination, determinism)", tools_list)
    run.check(f"{MODERN}: resources/prompts lists", other_lists)
    run.check(f"{MODERN}: missing _meta -> 400/-32602", missing_meta)
    run.check(f"{MODERN}: unsupported version -> 400/-32022", unsupported_version)
    run.check(f"{MODERN}: header mismatch -> 400/-32020", header_mismatch)
    run.check(f"{MODERN}: unknown method -> 404/-32601", unknown_method)
    run.check(f"{MODERN}: unknown resource -> -32602", unknown_resource)
    run.check(f"{MODERN}: unknown tool", unknown_tool)
    run.check(f"{MODERN}: invalid arguments -> isError result", invalid_arguments_are_tool_errors)
    run.check(f"{MODERN}: subscriptions/listen acknowledgement", listen)


def legacy_checks(run, client, version, state):
    session = {}

    def initialize():
        r = client.initialize(version)
        expect(r.status == 200, f"HTTP {r.status}")
        sid = r.headers.get("mcp-session-id")
        msg, _ = r.rpc()
        result = msg.get("result")
        expect(result is not None, f"error: {msg.get('error')}")
        expect(result.get("protocolVersion") == version, f"server negotiated {result.get('protocolVersion')}")
        expect(isinstance(result.get("capabilities"), dict), "capabilities missing")
        expect(isinstance(result.get("serverInfo"), dict) and "name" in result["serverInfo"], "serverInfo missing")
        expect(sid is not None and all(0x21 <= ord(c) <= 0x7E for c in sid), "Mcp-Session-Id missing or not visible ASCII")
        session["id"] = sid
        session["caps"] = result["capabilities"]
        r = client.legacy(sid, version, "notifications/initialized", notification=True)
        expect(r.status == 202, f"notifications/initialized -> HTTP {r.status}")
        r.close()

    def need():
        if "id" not in session:
            raise Skip("initialize failed")
        return session["id"]

    def ping():
        sid = need()
        msg, _ = client.legacy(sid, version, "ping").rpc()
        expect(msg.get("result") == {}, f"ping result {msg}")

    def tools():
        sid = need()
        names, cursor, pages = [], None, 0
        while True:
            msg, _ = client.legacy(sid, version, "tools/list", {"cursor": cursor} if cursor else {}).rpc()
            result = msg.get("result")
            expect(result is not None, f"error: {msg.get('error')}")
            expect("resultType" not in result, "legacy results should not carry resultType")
            for t in result.get("tools", []):
                names.append(t["name"])
                expect((t.get("inputSchema") or {}).get("type") == "object", f"{t['name']}: inputSchema.type must be object")
                if version == "2025-03-26":
                    expect("outputSchema" not in t, f"{t['name']}: outputSchema does not exist in 2025-03-26")
            cursor = result.get("nextCursor")
            pages += 1
            if not cursor or pages > 100:
                break
        return f"{len(names)} tools"

    def session_rules():
        sid = need()
        body = {"jsonrpc": "2.0", "id": client.next_id(), "method": "ping"}
        r = client.send(body=body, headers={"MCP-Protocol-Version": version})
        expect(r.status == 400, f"missing session -> expected 400, got {r.status}")
        r.close()
        r = client.send(body=body, headers={"Mcp-Session-Id": "conformance-unknown-session", "MCP-Protocol-Version": version})
        expect(r.status == 404, f"unknown session -> expected 404, got {r.status}")
        r.close()
        r = client.send(body=body, headers={"Mcp-Session-Id": sid, "MCP-Protocol-Version": "1999-01-01"})
        expect(r.status == 400, f"bad MCP-Protocol-Version -> expected 400, got {r.status}")
        r.close()

    def get_stream():
        sid = need()
        r = client.send(method="GET", headers={"Mcp-Session-Id": sid, "MCP-Protocol-Version": version, "Accept": "text/event-stream"}, timeout=10)
        try:
            if r.status == 405:
                raise Skip("server does not offer a GET stream")
            expect(r.status == 200, f"HTTP {r.status}")
            expect(r.content_type == "text/event-stream", f"content-type {r.content_type}")
        finally:
            r.close()

    def batch():
        sid = need()
        body = [{"jsonrpc": "2.0", "id": client.next_id(), "method": "ping"}]
        r = client.send(body=body, headers={"Mcp-Session-Id": sid, "MCP-Protocol-Version": version})
        if version == "2025-03-26":
            expect(r.status == 200, f"batch -> HTTP {r.status}")
            data = r.json()
            expect(isinstance(data, list) and len(data) == 1 and data[0].get("result") == {}, f"batch response {data}")
        else:
            expect(r.status == 400, f"batches were removed after 2025-03-26; expected 400, got {r.status}")
            r.close()

    def delete():
        sid = need()
        r = client.send(method="DELETE", headers={"Mcp-Session-Id": sid, "MCP-Protocol-Version": version})
        if r.status == 405:
            r.close()
            raise Skip("server does not allow DELETE")
        expect(r.status in (200, 202, 204), f"DELETE -> HTTP {r.status}")
        r.close()
        r = client.legacy(sid, version, "ping")
        expect(r.status == 404, f"request after DELETE -> expected 404, got {r.status}")
        r.close()

    run.check(f"{version}: initialize + initialized", initialize)
    run.check(f"{version}: ping", ping)
    run.check(f"{version}: tools/list", tools)
    run.check(f"{version}: session header rules (400/404)", session_rules)
    run.check(f"{version}: GET event stream", get_stream)
    run.check(f"{version}: batching rules", batch)
    run.check(f"{version}: DELETE session", delete)


def live_checks(run, client, state):
    tools = state.get("tools")
    call = None
    if tools is not None and state.get("modern"):
        def call(name, args):
            msg, _ = client.modern("tools/call", {"name": name, "arguments": args}, timeout=60).rpc(timeout=60)
            return msg, None

        def read(uri):
            msg, _ = client.modern("resources/read", {"uri": uri}, timeout=60).rpc(timeout=60)
            return msg
    else:
        version = next((v for v in LEGACY), None)
        r = client.initialize(version)
        sid = r.headers.get("mcp-session-id")
        msg, _ = r.rpc()
        if sid is None or "result" not in msg:
            run.check("live: setup", lambda: (_ for _ in ()).throw(AssertionError("could not initialize a session")))
            return
        version = msg["result"]["protocolVersion"]
        client.legacy(sid, version, "notifications/initialized", notification=True).close()
        tools, cursor = [], None
        while True:
            m, _ = client.legacy(sid, version, "tools/list", {"cursor": cursor} if cursor else {}).rpc()
            tools.extend(m["result"]["tools"])
            cursor = m["result"].get("nextCursor")
            if not cursor:
                break

        def call(name, args):
            m, _ = client.legacy(sid, version, "tools/call", {"name": name, "arguments": args}, timeout=60).rpc(timeout=60)
            return m, version

        def read(uri):
            m, _ = client.legacy(sid, version, "resources/read", {"uri": uri}, timeout=60).rpc(timeout=60)
            return m

    readonly = [t for t in tools if (t.get("annotations") or {}).get("readOnlyHint") is True and not (t.get("annotations") or {}).get("destructiveHint")]
    skipped = [t["name"] for t in tools if t not in readonly]
    print(f"live: {len(readonly)} read-only tools to call; not calling {len(skipped)} others: {', '.join(skipped) or '-'}", flush=True)
    tool_errors = []

    for tool in readonly:
        def one(tool=tool):
            args = minimal_value(tool.get("inputSchema") or {}) or {}
            schema_errors = validate(args, tool.get("inputSchema") or {})
            expect(not schema_errors, f"generated arguments do not satisfy inputSchema: {schema_errors[:3]}")
            msg, legacy_version = call(tool["name"], args)
            expect("error" not in msg, f"JSON-RPC error {msg.get('error')} for args {json.dumps(args)}")
            result = msg["result"]
            check_call_tool_result(result, legacy_version)
            if result.get("isError"):
                text = " ".join(b.get("text", "") for b in result["content"] if b.get("type") == "text")
                tool_errors.append((tool["name"], text))
                return f"tool error (not a protocol failure) for {json.dumps(args)}: {text[:160]}"
            if "outputSchema" in tool and legacy_version != "2025-03-26":
                expect("structuredContent" in result, "outputSchema declared but structuredContent missing")
                errors = validate(result["structuredContent"], tool["outputSchema"])
                expect(not errors, f"structuredContent violates outputSchema: {errors[:5]}")
            return f"args {json.dumps(args)}"

        run.check(f"live: tools/call {tool['name']}", one)

    for res in state.get("resources", []):
        def one_resource(res=res):
            msg = read(res["uri"])
            if "error" in msg:
                return f"error {msg['error'].get('code')}: {msg['error'].get('message')}"
            contents = msg["result"].get("contents")
            expect(isinstance(contents, list) and contents, "contents must be a non-empty array")
            for c in contents:
                expect(isinstance(c.get("uri"), str), "contents[].uri missing")
                expect(("text" in c) != ("blob" in c), "contents[] must have exactly one of text/blob")
                if (c.get("mimeType") or "").endswith("json") and "text" in c:
                    json.loads(c["text"])
            return f"{len(contents)} item(s)"

        run.check(f"live: resources/read {res['uri']}", one_resource)

    if tool_errors:
        print(f"live: {len(tool_errors)} tool(s) returned isError with minimal arguments (often expected, e.g. nothing targeted).", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--url", required=True, help="MCP endpoint, e.g. http://127.0.0.1:41800/mcp")
    parser.add_argument("--token-file", help="file containing the bearer token")
    parser.add_argument("--token", help="bearer token (prefer --token-file or XIV_MCP_TOKEN)")
    parser.add_argument("--live", action="store_true", help="call every read-only tool and read every static resource")
    parser.add_argument("--era", choices=["both", "modern", "legacy"], default="both")
    parser.add_argument("--timeout", type=float, default=30)
    parser.add_argument("--json", metavar="FILE", help="write a JSON report")
    parser.add_argument("-v", "--verbose", action="store_true")
    args = parser.parse_args()

    token = args.token or os.environ.get("XIV_MCP_TOKEN")
    if args.token_file:
        with open(os.path.expanduser(args.token_file), encoding="utf-8") as f:
            token = f.read().strip()

    client = Client(args.url, token, timeout=args.timeout)
    run = Runner(verbose=args.verbose)
    state = {}
    print(f"MCP conformance against {args.url} ({'authenticated' if token else 'no token'})", flush=True)

    transport_checks(run, client)
    if args.era in ("both", "modern"):
        modern_checks(run, client, state)
    if args.era in ("both", "legacy"):
        for version in LEGACY:
            legacy_checks(run, client, version, state)
    if args.live:
        live_checks(run, client, state)

    counts = {s: sum(1 for r in run.results if r["status"] == s) for s in ("PASS", "FAIL", "SKIP")}
    print(f"\n{counts['PASS']} passed, {counts['FAIL']} failed, {counts['SKIP']} skipped", flush=True)
    if args.json:
        with open(args.json, "w", encoding="utf-8") as f:
            json.dump({"url": args.url, "counts": counts, "results": run.results}, f, indent=2)
    return 1 if run.failed else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)
