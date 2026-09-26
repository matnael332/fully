# Package Contents

This is the canonical local project package for Claude Code.

Important root files:
- `CLAUDE.md` — mandatory Claude Code instructions.
- `README.md` — project entry point and current status.
- `WorkContinuityEngine.sln` — .NET solution.
- `global.json` — .NET SDK baseline.
- `Directory.Build.props` / `Directory.Packages.props` — solution-wide build/package configuration.
- `.env.example` — local secret/config template. Never commit a real `.env`.

Canonical documentation:
- `docs/canonical/PRD-v1.md`
- `docs/canonical/SYSTEM-ARCHITECTURE-v1.md`
- `docs/canonical/ARCHITECTURE-INVARIANTS-v1.md`
- `docs/canonical/DEVELOPMENT-GATES-v1.md`
- `docs/canonical/PROJECT-STATUS.md`

Implemented development work:
- Gate 1A repository/solution skeleton — **PASS**, runtime verified.
- Gate 1B local development environment — **PASS**, runtime verified.

Build and Docker smoke verification is complete; see `docs/canonical/PROJECT-STATUS.md` and the reports in `docs/gates/` for the recorded evidence. Real test-project infrastructure remains scheduled for a later Gate. Do not start Gate 1C without explicit product-owner authorization.
