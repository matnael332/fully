---
name: gate-closure
description: User-invoked only. The FULLY gate and sub-gate closure procedure. Mode A (review) verifies a finished sub-gate and prepares the evidence for owner review, with no staging, commit or push. Mode B (close) runs only after the product owner's explicit closure order in the conversation - exact-path staging, one commit, a fast-forward push when authorized, CI followed on the pushed SHA and the remote closure proved. It never starts the next gate and never touches Azure or real monday.
disable-model-invocation: true
argument-hint: "<sub-gate> review | <sub-gate> close"
---

# Gate Closure for FULLY

## Overview

Closing a gate is two acts, and this skill keeps them apart. **Mode A (review)** proves that a finished sub-gate is
ready and hands the evidence to the product owner; it never stages, commits or pushes. **Mode B (close)** turns an
explicit owner closure order into one exact commit, a fast-forward push and a verified CI run, then stops. The pattern
is the proven 3A to 3F and PKCE maintenance closures (`aaf2175`, `f92c94c`). CLAUDE.md, the frozen documents and the
gate's scope document stay the authority; a conflict with them is stopped and reported, never resolved here.

## Authority first

- Invoking the skill is never authorization. The arguments name the sub-gate and the mode; none, `review`, or an
  unclear request means Mode A. A sub-gate still NEXT / NOT STARTED has nothing to close: report that and stop.
- Mode B needs the owner's closure order **in this conversation**, naming the sub-gate; quote it in the report.
  Its words set the authority: "commit and push" grants both, "commit" grants only the commit. An order that names
  the commit is the "go"; otherwise show the exact commit command and wait for one.
- A closure order implies nothing else. Azure (`az deployment`, `eng/infra.ps1 -Approve`, Container Apps jobs, Key
  Vault, RBAC), `CD Dev` with `deploy: true`, applying migrations in Azure, a monday app, OAuth credential or webhook
  registration, and any real monday call each need their own approval.
- The next sub-gate is never started, planned in code or scaffolded; it stays NEXT / NOT STARTED.

## Guards this skill relies on

The project-local hooks (wired in `.claude/settings.local.json`) enforce part of this procedure. The skill does not
duplicate them, and its rules hold in full when they are absent.
- `fully_approval_guard.py` denies `git add .`, `git add -A`, force pushes, `git clean`, `git reset --hard`,
  `git restore .` and broad deletes, and asks before `git push`, `git add -u` and `gh workflow run cd-dev`. A deny is
  a stop, never a cue to rephrase. The push prompt is the owner's second confirmation, not the authorization.
- It denies edits to the six frozen documents and asks before `.env*`, `.github/workflows/**`, `infra/bicep/**` and
  committed migrations. `fully_hygiene_check.py` checks each file Claude edits; fix what it blocks, never bypass it.
- `fully_session_status.py` prints the baseline snapshot and holds the frozen hash set.
- The guards do not cover these, so this skill forbids them: `git commit --amend`, `--no-verify`, `git rebase`,
  `git pull`, `git stash`, history rewrites and creating tags.

Conventions: commands run in the Bash tool (Git Bash). `PY` is the Python 3 interpreter of this machine, resolved once
per command with `PY="${FULLY_PYTHON:-$(command -v python || command -v python3)}"`; set `FULLY_PYTHON` to pin one, such
as the interpreter the locally wired hooks use. `SP` is this session's scratchpad.
`FROZEN` is the six hash-frozen documents: `docs/canonical/PRD-v1.md
docs/canonical/SYSTEM-ARCHITECTURE-v1.md docs/canonical/ARCHITECTURE-INVARIANTS-v1.md
docs/canonical/DEVELOPMENT-GATES-v1.md docs/premium/FULLY-PREMIUM-ARCHITECTURE-v1.md
docs/premium/PREMIUM-POLICIES-v1.md`; the frozen hash check covers exactly these six.
`docs/premium/PREMIUM-DEVELOPMENT-GATES-v1.md` is not frozen: it is a controlled status and plan document. Like any
edit under `docs/premium/`, a change to it needs the owner's explicit approval. Its Core Gate status row changes only
when a whole Core Gate changes state (step 4). `docs/canonical/PROJECT-STATUS.md` is not frozen.
Read-only git commands use `git --no-optional-locks`, so a check never rewrites the index; the index changes only in
step 5.

Test output, local or CI, is read the same way. First strip color codes, which CI logs store as ESC bytes or as
literal `^[` text: `sed -E 's/(\x1b|\^\[)\[[0-9;]*m//g'`. Then:
- totals: `grep -oE '(total|failed|succeeded|skipped): [0-9]+'`;
- test names: `grep -oE '(failed|skipped) WorkContinuity\.[A-Za-z0-9_.]+'`. Anchor on the namespace, because CI
  logs also carry PostgreSQL lines such as "authentication failed for user";
- build: `grep -oE '[0-9]+ (Warning|Error)\(s\)'`.

## 1. Baseline

1. `"$PY" -I -S .claude/hooks/fully_session_status.py`. It must show branch `main`, HEAD equal to the local
   `origin/main`, the expected Gate line, PostgreSQL healthy, the emulator available and `Frozen docs: 6/6 unchanged`.
   Report anything else before going on.
2. `git --no-optional-locks status --porcelain=v2 --branch --untracked-files=all`. Also check that
   `git --no-optional-locks log --oneline -3` shows the previous closure commit as the parent, and that
   `git --no-optional-locks diff --cached --quiet` passes, so nothing is staged.
3. Write the approved scope as `$SP/<id>-stage-list.txt`, one plain path per line. Take it from the owner's
   authorization, the Definition of Done and the implementation record, and note the expected counts of modified,
   added and deleted files.
4. Recognize local Claude project tooling: any changed or untracked path under `.claude/agents/`, `.claude/hooks/` or
   `.claude/skills/`, or exactly `.claude/settings.json` or `.claude/settings.local.json`. List these paths separately
   as local tooling; they are not unexpected gate-scope findings. Select them with
   `git --no-optional-locks status --porcelain=v1 --untracked-files=all | cut -c4- | grep -E '^\.claude/(agents/|hooks/|skills/|settings(\.local)?\.json$)'`.
   - Recognizing them authorizes nothing. They are never staged or committed: a gate commit includes one only when the
     owner's closure order explicitly approves that path for that commit, and then the path is in the stage list
     (item 3).
   - The exact staged-inventory rules of step 5 are unchanged.
5. Every changed or untracked path is in the stage list, recognized local tooling, or a finding.

Mode A stays local, and the local `origin/main` ref is enough. Mode B adds `git ls-remote origin refs/heads/main`.

## 2. Verification strategy

Run each suite as few times as the evidence allows.
- While developing: targeted tests only (`--filter-class`, `--filter-method`) in the touched project.
- Before review: the affected integration namespaces (`--filter-namespace WorkContinuity.Integration.Tests.<Area>`).
- Mutation evidence only for the production logic this sub-gate changed, never for docs or tests:
  - exact-anchor mutants, each anchor asserted to occur exactly once before anything is written;
  - a hash-verified restore after each mutant, and a pre-mutation manifest `$SP/<id>-mutation.sha256`;
  - killed/total recorded, with every equivalent mutant justified.
  Claude Code has killed runs for memory before, so after an interrupted run check the manifest before anything else.
- The full suite runs once, in step 3, on the final code tree. It reruns only if a non-Markdown path changes after
  that run (the fingerprint shows it), if a failure was fixed, or if the owner orders it.
- Long runs go in the foreground, in batches under 10 minutes; background shells get reaped under memory pressure.

## 3. Final local verification (once, on the final code tree)

1. The local stack is up (step 1). The database tests and the Service Bus emulator tests need it. `.env` expects the
   emulator, so its 9 tests run locally and must pass, not skip.
2. `dotnet build WorkContinuityEngine.sln --no-incremental -nologo -v q` gives 0 Warning(s), 0 Error(s).
3. Tests run with `--no-build`. Read each summary's totals, and any failed names, as the conventions describe.
   - Unit, Security and Architecture: `dotnet test --project tests/App.<P>.Tests/App.<P>.Tests.csproj --no-build`.
   - Integration: count with `--list-tests`, then run namespace batches under 10 minutes each, each namespace as
     `--filter-namespace WorkContinuity.Integration.Tests.<X>`. Today's batches are {Acceptance, Connectors,
     Database, Health, Ingress}, {InitialSync, Observability, Processing} and {Reconciliation, Retention, Transport}.
   - The batch totals must add up to the listing; a new folder missing from the batches would otherwise go untested.
   - Expected: failed 0 and skipped 0. The total is unit + security + architecture + integration.
4. `"$PY" eng/verify-architecture.py` and `"$PY" eng/verify-local-env.py` each exit 0.
5. Frozen: the status hook shows 6/6, and `git --no-optional-locks diff --quiet HEAD -- $FROZEN` exits 0.
6. `git --no-optional-locks diff --check` is clean. It cannot see untracked files, so also check that
   `grep -lE '[[:blank:]]$'` finds nothing in the approved untracked files.
7. Line endings and BOM, read as bytes. A BOM is allowed only on EF migration Designer files and the model snapshot,
   or where the HEAD version has one:
   `while read -r f; do [ -f "$f" ] || continue; c=$(tr -cd '\r' < "$f" | wc -c); l=$(wc -l < "$f"); [ "$c" -eq 0 ] || [ "$c" -eq "$l" ] || echo "MIXED $f"; [ "$(head -c 3 "$f" | od -An -tx1 | tr -d ' \n')" != efbbbf ] || echo "BOM $f"; done < "$SP/<id>-stage-list.txt"`
8. A migration is new, never an edit of a committed one, and contains none of the vocabulary `DatabaseCredentialTests`
   forbids.
9. No artifacts. An untracked path that is neither in the list nor recognized local tooling (step 1.4) is removed by
   exact path if this session created it, and reported otherwise: `TestResults/`, `*.trx`, `*.log`, `*.orig`, `*.rej`,
   `*.bak`, mutation backups, scratch scripts, `__pycache__/`.
10. Fingerprint: run `sha256sum` over every approved path that exists into `$SP/<id>-final-run.sha256`, and keep it
    beside the totals. No fingerprint, no reuse.

Mode A ends here with its report. It may write the evidence into the sub-gate's implementation record (documentation
inside the approved scope). It never marks the sub-gate PASS or CLOSED.

## 4. Closure records (Mode B)

1. Record the closure in every status document that carries the sub-gate's state; find them with grep:
   - the gate scope document, `docs/gates/gate-<n>-scope.md`. Its status row reads
     `**PASS** — approved by the product owner on <date>`, and a new `### <id> owner review and closure (<date>)`
     section gives the owner's decisions, the final evidence, Azure rollout as its own approval, and the next sub-gate
     NEXT / NOT STARTED;
   - `docs/canonical/PROJECT-STATUS.md` and `CLAUDE.md`, plus `INIT.md` and `README.md` where they carry status.
   A sub-gate closure never edits `docs/premium/`. Its state lives in the documents above, and the "Core Gate <n>"
   row of `docs/premium/PREMIUM-DEVELOPMENT-GATES-v1.md` stays IN PROGRESS (Gate 3: 3A to 3F).
   That row changes only when the overall Core Gate state changes, above all when the last sub-gate passes and the
   gate becomes PASS / CLOSED (Gate 3: when 3G passes). Only then is that one row edited, and only with the owner's
   explicit approval: the Mode A report lists it among the planned closure records, and the closure order must name
   it. For Gate 3 the authority for this rule is decision F of `docs/gates/gate-3-scope.md`, "Owner decisions on
   record before 3G (recorded 2026-10-04)"; this skill applies that record and is not its source.
   Closing a gate's last sub-gate also records the gate as PASS and the next gate as NEXT / NOT STARTED. The status
   hook still reads the old gate's table afterwards; tell the owner, and change it only on their decision.
2. The commit's own SHA cannot be in the repository. Report it, and record it in memory after the push.
3. Then decide whether to reuse the step 3 run:
   - If `sha256sum -c "$SP/<id>-final-run.sha256"` matches every non-Markdown path, the step 3 run stands.
   - Rerun only the three projects that need no database (about 30 s). Security holds every test that reads Markdown
     today: `DatabaseCredentialTests` scans each `.md` outside `bin`, `obj`, `.git` and `tests`, and
     `RetentionSecurityTests` reads every file under `infra/`, `.github/` and `eng/`. If test code changed,
     re-derive this with a grep.
   - Also rerun the verifiers, the frozen check, `diff --check` and the line-ending and BOM check.
   - A non-Markdown mismatch, a missing fingerprint or an owner order means a full rerun of step 3.
   - Mutation evidence stands only if `sha256sum -c "$SP/<id>-mutation.sha256"` passes.

## 5. Staging (Mode B)

1. Stage exactly the approved paths with `git add --pathspec-from-file="$SP/<id>-stage-list.txt"` or
   `git add -- <path> ...`. Never use `git add .`, `git add -A`, `git add -u`, `git commit -a`, a directory or a
   wildcard.
2. `git diff --cached --name-status` equals the list path for path, with the expected M, A and D counts.
   `git diff --cached --stat | tail -1` gives the size.
3. `git diff --cached --name-only -- $FROZEN` prints nothing: the six frozen documents are never staged.
   `git diff --cached --name-only -- docs/premium/` prints nothing either. The one exception is a whole-gate state
   change whose closure order names the "Core Gate <n>" row; then `git diff --cached -U0 -- docs/premium/` shows that
   row alone.
4. Nothing is left behind. `git --no-optional-locks diff --name-only` and
   `git --no-optional-locks ls-files --others --exclude-standard` list only recognized local tooling (step 1.4), which
   stays unstaged.
5. `git diff --cached --check` is clean. Each staged text blob has no CR and a BOM only where step 3 allows one:
   `git --no-optional-locks diff --cached --name-only --diff-filter=AM | while read -r f; do n=$(git --no-optional-locks show ":$f" | tr -cd '\r' | wc -c); b=$(git --no-optional-locks show ":$f" | head -c 3 | od -An -tx1 | tr -d ' \n'); [ "$n" -eq 0 ] || echo "CR $n $f"; [ "$b" != efbbbf ] || echo "BOM $f"; done`
6. A mismatch stops the closure. Unstage only a path you staged by mistake, by exact path
   (`git restore --staged -- <path>`), and report it.

## 6. Commit (Mode B, with commit authority)

Write the message with `git commit` and one `-m` per paragraph, at most 10 lines and no heredoc. Avoid backticks and
`$` inside the message.
- Subject: `feat: complete Gate <id> <summary>` for a sub-gate, `fix: <summary>` for a maintenance item.
- Body: what was delivered, the owner decisions in force, and the evidence: the local total with its unit, security,
  architecture and integration counts, and the mutation results. End the body with "No Azure and no real monday
  activity."
- Last `-m`: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`, with the current model's name. Never add a
  `Claude-Session` trailer. Never use `--amend`, `--no-verify` or a signing bypass.

Then `git show --stat --format='%H %P' HEAD` must list exactly the staged files, with the baseline HEAD as the parent.

## 7. Push (Mode B, with push authority)

1. `git ls-remote origin refs/heads/main` must equal the commit's parent, so the push is a fast-forward. If the
   remote moved, stop: no fetch-and-rebase, merge or force without the owner.
2. Record the latest CD run: `gh run list --workflow cd-dev.yml --limit 1 --json databaseId,createdAt,headSha`.
3. Push with a plain `git push origin main`. Never `--force`, `--force-with-lease`, a `+` refspec, `--mirror`,
   `--delete` or `--tags`.
4. `git --no-optional-locks rev-parse HEAD origin/main` and `git ls-remote origin refs/heads/main` agree.

## 8. CI on the pushed SHA

Strip `\r` from `gh` output before comparing values.
1. Find the run, retrying briefly until it appears:
   `gh run list --branch main --limit 5 --json databaseId,workflowName,headSha,status,conclusion,event --jq '.[] | select(.headSha=="<full sha>")'`
2. `timeout 580 gh run watch <run> --exit-status --interval 30`. Repeat it if the run outlasts the call.
3. `gh run view <run> --json conclusion,jobs` shows all three jobs succeeded: `Build, verify and test`,
   `Container images (Release build, no push)` and `Secret scan (gitleaks, pinned by version and checksum)`. That
   includes the steps `Architecture dependency rules` and `Local environment structure`.
4. Save the test job's log with `gh run view <run> --job <build-test job> --log > "$SP/<id>-ci.log"`. Read it as
   the conventions describe:
   - the build shows 0 warnings and 0 errors;
   - the CI total equals the local total, and failed is 0;
   - the skipped names are exactly the Service Bus emulator tests, because CI hosts no emulator by design. Today
     that is 9: `ServiceBusAcceptanceTests` 2, `DependencyProbeTests` 3 and `ServiceBusTransportTests` 4.
5. Save the secret-scan job's log the same way and strip it. `grep -oE '[0-9]+ commits scanned|no leaks found|leaks found: [0-9]+'`
   must give the commit count and "no leaks found" twice, once for the history scan and once for the working tree.
6. No unexpected CD: the latest `cd-dev.yml` run is still the one recorded in step 7, and no other workflow ran for
   the SHA.
7. A red run stops the closure. Report the job, the step and a log excerpt; make no fix-forward commit without the
   owner.

## 9. Remote closure

1. HEAD, `origin/main` and the remote `main` are all the pushed SHA.
2. `git --no-optional-locks status --porcelain=v2 --untracked-files=all` shows no tracked change and no untracked path,
   apart from recognized local tooling (step 1.4).
3. Frozen: 6/6, and `git diff --stat <parent> <sha> -- $FROZEN` is empty. `docs/premium/` is unchanged as well
   (`git diff --stat <parent> <sha> -- docs/premium/`), except for an approved "Core Gate <n>" row at a whole-gate
   closure.
4. The status hook's Gate line shows the sub-gate as PASS / CLOSED and the next one as NEXT / NOT STARTED.
5. Record the closure in memory: SHA, files, CI run, totals and gitleaks.
6. Stop. Do not begin the next sub-gate, dispatch CD, apply a migration in Azure or call monday.

## Never

- Weaken, skip, filter out or delete a test, or rerun it until it passes. Report a failure with its output, and an
  intermittent one as intermittent.
- Claim a check that did not run. Label partial evidence as partial.
- Use destructive git: `git clean`, `git reset --hard`, `git restore .`, `git checkout -- .`, stashing anyone's work,
  `git branch -D`, force pushes or history rewrites. Clean up only what this session created, by exact path.
- Edit or stage a frozen document, or anything under `docs/premium/`, without the owner's explicit approval of that
  exact change. A sub-gate closure never touches `docs/premium/`.

## Output

Mode A, before owner review:

```
GATE <id> - PRE-OWNER-REVIEW: READY FOR OWNER REVIEW | NOT READY FOR OWNER REVIEW
Blockers: none | <each, with its evidence>
Baseline: main @ <sha> == origin/main (local); scope <n> paths (<m> M, <a> A, <d> D)
Build: 0 warnings, 0 errors (--no-incremental)
Tests: <T>/<T> - unit <u>, security <s>, architecture <a>, integration <i> (<b> batches = listing <i>); 0 skipped
Mutation: <k>/<n> per set, equivalents explained | not applicable
Checks: verifiers PASS/PASS; frozen 6/6; diff --check clean; EOL/BOM <n> files clean; no artifacts
Fingerprint: $SP/<id>-final-run.sha256 (<n> paths); mutation manifest <file>
Git: <modified/untracked summary>; nothing staged; local tooling (not staged): <paths>
Closure records planned: <status documents step 4 will change; the docs/premium Core Gate row only at a whole-gate closure>
Not run or deviations: none | <each>
```

Mode B, after the owner's closure order:

```
GATE <id> - CLOSED (owner order: "<quote>")
Commit: <full sha> "<subject>" - <n> files (<m> M, <a> A), +<x>/-<y>, parent <sha>
CI: run <id> success - build/verify/test, container images, secret scan; verifiers PASS
Tests: CI <T> total, <P> passed, 0 failed, <S> skipped (Service Bus emulator); local <T>/<T>
Full suite: ran once on the final code tree | rerun because <reason>
Gitleaks: <n> commits and the working tree, no leaks
Remote: HEAD == origin/main == remote main == <sha>; tree clean apart from local tooling (not committed): <paths>
Frozen: 6/6 (exactly the six frozen documents)
docs/premium (controlled, not frozen): unchanged | only the approved "Core Gate <n>" row (whole-gate closure)
Gate: <id> PASS / CLOSED; <next> NEXT / NOT STARTED (not started)
Boundaries: no CD (latest still <id>), no Azure, no real monday
```
