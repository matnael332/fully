#!/usr/bin/env python3
from __future__ import annotations

import json
import pathlib
import sys

import yaml

ROOT = pathlib.Path(__file__).resolve().parents[1]
errors: list[str] = []

required = [
    ROOT / ".env.example",
    ROOT / ".dockerignore",
    ROOT / "infra/local/docker-compose.yml",
    ROOT / "infra/local/servicebus/config.json",
    ROOT / "src/App.Web/Dockerfile",
    ROOT / "src/App.Worker/Dockerfile",
    ROOT / "src/App.Web/appsettings.json",
    ROOT / "src/App.Infrastructure/Health/DependencyProbe.cs",
    ROOT / "eng/dev.ps1",
    ROOT / "eng/db.ps1",
    ROOT / "infra/local/postgres/init/10-runtime-role.sh",
    ROOT / "src/App.Infrastructure/Persistence/WorkContinuityModel.cs",
]

for path in required:
    if not path.exists():
        errors.append(f"missing: {path.relative_to(ROOT)}")

try:
    compose = yaml.safe_load((ROOT / "infra/local/docker-compose.yml").read_text(encoding="utf-8"))
    services = set((compose or {}).get("services", {}))
    expected_services = {"postgres", "mssql", "servicebus-emulator", "db-migrator", "web", "worker"}
    if services != expected_services:
        errors.append(f"compose services mismatch: expected {sorted(expected_services)}, got {sorted(services)}")
except Exception as exc:
    errors.append(f"docker-compose.yml invalid: {exc}")

try:
    config = json.loads((ROOT / "infra/local/servicebus/config.json").read_text(encoding="utf-8"))
    namespaces = config["UserConfig"]["Namespaces"]
    queues = {q["Name"] for q in namespaces[0]["Queues"]}
    expected_queues = {"event-processing", "ai-jobs", "action-jobs"}
    if queues != expected_queues:
        errors.append(f"service bus queues mismatch: expected {sorted(expected_queues)}, got {sorted(queues)}")
except Exception as exc:
    errors.append(f"servicebus config invalid: {exc}")

example_env = (ROOT / ".env.example").read_text(encoding="utf-8")
if "ACCEPT_EULA=N" not in example_env:
    errors.append(".env.example must require explicit EULA acceptance")
if "CHANGE_ME" not in example_env:
    errors.append(".env.example should contain obvious non-secret placeholders")
if "POSTGRES_APP_PASSWORD" not in example_env:
    errors.append(".env.example must document the non-owner runtime role password")

# The local image must carry pgvector; plain postgres images do not.
compose_text = (ROOT / "infra/local/docker-compose.yml").read_text(encoding="utf-8")
if "pgvector/pgvector:pg17" not in compose_text:
    errors.append("local postgres image must provide the pgvector extension")

# The application must never connect as the schema owner: a superuser bypasses row-level security.
if "Username=${POSTGRES_APP_USER" not in compose_text:
    errors.append("web/worker must connect as the non-owner runtime role")

ignore = (ROOT / ".gitignore").read_text(encoding="utf-8")
if ".env" not in ignore:
    errors.append(".gitignore does not exclude .env")

if (ROOT / ".env").exists():
    tracked = False
    try:
        import subprocess
        result = subprocess.run(
            ["git", "ls-files", "--error-unmatch", ".env"],
            cwd=ROOT,
            capture_output=True,
            text=True,
        )
        tracked = result.returncode == 0
    except Exception:
        pass
    if tracked:
        errors.append(".env is tracked by Git")

if errors:
    print("LOCAL ENV STRUCTURE CHECK: FAIL")
    for error in errors:
        print(f"- {error}")
    sys.exit(1)

print("LOCAL ENV STRUCTURE CHECK: PASS")
print("- docker-compose structure valid")
print("- pgvector-capable PostgreSQL image configured")
print("- runtime role separated from the schema owner")
print("- Service Bus emulator configuration valid")
print("- secrets remain external to source control")
print("- Web/Worker health-check foundation present")
