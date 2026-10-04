---
name: mutation-evidence
description: User-invoked only, as /mutation-evidence <scope>. The FULLY procedure for mutation evidence on production logic that the current gate or sub-gate changed, when its Definition of Done or the product owner requires that evidence. Not for documentation-only changes, never a whole-repository mutation run, and never a commit, push, Azure or monday action, or the start of another gate.
disable-model-invocation: true
argument-hint: "<scope>"
---

# Mutation Evidence for FULLY

## Overview

Mutation evidence proves that the tests would fail if a guard the gate added or changed were missing. Here it is:
- **reproducible**: a plan file and the bundled runner;
- **bounded**: targeted tests and small foreground batches;
- **hash-verified**: every mutated byte is restored and proved by SHA-256;
- **current only while** the mutated files, their detecting tests and the mutant definitions are unchanged.

The runner, `mutation_runner.py` beside this file, enforces the mechanics. Every rule below holds in full when the runner
is unavailable. CLAUDE.md, the frozen documents and the gate's scope document stay the authority, and a conflict with
them is stopped and reported.

**Violating the letter of these rules is violating their spirit.**

## Authority

- The skill runs only on the owner's `/mutation-evidence <scope>`. The scope names the gate or sub-gate and the
  production logic. When the scope is empty or unclear, ask before planning. The runner is never started outside an
  invocation of this skill.
- The invocation authorizes only these steps: temporary mutation of the planned files under `src/`, each restored and
  verified by hash; building the detecting test projects; and running the detecting tests on the local stack.
- It authorizes nothing else. No staging, commit or push. No destructive git: `git checkout --`, `git restore`,
  `git reset`, `git stash` or `git clean`. No Azure, no migration in Azure, no CD and no monday call.
- It never starts the next gate or sub-gate. Gate 3G stays NOT STARTED until its own authorization.
- The approval guard cannot see writes the runner makes, including to committed migrations. The runner therefore writes
  only the planned files under `src/` and restores every one.

## When it applies

Mutation evidence is due only when all three hold:
1. the gate or sub-gate changed production logic under `src/`: code, migrations, database SQL or the runtime-role
   bootstrap;
2. its Definition of Done or the owner requires mutation evidence;
3. each mutant tests a named invariant or failure mode of that change.

When a change is only to documentation, tests or configuration, report "mutation evidence: not applicable" and stop.
- Never mutate the whole repository.
- Never add mutants to raise a count.
- Never mutate code the gate did not change unless the owner names it.

## Conventions

- `PY` is the Python 3 interpreter of this machine, resolved once per command with
  `PY="${FULLY_PYTHON:-$(command -v python || command -v python3)}"`; set `FULLY_PYTHON` to pin one. `SP` is this
  session's scratchpad.
- `RUNNER` is `.claude/skills/mutation-evidence/mutation_runner.py`, and `PLAN` is `$SP/<id>-mutants.json`. `<id>` is
  the campaign, normally the sub-gate (`3g`, or `pkce` for a maintenance item).
- Commands run in the Bash tool from the repository root: `"$PY" "$RUNNER" <command> --plan "$PLAN"`. Run them in the
  foreground, never `run_in_background`, with a Bash timeout of 600000.
- `"$PY" "$RUNNER" --help` prints the plan schema.
- After any change to the runner, `"$PY" .claude/skills/mutation-evidence/test_mutation_runner.py` must pass before it
  is used again.
- Start from the session status: PostgreSQL healthy, the emulator available, frozen documents 6/6. An unhealthy stack
  makes the integration controls fail, and the runner then refuses to start.

## 1. Scope and plan

1. Name the changed production files from `git --no-optional-locks status --porcelain=v2 --untracked-files=all` and
   `git --no-optional-locks diff --stat HEAD`, and the guards from the gate's Definition of Done, its owner decisions
   and its implementation record.
2. Write one mutant per guard or failure mode:
   - `edits`: an exact `old` anchor that occurs once in the file, and a `new` text that removes or weakens exactly that
     guard;
   - `behaviour`: the change, in words;
   - `detect`: the smallest set of existing tests that should fail, each a test class (or a class and a method) with its
     kind: `behavioural`, `fresh-db` or `static`.
3. Mark all eight security classes (section 7), each with the ids of its mutants or `n/a: <reason>`.
4. `"$PY" "$RUNNER" check --plan "$PLAN"` must report 0 errors. Among other things, it checks that:
   - each anchor occurs exactly once;
   - each edit is under `src/`;
   - each detecting class and method exists;
   - each `fresh-db` class creates and migrates a fresh database in every selected test;
   - each schema mutant has fresh-database detection (section 6);
   - all eight security classes are present.
5. Show the mutant table (ID, file and line, behaviour, detecting tests) in the conversation before the first run.

## 2. Baseline

`"$PY" "$RUNNER" baseline --plan "$PLAN"` refuses while an in-flight journal or a lock exists. It records:
- HEAD and the git status;
- the SHA-256 of every file the plan mutates;
- a byte backup of each such file, in `$SP/<id>-mutation/originals/`.

Read the printed git state and diff size of each file. It must be the reviewed change under test, with nothing left over
from an earlier mutation. When a file changes later, take the baseline again: evidence for the old bytes becomes stale
by hash.

## 3. Run in deterministic foreground batches

`"$PY" "$RUNNER" run [ids] [--max N]` runs one batch. A batch is the named mutants, or the next pending ones, in plan
order, never in argument order. The default batch is 3 and the maximum 6. No new mutant starts once the 480-second
budget could be exceeded.
- First, each detecting selection runs once on the unmutated tree, as a control. It must pass, with at least one
  passing test for every detecting entry, or nothing is mutated. The control is cached against a fingerprint of `src/`
  and `tests/`.
- Then, for each mutant: write the journal, apply the mutation, build only its detecting test projects (warnings are not
  errors), and run only its detecting tests with an xunit XML report. Then restore the file, verify it by SHA-256 and
  record the verdict.
- After the batch, the runner rebuilds the restored code and checks every baseline file again.
- Run `run` again until nothing is pending. A broken run stops the batch and stays pending until it is run again.

## 4. Restoration and hashes

- After every mutant, the runner writes back the original bytes it holds in memory and verifies them against the
  baseline SHA-256. On a mismatch it writes the byte backup and verifies again. Restoration never relies on
  `git checkout`, `git restore`, `git stash` or `git reset`.
- When a restoration cannot be verified, the runner exits 2 and keeps the journal and the lock. Stop everything, run
  `"$PY" "$RUNNER" recover --plan "$PLAN"`, verify and report. No mutant starts while a restoration is uncertain.
- Claude Code has killed long runs for memory. A runner killed during a mutant leaves its journal. `recover` restores
  every listed file from its backup, verifies it, records the attempt as INTERRUPTED and frees the lock. That mutant
  then runs again. After any interrupted run, check the journal before anything else.
- At the end, every mutated file matches its baseline SHA-256 (report section 5), and git status did not change during
  any batch.

## 5. Test selection

- Each mutant runs only its detecting tests: never the full suite, a namespace, a whole project or the solution. The
  runner accepts only named test classes and methods.
- Choose the smallest set able to fail for the intended reason: a unit test, then a security or static test, then the
  integration class that exercises the guard.
- The full suite runs once, after the mutation evidence and on the final tree, in the gate's normal final verification
  (gate-closure Mode A, step 3).

## 6. Migration and schema mutants

A mutant is a schema mutant when it edits `src/App.Infrastructure/Persistence/Migrations/`, `RuntimeRoleBootstrap.cs`
or `DatabaseMigrator.cs`.
- The shared integration database already holds the migrations, and an applied migration is never applied again there.
  A test that runs only against it proves nothing about a schema mutant.
- **Required: a `fresh-db` detecting test.** Such a test:
  - creates a database by an exact name;
  - bootstraps the runtime role and migrates from zero in the rollout order, or from the required baseline migration;
  - verifies the schema, constraint, privilege or policy on that database.

  `ConnectorAuthorizationFreshDatabaseTests`, `ReconciliationTargetDatabaseSecurityTests` and
  `InitialSyncDatabaseSecurityTests` are the pattern. When no such test covers the guard, the evidence is incomplete:
  report the gap. Writing the test is gate work, under the gate's own authorization.
- A schema mutant is CAUGHT only when a fresh-database test fails. A static pin or a shared-database test failing alone
  leaves it SURVIVED.
- `evidence: static` is only for the vacuity of a static guard, such as 3F's V05. Every detecting test is then static,
  and `schema_covered_by` names a schema mutant of the same file that a fresh database catches.
- A later migration may re-assert what the mutant changes, as the scope-version migration did for F39; the final schema
  then cannot show the mutant. Either a fresh-database test migrates exactly to the mutated migration, or the mutant
  goes to the owner as an INVALID judgment. For F39 itself, see the historical exception below.
- Live-database weakening, such as Gate 2G's row-level-security vacuity, is not a mutant. It runs only on the local
  database and is restored by an exit trap. The restoration is proved by comparing, before and after, the flags in
  `pg_class`, the full expressions in `pg_policies`, and the privileges. It is reported apart from the mutant totals.

### Historical exception: Gate 3F mutant F39 (owner decision, 2026-10-03)

The authority for this exception is `docs/gates/gate-3-scope.md`, "Owner decisions on record before 3G (recorded
2026-10-04)", its item on the historical Gate 3F mutation evidence, F39. This skill is not its source; the bullets
below apply that record.

- Under this taxonomy, historical Gate 3F mutant F39 (the replay floor made updatable by the runtime role in the
  `InitialSync` migration's grant) would not qualify as CAUGHT. In its final run, after the scope-version migration
  re-asserted that grant, only the static pin `InitialSyncSecurityTests` caught it, and no fresh-database test did.
- Gate 3F remains PASS / CLOSED.
- This is a historical evidence-classification exception, not a production defect.
- No retroactive mutation campaign is required.
- All future migration and schema mutation evidence must follow the fresh-database rule of this section.

## 7. Security mutants

Include each class that applies to the changed logic, and mark the others `n/a: <reason>`.

| Class (plan key) | Fully precedents |
|---|---|
| Tenant predicate removed (`tenant-predicate`) | M15, F26 |
| Row-level security or tenant binding removed (`rls-tenant-binding`) | E33, E36, F42, M14 |
| Authorization guard removed (`authorization-guard`) | E20, F32 |
| Identity or transaction binding removed (`identity-transaction-binding`) | P08, P09, E22, F30 |
| Retry or failure treated as success (`failure-as-success`) | P11 to P14, M12, M13 |
| Replay or idempotency protection removed (`replay-idempotency`) | M1, P06 |
| Secret or log protection removed (`secret-log-protection`) | E31 |
| Authority or revocation check removed (`authority-revocation`) | M8, F15 |

## 8. Verdicts

| Verdict | Meaning | How the runner decides |
|---|---|---|
| CAUGHT | The intended detecting test fails for the intended reason | A named detecting test that passed in the control fails with an xunit assertion. When the plan declares `expect`, only a failure matching it counts. For a schema mutant, the failing test must be a fresh-database test. |
| SURVIVED | The tests still pass, or fail for an unrelated or unconfirmed reason | Everything else. That includes a failure outside the named tests, a non-assertion exception, and a broken run: zero tests (exit 8), an abort (exit 3), a crash, a timeout or a fixture failure. A broken run is unproven and runs again once the environment is fixed. |
| INVALID | The mutant does not compile, is semantically impossible, or does not represent the intended behavioural change | A build failure, or a written `judgments` entry |

- INVALID never counts as CAUGHT.
- A judgment can make a mutant INVALID, never CAUGHT, and never applies to a broken run.
- Every INVALID needs a written justification before the evidence is READY. A mutant that does not compile is first
  restated so that it compiles with the same behavioural change, as D11 and G09 were. Its earlier attempt stays in the
  report.

## 9. Failure policy: SURVIVED

1. A meaningful SURVIVED mutant makes the evidence NOT READY. Stop claiming that it is complete.
2. Find the cause:
   - a production defect: the guard does not hold;
   - a test gap: the guard holds, but no test observes it.
3. Repair only as the gate's authorization and the owner allow:
   - a production defect is a gate finding for the owner;
   - a test gap is closed by a test change within scope, as with C37, E03, F12, and M4 and M5.
4. Rerun the mutant, and every mutant whose files or detecting tests the repair changed (`status` marks them stale).
   Then rerun the final verification.
5. Never weaken, reword or redesign a mutant to make a test catch it. Never swap its detecting test for a weaker one or
   delete that test. A restatement may only fix an anchor that moved or a compile error, and it keeps the behavioural
   change. The report lists every earlier attempt and every redefinition.
6. An equivalent mutant needs evidence that the behaviour cannot occur. H13 had it: the runtime role cannot delete
   Snapshot rows, by grant and by the restrictive policy. "No test can observe it" is a test gap, not equivalence.

## 10. Evidence and gate-closure

- `"$PY" "$RUNNER" status --plan "$PLAN"` writes `$SP/<id>-mutation/report.md` in the output contract below.
- Only when READY, `status` also writes the manifest `$SP/<id>-mutation.sha256`. It holds the baseline SHA-256 of:
  - every mutated production file;
  - every detecting test file, and the test files those tests use;
  - the plan, which holds the mutant definitions.

  While not READY, no manifest exists, and any earlier one is removed.
- READY requires all of the following:
  - every mutant current;
  - 0 SURVIVED;
  - every INVALID justified;
  - every schema mutant proven on a fresh database;
  - every restoration verified;
  - no journal and no lock.
- gate-closure reuses the evidence only while `sha256sum -c "$SP/<id>-mutation.sha256"` passes from the repository
  root. That means the production files match the recorded hashes, the relevant tests have not changed and the mutant
  definitions have not changed. Otherwise the evidence is stale: run `status`, rerun the mutants it lists as stale, then
  `status` again.
- Any byte change counts as material. Rerunning the affected mutants is targeted and cheap; do not argue that a change
  is immaterial.
- A later session reuses evidence only through the absolute paths that the report gives.

## Rationalizations

| Thought | Reality |
|---|---|
| "A test failed, so it is caught" | Only a named detecting test, failing for the intended reason, counts. Zero tests, aborts, crashes and fixture failures are broken runs. |
| "INVALID is as good as caught" | INVALID never counts as CAUGHT. Restate it or justify it. |
| "The static pin caught the migration mutant" | Not for a schema mutant. Without a fresh database, it SURVIVED. |
| "It passed on the shared database" | That database is already migrated; it proves nothing about a schema mutant. |
| "Run the full suite per mutant to be safe" | Forbidden. Use the targeted tests; the full suite runs once, at the final verification. |
| "`git checkout` restores the file" | Restore the original bytes and verify the SHA-256. |
| "No test can see it, so it is equivalent" | That is a test gap: SURVIVED. Equivalence needs proof that the behaviour cannot occur. |
| "Adjust the mutant so that it is caught" | Forbidden. Keep the behavioural change, and fix the test or report the gap. |
| "Only a comment changed in that test" | Any byte change makes the evidence stale. Rerun the affected mutants. |
| "All mutations passed" | Report X / Y CAUGHT, Z SURVIVED and N INVALID, plus anything not run or stale. |
| "Run it in the background to save time" | Run foreground batches only. Background shells get reaped under memory pressure. |

## Output contract

Return the runner's `status` report, which has exactly these sections, and nothing that the report does not support:

```
Mutation evidence <id>: READY | NOT READY
1. Mutation scope: the changed production logic and the invariants under test
2. Production hashes: file | SHA-256 at baseline | git state at baseline | matches now
3. Mutants: ID | file:line | behaviour changed | detecting test (kind) | result, with its failing test and reason
4. Totals:
   X / Y CAUGHT
   Z SURVIVED
   N INVALID
   (plus K not run or stale, which count as nothing)
5. Restoration and hash verification: every file back at its baseline SHA-256; no journal, no lock; git status
6. Fresh-database evidence: each schema mutant and the fresh-database test that caught it, or the gap
7. Remaining gaps: SURVIVED, INVALID with justification, static-only catches, not run or stale, earlier attempts and
   redefinitions, security classes marked n/a
8. Status: READY | NOT READY with the reasons; the manifest path, or none
```

Never report "all mutations passed" without the counts. Label partial evidence as partial. Name every mutant that was
not run.
