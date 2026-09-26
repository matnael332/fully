# Gate 1C Report — Core Platform Skeleton

## Scope

Gate 1C adds the minimal Core platform primitives that later Gates depend on. It adds no business
behaviour: no monday logic, no AI, no UI, no database schema, no authentication flow.

## Implemented

### Tenant context — `WorkContinuity.Core.Tenancy`

- `TenantId` — opaque GUID-backed canonical identifier. Parsing proves well-formedness only, never
  trust.
- `TenantResolutionSource` — `None`, `AuthenticatedPrincipal`, `TrustedServiceIdentity`. There is
  deliberately **no** value meaning "the caller told us", so no code can resolve a tenant from
  request data and still name a legitimate source.
- `ITenantContext` / `TenantContext` — immutable. `TryGetTenantId` returns `false` when unresolved,
  forcing callers to handle the unresolved case instead of reading a silent default.
- `ITenantContextAccessor` / `TenantContextAccessor` — flow-scoped. Assigning a resolved tenant a
  second time in one scope throws, so the tenant context is immutable after resolution.

No database implementation, and nothing in the application resolves a tenant yet.

### Identity abstractions — `WorkContinuity.Core.Identity`

- `UserId` — canonical internal user/employee identifier, the only security identifier for a person.
- `IdentityProviderId` — validated provider key with `EntraId` and `LocalDemo` well-known values,
  keeping the platform provider-agnostic.
- `ExternalIdentity` — provider plus the provider's stable subject/object identifier.
- `PlatformRole` — Employee, Manager, TenantAdmin, SecurityAuditor, InternalSupport,
  ServiceIdentity.
- `TenantMembership` — tenant plus user plus an immutable role set plus an active flag.
- `Principal` — the authentication outcome only. It exposes no tenant, no roles and no permissions,
  which keeps authentication and authorization separate types.

No type anywhere in Core carries an email, UPN or mail member, asserted by test.

### Correlation — `WorkContinuity.Core.Correlation`

- `CorrelationId` — strict length and character allowlist. Carriage returns, line feeds and other
  control characters are rejected, so an inbound value cannot forge a log line or inject a header.
- `ICorrelationContext` / `CorrelationContext` — `Generate()` for a new operation,
  `FromInbound(...)` to propagate a valid caller value and silently replace an invalid one.
- `ICorrelationContextAccessor` / `CorrelationContextAccessor` — flow-scoped and transport-free, so
  the same primitive serves HTTP, the worker, and later the broker, AI calls, actions and audit.
  Core references no Azure or ASP.NET types.

### Clock — `WorkContinuity.Core.Time`

`IClock` with `DateTimeOffset UtcNow`, and `SystemClock`. No extra time library.

### Result / error model — `WorkContinuity.Core.Results`

- `ErrorCategory` — Validation, NotFound, Conflict, Unauthorized, Forbidden, Infrastructure.
- `Error` — category, stable machine-readable code, safe bounded message. It has no exception,
  stack-trace or inner-error member, and **no constructor or factory accepts an `Exception`**. That
  absence is the control: internal detail cannot be passed in by accident.
- `Result` and `Result<TValue>` — small readonly structs. Reading a value from a failure, or an
  error from a success, throws. No third-party result library.

### Feature flags — `WorkContinuity.Core.Features`

- `FeatureFlags.AutoExecutionFromLearning` = `AUTO_EXECUTION_FROM_LEARNING`.
- `IFeatureFlagProvider` with a global overload and a tenant-scoped overload.
- `InMemoryFeatureFlagProvider` — **absent means disabled**, so a flag is never on by accident. A
  tenant override beats the global value; a tenant without an override falls back to it.

No durable or Azure App Configuration store, as specified.

### Audit contracts — `WorkContinuity.Core.Audit`

Contracts only. `AuditOutcome` (including `Unknown`, distinct from `Failed`, so an unestablished
result is reconciled rather than retried as a failure), `AuditActorKind`, `AuditActor`,
`ResourceReference`, `AuditRecord` and `IAuditSink`.

`AuditRecord` carries tenant, actor, operation, resource reference, UTC timestamp, correlation id,
outcome and a rule/policy reason. It has no payload, content, body, document, snapshot or raw
member, and no open-ended `object`, dictionary or byte-array member that could smuggle work content
past a name check. Raw work data and security audit data keep separate purposes and lifecycles, so
the audit trail cannot become shadow storage for work content.

`IAuditSink` has no implementation and is not registered: nothing can persist an audit record until
the storage Gate supplies a sink with its own retention lifecycle.

### Operation context — `WorkContinuity.Core.Operations`

`IOperationContext` / `OperationContext` with exactly four validated primitives: tenant context,
principal, correlation id and a UTC start time taken from `IClock`. No repositories, no clients, no
behaviour — asserted by a test that fails if a fifth member appears.

### Web integration — `App.Web`

- `CorrelationMiddleware` — outermost. Accepts a valid inbound `X-Correlation-Id`, otherwise
  generates one, and writes the parsed value to the response header.
- `PlatformExceptionMiddleware` — converts any unhandled exception into HTTP 500 with a fixed safe
  JSON body plus the correlation id. Exception detail goes to server-side logs only.
- `PlatformErrors` / `PlatformErrorResponse` — the wire shape exposes only code, message and
  correlation id.
- DI registrations for `IClock`, both accessors and `IFeatureFlagProvider`.
- Health endpoints unchanged and still working.
- **No middleware resolves a tenant.** A tenant id in a query string, header, route or body is never
  promoted into the security context.

### Worker integration — `App.Worker`

The worker composes `SystemClock`, `CorrelationContextAccessor` and `OperationContext` by hand,
starting a fresh correlation scope per pass and logging it. This proves the primitives carry no HTTP
or hosting dependency. No event processing was added.

## Test infrastructure

Gate 1C authorized a real test framework. One package was added: **xunit.v3**, self-executing on
Microsoft.Testing.Platform, so no separate runner or VSTest adapter package is needed.

Two toolchain repairs were required and are worth recording:

1. The .NET 10 SDK **removed the VSTest path** for Microsoft.Testing.Platform projects. `dotnet test`
   must be opted into the new runner via a `test.runner` entry in `global.json`. The older
   `TestingPlatformDotnetTestSupport` MSBuild property routes to the removed VSTest target and makes
   the build fail, so it is not used.
2. `tests/Directory.Build.props` centralizes test-project settings. Because `Directory.Build.props`
   discovery stops at the first file found walking up, it imports the repository-wide props
   explicitly.

The four previously placeholder test projects now contain real assertions.

## Verification (executed 2026-09-27)

| Check | Result |
|---|---|
| `dotnet restore` | PASS |
| `dotnet build -c Debug` | PASS — 0 warnings, 0 errors, `TreatWarningsAsErrors=true` |
| `dotnet test` (whole solution) | **PASS — 188 tests, 0 failed, 0 skipped** |
| `eng/verify-architecture.py` | PASS |
| `eng/verify-local-env.py` | PASS |
| Gate 1B regression (`eng/dev.ps1 up`, clean slate) | PASS |
| Live HTTP correlation header | PASS |
| Architecture/PRD contradictions | none |

Test distribution: App.Unit.Tests 138, App.Security.Tests 20, App.Integration.Tests 19,
App.Architecture.Tests 11.

### The tests were checked for vacuity

Assertions that assert an *absence* pass trivially if written wrongly, so the critical ones were
mutation-tested:

- Adding an `EmailAddress` property to `Principal` failed
  `No_core_type_exposes_an_email_member` and `A_principal_carries_no_tenant_and_no_roles`.
- Writing `exception.Message` into the error payload failed
  `The_response_leaks_no_exception_detail`.

Both mutations were reverted and the suite returned to 188 passing.

## Acceptance criteria

| Criterion | Result |
|---|---|
| `dotnet restore` PASS | yes |
| `dotnet build` PASS, 0 warnings, 0 errors | yes |
| Real automated tests PASS | yes — 188 |
| Architecture verification PASS | yes, script and test suite |
| Gate 1B local environment still PASS | yes |
| Correlation middleware verified | yes — automated tests and a live HTTP call |
| `AUTO_EXECUTION_FROM_LEARNING` defaults OFF | yes |
| Canonical identity does not depend on email | yes, asserted across the whole Core assembly |
| Tenant context cannot be silently derived from untrusted request data | yes — type system plus a source scan of every production file |
| Audit contracts avoid raw work content | yes, by member name and by member type |
| No architecture/PRD contradiction | none found |

## Gate status

**PASS.** Gate 1D was not started.
