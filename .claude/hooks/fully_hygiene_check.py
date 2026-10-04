"""Fully hygiene check: a project-local Claude Code PostToolUse hook for Edit, Write, MultiEdit and NotebookEdit.

After a tool changes a file inside the Fully repository, it inspects that one file - never the rest of the repository -
for cheap format and migration mistakes, so they surface at once instead of in the final build or test run:

  fail  mixed CRLF/LF line endings; malformed UTF-8; NUL bytes in a text file; merge conflict markers; trailing
        whitespace (.editorconfig: trim_trailing_whitespace = true for every file); a UTF-8 BOM where the repository
        keeps none (it keeps one only on EF migration Designer files and the model snapshot, and preserves a BOM a
        file already has in HEAD); and, in an EF migration .cs file, any term DatabaseCredentialTests forbids
        (PASSWORD, Password=, SECRET, CREATE ROLE, CREATE USER; case-insensitive; comments included).
  warn  a uniformly CRLF working copy (the repository stores LF; such a copy commits as LF, but must never be mixed)
        and an EF migration Designer file or model snapshot without the BOM dotnet-ef gives them.

It never changes the file and never prints file content: a report names the file, the problem and line numbers.
Binary files are ignored. If it cannot run, it says so without blocking anything.
Dry-run tests: .claude/hooks/test_fully_hygiene_check.py.
"""
import json
import os
import re
import sys

BOM = b"\xef\xbb\xbf"
FILE_TOOLS = ("Edit", "Write", "MultiEdit", "NotebookEdit")
# DatabaseCredentialTests.No_migration_contains_a_credential_or_a_role_password: the same scope and vocabulary.
MIGRATIONS_DIR = "src/app.infrastructure/persistence/migrations/"
FORBIDDEN_MIGRATION_TERMS = ("PASSWORD", "Password=", "SECRET", "CREATE ROLE", "CREATE USER")
BINARY_EXTENSIONS = {
    ".png", ".jpg", ".jpeg", ".gif", ".ico", ".zip", ".pfx", ".snk", ".dll", ".exe", ".pdf", ".msix",  # .gitattributes
    ".pdb", ".nupkg", ".snupkg", ".woff", ".woff2", ".ttf", ".otf", ".eot", ".bmp", ".webp", ".tif", ".tiff", ".7z",
    ".gz", ".tgz", ".tar", ".jar", ".class", ".so", ".dylib", ".bin", ".db", ".sqlite", ".mp3", ".mp4", ".wav", ".avi",
    ".mov", ".docx", ".xlsx", ".pptx", ".doc", ".xls", ".pyc", ".cer", ".p12", ".dat",
}
TEXT_EXTENSIONS = {
    ".cs", ".csproj", ".props", ".targets", ".sln", ".slnx", ".json", ".jsonc", ".md", ".yml", ".yaml", ".xml", ".bicep",
    ".bicepparam", ".ps1", ".psm1", ".psd1", ".sh", ".bash", ".py", ".sql", ".txt", ".config", ".editorconfig",
    ".gitattributes", ".gitignore", ".dockerignore", ".http", ".razor", ".cshtml", ".css", ".scss", ".js", ".mjs",
    ".ts", ".tsx", ".html", ".htm", ".toml", ".ini", ".cmd", ".bat", ".csv", ".tsv", ".svg", ".sha256", ".ipynb",
    ".resx", ".runsettings", ".nuspec", ".ruleset", ".env",
}
TEXT_NAMES = {"dockerfile", "makefile", "license", "readme", "codeowners"}
SHOWN_LINES = 5


def workspace_root():
    root = os.environ.get("FULLY_HYGIENE_ROOT") or os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    return os.path.realpath(root)


def relative_path(path, root):
    full = os.path.realpath(path)
    prefix = root.rstrip("\\/") + os.sep
    if not full.lower().startswith(prefix.lower()):
        return None
    return full[len(prefix):].replace("\\", "/")


def line_list(numbers):
    shown = ", ".join(str(number) for number in numbers[:SHOWN_LINES])
    return shown + (f" and {len(numbers) - SHOWN_LINES} more" if len(numbers) > SHOWN_LINES else "")


def committed_starts_with_bom(relative, root):
    """True or False when HEAD's version of the file does or does not start with a BOM; None when git cannot say."""
    import subprocess
    try:
        result = subprocess.run(["git", "--no-optional-locks", "-C", root, "cat-file", "blob", "HEAD:" + relative],
                                capture_output=True, timeout=5)
    except (OSError, subprocess.SubprocessError):
        return None
    if result.returncode == 0:
        return result.stdout.startswith(BOM)
    message = result.stderr.decode("utf-8", "replace")
    return False if ("does not exist" in message or "exists on disk, but not in" in message) else None


def check(data, relative, root):
    failures, warnings = [], []
    lower = relative.lower()
    name = lower.rsplit("/", 1)[-1]
    extension = os.path.splitext(name)[1]
    if extension in BINARY_EXTENSIONS:
        return failures, warnings
    if b"\x00" in data:
        if extension in TEXT_EXTENSIONS or name in TEXT_NAMES or name.startswith(".env"):
            failures.append(f"contains a NUL byte (first on line {data[:data.index(b'\x00')].count(b'\n') + 1})")
        return failures, warnings  # a binary file, or a text file whose other checks would be meaningless

    has_bom = data.startswith(BOM)
    body = data[3:] if has_bom else data
    in_migrations = "/migrations/" in "/" + lower
    bom_expected = in_migrations and (name.endswith(".designer.cs") or name.endswith("modelsnapshot.cs"))
    if has_bom and not bom_expected:
        committed = committed_starts_with_bom(relative, root)
        if committed is None:
            warnings.append("starts with a UTF-8 BOM and the guard cannot tell whether the committed version has one "
                            "(the repository keeps a BOM only on EF migration Designer files and the model snapshot)")
        elif not committed:
            failures.append("starts with a UTF-8 BOM; the repository keeps a BOM only on EF migration Designer files and "
                            "the model snapshot")
    elif bom_expected and not has_bom:
        warnings.append("has no UTF-8 BOM; EF migration Designer files and the model snapshot carry one in this repository")

    try:
        text = body.decode("utf-8")
    except UnicodeDecodeError as error:
        failures.append(f"is not valid UTF-8 (first bad byte on line {body[:error.start].count(b'\n') + 1})")
        text = body.decode("utf-8", "replace")

    segments = body.split(b"\n")[:-1]
    crlf_lines = [number for number, segment in enumerate(segments, 1) if segment.endswith(b"\r")]
    if crlf_lines and len(crlf_lines) < len(segments):
        lf_lines = [number for number, segment in enumerate(segments, 1) if not segment.endswith(b"\r")]
        failures.append(f"mixes CRLF and LF line endings ({len(crlf_lines)} CRLF, {len(lf_lines)} LF; CRLF on line(s) "
                        f"{line_list(crlf_lines)}; LF on line(s) {line_list(lf_lines)})")
    elif crlf_lines:
        warnings.append("uses CRLF line endings; the repository stores LF (.gitattributes eol=lf): keep the file uniformly "
                        "CRLF or normalize it to LF, never mixed")

    trailing, markers, in_conflict = [], [], False
    lines = text.split("\n")
    for number, line in enumerate(lines, 1):
        line = line[:-1] if line.endswith("\r") else line
        if line != line.rstrip(" \t\f\v"):
            trailing.append(number)
        if re.match(r"<{7}(?: |$)", line):
            in_conflict = True
            markers.append(number)
        elif re.match(r">{7}(?: |$)", line):
            in_conflict = False
            markers.append(number)
        elif in_conflict and (line == "=======" or re.match(r"\|{7}(?: |$)", line)):
            markers.append(number)
    if trailing:
        failures.append(f"has trailing whitespace (.editorconfig trim_trailing_whitespace = true) on line(s) {line_list(trailing)}")
    if markers:
        failures.append(f"contains merge conflict markers on line(s) {line_list(markers)}")

    if lower.startswith(MIGRATIONS_DIR) and extension == ".cs":
        folded = [line.lower() for line in lines]
        for term in FORBIDDEN_MIGRATION_TERMS:
            hits = [number for number, line in enumerate(folded, 1) if term.lower() in line]
            if hits:
                failures.append(f"contains '{term}', which DatabaseCredentialTests forbids in migrations, on line(s) {line_list(hits)}")
    return failures, warnings


def emit(relative, failures, warnings):
    if failures:
        reason = f"Fully hygiene: {relative} " + "; ".join(failures + warnings) + \
                 ". The hook did not change the file; fix it now (it would fail later checks)."
        sys.stdout.write(json.dumps({"decision": "block", "reason": reason}))
    elif warnings:
        context = f"Fully hygiene warning: {relative} " + "; ".join(warnings) + "."
        sys.stdout.write(json.dumps({"hookSpecificOutput": {"hookEventName": "PostToolUse", "additionalContext": context}}))


def main():
    try:
        payload = json.loads(sys.stdin.buffer.read().decode("utf-8", "replace") or "{}")
        if payload.get("tool_name") not in FILE_TOOLS:
            return 0
        tool_input = payload.get("tool_input") or {}
        response = payload.get("tool_response") if isinstance(payload.get("tool_response"), dict) else {}
        path = tool_input.get("file_path") or tool_input.get("notebook_path") or response.get("filePath")
        if not isinstance(path, str) or not path.strip():
            return 0
        if not os.path.isabs(path) and isinstance(payload.get("cwd"), str):
            path = os.path.join(payload["cwd"], path)
        root = workspace_root()
        relative = relative_path(path, root)
        if relative is None or not os.path.isfile(path):
            return 0
        with open(path, "rb") as handle:
            data = handle.read()
        failures, warnings = check(data, relative, root)
    except Exception as error:  # noqa: BLE001 - a hygiene failure must never block work
        emit("(hook)", [], [f"could not run ({type(error).__name__}); nothing was blocked"])
        return 0
    emit(relative, failures, warnings)
    return 0


if __name__ == "__main__":
    sys.exit(main())
