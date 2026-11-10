"""Minimal stdio MCP client for smoke-testing hma-mcp.

usage: python tests/smoke.py <path-to-hma-mcp.exe> [rom.gba]
"""
import json, subprocess, sys

exe = sys.argv[1]
rom = sys.argv[2] if len(sys.argv) > 2 else None
p = subprocess.Popen([exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, encoding="utf-8")
next_id = 0

def rpc(method, params=None, notify=False):
    global next_id
    msg = {"jsonrpc": "2.0", "method": method, "params": params or {}}
    if not notify:
        next_id += 1
        msg["id"] = next_id
    p.stdin.write(json.dumps(msg) + "\n"); p.stdin.flush()
    if notify: return None
    while True:
        reply = json.loads(p.stdout.readline())
        if reply.get("id") == next_id: return reply

def call(name, **args):
    r = rpc("tools/call", {"name": name, "arguments": args})["result"]
    text = r["content"][0]["text"]
    print(f"--- {name} {'ERROR ' if r.get('isError') else ''}{args}\n{text[:1500]}\n")
    return r

init = rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "smoke", "version": "0"}})
print("server:", init["result"]["serverInfo"])
rpc("notifications/initialized", notify=True)
tools = rpc("tools/list")["result"]["tools"]
print(len(tools), "tools:", ", ".join(t["name"] for t in tools), "\n")
call("rom_info")  # expected error: no ROM open

if rom:
    call("open_rom", path=rom)
    call("list_anchors", filter="data.pokemon.stats", limit=5)
    call("describe_table", table="data.pokemon.stats")
    call("read_table", table="data.pokemon.stats", start=1, count=2, fields="hp,attack,type1,type2")
    call("resolve", target="data.pokemon.names")
    call("find_free_space", length=256)
    call("write_table_field", table="data.pokemon.stats", row="1", field="hp", value="99")
    call("undo")
    call("read_table", table="data.pokemon.stats", start=1, count=1, fields="hp")
    call("list_anchors", filter="scripts", limit=5)

p.stdin.close(); p.terminate()
