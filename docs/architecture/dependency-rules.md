# Dependency Rules — Gate 1A

The MVP is a modular monolith with explicit compile-time boundaries.

## Allowed production dependencies

| Project | May reference |
|---|---|
| App.Core | none |
| App.Infrastructure | App.Core |
| App.Connectors.Monday | App.Core |
| App.Agent.Windows | App.Core |
| App.Simulator | App.Core |
| App.Web | App.Core, App.Infrastructure, App.Connectors.Monday |
| App.Worker | App.Core, App.Infrastructure, App.Connectors.Monday |

## Forbidden dependencies

- App.Core must never reference infrastructure, web, worker, connector, agent, or simulator projects.
- App.Infrastructure must not reference App.Web or App.Worker.
- Connector implementations must not reference App.Web, App.Worker, or the Windows agent.
- The Windows agent must not reference infrastructure or a SaaS connector.
- The simulator must not become a production dependency.
- No production project may reference any project under `/tests`.

## Test projects

Test projects may reference production projects; production projects may never reference a test
project. `App.Architecture.Tests` deliberately references no production project: it inspects the
project files on disk, so it cannot perturb the graph it validates.

## Package rules

`App.Core` references no external package at all, so it cannot acquire a web, Azure, database, AI or
SaaS dependency — not even transitively. Persistence packages belong to `App.Infrastructure` only.

Gate 1G introduced the first Azure packages, each allowed in exactly one project and nowhere else:
`Azure.Identity` and `Azure.Messaging.ServiceBus` in `App.Infrastructure` (the readiness probe's
Service Bus link check, authenticated by the workload identity); `Azure.Monitor.OpenTelemetry.AspNetCore`
in `App.Web` and `Azure.Monitor.OpenTelemetry.Exporter` in `App.Worker` (telemetry export). Every
other `Azure.*` or `Microsoft.Azure.*` package, and every AI provider package, remains forbidden in
every production project until the Gate that introduces it.

## Enforcement

These rules are enforced twice: by `eng/verify-architecture.py` and by the assertions in
`tests/App.Architecture.Tests`, so a violation fails both the gate script and the build.

## Architectural intent

Business contracts and invariants live in `App.Core`. Infrastructure implements interfaces from Core. Composition roots are `App.Web` and `App.Worker`. SaaS adapters are replaceable. The Windows agent remains a limited sensor/executor rather than a business authority.
