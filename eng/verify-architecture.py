#!/usr/bin/env python3
from __future__ import annotations

import pathlib
import sys
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[1]
SRC = ROOT / "src"

allowed = {
    "App.Core": set(),
    "App.Infrastructure": {"App.Core"},
    "App.Connectors.Monday": {"App.Core"},
    "App.Agent.Windows": {"App.Core"},
    "App.Simulator": {"App.Core"},
    "App.Web": {"App.Core", "App.Infrastructure", "App.Connectors.Monday"},
    "App.Worker": {"App.Core", "App.Infrastructure", "App.Connectors.Monday"},
}

errors: list[str] = []
seen = set()

for project_name, allowed_refs in allowed.items():
    project_dir = SRC / project_name
    project_file = project_dir / f"{project_name}.csproj"
    if not project_file.exists():
        errors.append(f"missing project: {project_file.relative_to(ROOT)}")
        continue

    seen.add(project_name)
    tree = ET.parse(project_file)
    refs = set()
    for ref in tree.findall(".//ProjectReference"):
        include = ref.attrib.get("Include", "")
        target = pathlib.Path(include).stem
        refs.add(target)
        if "/tests/" in include.replace("\\", "/") or include.startswith("../../tests"):
            errors.append(f"{project_name} references a test project: {include}")

    unexpected = refs - allowed_refs
    missing = allowed_refs - refs
    if unexpected:
        errors.append(f"{project_name} has forbidden refs: {sorted(unexpected)}")
    if missing:
        errors.append(f"{project_name} is missing required refs: {sorted(missing)}")

# Production code must not point into /tests by textual reference either.
for csproj in SRC.rglob("*.csproj"):
    text = csproj.read_text(encoding="utf-8")
    if "tests/" in text.lower() or "tests\\" in text.lower():
        errors.append(f"production project references tests path: {csproj.relative_to(ROOT)}")

if errors:
    print("ARCHITECTURE CHECK: FAIL")
    for err in errors:
        print(f"- {err}")
    sys.exit(1)

print("ARCHITECTURE CHECK: PASS")
for name in sorted(seen):
    print(f"- {name}: dependency boundary valid")
