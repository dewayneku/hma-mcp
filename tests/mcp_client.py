"""Tiny stdio MCP client used by the hma-mcp test scripts."""
import json
import subprocess


class McpClient:
    def __init__(self, exe, quiet=False):
        self.p = subprocess.Popen([exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=subprocess.DEVNULL, text=True, encoding="utf-8")
        self.next_id = 0
        self.quiet = quiet
        init = self.rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                       "clientInfo": {"name": "hma-mcp-tests", "version": "0"}})
        self.server_info = init["result"]["serverInfo"]
        self.rpc("notifications/initialized", notify=True)

    def rpc(self, method, params=None, notify=False):
        msg = {"jsonrpc": "2.0", "method": method, "params": params or {}}
        if not notify:
            self.next_id += 1
            msg["id"] = self.next_id
        self.p.stdin.write(json.dumps(msg) + "\n")
        self.p.stdin.flush()
        if notify:
            return None
        while True:
            reply = json.loads(self.p.stdout.readline())
            if reply.get("id") == self.next_id:
                return reply

    def tools(self):
        return self.rpc("tools/list")["result"]["tools"]

    def call(self, tool, /, expect_error=False, **args):
        """Call a tool. Returns parsed JSON when possible, else text. Raises on unexpected errors."""
        r = self.rpc("tools/call", {"name": tool, "arguments": args})["result"]
        text = r["content"][0]["text"]
        is_error = bool(r.get("isError"))
        if not self.quiet:
            print(f"--- {tool} {'ERROR ' if is_error else ''}{args}\n{text[:1200]}\n")
        if is_error != expect_error:
            raise AssertionError(f"{tool}: expected error={expect_error}, got: {text}")
        try:
            return json.loads(text)
        except ValueError:
            return text

    def close(self):
        self.p.stdin.close()
        self.p.terminate()
