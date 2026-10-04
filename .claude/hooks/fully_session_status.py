"""Fully session status: a project-local Claude Code SessionStart hook (startup, resume, clear and compact).

It prints a short, read-only snapshot that Claude Code adds to the session context:

  Repository            this repository's path
  Branch, HEAD          the current branch, or (detached HEAD), and the short commit id
  origin/main           HEAD against the local origin/main ref: aligned, ahead, behind or diverged; nothing is fetched
  Working tree          modified, staged and untracked file counts, from git status --porcelain=v2
  Gate                  from the status table of docs/gates/gate-3-scope.md: the latest PASS sub-gate, every maintenance
                        item and every sub-gate not yet PASS; a PASS approved by the product owner reads PASS / CLOSED
  PostgreSQL            whether a PostgreSQL server answers where the database tests connect (POSTGRES_TEST_HOST and
                        POSTGRES_PORT from the environment or .env, as LocalEnvironment resolves them; local hosts only)
  Service Bus emulator  whether its health endpoint answers 200 (what eng/dev.ps1 waits for), and whether the tests
                        expect an emulator here (LocalEnvironment: .env ACCEPT_EULA=Y or a test connection string)
  Frozen docs           the SHA-256 of the six frozen documents against the frozen set; names appear only on a mismatch

It only reads. git runs status, rev-parse and rev-list only, with --no-optional-locks so the index is never rewritten,
and with the file-system monitor off. Nothing is fetched, pulled, pushed, staged, built, tested, migrated, started or
repaired, and no remote host or provider is contacted: the two probes connect to the loopback interface only, with
short timeouts, and the whole snapshot is bounded at about two seconds. Whatever cannot be determined cheaply is
UNKNOWN, an unavailable service is reported rather than raised, no .env value is printed, and the hook always exits 0.
Dry-run tests: .claude/hooks/test_fully_session_status.py.
"""
import hashlib
import os
import re
import socket
import subprocess
import sys
import threading
import time

# The frozen set: each frozen document's SHA-256 as committed (Core v1 documents; Premium documents frozen at P0).
FROZEN_DOCUMENTS = (
    ("docs/canonical/PRD-v1.md", "f8f2dc7bc9077a4a881a068114fbc11c7e1571a94b9b9df920c1c15c8bdca326"),
    ("docs/canonical/SYSTEM-ARCHITECTURE-v1.md", "1cc196980f6e3f32955ea0fc348312b5805e2c312b6a7048abfd2c2c6192fa47"),
    ("docs/canonical/ARCHITECTURE-INVARIANTS-v1.md", "5ad268734d072bf21cbb8fc02abbe673f6c8c4c5f31fb700681450f0f9b234b7"),
    ("docs/canonical/DEVELOPMENT-GATES-v1.md", "c30f5c8bc1e4e09ebea76501fbe8aef36ae99e9027881f381b7f06f2da2a8686"),
    ("docs/premium/FULLY-PREMIUM-ARCHITECTURE-v1.md", "cec4a849d3e93716d27f516ef75232c8fb74a7aa6d9b1d370a6395ff54ae1bb3"),
    ("docs/premium/PREMIUM-POLICIES-v1.md", "a903f9ec2702159780c72ce9ca9db0d5b650ab91f077ac0fe0694e2332185221"),
)
GATE_DOCUMENT = "docs/gates/gate-3-scope.md"
GATE_READ_LIMIT = 64 * 1024  # the status table opens the document
DASH = " — "
SETTING_KEYS = ("POSTGRES_TEST_HOST", "POSTGRES_PORT", "EMULATOR_HTTP_PORT", "ACCEPT_EULA",
                "SERVICEBUS_TEST_CONNECTION_STRING")
LOOPBACK = {"localhost": "127.0.0.1", "127.0.0.1": "127.0.0.1", "::1": "::1"}
SSL_REQUEST = b"\x00\x00\x00\x08\x04\xd2\x16\x2f"  # every PostgreSQL server answers it with S or N
HEALTH_REQUEST = b"GET /health HTTP/1.0\r\nHost: localhost\r\n\r\n"
CONNECT_TIMEOUT = 0.3  # Windows refuses a closed loopback port only after about two seconds
ANSWER_TIMEOUT = 0.8
DEADLINE = 2.0
GIT_FIELDS = ("Branch", "HEAD", "origin/main", "Working tree")
COMMIT = re.compile(r"[0-9a-f]{40}|[0-9a-f]{64}")


def repository_root():
    root = os.environ.get("FULLY_STATUS_ROOT") or os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    return os.path.realpath(root)


def local_settings(root):
    """The .env entries the tests read and the effective settings, where a non-blank environment variable wins over
    .env (LocalEnvironment.Read). Only the keys in SETTING_KEYS are kept; no password is ever held."""
    dot_env = {}
    try:
        with open(os.path.join(root, ".env"), "rb") as handle:
            lines = handle.read(64 * 1024).decode("utf-8-sig", "replace").splitlines()
    except OSError:
        lines = []
    for line in lines:
        key, separator, value = line.strip().partition("=")
        if separator and key.strip() in SETTING_KEYS:  # a commented-out line's key starts with # and never matches
            dot_env[key.strip()] = value.strip()
    settings = dict(dot_env)
    for key in SETTING_KEYS:
        if os.environ.get(key, "").strip():
            settings[key] = os.environ[key].strip()
    return dot_env, settings


def git(root, deadline, *arguments):
    """A read-only git query's output, or None when git is missing, fails or runs out of time."""
    try:
        completed = subprocess.run(["git", "--no-optional-locks", "-c", "core.fsmonitor=false", "-C", root, *arguments],
                                   stdin=subprocess.DEVNULL, capture_output=True,
                                   timeout=max(0.1, deadline - time.monotonic()))
    except (OSError, subprocess.SubprocessError):
        return None
    return completed.stdout.decode("utf-8", "replace") if completed.returncode == 0 else None


def git_state(root, deadline):
    status = git(root, deadline, "status", "--porcelain=v2", "--branch", "--untracked-files=all")
    if status is None:
        return dict.fromkeys(GIT_FIELDS, "UNKNOWN (no git repository here, or git did not answer)")
    headers, modified, staged, untracked, unmerged = {}, 0, 0, 0, 0
    for line in status.splitlines():
        if line.startswith("# "):
            key, _, value = line[2:].partition(" ")
            headers[key] = value
        elif line[:2] in ("1 ", "2 "):
            staged += line[2] != "."
            modified += line[3] != "."
        elif line.startswith("u "):
            unmerged += 1
        elif line.startswith("? "):
            untracked += 1
    oid, head = headers.get("branch.oid", ""), headers.get("branch.head", "")
    tree = f"{modified} modified / {staged} staged / {untracked} untracked" + (f" / {unmerged} unmerged" if unmerged else "")
    return {
        "Branch": "(detached HEAD)" if head == "(detached)" else head or "UNKNOWN",
        "HEAD": oid[:7] if COMMIT.fullmatch(oid) else "UNKNOWN (no commit yet)",
        "origin/main": origin_main(root, deadline, headers, oid),
        "Working tree": tree,
    }


def origin_main(root, deadline, headers, oid):
    """HEAD against the local origin/main ref, which is only as fresh as the last fetch; nothing is fetched here."""
    if not COMMIT.fullmatch(oid):
        return "UNKNOWN (no commit yet)"
    counts = re.fullmatch(r"\+(\d+) -(\d+)", headers.get("branch.ab", ""))
    if headers.get("branch.upstream") != "origin/main" or not counts:
        remote = (git(root, deadline, "rev-parse", "--verify", "--quiet", "refs/remotes/origin/main") or "").strip()
        if not COMMIT.fullmatch(remote):
            return "UNKNOWN (no local origin/main ref)"
        compared = "0\t0" if remote == oid else git(root, deadline, "rev-list", "--left-right", "--count",
                                                     "HEAD...refs/remotes/origin/main")
        counts = re.fullmatch(r"(\d+)\s+(\d+)\s*", compared or "")
        if not counts:
            return "UNKNOWN (git could not compare HEAD with origin/main)"
    ahead, behind = int(counts.group(1)), int(counts.group(2))
    if ahead and behind:
        state = f"diverged, ahead {ahead} and behind {behind}"
    else:
        state = f"ahead {ahead}" if ahead else f"behind {behind}" if behind else "aligned"
    return f"{state} (local ref, not fetched)"


def gate_state(root):
    """The status table's latest PASS sub-gate, every maintenance item and every sub-gate not yet PASS, in table order."""
    try:
        with open(os.path.join(root, *GATE_DOCUMENT.split("/")), "rb") as handle:
            text = handle.read(GATE_READ_LIMIT).decode("utf-8", "replace")
    except OSError:
        return f"UNKNOWN ({GATE_DOCUMENT} is missing or unreadable)"
    rows, in_table = [], False
    for line in (line.strip() for line in text.splitlines()):
        if not in_table:
            in_table = re.fullmatch(r"\|\s*Item\s*\|\s*Status\s*\|", line) is not None
        elif not line.startswith("|"):
            break
        elif not line.startswith("|-"):
            item, _, status = line.strip("|").partition("|")
            rows.append((item.strip(), status.strip()))
    selected, latest_pass = {}, None
    for index, (item, status) in enumerate(rows):
        marked = re.match(r"\*\*(.+?)\*\*", status)
        state = (marked.group(1) if marked else status.split(";")[0]).strip()
        if state == "PASS" and "approved by the product owner" in status:
            state = "PASS / CLOSED"
        sub_gate = re.match(r"(\d+[A-Z])" + DASH, item)
        if "maintenance" in item.lower():
            selected[index] = f"{item.split(DASH, 1)[-1]} {state}"
        elif sub_gate and state.startswith("PASS"):
            latest_pass = index, f"{sub_gate.group(1)} {state}"
        elif sub_gate:
            selected[index] = f"{sub_gate.group(1)} {state}"
    if latest_pass:
        selected[latest_pass[0]] = latest_pass[1]
    if not selected:
        return f"UNKNOWN (no sub-gate status table in {GATE_DOCUMENT})"
    return "; ".join(selected[index] for index in sorted(selected))


def answer(address, port, request, enough):
    """The first bytes a loopback service answers to one request: None when nothing accepts the connection, b"" when
    it is accepted but closed, reset or left unanswered (a published port whose service is not ready)."""
    try:
        connection = socket.create_connection((address, port), timeout=CONNECT_TIMEOUT)
    except OSError:
        return None
    with connection:
        stop, data = time.monotonic() + ANSWER_TIMEOUT, b""
        try:
            connection.sendall(request)
            while not enough(data):
                connection.settimeout(max(0.01, stop - time.monotonic()))
                chunk = connection.recv(64)
                if not chunk:
                    break
                data += chunk
        except OSError:
            pass
        return data


def port_number(value, default):
    try:
        port = int(value or default)
    except ValueError:
        return None
    return port if 0 < port < 65536 else None


def endpoint(address, port):
    return f"[{address}]:{port}" if ":" in address else f"{address}:{port}"


def postgres_state(settings):
    address = LOOPBACK.get((settings.get("POSTGRES_TEST_HOST") or "localhost").lower().strip("[]"))
    port = port_number(settings.get("POSTGRES_PORT"), 5432)
    if address is None:
        return "UNKNOWN (POSTGRES_TEST_HOST is not a local host; not probed)"
    if port is None:
        return "UNKNOWN (POSTGRES_PORT is not a port number)"
    where = endpoint(address, port)
    data = answer(address, port, SSL_REQUEST, lambda data: len(data) >= 1)
    if data is None:
        return f"unavailable (no answer on {where}; the database tests need the local stack)"
    if data[:1] in (b"S", b"N"):
        return f"healthy (PostgreSQL answering on {where})"
    return f"unavailable ({where} accepted a connection but gave no PostgreSQL answer)"


def emulator_state(settings, dot_env):
    """LocalEnvironment.IsEmulatorExpected decides whether the emulator tests run; the probe asks its health endpoint."""
    expected = bool(os.environ.get("SERVICEBUS_TEST_CONNECTION_STRING", "").strip()) \
        or "SERVICEBUS_TEST_CONNECTION_STRING" in dot_env or dot_env.get("ACCEPT_EULA", "").upper() == "Y"
    note = "expected here, so the emulator tests run" if expected else "not expected here, so the emulator tests skip"
    address = "127.0.0.1"
    if settings.get("SERVICEBUS_TEST_CONNECTION_STRING"):
        host = re.search(r"(?i)endpoint=sb://([^/;:]+)", settings["SERVICEBUS_TEST_CONNECTION_STRING"])
        address = LOOPBACK.get(host.group(1).lower()) if host else None
        if address is None:
            return f"UNKNOWN (the test connection string names a host that is not local; not probed; {note})"
    port = port_number(settings.get("EMULATOR_HTTP_PORT"), 5300)
    if port is None:
        return f"UNKNOWN (EMULATOR_HTTP_PORT is not a port number; {note})"
    where = endpoint(address, port)
    data = answer(address, port, HEALTH_REQUEST, lambda data: b"\r\n" in data or len(data) >= 64)
    if data is None:
        return f"unavailable (no answer on {where}; {note})"
    status = re.match(rb"HTTP/\d(?:\.\d)? (\d{3})", data)
    if status and status.group(1) == b"200":
        return f"available (health endpoint answers 200 on {where})"
    if not status:
        return f"unavailable ({where} accepted a connection but gave no HTTP answer; {note})"
    return f"unavailable (health endpoint on {where} answered {status.group(1).decode()}; {note})"


def frozen_state(root):
    changed, endings, missing = [], [], []
    for relative, expected in FROZEN_DOCUMENTS:
        try:
            with open(os.path.join(root, *relative.split("/")), "rb") as handle:
                data = handle.read()
        except OSError:
            missing.append(relative)
            continue
        if hashlib.sha256(data).hexdigest() != expected:
            same_text = hashlib.sha256(data.replace(b"\r\n", b"\n")).hexdigest() == expected
            (endings if same_text else changed).append(relative)
    unchanged = len(FROZEN_DOCUMENTS) - len(changed) - len(endings) - len(missing)
    text = f"{unchanged}/{len(FROZEN_DOCUMENTS)} unchanged"
    for label, names in (("CHANGED", changed), ("LINE ENDINGS ONLY", endings), ("MISSING OR UNREADABLE", missing)):
        if names:
            text += f"; {label}: {', '.join(names)}"
    return text


def render():
    """The snapshot as text. Every field is filled: UNKNOWN when it could not be determined within the deadline."""
    deadline = time.monotonic() + DEADLINE
    root = repository_root()
    dot_env, settings = local_settings(root)
    results = {}

    def collect(name, function):
        try:
            results[name] = function()
        except Exception as error:  # noqa: BLE001 - a status snapshot never fails a session
            results[name] = f"UNKNOWN ({type(error).__name__})"

    jobs = (("git", lambda: git_state(root, deadline)), ("PostgreSQL", lambda: postgres_state(settings)),
            ("Service Bus emulator", lambda: emulator_state(settings, dot_env)))
    threads = [threading.Thread(target=collect, args=job, daemon=True) for job in jobs]
    for thread in threads:
        thread.start()
    collect("Gate", lambda: gate_state(root))
    collect("Frozen docs", lambda: frozen_state(root))
    for thread in threads:
        thread.join(max(0.0, deadline - time.monotonic()))
    late = f"UNKNOWN (no answer within {DEADLINE:g} s)"
    git_fields = results.get("git")
    if not isinstance(git_fields, dict):
        git_fields = dict.fromkeys(GIT_FIELDS, git_fields or late)
    lines = ["FULLY SESSION STATUS", f"Repository: {root}"]
    lines += [f"{key}: {git_fields[key]}" for key in GIT_FIELDS]
    lines += [f"{key}: {results.get(key, late)}" for key in ("Gate", "PostgreSQL", "Service Bus emulator", "Frozen docs")]
    return "\n".join(lines) + "\n"


def main():
    try:
        text = render()
    except Exception as error:  # noqa: BLE001 - a status snapshot never fails a session
        text = f"FULLY SESSION STATUS\nStatus: UNKNOWN (the status hook failed: {type(error).__name__})\n"
    sys.stdout.buffer.write(text.encode("utf-8"))
    sys.stdout.flush()
    return 0


if __name__ == "__main__":
    sys.exit(main())
