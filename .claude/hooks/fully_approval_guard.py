"""Fully approval-boundary guard: a project-local Claude Code PreToolUse hook for the Bash and PowerShell tools and for
the monday MCP tools.

For a shell command, the hook payload arrives on stdin and the guard splits the FULL command into the simple commands
it would run - across &&, ||, ;, |, &, newlines, subshells, command substitutions, script blocks, heredocs fed to a
shell, and nested shells such as bash -c, pwsh -Command, cmd /c, eval or Invoke-Expression - and classifies each one:

  deny  destructive actions that never run from Claude Code in Fully: git add . / -A / --all, git clean,
        git reset --hard, git restore . / git checkout -- . (discarding work), git branch -D, force or mirror pushes,
        docker system or volume prune, docker compose down -v, and recursive deletes of the workspace, a drive, the
        home folder, .git or a root-level wildcard (rm -rf / . * ~, Remove-Item -Recurse -Force on the same);
  ask   approval boundaries that need the product owner's explicit go: git push, git branch -d, workflow dispatch and
        other GitHub writes, Azure and Key Vault writes, role assignments, eng/infra.ps1 -Approve, deployments and
        image pushes, direct calls to *.monday.com or to Azure or GitHub API endpoints, destructive database steps,
        and recursive deletes that are not limited to build or test artifacts.

Recursive deletes of build and test artifacts stay allowed: bin, obj, TestResults and __pycache__ inside the workspace,
anything under the temporary folders, and the wce_it_* test databases.

For a file edit (Edit, Write, MultiEdit, NotebookEdit) and for a direct shell write (redirection, tee, sed -i, cp, mv,
rm, Set-Content, Out-File, Copy-Item, Move-Item, Remove-Item and similar), the target path is resolved safely - relative
paths, '..', mixed separators, drive and Git Bash forms, case, trailing dots and spaces, alternate data streams, links
and 8.3 names - and the six frozen documents are denied, while .env files, .github/workflows, infra/bicep and existing
committed EF Core migrations ask. A path the guard cannot resolve safely asks.

For a monday MCP tool, reads print nothing; tools that change test data ask; destructive, administrative, code-executing
and automation tools are denied; an unclassified tool asks. The project-local permission rules mirror these sets.

Anything else prints nothing, so Claude Code's normal permission flow (including auto mode) decides. Text that is only
data - echo, printf, Write-Output, grep, commit messages, heredoc bodies not fed to a shell - is not treated as a
command. If the guard cannot analyse a command it asks; it never turns a decision into an allow.

It guards against accidents, not against deliberate evasion: aliases, variables and scripts it cannot see are out of
its reach. Dry-run tests: .claude/hooks/test_fully_approval_guard.py.
"""
import base64
import json
import os
import re
import subprocess
import sys

DENY = "deny"
ASK = "ask"
MAX_DEPTH = 8
SELFTEST = "fully-guard-selftest"
STATE = {"outside": False}


# --------------------------------------------------------------------------------------------- lexing

class Command:
    """One simple command: its words, the bodies fed to its stdin, the text nested inside it, and the command piped
    into it."""

    def __init__(self):
        self.tokens = []
        self.bodies = []
        self.inner = []
        self.first_quoted = False
        self.called = False
        self.pipe_prev = None
        self.writes = []


def matching(text, start, open_char, close_char, dialect):
    """Index of the bracket that closes one opened just before `start`; len(text) when it is never closed."""
    depth, i, n = 1, start, len(text)
    escape = "\\" if dialect == "bash" else "`"
    while i < n:
        c = text[i]
        if c == escape:
            i += 2
            continue
        if c == "'":
            j = text.find("'", i + 1)
            i = n if j < 0 else j + 1
            continue
        if c == '"':
            j = i + 1
            while j < n and text[j] != '"':
                j += 2 if text[j] == escape else 1
            i = j + 1
            continue
        if c == open_char:
            depth += 1
        elif c == close_char:
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return n


BASH_REDIRECT = re.compile(r"&>>|&>|>>|>\||>&|<&|<>|<<<|<<-|<<|>|<")


class Lexer:
    def __init__(self, text, dialect):
        self.text, self.dialect = text, dialect
        self.commands, self.nested = [], []
        self.current = Command()
        self.word = None
        self.target = None
        self.strip_tabs = False
        self.pending = []

    # Words and commands.
    def add(self, value, quoted=False):
        if self.word is None and not self.current.tokens and quoted:
            self.current.first_quoted = True
        self.word = (self.word or "") + value

    def finish_word(self):
        if self.word is None:
            return
        if self.target == "heredoc":
            self.pending.append((self.current, self.word, self.strip_tabs))
        elif self.target == "herestring":
            self.current.bodies.append(self.word)
        elif self.target == "write" or (self.target == "duplicate" and not (self.word.isdigit() or self.word == "-")):
            self.current.writes.append(self.word)
        elif self.target not in ("redirect", "duplicate"):
            self.current.tokens.append(self.word)
        self.word, self.target = None, None

    def finish_command(self, pipe=False):
        self.finish_word()
        finished = None
        if self.current.tokens or self.current.bodies:
            self.commands.append(self.current)
            finished = self.current
        self.current = Command()
        if pipe:
            self.current.pipe_prev = finished

    def nest(self, inner, dialect, placeholder):
        self.nested.append((inner, dialect))
        self.current.inner.append(inner)
        self.add(placeholder)

    def read_heredocs(self, i):
        text, n = self.text, len(self.text)
        for owner, delimiter, strip in self.pending:
            lines = []
            while i < n:
                j = text.find("\n", i)
                line = text[i:] if j < 0 else text[i:j]
                i = n if j < 0 else j + 1
                if (line.lstrip("\t") if strip else line).rstrip("\r") == delimiter:
                    break
                lines.append(line)
            owner.bodies.append("\n".join(lines))
        self.pending = []
        return i

    # Bash.
    def bash_double(self, i):
        text, n, buffer = self.text, len(self.text), []
        j = i + 1
        while j < n and text[j] != '"':
            c = text[j]
            if c == "\\" and j + 1 < n and text[j + 1] in '"\\$`\n':
                buffer.append(text[j + 1])
                j += 2
            elif text.startswith("$((", j):
                k = matching(text, j + 3, "(", ")", "bash")
                buffer.append("$((...))")
                j = k + 2
            elif text.startswith("$(", j):
                k = matching(text, j + 2, "(", ")", "bash")
                self.nested.append((text[j + 2:k], "bash"))
                self.current.inner.append(text[j + 2:k])
                buffer.append("$(...)")
                j = k + 1
            elif c == "`":
                k = text.find("`", j + 1)
                k = n if k < 0 else k
                self.nested.append((text[j + 1:k], "bash"))
                self.current.inner.append(text[j + 1:k])
                buffer.append("`...`")
                j = k + 1
            else:
                buffer.append(c)
                j += 1
        self.add("".join(buffer), quoted=True)
        return j + 1

    def bash_step(self, i):
        text, n, c = self.text, len(self.text), self.text[i]
        if c == "\\":
            if i + 1 < n and text[i + 1] == "\n":
                return i + 2
            self.add(text[i + 1] if i + 1 < n else "")
            return i + 2
        if c == "'":
            j = text.find("'", i + 1)
            j = n if j < 0 else j
            self.add(text[i + 1:j], quoted=True)
            return j + 1
        if c == '"':
            return self.bash_double(i)
        if text.startswith("$((", i):
            k = matching(text, i + 3, "(", ")", "bash")
            self.add("$((...))")
            return k + 2
        if text.startswith(("$(", "<(", ">("), i):
            k = matching(text, i + 2, "(", ")", "bash")
            self.nest(text[i + 2:k], "bash", "$(...)")
            return k + 1
        if c == "`":
            k = text.find("`", i + 1)
            k = n if k < 0 else k
            self.nest(text[i + 1:k], "bash", "`...`")
            return k + 1
        if c == "#" and self.word is None:
            j = text.find("\n", i)
            return n if j < 0 else j
        match = BASH_REDIRECT.match(text, i) if c in "<>&" else None
        if match and (c != "&" or match.group(0).startswith("&>")):
            operator = match.group(0)
            if self.word is not None and self.word.isdigit():
                self.word = None
            else:
                self.finish_word()
            if operator in ("<<", "<<-"):
                self.target, self.strip_tabs = "heredoc", operator == "<<-"
            elif operator == "<<<":
                self.target = "herestring"
            elif operator in (">&", "<&"):
                self.target = "duplicate"
            elif operator == "<":
                self.target = "redirect"
            else:
                self.target = "write"
            return match.end()
        if c in ";&|()":
            self.finish_command(pipe=c == "|" and not text.startswith("||", i))
            return i + 2 if text.startswith(("&&", "||", ";;", "|&"), i) else i + 1
        self.add(c)
        return i + 1

    # PowerShell.
    def powershell_double(self, i):
        text, n, buffer = self.text, len(self.text), []
        j = i + 1
        while j < n:
            c = text[j]
            if c == "`" and j + 1 < n:
                buffer.append(text[j + 1])
                j += 2
            elif c == '"':
                if j + 1 < n and text[j + 1] == '"':
                    buffer.append('"')
                    j += 2
                    continue
                break
            elif text.startswith("$(", j):
                k = matching(text, j + 2, "(", ")", "powershell")
                self.nested.append((text[j + 2:k], "powershell"))
                self.current.inner.append(text[j + 2:k])
                buffer.append("$(...)")
                j = k + 1
            else:
                buffer.append(c)
                j += 1
        self.add("".join(buffer), quoted=True)
        return j + 1

    def powershell_step(self, i):
        text, n, c = self.text, len(self.text), self.text[i]
        if c == "`":
            if i + 1 < n and text[i + 1] in "\r\n":
                return i + 3 if text.startswith("\r\n", i + 1) else i + 2
            self.add(text[i + 1] if i + 1 < n else "")
            return i + 2
        here = re.match(r"@(['\"])[ \t]*\r?\n", text[i:])
        if here:
            quote = here.group(1)
            start = i + here.end()
            end = text.find("\n" + quote + "@", start - 1)
            end = n if end < 0 else end
            body = text[start:end]
            if quote == '"':
                for sub in re.finditer(r"\$\(", body):
                    k = matching(body, sub.end(), "(", ")", "powershell")
                    self.nested.append((body[sub.end():k], "powershell"))
            self.add(body, quoted=True)
            return min(n, end + 3)
        if c == "'":
            j, buffer = i + 1, []
            while j < n:
                if text[j] == "'":
                    if j + 1 < n and text[j + 1] == "'":
                        buffer.append("'")
                        j += 2
                        continue
                    break
                buffer.append(text[j])
                j += 1
            self.add("".join(buffer), quoted=True)
            return j + 1
        if c == '"':
            return self.powershell_double(i)
        if text.startswith(("$(", "@("), i):
            k = matching(text, i + 2, "(", ")", "powershell")
            self.nest(text[i + 2:k], "powershell", "$(...)")
            return k + 1
        if c == "(":
            k = matching(text, i + 1, "(", ")", "powershell")
            self.nest(text[i + 1:k], "powershell", "(...)")
            return k + 1
        if c == "{":
            k = matching(text, i + 1, "{", "}", "powershell")
            self.nest(text[i + 1:k], "powershell", "{...}")
            return k + 1
        if text.startswith("<#", i):
            j = text.find("#>", i + 2)
            return n if j < 0 else j + 2
        if c == "#" and self.word is None:
            j = text.find("\n", i)
            return n if j < 0 else j
        if c == ">" or (c in "*0123456789" and self.word is None and i + 1 < n and text[i + 1] == ">"):
            if c != ">":
                i += 1
            elif self.word is not None and (self.word.isdigit() or self.word == "*"):
                self.word = None
            else:
                self.finish_word()
            i += 2 if text.startswith(">>", i) else 1
            if i + 1 < n and text[i] == "&" and text[i + 1].isdigit():
                return i + 2
            self.target = "write"
            return i
        if c == "<":
            self.finish_word()
            self.target = "redirect"
            return i + 1
        if c in ";|":
            self.finish_command(pipe=c == "|" and not text.startswith("||", i))
            return i + 2 if text.startswith("||", i) else i + 1
        if c == "&":
            if text.startswith("&&", i):
                self.finish_command()
                return i + 2
            if self.word is None and not self.current.tokens:
                self.current.called = True
                return i + 1
            self.finish_command()
            return i + 1
        self.add(c)
        return i + 1

    def run(self):
        text, n, i = self.text, len(self.text), 0
        step = self.bash_step if self.dialect == "bash" else self.powershell_step
        while i < n:
            c = text[i]
            if c == "\n":
                self.finish_command()
                i += 1
                if self.pending:
                    i = self.read_heredocs(i)
                continue
            if c in " \t\r":
                self.finish_word()
                i += 1
                continue
            i = step(i)
        self.finish_command()
        return self


# --------------------------------------------------------------------------------------------- paths

ARTIFACT_NAMES = {"bin", "obj", "testresults", "__pycache__"}


def norm_path(raw):
    path = raw.strip().replace("\\", "/").lower()
    return re.sub(r"/{2,}", "/", path)


def path_forms(path):
    """The Windows and Git Bash spellings of an absolute Windows path, normalised."""
    if not path:
        return set()
    form = norm_path(path).rstrip("/")
    forms = {form}
    drive = re.match(r"^([a-z]):(/.*)?$", form)
    if drive:
        forms.add("/" + drive.group(1) + (drive.group(2) or ""))
    return forms


WORKSPACE_FORMS = path_forms(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))) \
    | path_forms(os.environ.get("CLAUDE_PROJECT_DIR", ""))
HOME_FORMS = path_forms(os.path.expanduser("~")) | path_forms(os.environ.get("USERPROFILE", ""))
TEMP_FORMS = {"/tmp", "/var/tmp", "$tmp", "$temp", "$tmpdir", "${tmpdir}", "$env:temp", "$env:tmp", "%temp%", "%tmp%"} \
    | path_forms(os.environ.get("TEMP", "")) | path_forms(os.environ.get("TMP", ""))
BROAD_NAMES = {"*", "**", "*.*", ".", "..", "~", "/", "$home", "${home}", "$env:userprofile", "$env:homepath", "$pwd",
               "${pwd}", "$claude_project_dir", "${claude_project_dir}", "$env:claude_project_dir"}


def location(raw):
    """Where a path points: ('workspace' | 'temp' | 'outside', the rest of the path)."""
    path = norm_path(raw).rstrip("/") or "/"
    for root in sorted(WORKSPACE_FORMS, key=len, reverse=True):
        if path == root:
            return "workspace", ""
        if path.startswith(root + "/"):
            rest = path[len(root) + 1:]
            return ("outside", rest) if ".." in rest.split("/") else ("workspace", rest)
    for root in TEMP_FORMS:
        if path.startswith(root + "/") and ".." not in path.split("/"):
            return "temp", path[len(root) + 1:]
    if path.startswith(("/", "~", "$", "%")) or re.match(r"^[a-z]:", path) or ".." in path.split("/"):
        return "outside", path
    return ("outside" if STATE["outside"] else "workspace"), path


def is_broad(raw):
    """A path whose recursive deletion removes the workspace, a drive, the home folder, .git or a whole tree."""
    path = norm_path(raw)
    while path.endswith("/."):
        path = path[:-2]
    if path.endswith(("/*", "/**", "/*.*")):
        path = path.rsplit("/", 1)[0] or "/"
    path = path.rstrip("/") or "/"
    if path in BROAD_NAMES or re.fullmatch(r"\.\.(/\.\.)*", path) or path in WORKSPACE_FORMS or path in HOME_FORMS:
        return True
    if re.fullmatch(r"[a-z]:", path) or re.fullmatch(r"/[a-z]", path):
        return True
    return path == ".git" or path.endswith("/.git")


def is_artifact(raw):
    where, rest = location(raw)
    if where == "temp":
        return True
    if where != "workspace":
        return False
    parts = [part for part in rest.split("/") if part not in ("", ".", "*", "**")]
    return any(part in ARTIFACT_NAMES for part in parts)


def recursive_delete(label, targets, deny_broad):
    if not targets:
        return [(ASK, f"{label} on targets the guard cannot see")]
    broad = [target for target in targets if is_broad(target)]
    if broad:
        return [(DENY if deny_broad else ASK, f"{label} {broad[0]} deletes the workspace or a whole tree")]
    other = [target for target in targets if not is_artifact(target)]
    if other:
        return [(ASK, f"{label} {other[0]} is not limited to build or test artifacts")]
    return []


# --------------------------------------------------------------------------------------------- classification

KEYWORDS = {"if", "then", "else", "elif", "fi", "do", "done", "while", "until", "!", "{", "}", "time", "esac",
            "function", "elseif", "foreach", "switch", "try", "finally"}
ASSIGNMENT = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*=")
PS_ASSIGNMENT_OPERATORS = {"=", "+=", "-=", "*=", "/=", "%=", "??="}
PLAIN_PREFIXES = {"sudo", "nohup", "command", "builtin", "exec", "stdbuf", "winpty", "unbuffer", "noglob", "time"}
SHELLS = {"bash", "sh", "zsh", "dash", "ksh", "fish"}
POWERSHELLS = {"pwsh", "powershell", "powershell_ise"}
INTERPRETERS = {"python", "python3", "py", "node", "deno", "bun", "ruby", "perl", "php"}
CD_COMMANDS = {"cd", "pushd", "chdir", "set-location", "sl", "push-location"}
PS_REMOVE = {"remove-item", "ri", "rm", "del", "erase", "rd", "rmdir"}
CMD_REMOVE = {"rd", "rmdir", "del", "erase"}
GET_CHILDREN = {"get-childitem", "gci", "ls", "dir", "get-item", "gi"}
DATA_COMMANDS = {
    "echo", "printf", "write-output", "write-host", "write-verbose", "write-information", "write-warning",
    "write-error", "write-debug", "write", "out-file", "out-host", "out-string", "out-null", "set-content",
    "add-content", "cat", "type", "get-content", "gc", "more", "less", "head", "tail", "grep", "egrep", "fgrep", "rg",
    "ag", "ack", "findstr", "select-string", "sls", "sed", "awk", "cut", "sort", "uniq", "wc", "tr", "jq", "yq", "diff",
    "cmp", "tee", "column", "nl", "od", "xxd", "sha256sum", "md5sum", "test", "[", "[[", "true", "false", ":", "read",
    "export", "set", "unset", "ls", "dir", "get-childitem", "gci", "stat", "file", "realpath", "dirname", "basename",
    "pwd", "cd", "pushd", "popd", "set-location", "sl", "get-item", "test-path", "resolve-path", "convertto-json",
    "convertfrom-json", "select-object", "where-object", "foreach-object", "sort-object", "measure-object",
    "format-table", "format-list", "group-object", "for", "in", "case",
}
NETWORK_METHODS = re.compile(r"(?i)download(string|file|data)|upload(string|file|data)|openread|"
                             r"(get|post|put|patch|delete|send)(string)?async|webclient|httpclient|webrequest")
PROVIDER_HOSTS = [
    (re.compile(r"(?i)(?:^|[^a-z0-9-])(?:[a-z0-9-]+\.)*monday\.com\b"), "a direct call to monday.com (real provider)"),
    (re.compile(r"(?i)management\.azure\.com|\.vault\.azure\.net|\.azurecr\.io|\.servicebus\.windows\.net|"
                r"\.postgres\.database\.azure\.com|\.azurecontainerapps\.io|\.(?:blob|queue|table|file)\.core\.windows\.net|"
                r"\.applicationinsights\.azure\.com|\.monitor\.azure\.com|login\.microsoftonline\.com|graph\.microsoft\.com"),
     "a direct call to an Azure endpoint"),
    (re.compile(r"(?i)\b(?:api|uploads)\.github\.com"), "a direct call to the GitHub API (use gh for reads)"),
]
DESTRUCTIVE_SQL = re.compile(
    r"(?i)\b(drop\s+(database|schema|table|role|user|owned|extension|policy|function|view|index|trigger|type|sequence|"
    r"materialized)|truncate|delete\s+from|alter\s+(role|user|default\s+privileges)|grant|revoke|reassign\s+owned|"
    r"update\s+[\w.\"]+\s+set)\b")
TEST_DATABASE = re.compile(r"(?i)^wce_it_[a-z0-9_]+$")
TEST_DATABASE_DROP = re.compile(
    r"(?i)\bdrop\s+database\s+(if\s+exists\s+)?\"?wce_it_[a-z0-9_]+\"?(\s+with\s*\(\s*force\s*\))?\s*;?")


def command_name(token):
    name = token.replace("\\", "/").rstrip("/").rsplit("/", 1)[-1].lower()
    for suffix in (".exe", ".cmd", ".bat", ".com"):
        if name.endswith(suffix):
            return name[: -len(suffix)]
    return name


def short_flags(token):
    """The letters of a single-dash flag cluster such as -uf, or '' for anything else."""
    return token[1:] if len(token) > 1 and token[0] == "-" and token[1] != "-" else ""


def strip_prefixes(tokens, dialect):
    t = list(tokens)
    while t:
        head = t[0]
        name = command_name(head)
        if head.lower() in KEYWORDS or (dialect == "bash" and ASSIGNMENT.match(head)):
            t = t[1:]
        elif dialect == "powershell" and head in (".", "&") and len(t) > 1:
            t = t[1:]
        elif name in PLAIN_PREFIXES:
            t = t[1:]
            while t and t[0].startswith("-"):
                t = t[1:]
        elif name == "env":
            t = t[1:]
            while t and (t[0].startswith("-") or ASSIGNMENT.match(t[0])):
                t = t[2:] if t[0] in ("-u", "--unset", "-C", "--chdir") else t[1:]
        elif name == "nice":
            t = t[1:]
            if t and t[0] == "-n":
                t = t[2:]
            elif t and t[0].startswith("-"):
                t = t[1:]
        elif name == "timeout":
            t = t[1:]
            while t and t[0].startswith("-"):
                t = t[2:] if t[0] in ("-s", "-k", "--signal", "--kill-after") else t[1:]
            t = t[1:]
        else:
            break
    return t


def analyse(text, dialect, depth=0):
    if depth > MAX_DEPTH:
        return [(ASK, "the command nests too deeply for the guard to analyse")]
    lexer = Lexer(text, dialect).run()
    findings = []
    for command in lexer.commands:
        tokens = strip_prefixes(command.tokens, dialect)
        if tokens and command_name(tokens[0]) in CD_COMMANDS:
            target = next((token for token in tokens[1:] if not token.startswith("-")), "~")
            STATE["outside"] = location(target)[0] != "workspace"
        findings += classify(command, dialect, depth)
    for inner, inner_dialect in lexer.nested:
        findings += analyse(inner, inner_dialect, depth + 1)
    return findings


def classify_tokens(tokens, dialect, depth, bodies=()):
    command = Command()
    command.tokens = list(tokens)
    command.bodies = list(bodies)
    return classify(command, dialect, depth)


def classify(command, dialect, depth):
    return classify_command(command, dialect, depth) + shell_write_findings(command, dialect)


def classify_command(command, dialect, depth):
    if depth > MAX_DEPTH:
        return [(ASK, "the command nests too deeply for the guard to analyse")]
    tokens = strip_prefixes(command.tokens, dialect)
    bodies = command.bodies
    if not tokens:
        return []
    name = command_name(tokens[0])
    args = tokens[1:]
    low = [a.lower() for a in args]

    if name == SELFTEST:
        return [(DENY, "self-test: the Fully approval guard is active")]

    if dialect == "powershell" and not command.called:
        head = tokens[0]
        if head.startswith("$"):
            if args and args[0] in PS_ASSIGNMENT_OPERATORS:
                return classify_tokens(args[1:], dialect, depth + 1, bodies) if len(args) > 1 else []
            return host_findings(tokens, command.inner) if NETWORK_METHODS.search(" ".join(tokens)) else []
        if head.startswith("[") or command.first_quoted:
            return host_findings(tokens, command.inner) if NETWORK_METHODS.search(" ".join(tokens)) else []

    # Commands that run other commands.
    if name in SHELLS:
        return shell_findings(args, bodies, depth)
    if name in POWERSHELLS:
        return powershell_findings(args, bodies, depth)
    if name == "cmd":
        for index, value in enumerate(low):
            if value in ("/c", "/k"):
                return analyse(" ".join(args[index + 1:]), "bash", depth + 1)
        return []
    if name == "eval":
        return analyse(" ".join(args), "bash", depth + 1)
    if name in ("invoke-expression", "iex"):
        rest = [a for a in args if a.lower() not in ("-command", "-com", "-comm")]
        if not rest:
            return [(ASK, "Invoke-Expression runs code the guard cannot see")]
        return analyse(" ".join(rest), "powershell", depth + 1)
    if name == "xargs":
        i = 0
        while i < len(args) and args[i].startswith("-"):
            i += 2 if args[i] in ("-I", "-n", "-P", "-L", "-d", "-E", "-s", "-a") else 1
        inner = args[i:]
        if inner and command_name(inner[0]) in ("rm", "rd", "rmdir") and is_recursive_rm(inner[1:]):
            if command.pipe_prev is not None and piped_artifacts(command.pipe_prev):
                return []
            return [(ASK, "a recursive delete of piped targets the guard cannot see")]
        return classify_tokens(inner, dialect, depth + 1)
    if name == "find":
        return find_findings(args, dialect, depth)
    if name in ("start-process", "saps") or (dialect == "powershell" and name == "start"):
        return start_process_findings(args, depth)
    if name == "wsl":
        i = 0
        while i < len(args) and args[i].startswith("-"):
            i += 2 if args[i] in ("-d", "--distribution", "-u", "--user", "--cd") else 1
        return classify_tokens(args[i:], "bash", depth + 1, bodies)
    if name in ("docker", "docker-compose", "podman"):
        return docker_findings(name, args, bodies, depth)

    # Recursive deletes.
    if name in CMD_REMOVE and "/s" in low:
        switches = {a for a in low if re.fullmatch(r"/[a-z]", a)}
        targets = [a for a in args if a.lower() not in switches]
        return recursive_delete(f"{name} /s", targets, deny_broad=True)
    if dialect == "powershell" and name in PS_REMOVE:
        return remove_item_findings(args, command.pipe_prev)
    if name == "rm":
        return rm_findings(args)

    findings = []
    if name == "git":
        findings += git_findings(args)
    elif name == "gh":
        findings += gh_findings(args)
    elif name == "az":
        findings += az_findings(args)
    elif re.match(r"^[a-z]+-az[a-z]", name):
        findings += az_cmdlet_findings(name)
    elif name in ("azd", "railway", "kubectl", "helm", "terraform", "tofu", "pulumi", "vercel", "netlify", "fly",
                  "flyctl", "heroku", "firebase", "gcloud", "aws", "func", "swa"):
        findings += deploy_findings(name, args)
    elif name == "dotnet":
        findings += dotnet_findings(low)
    elif name in ("npm", "pnpm", "yarn") and "publish" in low:
        findings.append((ASK, f"{name} publish publishes a package"))
    elif name == "psql":
        findings += sql_findings(args, bodies)
    elif name == "dropdb":
        positional = [a for a in args if not a.startswith("-")]
        if not positional or not TEST_DATABASE.match(positional[-1]):
            findings.append((ASK, "dropdb destroys a database that is not a wce_it_ test database"))
    elif name == "infra.ps1":
        if any(a.startswith("-approve") for a in low) or "deploy" in low:
            findings.append((ASK, "eng/infra.ps1 deploy -Approve changes Azure"))
    elif name == "db.ps1" and "reset" in low:
        findings.append((ASK, "eng/db.ps1 reset destroys the local PostgreSQL volume"))

    if name in INTERPRETERS and bodies:
        findings += host_findings([], bodies)
    if name not in DATA_COMMANDS and name not in ("git", "gh", "az"):
        findings += host_findings(tokens, list(bodies) + command.inner)
    return findings


def host_findings(tokens, extra):
    text = " ".join(list(tokens) + list(extra))
    return [(ASK, reason) for pattern, reason in PROVIDER_HOSTS if pattern.search(text)]


def shell_findings(args, bodies, depth):
    for index, token in enumerate(args):
        if token == "-c" or ("c" in short_flags(token)):
            if index + 1 < len(args):
                return analyse(args[index + 1], "bash", depth + 1)
            return []
        if not token.startswith("-"):
            return []  # a script file the guard cannot see
    return [finding for body in bodies for finding in analyse(body, "bash", depth + 1)]


def is_prefix(value, option, minimum):
    return len(value) >= minimum and option.startswith(value)


def powershell_findings(args, bodies, depth):
    i = 0
    while i < len(args):
        value = args[i].lower()
        if value in ("-c", "-command") or is_prefix(value, "-command", 5) or value in ("-cwa", "-commandwithargs"):
            rest = args[i + 1:]
            if rest == ["-"]:
                break
            return analyse(" ".join(rest), "powershell", depth + 1)
        if value in ("-e", "-ec", "-enc") or is_prefix(value, "-encodedcommand", 4):
            try:
                decoded = base64.b64decode(args[i + 1]).decode("utf-16-le")
            except Exception:  # noqa: BLE001 - an unreadable encoded command is asked about, never allowed
                return [(ASK, "an encoded PowerShell command the guard cannot read")]
            return analyse(decoded, "powershell", depth + 1)
        if value in ("-f", "-file") or is_prefix(value, "-file", 3):
            return classify_tokens(args[i + 1:], "powershell", depth + 1)
        if value in ("-executionpolicy", "-ex", "-ep", "-workingdirectory", "-wd", "-windowstyle", "-w",
                     "-outputformat", "-of", "-inputformat", "-if", "-configurationname", "-version", "-v"):
            i += 2
            continue
        if not value.startswith("-"):
            return classify_tokens(args[i:], "powershell", depth + 1)
        i += 1
    return [finding for body in bodies for finding in analyse(body, "powershell", depth + 1)]


def start_process_findings(args, depth):
    file, arguments, i = None, [], 0
    while i < len(args):
        value = args[i].lower()
        if value in ("-filepath", "-path") or is_prefix(value, "-filepath", 3):
            file = args[i + 1] if i + 1 < len(args) else None
            i += 2
        elif value in ("-argumentlist", "-args") or is_prefix(value, "-argumentlist", 3):
            i += 1
            while i < len(args):
                arguments.append(args[i])
                i += 1
                if not arguments[-1].endswith(","):
                    break
        elif not value.startswith("-") and file is None:
            file = args[i]
            i += 1
        else:
            i += 1
    if file is None:
        return []
    words = [part for argument in arguments for part in re.split(r"[,\s]+", argument) if part]
    return classify_tokens([file] + words, "powershell", depth + 1)


def is_recursive_rm(args):
    return any(a in ("--recursive", "/s", "/S") or "r" in short_flags(a) or "R" in short_flags(a) for a in args)


def rm_findings(args):
    recursive = force = options_done = False
    targets = []
    for value in args:
        if not options_done and value == "--":
            options_done = True
        elif not options_done and value.startswith("--"):
            recursive |= value == "--recursive"
            force |= value == "--force"
        elif not options_done and len(value) > 1 and value.startswith("-"):
            recursive |= "r" in value[1:] or "R" in value[1:]
            force |= "f" in value[1:]
        else:
            targets.append(value)
    if not recursive:
        return []
    return recursive_delete("rm -rf" if force else "rm -r", targets, deny_broad=True)


def collect_values(args, i, into):
    """Collects a PowerShell parameter value at args[i], following trailing commas; returns the next index."""
    while i < len(args):
        into.append(args[i])
        i += 1
        if not into[-1].endswith(","):
            break
    return i


def split_list(values):
    return [part.strip() for value in values for part in value.split(",") if part.strip()]


PS_VALUE_PARAMETERS = {"-exclude", "-credential", "-stream", "-erroraction", "-ea", "-warningaction", "-wa",
                       "-informationaction", "-infa", "-errorvariable", "-ev", "-outvariable", "-ov",
                       "-pipelinevariable", "-pv", "-depth", "-attributes"}


def parse_ps_paths(args):
    """Paths, -Include/-Filter values and switches of a PowerShell item cmdlet."""
    paths, filters, switches, i = [], [], {}, 0
    while i < len(args):
        value = args[i]
        flag, _, inline = value.lower().partition(":")
        if flag.startswith("-") and len(flag) > 1:
            if flag in ("-path", "-literalpath", "-lp", "-pspath") or is_prefix(flag, "-path", 3) \
                    or is_prefix(flag, "-literalpath", 3):
                i = collect_values(args, i + 1, paths)
                continue
            if is_prefix(flag, "-include", 3) or is_prefix(flag, "-filter", 3):
                i = collect_values(args, i + 1, filters)
                continue
            if flag in PS_VALUE_PARAMETERS and not inline:
                i += 2
                continue
            switches[flag] = inline != "$false"
            i += 1
            continue
        paths.append(value)
        i += 1
    return split_list(paths), [f.lower() for f in split_list(filters)], switches


def remove_item_findings(args, pipe_prev):
    paths, filters, switches = parse_ps_paths(args)
    recurse = force = False
    for flag, on in switches.items():
        if re.fullmatch(r"-[rf]{2,}", flag) and "r" in flag and "f" in flag:
            recurse = force = on  # a Bash habit such as rm -rf typed into PowerShell
        elif is_prefix(flag, "-recurse", 2):
            recurse = on
        elif is_prefix(flag, "-force", 3):
            force = on
    if not recurse:
        return []
    label = "Remove-Item -Recurse -Force" if force else "Remove-Item -Recurse"
    if not paths:
        if pipe_prev is not None and piped_artifacts(pipe_prev):
            return []
        return [(ASK, f"{label} on piped items the guard cannot see")]
    if filters and all(f in ARTIFACT_NAMES for f in filters) and all(location(p)[0] == "workspace" for p in paths):
        return []
    return recursive_delete(label, paths, deny_broad=force)


def find_scope(args):
    """True when a find selects only build or test artifact directories inside the workspace."""
    low = [a.lower() for a in args]
    roots = []
    for value in args:
        if value.startswith(("-", "(", "!", ")")):
            break
        roots.append(value)
    if "-not" in low or "!" in low:
        return False
    names = [low[i + 1] for i, value in enumerate(low[:-1]) if value in ("-name", "-iname")]
    return bool(names) and all(name in ARTIFACT_NAMES for name in names) \
        and all(location(root)[0] == "workspace" for root in roots or ["."])


def piped_artifacts(command):
    """True when the command feeding a delete selects only build or test artifacts inside the workspace."""
    tokens = strip_prefixes(command.tokens, "powershell")
    if not tokens:
        return False
    name = command_name(tokens[0])
    if name == "find":
        return find_scope(tokens[1:])
    if name in GET_CHILDREN:
        paths, filters, _ = parse_ps_paths(tokens[1:])
        roots = paths or ["."]
        if not all(location(root)[0] == "workspace" for root in roots):
            return False
        return all(f in ARTIFACT_NAMES for f in filters) if filters else all(is_artifact(root) for root in roots)
    return False


def find_findings(args, dialect, depth):
    low = [a.lower() for a in args]
    scoped = find_scope(args)
    findings = []
    if "-delete" in low and not scoped:
        findings.append((ASK, "find -delete removes every match; not limited to build or test artifacts"))
    for index, value in enumerate(low):
        if value in ("-exec", "-execdir", "-ok", "-okdir"):
            inner = []
            for token in args[index + 1:]:
                if token in (";", "+"):
                    break
                inner.append(token)
            if inner and command_name(inner[0]) in ("rm", "rmdir", "unlink"):
                if not scoped:
                    findings.append((ASK, "find -exec rm deletes every match; not limited to build or test artifacts"))
                continue
            findings += classify_tokens(inner, dialect, depth + 1)
    return findings


GIT_OPTIONS_WITH_VALUE = {"-c", "-C", "--git-dir", "--work-tree", "--namespace", "--exec-path", "--super-prefix",
                          "--config-env"}
BROAD_PATHSPECS = {".", "./", ":/", ":/.", "*", "./*", ":(top)", ":(top)."}


def git_findings(args):
    i = 0
    while i < len(args) and args[i].startswith("-"):
        i += 2 if args[i] in GIT_OPTIONS_WITH_VALUE else 1
    if i >= len(args):
        return []
    sub, rest = args[i].lower(), args[i + 1:]
    if sub == "add":
        broad = [a for a in rest if a in (".", "./", ":/", ":/.", "*", "--all") or "A" in short_flags(a)]
        if broad:
            return [(DENY, f"git add {broad[0]} stages everything; stage files by name")]
        if any(a == "--update" or "u" in short_flags(a) for a in rest):
            return [(ASK, "git add -u stages every tracked change; stage files by name")]
        return []
    if sub == "clean":
        return [(DENY, "git clean deletes untracked files")]
    if sub == "reset" and "--hard" in rest:
        return [(DENY, "git reset --hard discards work")]
    if sub == "restore":
        staged = any(a == "--staged" or "S" in short_flags(a) for a in rest)
        worktree = any(a == "--worktree" or "W" in short_flags(a) for a in rest)
        paths, skip = [], False
        for value in rest:
            if skip:
                skip = False
            elif value in ("-s", "--source"):
                skip = True
            elif not value.startswith("-"):
                paths.append(value)
        broad = [p for p in paths if p in BROAD_PATHSPECS]
        if broad and (worktree or not staged):
            return [(DENY, f"git restore {broad[0]} discards uncommitted work")]
        return []
    if sub == "checkout":
        paths = rest[rest.index("--") + 1:] if "--" in rest else [a for a in rest if not a.startswith("-")]
        broad = [p for p in paths if p in BROAD_PATHSPECS]
        if broad:
            return [(DENY, f"git checkout -- {broad[0]} discards uncommitted work")]
        return []
    if sub == "branch":
        letters = "".join(short_flags(a) for a in rest)
        delete = "d" in letters or "D" in letters or "--delete" in rest
        force = "D" in letters or "f" in letters or "--force" in rest
        if delete and force:
            return [(DENY, "git branch -D deletes a branch even when it is not merged")]
        if delete:
            return [(ASK, "git branch -d deletes a branch")]
        return []
    if sub == "rm":
        if any(a in ("-r", "--recursive") or "r" in short_flags(a) for a in rest) \
                and any(a in BROAD_PATHSPECS for a in rest):
            return [(ASK, "git rm -r on the whole tree removes every tracked file")]
        return []
    if sub == "push":
        force = [a for a in rest if a in ("--force", "--mirror", "--prune")
                 or a.startswith(("--force-with-lease", "--force-if-includes"))
                 or "f" in short_flags(a) or (len(a) > 1 and a.startswith("+"))]
        if force:
            return [(DENY, f"a force or mirror push ({force[0]}) rewrites the remote")]
        return [(ASK, "git push publishes to the remote")]
    return []


GH_READ_VERBS = {"list", "ls", "view", "watch", "status", "checks", "diff", "download", "clone", "get"}


def gh_findings(args):
    words, i = [], 0
    while i < len(args):
        if args[i] in ("-R", "--repo", "--hostname"):
            i += 2
            continue
        if not args[i].startswith("-"):
            words.append(args[i])
        i += 1
    if not words:
        return []
    group = words[0].lower()
    if group == "api":
        return gh_api_findings(args)
    if group in ("help", "version", "status", "search", "browse", "completion", "--version"):
        return []
    verb = words[1].lower() if len(words) > 1 else ""
    if verb in GH_READ_VERBS:
        return []
    if group == "workflow" and verb == "run":
        deploying = any("cd-dev" in a.lower() or a.lower() == "cd dev" for a in args)
        return [(ASK, "gh workflow run dispatches a workflow" + (" (CD Dev deploys to Azure)" if deploying else ""))]
    return [(ASK, f"gh {group} {verb}".rstrip() + " changes GitHub")]


def gh_api_findings(args):
    method, explicit, fields, graphql = "GET", False, False, False
    for index, value in enumerate(args):
        low = value.lower()
        if low in ("-x", "--method") and index + 1 < len(args):
            method, explicit = args[index + 1].upper(), True
        elif low.startswith("--method="):
            method, explicit = value.split("=", 1)[1].upper(), True
        elif low.startswith("-x") and len(value) > 2:
            method, explicit = value[2:].upper(), True
        elif low in ("-f", "--field", "--raw-field", "--input") or low.startswith(("--field=", "--raw-field=", "--input=")):
            fields = True
        elif low == "graphql":
            graphql = True
    if graphql:
        return [(ASK, "a GitHub GraphQL mutation")] if any("mutation" in a.lower() for a in args) else []
    if method != "GET" or (fields and not explicit):
        return [(ASK, f"gh api {method if explicit else 'POST'} changes GitHub")]
    return []


AZ_READ_VERBS = {"show", "list", "query", "version", "what-if", "validate", "exists", "check-name-availability", "get",
                 "help", "find", "tail", "wait", "download"}
AZ_LOCAL_BICEP = {"build", "lint", "format", "decompile", "decompile-params", "build-params", "generate-params",
                  "version", "list-versions"}


def az_findings(args):
    if any(a in ("-h", "--help") for a in args):
        return []
    words = []
    for value in args:
        if value.startswith("-"):
            break
        words.append(value.lower())
    if not words:
        return []
    verb, path = words[-1], " ".join(words)
    if words[0] == "bicep" and verb in AZ_LOCAL_BICEP:
        return []
    if words[0] == "rest":
        method = "GET"
        for index, value in enumerate(args):
            if value.lower() in ("--method", "-m") and index + 1 < len(args):
                method = args[index + 1].upper()
        if method == "GET" and not any(a.lower() in ("--body", "-b") for a in args):
            return []
        return [(ASK, f"az rest {method} changes Azure")]
    if verb in AZ_READ_VERBS or verb.startswith(("list-", "show-", "get-")):
        return []
    if "keyvault" in words:
        return [(ASK, f"a Key Vault write (az {path})")]
    if words[:2] in (["role", "assignment"], ["role", "definition"]):
        return [(ASK, f"a role assignment change (az {path})")]
    if "deployment" in words:
        return [(ASK, f"an Azure deployment (az {path})")]
    return [(ASK, f"an Azure change (az {path})")]


AZ_CMDLET_READ_VERBS = {"get", "test", "find", "search", "measure", "resolve", "select", "format", "out", "show",
                        "wait", "export", "convertto", "convertfrom", "compare", "read"}


def az_cmdlet_findings(name):
    verb = name.split("-", 1)[0]
    if verb in AZ_CMDLET_READ_VERBS:
        return []
    if "keyvault" in name:
        return [(ASK, f"a Key Vault write ({name})")]
    if "roleassignment" in name or "roledefinition" in name:
        return [(ASK, f"a role assignment change ({name})")]
    return [(ASK, f"an Azure change ({name})")]


DEPLOY_WRITE_VERBS = {"up", "deploy", "redeploy", "provision", "down", "destroy", "apply", "create", "delete", "remove",
                      "rm", "replace", "patch", "scale", "rollout", "set", "edit", "install", "upgrade", "uninstall",
                      "rollback", "push", "publish", "release", "import", "update", "run", "exec", "start", "stop",
                      "restart", "promote", "attach", "detach", "taint", "untaint", "link", "add", "cordon", "drain",
                      "label", "annotate", "expose", "autoscale", "cp"}


def deploy_findings(name, args):
    words = [a.lower() for a in args if not a.startswith("-")][:3]
    hit = next((w for w in words if w in DEPLOY_WRITE_VERBS), None)
    if hit or "--prod" in args:
        return [(ASK, f"a deployment or cloud change ({name} {hit or '--prod'})")]
    return []


def dotnet_findings(low):
    for index, value in enumerate(low):
        if value in ("ef", "dotnet-ef") and low[index + 1:index + 3] == ["database", "drop"]:
            return [(ASK, "dotnet ef database drop destroys a database")]
    if low[:2] in (["nuget", "push"], ["nuget", "delete"]):
        return [(ASK, "publishing to a NuGet feed")]
    return []


def sql_findings(args, bodies):
    texts = list(bodies)
    for index, value in enumerate(args):
        flags = short_flags(value)
        if value == "--command" and index + 1 < len(args):
            texts.append(args[index + 1])
        elif value.startswith("--command="):
            texts.append(value.split("=", 1)[1])
        elif "c" in flags:
            after = flags[flags.index("c") + 1:]
            texts.append(after if after else (args[index + 1] if index + 1 < len(args) else ""))
    remaining = [TEST_DATABASE_DROP.sub(" ", text) for text in texts]
    if any(DESTRUCTIVE_SQL.search(text) for text in remaining):
        return [(ASK, "destructive or privilege-changing SQL")]
    return []


DOCKER_OPTIONS_WITH_VALUE = {"-c", "--context", "-H", "--host", "--config", "-l", "--log-level", "--tlscacert",
                             "--tlscert", "--tlskey"}
DOCKER_EXEC_OPTIONS_WITH_VALUE = {"-u", "--user", "-e", "--env", "-w", "--workdir", "--env-file", "--detach-keys",
                                  "--index"}
COMPOSE_OPTIONS_WITH_VALUE = {"-f", "--file", "-p", "--project-name", "--env-file", "--profile",
                              "--project-directory", "--ansi", "--progress", "--parallel"}


def after_options(args, with_value):
    i = 0
    while i < len(args) and args[i].startswith("-"):
        i += 2 if args[i] in with_value else 1
    return args[i:]


def docker_findings(name, args, bodies, depth):
    if name == "docker-compose":
        return compose_findings(args, bodies, depth)
    rest = after_options(args, DOCKER_OPTIONS_WITH_VALUE)
    if not rest:
        return []
    sub, rest = rest[0].lower(), rest[1:]
    verb = rest[0].lower() if rest else ""
    if sub == "compose":
        return compose_findings(rest, bodies, depth)
    if sub == "system" and verb == "prune":
        return [(DENY, "docker system prune deletes images, containers and volumes")]
    if sub == "volume" and verb == "prune":
        return [(DENY, "docker volume prune deletes volumes, including local database data")]
    if sub == "volume" and verb in ("rm", "remove"):
        return [(ASK, "docker volume rm deletes a volume")]
    if sub in ("image", "container", "builder", "network", "buildx") and verb == "prune":
        return [(ASK, f"docker {sub} prune is a broad cleanup")]
    if sub == "push" or (sub in ("build", "buildx") and "--push" in rest):
        return [(ASK, "pushing an image publishes it")]
    if sub == "exec":
        inner = after_options(rest, DOCKER_EXEC_OPTIONS_WITH_VALUE)
        return classify_tokens(inner[1:], "bash", depth + 1, bodies) if inner else []
    return []


def compose_findings(args, bodies, depth):
    rest = after_options(args, COMPOSE_OPTIONS_WITH_VALUE)
    if not rest:
        return []
    sub, rest = rest[0].lower(), rest[1:]
    if sub == "down" and any(a in ("--volumes",) or a.startswith("--volumes=") or "v" in short_flags(a) for a in rest):
        return [(DENY, "docker compose down -v deletes the volumes, including local database data")]
    if sub == "push":
        return [(ASK, "pushing an image publishes it")]
    if sub in ("exec", "run"):
        inner = after_options(rest, DOCKER_EXEC_OPTIONS_WITH_VALUE)
        return classify_tokens(inner[1:], "bash", depth + 1, bodies) if inner else []
    return []


# --------------------------------------------------------------------------------------------- monday MCP tools

MONDAY_TOOL = re.compile(r"(?i)^mcp__.*monday")
# Read-only: they pass to Claude Code's normal permission flow.
MONDAY_READ = {
    "agent_catalog", "all_api_read", "all_widgets_schema", "board_insights", "explore_meetings", "get_action",
    "get_assets", "get_automation_runs", "get_automation_statistics", "get_board_activity", "get_board_info",
    "get_board_items_page", "get_column_type_info", "get_form", "get_graphql_schema", "get_meetings_content",
    "get_monday_dev_sprints_boards", "get_monday_knowledge", "get_run_once_trigger_entities", "get_sprint_summary",
    "get_sprints_metadata", "get_type_details", "get_updates", "get_user_context", "get_workflow_run_once_status",
    "invoke_process_planner", "list_actions", "list_automations", "list_users_and_teams", "list_workspaces",
    "read_docs", "search", "search_meetings_content", "show-assign", "show-battery", "show-chart", "show-table",
    "validate_workflow", "vibe_ask", "vibe_get", "vibe_list", "workspace_info",
}
# Changes to boards, items, docs, forms and views that a 3G test workspace may need: each asks for the owner's go.
MONDAY_ASK = {
    "change_item_column_values", "create_board", "create_column", "create_dashboard", "create_doc", "create_folder",
    "create_form", "create_form_submission", "create_group", "create_item", "create_items", "create_notification",
    "create_update", "create_view", "create_view_table", "create_widget", "finalize_asset_upload",
    "form_questions_editor", "get_asset_upload_url", "move_object", "update_column", "update_doc", "update_folder",
    "update_form", "update_items", "update_view", "update_view_table", "vibe_rename",
}
# Destructive, administrative, code-executing, generic-write and automation tools: denied.
MONDAY_DENY = {
    "all_api_write", "all_monday_api", "connect_external_agent", "create_action", "create_automation",
    "create_workflow", "create_workspace", "delete_action", "delete_view", "execute_code", "invoke_workflow_expert",
    "manage_agent", "manage_agent_knowledge", "manage_agent_skills", "manage_agent_triggers", "manage_automations",
    "publish_workflow", "run_action", "run_workflow_once", "stop_workflow_run_once", "submit_bug_or_feature_request",
    "update_action", "update_workspace", "vibe_collaborators", "vibe_create", "vibe_delete", "vibe_publication",
    "vibe_update",
}


def monday_findings(tool_name):
    tool = tool_name.rsplit("__", 1)[-1].lower()
    if tool in MONDAY_DENY:
        return [(DENY, f"monday MCP {tool} is destructive, administrative, executes code or changes automations")]
    if tool in MONDAY_ASK:
        return [(ASK, f"monday MCP {tool} changes real monday data")]
    if tool in MONDAY_READ:
        return []
    if "delete" in tool or re.match(r"(remove|purge|archive|execute|run|publish|manage|connect|grant|revoke|transfer)", tool):
        return [(DENY, f"monday MCP {tool} is unclassified and looks destructive or administrative")]
    return [(ASK, f"monday MCP {tool} is unclassified; it may change real monday data")]


# --------------------------------------------------------------------------------------------- protected files

FILE_TOOLS = {"Edit", "Write", "MultiEdit", "NotebookEdit"}
FROZEN_FILES = (
    "docs/canonical/PRD-v1.md",
    "docs/canonical/SYSTEM-ARCHITECTURE-v1.md",
    "docs/canonical/ARCHITECTURE-INVARIANTS-v1.md",
    "docs/canonical/DEVELOPMENT-GATES-v1.md",
    "docs/premium/FULLY-PREMIUM-ARCHITECTURE-v1.md",
    "docs/premium/PREMIUM-POLICIES-v1.md",
)
FROZEN_LOWER = {path.lower() for path in FROZEN_FILES}
WORKSPACE_DIR = os.path.realpath(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
MIGRATION_FILE = re.compile(r"(?i)^\d{14}_[a-z0-9_]+(\.designer)?\.cs$")
PROTECTED_HINT = re.compile(
    r"(?i)(prd|system-architecture|architecture-invariants|development-gates|fully-premium-architecture|"
    r"premium-policies)-v1\.md|(^|[\\/])\.env(\.|$)|\.github[\\/]+workflows|infra[\\/]+bicep|migrations[\\/]+\d{14}_")
NULL_DEVICES = {"/dev/null", "\\dev\\null", "nul", "$null"}


def file_identity(path):
    try:
        status = os.stat(path)
    except (OSError, ValueError):
        return None
    return (status.st_dev, status.st_ino) if status.st_ino else None


FROZEN_IDENTITIES = {identity for identity in (file_identity(os.path.join(WORKSPACE_DIR, *rel.split("/")))
                                               for rel in FROZEN_FILES) if identity}


def canonical_path(raw, cwd):
    """The absolute, resolved Windows form of a path, or None when it cannot be resolved safely."""
    if not isinstance(raw, str) or not raw.strip() or "\x00" in raw:
        return None
    path = raw.strip().replace("/", "\\")
    if path.startswith(("\\\\?\\", "\\\\.\\")):
        path = path[4:]
        if path.upper().startswith("UNC\\"):
            return None
    if path.startswith("\\\\"):
        return None  # a network or administrative share
    git_bash = re.match(r"^\\(?:mnt\\)?([a-zA-Z])(?:\\(.*))?$", path)
    if git_bash:
        path = git_bash.group(1) + ":\\" + (git_bash.group(2) or "")
    if re.match(r"^[a-zA-Z]:(?!\\)", path):
        return None  # drive-relative, such as C:docs\file
    if not re.match(r"^[a-zA-Z]:\\", path):
        base = canonical_path(cwd, None) if cwd else None
        path = os.path.join(base or WORKSPACE_DIR, path)
    drive, rest = path[:2], path[2:]
    parts = []
    for part in rest.split("\\"):
        part = part.split(":", 1)[0]  # an alternate data stream names the same file
        if part not in (".", ".."):
            part = part.rstrip(" .")  # Windows ignores trailing dots and spaces
        if part.startswith("~") or "$" in part or re.search(r"%[^%]*%", part):
            return None  # a home shortcut or variable only a shell resolves
        parts.append(part)
    try:
        return os.path.realpath(os.path.normpath(drive + "\\".join(parts)))
    except (OSError, ValueError):
        return None


def workspace_relative(path):
    root = WORKSPACE_DIR.rstrip("\\")
    if path.lower() == root.lower():
        return ""
    if path.lower().startswith(root.lower() + "\\"):
        return path[len(root) + 1:].replace("\\", "/").lower()
    return None


def committed_migrations():
    """The lower-case paths of the migrations committed in HEAD, or None when git cannot say."""
    try:
        listed = subprocess.run(["git", "--no-optional-locks", "-C", WORKSPACE_DIR, "ls-tree", "-r", "--name-only", "HEAD"],
                                capture_output=True, timeout=20)
    except (OSError, subprocess.SubprocessError):
        return None
    if listed.returncode != 0:
        return None
    names = listed.stdout.decode("utf-8", "replace").splitlines()
    return {name.lower() for name in names
            if "/migrations/" in "/" + name.lower() and MIGRATION_FILE.match(name.rsplit("/", 1)[-1])}


def protected_findings(raw, cwd, shell=False):
    if isinstance(raw, str) and raw.strip().lower() in NULL_DEVICES:
        return []
    path = canonical_path(raw, cwd)
    if path is None:
        if not shell:
            return [(ASK, f"the guard cannot resolve the path {str(raw)[:120]!r} safely; confirm it is not a protected file")]
        if isinstance(raw, str) and PROTECTED_HINT.search(raw):
            return [(ASK, f"a write to {raw[:120]} names a protected file the guard cannot resolve")]
        return []
    if os.path.basename(path).lower() == SELFTEST:
        return [(DENY, "self-test: the Fully protected-files guard is active")]
    relative = workspace_relative(path)
    identity = file_identity(path)
    if (identity and identity in FROZEN_IDENTITIES) or (relative is not None and relative in FROZEN_LOWER):
        return [(DENY, f"{relative or path} is a frozen document; it changes only with the product owner's explicit approval")]
    if relative is None:
        return []
    name = relative.rsplit("/", 1)[-1]
    if name == ".env" or name.startswith(".env."):
        return [(ASK, f"{relative} holds environment configuration")]
    if relative.startswith(".github/workflows/"):
        return [(ASK, f"{relative} is a CI/CD workflow")]
    if relative.startswith("infra/bicep/"):
        return [(ASK, f"{relative} defines Azure infrastructure")]
    if "/migrations/" in "/" + relative and MIGRATION_FILE.match(name):
        committed = committed_migrations()
        if committed is None:
            return [(ASK, f"the guard cannot tell whether {relative} is a committed migration")]
        if relative in committed:
            return [(ASK, f"{relative} is an existing committed EF Core migration")]
    return []


PS_WRITERS = {"set-content", "sc", "add-content", "ac", "clear-content", "clc", "out-file", "new-item", "ni",
              "remove-item", "ri", "rm", "del", "erase", "rd", "rmdir", "set-item", "si", "rename-item", "ren", "rni",
              "copy-item", "copy", "cpi", "cp", "move-item", "move", "mi", "mv"}
PS_SKIPPED_VALUES = ("-value", "-encoding", "-itemtype", "-inputobject", "-width", "-filter", "-include", "-exclude",
                     "-credential", "-stream", "-delimiter")


def basename_of(path):
    return re.split(r"[\\/]", path.rstrip("/\\"))[-1]


def joined(directories, sources):
    return [directory.rstrip("/\\") + "/" + basename_of(source) for directory in directories for source in sources]


def ps_write_targets(name, args):
    named, positional, i = {}, [], 0
    while i < len(args):
        value = args[i]
        flag = value.lower().partition(":")[0]
        if flag.startswith("-") and len(flag) > 1:
            key = None
            if flag in ("-path", "-literalpath", "-lp", "-pspath", "-filepath") or is_prefix(flag, "-literalpath", 3) \
                    or is_prefix(flag, "-path", 3) or (name == "out-file" and is_prefix(flag, "-filepath", 3)):
                key = "path"
            elif is_prefix(flag, "-destination", 3):
                key = "destination"
            elif is_prefix(flag, "-newname", 3):
                key = "newname"
            elif flag == "-name":
                key = "name"
            elif any(is_prefix(flag, option, 4) for option in PS_SKIPPED_VALUES) or flag in PS_VALUE_PARAMETERS:
                key = "skip"
            if key:
                values = []
                i = collect_values(args, i + 1, values)
                if key != "skip":
                    named.setdefault(key, []).extend(split_list(values))
                continue
            i += 1
            continue
        positional.append(value)
        i += 1
    positional = split_list(positional)
    paths = named.get("path", [])
    first = paths or positional[:1]
    rest = positional if paths else positional[1:]
    if name in ("remove-item", "ri", "rm", "del", "erase", "rd", "rmdir"):
        return paths + positional
    if name in ("new-item", "ni"):
        names = named.get("name", [])
        return joined(first, names) if names else first
    if name in ("rename-item", "ren", "rni"):
        new_names = named.get("newname", []) or rest[:1]
        return first + [os.path.dirname(source.replace("\\", "/")) + "/" + new for source in first for new in new_names]
    if name in ("copy-item", "copy", "cpi", "cp", "move-item", "move", "mi", "mv"):
        destinations = named.get("destination", []) or rest[:1]
        moved = first if name in ("move-item", "move", "mi", "mv") else []
        return destinations + joined(destinations, first) + moved
    return first


def write_targets(name, args, dialect):
    if dialect == "powershell" and name in PS_WRITERS:
        return ps_write_targets(name, args)
    positional = [a for a in args if not a.startswith("-")]
    if name == "tee":
        return positional
    if name in ("sed", "perl") and any(a == "--in-place" or a.startswith("--in-place=") or "i" in short_flags(a) for a in args):
        return positional
    if name in ("rm", "unlink", "shred", "truncate"):
        return positional
    if name in ("cp", "install", "ln", "mv") and positional:
        if len(positional) == 1:
            return positional if name == "mv" else []
        destination, sources = positional[-1], positional[:-1]
        return [destination] + joined([destination], sources) + (sources if name == "mv" else [])
    if name == "dd":
        return [a[3:] for a in args if a.startswith("of=")]
    return []


def shell_write_findings(command, dialect):
    targets = list(command.writes)
    tokens = strip_prefixes(command.tokens, dialect)
    if tokens:
        targets += write_targets(command_name(tokens[0]), tokens[1:], dialect)
    findings = []
    for target in targets:
        findings += protected_findings(target, STATE.get("cwd"), shell=True)
    return findings


# --------------------------------------------------------------------------------------------- hook entry point

def decide(findings):
    denies = list(dict.fromkeys(reason for decision, reason in findings if decision == DENY))
    asks = list(dict.fromkeys(reason for decision, reason in findings if decision == ASK))
    if denies:
        return DENY, ("Fully guard - hard-denied: " + "; ".join(denies[:4]) + ". This never runs from Claude Code in "
                      "Fully; if the product owner orders it, the owner runs it.")
    if asks:
        return ASK, ("Fully guard - approval boundary: " + "; ".join(asks[:4]) + ". Approve only with the product "
                     "owner's explicit go.")
    return None, ""


def main():
    try:
        payload = json.loads(sys.stdin.buffer.read().decode("utf-8", "replace") or "{}")
        tool = payload.get("tool_name", "")
        cwd = payload.get("cwd")
        STATE["cwd"] = cwd if isinstance(cwd, str) and cwd.strip() else None
        if isinstance(tool, str) and MONDAY_TOOL.match(tool):
            findings = monday_findings(tool)
        elif tool in FILE_TOOLS:
            tool_input = payload.get("tool_input") or {}
            findings = protected_findings(tool_input.get("file_path", tool_input.get("notebook_path")), STATE["cwd"])
        else:
            command = (payload.get("tool_input") or {}).get("command", "")
            if tool not in ("Bash", "PowerShell") or not isinstance(command, str) or not command.strip():
                return 0
            if STATE["cwd"]:
                STATE["outside"] = location(STATE["cwd"])[0] != "workspace"
            findings = analyse(command, "powershell" if tool == "PowerShell" else "bash")
    except Exception as error:  # noqa: BLE001 - a guard failure asks; it never allows silently
        findings = [(ASK, f"the guard could not analyse this command ({type(error).__name__})")]
    decision, reason = decide(findings)
    if decision:
        sys.stdout.write(json.dumps({"hookSpecificOutput": {"hookEventName": "PreToolUse",
                                                            "permissionDecision": decision,
                                                            "permissionDecisionReason": reason}}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
