"""Dry-run tests for the Fully session status hook. Repository cases run against temporary git repositories; services are
local fake listeners or closed ports; every process and socket the hook opens is recorded; nothing is fetched, written or
started, and no provider is contacted.

Run from the repository root: python .claude/hooks/test_fully_session_status.py [--configured]
"""
import ast
import importlib.util
import json
import os
import re
import shutil
import socket
import stat
import statistics
import subprocess
import sys
import tempfile
import threading
import time

sys.dont_write_bytecode = True

HERE = os.path.dirname(os.path.abspath(__file__))
HOOK = os.path.join(HERE, "fully_session_status.py")
PROJECT = os.path.dirname(os.path.dirname(HERE))
SETTINGS = os.path.join(PROJECT, ".claude", "settings.local.json")
FROZEN = ["docs/canonical/PRD-v1.md", "docs/canonical/SYSTEM-ARCHITECTURE-v1.md",
          "docs/canonical/ARCHITECTURE-INVARIANTS-v1.md", "docs/canonical/DEVELOPMENT-GATES-v1.md",
          "docs/premium/FULLY-PREMIUM-ARCHITECTURE-v1.md", "docs/premium/PREMIUM-POLICIES-v1.md"]
GATE_DOC = "docs/gates/gate-3-scope.md"
SERVICE_KEYS = ("POSTGRES_TEST_HOST", "POSTGRES_PORT", "EMULATOR_HTTP_PORT", "ACCEPT_EULA",
                "SERVICEBUS_TEST_CONNECTION_STRING", "FULLY_STATUS_ROOT")
FIELDS = ["Repository", "Branch", "HEAD", "origin/main", "Working tree", "Gate", "PostgreSQL", "Service Bus emulator",
          "Frozen docs"]
SSL_REQUEST = b"\x00\x00\x00\x08\x04\xd2\x16\x2f"
GIT_PREFIX = ["git", "--no-optional-locks", "-c", "core.fsmonitor=false", "-C"]
READ_ONLY_GIT = ("status", "rev-parse", "rev-list")
MUTATING_GIT = {"fetch", "pull", "push", "commit", "add", "reset", "checkout", "switch", "stash", "merge", "rebase", "gc",
                "clean", "rm", "mv", "restore", "update-index", "update-ref", "remote", "ls-remote", "submodule", "config"}
SCRATCH = []
RESULTS = []


def check(description, ok, detail=""):
    RESULTS.append(bool(ok))
    print(f"{'PASS' if ok else 'FAIL'} {description}" + ("" if ok else f"   [{detail}]"))


# --------------------------------------------------------------------------------------------- scratch helpers

def remove_tree(path):
    def writable(function, target, _):
        os.chmod(target, stat.S_IWRITE)
        function(target)
    if os.path.exists(path):
        shutil.rmtree(path, onexc=writable)


def git(root, *arguments, checked=True):
    completed = subprocess.run(["git", "-C", root, "-c", "user.name=status-test", "-c", "user.email=status@test.invalid",
                                "-c", "core.autocrlf=false", *arguments], check=checked, capture_output=True, timeout=60)
    return completed.stdout.decode().strip()


def write(root, relative, content):
    path = os.path.join(root, *relative.split("/"))
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as handle:
        handle.write(content if isinstance(content, bytes) else content.encode("utf-8"))
    return path


def project_bytes(relative):
    with open(os.path.join(PROJECT, *relative.split("/")), "rb") as handle:
        return handle.read()


def scratch_directory():
    path = tempfile.mkdtemp(prefix="fully-status-")
    SCRATCH.append(path)
    return path


def closed_port():
    probe = socket.socket()
    probe.bind(("127.0.0.1", 0))
    port = probe.getsockname()[1]
    probe.close()
    return port


class FakeService:
    """A loopback listener. postgres answers the SSLRequest with N; health and health503 answer an HTTP status line;
    silent closes at once without answering; hang never answers."""

    def __init__(self, kind):
        self.kind = kind
        self.received = []
        self.server = socket.socket()
        self.server.bind(("127.0.0.1", 0))
        self.server.listen(16)
        self.port = self.server.getsockname()[1]
        threading.Thread(target=self.serve, daemon=True).start()

    def serve(self):
        while True:
            try:
                connection, _ = self.server.accept()
            except OSError:
                return
            threading.Thread(target=self.handle, args=(connection,), daemon=True).start()

    def handle(self, connection):
        with connection:
            connection.settimeout(5)
            try:
                if self.kind == "silent":
                    return
                self.received.append(connection.recv(1024))
                if self.kind == "postgres":
                    connection.sendall(b"N")
                elif self.kind == "health":
                    connection.sendall(b"HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n")
                elif self.kind == "health503":
                    connection.sendall(b"HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\n\r\n")
                while connection.recv(64):
                    pass
            except OSError:
                pass

    def close(self):
        self.server.close()


def repository(postgres_port, emulator_port, frozen=True, gate=True, accept="Y", extra_env=""):
    """A temporary git repository laid out like Fully, with origin/main at HEAD and a git-ignored local .env."""
    root = scratch_directory()
    git(root, "init", "-q", "-b", "main")
    write(root, ".gitignore", ".env\n")
    write(root, ".env", f"POSTGRES_PORT={postgres_port}\nEMULATOR_HTTP_PORT={emulator_port}\nACCEPT_EULA={accept}\n"
                        f"POSTGRES_PASSWORD=never-printed-secret\n{extra_env}")
    write(root, "src/a.cs", "a\n")
    write(root, "src/b.cs", "b\n")
    for relative in FROZEN if frozen else []:
        write(root, relative, project_bytes(relative))
    if gate:
        write(root, GATE_DOC, project_bytes(GATE_DOC))
    git(root, "add", "-A")
    git(root, "commit", "-q", "-m", "initial")
    # An origin that is never contacted, so that a branch's upstream resolves to its refs/remotes/origin/* ref.
    git(root, "remote", "add", "origin", "../no-such-remote.git")
    git(root, "update-ref", "refs/remotes/origin/main", "HEAD")
    return root


def clean_environment(**overrides):
    environment = {key: value for key, value in os.environ.items() if key not in SERVICE_KEYS}
    environment.update(overrides)
    return environment


def parse(text):
    fields = {}
    for line in text.splitlines()[1:]:
        key, _, value = line.partition(": ")
        fields[key] = value
    return fields


def run_hook(root=None, **overrides):
    environment = clean_environment(**overrides)
    if root is not None:
        environment["FULLY_STATUS_ROOT"] = root
    started = time.perf_counter()
    result = subprocess.run([sys.executable, "-I", "-S", HOOK], capture_output=True, env=environment, timeout=30)
    elapsed = (time.perf_counter() - started) * 1000
    text = result.stdout.decode("utf-8", "replace")
    return result.returncode, text, parse(text), elapsed, result.stderr


def snapshot(root):
    state = {}
    for directory, _, names in os.walk(root):
        for name in names:
            path = os.path.join(directory, name)
            status = os.stat(path)
            state[path] = (status.st_size, status.st_mtime_ns)
    return state


def load_hook():
    spec = importlib.util.spec_from_file_location("fully_session_status", HOOK)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def static_view(source):
    """Imports, module attributes used, open() modes and non-docstring string constants of the hook's code."""
    tree = ast.parse(source)
    docstrings = {id(node.body[0].value) for node in ast.walk(tree)
                  if isinstance(node, (ast.Module, ast.FunctionDef, ast.ClassDef)) and node.body
                  and isinstance(node.body[0], ast.Expr) and isinstance(node.body[0].value, ast.Constant)
                  and isinstance(node.body[0].value.value, str)}
    imports, attributes, opens, strings = set(), {}, [], []
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            imports |= {alias.name for alias in node.names}
        elif isinstance(node, ast.ImportFrom):
            imports.add(node.module or "")
        elif isinstance(node, ast.Attribute) and isinstance(node.value, ast.Name):
            attributes.setdefault(node.value.id, set()).add(node.attr)
        elif isinstance(node, ast.Constant) and isinstance(node.value, str) and id(node) not in docstrings:
            strings.append(node.value)
        if isinstance(node, ast.Call) and isinstance(node.func, ast.Name) and node.func.id == "open":
            mode = node.args[1] if len(node.args) > 1 else next((k.value for k in node.keywords if k.arg == "mode"), None)
            opens.append(mode.value if isinstance(mode, ast.Constant) else None)
    return imports, attributes, opens, strings


# --------------------------------------------------------------------------------------------- cases

def main():
    postgres, health = FakeService("postgres"), FakeService("health")
    unhealthy, silent, hang = FakeService("health503"), FakeService("silent"), FakeService("hang")
    down = closed_port()
    try:
        # 1 clean repository: every field, in order; alignment; zero counts; services up; frozen set intact.
        root = repository(postgres.port, health.port)
        code, text, fields, _, errors = run_hook(root)
        head = git(root, "rev-parse", "--short=7", "HEAD")
        check("1 clean repository: branch main, HEAD, origin/main aligned and 0 modified / 0 staged / 0 untracked",
              code == 0 and fields.get("Branch") == "main" and fields.get("HEAD") == head
              and fields.get("origin/main") == "aligned (local ref, not fetched)"
              and fields.get("Working tree") == "0 modified / 0 staged / 0 untracked", text)
        check("the snapshot is headed FULLY SESSION STATUS and has exactly the nine fields, in order, and no stderr",
              text.splitlines()[0] == "FULLY SESSION STATUS" and list(fields) == FIELDS and not errors, (text, errors))
        check("Repository is the repository path", fields.get("Repository", "").lower() == os.path.realpath(root).lower(),
              fields.get("Repository"))
        check("the gate status is derived from the gate document's status table",
              fields.get("Gate") == "3F PASS / CLOSED; PKCE verifier cleanup PASS / CLOSED; 3G NEXT / NOT STARTED",
              fields.get("Gate"))
        check("5 PostgreSQL healthy: a local server answering the PostgreSQL SSLRequest",
              fields.get("PostgreSQL") == f"healthy (PostgreSQL answering on 127.0.0.1:{postgres.port})"
              and postgres.received[-1:] == [SSL_REQUEST], (fields.get("PostgreSQL"), postgres.received[-1:]))
        check("Service Bus emulator available: its health endpoint answers 200",
              fields.get("Service Bus emulator") == f"available (health endpoint answers 200 on 127.0.0.1:{health.port})"
              and health.received[-1:] and health.received[-1].startswith(b"GET /health HTTP/1.0\r\n"),
              fields.get("Service Bus emulator"))
        check("frozen documents identical to the frozen set: 6/6 unchanged", fields.get("Frozen docs") == "6/6 unchanged",
              fields.get("Frozen docs"))
        check("no .env value is printed", "never-printed-secret" not in text, text)

        # 2 counts: modified, staged (one staged and modified again), untracked, then unmerged.
        write(root, "src/a.cs", "a changed\n")
        write(root, "src/b.cs", "b staged\n")
        git(root, "add", "src/b.cs")
        write(root, "src/b.cs", "b staged then changed again\n")
        write(root, "src/new1.cs", "x\n")
        write(root, "notes/new2.md", "y\n")
        write(root, "notes/new3.md", "z\n")
        tree = run_hook(root)[2].get("Working tree")
        check("2 counts: 2 modified / 1 staged / 3 untracked (untracked counted per file, inside new folders too)",
              tree == "2 modified / 1 staged / 3 untracked", tree)
        git(root, "stash", "-q", "-u")
        conflict = repository(down, down)
        git(conflict, "checkout", "-q", "-b", "other")
        write(conflict, "src/a.cs", "other\n")
        git(conflict, "commit", "-q", "-am", "other")
        git(conflict, "checkout", "-q", "main")
        write(conflict, "src/a.cs", "main\n")
        git(conflict, "commit", "-q", "-am", "main")
        git(conflict, "merge", "-q", "other", checked=False)
        tree = run_hook(conflict)[2].get("Working tree")
        check("2 a merge conflict is counted as unmerged", tree == "0 modified / 0 staged / 0 untracked / 1 unmerged", tree)

        # 3 detached and non-main branches.
        git(root, "checkout", "-q", "-b", "feature/x")
        check("3 a non-main branch is named", run_hook(root)[2].get("Branch") == "feature/x")
        write(root, "src/e.cs", "e\n")
        git(root, "add", "src/e.cs")
        git(root, "commit", "-q", "-m", "feature")
        git(root, "update-ref", "refs/remotes/origin/feature/x", "HEAD")
        git(root, "config", "branch.feature/x.remote", "origin")
        git(root, "config", "branch.feature/x.merge", "refs/heads/feature/x")
        check("3 precondition: git status reports feature/x tracking origin/feature/x, level with it",
              "# branch.upstream origin/feature/x\n# branch.ab +0 -0" in git(root, "status", "--porcelain=v2", "--branch"))
        state = run_hook(root)[2].get("origin/main")
        check("3 a branch tracking another upstream is still compared with origin/main",
              state == "ahead 1 (local ref, not fetched)", state)
        git(root, "checkout", "-q", "--detach")
        fields = run_hook(root)[2]
        check("3 a detached HEAD is reported, with its commit", fields.get("Branch") == "(detached HEAD)"
              and fields.get("HEAD") == git(root, "rev-parse", "--short=7", "HEAD") != head, fields)
        git(root, "checkout", "-q", "main")

        # 4 HEAD against origin/main, by rev-list (no upstream) and by status (upstream origin/main, as in Fully).
        base = git(root, "rev-parse", "HEAD")
        for upstream in (False, True):
            path = "branch.ab" if upstream else "rev-list"
            if upstream:
                git(root, "config", "branch.main.remote", "origin")
                git(root, "config", "branch.main.merge", "refs/heads/main")
            git(root, "update-ref", "refs/remotes/origin/main", base)
            git(root, "reset", "-q", "--hard", base)
            write(root, "src/c.cs", f"c {upstream}\n")
            git(root, "add", "src/c.cs")
            git(root, "commit", "-q", "-m", "ahead")
            reported = "# branch.upstream origin/main\n# branch.ab +1 -0" in git(root, "status", "--porcelain=v2", "--branch")
            check(f"4 precondition: git status {'reports' if upstream else 'does not report'} main's upstream ({path})",
                  reported == upstream)
            state = run_hook(root)[2].get("origin/main")
            check(f"4 HEAD ahead of origin/main ({path})", state == "ahead 1 (local ref, not fetched)", state)
            git(root, "update-ref", "refs/remotes/origin/main", "HEAD")
            git(root, "reset", "-q", "--hard", base)
            state = run_hook(root)[2].get("origin/main")
            check(f"4 HEAD behind origin/main ({path})", state == "behind 1 (local ref, not fetched)", state)
            write(root, "src/d.cs", f"d {upstream}\n")
            git(root, "add", "src/d.cs")
            git(root, "commit", "-q", "-m", "diverge")
            state = run_hook(root)[2].get("origin/main")
            check(f"4 HEAD diverged from origin/main ({path})",
                  state == "diverged, ahead 1 and behind 1 (local ref, not fetched)", state)
        git(root, "update-ref", "-d", "refs/remotes/origin/main")
        state = run_hook(root)[2].get("origin/main")
        check("4 no local origin/main ref is UNKNOWN", state == "UNKNOWN (no local origin/main ref)", state)

        # 6 and 7 unavailable services are informational only.
        quiet = repository(down, down)
        code, text, fields, _, _ = run_hook(quiet)
        check("6 PostgreSQL unavailable is informational: exit 0, every other field still reported",
              code == 0 and fields.get("PostgreSQL") == f"unavailable (no answer on 127.0.0.1:{down}; the database tests "
              "need the local stack)" and fields.get("Branch") == "main" and fields.get("Frozen docs") == "6/6 unchanged",
              text)
        check("7 Service Bus emulator unavailable is informational, and says the .env expects it",
              code == 0 and fields.get("Service Bus emulator") == f"unavailable (no answer on 127.0.0.1:{down}; expected "
              "here, so the emulator tests run)", fields.get("Service Bus emulator"))
        state = run_hook(quiet, POSTGRES_PORT=str(postgres.port))[2].get("PostgreSQL")
        check("5 an environment variable wins over .env, as LocalEnvironment resolves the test database",
              state == f"healthy (PostgreSQL answering on 127.0.0.1:{postgres.port})", state)
        state = run_hook(repository(silent.port, unhealthy.port))[2]
        check("6 an open port with no PostgreSQL behind it is unavailable",
              state.get("PostgreSQL") == f"unavailable (127.0.0.1:{silent.port} accepted a connection but gave no "
              "PostgreSQL answer)", state.get("PostgreSQL"))
        check("7 a health endpoint answering 503 is unavailable",
              state.get("Service Bus emulator", "").startswith(f"unavailable (health endpoint on 127.0.0.1:{unhealthy.port} "
                                                               "answered 503"), state.get("Service Bus emulator"))
        state = run_hook(repository(down, silent.port))[2].get("Service Bus emulator")
        check("7 an open port with no emulator behind it is unavailable",
              state == f"unavailable (127.0.0.1:{silent.port} accepted a connection but gave no HTTP answer; expected "
              "here, so the emulator tests run)", state)
        unlicensed = repository(down, down, accept="N")
        state = run_hook(unlicensed, ACCEPT_EULA="Y")[2].get("Service Bus emulator")
        check("7 without .env ACCEPT_EULA=Y the emulator is not expected, even with a process-level ACCEPT_EULA=Y",
              state == f"unavailable (no answer on 127.0.0.1:{down}; not expected here, so the emulator tests skip)", state)
        state = run_hook(unlicensed, SERVICEBUS_TEST_CONNECTION_STRING="Endpoint=sb://emulator.example;"
                         "SharedAccessKey=never-printed-key;")[2].get("Service Bus emulator", "")
        check("7 a test connection string naming a non-local host is UNKNOWN, expected, and its key is not printed",
              state.startswith("UNKNOWN (the test connection string names a host that is not local; not probed; expected")
              and "never-printed-key" not in state, state)

        # 8 frozen document mismatches are reported clearly, with names.
        write(quiet, "docs/canonical/PRD-v1.md", "edited\n")
        write(quiet, "docs/canonical/DEVELOPMENT-GATES-v1.md",
              project_bytes("docs/canonical/DEVELOPMENT-GATES-v1.md").replace(b"\n", b"\r\n"))
        os.remove(os.path.join(quiet, *"docs/premium/PREMIUM-POLICIES-v1.md".split("/")))
        frozen = run_hook(quiet)[2].get("Frozen docs")
        check("8 a frozen document mismatch is reported clearly, by name",
              frozen == "3/6 unchanged; CHANGED: docs/canonical/PRD-v1.md; LINE ENDINGS ONLY: "
              "docs/canonical/DEVELOPMENT-GATES-v1.md; MISSING OR UNREADABLE: docs/premium/PREMIUM-POLICIES-v1.md", frozen)

        # 9 missing or unusable status documentation is UNKNOWN, never a failure; the gate line follows the document.
        bare = repository(down, down, frozen=False, gate=False)
        code, text, fields, _, _ = run_hook(bare)
        check("9 a missing gate document gives UNKNOWN, exit 0",
              code == 0 and fields.get("Gate") == f"UNKNOWN ({GATE_DOC} is missing or unreadable)", text)
        check("9 missing frozen documents are reported, not raised",
              fields.get("Frozen docs", "").startswith("0/6 unchanged; MISSING OR UNREADABLE: "), fields.get("Frozen docs"))
        write(bare, GATE_DOC, "# Gate 3\n\nNo status table here.\n")
        state = run_hook(bare)[2].get("Gate")
        check("9 a gate document without a status table gives UNKNOWN",
              state == f"UNKNOWN (no sub-gate status table in {GATE_DOC})", state)
        dash = "\u2014"
        table = ("# Gate 3\n\n| Item | Status |\n|---|---|\n"
                 f"| Gate 3 {dash} monday Connector | **AUTHORIZED 2026-09-29, IN PROGRESS** |\n"
                 f"| 3F {dash} Initial sync | **PASS** {dash} approved by the product owner on 2026-10-03 |\n"
                 f"| 3B maintenance before 3G {dash} PKCE verifier cleanup | **PASS** {dash} approved by the product owner "
                 "on 2026-10-03 |\n"
                 f"| 3G {dash} Real end-to-end acceptance | **{{state}}** {dash} {{note}} |\n"
                 "| Premium Gates P1 to P11 | Not started; not authorized |\n\nText.\n")
        write(bare, GATE_DOC, table.format(state="IN PROGRESS", note="authorized 2026-10-04"))
        state = run_hook(bare)[2].get("Gate")
        check("9 the gate line follows the document: 3G IN PROGRESS",
              state == "3F PASS / CLOSED; PKCE verifier cleanup PASS / CLOSED; 3G IN PROGRESS", state)
        write(bare, GATE_DOC, table.format(state="PASS", note="submitted for owner review"))
        state = run_hook(bare)[2].get("Gate")
        check("9 a PASS without owner approval is not reported as CLOSED",
              state == "PKCE verifier cleanup PASS / CLOSED; 3G PASS", state)
        nowhere = scratch_directory()
        code, text, fields, _, _ = run_hook(nowhere, POSTGRES_PORT=str(down), EMULATOR_HTTP_PORT=str(down))
        check("9 a folder that is not a git repository gives UNKNOWN git fields, exit 0",
              code == 0 and all(fields.get(key, "").startswith("UNKNOWN") for key in ("Branch", "HEAD", "origin/main",
                                                                                     "Working tree")), text)

        # Hard bound: services that accept and never answer cannot hold the session.
        stalled = repository(hang.port, hang.port)
        code, text, fields, elapsed, _ = run_hook(stalled)
        check(f"a stalled PostgreSQL and emulator are reported unavailable within the bound ({elapsed:.0f} ms < 2500 ms)",
              code == 0 and elapsed < 2500
              and fields.get("PostgreSQL") == f"unavailable (127.0.0.1:{hang.port} accepted a connection but gave no "
              "PostgreSQL answer)" and fields.get("Service Bus emulator") == f"unavailable (127.0.0.1:{hang.port} "
              "accepted a connection but gave no HTTP answer; expected here, so the emulator tests run)", (elapsed, text))

        # 10 no write: no file of a repository, .git and its index included, changes during runs.
        probe = repository(postgres.port, health.port)
        write(probe, "src/a.cs", "dirty\n")
        write(probe, "untracked.txt", "u\n")
        before = snapshot(probe)
        for _ in range(3):
            run_hook(probe)
        check("10 three runs change no file, .git and its index included", snapshot(probe) == before)

        # 10 no network or provider action: every process and socket the hook opens is recorded in-process.
        hook = load_hook()
        processes, attempts, loopback_open = [], [], [True]
        real_popen, real_connect = hook.subprocess.Popen, hook.socket.create_connection

        def recording_popen(arguments, *rest, **options):
            processes.append(list(arguments))
            return real_popen(arguments, *rest, **options)

        def recording_connect(address, *rest, **options):
            attempts.append(tuple(address))
            if not loopback_open[0] or address[0] not in ("127.0.0.1", "::1"):
                raise OSError("refused by the test recorder")  # nothing beyond the loopback is ever reached
            return real_connect(address, *rest, **options)

        remote = repository(down, down, extra_env="POSTGRES_TEST_HOST=db.example.com\n")
        saved = {key: os.environ.pop(key) for key in SERVICE_KEYS if key in os.environ}
        hook.subprocess.Popen, hook.socket.create_connection = recording_popen, recording_connect
        outputs, hosts, defaults, bounded, bounded_ms = [], [], [], {}, float("inf")
        try:
            for target in (probe, root, remote):
                os.environ["FULLY_STATUS_ROOT"] = target
                outputs.append(parse(hook.render()))
            hosts = [address[0] for address in attempts]
            os.environ.pop("FULLY_STATUS_ROOT")
            started = len(processes)
            hook.render()
            common_case = processes[started:]
            loopback_open[0] = False
            attempts.clear()
            hook.postgres_state({})
            hook.emulator_state({}, {})
            defaults = list(attempts)
            loopback_open[0] = True
            hook.ANSWER_TIMEOUT, hook.DEADLINE = 5.0, 0.5
            os.environ["FULLY_STATUS_ROOT"] = stalled
            started = time.perf_counter()
            bounded = parse(hook.render())
            bounded_ms = (time.perf_counter() - started) * 1000
        finally:
            hook.subprocess.Popen, hook.socket.create_connection = real_popen, real_connect
            hook.ANSWER_TIMEOUT, hook.DEADLINE = 0.8, 2.0
            os.environ.pop("FULLY_STATUS_ROOT", None)
            os.environ.update(saved)
        check("10 only read-only git queries run: status, rev-parse or rev-list behind the fixed read-only prefix",
              processes and all(p[:5] == GIT_PREFIX and p[6] in READ_ONLY_GIT and not MUTATING_GIT & set(p)
                                for p in processes), processes)
        check("10 sockets open to the loopback interface only", hosts and set(hosts) <= {"127.0.0.1", "::1"}, hosts)
        check("10 a non-local PostgreSQL host is not probed and is reported UNKNOWN",
              outputs[2].get("PostgreSQL") == "UNKNOWN (POSTGRES_TEST_HOST is not a local host; not probed)",
              outputs[2].get("PostgreSQL"))
        check("the common case, main tracking origin/main (the real repository), costs one git process: status",
              [p[6] for p in common_case] == ["status"], common_case)
        check("defaults: PostgreSQL on 127.0.0.1:5432 and the emulator health endpoint on 127.0.0.1:5300 (none reached)",
              defaults == [("127.0.0.1", 5432), ("127.0.0.1", 5300)], defaults)
        check(f"the overall deadline bounds probes that outlive their own timeouts ({bounded_ms:.0f} ms < 1500 ms)",
              bounded_ms < 1500 and bounded.get("PostgreSQL") == "UNKNOWN (no answer within 0.5 s)"
              and bounded.get("Service Bus emulator") == "UNKNOWN (no answer within 0.5 s)"
              and bounded.get("Branch") == "main", (bounded_ms, bounded))
        with open(HOOK, encoding="utf-8") as handle:
            imports, attributes, opens, strings = static_view(handle.read())
        check("10 the hook imports only local standard modules (no HTTP or provider client)",
              imports <= {"hashlib", "os", "re", "socket", "subprocess", "sys", "threading", "time"}, imports)
        check("10 sockets only through create_connection; processes only through subprocess.run",
              attributes.get("socket", set()) <= {"create_connection"}
              and attributes.get("subprocess", set()) <= {"run", "DEVNULL", "SubprocessError"}, attributes)
        check("10 no file is opened for writing and os is used only to read paths and the environment",
              opens and set(opens) == {"rb"} and attributes.get("os", set()) <= {"environ", "path"}, (opens, attributes))
        check("10 the code names no mutating git command, container tool, provider or web address",
              not MUTATING_GIT & set(strings) and not any(word in value.lower() for value in strings for word in (
                  "docker", "monday", "azure", "github", "http://", "https://")), strings)

        # The real repository, read-only: every field resolves and its index is untouched.
        index = os.path.join(PROJECT, ".git", "index")
        index_before = os.stat(index).st_mtime_ns
        times = []
        for _ in range(10):
            code, text, fields, elapsed, _ = run_hook()
            times.append(elapsed)
        check("the real repository: exit 0, no UNKNOWN field, frozen set 6/6, repository path",
              code == 0 and "UNKNOWN" not in text and fields.get("Frozen docs") == "6/6 unchanged"
              and fields.get("Repository", "").lower() == PROJECT.lower(), text)
        check("the real repository's git index is not rewritten", os.stat(index).st_mtime_ns == index_before)
        print("INFO real repository snapshot:\n    " + text.strip().replace("\n", "\n    "))
        print(f"INFO end-to-end time over 10 runs, interpreter start included: median {statistics.median(times):.0f} ms, "
              f"max {max(times):.0f} ms")

        if "--configured" in sys.argv:
            with open(SETTINGS, encoding="utf-8") as handle:
                settings = json.load(handle)
            entries = settings.get("hooks", {}).get("SessionStart", [])
            command = entries[0]["hooks"][0].get("command", "") if len(entries) == 1 else ""
            check("configured: one SessionStart entry for startup, resume, clear and compact, with a timeout",
                  len(entries) == 1 and entries[0].get("matcher") == "startup|resume|clear|compact"
                  and entries[0]["hooks"][0].get("timeout") == 10, entries)
            bash = next(p for p in ("C:/Program Files/Git/bin/bash.exe", "C:/Program Files/Git/usr/bin/bash.exe")
                        if os.path.exists(p))
            environment = clean_environment(FULLY_STATUS_ROOT=probe, CLAUDE_PROJECT_DIR=PROJECT)
            result = subprocess.run([bash, "-c", command], capture_output=True, env=environment, timeout=30)
            output = result.stdout.decode("utf-8", "replace")
            check("configured: the exact hook command runs through Git Bash and prints the snapshot",
                  result.returncode == 0 and list(parse(output)) == FIELDS and "Branch: main" in output, output[-400:])
            # The interpreter is whatever the local settings wire: the first word of the command that names python.
            interpreter = re.search(r"[^\"'\s=]*python[^\"'\s]*", command, re.IGNORECASE)
            broken = command.replace(interpreter.group(0), "C:/no-such-directory/python.exe") if interpreter else command
            result = subprocess.run([bash, "-c", broken], capture_output=True, env=environment, timeout=30)
            output = result.stdout.decode("utf-8", "replace")
            check("configured: without its interpreter the hook still exits 0 and reports UNKNOWN",
                  broken != command and result.returncode == 0 and output.startswith("FULLY SESSION STATUS")
                  and "UNKNOWN" in output, output)
    finally:
        for service in (postgres, health, unhealthy, silent, hang):
            service.close()
        for path in SCRATCH:
            remove_tree(path)

    leftovers = [path for path in SCRATCH if os.path.exists(path)]
    check(f"every scratch directory was removed ({len(SCRATCH)} created)", not leftovers, leftovers)
    print(f"\n{sum(RESULTS)}/{len(RESULTS)} passed")
    return 0 if all(RESULTS) else 1


if __name__ == "__main__":
    sys.exit(main())
