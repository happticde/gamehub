"""Start the actual non-root Core/Ingest image with disposable SQLite and no external network."""

import argparse
import subprocess
import time
import uuid


def smoke(image):
    user = subprocess.check_output(["docker", "image", "inspect", "--format", "{{.Config.User}}", image], text=True).strip()
    if user in {"", "0", "root", "0:0"}:
        raise ValueError("Service images must run as a non-root user")
    name = "gamehub-service-smoke-" + uuid.uuid4().hex
    # These synthetic keys are never deployment credentials. No environment file, database,
    # published port, Docker socket or provider account is shared with the image under test.
    settings = {
        "ASPNETCORE_ENVIRONMENT": "Production",
        "ASPNETCORE_URLS": "http://127.0.0.1:8080",
        "Database__Provider": "Sqlite",
        "ConnectionStrings__Sqlite": "Data Source=/app/data/smoke.db",
        "Authentication__ApiKey": "smoke-only-operator-key-01234567890123456789",
        "Authentication__ServiceKey": "smoke-only-delegation-key-01234567890123456",
        "Services__IngestServiceKey": "smoke-only-delegation-key-01234567890123456",
        "Igdb__EnableBackgroundEnrichment": "false",
        "Sync__EnableScheduler": "false",
        "MultiUser__Enabled": "false",
        "Mcp__Device__Enabled": "false",
    }
    environment = [argument for key, value in settings.items() for argument in ("-e", f"{key}={value}")]
    try:
        subprocess.run(["docker", "run", "--rm", "-d", "--name", name, "--network", "none",
                        "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
                        "--tmpfs", "/tmp:rw,noexec,nosuid,size=64m",
                        "--tmpfs", f"/app/data:rw,noexec,nosuid,size=64m,uid={user},mode=0700",
                        *environment, image], check=True, capture_output=True, timeout=30)
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            state = subprocess.run(["docker", "inspect", "--format", "{{.State.Running}}", name], capture_output=True, text=True, timeout=10)
            if state.returncode or state.stdout.strip() != "true":
                raise ValueError("Service image exited before its health endpoint became ready")
            result = subprocess.run(["docker", "exec", name, "wget", "-q", "-T", "2", "-O", "-",
                                     "http://127.0.0.1:8080/health"], capture_output=True, text=True, timeout=5)
            if result.returncode == 0 and result.stdout.strip() == "Healthy":
                print("Service image passed: non-root production entry point, migrated SQLite, healthy HTTP, isolated disposable storage.")
                return
            time.sleep(0.5)
        raise ValueError("Service image did not become healthy within the startup budget")
    finally:
        subprocess.run(["docker", "rm", "-f", name], capture_output=True, timeout=20)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image")
    smoke(parser.parse_args().image)
