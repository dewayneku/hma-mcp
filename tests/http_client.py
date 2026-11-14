"""Minimal Streamable HTTP MCP client for the hma-mcp tests. Same call()/call_image() interface as mcp_client.McpClient."""
import base64
import json
import urllib.error
import urllib.request


class HttpMcpClient:
    def __init__(self, url, bearer=None, quiet=True):
        self.url, self.bearer, self.quiet = url, bearer, quiet
        self.session = None
        self.next_id = 0
        init = self.rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                       "clientInfo": {"name": "hma-mcp-tests", "version": "0"}})
        self.server_info = init["result"]["serverInfo"]
        self.rpc("notifications/initialized", notify=True)

    def post(self, body):
        """POST a JSON-RPC message; returns (status, parsed reply or None)."""
        req = urllib.request.Request(self.url, data=json.dumps(body).encode(), method="POST")
        req.add_header("Content-Type", "application/json")
        req.add_header("Accept", "application/json, text/event-stream")
        if self.bearer:
            req.add_header("Authorization", "Bearer " + self.bearer)
        if self.session:
            req.add_header("Mcp-Session-Id", self.session)
        try:
            with urllib.request.urlopen(req, timeout=120) as r:
                self.session = r.headers.get("Mcp-Session-Id") or self.session
                text = r.read().decode("utf-8")
                if not text.strip():
                    return r.status, None
                if r.headers.get("Content-Type", "").startswith("text/event-stream"):
                    # the reply is the last "data:" event that carries our id
                    for line in reversed(text.splitlines()):
                        if line.startswith("data:"):
                            return r.status, json.loads(line[5:].strip())
                    return r.status, None
                return r.status, json.loads(text)
        except urllib.error.HTTPError as e:
            return e.code, None

    def rpc(self, method, params=None, notify=False):
        msg = {"jsonrpc": "2.0", "method": method, "params": params or {}}
        if not notify:
            self.next_id += 1
            msg["id"] = self.next_id
        status, reply = self.post(msg)
        if notify:
            return None
        if reply is None:
            raise AssertionError(f"{method}: HTTP {status}")
        return reply

    def tools(self):
        return self.rpc("tools/list")["result"]["tools"]

    def call(self, tool, /, expect_error=False, **args):
        r = self.rpc("tools/call", {"name": tool, "arguments": args})["result"]
        text = next(b["text"] for b in r["content"] if b["type"] == "text")
        if bool(r.get("isError")) != expect_error:
            raise AssertionError(f"{tool}: expected error={expect_error}, got: {text}")
        try:
            return json.loads(text)
        except ValueError:
            return text

    def call_image(self, tool, /, **args):
        r = self.rpc("tools/call", {"name": tool, "arguments": args})["result"]
        text = next(b["text"] for b in r["content"] if b["type"] == "text")
        if r.get("isError"):
            raise AssertionError(f"{tool}: unexpected error: {text}")
        images = [base64.b64decode(b["data"]) for b in r["content"] if b["type"] == "image"]
        assert len(images) == 1, f"{tool}: expected one image"
        return json.loads(text), images[0]


def status_of(url, bearer=None):
    """HTTP status of an initialize request (to check the token gate)."""
    c = HttpMcpClient.__new__(HttpMcpClient)
    c.url, c.bearer, c.session, c.next_id = url, bearer, None, 0
    status, _ = c.post({"jsonrpc": "2.0", "id": 1, "method": "initialize",
                        "params": {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "t", "version": "0"}}})
    return status
