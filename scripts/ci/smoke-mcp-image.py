"""Exercise the shipped MCP process and volume without live credentials or a running Core."""

import json
import hashlib
import queue
import secrets
import subprocess
import sys
import threading
import uuid
from datetime import datetime, timedelta, timezone


TOOLS = {"search_library", "get_game", "get_random_game", "get_statistics", "list_tags",
         "set_play_status", "set_rating"}


def smoke_user_error(base, image, expected, credentials=()):
    """Call a real tool in explicit user mode; setup errors stay safe and never fall back."""
    process = subprocess.Popen([*base, "-i", "-e", "Mcp__Connection=primary",
                                "-e", "Mcp__AuthenticationMode=User", image],
                               stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                               stderr=subprocess.PIPE, text=True)
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
        for _ in range(100):
            line = messages.get(timeout=30)
            if line is None or any(value in line for value in credentials):
                raise ValueError("User-mode stdout closed or exposed a fixture credential")
            message = json.loads(line)
            if message.get("jsonrpc") != "2.0":
                raise ValueError("User-mode stdout was not JSON-RPC")
            if message.get("id") == identifier:
                return message.get("result", {})
        raise ValueError("No user-mode response")

    try:
        send({"id": 1, "method": "initialize", "params": {
            "protocolVersion": "2025-06-18", "capabilities": {},
            "clientInfo": {"name": "user-authorization-smoke", "version": "1.0.0"}}})
        if "tools" not in response(1).get("capabilities", {}):
            raise ValueError("User-mode initialization failed")
        send({"method": "notifications/initialized"})
        send({"id": 2, "method": "tools/call", "params": {"name": "get_statistics", "arguments": {}}})
        result = response(2)
        if not result.get("isError") or not any(expected in item.get("text", "")
                                                for item in result.get("content", [])):
            raise ValueError("The user-mode tool did not return its sanitized recovery error")
        process.stdin.close()
        if process.wait(timeout=15) != 0:
            raise ValueError("User-mode process did not shut down cleanly")
        diagnostics = process.stderr.read()
        if any(value in diagnostics for value in credentials):
            raise ValueError("User-mode diagnostics exposed a fixture credential")
    finally:
        if process.poll() is None:
            process.kill()
            process.wait(timeout=5)


def smoke_credentials(image):
    """Exercise real disposable containers and volume metadata, without live user credentials."""
    volume = "gamehub-mcp-smoke-" + uuid.uuid4().hex
    subprocess.run(["docker", "volume", "create", volume], check=True, stdout=subprocess.DEVNULL)
    mount = ["--mount", f"type=volume,src={volume},dst=/var/lib/gamehub-mcp"]
    trust = ["-e", "Mcp__Issuer=https://127.0.0.1/", "-e", "Mcp__Resource=https://127.0.0.1/api"]
    base = ["docker", "run", "--rm", "--network", "none", "--read-only", "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges", *mount, *trust]

    def command(connection, *arguments):
        result = subprocess.run([*base, "-e", f"Mcp__Connection={connection}", image, *arguments],
                                text=True, capture_output=True, timeout=30)
        if result.stdout:
            raise ValueError("A setup command wrote to stdout")
        return result

    try:
        if command("primary", "setup").returncode != 0:
            raise ValueError("Non-root setup failed on the new named volume")
        if command("primary", "login").returncode != 1:
            raise ValueError("Noninteractive login did not fail closed")
        smoke_user_error(base, image, "No stored authorization")
        fixture_credentials = []
        for connection in ("primary", "other"):
            selected = hashlib.sha256(("https://127.0.0.1/\nhttps://127.0.0.1/api\n" + connection).encode()).hexdigest()
            # Unissued fixture values travel only on stdin, never arguments, logs or image layers.
            credential = {"Version": 1, "Issuer": "https://127.0.0.1/", "Resource": "https://127.0.0.1/api",
                          "Connection": connection, "AccessToken": secrets.token_urlsafe(32),
                          "RefreshToken": secrets.token_urlsafe(32), "Scope": "library:read offline_access",
                          "AccessExpiresAt": (datetime.now(timezone.utc)
                                              + timedelta(minutes=-1 if connection == "primary" else 10)).isoformat()}
            fixture_credentials.extend(credential[key] for key in ("AccessToken", "RefreshToken"))
            subprocess.run([*base, "-i", "-e", f"SELECTED={selected}", "--entrypoint", "sh", image, "-ec",
                            'umask 077; mkdir -p "/var/lib/gamehub-mcp/$SELECTED"; '
                            'cat > "/var/lib/gamehub-mcp/$SELECTED/credential.json"; '
                            'test "$(stat -c %a /var/lib/gamehub-mcp)" = 700; '
                            'test "$(stat -c %a /var/lib/gamehub-mcp/$SELECTED)" = 700; '
                            'test "$(stat -c %a /var/lib/gamehub-mcp/$SELECTED/credential.json)" = 600'],
                           input=json.dumps(credential), text=True, capture_output=True, check=True, timeout=30)
            stored = command(connection, "status")
            if stored.returncode != 0 or "Authorization stored" not in stored.stderr:
                raise ValueError("A fresh container did not retain its selected credential")
            if any(credential[key] in stored.stderr for key in ("AccessToken", "RefreshToken")):
                raise ValueError("Status exposed a fixture credential")
        # Networking is disabled. A possibly consumed renewal cannot be retried after restart.
        for _ in range(2):
            smoke_user_error(base, image, "Authorization renewal could not be confirmed", fixture_credentials)
        if "requires recovery" not in command("primary", "status").stderr:
            raise ValueError("A fresh container did not preserve uncertain-renewal recovery")
        unavailable = command("primary", "logout")
        if unavailable.returncode != 2 or "Remote revocation could not be confirmed" not in unavailable.stderr:
            raise ValueError("Unavailable remote logout was not reported")
        if "No stored authorization" not in command("primary", "status").stderr:
            raise ValueError("Logout retained its selected local credential")
        if "Authorization stored" not in command("other", "status").stderr:
            raise ValueError("Logout removed an unrelated local connection")
        print("MCP credentials passed: non-root permissions, user-tool setup, uncertain renewal/restart, isolated logout, clean stdio.")
    finally:
        subprocess.run(["docker", "volume", "rm", volume], check=True, stdout=subprocess.DEVNULL)


def smoke(image):
    user = subprocess.check_output(
        ["docker", "image", "inspect", "--format", "{{.Config.User}}", image], text=True).strip()
    if user in {"", "0", "root", "0:0"}:
        raise ValueError("The MCP image must run as a non-root user")
    smoke_credentials(image)

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
