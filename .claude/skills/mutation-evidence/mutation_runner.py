"""Fully mutation-evidence runner: the tool of the /mutation-evidence skill (SKILL.md beside this file).

One planned mutant at a time, it writes the mutation into production code under src/, builds and runs only that
mutant's detecting tests, restores the exact original bytes and verifies them by SHA-256 before anything else happens,
and records the verdict: CAUGHT, SURVIVED or INVALID. Evidence is bound to the hashes of the mutated files, of the
detecting test files and of each mutant definition; the gate-closure manifest exists only while all of it is current
and the campaign is READY.

Run it from the repository root in the Bash tool, in the foreground, one command at a time:
  "$PY" .claude/skills/mutation-evidence/mutation_runner.py <command> --plan "$SP/<campaign>-mutants.json"

  check      validate the plan and every anchor; writes nothing
  baseline   record git status, SHA-256 and byte backups of every file the plan mutates
  run        [ids] [--max N]: the named mutants, or the next pending ones, in plan order, as one bounded batch
  recover    restore what an interrupted run left mutated, from the byte backups, and verify it
  status     classify, write the report and, only when READY, the gate-closure manifest

Exit codes: 0 done (status: READY); 1 refused, stopped or NOT READY; 2 a restoration could not be verified (stop).

Plan: a JSON object in a file outside the repository. The campaign directory <campaign>-mutation/ and the manifest
<campaign>-mutation.sha256 are written beside it.
  campaign          id used in those names, such as "3g"
  scope             the changed production logic and the invariants under test
  security_classes  every key of SECURITY_CLASSES: a list of mutant ids, or "n/a: <reason>"
  test_support      optional: further test files whose change makes the evidence stale
  mutants           a list of objects:
    id              unique, such as "P01"
    behaviour       the behavioural change, in words
    edits           [{"file": "src/...", "old": text found exactly once, "new": its replacement}], LF newlines
    detect          [{"project": "App.Unit.Tests", "class": "SimpleOrQualifiedName", "method": optional name,
                      "kind": "behavioural" (default) | "fresh-db" | "static"}]
    expect          optional regex over "<exception type>: <first message line>": the intended reason. With it, a
                    failure counts only when it matches; without it, only an xunit assertion failure counts
    evidence        migration or schema mutants only: "schema" (default), which needs a fresh-db detection, or
                    "static" (a static guard's vacuity), which needs schema_covered_by: the id of a schema mutant on
                    the same file that a fresh database catches
  judgments         {"<id>": {"verdict": "INVALID", "reason": "..."}}: an equivalent or uncompilable mutant with its
                    justification. A judgment can never make a mutant CAUGHT, and never applies to a broken run.
"""
import argparse
import datetime
import hashlib
import json
import os
import re
import subprocess
import sys
import time
import xml.etree.ElementTree as ElementTree
from pathlib import Path

REPO = Path(os.environ.get("FULLY_MUTATION_ROOT") or Path(__file__).resolve().parents[3]).resolve()
MUTABLE_ROOT = "src/"
# A mutation here changes what a database is given; only a database created and migrated for the test can show it.
SCHEMA_PATHS = (
    "src/App.Infrastructure/Persistence/Migrations/",
    "src/App.Infrastructure/Persistence/RuntimeRoleBootstrap.cs",
    "src/App.Infrastructure/Persistence/DatabaseMigrator.cs",
)
SECURITY_CLASSES = {
    "tenant-predicate": "tenant predicate removed",
    "rls-tenant-binding": "row-level security or tenant binding removed",
    "authorization-guard": "authorization guard removed",
    "identity-transaction-binding": "identity or transaction binding removed",
    "failure-as-success": "a retry or failure treated as success",
    "replay-idempotency": "replay or idempotency protection removed",
    "secret-log-protection": "secret or log protection removed",
    "authority-revocation": "authority or revocation check removed",
}
KINDS = ("behavioural", "fresh-db", "static")
VERDICTS = ("CAUGHT", "SURVIVED", "INVALID")
# xunit.v3 reports an assertion failure as an Xunit.Sdk.*Exception; these Xunit.Sdk types are not assertions.
NOT_ASSERTIONS = {"Xunit.Sdk.TestTimeoutException", "Xunit.Sdk.TestPipelineException",
                  "Xunit.Sdk.TestClassException", "Xunit.Sdk.SkipException"}
FIXTURE_FAILURES = {"Xunit.Sdk.TestPipelineException", "Xunit.Sdk.TestClassException"}
# Microsoft.Testing.Platform: 0 every test passed, 2 some failed. 8 means zero tests ran; anything else is a broken run.
COMPLETED_EXIT_CODES = {0, 2}
FRESH_MARKERS = ("FreshDatabase.", "CREATE DATABASE")
DEFAULT_MAX, HARD_MAX = 3, 6
TEST_TIMEOUT = "5m"
PROCESS_TIMEOUT = 420
BATCH_BUDGET = 480
BOM = b"\xef\xbb\xbf"

CAMPAIGN_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,40}$")
MUTANT_ID = re.compile(r"^[A-Za-z][A-Za-z0-9_-]{0,15}$")
CLASS_NAME = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$")
METHOD_NAME = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")
TYPE_DECLARATION = re.compile(r"\b(?:class|record|struct|interface|enum)\s+([A-Za-z_][A-Za-z0-9_]*)")
TEST_ATTRIBUTE = re.compile(r"^[ \t]*\[(?:Fact|Theory)\b", re.M)
NESTED_TYPE = re.compile(
    r"^[ \t]*(?:private|internal|public|protected)\s+(?:(?:sealed|static|abstract|partial)\s+)*(?:class|record|struct)\s",
    re.M)
DECLARED_METHOD = re.compile(r"\b(?:Task|ValueTask|void)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(")
IDENTIFIER = re.compile(r"\b[A-Z][A-Za-z0-9_]*\b")


class Refusal(Exception):
    """A precondition failed; nothing is mutated."""


class RestoreFailure(Exception):
    """A file could not be brought back to its recorded SHA-256; everything stops."""


# ----------------------------------------------------------------------------------------------------------- helpers

def sha256_bytes(data):
    return hashlib.sha256(data).hexdigest()


def sha256_file(path):
    return sha256_bytes(Path(path).read_bytes())


def utc_now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds")


def write_file(path, data):
    """Every write of a repository file goes through here, so a test can fault it."""
    Path(path).write_bytes(data)


def write_json(path, value):
    Path(path).write_bytes((json.dumps(value, indent=1, ensure_ascii=False) + "\n").encode("utf-8"))


def read_json(path, default=None):
    path = Path(path)
    return json.loads(path.read_bytes().decode("utf-8-sig")) if path.is_file() else default


def inside(path, root):
    try:
        Path(path).resolve().relative_to(root)
        return True
    except ValueError:
        return False


def safe_relative(name):
    return (isinstance(name, str) and name and not name.startswith("/") and ":" not in name
            and ".." not in name.split("/") and "\\" not in name)


def source_files(root):
    """The files under root, skipping build output."""
    for directory, subdirectories, files in os.walk(root):
        subdirectories[:] = sorted(name for name in subdirectories if name not in ("bin", "obj", "TestResults"))
        for name in sorted(files):
            yield Path(directory) / name


def repo_relative(path):
    return Path(path).resolve().relative_to(REPO).as_posix()


def one_line(text, limit=160):
    text = " ".join((text or "").split())
    return text if len(text) <= limit else text[:limit - 3] + "..."


def first_line(text):
    for line in (text or "").splitlines():
        if line.strip():
            return line.strip()
    return ""


def git(*arguments):
    try:
        done = subprocess.run(["git", "--no-optional-locks", "-C", str(REPO), *arguments], capture_output=True)
    except OSError:
        return None
    return done.stdout.decode("utf-8", "replace") if done.returncode == 0 else None


def dotnet(arguments, timeout=PROCESS_TIMEOUT):
    """Runs dotnet from the repository root: (exit code, or None when killed for the timeout; combined output)."""
    process = subprocess.Popen(["dotnet", *arguments], cwd=REPO, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    try:
        output, _ = process.communicate(timeout=timeout)
        return process.returncode, output.decode("utf-8", "replace")
    except subprocess.TimeoutExpired:
        if os.name == "nt":
            subprocess.run(["taskkill", "/F", "/T", "/PID", str(process.pid)], capture_output=True)
        else:
            process.kill()
        output, _ = process.communicate()
        return None, output.decode("utf-8", "replace")


def source_fingerprint():
    """One hash over every file under src/ and tests/, taken while nothing is mutated."""
    digest = hashlib.sha256()
    for root in ("src", "tests"):
        for path in source_files(REPO / root):
            digest.update(repo_relative(path).encode("utf-8") + b"\0" + sha256_file(path).encode("ascii") + b"\n")
    return digest.hexdigest()


# --------------------------------------------------------------------------------------------------------- the plan

class Campaign:
    def __init__(self, plan_path):
        self.plan_path = Path(plan_path).resolve()
        try:
            self.raw = self.plan_path.read_bytes()
            self.plan = json.loads(self.raw.decode("utf-8-sig"))
        except (OSError, ValueError) as error:
            raise Refusal(f"cannot read the plan {self.plan_path}: {error}") from error
        name = self.plan.get("campaign") if isinstance(self.plan, dict) else None
        if not isinstance(name, str) or not CAMPAIGN_ID.match(name):
            raise Refusal(f"campaign must match {CAMPAIGN_ID.pattern}")
        self.name = name
        self.dir = self.plan_path.parent / f"{name}-mutation"
        self.manifest = self.plan_path.parent / f"{name}-mutation.sha256"
        self.baseline_file = self.dir / "baseline.json"
        self.results_file = self.dir / "results.jsonl"
        self.batches_file = self.dir / "batches.jsonl"
        self.controls_file = self.dir / "controls.json"
        self.journal = self.dir / "inflight.json"
        self.lock = self.dir / "runner.lock"
        self.originals = self.dir / "originals"
        self.reports = self.dir / "reports"
        self.report_file = self.dir / "report.md"

    def baseline(self):
        return read_json(self.baseline_file)

    def results(self):
        """Every recorded attempt, oldest first. A line torn by a killed process is skipped, never guessed at."""
        if not self.results_file.is_file():
            return []
        records = []
        for line in self.results_file.read_bytes().decode("utf-8", "replace").splitlines():
            try:
                record = json.loads(line) if line.strip() else None
            except ValueError:
                record = None
            if isinstance(record, dict) and isinstance(record.get("mutant"), str):
                records.append(record)
        return records

    def append(self, path, record):
        self.dir.mkdir(parents=True, exist_ok=True)
        with open(path, "ab") as handle:
            handle.write((json.dumps(record, ensure_ascii=False) + "\n").encode("utf-8"))
            handle.flush()
            os.fsync(handle.fileno())


class TestIndex:
    """What the test projects declare: test classes, their methods, and the files a test class depends on."""

    def __init__(self):
        self._projects = {}

    def project(self, name):
        if name not in self._projects:
            files = {}
            for path in source_files(REPO / "tests" / name):
                if path.suffix == ".cs":
                    files[repo_relative(path)] = path.read_bytes().decode("utf-8-sig", "replace")
            declared = {}
            for file_name, text in files.items():
                for match in TYPE_DECLARATION.finditer(text):
                    declared.setdefault(match.group(1), set()).add(file_name)
            self._projects[name] = (files, declared)
        return self._projects[name]

    def class_files(self, project, class_name):
        files, declared = self.project(project)
        simple = class_name.rsplit(".", 1)[-1]
        found = sorted(declared.get(simple, ()))
        if "." in class_name:
            namespace = re.escape(class_name.rsplit(".", 1)[0])
            found = [name for name in found if re.search(rf"\bnamespace\s+{namespace}\s*[;{{]", files[name])]
        return found

    def test_methods(self, project, class_name):
        """{method: text from its [Fact] or [Theory] attribute to the next test or nested type}."""
        files, _ = self.project(project)
        methods = {}
        for name in self.class_files(project, class_name):
            text = files[name]
            starts = [match.start() for match in TEST_ATTRIBUTE.finditer(text)]
            for position, start in enumerate(starts):
                end = starts[position + 1] if position + 1 < len(starts) else len(text)
                nested = NESTED_TYPE.search(text, start, end)
                chunk = text[start:nested.start() if nested else end]
                declared = DECLARED_METHOD.search(chunk)
                if declared:
                    methods[declared.group(1)] = chunk
        return methods

    def text(self, project, file_name):
        return self.project(project)[0][file_name]

    def support_files(self, project, file_name):
        """The test files declaring a type this file uses (one level): fixtures, fakes, seeding helpers."""
        files, declared = self.project(project)
        text = files[file_name]
        own = {match.group(1) for match in TYPE_DECLARATION.finditer(text)}
        found = set()
        for identifier in set(IDENTIFIER.findall(text)) - own:
            found.update(declared.get(identifier, ()))
        found.discard(file_name)
        return found


def mutate_bytes(original, pairs):
    """Applies exact-anchor replacements, keeping the BOM and line endings: ((bytes, first lines), None) or (None, problem)."""
    has_bom = original.startswith(BOM)
    try:
        text = original[3 if has_bom else 0:].decode("utf-8")
    except UnicodeDecodeError:
        return None, "not UTF-8 text"
    crlf, lf = text.count("\r\n"), text.count("\n")
    if crlf and crlf != lf:
        return None, "mixed line endings; normalize the file first"
    body = text.replace("\r\n", "\n")
    lines = []
    for old, new in pairs:
        count = body.count(old)
        if count != 1:
            return None, f"anchor found {count} times, expected exactly once: {one_line(old, 70)!r}"
        index = body.index(old)
        lines.append(body[:index].count("\n") + 1)
        body = body[:index] + new + body[index + len(old):]
    mutated = (BOM if has_bom else b"") + body.replace("\n", "\r\n" if crlf else "\n").encode("utf-8")
    if mutated == original:
        return None, "the mutant changes nothing"
    return (mutated, lines), None


def is_schema_file(name):
    return any(name == path or (path.endswith("/") and name.startswith(path)) for path in SCHEMA_PATHS)


def definition_hash(mutant):
    core = {key: mutant[key] for key in ("id", "behaviour", "edits", "detect", "expect", "evidence", "covered_by")}
    return sha256_bytes(json.dumps(core, sort_keys=True, ensure_ascii=False).encode("utf-8"))


def selections(detect):
    """One test invocation per project and filter type: same-type filters are OR'ed, different types AND'ed."""
    groups = {}
    for entry in detect:
        name = entry["class"] if "." in entry["class"] else "*." + entry["class"]
        kind = "method" if entry["method"] else "class"
        pattern = f"{name}.{entry['method']}" if entry["method"] else name
        groups.setdefault((entry["project"], kind), set()).add(pattern)
    return [(project, kind, tuple(sorted(patterns))) for (project, kind), patterns in sorted(groups.items())]


def selection_key(selection):
    project, kind, patterns = selection
    return f"{project}|{kind}|{','.join(patterns)}"


def parse_edits(label, raw, errors):
    if not isinstance(raw, list) or not raw:
        errors.append(f"{label}: edits must be a non-empty list")
        return [], {}, []
    edits, by_file = [], {}
    for position, edit in enumerate(raw, 1):
        if not isinstance(edit, dict) or set(edit) != {"file", "old", "new"} \
                or not all(isinstance(edit[key], str) for key in ("file", "old", "new")):
            errors.append(f"{label}: edit {position} must have exactly the strings file, old and new")
            continue
        name = edit["file"]
        if not safe_relative(name) or not name.startswith(MUTABLE_ROOT):
            errors.append(f"{label}: {name!r} is not production code under {MUTABLE_ROOT}; only src/ is mutated")
            continue
        path = REPO / name
        if not path.is_file() or not inside(path, REPO):
            errors.append(f"{label}: {name} does not exist")
            continue
        old, new = edit["old"].replace("\r\n", "\n"), edit["new"].replace("\r\n", "\n")
        if not old or old == new:
            errors.append(f"{label}: edit {position} needs a non-empty old text and a different new text")
            continue
        edits.append({"file": name, "old": old, "new": new})
        by_file.setdefault(name, []).append((old, new))
    locations = []
    for name, pairs in by_file.items():
        result, problem = mutate_bytes((REPO / name).read_bytes(), pairs)
        if problem:
            errors.append(f"{label}: {name}: {problem}")
            continue
        locations.append(f"{name}:{result[1][0]}")
    return edits, by_file, locations


def parse_detect(label, raw, index, errors):
    if not isinstance(raw, list) or not raw:
        errors.append(f"{label}: detect must name at least one detecting test; the full suite is never run per mutant")
        return []
    detect = []
    for position, entry in enumerate(raw, 1):
        where = f"{label}: detect {position}"
        if not isinstance(entry, dict) or not set(entry) <= {"project", "class", "method", "kind"}:
            errors.append(f"{where}: only project, class, method and kind are allowed")
            continue
        project, class_name = entry.get("project"), entry.get("class")
        method, kind = entry.get("method"), entry.get("kind", "behavioural")
        if not isinstance(project, str) or not METHOD_NAME.match(project.replace(".", "_")) \
                or not (REPO / "tests" / project / f"{project}.csproj").is_file():
            errors.append(f"{where}: project {project!r} is not a test project under tests/")
            continue
        if not isinstance(class_name, str) or not CLASS_NAME.match(class_name):
            errors.append(f"{where}: class must be a test class name, without wildcards")
            continue
        if method is not None and (not isinstance(method, str) or not METHOD_NAME.match(method)):
            errors.append(f"{where}: method must be a test method name, without wildcards")
            continue
        if kind not in KINDS:
            errors.append(f"{where}: kind must be one of {', '.join(KINDS)}")
            continue
        files = index.class_files(project, class_name)
        if not files:
            errors.append(f"{where}: no class {class_name} is declared in tests/{project}")
            continue
        methods = index.test_methods(project, class_name)
        if method is not None and method not in methods:
            errors.append(f"{where}: {class_name} has no test method {method}")
            continue
        if not methods:
            errors.append(f"{where}: {class_name} has no [Fact] or [Theory] method")
            continue
        if kind == "fresh-db":
            text = "".join(index.text(project, name) for name in files)
            fresh = {name for name, chunk in methods.items() if any(marker in chunk for marker in FRESH_MARKERS)}
            needed = {method} if method else set(methods)
            if "CREATE DATABASE" not in text or "MigrateAsync" not in text or not needed <= fresh:
                errors.append(f"{where}: {class_name}{'.' + method if method else ''} does not create and migrate a "
                              "fresh database in every selected test; name a fresh-database test")
                continue
        detect.append({"project": project, "class": class_name, "method": method, "kind": kind})
    return detect


def analyse(campaign):
    """Validates the plan against the current files: (errors, analysis). Writes nothing."""
    plan, errors = campaign.plan, []
    allowed = {"campaign", "scope", "security_classes", "test_support", "mutants", "judgments"}
    if set(plan) - allowed:
        errors.append(f"unknown plan keys: {sorted(set(plan) - allowed)}")
    if inside(campaign.plan_path, REPO):
        errors.append("the plan is inside the repository; keep it and its evidence in the session scratchpad")
    if not isinstance(plan.get("scope"), str) or not plan["scope"].strip():
        errors.append("scope: name the changed production logic and the invariants under test")
    support = plan.get("test_support", [])
    if not isinstance(support, list) or not all(isinstance(name, str) and safe_relative(name)
                                                and name.startswith("tests/") and (REPO / name).is_file()
                                                for name in support):
        errors.append("test_support: a list of existing files under tests/")
        support = []
    index = TestIndex()
    mutants, by_id = [], {}
    raw_mutants = plan.get("mutants")
    if not isinstance(raw_mutants, list) or not raw_mutants:
        errors.append("mutants: a non-empty list is required")
        raw_mutants = []
    for position, raw in enumerate(raw_mutants, 1):
        if not isinstance(raw, dict):
            errors.append(f"mutant {position}: not an object")
            continue
        unknown = set(raw) - {"id", "behaviour", "edits", "detect", "expect", "evidence", "schema_covered_by"}
        mutant_id = raw.get("id")
        if not isinstance(mutant_id, str) or not MUTANT_ID.match(mutant_id) or mutant_id in by_id:
            errors.append(f"mutant {position}: id {mutant_id!r} must be unique and match {MUTANT_ID.pattern}")
            continue
        if unknown:
            errors.append(f"{mutant_id}: unknown keys {sorted(unknown)}")
        behaviour = raw.get("behaviour")
        if not isinstance(behaviour, str) or not behaviour.strip():
            errors.append(f"{mutant_id}: behaviour must say what the mutation changes")
            behaviour = ""
        edits, by_file, locations = parse_edits(mutant_id, raw.get("edits"), errors)
        detect = parse_detect(mutant_id, raw.get("detect"), index, errors)
        expect = raw.get("expect")
        if expect is not None:
            try:
                if not isinstance(expect, str) or not expect:
                    raise re.error("empty")
                re.compile(expect)
            except re.error:
                errors.append(f"{mutant_id}: expect must be a valid, non-empty regular expression")
                expect = None
        schema = any(is_schema_file(name) for name in by_file)
        evidence, covered = raw.get("evidence"), raw.get("schema_covered_by")
        if not schema:
            if evidence is not None or covered is not None:
                errors.append(f"{mutant_id}: evidence and schema_covered_by apply only to migration or schema mutants")
            evidence = covered = None
        else:
            evidence = evidence or "schema"
            if evidence == "schema":
                if covered is not None:
                    errors.append(f"{mutant_id}: schema_covered_by belongs only to a mutant whose evidence is static")
                if not any(entry["kind"] == "fresh-db" for entry in detect):
                    errors.append(f"{mutant_id}: a migration or schema mutant needs a fresh-db detecting test; a test "
                                  "on the already-migrated shared database proves nothing about it")
            elif evidence == "static":
                if any(entry["kind"] != "static" for entry in detect):
                    errors.append(f"{mutant_id}: evidence static means every detecting test is a static pin")
                if not isinstance(covered, str):
                    errors.append(f"{mutant_id}: evidence static needs schema_covered_by, a schema mutant on the same "
                                  "file that a fresh database catches")
            else:
                errors.append(f"{mutant_id}: evidence must be schema or static")
        test_files = set(support)
        for entry in detect:
            for name in index.class_files(entry["project"], entry["class"]):
                test_files.add(name)
                test_files.update(index.support_files(entry["project"], name))
        mutant = {"id": mutant_id, "behaviour": behaviour.strip(), "edits": edits, "detect": detect, "expect": expect,
                  "evidence": evidence, "covered_by": covered}
        mutant.update(definition=definition_hash(mutant), pairs=by_file, files=sorted(by_file), locations=locations,
                      schema=schema, test_files=sorted(test_files), selections=selections(detect))
        mutants.append(mutant)
        by_id[mutant_id] = mutant
    for mutant in mutants:
        if mutant["evidence"] == "static" and isinstance(mutant["covered_by"], str):
            other = by_id.get(mutant["covered_by"])
            if other is None or other["evidence"] != "schema" or not set(other["files"]) & set(mutant["files"]):
                errors.append(f"{mutant['id']}: schema_covered_by {mutant['covered_by']} must be a schema mutant of "
                              "the same file")
    classes = plan.get("security_classes")
    if not isinstance(classes, dict) or set(classes) != set(SECURITY_CLASSES):
        errors.append(f"security_classes: give exactly these keys: {', '.join(SECURITY_CLASSES)}")
    else:
        for key, value in classes.items():
            if isinstance(value, str) and value.startswith("n/a:") and len(value[4:].strip()) >= 10:
                continue
            if isinstance(value, list) and value and all(item in by_id for item in value):
                continue
            errors.append(f"security_classes.{key}: a list of this plan's mutant ids, or 'n/a: <reason>'")
    judgments = plan.get("judgments", {})
    if not isinstance(judgments, dict):
        errors.append("judgments: an object keyed by mutant id")
        judgments = {}
    for mutant_id, judgment in judgments.items():
        if mutant_id not in by_id:
            errors.append(f"judgments.{mutant_id}: no such mutant")
        elif not isinstance(judgment, dict) or set(judgment) != {"verdict", "reason"} \
                or judgment.get("verdict") != "INVALID":
            errors.append(f"judgments.{mutant_id}: the only judgment is verdict INVALID with a reason; "
                          "nothing can be judged CAUGHT")
        elif not isinstance(judgment["reason"], str) or len(judgment["reason"].strip()) < 20:
            errors.append(f"judgments.{mutant_id}: give the reason (at least 20 characters)")
    targets = sorted({name for mutant in mutants for name in mutant["files"]})
    return errors, {"mutants": mutants, "by_id": by_id, "targets": targets, "judgments": judgments,
                    "index": index, "support": support}


def fresh_methods(index, project, class_name):
    return {name for name, chunk in index.test_methods(project, class_name).items()
            if any(marker in chunk for marker in FRESH_MARKERS)}


# ------------------------------------------------------------------------------------------------- classification

def parse_report(path):
    """The xUnit.net v2+ XML report: totals and every test, or None when it is missing or unreadable."""
    path = Path(path)
    if not path.is_file():
        return None
    try:
        root = ElementTree.parse(path).getroot()
    except ElementTree.ParseError:
        return None
    assemblies = root.findall("assembly") if root.tag == "assemblies" else [root] if root.tag == "assembly" else []
    if not assemblies:
        return None
    total = errors = 0
    tests = []
    for assembly in assemblies:
        total += int(assembly.get("total", "0"))
        errors += int(assembly.get("errors", "0"))
        for test in assembly.iter("test"):
            failure = test.find("failure")
            tests.append({"type": test.get("type", ""), "method": test.get("method", ""),
                          "name": test.get("name", ""), "result": test.get("result", ""),
                          "exception": failure.get("exception-type", "") if failure is not None else "",
                          "message": first_line(failure.findtext("message", "")) if failure is not None else ""})
    return {"total": total, "errors": errors, "tests": tests}


def entry_matches(entry, project, test):
    if entry["project"] != project:
        return False
    if "." in entry["class"]:
        if test["type"] != entry["class"]:
            return False
    elif test["type"].rsplit(".", 1)[-1] != entry["class"]:
        return False
    return entry["method"] is None or test["method"] == entry["method"]


def is_assertion(exception):
    return exception.startswith("Xunit.Sdk.") and exception.endswith("Exception") and exception not in NOT_ASSERTIONS


def run_problems(runs):
    """Why these test runs cannot be read as evidence at all (a broken run); empty when they can."""
    problems = []
    for run in runs:
        where = f"{run['project']} ({run['kind']} filter)"
        report, code = run["report"], run["exit"]
        if code is None:
            problems.append(f"{where}: killed after {PROCESS_TIMEOUT} s")
        elif code not in COMPLETED_EXIT_CODES:
            problems.append(f"{where}: exit code {code}" + (" (zero tests ran)" if code == 8 else ""))
        if report is None:
            problems.append(f"{where}: no xunit report")
            continue
        failed = [test for test in report["tests"] if test["result"] == "Fail"]
        if report["total"] == 0:
            problems.append(f"{where}: zero tests ran")
        if report["errors"]:
            problems.append(f"{where}: {report['errors']} assembly or cleanup errors")
        if code in COMPLETED_EXIT_CODES and (code == 0) == bool(failed):
            problems.append(f"{where}: exit code {code} disagrees with {len(failed)} failed tests in the report")
        fixture = [test for test in failed if test["exception"] in FIXTURE_FAILURES]
        if fixture:
            problems.append(f"{where}: a test fixture failed ({fixture[0]['exception']}: {fixture[0]['message']})")
    return problems


def classify(mutant, build_ok, compile_errors, runs, fresh):
    """The verdict for one mutant run. fresh: {(project, class simple name): methods that create a fresh database}."""
    if not build_ok:
        detail = compile_errors[0] if compile_errors else "the build failed"
        return {"verdict": "INVALID", "reason": f"does not compile: {one_line(detail)}", "broken": False,
                "qualifying": [], "unconfirmed": []}
    problems = run_problems(runs)
    if problems:
        return {"verdict": "SURVIVED", "reason": "unproven, the run is broken: " + "; ".join(problems),
                "broken": True, "qualifying": [], "unconfirmed": []}
    qualifying, unconfirmed = [], []
    for run in runs:
        for test in run["report"]["tests"]:
            if test["result"] != "Fail":
                continue
            entries = [entry for entry in mutant["detect"] if entry_matches(entry, run["project"], test)]
            summary = {"test": f"{test['type'].rsplit('.', 1)[-1]}.{test['method']}", "project": run["project"],
                       "exception": test["exception"], "message": test["message"]}
            reason = f"{test['exception']}: {test['message']}"
            if mutant["expect"] is not None:
                expected = bool(entries) and re.search(mutant["expect"], reason) is not None
            else:
                expected = bool(entries) and is_assertion(test["exception"])
            if not expected:
                unconfirmed.append(summary)
                continue
            kinds = sorted({entry["kind"] for entry in entries})
            simple = test["type"].rsplit(".", 1)[-1]
            summary.update(kinds=kinds, fresh="fresh-db" in kinds and test["method"] in fresh.get((run["project"], simple), ()))
            qualifying.append(summary)
    result = {"broken": False, "qualifying": qualifying, "unconfirmed": unconfirmed}
    if mutant["schema"] and mutant["evidence"] == "schema":
        if any(item["fresh"] for item in qualifying):
            return dict(result, verdict="CAUGHT", reason="a fresh database detected it")
        elsewhere = ", ".join(item["test"] for item in qualifying)
        return dict(result, verdict="SURVIVED", reason="no fresh-database test detected this schema mutant"
                    + (f"; it failed only {elsewhere}, which is not fresh-database evidence" if elsewhere else ""))
    if mutant["schema"] and mutant["evidence"] == "static":
        if any("static" in item["kinds"] for item in qualifying):
            return dict(result, verdict="CAUGHT", reason=f"the static guard fired; schema behaviour is proven by "
                                                          f"{mutant['covered_by']}")
        return dict(result, verdict="SURVIVED", reason="the static guard did not fire")
    if qualifying:
        return dict(result, verdict="CAUGHT", reason="a detecting test failed for the intended reason")
    if unconfirmed:
        listed = "; ".join(f"{item['test']} ({item['exception']})" for item in unconfirmed[:3])
        rule = "the plan's expect" if mutant["expect"] is not None else "an xunit assertion failure"
        return dict(result, verdict="SURVIVED", reason="tests failed only for an unrelated or unconfirmed reason: "
                    + listed + f"; only {rule} in a detecting test counts")
    return dict(result, verdict="SURVIVED", reason="every detecting test passed")


# ----------------------------------------------------------------------------------------------------- execution

class Lock:
    def __init__(self, campaign, command):
        self.campaign, self.command = campaign, command

    def __enter__(self):
        self.campaign.dir.mkdir(parents=True, exist_ok=True)
        try:
            handle = os.open(self.campaign.lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
        except FileExistsError:
            raise Refusal(f"{self.campaign.lock} exists: a runner is active or was interrupted; run recover") from None
        os.write(handle, json.dumps({"command": self.command, "pid": os.getpid(), "at": utc_now()}).encode("utf-8"))
        os.close(handle)
        return self

    def __exit__(self, kind, value, traceback):
        if kind is None or not issubclass(kind, RestoreFailure):
            self.campaign.lock.unlink(missing_ok=True)
        return False


def build(project):
    code, output = dotnet(["build", f"tests/{project}/{project}.csproj", "-c", "Debug", "-v", "q", "-nologo",
                           "--disable-build-servers", "-p:TreatWarningsAsErrors=false"])
    errors = list(dict.fromkeys(line.strip() for line in output.splitlines() if ": error " in line))
    return code == 0, errors[:3]


def run_tests(campaign, selection, label):
    project, kind, patterns = selection
    campaign.reports.mkdir(parents=True, exist_ok=True)
    name = f"{label}-{project}-{kind}.xml"
    (campaign.reports / name).unlink(missing_ok=True)
    arguments = ["test", "--project", f"tests/{project}/{project}.csproj", "-c", "Debug", "--no-build",
                 "--no-progress", "--results-directory", str(campaign.reports), "--report-xunit-xml",
                 "--report-xunit-xml-filename", name, "--timeout", TEST_TIMEOUT]
    for pattern in patterns:
        arguments += [f"--filter-{kind}", pattern]
    code, output = dotnet(arguments)
    return {"project": project, "kind": kind, "patterns": list(patterns), "exit": code,
            "report": parse_report(campaign.reports / name), "report_file": name,
            "tail": [one_line(line) for line in output.splitlines()[-4:]]}


def restore_all(campaign, originals, expected):
    """Writes every original back and proves it by SHA-256, falling back to the byte backup; raises RestoreFailure."""
    outcome, failures = {}, []
    for name, data in originals.items():
        path, want = REPO / name, expected[name]
        try:
            write_file(path, data)
            if sha256_file(path) == want:
                outcome[name] = "restored, SHA-256 verified"
                continue
        except OSError:
            pass
        try:
            backup = campaign.originals / want
            if backup.is_file() and sha256_file(backup) == want:
                write_file(path, backup.read_bytes())
                if sha256_file(path) == want:
                    outcome[name] = "restored from the byte backup, SHA-256 verified"
                    continue
        except OSError:
            pass
        failures.append(f"{name} (expected SHA-256 {want})")
    if failures:
        raise RestoreFailure("not restored: " + ", ".join(failures))
    return outcome


def verify_targets(baseline, names):
    mismatched = []
    for name in names:
        recorded = baseline["targets"].get(name)
        path = REPO / name
        if recorded is None:
            mismatched.append(f"{name} is not in the baseline")
        elif not path.is_file() or sha256_file(path) != recorded["sha256"]:
            mismatched.append(f"{name} differs from its baseline SHA-256")
    return mismatched


def git_snapshot():
    return git("status", "--porcelain=v2", "--untracked-files=all")


def control_problems(run, entries):
    problems = run_problems([run])
    if problems:
        return problems
    tests = run["report"]["tests"]
    failing = [test for test in tests if test["result"] == "Fail"]
    if failing:
        problems.append(f"{len(failing)} selected tests fail on the unmutated tree, such as "
                        f"{failing[0]['type'].rsplit('.', 1)[-1]}.{failing[0]['method']}")
    for entry in entries:
        matched = [test for test in tests if entry_matches(entry, run["project"], test)]
        types = sorted({test["type"] for test in matched})
        label = entry["class"] + (f".{entry['method']}" if entry["method"] else "")
        if not any(test["result"] == "Pass" for test in matched):
            problems.append(f"{label} matched no passing test")
        if len(types) > 1:
            problems.append(f"{label} matched several classes ({', '.join(types)}); qualify it")
    return problems


def ensure_controls(campaign, mutants, fingerprint):
    """Runs each selection once on the unmutated tree; every detecting test must pass there first."""
    controls = read_json(campaign.controls_file, {})
    needed = {}
    for mutant in mutants:
        for selection in mutant["selections"]:
            entries = [entry for entry in mutant["detect"] if entry["project"] == selection[0]
                       and ("method" if entry["method"] else "class") == selection[1]]
            needed.setdefault(selection, []).extend(entries)
    for number, (selection, entries) in enumerate(needed.items(), 1):
        key = selection_key(selection)
        cached = controls.get(key)
        fresh_run = not (cached and cached.get("fingerprint") == fingerprint)
        run = run_tests(campaign, selection, f"control-{int(time.time())}-{number}") if fresh_run else cached["run"]
        problems = control_problems(run, entries)
        if problems:
            raise Refusal(f"control run {key}: " + "; ".join(problems)
                          + ". The plan or the environment is wrong; no mutant ran.")
        if fresh_run:
            controls[key] = {"fingerprint": fingerprint, "at": utc_now(), "run": run}
            write_json(campaign.controls_file, controls)
        print(f"control {key}: {run['report']['total']} tests passed on the unmutated tree")


def run_mutant(campaign, mutant, baseline, index):
    originals = {name: (REPO / name).read_bytes() for name in mutant["files"]}
    expected = {name: baseline["targets"][name]["sha256"] for name in mutant["files"]}
    for name, data in originals.items():
        backup = campaign.originals / expected[name]
        if sha256_bytes(data) != expected[name] or not backup.is_file() or sha256_file(backup) != expected[name]:
            raise Refusal(f"{name} or its byte backup does not match the baseline; nothing was mutated")
    mutated = {}
    for name, pairs in mutant["pairs"].items():
        result, problem = mutate_bytes(originals[name], pairs)
        if problem:
            raise Refusal(f"{mutant['id']}: {name}: {problem}; nothing was mutated")
        mutated[name] = result[0]
    tests = {name: sha256_file(REPO / name) for name in mutant["test_files"]}
    started = utc_now()
    with open(campaign.journal, "xb") as handle:
        handle.write(json.dumps({"mutant": mutant["id"], "started": started, "files": expected}).encode("utf-8"))
        handle.flush()
        os.fsync(handle.fileno())
    build_ok, compile_errors, runs = False, [], []
    try:
        for name, data in mutated.items():
            write_file(REPO / name, data)
        build_ok = True
        for project in dict.fromkeys(entry["project"] for entry in mutant["detect"]):
            built, errors = build(project)
            if not built:
                build_ok, compile_errors = False, errors
                break
        if build_ok:
            label = f"{mutant['id']}-{int(time.time())}"
            runs = [run_tests(campaign, selection, label) for selection in mutant["selections"]]
    finally:
        restored = restore_all(campaign, originals, expected)
        campaign.journal.unlink()
    fresh = {(entry["project"], entry["class"].rsplit(".", 1)[-1]): fresh_methods(index, entry["project"], entry["class"])
             for entry in mutant["detect"] if entry["kind"] == "fresh-db"}
    verdict = classify(mutant, build_ok, compile_errors, runs, fresh)
    return dict(verdict, mutant=mutant["id"], definition=mutant["definition"], behaviour=mutant["behaviour"],
                locations=mutant["locations"], targets=expected, tests=tests, started=started, finished=utc_now(),
                compile_errors=compile_errors, restored=restored,
                runs=[{key: run[key] for key in ("project", "kind", "patterns", "exit", "report_file")}
                      | {"total": run["report"]["total"] if run["report"] else None} for run in runs])


# ------------------------------------------------------------------------------------------------------- commands

def require_clean(campaign):
    if campaign.journal.is_file():
        raise Refusal(f"{campaign.journal} exists: a mutant was interrupted; run recover first")
    if campaign.lock.is_file():
        raise Refusal(f"{campaign.lock} exists: a runner is active or was interrupted; run recover first")


def command_check(campaign, _arguments):
    errors, analysis = analyse(campaign)
    for mutant in analysis["mutants"]:
        detect = "; ".join(f"{entry['class']}{'.' + entry['method'] if entry['method'] else ''} ({entry['kind']})"
                           for entry in mutant["detect"])
        schema = f" [schema, evidence {mutant['evidence']}]" if mutant["schema"] else ""
        print(f"{mutant['id']}  {', '.join(mutant['locations'])}{schema}\n    {mutant['behaviour']}\n    detect: {detect}")
    for error in errors:
        print(f"ERROR {error}")
    print(f"{len(analysis['mutants'])} mutants, {len(analysis['targets'])} production files, {len(errors)} errors")
    return 1 if errors else 0


def command_baseline(campaign, _arguments):
    require_clean(campaign)
    errors, analysis = analyse(campaign)
    if errors:
        raise Refusal("the plan has errors; run check")
    with Lock(campaign, "baseline"):
        snapshot = git_snapshot()
        states = git("status", "--porcelain=v1", "--untracked-files=all", "--", *analysis["targets"]) or ""
        state_of = {line[3:]: line[:2] for line in states.splitlines() if len(line) > 3}
        campaign.originals.mkdir(parents=True, exist_ok=True)
        targets = {}
        for name in analysis["targets"]:
            data = (REPO / name).read_bytes()
            digest = sha256_bytes(data)
            backup = campaign.originals / digest
            if not backup.is_file():
                backup.write_bytes(data)
            if sha256_file(backup) != digest:
                raise Refusal(f"the byte backup of {name} does not verify")
            numstat = git("diff", "HEAD", "--numstat", "--", name) or ""
            targets[name] = {"sha256": digest, "git": state_of.get(name, "unmodified"),
                             "numstat": " ".join(numstat.split()[:2]) if numstat.strip() else ""}
        baseline = {"created": utc_now(), "head": (git("rev-parse", "HEAD") or "").strip(),
                    "git_status": snapshot, "plan_sha256": sha256_bytes(campaign.raw), "targets": targets}
        write_json(campaign.baseline_file, baseline)
    print(f"baseline {campaign.baseline_file}: HEAD {baseline['head'][:12]}, {len(targets)} production files")
    for name, target in targets.items():
        change = f", +/- {target['numstat']} against HEAD" if target["numstat"] else ""
        print(f"  {target['sha256']}  {name}  ({target['git']}{change})")
    print("Confirm each change against HEAD is the reviewed work under test before any run.")
    return 0


def command_run(campaign, arguments):
    require_clean(campaign)
    errors, analysis = analyse(campaign)
    if errors:
        raise Refusal("the plan has errors; run check")
    baseline = campaign.baseline()
    if baseline is None:
        raise Refusal("no baseline; run baseline first")
    mismatched = verify_targets(baseline, analysis["targets"])
    if mismatched:
        raise Refusal("; ".join(mismatched) + ". Run baseline again after reviewing the change.")
    maximum = arguments.max
    if not 1 <= maximum <= HARD_MAX:
        raise Refusal(f"--max must be between 1 and {HARD_MAX}: batches stay small and in the foreground")
    unknown = sorted(set(arguments.ids) - set(analysis["by_id"]))
    if unknown:
        raise Refusal(f"unknown mutant ids: {', '.join(unknown)}")
    results = campaign.results()
    if arguments.ids:
        chosen = [mutant for mutant in analysis["mutants"] if mutant["id"] in arguments.ids]
    else:
        chosen = [mutant for mutant in analysis["mutants"] if pending(mutant, results)]
    batch = chosen[:maximum]
    if not batch:
        print("no mutant is pending; run status")
        return 0
    stopped = broken = None
    with Lock(campaign, "run"):
        started, longest, done = time.monotonic(), 90.0, []
        before = git_snapshot()
        fingerprint = source_fingerprint()
        projects = list(dict.fromkeys(entry["project"] for mutant in batch for entry in mutant["detect"]))
        for project in projects:
            built, problems = build(project)
            if not built:
                raise Refusal(f"the unmutated tree does not build ({project}): {'; '.join(problems)}")
        ensure_controls(campaign, batch, fingerprint)
        for mutant in batch:
            if time.monotonic() - started + longest > BATCH_BUDGET:
                stopped = f"the {BATCH_BUDGET} s batch budget is used up; run again for the rest"
                break
            began = time.monotonic()
            record = run_mutant(campaign, mutant, baseline, analysis["index"])
            campaign.append(campaign.results_file, record)
            done.append(mutant["id"])
            longest = max(longest, time.monotonic() - began)
            restored = "; ".join(f"{name}: {how}" for name, how in record["restored"].items())
            print(f"{record['mutant']} {record['verdict']}: {record['reason']}\n    {restored}")
            if record["broken"]:
                stopped = broken = "a broken run: fix the environment, then run the mutant again"
                break
        for project in projects:
            build(project)
        after = git_snapshot()
        mismatched = verify_targets(baseline, analysis["targets"])
        if mismatched:
            raise RestoreFailure("; ".join(mismatched))
        campaign.append(campaign.batches_file, {"at": utc_now(), "mutants": done, "stopped": stopped,
                                                "git_status_unchanged": before == after})
    results = campaign.results()
    waiting = [mutant["id"] for mutant in analysis["mutants"] if pending(mutant, results)]
    print(f"all {len(analysis['targets'])} production files match their baseline SHA-256; git status "
          f"{'unchanged' if before == after else 'CHANGED during the batch'}")
    if stopped:
        print(f"STOPPED: {stopped}")
    print(f"pending: {', '.join(waiting) if waiting else 'none; run status'}")
    return 1 if broken else 0


def command_recover(campaign, _arguments):
    if campaign.journal.is_file():
        journal = read_json(campaign.journal)
        failures = []
        for name, want in journal["files"].items():
            backup = campaign.originals / want
            if not backup.is_file() or sha256_file(backup) != want:
                failures.append(f"{name}: no verified byte backup")
                continue
            write_file(REPO / name, backup.read_bytes())
            if sha256_file(REPO / name) != want:
                failures.append(f"{name}: does not verify after restoring")
        if failures:
            print("RESTORE FAILED: " + "; ".join(failures))
            return 2
        campaign.append(campaign.results_file, {"mutant": journal["mutant"], "verdict": "INTERRUPTED",
                                                "started": journal["started"], "finished": utc_now(),
                                                "reason": "the run was interrupted; the files were restored from "
                                                          "their byte backups and verified"})
        campaign.journal.unlink()
        print(f"{journal['mutant']}: interrupted run restored; " + ", ".join(journal["files"]) + " verified")
    campaign.lock.unlink(missing_ok=True)
    baseline = campaign.baseline()
    if baseline:
        mismatched = verify_targets(baseline, list(baseline["targets"]))
        if mismatched:
            print("NOT RESTORED BY THE RUNNER (no journal names them): " + "; ".join(mismatched))
            return 1
        print(f"all {len(baseline['targets'])} baseline files match their SHA-256")
    return 0


def pending(mutant, results):
    """A mutant still to run: no current evidence, or only a broken run, which proves nothing."""
    record, _ = current(mutant, results)
    return record is None or record["broken"]


def current(mutant, results):
    """(the latest result for this exact definition, None) when its evidence is current, else (None, why)."""
    attempts = [record for record in results if record.get("mutant") == mutant["id"] and record["verdict"] in VERDICTS]
    if not attempts:
        return None, "not run"
    matching = [record for record in attempts if record["definition"] == mutant["definition"]]
    if not matching:
        return None, "stale: the mutant definition changed"
    latest = matching[-1]
    for name, digest in latest["targets"].items():
        if not (REPO / name).is_file() or sha256_file(REPO / name) != digest:
            return None, f"stale: {name} changed"
    if set(latest["tests"]) != set(mutant["test_files"]):
        return None, "stale: the detecting test files changed"
    for name, digest in latest["tests"].items():
        if not (REPO / name).is_file() or sha256_file(REPO / name) != digest:
            return None, f"stale: test file {name} changed"
    return latest, None


def evaluate(campaign, errors, analysis):
    """Final verdicts, totals, gaps and READY for the current files."""
    baseline, results = campaign.baseline(), campaign.results()
    judgments = analysis["judgments"] if not errors else {}
    rows, reasons, gaps = [], [], []
    for mutant in analysis["mutants"]:
        record, why = current(mutant, results)
        row = {"mutant": mutant, "record": record, "verdict": None, "why": why, "judged": None}
        if record:
            row["verdict"] = record["verdict"]
            judgment = judgments.get(mutant["id"])
            if judgment and record["verdict"] in ("SURVIVED", "INVALID") and not record["broken"]:
                row["verdict"], row["judged"] = "INVALID", judgment["reason"]
        rows.append(row)
    totals = {verdict: sum(1 for row in rows if row["verdict"] == verdict) for verdict in VERDICTS}
    missing = [row for row in rows if row["verdict"] is None]
    if errors:
        reasons.append(f"the plan has {len(errors)} errors")
    if baseline is None:
        reasons.append("no baseline")
    else:
        mismatched = verify_targets(baseline, analysis["targets"])
        if mismatched:
            reasons.append("restoration: " + "; ".join(mismatched))
    if campaign.journal.is_file():
        reasons.append("an interrupted mutant is in flight: run recover")
    if campaign.lock.is_file():
        reasons.append("a runner lock is present: a run is active or was interrupted")
    if missing:
        reasons.append(f"{len(missing)} mutants not run or stale")
    if totals["SURVIVED"]:
        reasons.append(f"{totals['SURVIVED']} SURVIVED")
    unjustified = [row for row in rows if row["verdict"] == "INVALID" and not row["judged"]]
    if unjustified:
        reasons.append(f"{len(unjustified)} INVALID without a justification")
    by_row = {row["mutant"]["id"]: row for row in rows}
    for row in rows:
        mutant = row["mutant"]
        if mutant["evidence"] == "static" and row["verdict"] == "CAUGHT":
            covering = by_row.get(mutant["covered_by"])
            if not covering or covering["verdict"] != "CAUGHT":
                reasons.append(f"{mutant['id']}: its schema behaviour is not proven: {mutant['covered_by']} is not CAUGHT")
        if row["record"] and not row["record"].get("restored"):
            reasons.append(f"{mutant['id']}: no restoration record")
    history = [record for record in results if record.get("mutant") in by_row]
    for row in rows:
        mutant, record = row["mutant"], row["record"]
        if row["verdict"] == "SURVIVED":
            gaps.append(f"{mutant['id']} SURVIVED: {record['reason']}")
        elif row["verdict"] == "INVALID":
            gaps.append(f"{mutant['id']} INVALID: {row['judged'] or record['reason'] + '; no justification yet'}")
        elif row["verdict"] is None:
            gaps.append(f"{mutant['id']} {row['why']}")
        elif row["verdict"] == "CAUGHT" and not mutant["schema"] \
                and all(item["kinds"] == ["static"] for item in record["qualifying"]):
            gaps.append(f"{mutant['id']} caught by static pins only: no behavioural test detected it")
        earlier = [item for item in history if item["mutant"] == mutant["id"] and item is not record
                   and item["verdict"] in ("SURVIVED", "INVALID", "INTERRUPTED")]
        for item in earlier:
            changed = "" if item.get("definition") in (None, mutant["definition"]) else (
                ", redefined since" + ("; the behaviour text changed" if item.get("behaviour") != mutant["behaviour"]
                                       else ""))
            gaps.append(f"{mutant['id']} earlier attempt {item['verdict']} at {item.get('finished', '?')}{changed}")
    classes = campaign.plan.get("security_classes") if isinstance(campaign.plan.get("security_classes"), dict) else {}
    for key, value in classes.items():
        if isinstance(value, str):
            gaps.append(f"security class {key} not applicable: {value[4:].strip()}")
    batches = [json.loads(line) for line in (campaign.batches_file.read_bytes().decode("utf-8").splitlines()
                                             if campaign.batches_file.is_file() else []) if line.strip()]
    changed_batches = [batch for batch in batches if not batch.get("git_status_unchanged")]
    for batch in changed_batches:
        gaps.append(f"git status changed during the batch at {batch['at']} ({', '.join(batch['mutants'])})")
    return {"rows": rows, "totals": totals, "missing": missing, "reasons": reasons, "gaps": gaps,
            "ready": not reasons, "baseline": baseline, "batches": batches}


def cell(text):
    return one_line(str(text), 220).replace("|", "\\|")


def render(campaign, errors, analysis, state):
    lines = [f"# Mutation evidence {campaign.name}: {'READY' if state['ready'] else 'NOT READY'}", "",
             f"Plan `{campaign.plan_path.as_posix()}` (SHA-256 {sha256_bytes(campaign.raw)[:16]}), evidence in "
             f"`{campaign.dir.as_posix()}`.", "", "## 1. Mutation scope", "", str(campaign.plan.get("scope") or "").strip(), "",
             f"{len(analysis['mutants'])} mutants over {len(analysis['targets'])} production files; detecting tests "
             f"selected per mutant, never the full suite."]
    if errors:
        lines += [""] + [f"- plan error: {cell(error)}" for error in errors]
    baseline = state["baseline"]
    lines += ["", "## 2. Production hashes", ""]
    if baseline:
        lines += [f"Baseline {baseline['created']} at HEAD {baseline['head'][:12]}.", "",
                  "| File | SHA-256 at baseline | Git state at baseline | Now |", "|---|---|---|---|"]
        for name in analysis["targets"]:
            target = baseline["targets"].get(name)
            now = "missing" if not (REPO / name).is_file() else (
                "matches" if target and sha256_file(REPO / name) == target["sha256"] else "DIFFERS")
            lines.append(f"| `{name}` | `{target['sha256'] if target else 'none'}` | "
                         f"{cell(target['git'] + (' +/- ' + target['numstat'] if target['numstat'] else '')) if target else 'none'} | {now} |")
    else:
        lines.append("No baseline yet.")
    lines += ["", "## 3. Mutants", "", "| ID | File:line | Behaviour changed | Detecting test (kind) | Result |",
              "|---|---|---|---|---|"]
    for row in state["rows"]:
        mutant, record = row["mutant"], row["record"]
        detect = "; ".join(f"{entry['class']}{'.' + entry['method'] if entry['method'] else ''} ({entry['kind']})"
                           for entry in mutant["detect"])
        if record is None:
            result = row["why"]
        else:
            shown = record["qualifying"][0] if record["qualifying"] else (
                record["unconfirmed"][0] if record["unconfirmed"] else None)
            evidence = f"; {shown['test']} - {shown['exception']}: {shown['message']}" if shown else ""
            judged = f"; judged INVALID: {row['judged']}" if row["judged"] else ""
            restored = "restored and verified" if record.get("restored") else "NO RESTORATION RECORD"
            result = f"**{row['verdict']}** - {record['reason']}{evidence}{judged}; {restored}"
        lines.append(f"| {mutant['id']} | {cell(', '.join(mutant['locations']))} | {cell(mutant['behaviour'])} | "
                     f"{cell(detect)} | {cell(result)} |")
    totals = state["totals"]
    lines += ["", "## 4. Totals", "", "```", f"{totals['CAUGHT']} / {len(state['rows'])} CAUGHT",
              f"{totals['SURVIVED']} SURVIVED", f"{totals['INVALID']} INVALID", "```"]
    if state["missing"]:
        lines.append(f"{len(state['missing'])} not run or stale; they count as nothing.")
    restored = sum(1 for row in state["rows"] if row["record"] and row["record"].get("restored"))
    lines += ["", "## 5. Restoration and hash verification", ""]
    mismatched = verify_targets(baseline, analysis["targets"]) if baseline else ["no baseline"]
    lines += [f"- Every mutated production file matches its baseline SHA-256: "
              f"{'yes' if not mismatched else 'NO: ' + '; '.join(mismatched)}.",
              f"- Mutants with a verified restoration record: {restored} of {sum(1 for row in state['rows'] if row['record'])} run.",
              f"- In-flight journal: {'PRESENT (run recover)' if campaign.journal.is_file() else 'none'}; runner lock: "
              f"{'PRESENT' if campaign.lock.is_file() else 'none'}.",
              f"- Batches: {len(state['batches'])}; git status changed during "
              f"{sum(1 for batch in state['batches'] if not batch.get('git_status_unchanged'))} of them."]
    lines += ["", "## 6. Fresh-database evidence", ""]
    schema_rows = [row for row in state["rows"] if row["mutant"]["schema"]]
    if not schema_rows:
        lines.append("No migration or schema mutant in this campaign.")
    for row in schema_rows:
        mutant, record = row["mutant"], row["record"]
        fresh = [item["test"] for item in (record["qualifying"] if record else []) if item.get("fresh")]
        if mutant["evidence"] == "static":
            detail = f"static guard vacuity; schema behaviour proven by {mutant['covered_by']}"
        else:
            detail = ("fresh database: " + ", ".join(fresh)) if fresh else "NO fresh-database detection"
        lines.append(f"- {mutant['id']} ({', '.join(mutant['files'])}): {detail}")
    lines += ["", "## 7. Remaining gaps", ""] + ([f"- {cell(gap)}" for gap in state["gaps"]] or ["- none"])
    lines += ["", "## 8. Status", "", f"**{'READY' if state['ready'] else 'NOT READY'}**"
              + ("" if state["ready"] else ": " + "; ".join(state["reasons"]))]
    return "\n".join(lines) + "\n"


def command_status(campaign, _arguments):
    errors, analysis = analyse(campaign)
    state = evaluate(campaign, errors, analysis)
    if state["ready"]:
        entries = {name: state["baseline"]["targets"][name]["sha256"] for name in analysis["targets"]}
        for row in state["rows"]:
            entries.update(row["record"]["tests"])
        lines = [f"{digest} *{name}" for name, digest in sorted(entries.items())]
        lines.append(f"{sha256_bytes(campaign.raw)} *{campaign.plan_path.as_posix()}")
        campaign.manifest.write_bytes(("\n".join(lines) + "\n").encode("utf-8"))
        note = f"Gate-closure manifest: `{campaign.manifest.as_posix()}` ({len(lines)} files; `sha256sum -c` from the repository root)."
    else:
        campaign.manifest.unlink(missing_ok=True)
        note = "Gate-closure manifest: not written, and any earlier one removed, because the evidence is NOT READY."
    text = render(campaign, errors, analysis, state) + "\n" + note + "\n"
    campaign.dir.mkdir(parents=True, exist_ok=True)
    campaign.report_file.write_bytes(text.encode("utf-8"))
    print(text)
    return 0 if state["ready"] else 1


def main(argv=None):
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("command", choices=("check", "baseline", "run", "recover", "status"))
    parser.add_argument("ids", nargs="*", help="run: the mutants to run, in plan order")
    parser.add_argument("--plan", required=True)
    parser.add_argument("--max", type=int, default=DEFAULT_MAX, help=f"run: batch size, 1 to {HARD_MAX}")
    arguments = parser.parse_args(argv)
    if arguments.ids and arguments.command != "run":
        parser.error("mutant ids are accepted only by run")
    handlers = {"check": command_check, "baseline": command_baseline, "run": command_run,
                "recover": command_recover, "status": command_status}
    try:
        return handlers[arguments.command](Campaign(arguments.plan), arguments)
    except Refusal as refusal:
        print(f"REFUSED: {refusal}")
        return 1
    except RestoreFailure as failure:
        print(f"RESTORE FAILED: {failure}")
        print("Stop everything. The journal and the lock are kept; run recover and verify each file's SHA-256.")
        return 2


if __name__ == "__main__":
    sys.exit(main())
