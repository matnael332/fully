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

## Architectural intent

Business contracts and invariants live in `App.Core`. Infrastructure implements interfaces from Core. Composition roots are `App.Web` and `App.Worker`. SaaS adapters are replaceable. The Windows agent remains a limited sensor/executor rather than a business authority.
