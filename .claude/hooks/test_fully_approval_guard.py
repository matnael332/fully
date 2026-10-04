"""Dry-run tests for the Fully approval guard. Nothing here executes a tested command or calls monday: every case is sent
to the guard as the JSON payload Claude Code gives a PreToolUse hook, and only the guard's decision is checked. The
monday MCP permission rules are checked by reading the project-local settings, never by calling a tool.

Run from the repository root: python .claude/hooks/test_fully_approval_guard.py [--configured]
"""
import importlib.util
import json
import os
import shutil
import subprocess
import sys

sys.dont_write_bytecode = True  # importing the guard for the config checks must leave no __pycache__ behind

HERE = os.path.dirname(os.path.abspath(__file__))
GUARD = os.path.join(HERE, "fully_approval_guard.py")
PROJECT = os.path.dirname(os.path.dirname(HERE))
SETTINGS = os.path.join(PROJECT, ".claude", "settings.local.json")
OUTSIDE = "C:\\Windows"

B, P = "Bash", "PowerShell"
M = "mcp__plugin_small-business_monday-com__"
E, W, N = "Edit", "Write", "NotebookEdit"
FILE_TOOLS = ("Edit", "Write", "MultiEdit", "NotebookEdit")


def under(*parts):
    return os.path.join(PROJECT, *parts)


FWD = PROJECT.replace("\\", "/")
GIT_BASH = "/" + PROJECT[0].lower() + PROJECT[2:].replace("\\", "/")
PRD = under("docs", "canonical", "PRD-v1.md")
MIGRATIONS = under("src", "App.Infrastructure", "Persistence", "Migrations")
COMMITTED_MIGRATION = os.path.join(MIGRATIONS, "20261003073534_ConnectorAuthorizationVerifierCleanup.cs")
SCRATCH = os.environ.get("TEMP", "C:\\Windows\\Temp")


def short_path(path):
    """The 8.3 short form of an existing path, or None when the volume has no short names."""
    try:
        import ctypes
        buffer = ctypes.create_unicode_buffer(1024)
        length = ctypes.windll.kernel32.GetShortPathNameW(path, buffer, 1024)
        value = buffer.value if length else None
        return value if value and value.lower() != path.lower() else None
    except Exception:  # noqa: BLE001 - only a test helper
        return None


SHORT_PRD = short_path(PRD)
SHORT_CI = short_path(under(".github", "workflows", "ci.yml"))
SHORT_BICEP = short_path(under("infra", "bicep", "main.bicep"))

# The monday MCP tools this Claude session exposes (inspected 2026-10-03); every one must be classified.
SESSION_MONDAY_TOOLS = """
agent_catalog all_api_read all_api_write all_monday_api all_widgets_schema board_insights change_item_column_values
connect_external_agent create_action create_automation create_board create_column create_dashboard create_doc
create_folder create_form create_form_submission create_group create_item create_items create_notification
create_update create_view create_view_table create_widget create_workflow create_workspace delete_action delete_view
execute_code explore_meetings finalize_asset_upload form_questions_editor get_action get_asset_upload_url get_assets
get_automation_runs get_automation_statistics get_board_activity get_board_info get_board_items_page
get_column_type_info get_form get_graphql_schema get_meetings_content get_monday_dev_sprints_boards
get_monday_knowledge get_run_once_trigger_entities get_sprint_summary get_sprints_metadata get_type_details
get_updates get_user_context get_workflow_run_once_status invoke_process_planner invoke_workflow_expert list_actions
list_automations list_users_and_teams list_workspaces manage_agent manage_agent_knowledge manage_agent_skills
manage_agent_triggers manage_automations move_object publish_workflow read_docs run_action run_workflow_once search
search_meetings_content show-assign show-battery show-chart show-table stop_workflow_run_once
submit_bug_or_feature_request update_action update_column update_doc update_folder update_form update_items
update_view update_view_table update_workspace validate_workflow vibe_ask vibe_collaborators vibe_create vibe_delete
vibe_get vibe_list vibe_publication vibe_rename vibe_update workspace_info
""".split()

CASES = [
    # The owner's required proofs (step 5).
    (B, "git status", "allow"),
    (B, "dotnet test --project tests/App.Unit.Tests/App.Unit.Tests.csproj --no-build", "allow"),
    (B, "git push origin main", "ask"),
    (B, "az resource create -g rg-wce-dev-neu -n x --resource-type Microsoft.Web/sites --properties {}", "ask"),
    (B, "curl -X POST https://api.monday.com/v2 -H 'Authorization: x' -d '{\"query\":\"{ me { id } }\"}'", "ask"),
    (B, "git add .", "deny"),
    (B, "git reset --hard HEAD~1", "deny"),
    (B, "docker volume prune -f", "deny"),
    # The owner's required proofs (step 5B).
    (B, "git restore .", "deny"),
    (B, "git branch -D test", "deny"),
    (B, "rm -rf " + GIT_BASH, "deny"),
    (B, "rm -rf bin obj", "allow"),
    (M + "get_board_info", None, "allow"),
    (M + "create_item", None, "ask"),
    (M + "update_items", None, "ask"),
    (M + "change_item_column_values", None, "ask"),
    (M + "delete_item", None, "deny"),
    (M + "create_automation", None, "deny"),
    (M + "manage_automations", None, "deny"),
    (M + "execute_code", None, "deny"),
    (M + "all_api_write", None, "deny"),
    (M + "all_monday_api", None, "deny"),
    # The full command is inspected: chains, pipes, background, subshells, substitutions, nested shells, prefixes.
    (B, "cd " + GIT_BASH + " && git push", "ask"),
    (B, "git status; git push", "ask"),
    (B, "git fetch || git push", "ask"),
    (B, "git log --oneline | head -5 && git push origin main", "ask"),
    (B, "echo start & git push", "ask"),
    (B, "(git push)", "ask"),
    (B, "echo $(git push)", "ask"),
    (B, "echo \"result: `git push`\"", "ask"),
    (B, "bash -c \"git push --force\"", "deny"),
    (B, "sh -lc 'git reset --hard'", "deny"),
    (B, "eval \"git clean -fdx\"", "deny"),
    (B, "timeout 60 git push", "ask"),
    (B, "env GIT_TRACE=1 git push", "ask"),
    (B, "GIT_SSH_COMMAND=x git push", "ask"),
    (B, "xargs git add -A < /dev/null", "deny"),
    (B, "if true; then git push; fi", "ask"),
    (B, "for f in a b; do git add \"$f\"; done", "allow"),
    (B, "bash <<'EOF'\ngit push --force\nEOF", "deny"),
    (B, "cmd /c \"git push\"", "ask"),
    (P, "git status; git push", "ask"),
    (P, "git fetch && git push origin main", "ask"),
    (P, "if ($?) { git push }", "ask"),
    (P, "pwsh -NoProfile -Command \"git reset --hard\"", "deny"),
    (P, "& git push --force", "deny"),
    (P, "Invoke-Expression \"git clean -fd\"", "deny"),
    (P, "Start-Process git -ArgumentList 'push','--force'", "deny"),
    (P, "git status | Out-String\ngit push", "ask"),
    # Every force or mirror form is denied.
    (B, "git push --force origin main", "deny"),
    (B, "git push -f", "deny"),
    (B, "git push --force-with-lease", "deny"),
    (B, "git push origin +main", "deny"),
    (B, "git -C /c/repo push -uf origin main", "deny"),
    (B, "git push --mirror", "deny"),
    # The rest of the hard-deny list.
    (B, "git add -A", "deny"),
    (B, "git add --all", "deny"),
    (B, "git add -A src", "deny"),
    (B, "git clean -n", "deny"),
    (B, "docker system prune -af", "deny"),
    (B, "docker compose down -v", "deny"),
    (B, "docker compose -f infra/local/docker-compose.yml --env-file .env down --volumes", "deny"),
    (B, "docker-compose down -v", "deny"),
    (P, "docker volume prune --force", "deny"),
    # Destructive local git commands (step 5B).
    (B, "git restore --worktree .", "deny"),
    (B, "git restore -W .", "deny"),
    (B, "git restore --staged --worktree .", "deny"),
    (B, "git restore -- .", "deny"),
    (B, "git checkout -- .", "deny"),
    (B, "git checkout .", "deny"),
    (B, "git checkout HEAD -- .", "deny"),
    (B, "git branch --delete --force test", "deny"),
    (B, "git branch -df test", "deny"),
    (B, "git branch -d test", "ask"),
    (B, "git branch --delete test", "ask"),
    (B, "git restore --staged .", "allow"),
    (B, "git restore src/App.Core/Foo.cs", "allow"),
    (B, "git checkout main", "allow"),
    (B, "git checkout -b feature/x", "allow"),
    (B, "git checkout -- src/App.Core/Foo.cs", "allow"),
    (B, "git branch -a", "allow"),
    (B, "git branch -m old new", "allow"),
    (B, "git rm -r --cached .", "ask"),
    # Broad recursive deletes are denied; artifact cleanup stays allowed; anything else asks (step 5B).
    (B, "rm -rf /", "deny"),
    (B, "rm -rf .", "deny"),
    (B, "rm -rf *", "deny"),
    (B, "rm -rf ./*", "deny"),
    (B, "rm -r .", "deny"),
    (B, "rm -rf ~", "deny"),
    (B, "rm -rf .git", "deny"),
    (B, "rm -rf ..", "deny"),
    (B, "rm -rf /c/", "deny"),
    (B, "rm -rf -- .", "deny"),
    (B, "cd " + GIT_BASH + " && rm -rf *", "deny"),
    (B, "rm -rf " + FWD + "/*", "deny"),
    (B, "rm -rf src/App.Web/bin src/App.Web/obj tests/App.Unit.Tests/bin", "allow"),
    (B, "rm -rf TestResults", "allow"),
    (B, "rm -rf **/bin **/obj", "allow"),
    (B, "rm -rf .claude/hooks/__pycache__", "allow"),
    (B, "rm -rf " + GIT_BASH + "/tests/App.Unit.Tests/obj", "allow"),
    (B, "rm -rf /tmp/wce-scratch", "allow"),
    (B, "rm -f notes.tmp", "allow"),
    (B, "rm -rf src", "ask"),
    (B, "rm -rf docs/gates", "ask"),
    (B, "rm -rf src/*", "ask"),
    (B, "rm -rf /usr/bin", "ask"),
    (B, "rm -rf bin/../src", "ask"),
    (B, "cd / && rm -rf bin", "ask"),
    (B, "rm -rf bin", "ask", OUTSIDE),
    (B, "rm -rf \"$SOMEWHERE\"", "ask"),
    (B, "find . -type d \\( -name bin -o -name obj \\) -exec rm -rf {} +", "allow"),
    (B, "find tests -name TestResults -type d -exec rm -rf {} +", "allow"),
    (B, "find . -type d -name obj | xargs rm -rf", "allow"),
    (B, "find . -name '*.cs' -delete", "ask"),
    (B, "find . -name '*.tmp' -exec rm {} \\;", "ask"),
    (B, "find / -name bin -exec rm -rf {} +", "ask"),
    (B, "find . -not -name bin -delete", "ask"),
    (B, "ls | xargs rm -rf", "ask"),
    (B, "cmd /c \"rd /s /q .\"", "deny"),
    (B, "cmd /c \"rd /s /q bin\"", "allow"),
    (P, "Remove-Item -Recurse -Force .", "deny"),
    (P, "Remove-Item -Recurse -Force *", "deny"),
    (P, "Remove-Item .\\* -Recurse -Force", "deny"),
    (P, "Remove-Item -Path " + PROJECT + " -Recurse -Force", "deny"),
    (P, "Remove-Item -LiteralPath $HOME -Recurse -Force", "deny"),
    (P, "rm -r -fo .", "deny"),
    (P, "rm -rf *", "deny"),
    (P, "Remove-Item -Recurse -Force bin, obj", "allow"),
    (P, "Remove-Item -Recurse -Force .\\src\\App.Web\\bin", "allow"),
    (P, "Remove-Item -Recurse -Force TestResults", "allow"),
    (P, "Remove-Item -Path . -Recurse -Include bin,obj -Force", "allow"),
    (P, "Get-ChildItem -Recurse -Directory -Include bin,obj | Remove-Item -Recurse -Force", "allow"),
    (P, "Remove-Item -Recurse -Force \"$env:TEMP\\wce-scratch\"", "allow"),
    (P, "Remove-Item notes.tmp", "allow"),
    (P, "Remove-Item -Recurse .", "ask"),
    (P, "Remove-Item -Recurse src", "ask"),
    (P, "Remove-Item -Recurse -Force src", "ask"),
    (P, "Get-ChildItem -Recurse | Remove-Item -Recurse -Force", "ask"),
    (P, "Get-ChildItem C:\\ -Recurse -Include bin | Remove-Item -Recurse -Force", "ask"),
    # Approval boundaries ask.
    (B, "gh workflow run cd-dev.yml -f deploy=true", "ask"),
    (B, "gh workflow run \"CD Dev\"", "ask"),
    (B, "gh run rerun 37111783729", "ask"),
    (B, "gh api -X POST repos/example-owner/fully/dispatches -f event_type=x", "ask"),
    (B, "gh api repos/example-owner/fully/issues -f title=x", "ask"),
    (B, "gh pr create --title x --body y", "ask"),
    (B, "az deployment group create -g rg -f main.bicep", "ask"),
    (B, "az keyvault secret set --vault-name kv --name x --value y", "ask"),
    (B, "az role assignment create --assignee x --role Reader --scope /subscriptions/x", "ask"),
    (B, "az containerapp job start -n caj-wce-dev-neu-migrate -g rg", "ask"),
    (B, "az containerapp update -n app -g rg --image x", "ask"),
    (B, "az rest --method put --url https://management.azure.com/x --body {}", "ask"),
    (P, "./eng/infra.ps1 deploy -Environment dev -Approve", "ask"),
    (P, "pwsh -File eng/infra.ps1 deploy -Approve", "ask"),
    (P, ".\\eng\\infra.ps1 -Action deploy -Approve:$true", "ask"),
    (P, "Set-AzKeyVaultSecret -VaultName kv -Name x -SecretValue $s", "ask"),
    (P, "New-AzRoleAssignment -ObjectId x -RoleDefinitionName Reader", "ask"),
    (P, "New-AzResourceGroupDeployment -ResourceGroupName rg -TemplateFile main.bicep", "ask"),
    (B, "docker push ghcr.io/x/y:tag", "ask"),
    (B, "azd up", "ask"),
    (B, "dotnet nuget push x.nupkg", "ask"),
    (P, "Invoke-RestMethod -Uri https://api.monday.com/v2 -Method Post -Body $b", "ask"),
    (P, "$r = Invoke-RestMethod https://api.monday.com/v2", "ask"),
    (P, "[Net.WebClient]::new().DownloadString(\"https://api.monday.com/v2\")", "ask"),
    (P, "curl https://auth.monday.com/oauth2/token", "ask"),
    (B, "python -c \"import urllib.request; urllib.request.urlopen('https://api.monday.com/v2')\"", "ask"),
    (B, "python - <<'EOF'\nimport requests\nrequests.post('https://api.monday.com/v2')\nEOF", "ask"),
    (B, "wget https://auth.monday.com/oauth2/token", "ask"),
    (B, "curl -s https://management.azure.com/subscriptions?api-version=2020-01-01", "ask"),
    (B, "psql -h wce.postgres.database.azure.com -U x -c 'select 1'", "ask"),
    (B, "docker exec work-continuity-local-postgres-1 psql -U x -d y -c \"DROP DATABASE workcontinuity\"", "ask"),
    (B, "dotnet ef database drop --force", "ask"),
    (P, "./eng/db.ps1 reset", "ask"),
    (B, "docker volume rm work-continuity-local_postgres-data", "ask"),
    (B, "dropdb -h localhost -U postgres workcontinuity", "ask"),
    (B, "git push --dry-run", "ask"),
    (B, "git add -u", "ask"),
    # Known temporary test databases may be cleaned up (step 5B).
    (B, "docker exec work-continuity-local-postgres-1 psql -U x -d y -c \"DROP DATABASE IF EXISTS wce_it_gate3b_privileges WITH (FORCE)\"", "allow"),
    (B, "dropdb -h localhost -U postgres wce_it_gate3f_view", "allow"),
    # Ordinary work stays allowed, and text that is only data is not a command.
    (B, "git --no-optional-locks diff --stat", "allow"),
    (B, "git log --oneline -5", "allow"),
    (B, "git show HEAD --stat", "allow"),
    (B, "git fetch origin", "allow"),
    (B, "git stash list", "allow"),
    (B, "git add src/App.Core/Foo.cs tests/Bar.cs", "allow"),
    (B, "git commit -m \"Never git push --force or git add . here\"", "allow"),
    (B, "git commit -m \"$(cat <<'EOF'\nfix: don't git push yet\nEOF\n)\"", "allow"),
    (B, "echo \"git push --force && git reset --hard && rm -rf /\"", "allow"),
    (B, "printf '%s\\n' \"az keyvault secret set\"", "allow"),
    (B, "grep -rn \"api.monday.com\" src/ docs/", "allow"),
    (B, "echo https://api.monday.com/v2", "allow"),
    (B, "cat <<'EOF' > notes.md\ngit push --force\ndocker volume prune -f\nrm -rf /\nEOF", "allow"),
    (B, "git commit -F - <<'EOF'\ngit push --force\nEOF", "allow"),
    (B, "git status # then git push", "allow"),
    (P, "Write-Output \"git push; git reset --hard; Remove-Item -Recurse -Force .\"", "allow"),
    (P, "Select-String -Path docs/*.md -Pattern 'monday.com'", "allow"),
    (P, "$lines | Where-Object { $_ -match 'monday.com' }", "allow"),
    (P, "git commit -m @'\nnever git push --force\n'@", "allow"),
    (B, "gh run view 37111783729 --log", "allow"),
    (B, "gh run list --workflow cd-dev.yml --limit 3", "allow"),
    (B, "gh run watch 37111783729 --exit-status --interval 30", "allow"),
    (B, "gh workflow view cd-dev.yml", "allow"),
    (B, "gh api repos/example-owner/fully/actions/runs", "allow"),
    (B, "gh auth status", "allow"),
    (B, "az account show", "allow"),
    (B, "az deployment group what-if -g rg -f main.bicep", "allow"),
    (B, "az bicep build --file infra/bicep/main.bicep", "allow"),
    (B, "az containerapp job execution list -n caj -g rg", "allow"),
    (P, "Get-AzResource -ResourceGroupName rg", "allow"),
    (B, "timeout 600 dotnet build WorkContinuityEngine.sln --no-incremental -nologo -v q", "allow"),
    (B, "dotnet ef migrations script --idempotent", "allow"),
    (B, "docker exec work-continuity-local-postgres-1 sh -c 'psql -U \"$POSTGRES_USER\" -d \"$POSTGRES_DB\" -tAc \"SELECT conname FROM pg_constraint\"'", "allow"),
    (B, "docker compose -f infra/local/docker-compose.yml down", "allow"),
    (B, "docker ps --format '{{.Names}} {{.Status}}'", "allow"),
    (P, "./eng/dev.ps1 down", "allow"),
    (P, "./eng/infra.ps1 whatif -Environment dev", "allow"),
    (B, "ls -la .claude && git status --short", "allow"),
    (B, "\"/c/tools/python/python.exe\" eng/verify-architecture.py", "allow"),
    # monday MCP: reads pass to the normal flow; test-data changes ask; destructive, admin, execution and automation deny.
    (M + "all_api_read", None, "allow"),
    (M + "search", None, "allow"),
    (M + "get_board_items_page", None, "allow"),
    (M + "list_workspaces", None, "allow"),
    (M + "show-table", None, "allow"),
    (M + "create_board", None, "ask"),
    (M + "create_column", None, "ask"),
    (M + "create_update", None, "ask"),
    (M + "create_notification", None, "ask"),
    (M + "move_object", None, "ask"),
    (M + "delete_view", None, "deny"),
    (M + "vibe_delete", None, "deny"),
    (M + "run_action", None, "deny"),
    (M + "create_action", None, "deny"),
    (M + "run_workflow_once", None, "deny"),
    (M + "invoke_workflow_expert", None, "deny"),
    (M + "connect_external_agent", None, "deny"),
    (M + "update_workspace", None, "deny"),
    (M + "vibe_collaborators", None, "deny"),
    (M + "frobnicate_board", None, "ask"),
    ("mcp__plugin_monday-crm_monday__create_item", None, "ask"),
    ("mcp__stitch__list_projects", None, "allow"),
    # Step 6 - frozen documents are always denied (required cases 1-3).
    (E, PRD, "deny"),
    (E, under("docs", "canonical", "SYSTEM-ARCHITECTURE-v1.md"), "deny"),
    (W, under("docs", "premium", "PREMIUM-POLICIES-v1.md"), "deny"),
    (E, under("docs", "canonical", "ARCHITECTURE-INVARIANTS-v1.md"), "deny"),
    (W, under("docs", "canonical", "DEVELOPMENT-GATES-v1.md"), "deny"),
    (E, under("docs", "premium", "FULLY-PREMIUM-ARCHITECTURE-v1.md"), "deny"),
    (N, PRD, "deny"),
    ("MultiEdit", PRD, "deny"),
    # Normal project edits stay in the normal flow (required cases 4-6, 11, 12).
    (E, under("docs", "canonical", "PROJECT-STATUS.md"), "allow"),
    (E, under("src", "App.Core", "Connectors", "IConnectorAuthorizationStore.cs"), "allow"),
    (E, under("tests", "App.Unit.Tests", "MondayConnectionAuthorizerTests.cs"), "allow"),
    (E, under("docs", "gates", "gate-3-scope.md"), "allow"),
    (E, under("CLAUDE.md"), "allow"),
    (E, under("INIT.md"), "allow"),
    (E, under("README.md"), "allow"),
    (W, os.path.join(MIGRATIONS, "20261005000000_NewThing.cs"), "allow"),
    (W, os.path.join(MIGRATIONS, "20261005000000_NewThing.Designer.cs"), "allow"),
    (E, os.path.join(MIGRATIONS, "AdministrativeDbContextModelSnapshot.cs"), "allow"),
    (E, under("infra", "local", "docker-compose.yml"), "allow"),
    (E, under("docs", "canonical", "PRD-v1.md.bak"), "allow"),
    (E, under(".envrc"), "allow"),
    (W, os.path.join(SCRATCH, "notes.md"), "allow"),
    # Sensitive files ask (required cases 7-10).
    (E, under(".github", "workflows", "ci.yml"), "ask"),
    (E, under(".github", "workflows", "cd-dev.yml"), "ask"),
    (E, under("infra", "bicep", "main.bicep"), "ask"),
    (W, under("infra", "bicep", "modules", "new-module.bicep"), "ask"),
    (E, under(".env"), "ask"),
    (E, under(".env.example"), "ask"),
    (W, under(".env.local"), "ask"),
    (E, under("src", ".env"), "ask"),
    (E, COMMITTED_MIGRATION, "ask"),
    (E, COMMITTED_MIGRATION.replace(".cs", ".Designer.cs"), "ask"),
    # The same frozen document through other path forms is still denied (required case 13 and robustness).
    (E, "docs/canonical/PRD-v1.md", "deny"),
    (E, "docs\\canonical\\PRD-v1.md", "deny"),
    (E, "./docs/canonical/../canonical/PRD-v1.md", "deny"),
    (E, "src/../docs/canonical/PRD-v1.md", "deny"),
    (E, "PRD-v1.md", "deny", under("docs", "canonical")),
    (E, "../canonical/PRD-v1.md", "deny", under("docs", "premium")),
    (E, FWD + "/docs/canonical/PRD-v1.md", "deny"),
    (E, GIT_BASH + "/docs/canonical/PRD-v1.md", "deny"),
    (E, PROJECT.lower() + "\\DOCS\\Canonical\\prd-V1.MD", "deny"),
    (E, PROJECT.upper() + "\\DOCS\\CANONICAL\\PRD-V1.MD", "deny"),
    (E, PROJECT + "/docs\\canonical/PRD-v1.md", "deny"),
    (E, PROJECT + "\\src\\..\\docs\\canonical\\PRD-v1.md", "deny"),
    (E, PROJECT + "\\docs\\.\\canonical\\\\PRD-v1.md", "deny"),
    (E, PRD + ".", "deny"),
    (E, PRD + " ", "deny"),
    (E, PRD + "::$DATA", "deny"),
    (E, PRD + ":hidden", "deny"),
    (E, "\\\\?\\" + PRD, "deny"),
    (E, under(".ENV"), "ask"),
    (E, under(".GitHub", "Workflows", "CI.YML"), "ask"),
    (E, PROJECT + "/infra/BICEP/../bicep/main.bicep", "ask"),
    # Paths the guard cannot resolve safely ask instead of passing (fail closed).
    (E, None, "ask"),
    (E, "%USERPROFILE%\\Desktop\\fully\\docs\\canonical\\PRD-v1.md", "ask"),
    (E, "~/Desktop/fully/docs/canonical/PRD-v1.md", "ask"),
    (E, "$HOME/Desktop/fully/docs/canonical/PRD-v1.md", "ask"),
    (E, "\\\\localhost\\" + PRD[0] + "$" + PRD[2:], "ask"),
    (E, "C:docs\\canonical\\PRD-v1.md", "ask"),
    # Direct shell writes to protected files are caught too; reads and data stay allowed.
    (B, "echo x > docs/canonical/PRD-v1.md", "deny"),
    (B, "echo x >> docs/premium/PREMIUM-POLICIES-v1.md", "deny"),
    (B, "sed -i 's/a/b/' docs/canonical/SYSTEM-ARCHITECTURE-v1.md", "deny"),
    (B, "cp /tmp/x docs/canonical/DEVELOPMENT-GATES-v1.md", "deny"),
    (B, "cp /tmp/PRD-v1.md docs/canonical/", "deny"),
    (B, "mv docs/canonical/PRD-v1.md /tmp/", "deny"),
    (B, "rm docs/canonical/PRD-v1.md", "deny"),
    (B, "tee docs/canonical/PRD-v1.md < /tmp/x", "deny"),
    (B, "printf x > " + GIT_BASH + "/docs/premium/FULLY-PREMIUM-ARCHITECTURE-v1.md", "deny"),
    (B, "echo X=1 >> .env", "ask"),
    (B, "cat /tmp/x > .github/workflows/ci.yml", "ask"),
    (B, "echo x > \"$TARGET/docs/canonical/PRD-v1.md\"", "ask"),
    (B, "cat docs/canonical/PRD-v1.md", "allow"),
    (B, "grep -n Invariant docs/canonical/ARCHITECTURE-INVARIANTS-v1.md > /tmp/notes.txt", "allow"),
    (B, "sha256sum -c baseline.sha256 > /dev/null", "allow"),
    (B, "git diff docs/canonical/PRD-v1.md", "allow"),
    (B, "echo \"docs/canonical/PRD-v1.md\"", "allow"),
    (B, "echo x > \"$OUT\"", "allow"),
    (P, "Set-Content -Path docs\\canonical\\PRD-v1.md -Value x", "deny"),
    (P, "\"x\" | Out-File docs/canonical/PRD-v1.md", "deny"),
    (P, "Remove-Item docs/premium/PREMIUM-POLICIES-v1.md", "deny"),
    (P, "Move-Item docs/canonical/PRD-v1.md -Destination C:\\Windows\\Temp", "deny"),
    (P, "Add-Content .env \"X=1\"", "ask"),
    (P, "Copy-Item x -Destination infra\\bicep\\main.bicep", "ask"),
    (P, "Get-Content docs/canonical/PRD-v1.md", "allow"),
    (P, "Set-Content notes.txt \"docs/canonical/PRD-v1.md\"", "allow"),
    (P, "git status > $null", "allow"),
    # The self-tests prove the hooks are wired; they are denied and never run anything.
    (B, "fully-guard-selftest", "deny"),
    (P, "fully-guard-selftest", "deny"),
    (E, os.path.join(SCRATCH, "fully-guard-selftest"), "deny"),
] + ([(E, SHORT_PRD, "deny")] if SHORT_PRD else []) + ([(E, SHORT_CI, "ask")] if SHORT_CI else []) \
  + ([(E, SHORT_BICEP, "ask")] if SHORT_BICEP else [])


def decision_of(stdout):
    text = stdout.strip()
    if not text:
        return "allow", ""
    output = json.loads(text)["hookSpecificOutput"]
    assert output["hookEventName"] == "PreToolUse"
    return output["permissionDecision"], output.get("permissionDecisionReason", "")


def payload_of(tool, value, cwd=PROJECT):
    if tool in (B, P):
        tool_input = {"command": value}
    elif tool == N:
        tool_input = {"new_source": "x"} if value is None else {"notebook_path": value, "new_source": "x"}
    elif tool in FILE_TOOLS:
        tool_input = {"content": "x"} if tool == W else {"old_string": "a", "new_string": "b"}
        if value is not None:
            tool_input["file_path"] = value
    else:
        tool_input = {}
    return json.dumps({"tool_name": tool, "tool_input": tool_input, "cwd": cwd})


def run_guard(tool, command, cwd=PROJECT):
    result = subprocess.run([sys.executable, GUARD], input=payload_of(tool, command, cwd).encode("utf-8"),
                            capture_output=True, timeout=30)
    if result.returncode != 0:
        return "error", result.stderr.decode("utf-8", "replace")[-300:]
    return decision_of(result.stdout.decode("utf-8", "replace"))


def run_configured_hook(matcher, tool, command):
    """Runs the exact hook command from settings.local.json through Git Bash, as Claude Code does."""
    settings = json.load(open(SETTINGS, encoding="utf-8"))
    entries = [entry for entry in settings["hooks"]["PreToolUse"] if entry.get("matcher") == matcher]
    assert len(entries) == 1, f"expected exactly one PreToolUse entry for {matcher}"
    hook = entries[0]["hooks"][0]
    bash = next((path for path in ("C:/Program Files/Git/bin/bash.exe", "C:/Program Files/Git/usr/bin/bash.exe")
                 if os.path.exists(path)), shutil.which("bash"))
    environment = dict(os.environ, CLAUDE_PROJECT_DIR=PROJECT)
    result = subprocess.run([bash, "-c", hook["command"]], input=payload_of(tool, command).encode("utf-8"),
                            capture_output=True, timeout=30, env=environment)
    if result.returncode != 0:
        return "error", result.stderr.decode("utf-8", "replace")[-300:]
    return decision_of(result.stdout.decode("utf-8", "replace"))


def load_guard():
    spec = importlib.util.spec_from_file_location("fully_approval_guard", GUARD)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def identity_check():
    """A hardlink under an innocent name is still the frozen file: proved on two temporary stand-in files outside the
    repository, so no protected file is linked or touched."""
    import tempfile
    guard = load_guard()
    directory = tempfile.mkdtemp(prefix="fully-guard-identity-")
    original = os.path.join(directory, "frozen-stand-in.md")
    alias = os.path.join(directory, "innocent-name.md")
    try:
        with open(original, "w", encoding="utf-8") as handle:
            handle.write("stand-in")
        os.link(original, alias)
        guard.FROZEN_IDENTITIES = {guard.file_identity(original)}
        return guard.decide(guard.protected_findings(alias, None))[0] == "deny"
    finally:
        for path in (alias, original):
            if os.path.exists(path):
                os.remove(path)
        os.rmdir(directory)


def config_checks():
    """Reads the project-local settings: the monday rules match the guard's classification and nothing allows a write."""
    results = [("a hardlink to a frozen document is denied by file identity", identity_check())]
    guard = load_guard()
    read, ask, deny = guard.MONDAY_READ, guard.MONDAY_ASK, guard.MONDAY_DENY
    classified = read | ask | deny
    results.append(("every session monday tool is classified exactly once",
                    set(SESSION_MONDAY_TOOLS) <= classified and not (read & ask or read & deny or ask & deny)
                    and len(read) + len(ask) + len(deny) == len(classified)))
    settings = json.load(open(SETTINGS, encoding="utf-8"))
    permissions = settings.get("permissions", {})
    results.append(("permissions.deny lists exactly the destructive, admin, execution and automation tools",
                    {r for r in permissions.get("deny", []) if r.startswith(M)} == {M + t for t in deny}))
    results.append(("permissions.ask lists exactly the test-data change tools",
                    {r for r in permissions.get("ask", []) if r.startswith(M)} == {M + t for t in ask}))
    results.append(("no read tool is denied or asked by a rule",
                    not ({M + t for t in read} & (set(permissions.get("deny", [])) | set(permissions.get("ask", []))))))
    home = os.path.expanduser("~")
    sources = [os.path.join(home, ".claude", "settings.json"), os.path.join(home, ".claude", "settings.local.json"),
               os.path.join(PROJECT, ".claude", "settings.json"), SETTINGS]
    blanket = []
    for source in sources:
        if not os.path.exists(source):
            continue
        for rule in json.load(open(source, encoding="utf-8")).get("permissions", {}).get("allow", []):
            if "monday" in rule.lower() or rule in ("*", "mcp__*", "mcp__", "mcp") or rule.startswith("mcp__*"):
                blanket.append(f"{os.path.basename(os.path.dirname(source))}/{os.path.basename(source)}: {rule}")
    results.append(("no allow rule in any settings file matches a monday tool" + (f" ({blanket})" if blanket else ""),
                    not blanket))
    hooks = settings.get("hooks", {}).get("PreToolUse", [])
    commands = {entry.get("matcher"): entry["hooks"][0]["command"] for entry in hooks}
    results.append(("a monday MCP guard hook runs the same guard command as the shell hook",
                    commands.get("mcp__.*monday.*") is not None and commands.get("mcp__.*monday.*") == commands.get("Bash|PowerShell")))
    file_matcher = "Edit|Write|MultiEdit|NotebookEdit"
    results.append(("a file-edit guard hook covers Edit, Write, MultiEdit and NotebookEdit with the same guard command",
                    commands.get(file_matcher) is not None and commands.get(file_matcher) == commands.get("Bash|PowerShell")))
    results.append(("the six frozen documents are exactly the guard's hard-deny list",
                    sorted(guard.FROZEN_FILES) == sorted([
                        "docs/canonical/PRD-v1.md", "docs/canonical/SYSTEM-ARCHITECTURE-v1.md",
                        "docs/canonical/ARCHITECTURE-INVARIANTS-v1.md", "docs/canonical/DEVELOPMENT-GATES-v1.md",
                        "docs/premium/FULLY-PREMIUM-ARCHITECTURE-v1.md", "docs/premium/PREMIUM-POLICIES-v1.md"])
                    and all(os.path.isfile(under(*rel.split("/"))) for rel in guard.FROZEN_FILES)))

    def effective(tool):
        name = M + tool
        return "deny" if name in permissions.get("deny", []) else "ask" if name in permissions.get("ask", []) else "normal flow"

    for tool, expected in [("get_board_info", "normal flow"), ("all_api_read", "normal flow"), ("create_item", "ask"),
                           ("update_items", "ask"), ("change_item_column_values", "ask"), ("create_automation", "deny"),
                           ("manage_automations", "deny"), ("execute_code", "deny"), ("all_api_write", "deny"),
                           ("all_monday_api", "deny"), ("delete_view", "deny")]:
        results.append((f"permission rule for {tool}: {effective(tool)}", effective(tool) == expected))
    return results


def main():
    failures = 0
    for case in CASES:
        tool, command, expected = case[:3]
        cwd = case[3] if len(case) > 3 else PROJECT
        actual, reason = run_guard(tool, command, cwd)
        ok = actual == expected
        failures += 0 if ok else 1
        shown = (command if command is not None else "(no path)" if tool in FILE_TOOLS else "(MCP tool)").replace("\n", "\\n")
        label = tool if tool in (B, P) or tool in FILE_TOOLS else tool.replace(M, "monday:")
        print(f"{'PASS' if ok else 'FAIL'} {label[:38]:<38} {expected:<5} -> {actual:<5} {shown[:80]}"
              + ("" if cwd == PROJECT else f"  [cwd {cwd}]") + ("" if ok else f"   [{reason[:160]}]"))

    # Robustness: unreadable input asks instead of allowing; other tools pass through untouched.
    bad = subprocess.run([sys.executable, GUARD], input=b"not json", capture_output=True, timeout=30)
    robust = decision_of(bad.stdout.decode("utf-8", "replace"))[0] == "ask"
    edit = subprocess.run([sys.executable, GUARD], input=json.dumps({"tool_name": "Edit", "tool_input": {"file_path": "x"}}).encode(),
                          capture_output=True, timeout=30)
    passthrough = edit.stdout.strip() == b""
    print(f"{'PASS' if robust else 'FAIL'} malformed payload asks")
    print(f"{'PASS' if passthrough else 'FAIL'} a non-shell tool passes through")
    failures += (0 if robust else 1) + (0 if passthrough else 1)

    checks = config_checks()
    for description, ok in checks:
        failures += 0 if ok else 1
        print(f"{'PASS' if ok else 'FAIL'} config: {description}")

    configured = 0
    if "--configured" in sys.argv:
        for matcher, tool, command, expected in [
            ("Bash|PowerShell", B, "git status", "allow"), ("Bash|PowerShell", B, "git push", "ask"),
            ("Bash|PowerShell", P, "git add -A", "deny"), ("Bash|PowerShell", B, "rm -rf .", "deny"),
            ("mcp__.*monday.*", M + "get_board_info", None, "allow"), ("mcp__.*monday.*", M + "create_item", None, "ask"),
            ("mcp__.*monday.*", M + "execute_code", None, "deny"),
            ("Edit|Write|MultiEdit|NotebookEdit", E, PRD, "deny"),
            ("Edit|Write|MultiEdit|NotebookEdit", E, under("src", "App.Core", "Connectors", "IConnectorAuthorizationStore.cs"), "allow"),
            ("Edit|Write|MultiEdit|NotebookEdit", W, under(".env"), "ask"),
        ]:
            configured += 1
            actual, reason = run_configured_hook(matcher, tool, command)
            ok = actual == expected
            failures += 0 if ok else 1
            print(f"{'PASS' if ok else 'FAIL'} configured hook {tool.replace(M, 'monday:'):<24} {expected:<5} -> {actual:<5} "
                  f"{command or ''}" + ("" if ok else f"   [{reason[:200]}]"))
    for label, value in (("frozen", SHORT_PRD), ("workflow", SHORT_CI), ("bicep", SHORT_BICEP)):
        print(f"INFO 8.3 short-name {label} case " + (f"included: {value}" if value else "not available on this volume"))

    total = len(CASES) + 2 + len(checks) + configured
    print(f"\n{total - failures}/{total} passed")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
