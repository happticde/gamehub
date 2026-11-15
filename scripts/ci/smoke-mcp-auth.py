"""Real disposable MCP images against production Core with synthetic owners and private test TLS.

Run locally with --container gamehub_devcontainer-app-1; CI uses --host on a Linux SDK runner.
Only the public test CA is mounted in the client. No provider, production key or environment file
is used. Fixture control is private filesystem IPC in the test assembly, never a product endpoint.
"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import pty
import queue
import re
import select
import subprocess
import tempfile
import threading
import time
import uuid


def checked(args, **kwargs):
    result = subprocess.run(args, capture_output=True, timeout=120, **kwargs)
    if result.returncode:
        # Captured diagnostics can contain the intentional login display. Never echo them.
        raise RuntimeError("Docker gate command failed; inspect only sanitized test results.")
    return result.stdout


def gate(image, container=None, postgres=False):
    sequence = 0
    identifier = uuid.uuid4().hex
    channel = "/tmp/gamehub-mcp-gate-" + identifier
    volume = "gamehub-mcp-gate-" + identifier
    active = []
    containers = []
    fixture = None
    fixture_output = []
    prefix = ["docker", "exec", "-i", container] if container else []
    network = "container:" + container if container else "host"

    def remote_python(code, value=None):
        return checked([*prefix, "python3", "-c", code, channel],
                       input=json.dumps(value).encode() if value is not None else None)

    def wait_file(name, timeout=90):
        expires = time.monotonic() + timeout
        while time.monotonic() < expires:
            result = remote_python("import pathlib,sys; p=pathlib.Path(sys.argv[1])/" + repr(name)
                                   + "; print(p.read_text() if p.exists() else '')")
            if result.strip():
                return json.loads(result)
            # The final receipt can outlive a successfully exited fixture. Read it first.
            if fixture.poll() is not None:
                # Status/stack lines identify a failed assertion without printing values,
                # HTTP payloads, codes, tokens or full framework diagnostics.
                for line in b"".join(fixture_output).decode(errors="replace").splitlines():
                    if "McpDockerGateTests.cs:line" in line:
                        print(line.strip())
                raise RuntimeError("Production Core fixture exited before completing the gate.")
            time.sleep(0.2)
        raise RuntimeError("Production Core fixture did not complete its gate step.")

    def control(action, **values):
        nonlocal sequence
        sequence += 1
        remote_python("import json,os,pathlib,sys; p=pathlib.Path(sys.argv[1])/" + repr(str(sequence) + ".request")
                      + "; t=p.with_suffix('.tmp'); t.write_text(sys.stdin.read()); os.chmod(t,0o600); t.rename(p)",
                      {"action": action, **values})
        return wait_file(str(sequence) + ".response")

    try:
        remote_python("import os,sys; os.mkdir(sys.argv[1],0o700)")
        test = ["dotnet", "test", "tests/GameHub.Tests/GameHub.Tests.csproj", "--no-restore",
                "--filter", "FullyQualifiedName~DockerGate_ProductionCore", "--verbosity", "quiet"]
        if container:
            forwarded = [argument for key in ("GAMEHUB_TEST_POSTGRES_CONNECTION", "GAMEHUB_TEST_MCP_POSTGRES_CONNECTION")
                         if key in os.environ for argument in ("-e", key)]
            command = ["docker", "exec", "-w", "/workspaces/gamehub", "-e", "GAMEHUB_MCP_DOCKER_GATE=" + channel,
                       "-e", "GAMEHUB_MCP_DOCKER_POSTGRES=" + str(postgres).lower(), *forwarded, container, *test]
            environment = None
        else:
            command = test
            environment = {**os.environ, "GAMEHUB_MCP_DOCKER_GATE": channel,
                           "GAMEHUB_MCP_DOCKER_POSTGRES": str(postgres).lower()}
        fixture = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=environment)
        # Drain without echoing assertion payloads or the deliberate terminal code display.
        threading.Thread(target=lambda: fixture_output.append(fixture.stdout.read()), daemon=True).start()
        issuer = wait_file("ready.response", timeout=180)["issuer"]
        with tempfile.TemporaryDirectory(prefix="gamehub-mcp-ca-") as local:
            ca = str(Path(local) / "ca.pem")
            if container:
                checked(["docker", "cp", container + ":" + channel + "/ca.pem", ca])
            else:
                Path(ca).write_bytes(Path(channel, "ca.pem").read_bytes())
            os.chmod(local, 0o755)
            os.chmod(ca, 0o644)
            checked(["docker", "volume", "create", volume])
            base = ["docker", "run", "--rm", "--network", network, "--read-only", "--cap-drop", "ALL",
                    "--security-opt", "no-new-privileges",
                    "--mount", f"type=volume,src={volume},dst=/var/lib/gamehub-mcp",
                    "--mount", f"type=bind,src={ca},dst=/test-ca.pem,readonly",
                    "-e", "SSL_CERT_FILE=/test-ca.pem", "-e", "Mcp__Issuer=" + issuer,
                    "-e", "Mcp__Resource=" + issuer + "api", "-e", "Mcp__AuthenticationMode=User"]

            def login(connection, owner, write=False):
                master, slave = pty.openpty()
                name = "gamehub-mcp-login-" + uuid.uuid4().hex
                containers.append(name)
                process = subprocess.Popen([*base, "--name", name, "-it", "-e", "Mcp__Connection=" + connection,
                                            image, "login", *(["--tracking-write"] if write else [])],
                                           stdin=slave, stdout=slave, stderr=slave)
                os.close(slave)
                active.append(process)
                output = b""
                approved = False
                deadline = time.monotonic() + 60
                try:
                    while time.monotonic() < deadline:
                        if select.select([master], [], [], 0.2)[0]:
                            try:
                                part = os.read(master, 8192)
                            except OSError:
                                break
                            if not part:
                                break
                            output += part
                            code = re.search(rb"Enter code: ([A-Za-z0-9-]+)", output)
                            if code and not approved:
                                control("approve", owner=owner, code=code[1].decode(), write=write)
                                approved = True
                        if process.poll() is not None:
                            break
                    process.wait(timeout=15)
                    if process.returncode or not approved or b"Login complete" not in output:
                        raise RuntimeError("Interactive image login/consent failed.")
                finally:
                    os.close(master)

            def tool(connection, name, arguments=None, error=False, contains=None, absent=None):
                runtime_name = "gamehub-mcp-tool-" + uuid.uuid4().hex
                containers.append(runtime_name)
                process = subprocess.Popen([*base, "--name", runtime_name, "-i", "-e", "Mcp__Connection=" + connection, image],
                                           stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
                active.append(process)
                messages = queue.Queue()
                reader = threading.Thread(target=lambda: [messages.put(line) for line in process.stdout], daemon=True)
                reader.start()
                diagnostics = []
                errors = threading.Thread(target=lambda: diagnostics.append(process.stderr.read()), daemon=True)
                errors.start()

                def send(message):
                    process.stdin.write(json.dumps({"jsonrpc": "2.0", **message}) + "\n")
                    process.stdin.flush()

                def response(identifier):
                    for _ in range(100):
                        value = json.loads(messages.get(timeout=30))
                        if value.get("jsonrpc") != "2.0":
                            raise RuntimeError("Image emitted non-protocol stdout.")
                        if value.get("id") == identifier:
                            if "error" in value:
                                raise RuntimeError("Image returned an unexpected protocol error.")
                            return value["result"]
                    raise RuntimeError("Image did not respond.")

                send({"id": 1, "method": "initialize", "params": {"protocolVersion": "2025-06-18", "capabilities": {},
                      "clientInfo": {"name": "gamehub-auth-gate", "version": "1.0.0"}}})
                initialized = response(1)
                if initialized.get("protocolVersion") != "2025-06-18":
                    raise RuntimeError("Unexpected negotiated protocol version.")
                send({"method": "notifications/initialized"})
                send({"id": 2, "method": "tools/call", "params": {"name": name, "arguments": arguments or {}}})
                result = response(2)
                text = json.dumps(result)
                if (bool(result.get("isError")) != error or (contains and contains not in text) or (absent and absent in text)
                        or (not error and contains != "Error:" and "Error:" in text)):
                    reason = next((item.get("text", "") for item in result.get("content", [])
                                   if item.get("text", "").startswith("Error:")), "protocol error")
                    raise RuntimeError(f"Image tool {connection}/{name} violated its expected result (isError={bool(result.get('isError'))}): {reason}")
                process.stdin.close()
                if process.wait(timeout=15):
                    raise RuntimeError("Image failed clean stdin shutdown.")
                reader.join(timeout=5)
                errors.join(timeout=5)
                for line in list(messages.queue):
                    json.loads(line)
                if any(word in "".join(diagnostics) for word in ("access_token", "refresh_token", "Enter code:")):
                    raise RuntimeError("Normal tool startup exposed credential diagnostics.")

            login("reader", 0)
            login("writer", 1, write=True)
            for connection, own, foreign in (("reader", 101, 102), ("writer", 102, 101)):
                tool(connection, "search_library", contains="Gate owner " + str(own - 101), absent="Gate owner " + str(foreign - 101))
                tool(connection, "get_game", {"gameId": own}, contains="Gate owner " + str(own - 101))
                tool(connection, "get_game", {"gameId": foreign}, contains="Error:")
                tool(connection, "get_random_game", contains=str(own))
                tool(connection, "get_statistics")
                tool(connection, "list_tags")
                for name, args in (("set_play_status", {"gameId": own, "playStatus": "Completed"}),
                                   ("set_rating", {"gameId": own, "rating": 4})):
                    tool(connection, name, args, error=connection == "reader")
                    tool(connection, name, {**args, "gameId": foreign}, error=connection == "reader",
                         contains=None if connection == "reader" else "Error:")
            control("assert-data")
            control("restart")
            tool("reader", "get_statistics")
            # Expire only the client timestamp, preserving real issued tokens and server policy.
            selected = hashlib.sha256((issuer + "\n" + issuer + "api\nwriter").encode()).hexdigest()
            credential = "/var/lib/gamehub-mcp/" + selected + "/credential.json"
            before = checked([*base, "--entrypoint", "sh", image, "-ec",
                              'sed -i \'s/"AccessExpiresAt":"[^"]*"/"AccessExpiresAt":"2000-01-01T00:00:00Z"/\' ' + credential
                              + "; sha256sum " + credential]).split()[0]
            tool("writer", "get_statistics")
            after = checked([*base, "--entrypoint", "sha256sum", image, credential]).split()[0]
            if before == after:
                raise RuntimeError("A new image process did not persist rotating renewal.")
            tool("writer", "get_statistics")
            control("revoke", owner=0)
            tool("reader", "get_statistics", error=True)
            tool("writer", "get_statistics")
            logout = checked([*base, "-e", "Mcp__Connection=writer", image, "logout"])
            if logout:
                raise RuntimeError("Logout corrupted stdout.")
            tool("writer", "get_statistics", error=True)
            control("assert-revoked")
            control("stop")
            if fixture.wait(timeout=30):
                raise RuntimeError("Production Core fixture assertions failed.")
            print("MCP authenticated Docker gate passed: two guarded owners, seven tools, consent, TLS, restart, renewal, isolated revocation/logout, clean stdio.")
    finally:
        for name in containers:
            subprocess.run(["docker", "rm", "-f", name], capture_output=True, timeout=20)
        for process in active:
            if process.poll() is None:
                process.kill()
            process.wait(timeout=15)
        if fixture and fixture.poll() is None:
            # Ask the bounded fixture to dispose its database and keys even after a client failure.
            try:
                control("stop")
                fixture.wait(timeout=15)
            except (RuntimeError, subprocess.TimeoutExpired):
                fixture.terminate()
        subprocess.run(["docker", "volume", "rm", volume], capture_output=True, timeout=20)
        remote_python("import shutil,sys; shutil.rmtree(sys.argv[1],ignore_errors=True)")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image")
    location = parser.add_mutually_exclusive_group(required=True)
    location.add_argument("--container")
    location.add_argument("--host", action="store_true")
    parser.add_argument("--postgres", action="store_true")
    args = parser.parse_args()
    gate(args.image, args.container, args.postgres)
