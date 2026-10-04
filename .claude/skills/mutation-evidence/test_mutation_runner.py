"""Dry-run tests for the mutation-evidence runner. Every case runs in a temporary git repository (a sandbox with src/ and
tests/ trees) against a fake dotnet that builds and tests by reading the sandbox files, so nothing is built, tested or
written in the Fully repository. One section reads the real test sources, without writing anything, to prove that the
fresh-database detection matches Fully's own fresh-database test classes.

Run from the repository root: python .claude/skills/mutation-evidence/test_mutation_runner.py
"""
import importlib.util
import json
import os
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ElementTree
from pathlib import Path

sys.dont_write_bytecode = True

HERE = Path(__file__).resolve().parent
PROJECT = Path(os.environ.get("FULLY_SELFTEST_PROJECT") or HERE.parents[2]).resolve()
RESULTS = []
FAKE = {"mode": os.environ.get("FAKE_DOTNET_MODE", ""), "calls": []}
GUARD = "src/App.Core/Guard.cs"
MIGRATION = "src/App.Infrastructure/Persistence/Migrations/20990101000000_Sandbox.cs"
GUARD_LINE = "if (tenant != expected)"

SANDBOX_FILES = {
    GUARD: [
        "namespace Sandbox;",
        "",
        "public static class Guard",
        "{",
        "    public static bool Allows(int tenant, int expected)",
        "    {",
        "        if (tenant != expected)",
        "        {",
        "            return false;",
        "        }",
        "",
        "        return true;",
        "    }",
        "",
        "    // The name is never null.",
        "    public static string? Name() => \"sandbox\";",
        "}",
    ],
    MIGRATION: [
        "namespace Sandbox.Migrations;",
        "",
        "public partial class Sandbox",
        "{",
        "    protected void Up(MigrationBuilder migrationBuilder)",
        "    {",
        "        migrationBuilder.AddCheckConstraint(name: \"ck_sandbox\", table: \"sandbox\", sql: \"value > 0\");",
        "        migrationBuilder.Sql(\"GRANT SELECT ON sandbox TO workcontinuity_app;\");",
        "    }",
        "}",
    ],
    "tests/App.Unit.Tests/App.Unit.Tests.csproj": ["<Project Sdk=\"Microsoft.NET.Sdk\" />"],
    "tests/App.Unit.Tests/GuardTests.cs": [
        "namespace Sandbox.Unit.Tests;",
        "",
        "public sealed class GuardTests",
        "{",
        "    [Fact]",
        "    public void Refuses_a_foreign_tenant() => Assert.False(Guard.Allows(1, SandboxValues.Other));",
        "",
        "    [Fact]",
        "    public void Names_itself() => Assert.NotEmpty(Guard.Name()!);",
        "",
        "    [Fact]",
        "    public void Unrelated() => Assert.True(true);",
        "}",
    ],
    "tests/App.Unit.Tests/SandboxValues.cs": [
        "namespace Sandbox.Unit.Tests;",
        "",
        "public static class SandboxValues",
        "{",
        "    public const int Other = 2;",
        "}",
    ],
    "tests/App.Integration.Tests/App.Integration.Tests.csproj": ["<Project Sdk=\"Microsoft.NET.Sdk\" />"],
    "tests/App.Integration.Tests/SandboxFreshDatabaseTests.cs": [
        "namespace Sandbox.Integration.Tests;",
        "",
        "public sealed class SandboxFreshDatabaseTests",
        "{",
        "    [Fact]",
        "    public async Task The_check_constraint_exists_on_a_fresh_database()",
        "    {",
        "        await using var fresh = await FreshDatabase.CreateAsync(\"wce_sandbox\");",
        "        await fresh.MigrateAsync();",
        "    }",
        "",
        "    private sealed class FreshDatabase : IAsyncDisposable",
        "    {",
        "        // Drops, then runs CREATE DATABASE by an exact name; bootstrap and migration follow.",
        "        internal static Task<FreshDatabase> CreateAsync(string name) => Task.FromResult(new FreshDatabase());",
        "",
        "        internal Task MigrateAsync() => Task.CompletedTask;",
        "",
        "        public ValueTask DisposeAsync() => ValueTask.CompletedTask;",
        "    }",
        "}",
    ],
    "tests/App.Integration.Tests/SandboxSharedDatabaseTests.cs": [
        "namespace Sandbox.Integration.Tests;",
        "",
        "public sealed class SandboxSharedDatabaseTests",
        "{",
        "    [Fact]",
        "    public void Uses_the_shared_database() => Assert.True(true);",
        "}",
    ],
    "tests/App.Security.Tests/App.Security.Tests.csproj": ["<Project Sdk=\"Microsoft.NET.Sdk\" />"],
    "tests/App.Security.Tests/SandboxMigrationPinTests.cs": [
        "namespace Sandbox.Security.Tests;",
        "",
        "public sealed class SandboxMigrationPinTests",
        "{",
        "    [Fact]",
        "    public void Pins_the_migration_text() => Assert.Contains(\"AddCheckConstraint\", \"migration\");",
        "}",
    ],
}

UNIT = {"project": "App.Unit.Tests", "class": "GuardTests"}
FRESH = {"project": "App.Integration.Tests", "class": "SandboxFreshDatabaseTests", "kind": "fresh-db"}
SHARED = {"project": "App.Integration.Tests", "class": "SandboxSharedDatabaseTests"}
PIN = {"project": "App.Security.Tests", "class": "SandboxMigrationPinTests", "kind": "static"}
CHECK_LINE = "        migrationBuilder.AddCheckConstraint(name: \"ck_sandbox\", table: \"sandbox\", sql: \"value > 0\");\n"
GUARD_BLOCK = "        if (tenant != expected)\n        {\n            return false;\n        }\n\n"
MUTANTS = {
    "S01": {"id": "S01", "behaviour": "the tenant guard removed: a foreign tenant is allowed",
            "edits": [{"file": GUARD, "old": GUARD_BLOCK, "new": ""}], "detect": [UNIT]},
    "S02": {"id": "S02", "behaviour": "a comment reworded, which changes no behaviour",
            "edits": [{"file": GUARD, "old": "// The name is never null.", "new": "// The name is not null."}],
            "detect": [UNIT]},
    "S03": {"id": "S03", "behaviour": "a mutant that does not compile",
            "edits": [{"file": GUARD, "old": "        return true;\n", "new": "        return true COMPILE_ERROR;\n"}],
            "detect": [UNIT]},
    "S04": {"id": "S04", "behaviour": "the migration's check constraint removed",
            "edits": [{"file": MIGRATION, "old": CHECK_LINE, "new": ""}], "detect": [FRESH, PIN]},
    "S05": {"id": "S05", "behaviour": "the migration grants the runtime role DELETE",
            "edits": [{"file": MIGRATION, "old": "GRANT SELECT ON sandbox", "new": "GRANT SELECT, DELETE ON sandbox"}],
            "detect": [FRESH, SHARED, PIN]},
    "S06": {"id": "S06", "behaviour": "the name becomes null",
            "edits": [{"file": GUARD, "old": "=> \"sandbox\";", "new": "=> null;"}],
            "detect": [dict(UNIT, method="Names_itself")]},
    "S06X": {"id": "S06X", "behaviour": "the name becomes null, with the intended reason declared",
             "edits": [{"file": GUARD, "old": "=> \"sandbox\";", "new": "=> null;"}],
             "detect": [dict(UNIT, method="Names_itself")], "expect": "NullReferenceException"},
    "S07": {"id": "S07", "behaviour": "the check constraint renamed, which only the static pin sees",
            "edits": [{"file": MIGRATION, "old": "\"ck_sandbox\"", "new": "\"ck_other\""}],
            "detect": [PIN], "evidence": "static", "schema_covered_by": "S04"},
}
NOT_APPLICABLE = {key: "n/a: the sandbox has no such logic" for key in (
    "tenant-predicate", "rls-tenant-binding", "authorization-guard", "identity-transaction-binding",
    "failure-as-success", "replay-idempotency", "secret-log-protection", "authority-revocation")}


def check(description, ok, detail=""):
    RESULTS.append(bool(ok))
    print(f"{'PASS' if ok else 'FAIL'} {description}" + ("" if ok else f"   [{str(detail)[:600]}]"))


# ------------------------------------------------------------------------------------------------- the fake dotnet

def sandbox_text(name):
    return (Path(os.environ["FULLY_MUTATION_ROOT"]) / name).read_text(encoding="utf-8")


def rule_guard():
    return None if GUARD_LINE in sandbox_text(GUARD) else ("Xunit.Sdk.FalseException", "Assert.False() Failure")


def rule_name():
    if "=> null;" in sandbox_text(GUARD):
        return "System.NullReferenceException", "System.NullReferenceException : Object reference not set"
    return None


def rule_unrelated():
    return ("Xunit.Sdk.TrueException", "Assert.True() Failure") if FAKE["mode"] == "control-fail" else None


def rule_check():
    if "AddCheckConstraint" in sandbox_text(MIGRATION):
        return None
    return "Xunit.Sdk.EqualException", "Assert.Equal() Failure: the check constraint is missing"


def rule_pin():
    text = sandbox_text(MIGRATION)
    if all(part in text for part in ("AddCheckConstraint", "\"ck_sandbox\"", "GRANT SELECT ON sandbox TO")):
        return None
    return "Xunit.Sdk.ContainsException", "Assert.Contains() Failure: the migration text changed"


def rule_pass():
    return None


FAKE_TESTS = {
    "App.Unit.Tests": [("Sandbox.Unit.Tests.GuardTests", "Refuses_a_foreign_tenant", rule_guard),
                       ("Sandbox.Unit.Tests.GuardTests", "Names_itself", rule_name),
                       ("Sandbox.Unit.Tests.GuardTests", "Unrelated", rule_unrelated)],
    "App.Integration.Tests": [("Sandbox.Integration.Tests.SandboxFreshDatabaseTests",
                               "The_check_constraint_exists_on_a_fresh_database", rule_check),
                              ("Sandbox.Integration.Tests.SandboxSharedDatabaseTests", "Uses_the_shared_database",
                               rule_pass)],
    "App.Security.Tests": [("Sandbox.Security.Tests.SandboxMigrationPinTests", "Pins_the_migration_text", rule_pin)],
}


def fake_dotnet(arguments, timeout=None):
    """Stands in for dotnet build and dotnet test (Microsoft.Testing.Platform with an xunit XML report)."""
    FAKE["calls"].append(list(arguments))
    root = Path(os.environ["FULLY_MUTATION_ROOT"])
    if arguments[0] == "build":
        broken = any("COMPILE_ERROR" in path.read_text(encoding="utf-8") for path in (root / "src").rglob("*.cs"))
        return (1, "Guard.cs(12,20): error CS1002: ; expected\n") if broken else (0, "Build succeeded.\n")
    project = arguments[arguments.index("--project") + 1].split("/")[1]
    directory = Path(arguments[arguments.index("--results-directory") + 1])
    report = arguments[arguments.index("--report-xunit-xml-filename") + 1]
    classes = [arguments[i + 1] for i, value in enumerate(arguments) if value == "--filter-class"]
    methods = [arguments[i + 1] for i, value in enumerate(arguments) if value == "--filter-method"]
    mutated = GUARD_LINE not in sandbox_text(GUARD)
    if FAKE["mode"] == "kill" and mutated:
        os._exit(9)

    def like(pattern, value):
        return value.endswith(pattern[1:]) if pattern.startswith("*") else value == pattern

    outcomes = []
    for type_name, method, rule in FAKE_TESTS.get(project, []):
        if classes and methods:
            continue  # different filter types AND together: nothing matches both a class and a method filter
        if classes and not any(like(pattern, type_name) for pattern in classes):
            continue
        if methods and not any(like(pattern, f"{type_name}.{method}") for pattern in methods):
            continue
        failure = rule()
        if FAKE["mode"] == "pipeline" and mutated and project == "App.Unit.Tests":
            failure = ("Xunit.Sdk.TestPipelineException", "Collection fixture type 'Fixture' threw in InitializeAsync")
        outcomes.append((type_name, method, failure))
    if FAKE["mode"] == "zero":
        outcomes = []
    failed = sum(1 for outcome in outcomes if outcome[2])
    root_element = ElementTree.Element("assemblies")
    assembly = ElementTree.SubElement(root_element, "assembly", total=str(len(outcomes)), failed=str(failed), errors="0")
    collection = ElementTree.SubElement(assembly, "collection")
    for type_name, method, failure in outcomes:
        test = ElementTree.SubElement(collection, "test", name=f"{type_name}.{method}", type=type_name, method=method,
                                      result="Fail" if failure else "Pass")
        if failure:
            element = ElementTree.SubElement(test, "failure", {"exception-type": failure[0]})
            ElementTree.SubElement(element, "message").text = failure[1]
    directory.mkdir(parents=True, exist_ok=True)
    ElementTree.ElementTree(root_element).write(directory / report, encoding="utf-8", xml_declaration=True)
    return (8 if not outcomes else 2 if failed else 0), f"total: {len(outcomes)}\nfailed: {failed}\n"


# ------------------------------------------------------------------------------------------------------ harness

def load_runner():
    spec = importlib.util.spec_from_file_location("mutation_runner_under_test", HERE / "mutation_runner.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def remove_tree(path):
    def writable(function, target, _):
        os.chmod(target, stat.S_IWRITE)
        function(target)
    if os.path.exists(path):
        shutil.rmtree(path, onexc=writable)


def git(root, *arguments):
    return subprocess.run(["git", "-C", str(root), *arguments], capture_output=True, check=True)


def make_sandbox(base, name):
    root = base / name
    for relative, lines in SANDBOX_FILES.items():
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(("\n".join(lines) + "\n").encode("utf-8"))
    git(root, "init", "-q")
    git(root, "config", "core.autocrlf", "false")
    git(root, "config", "user.name", "self-test")
    git(root, "config", "user.email", "self-test@example.invalid")
    git(root, "add", "--", "src", "tests")
    git(root, "commit", "-q", "-m", "sandbox")
    return root.resolve()


def write_plan(directory, campaign, ids, judgments=None, security=None, extra=None):
    directory.mkdir(parents=True, exist_ok=True)
    plan = {"campaign": campaign, "scope": "self-test sandbox: the guard and the migration",
            "security_classes": security or dict(NOT_APPLICABLE),
            "mutants": [json.loads(json.dumps(MUTANTS[mutant_id])) for mutant_id in ids]}
    if judgments:
        plan["judgments"] = judgments
    if extra:
        plan.update(extra)
    path = directory / f"{campaign}-mutants.json"
    path.write_bytes(json.dumps(plan, indent=1).encode("utf-8"))
    return path


class Runner:
    """Calls the runner in-process with the fake dotnet, only ever against a sandbox."""

    def __init__(self, runner, temp_root):
        self.runner, self.temp_root = runner, temp_root

    def use(self, sandbox):
        os.environ["FULLY_MUTATION_ROOT"] = str(sandbox)
        self.runner.REPO = Path(sandbox).resolve()

    def __call__(self, *arguments):
        if not str(self.runner.REPO).startswith(str(self.temp_root)):
            raise SystemExit(f"refusing to run the runner outside the sandbox: {self.runner.REPO}")
        self.runner.dotnet = fake_dotnet
        from io import StringIO
        captured, original = StringIO(), sys.stdout
        sys.stdout = captured
        try:
            code = self.runner.main([str(argument) for argument in arguments])
        finally:
            sys.stdout = original
        return code, captured.getvalue()


def sha(path):
    import hashlib
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def records(plan_path, campaign):
    path = plan_path.parent / f"{campaign}-mutation" / "results.jsonl"
    if not path.is_file():
        return []
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def latest(plan_path, campaign):
    found = {}
    for record in records(plan_path, campaign):
        found[record["mutant"]] = record
    return found


# ------------------------------------------------------------------------------------------------------- the cases

def classification_cases(runner):
    mutant = {"detect": [{"project": "P", "class": "GuardTests", "method": None, "kind": "behavioural"}],
              "expect": None, "schema": False, "evidence": None, "covered_by": None}

    def run(code, tests, total=None):
        report = None if tests is None else {"total": len(tests) if total is None else total, "errors": 0,
                                             "tests": [{"type": f"N.{t[0]}", "method": t[1], "name": "", "result": t[2],
                                                        "exception": t[3], "message": t[4]} for t in tests]}
        return {"project": "P", "kind": "class", "exit": code, "report": report}

    assertion = ("GuardTests", "M", "Fail", "Xunit.Sdk.EqualException", "Assert.Equal() Failure")
    verdict = runner.classify(mutant, False, ["Guard.cs(1,1): error CS1002"], [], {})
    check("classify: a mutant that does not compile is INVALID, never CAUGHT", verdict["verdict"] == "INVALID", verdict)
    verdict = runner.classify(mutant, True, [], [run(8, [], total=0)], {})
    check("classify: exit code 8 (zero tests ran) is a broken run, never CAUGHT",
          verdict["verdict"] == "SURVIVED" and verdict["broken"] and "zero tests" in verdict["reason"], verdict)
    verdict = runner.classify(mutant, True, [], [run(3, [assertion])], {})
    check("classify: an aborted session (exit 3) is a broken run, even with a failed assertion in its report",
          verdict["verdict"] == "SURVIVED" and verdict["broken"], verdict)
    verdict = runner.classify(mutant, True, [], [run(2, None)], {})
    check("classify: a run without an xunit report is broken", verdict["broken"], verdict)
    verdict = runner.classify(mutant, True, [], [run(0, [assertion])], {})
    check("classify: an exit code that disagrees with the report is broken", verdict["broken"], verdict)
    verdict = runner.classify(mutant, True, [], [run(None, [assertion])], {})
    check("classify: a run killed for its timeout is broken", verdict["broken"], verdict)
    verdict = runner.classify(mutant, True, [], [run(2, [assertion])], {})
    check("classify: an xunit assertion failing in a detecting test is CAUGHT", verdict["verdict"] == "CAUGHT", verdict)
    other = ("OtherTests", "M", "Fail", "Xunit.Sdk.EqualException", "Assert.Equal() Failure")
    verdict = runner.classify(mutant, True, [], [run(2, [other, ("GuardTests", "N", "Pass", "", "")])], {})
    check("classify: an assertion failing only outside the detecting tests is SURVIVED",
          verdict["verdict"] == "SURVIVED", verdict)
    crash = ("GuardTests", "M", "Fail", "System.NullReferenceException", "Object reference not set")
    verdict = runner.classify(mutant, True, [], [run(2, [crash])], {})
    check("classify: a non-assertion failure without expect is SURVIVED (reason unconfirmed)",
          verdict["verdict"] == "SURVIVED" and verdict["unconfirmed"], verdict)
    timeout = ("GuardTests", "M", "Fail", "Xunit.Sdk.TestTimeoutException", "Test execution timed out")
    verdict = runner.classify(mutant, True, [], [run(2, [timeout])], {})
    check("classify: an xunit timeout is not an assertion: SURVIVED without expect", verdict["verdict"] == "SURVIVED",
          verdict)
    verdict = runner.classify(dict(mutant, expect="TestTimeoutException"), True, [], [run(2, [timeout])], {})
    check("classify: a declared expect accepts its intended non-assertion reason", verdict["verdict"] == "CAUGHT",
          verdict)
    verdict = runner.classify(dict(mutant, expect="the verifier was deleted"), True, [], [run(2, [assertion])], {})
    check("classify: a declared expect also narrows: an assertion for another reason is SURVIVED",
          verdict["verdict"] == "SURVIVED", verdict)
    fixture = ("GuardTests", "M", "Fail", "Xunit.Sdk.TestPipelineException", "Collection fixture threw")
    verdict = runner.classify(mutant, True, [], [run(2, [fixture])], {})
    check("classify: a fixture failure is a broken run (the environment), not a verdict",
          verdict["broken"] and verdict["verdict"] == "SURVIVED", verdict)
    schema = dict(mutant, schema=True, evidence="schema",
                  detect=[{"project": "P", "class": "FreshTests", "method": None, "kind": "fresh-db"},
                          {"project": "P", "class": "PinTests", "method": None, "kind": "static"}])
    pin = ("PinTests", "Pins", "Fail", "Xunit.Sdk.ContainsException", "Assert.Contains() Failure")
    verdict = runner.classify(schema, True, [], [run(2, [pin, ("FreshTests", "F", "Pass", "", "")])],
                              {("P", "FreshTests"): {"F"}})
    check("classify: a schema mutant caught only by a static pin is SURVIVED (no fresh-database evidence)",
          verdict["verdict"] == "SURVIVED" and "fresh-database" in verdict["reason"], verdict)
    fresh_failure = ("FreshTests", "F", "Fail", "Xunit.Sdk.EqualException", "Assert.Equal() Failure")
    verdict = runner.classify(schema, True, [], [run(2, [fresh_failure])], {("P", "FreshTests"): set()})
    check("classify: a failure in a fresh-db class whose method creates no fresh database is not fresh evidence",
          verdict["verdict"] == "SURVIVED", verdict)
    verdict = runner.classify(schema, True, [], [run(2, [fresh_failure])], {("P", "FreshTests"): {"F"}})
    check("classify: a schema mutant a fresh-database test detects is CAUGHT", verdict["verdict"] == "CAUGHT", verdict)


def validation_cases(call, temp_root, base_sandbox):
    plans = temp_root / "plans-validation"
    before = {name: sha(base_sandbox / name) for name in SANDBOX_FILES}
    path = write_plan(plans, "valid", list(MUTANTS))
    code, output = call("check", "--plan", path)
    check("check: the sandbox plan with all eight mutants is valid", code == 0, output)
    check("check: S04, S05 and S07 are recognised as schema mutants",
          output.count("[schema, evidence schema]") == 2 and output.count("[schema, evidence static]") == 1, output)

    def broken(label, phrase, mutate=None, ids=("S01",), extra=None, judgments=None, security=None, directory=None):
        plan_path = write_plan(directory or plans, f"bad{len(RESULTS)}", list(ids), judgments=judgments,
                               security=security, extra=extra)
        if mutate:
            plan = json.loads(plan_path.read_text(encoding="utf-8"))
            mutate(plan)
            plan_path.write_bytes(json.dumps(plan).encode("utf-8"))
        code, output = call("check", "--plan", plan_path)
        check(f"check refuses {label}", code != 0 and phrase in output, output[-700:])

    broken("a migration mutant without a fresh-db detecting test", "needs a fresh-db detecting test",
           mutate=lambda plan: plan["mutants"][0].update(detect=[SHARED, PIN]), ids=("S04",))
    broken("a fresh-db kind on a class that runs on the shared database", "does not create and migrate a fresh",
           mutate=lambda plan: plan["mutants"][0].update(detect=[dict(SHARED, kind="fresh-db")]), ids=("S04",))
    broken("a mutant with no detecting test (the full suite would be the only option)",
           "detect must name at least one detecting test", mutate=lambda plan: plan["mutants"][0].update(detect=[]))
    broken("a wildcard instead of a test class", "without wildcards",
           mutate=lambda plan: plan["mutants"][0].update(detect=[{"project": "App.Unit.Tests", "class": "*"}]))
    broken("a detecting entry without a class (a whole project)", "class must be a test class name",
           mutate=lambda plan: plan["mutants"][0].update(detect=[{"project": "App.Unit.Tests"}]))
    broken("an edit outside src/", "is not production code under src/",
           mutate=lambda plan: plan["mutants"][0]["edits"][0].update(file="tests/App.Unit.Tests/GuardTests.cs"))
    broken("an anchor that is not found", "anchor found 0 times",
           mutate=lambda plan: plan["mutants"][0]["edits"][0].update(old="no such text"))
    broken("an anchor found twice", "anchor found 2 times",
           mutate=lambda plan: plan["mutants"][0]["edits"][0].update(old="    }\n"))
    broken("an edit whose new text equals the old", "different new text",
           mutate=lambda plan: plan["mutants"][0]["edits"][0].update(new=GUARD_BLOCK))
    broken("a judgment that would make a mutant CAUGHT", "nothing can be judged CAUGHT",
           judgments={"S01": {"verdict": "CAUGHT", "reason": "the owner says it is fine to count it"}})
    broken("a judgment without a real reason", "give the reason", judgments={"S01": {"verdict": "INVALID", "reason": "x"}})
    broken("a plan without every security class", "give exactly these keys",
           security={key: value for key, value in NOT_APPLICABLE.items() if key != "tenant-predicate"})
    broken("a security class marked n/a without a reason", "a list of this plan's mutant ids",
           security=dict(NOT_APPLICABLE, **{"tenant-predicate": "n/a:"}))
    broken("a security class naming an unknown mutant", "a list of this plan's mutant ids",
           security=dict(NOT_APPLICABLE, **{"tenant-predicate": ["S99"]}))
    broken("a static-evidence schema mutant without schema_covered_by", "needs schema_covered_by",
           mutate=lambda plan: plan["mutants"][0].pop("schema_covered_by"), ids=("S07",))
    broken("a schema_covered_by naming a mutant that is not a schema mutant of the same file",
           "must be a schema mutant of the same file", ids=("S07", "S01"),
           mutate=lambda plan: plan["mutants"][0].update(schema_covered_by="S01"))
    broken("a detecting project that is not a test project", "is not a test project",
           mutate=lambda plan: plan["mutants"][0].update(detect=[{"project": "App.Nothing.Tests", "class": "GuardTests"}]))
    broken("a duplicate mutant id", "must be unique", ids=("S01", "S01"))
    broken("a test method the class does not declare", "has no test method",
           mutate=lambda plan: plan["mutants"][0].update(detect=[dict(UNIT, method="No_such_test")]))
    broken("a plan inside the repository", "inside the repository", directory=base_sandbox / "plans")
    after = {name: sha(base_sandbox / name) for name in SANDBOX_FILES}
    check("check writes nothing: every sandbox file keeps its SHA-256", before == after)
    check("check creates no campaign directory", not list(plans.glob("*-mutation")))


def taxonomy_cases(call, runner, temp_root):
    sandbox = make_sandbox(temp_root, "taxonomy")
    call.use(sandbox)
    plans = temp_root / "plans-taxonomy"
    path = write_plan(plans, "tax", ["S01", "S02", "S03", "S04", "S05", "S06", "S06X", "S07"])
    pristine = {GUARD: sha(sandbox / GUARD), MIGRATION: sha(sandbox / MIGRATION)}
    code, output = call("baseline", "--plan", path)
    campaign = plans / "tax-mutation"
    check("baseline: records the plan's production files, their hashes and byte backups",
          code == 0 and (campaign / "baseline.json").is_file()
          and all((campaign / "originals" / digest).is_file() for digest in pristine.values()), output)
    FAKE["calls"].clear()
    code, output = call("run", "--plan", path)
    found = latest(path, "tax")
    check("run: by default a bounded batch of three, the first pending mutants in plan order",
          code == 0 and [record["mutant"] for record in records(path, "tax")] == ["S01", "S02", "S03"], output)
    check("run: S01 (guard removed) is CAUGHT", found["S01"]["verdict"] == "CAUGHT", found.get("S01"))
    check("run: S02 (a comment) SURVIVED: every detecting test passed",
          found["S02"]["verdict"] == "SURVIVED" and "passed" in found["S02"]["reason"], found.get("S02"))
    check("run: S03 (does not compile) is INVALID", found["S03"]["verdict"] == "INVALID", found.get("S03"))
    check("run: every mutated file is back at its baseline SHA-256 after the batch",
          all(sha(sandbox / name) == digest for name, digest in pristine.items()))
    check("run: each mutant recorded a verified restoration",
          all(all("SHA-256 verified" in how for how in found[key]["restored"].values()) for key in ("S01", "S02", "S03")))
    check("run: no journal and no lock remain", not (campaign / "inflight.json").exists()
          and not (campaign / "runner.lock").exists())
    code, output = call("run", "S07", "S04", "--plan", path)
    order = [record["mutant"] for record in records(path, "tax")][3:]
    found = latest(path, "tax")
    check("run: named mutants run in plan order, not argument order (S04 before S07)", order == ["S04", "S07"], order)
    check("run: S04 (check constraint removed) is CAUGHT by the fresh-database test",
          found["S04"]["verdict"] == "CAUGHT" and any(item["fresh"] for item in found["S04"]["qualifying"]),
          found.get("S04"))
    check("run: S07 (static guard vacuity, covered by S04) is CAUGHT by its static pin",
          found["S07"]["verdict"] == "CAUGHT", found.get("S07"))
    code, output = call("run", "--plan", path)
    found = latest(path, "tax")
    check("run: S05 (DELETE granted) SURVIVED: only the static pin saw it, no fresh-database test did",
          found["S05"]["verdict"] == "SURVIVED" and "no fresh-database test" in found["S05"]["reason"], found.get("S05"))
    check("run: S06 (a crash, no expect) SURVIVED: the reason is unconfirmed",
          found["S06"]["verdict"] == "SURVIVED" and "unconfirmed" in found["S06"]["reason"], found.get("S06"))
    check("run: S06X (the same crash, expect declared) is CAUGHT", found["S06X"]["verdict"] == "CAUGHT",
          found.get("S06X"))
    code, output = call("run", "--max", "7", "--plan", path)
    check("run: a batch larger than the hard maximum is refused", code == 1 and "--max must be" in output, output)
    code, output = call("run", "--max", "0", "--plan", path)
    check("run: an empty batch size is refused", code == 1, output)
    tests = [call_ for call_ in FAKE["calls"] if call_[0] == "test"]
    check("run: every test invocation selects tests with exactly one filter type; none runs a whole project",
          tests and all(("--filter-class" in item) != ("--filter-method" in item) for item in tests), tests[:2])
    check("run: every test invocation is --no-build and writes an xunit XML report",
          all("--no-build" in item and "--report-xunit-xml" in item for item in tests))
    check("run: nothing ever builds or tests a solution",
          not any(any(part.endswith((".sln", ".slnx")) or part == "--solution" for part in item)
                  for item in FAKE["calls"]))
    check("run: a control run on the unmutated tree precedes each selection's first mutant",
          any(item for item in tests if any("control-" in part for part in item)))

    code, output = call("status", "--plan", path)
    check("status: NOT READY while mutants SURVIVED or stay INVALID without a justification",
          code == 1 and "NOT READY" in output and "3 SURVIVED" in output, output[-800:])
    check("status: the totals count INVALID apart from CAUGHT (4 / 8 CAUGHT, 3 SURVIVED, 1 INVALID)",
          "4 / 8 CAUGHT\n3 SURVIVED\n1 INVALID" in output, output)
    check("status: the report has the eight sections of the output contract",
          all(f"## {number}. " in output for number in range(1, 9)), output[:400])
    check("status: no gate-closure manifest exists while NOT READY", not (plans / "tax-mutation.sha256").exists())
    check("status: fresh-database evidence names S04's fresh test and flags S05",
          "S04 (" in output and "fresh database: SandboxFreshDatabaseTests" in output
          and "NO fresh-database detection" in output, output)
    check("status: the gaps list every SURVIVED mutant", all(f"{key} SURVIVED" in output for key in ("S02", "S05", "S06")))

    judgments = {"S02": {"verdict": "INVALID", "reason": "equivalent: a comment cannot change compiled behaviour"},
                 "S03": {"verdict": "INVALID", "reason": "restated as nothing: the compiler forbids the change"},
                 "S05": {"verdict": "INVALID", "reason": "owner-accepted equivalent at the final schema (sandbox)"},
                 "S06": {"verdict": "INVALID", "reason": "superseded by S06X, which declares the intended reason"}}
    path = write_plan(plans, "tax", ["S01", "S02", "S03", "S04", "S05", "S06", "S06X", "S07"], judgments=judgments)
    code, output = call("status", "--plan", path)
    manifest = plans / "tax-mutation.sha256"
    check("status: READY once every SURVIVED or INVALID mutant carries an explicit INVALID justification",
          code == 0 and "**READY**" in output, output[-900:])
    check("status: judged mutants count as INVALID, never CAUGHT (4 / 8 CAUGHT, 0 SURVIVED, 4 INVALID)",
          "4 / 8 CAUGHT\n0 SURVIVED\n4 INVALID" in output, output)
    check("status: each justification is listed in the remaining gaps",
          all(judgment["reason"] in output for judgment in judgments.values()), output)
    lines = manifest.read_text(encoding="utf-8").splitlines() if manifest.is_file() else []
    names = [line.split(" *", 1)[1] for line in lines if " *" in line]
    check("status: the manifest has sha256sum lines for the production files, the detecting test files and the plan",
          lines and all(re.match(r"^[0-9a-f]{64} \*\S", line) for line in lines)
          and {GUARD, MIGRATION, "tests/App.Unit.Tests/GuardTests.cs",
               "tests/App.Integration.Tests/SandboxFreshDatabaseTests.cs",
               "tests/App.Security.Tests/SandboxMigrationPinTests.cs"} <= set(names)
          and path.resolve().as_posix() in names, lines)
    check("status: the manifest includes the support files a detecting test uses (SandboxValues.cs)",
          "tests/App.Unit.Tests/SandboxValues.cs" in names, names)
    check("status: every manifest hash matches the file now",
          all(sha(Path(name) if ":" in name else sandbox / name) == line[:64] for line, name in zip(lines, names)))
    sha256sum = shutil.which("sha256sum") or next(
        (candidate for candidate in ("C:/Program Files/Git/usr/bin/sha256sum.exe",) if os.path.exists(candidate)), None)
    if sha256sum:
        verified = subprocess.run([sha256sum, "-c", str(manifest)], cwd=sandbox, capture_output=True)
        check("status: sha256sum -c accepts the manifest from the repository root", verified.returncode == 0,
              verified.stdout[-400:])
    else:
        print("SKIP sha256sum is not installed here; the Python hash check above covers the manifest")
    all_ids = ["S01", "S02", "S03", "S04", "S05", "S06", "S06X", "S07"]
    path = write_plan(plans, "tax", all_ids, judgments={key: value for key, value in judgments.items() if key != "S02"})
    code, output = call("status", "--plan", path)
    check("status: one SURVIVED mutant without a judgment, and nothing else wrong, keeps it NOT READY without a manifest",
          code == 1 and "**NOT READY**: 1 SURVIVED" in output and "S02 SURVIVED" in output and not manifest.exists(),
          output[-700:])
    path = write_plan(plans, "tax", all_ids, judgments=judgments)

    test_file = sandbox / "tests/App.Unit.Tests/GuardTests.cs"
    original = test_file.read_bytes()
    test_file.write_bytes(original + b"// a later edit\n")
    code, output = call("status", "--plan", path)
    check("staleness: a changed detecting test file makes the evidence stale and NOT READY",
          code == 1 and "stale: test file tests/App.Unit.Tests/GuardTests.cs changed" in output, output[-900:])
    check("staleness: the gate-closure manifest is removed while stale", not manifest.exists())
    test_file.write_bytes(original)
    code, output = call("status", "--plan", path)
    check("staleness: restoring the exact bytes makes it READY again", code == 0, output[-400:])
    support_file = sandbox / "tests/App.Unit.Tests/SandboxValues.cs"
    support_original = support_file.read_bytes()
    support_file.write_bytes(support_original.replace(b"Other = 2", b"Other = 3"))
    code, output = call("status", "--plan", path)
    check("staleness: a changed support file of a detecting test makes the evidence stale", code == 1, output[-400:])
    support_file.write_bytes(support_original)
    guard = sandbox / GUARD
    guard_original = guard.read_bytes()
    guard.write_bytes(guard_original + b"// a fix after the baseline\n")
    code, output = call("status", "--plan", path)
    check("staleness: a production file changed after the baseline is NOT READY",
          code == 1 and "differs from its baseline SHA-256" in output, output[-600:])
    code, output = call("run", "S01", "--plan", path)
    check("run: refused while a production file differs from the baseline", code == 1 and "baseline" in output, output)
    guard.write_bytes(guard_original)
    changed = json.loads(path.read_text(encoding="utf-8"))
    changed["mutants"][1]["behaviour"] = "a comment reworded (restated)"
    path.write_bytes(json.dumps(changed, indent=1).encode("utf-8"))
    code, output = call("status", "--plan", path)
    check("definitions: a changed mutant definition makes its evidence stale",
          code == 1 and "S02 stale: the mutant definition changed" in output, output[-600:])
    code, output = call("run", "S02", "--plan", path)
    code, output = call("status", "--plan", path)
    check("definitions: rerun, the redefined mutant is current again and its earlier SURVIVED attempt is reported",
          code == 0 and "S02 earlier attempt SURVIVED" in output and "the behaviour text changed" in output,
          output[-900:])


def restore_cases(call, runner, temp_root):
    sandbox = make_sandbox(temp_root, "restore")
    call.use(sandbox)
    plans = temp_root / "plans-restore"
    path = write_plan(plans, "res", ["S01"])
    call("baseline", "--plan", path)
    pristine = sha(sandbox / GUARD)
    original_write = runner.write_file
    seen = {"faults": 0}

    def corrupt_first_restore(target, data):
        if Path(target).name == "Guard.cs" and GUARD_LINE.encode() in data and seen["faults"] == 0:
            seen["faults"] += 1
            return original_write(target, b"garbage")
        return original_write(target, data)

    runner.write_file = corrupt_first_restore
    code, output = call("run", "--plan", path)
    runner.write_file = original_write
    found = latest(path, "res")
    check("restore: a restore that writes the wrong bytes is detected and repaired from the byte backup",
          code == 0 and seen["faults"] == 1 and sha(sandbox / GUARD) == pristine
          and "byte backup" in found["S01"]["restored"][GUARD], (output, found.get("S01")))

    def corrupt_every_restore(target, data):
        if Path(target).name == "Guard.cs" and GUARD_LINE.encode() in data:
            return original_write(target, b"garbage")
        return original_write(target, data)

    runner.write_file = corrupt_every_restore
    code, output = call("run", "S01", "--plan", path)
    runner.write_file = original_write
    campaign = plans / "res-mutation"
    check("restore: an unverifiable restoration stops everything with exit code 2",
          code == 2 and "RESTORE FAILED" in output, output)
    check("restore: the journal and the lock are kept for recovery",
          (campaign / "inflight.json").is_file() and (campaign / "runner.lock").is_file())
    check("restore: the file is left visibly wrong, not silently accepted", sha(sandbox / GUARD) != pristine)
    code, output = call("run", "--plan", path)
    check("restore: no further mutant runs while a journal or lock exists", code == 1 and "recover" in output, output)
    code, output = call("status", "--plan", path)
    check("restore: status is NOT READY while the journal exists", code == 1 and "in flight" in output, output[-500:])
    code, output = call("recover", "--plan", path)
    check("recover: restores the file from its byte backup and verifies its SHA-256",
          code == 0 and sha(sandbox / GUARD) == pristine, output)
    check("recover: removes the journal and the lock", not (campaign / "inflight.json").exists()
          and not (campaign / "runner.lock").exists())
    check("recover: records the interrupted attempt", any(record["verdict"] == "INTERRUPTED"
                                                          for record in records(path, "res")))
    code, output = call("status", "--plan", path)
    check("recover: the earlier CAUGHT evidence stands, and the interruption is reported",
          code == 0 and "S01 earlier attempt INTERRUPTED" in output, output[-700:])


def environment_cases(call, runner, temp_root):
    sandbox = make_sandbox(temp_root, "environment")
    call.use(sandbox)
    plans = temp_root / "plans-environment"
    path = write_plan(plans, "env", ["S01", "S02"])
    call("baseline", "--plan", path)
    pristine = sha(sandbox / GUARD)
    for mode, phrase, label in (("zero", "zero tests ran", "a filter matching zero tests"),
                                ("control-fail", "fail on the unmutated tree", "a detecting test failing unmutated")):
        FAKE["mode"] = mode
        code, output = call("run", "--plan", path)
        FAKE["mode"] = ""
        check(f"control: {label} refuses the batch before any mutation",
              code == 1 and phrase in output and sha(sandbox / GUARD) == pristine and not records(path, "env"), output)
        cached = json.loads((plans / "env-mutation" / "controls.json").read_text(encoding="utf-8")) \
            if (plans / "env-mutation" / "controls.json").is_file() else {}
        check(f"control: {label} is not cached, so the next run tries again", not cached, cached)
    FAKE["mode"] = "pipeline"
    code, output = call("run", "--plan", path)
    FAKE["mode"] = ""
    found = latest(path, "env")
    check("broken run: a fixture failure during a mutant is SURVIVED (unproven), never CAUGHT",
          found.get("S01", {}).get("verdict") == "SURVIVED" and found["S01"]["broken"], found)
    check("broken run: the batch stops (S02 not run) and exits 1", code == 1 and "S02" not in found, output)
    check("broken run: the file is restored", sha(sandbox / GUARD) == pristine)
    judged = write_plan(plans, "env", ["S01", "S02"],
                        judgments={"S01": {"verdict": "INVALID", "reason": "an attempt to explain away a broken run"}})
    code, output = call("status", "--plan", judged)
    check("broken run: a judgment cannot turn a broken run into INVALID; it stays SURVIVED and NOT READY",
          code == 1 and "S01 SURVIVED" in output, output[-700:])
    runner.BATCH_BUDGET = 0
    code, output = call("run", "--plan", path)
    runner.BATCH_BUDGET = 480
    check("budget: no mutant starts when the batch budget is used up; the stop is reported",
          code == 0 and "budget" in output and sha(sandbox / GUARD) == pristine, output)


def kill_cases(call, temp_root):
    sandbox = make_sandbox(temp_root, "kill")
    call.use(sandbox)
    plans = temp_root / "plans-kill"
    path = write_plan(plans, "kill", ["S01"])
    call("baseline", "--plan", path)
    pristine = sha(sandbox / GUARD)
    script = (f"import sys; sys.dont_write_bytecode = True; sys.path.insert(0, {str(HERE)!r}); "
              "import test_mutation_runner as t; runner = t.load_runner(); runner.dotnet = t.fake_dotnet; "
              f"sys.exit(runner.main(['run', '--plan', {str(path)!r}]))")
    environment = dict(os.environ, FULLY_MUTATION_ROOT=str(sandbox), FAKE_DOTNET_MODE="kill")
    child = subprocess.run([sys.executable, "-c", script], capture_output=True, env=environment, timeout=120)
    campaign = plans / "kill-mutation"
    check("kill: a runner killed mid-mutant leaves the mutation in place (no finally ran)",
          child.returncode == 9 and sha(sandbox / GUARD) != pristine, (child.returncode, child.stderr[-300:]))
    check("kill: the journal and the lock name what was in flight",
          (campaign / "inflight.json").is_file() and (campaign / "runner.lock").is_file())
    code, output = call("run", "--plan", path)
    check("kill: nothing else runs until recover", code == 1 and "recover" in output, output)
    code, output = call("recover", "--plan", path)
    check("kill: recover restores the exact bytes and verifies them", code == 0 and sha(sandbox / GUARD) == pristine,
          output)
    code, output = call("status", "--plan", path)
    check("kill: the interrupted mutant is NOT READY until it is run again", code == 1 and "S01 not run" in output,
          output[-500:])
    call("run", "--plan", path)
    code, output = call("status", "--plan", path)
    check("kill: run again, it is CAUGHT and the campaign READY", code == 0, output[-500:])


def real_repository_cases(runner):
    """Reads Fully's test sources only; writes nothing."""
    runner.REPO = PROJECT
    index = runner.TestIndex()
    for name in ("ConnectorAuthorizationFreshDatabaseTests", "ReconciliationTargetDatabaseSecurityTests",
                 "InitialSyncDatabaseSecurityTests"):
        methods = set(index.test_methods("App.Integration.Tests", name))
        fresh = runner.fresh_methods(index, "App.Integration.Tests", name)
        check(f"real repository: every test of {name} creates a fresh database ({len(methods)} tests)",
              methods and methods == fresh, (methods, fresh))
    for name in ("ConnectorAuthorizationStoreTests", "MondayConnectionLifecycleTests", "InitialSyncTests"):
        fresh = runner.fresh_methods(index, "App.Integration.Tests", name)
        check(f"real repository: {name} runs on the shared database, so it is no fresh-database evidence", not fresh,
              fresh)
    check("real repository: the migrations directory is a schema path",
          runner.is_schema_file("src/App.Infrastructure/Persistence/Migrations/20261003073534_ConnectorAuthorizationVerifierCleanup.cs"))
    check("real repository: ordinary production code is not a schema path",
          not runner.is_schema_file("src/App.Connectors.Monday/MondayConnectionAuthorizer.cs"))


def main():
    temp_root = Path(tempfile.mkdtemp(prefix="fully-mutation-selftest-")).resolve()
    os.environ["FULLY_MUTATION_ROOT"] = str(temp_root / "not-yet")
    try:
        runner = load_runner()
        call = Runner(runner, temp_root)
        classification_cases(runner)
        base = make_sandbox(temp_root, "validation")
        call.use(base)
        validation_cases(call, temp_root, base)
        taxonomy_cases(call, runner, temp_root)
        restore_cases(call, runner, temp_root)
        environment_cases(call, runner, temp_root)
        kill_cases(call, temp_root)
        real_repository_cases(runner)
    finally:
        remove_tree(temp_root)
    check(f"the temporary directory was removed ({temp_root.name})", not temp_root.exists())
    print(f"\n{sum(RESULTS)}/{len(RESULTS)} passed")
    return 0 if all(RESULTS) else 1


if __name__ == "__main__":
    sys.exit(main())
