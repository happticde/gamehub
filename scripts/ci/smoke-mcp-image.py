"""Exercise the shipped MCP process over stdio without credentials or a running Core."""

import json
import queue
import subprocess
import sys
import threading


TOOLS = {"search_library", "get_game", "get_random_game", "get_statistics", "list_tags",
         "set_play_status", "set_rating"}


def smoke(image):
    user = subprocess.check_output(
        ["docker", "image", "inspect", "--format", "{{.Config.User}}", image], text=True).strip()
    if user in {"", "0", "root", "0:0"}:
        raise ValueError("The MCP image must run as a non-root user")

    process = subprocess.Popen([
        "docker", "run", "--rm", "-i", "--network", "none", "--read-only",
        "--cap-drop", "ALL", "--security-opt", "no-new-privileges", image,
    ], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=sys.stderr, text=True)
    messages = queue.Queue()

    def read_stdout():
        for line in process.stdout:
            messages.put(line)
        messages.put(None)

    reader = threading.Thread(target=read_stdout, daemon=True)
    reader.start()

    def send(message):
        process.stdin.write(json.dumps({"jsonrpc": "2.0", **message}) + "\n")
        process.stdin.flush()

    def response(identifier):
        # Any banner or log on stdout corrupts the transport and must fail this check.
        for _ in range(100):
            line = messages.get(timeout=30)
            if line is None:
                raise ValueError("The MCP process closed stdout before responding")
            message = json.loads(line)
            if message.get("jsonrpc") != "2.0":
                raise ValueError("Unexpected non-JSON-RPC stdout")
            if message.get("id") == identifier:
                if "error" in message:
                    raise ValueError(f"MCP request {identifier} failed: {message['error']}")
                return message["result"]
        raise ValueError("No matching MCP response")

    try:
        send({"id": 1, "method": "initialize", "params": {
            "protocolVersion": "2025-06-18", "capabilities": {},
            "clientInfo": {"name": "gamehub-image-smoke", "version": "1.0.0"},
        }})
        initialized = response(1)
        if "tools" not in initialized.get("capabilities", {}):
            raise ValueError("The server did not advertise tools")
        send({"method": "notifications/initialized"})
        send({"id": 2, "method": "tools/list"})
        names = {tool["name"] for tool in response(2)["tools"]}
        if names != TOOLS:
            raise ValueError(f"Unexpected tool catalog: {sorted(names)}")
        # Validation must execute inside the image without making a Core request.
        send({"id": 3, "method": "tools/call", "params": {
            "name": "set_rating", "arguments": {"gameId": 1, "rating": 0},
        }})
        result = response(3)
        if not any("Rating must be between 1 and 5" in item.get("text", "")
                   for item in result.get("content", [])):
            raise ValueError("The tool call did not return the expected validation result")
        process.stdin.close()
        if process.wait(timeout=15) != 0:
            raise ValueError("The MCP container did not exit successfully after stdin closed")
        reader.join(timeout=5)
        while not messages.empty():
            line = messages.get_nowait()
            if line is not None:
                json.loads(line)
        print("MCP image passed: non-root, initialize, seven tools, validation, clean stdio, and shutdown.")
    finally:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)


if __name__ == "__main__":
    smoke(sys.argv[1])
