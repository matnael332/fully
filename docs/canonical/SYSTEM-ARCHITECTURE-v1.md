# System Architecture v1

Status: **READY TO FREEZE / canonical implementation baseline**

This document is subordinate to PRD v1 and the Architecture Invariants. When implementation detail conflicts with those documents, the canonical product/invariant rule wins and the conflict must be reported.

## 1. System context

External actors/systems:
- monday.com first SaaS connector;
- enterprise Identity Provider;
- employee phone/browser;
- employee Windows PC + Local Agent;
- external AI providers.

Core internal planes:

```text
SaaS / Agent / Employee / IdP events
        ↓
Connector + Event Ingestion
        ↓
Normalize + Validate
        ↓
Source-of-Truth Resolver
        ↓
Fact Event Ledger + Current Snapshot
        ↓
Work Memory + Context Builder
        ↓
AI Gateway
        ↓
Structured AI Output
        ↓
Deterministic Validator
        ↓
Brief / Recommendation / Question
        ↓
Action Engine → Policy → Approval → Executor
```

Cross-cutting:
- tenancy;
- security;
- audit;
- metrics;
- observability;
- retention.

## 2. Integration plane

Each SaaS uses an adapter that:
- authenticates with provider;
- receives webhooks/events;
- fetches current state for reconciliation;
- maps provider objects/events to canonical domain objects/events;
- does not leak provider-specific semantics into Core.

First adapter: `MondayAdapter` implementing a generic SaaS connector contract.

Webhooks are fast path; provider API reconciliation is correctness path.

Initial connection flow:
1. establish connector;
2. activate/buffer realtime intake;
3. read current authorized source state;
4. build initial Snapshot;
5. record sync watermark;
6. replay buffered newer events;
7. enter normal realtime mode.

## 3. Event and data plane

Use a CloudEvents-compatible canonical envelope with product extensions.

Core concepts:
- `id`;
- `source`;
- `type`;
- `subject`;
- `occurred_at`;
- `source_updated_at`;
- `received_at`;
- `processed_at`;
- `tenant_id`;
- actor/object identity;
- before/after change;
- `source_event_id` where available;
- `schema_version`;
- `correlation_id`;
- `causation_id`;
- source reference;
- domain payload.

Processing lifecycle:

```text
Ingress
→ source/auth verification
→ tenant resolution
→ durable receipt
→ ACK source
→ normalization
→ schema validation
→ dedupe
→ identity resolution
→ authority resolution
→ ordering/conflict check
→ canonical event
→ snapshot projection
→ work-memory update
→ downstream metrics/audit/brief work
```

ACK occurs only after durable receipt, not after downstream AI processing.

### Ordering

No blind receipt-order / last-write-wins.

Preference:
1. provider revision/sequence;
2. authoritative source update timestamp;
3. occurrence time;
4. if ambiguous, do not overwrite; reconcile.

Ordering is per object/field, not global.

### Deduplication

Prefer stable provider event ID within tenant/connection when available. Otherwise use a retry/fingerprint strategy only to flag duplicate candidates; reconcile if uncertain.

Internal consumers are idempotent.

### Snapshot

Critical fields carry provenance:
- value;
- authority;
- source object;
- source event;
- source update time;
- observed time;
- version;
- freshness/staleness.

Intent to mutate never becomes Snapshot fact until the authoritative source confirms.

### Quarantine

Bad schema, unknown tenant, unknown schema version, impossible ordering, authority conflict or invalid authentication is quarantined and cannot affect Snapshot/AI Facts/Brief/Actions.

## 4. Work Memory and intelligence plane

Work Memory has distinct layers:
- Current Snapshot — authoritative current facts;
- Structured Work Memory — work relationships/context;
- Semantic/Vector Memory — retrieval aid only;
- Open Uncertainties — unresolved material questions;
- relevant recent history.

Context Builder produces task-specific minimal context bundles. It does not send the entire organization or entire 30-day raw history to the model.

AI Gateway receives:
- task type;
- tenant/user context;
- data classification;
- prompt version;
- model policy;
- context bundle;
- evidence manifest;
- output schema;
- trace ID.

AI output separates:
- facts;
- interpretations;
- unknowns;
- recommendations;
- clarification questions;
- evidence references.

Evidence strength is derived from our source/provenance rules. LLM self-confidence is advisory only.

A deterministic validator downgrades/rejects unsupported facts and prevents AI output from bypassing action policy.

### Safe Mode

If configured AI providers fail:
- use confirmed Snapshot;
- deterministic rules;
- simple templates;
- no advanced inference.

The product still shows useful factual work-state information.

## 5. Planning and uncertainty

Open uncertainty is stored explicitly with:
- related object;
- reason;
- evidence;
- possible impact;
- created/resolved time.

Only materially relevant uncertainty is shown in the morning flow.

Overload validation uses deterministic source-backed inputs. AI may suggest plans but cannot perform hard reject from AI-only task-duration estimates.

## 6. Action engine

Only path to execution:

```text
Recommendation / user intent
→ Action Request
→ Action Classifier
→ Policy Engine
→ Risk Evaluation
→ Approval Check
→ Authorized Action Plan
→ Executor
→ Verification
→ Action Result
→ Audit
```

Action lifecycle includes:
- PROPOSED;
- CLASSIFIED;
- POLICY_CHECKED;
- WAITING_APPROVAL;
- AUTHORIZED;
- DISPATCHED;
- EXECUTING;
- VERIFYING;
- SUCCEEDED / FAILED / REJECTED / EXPIRED / CANCELLED / UNKNOWN.

`UNKNOWN` is not automatically retried as if failed; reconcile first.

Approvals bind to:
- employee;
- action ID;
- target;
- type;
- payload hash;
- timestamp;
- expiry.

Material payload change invalidates approval.

## 7. Windows Local Agent

Internal components:
- Device Identity;
- Local Policy Guard;
- Activity Sensor;
- Workspace Executor;
- Secure Sync + Local Queue.

Agent receives typed allowlisted actions only. No shell/script primitive.

Preferred integration hierarchy:
1. official SaaS API/Connector;
2. deep link/documented protocol;
3. OS/application metadata;
4. UI automation only as limited fallback.

Business mutations should use cloud connector/API when possible. Agent mainly orchestrates the local workspace.

Device enrollment binds Tenant → Employee → Device → Agent instance.

Cloud does not open an inbound port to the PC. Agent initiates authenticated outbound communication.

MVP cloud-to-agent transport may use authenticated short/long polling behind an abstraction that can later move to persistent push transport.

Offline local store is temporary, encrypted, and minimized.

## 8. Security and multi-tenancy

Identity first implementation: Microsoft Entra ID with OIDC abstraction.

Successful SSO is followed by:
- token validation;
- issuer/tenant validation;
- internal organization provisioning check;
- employee provisioning check;
- active account check;
- role/resource authorization.

Tenant context is established server-side.

Authorization uses both role-level and resource-level controls.

Manager access is aggregate by default.

Tenant Admin manages users, policies, connectors and devices but does not automatically gain unrestricted employee content access.

Internal support uses metadata by default and time-limited/scoped audited elevation for justified access.

Isolation applies to:
- relational data;
- cache keys;
- queues/jobs;
- object storage;
- vector retrieval;
- audit/logging;
- AI context.

Secrets use Key Vault and managed workload identities where possible.

Raw Work Data and Security Audit data use separate retention/purpose boundaries.

## 9. MVP deployment architecture

Architecture style: **event-driven Modular Monolith**.

Deployables:
1. Web/API;
2. Background Worker;
3. Scheduled/Synthetic Jobs;
4. Windows Agent distributed separately.

### Stack

- .NET 10 LTS / ASP.NET Core;
- React + TypeScript UI;
- Responsive PWA for phone;
- .NET 10 + Windows App SDK / WinUI 3 Agent;
- MSIX packaging;
- Azure Database for PostgreSQL Flexible Server;
- pgvector extension;
- Azure Service Bus Standard;
- Azure Container Apps;
- Azure Container Apps Jobs;
- Azure Key Vault;
- Azure Blob Storage for large/temporary approved content;
- Microsoft Entra ID;
- OpenTelemetry + Application Insights + Log Analytics;
- Azure Front Door + WAF before Human Pilot;
- Azure Container Registry;
- GitHub Actions;
- Bicep.

### Database

PostgreSQL stores operational entities such as:
- Tenant;
- Identity;
- Connection;
- Raw Receipt;
- Canonical Event;
- Snapshot;
- Work Memory;
- Uncertainty;
- Insight;
- Action/Audit;
- Metrics;
- embeddings via pgvector.

Tenant-aware tables include tenant isolation controls. RLS is planned as storage-level defense in depth in addition to application authorization.

### Broker

Initial logical queues/topics are intentionally limited:
- event-processing;
- ai-jobs;
- action-jobs.

Dead-letter handling is mandatory.

Service Bus is durable transport only; DB remains operational truth.

### Inbox/Outbox

Webhook receipt and outbox enqueue marker are committed transactionally before source ACK.

Outbox processing publishes to Service Bus. Consumers are idempotent.

### Agent local storage

SQLite for temporary:
- unsynced events;
- pending command state;
- last usable local state.

No local long-term work-history store.

### Environments

Minimum boundaries:
- Local Dev;
- Synthetic;
- Human Pilot/Production-like.

Synthetic and Human Pilot do not share DBs, credentials or connector secrets.

## 10. Observability and traceability

Use OpenTelemetry and correlation IDs across:

```text
Webhook
→ Event
→ Broker
→ Snapshot
→ AI invocation
→ Brief
→ Action
→ Agent
→ Result
```

Measure:
- API latency;
- webhook durable receipt latency;
- queue depth/DLQ;
- projection latency;
- AI latency/failures;
- Safe Mode activations;
- Agent failures;
- reconciliation failures;
- startup KPI.

Do not log raw secrets, bearer tokens or full sensitive payloads.

## 11. Synthetic Pilot architecture

20 synthetic workers × 31 days = 620 worker-days.

Two validation layers:
- deterministic simulator with known ground truth;
- real monday integration/test workspace.

VirtualAgent implements the same Action Contract as the Windows Agent, but a real Windows Agent must also pass smoke testing.

Runs:
- Run A — Functional;
- Run B — Chaos/Fault Injection;
- Run C — Security.

Fault injection includes:
- drop;
- delay;
- duplicate;
- reorder;
- corrupt;
- AI failure;
- connector failure;
- agent disconnect;
- permission revoke;
- approval expiry;
- action timeout.

Security probes include:
- cross-tenant resource access;
- cross-employee memory access;
- tenant-ID manipulation;
- wrong-device command;
- vector scope escape;
- approval replay/scope creep;
- prompt injection;
- unapproved send.

Every run stores simulation ID, seed, code/schema/prompt/scenario versions.

Golden Scenarios become a permanent regression suite.

## 12. Initial performance targets for pilot

Engineering targets, not marketing claims:
- durable webhook receipt p95 < 1s;
- ordinary event projection p95 < 5s;
- Morning Brief final assembly p95 < 10s when AI is available;
- Start Work plan dispatch p95 < 2s;
- Safe Mode generation p95 < 2s.

The user-facing ≤2 minute target refers to the morning interaction, not one server response.

## 13. Open risks intentionally left for implementation validation

These are not architecture contradictions:
- exact `Meaningful Work` event mapping for monday objects;
- hard time-feasibility validation when no authoritative duration exists;
- PWA notification delivery behavior across target browsers/devices;
- legal/commercial review of exact Controller/Processor contracts and employee notices before Human Pilot.

## 14. Development rule

Implementation may simplify internals, but may not weaken PRD behavior or invariants.

When a proposed code change:
- changes product behavior;
- creates a new authority path;
- weakens approval;
- changes retention/privacy;
- changes tenant isolation;
- changes Source of Truth;
- adds a new external capability;

Claude Code/developer must stop and report the conflict rather than silently changing the canonical documents.
