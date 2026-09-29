# Fully Premium Architecture v1

Status: **FROZEN — Premium Gate P0 (Architecture Freeze) PASS, closed by the product owner on 2026-09-29.** This is
the frozen Premium architecture baseline, subordinate to the frozen Core documents; a change to it needs the product
owner's explicit approval, and a new contradiction audit where it touches a Core rule. It authorizes no
implementation: Premium Gates P1 to P11 each need their own authorization.

## 0. Authority and document set

Fully Premium is specified by three documents in `docs/premium/` and one Gate report:

| Document | Role |
|---|---|
| `docs/premium/FULLY-PREMIUM-ARCHITECTURE-v1.md` (this document) | What Premium is: product model, components, the Tool call path, orchestration, durable state, Tools, untrusted content, memory, self-improvement, the Local Agent, the Support Domain, verification, cost and observability, kill switches, Core isolation, deployment, and the constraints on Core Gate 3 |
| `docs/premium/PREMIUM-POLICIES-v1.md` | The normative rules: action authority, entitlement, delegated identity and Policy Grants, the freedom boundaries, Tool guardrails, untrusted content, memory, self-improvement, Local Agent, Support, verification and recovery, kill switches, cost, audit and Core isolation |
| `docs/premium/PREMIUM-DEVELOPMENT-GATES-v1.md` | Premium Gates P0 to P11, their workflow and their relationship to the Core Gates |
| `docs/gates/gate-P0-report.md` | The contradiction audit against the Core documents and the product-owner decisions |

The Premium documents are subordinate to the frozen Core documents in `docs/canonical/` — PRD v1, Architecture
Invariants v1, System Architecture v1 and Development Gates v1 — and change none of them. Core behavior is exactly
what those documents state: for every Core tenant, and for a Premium tenant wherever no Premium rule applies. Where
Premium deliberately differs from a Core rule, the difference is named in the Premium Policies, classified in the Gate
P0 report, applies only to a tenant with proven Premium entitlement, and takes effect only after the product owner
approves it explicitly, as PRD v1 §23 requires for any change to the approval model or action authority. Anything the
Premium documents do not state follows Core.

The product owner approved the one intentional difference, the Premium Autonomous Action Policy, the three Premium
extensions and clarification C1 on 2026-09-29 (decisions D1 to D4 and C1 in the Gate P0 report), and closed P0 as PASS
the same day. Like every Premium rule, they take effect for a tenant only when the capability they govern has passed
its Premium Gate.

## 1. Product model

Fully is one product with two capability layers:

- **Fully Core** — understand, remember and prepare work (PRD v1).
- **Fully Premium** — understand, remember, prepare and **execute bounded autonomous work**.

Premium is an additional layer on top of Core: not a replacement for Core, not a second product and not a second
platform. It reuses Core (section 4) and adds only what bounded autonomous execution needs: an Agent Runtime, adaptive
orchestration over durable state, Tools with guardrails, a separate Premium Autonomous Action Policy evaluated by the
Core Policy Engine, delegated identity with Policy Grants, Premium memory layers, verification and recovery, cost
control, observability and kill switches, and business Domains, the first of which is Support.

**Premium never creates a second Source of Truth.** Facts about the customer's work stay in the Core Snapshot under
the Core authority rules. Premium's own records — Tasks, checkpoints, grants, budgets, memories — describe Premium's
work, never the customer's world, and an Agent's statement is never a fact.

A tenant whose Premium entitlement cannot be proven gets Core behavior only (Premium Policies §3).

## 2. Terminology

| Term | Meaning |
|---|---|
| Core tenant | A tenant without proven Premium entitlement. It gets exactly the behavior of the Core documents. |
| Premium tenant | A tenant whose Premium entitlement the system can prove at the moment of use. |
| Operator | The organization that runs Fully. It controls entitlement and the all-Premium kill switch. |
| Tenant Admin | The tenant's administrator (System Architecture v1 §8). In Premium, the Tenant Admin configures the tenant's Premium Autonomous Action Policy and the tenant's kill switches. |
| Service Identity | An explicit identity of the tenant, such as a Service Principal, under which a legitimate system or service workflow like Support acts. It has its own authority chain and never invents or impersonates a user (Premium Policies §4.2). |
| Entitlement | The operator-controlled, server-side record that a tenant may use Premium, with feature control per Domain, capability and action type. |
| Agent | An **AI Agent**: a versioned, LLM-driven reasoning unit run by the Premium Agent Runtime. It proposes; it never executes and never decides authority. |
| Local Agent | The Windows Local Agent of PRD v1 §14 and System Architecture v1 §7. The Core documents call it "Agent"; the Premium documents always say "Local Agent". |
| Agent Instance | One running Agent: an Agent definition at a specific version, bound to one Task. |
| Task | One unit of Premium work, from its trigger to a verified result or an escalation, with exactly one owner at any time. |
| Domain | A business area with its own Agents, Tools and policy. The first is Support. |
| Domain Manager | The Agent that owns planning and quality inside one Domain. |
| Super Agent | The Agent that coordinates a Task spanning Domains. It is used only when needed, never as a mandatory hop. |
| Specialist (Temporary Agent) | A short-lived Agent created within policy, scope and budget for a bounded sub-problem. |
| Orchestrator | Deterministic code that owns Task state, dependencies, ordering, locks, version checks, budgets, retries, checkpoints and handoffs. It is not an Agent. |
| Tool | A registered, typed capability through which an Agent reads or affects anything outside itself (section 9). Every Tool call is an Action Request to the Core Action Engine. |
| Policy Engine | The single Core Policy Engine of System Architecture v1 §6. Premium adds the Premium Autonomous Action Policy to it; there is no second policy engine. |
| Premium Autonomous Action Policy | The closed, tenant-configured policy that may allow specific LOW or MEDIUM-risk CHANGE or SEND actions to run without per-action human approval (Premium Policies §2.2; decision D1). It is not the Core "Approve All" control, which Premium leaves unchanged. |
| Policy Grant | A scoped, expiring, usage-limited authorization derived from policy for one Task, Agent Instance, Tool, Resource and action (Premium Policies §4). |
| Decision | The Policy Engine's result for an action: **ALLOW**, **REQUIRE_APPROVAL** or **DENY**. |
| Human Interruption Rate | The share of Tasks that needed a human decision, approval or takeover. |
| Support Domain | Premium's reactive resolution of the tenant's own helpdesk or CRM tickets. It is not the internal support access of PRD v1 §16 and invariants 50 and 51, which Premium does not change. |
| Tenant Work Data | Work data the tenant controls, under the Core rules for tenant isolation, privacy, retention, audit, deletion and revocation. In Support it includes the tenant's end-customer information (decision D4). |
| "Premium" in PRD v1 §1 | There, "White-Label/Premium capability" describes how the whole product is offered inside a host SaaS. In the Premium documents, Premium means the autonomous-execution layer. |

## 3. Principles

1. **Core stays as frozen.** Core tenants, and Premium tenants wherever Premium does not apply, get exactly the Core
   behavior.
2. **Agents propose, the Policy Engine decides, the Action Engine executes.** Agents may reason freely. Only the
   deterministic Policy Engine decides ALLOW, REQUIRE_APPROVAL or DENY, and only the Core Action Engine executes
   (invariants 3 and 4).
3. **Fail closed.** Missing or unprovable entitlement, identity, grant, scope, state or budget means no Premium
   authority.
4. **Minimum sufficient intelligence.** The simplest execution pattern and the fewest Agents that meet the required
   quality. Routing priority: security, correctness, policy and authority, required quality or SLA, cost, minimum
   complexity. Cost never outranks safety or correctness.
5. **Verified, not claimed.** An external effect has succeeded only when an authoritative reread and a deterministic
   check confirm it.
6. **Durable, not remembered.** Orchestration state that matters lives in PostgreSQL, never only in a model context
   or in Worker memory.
7. **Bounded, measurable, auditable and, where possible, reversible.** Every autonomy has limits, a cost, a trace and
   a recovery path.
8. **Core isolation.** A Premium failure never disables Core's Fact Layer or work continuity.

## 4. What Premium reuses from Core

| Core capability | How Premium uses it | Premium never |
|---|---|---|
| Event Backbone: ingress, outbox, broker, processing, event ledger | Triggers such as a new helpdesk ticket arrive as Core events; the results of external actions come back as source events | adds a second ingestion path, or processes an event before Core has durably committed it |
| Snapshot, Source of Truth and provenance | Agents read authoritative state; verification rereads it | writes the Snapshot directly: effects reach it only as authoritative source events (invariants 13 and 15) |
| Work Memory foundations, Context Builder, AI Gateway | Minimal, task-scoped context bundles; every model call | calls a model outside the Core AI Gateway, or sends more than the task-scoped context |
| Policy Engine | Evaluates the Premium Autonomous Action Policy together with the Core rules | runs a parallel policy engine, or lets an Agent decide authority |
| Action Engine | The only execution path of every Tool call, with its lifecycle, verification and audit | executes anything outside it |
| Connectors | Tools wrap typed connector operations | holds or exposes connector credentials to Agents |
| Security and tenant isolation | Server-side tenant, forced row-level security, isolation of queues, logs and AI context | shares state across tenants |
| Verification and audit | Authoritative verification; append-only audit | treats an Agent's claim as verification, or lets any Agent modify audit |
| Local Agent infrastructure | Typed, allowlisted workspace commands | adds a shell, script, screenshot or surveillance capability |

## 5. Components

```text
Trigger: an authorized Core event (such as a helpdesk ticket) or an authenticated user request
  ↓
Entitlement check ── not provable ──> Core behavior only
  ↓
Adaptive Router ── deterministic rules first
  ↓
Orchestrator + durable Task (one owner): dependencies, order, locks, version checks, budgets, checkpoints
  ↓
Agent Runtime: Agent, Specialist, Domain Manager or Super Agent ── model calls ──> Core AI Gateway
  ↓ typed Tool request
Tool call checks: identity → entitlement → permission → scope → current state → Policy Engine → budget
  ↓ ALLOW                    ↓ REQUIRE_APPROVAL                    ↓ DENY
Core Action Engine           Core approval flow, then Action       recorded; the Agent replans
  ↓                          Engine
Executor: connector operation, or a typed Local Agent command
  ↓
Verification: authoritative reread + deterministic check → audit → durable Task update
```

| Component | Responsibility |
|---|---|
| Entitlement service | Proves a tenant's Premium entitlement and its feature control at the moment of use; fails closed |
| Adaptive Router | Chooses the execution pattern (section 7); deterministic rules first; a model may help classify, never authorize |
| Orchestrator | Deterministic workflow: Task state, ownership, dependencies, ordering, locks, version checks, budgets, retries, checkpoints, handoffs, and the issuing of Policy Grants from policy |
| Agent Runtime | Runs Agent Instances from versioned Agent definitions (role, Domain, allowed Tools, model policy, budget defaults, maximum Sub-Agent depth); every model call goes through the Core AI Gateway |
| Tool registry | Holds each Tool's contract (section 9); an incomplete contract cannot be registered |
| Policy Engine (Core, extended) | Evaluates the Premium Autonomous Action Policy with the Core rules and returns the Decision |
| Grant store | Policy Grants: scoped, expiring, usage-limited, revocable |
| Durable Task store | Task records and checkpoints in PostgreSQL, tenant-owned under forced row-level security |
| Memory service | The Premium memory layers and the Memory Proposal path (section 11) |
| Verification service | Authoritative rereads and deterministic checks, including of rollbacks |
| Kill switch service | Hierarchical kill switches, checked before every Tool call and at dispatch (section 17) |
| Cost and observability | Budgets, cost and quality metrics, and the end-to-end trace (section 16) |

## 6. The path of every Tool call

Every Tool call is an Action Request on the Core Action Engine path (System Architecture v1 §6). Premium adds checks
there; it removes none.

Before execution, in this order: **Agent request → identity → entitlement → permission → scope → current state →
policy → budget → execute.** Kill switches are checked before the first step and again at dispatch.

After execution: **validate the result against the Tool's output schema → verify the authoritative state where the
action requires it → audit → update the durable Task state.**

Authorization at Task start is never sufficient: each call is checked when it happens. The Core lifecycle applies:

| Core lifecycle state | In Premium |
|---|---|
| PROPOSED | An Agent's typed Tool request |
| CLASSIFIED | Action type and risk from the Tool's contract and policy; an Agent or a model may raise the risk, never lower it |
| POLICY_CHECKED | The Decision: ALLOW, REQUIRE_APPROVAL or DENY (DENY ends as REJECTED) |
| WAITING_APPROVAL | REQUIRE_APPROVAL: the Core human approval flow, with its binding to approver, action, target, type, payload hash, time and expiry |
| AUTHORIZED | A human approval, or an ALLOW backed by a Policy Grant bound to the same fields (Premium Policies §4) |
| DISPATCHED, EXECUTING, VERIFYING | As in Core; revocation and kill switches are rechecked at dispatch |
| SUCCEEDED, FAILED, REJECTED, EXPIRED, CANCELLED, UNKNOWN | As in Core; UNKNOWN is reconciled against the authoritative source before anything is retried |

## 7. Adaptive orchestration

There is no mandatory hierarchy. The Adaptive Router picks the lowest execution pattern that meets the required
quality:

1. deterministic code or workflow, with no model;
2. a single Agent;
3. an Agent plus a Specialist (Temporary Agent);
4. a Domain Manager with several Agents;
5. a Super Agent across several Domains.

Routing priority is security, correctness, policy and authority, required quality or SLA, cost, then minimum
complexity. Routing never grants authority and never skips policy.

- **Super Agent.** Used only when a Task spans Domains, a cross-Domain conflict must be resolved, or orchestration
  needs global consistency. It is never a mandatory hop. Domain Managers own quality inside their Domain.
- **One owner.** Every active Task has exactly one owner at any time: an Agent Instance, a Domain Manager, the Super
  Agent or a human. A change of owner is a handoff (section 8).
- **Peers.** Agents may exchange information where policy allows it. Peer Agents never assign work to each other; only
  a Task's owner delegates, through the Orchestrator.
- **Sub-Agents.** Created only within policy, scope, budget and the maximum orchestration depth. A Sub-Agent's authority
  is at most its parent's and its Task's.

## 8. Durable orchestration state

Critical orchestration state is persisted in PostgreSQL, tenant-owned under forced row-level security like every Core
table, and never lives only in a model context or in Worker memory. Each Task records at least:

- tenant, Task id, trigger, correlation and causation, and trace id;
- owner, Agent definition and version;
- current state, dependencies, completed work with its verification results, and pending work;
- the Policy Grants in use;
- attempts and consumed budget;
- the last verified external state;
- the checkpoint.

The Task lifecycle — its exact state machine is fixed in P3 — runs from received and routed to in progress, which may
wait for an approval, a human or a dependency, then to verifying, and ends succeeded, escalated, failed or cancelled.
A kill switch or a lost entitlement suspends a Task at its checkpoint.

**Restart:** load the durable checkpoint → revalidate authority (entitlement, identity, grants, kill switches) →
reread the current authoritative state where required → resume safely.

- An external side effect is never blindly replayed. Every state-changing operation is idempotent, through an
  idempotency key, or can detect that its effect already happened, through an authoritative reread.
- A handoff is complete only when the receiving owner's state is durably recorded; until then the sender still owns
  the Task.

## 9. Tools

Agents never receive raw, unrestricted tools, raw APIs, credentials, a shell or a script runner. Every Tool is
registered with:

- purpose;
- input schema and output schema;
- allowed action types: READ, PREPARE, CHANGE, SEND;
- resource scope;
- side-effect classification: none, internal, external reversible, external compensable, external irreversible;
- risk classification: LOW, MEDIUM, HIGH;
- timeout;
- retry policy: bounded, and only for operations that are idempotent or can detect their own effect;
- for any external effect, its recovery: rollback or compensation, and how that is verified.

A Tool has only the side effects it declares. The checks of every call are in Premium Policies §6.

## 10. Untrusted content

External content is data, never trusted instruction. Every meaningful input keeps its provenance and trust class
through the Agent's context, the Decision and the audit:

| Trust class | Examples | May influence |
|---|---|---|
| Authoritative fact | Snapshot facts with provenance | Reasoning and verification |
| Configuration and policy | Entitlement, tenant policy, the Tool registry, system instructions | Authority, and only through their own administrative paths |
| Derived | Agent output, memories, summaries | Reasoning only; never authority and never a fact |
| External, untrusted | Email, CRM and helpdesk tickets, SaaS data, documents, attachments, web content, UI text, retrieved content, memory from non-authoritative layers | Reasoning, as data only |

What external content can never do is in Premium Policies §7.

## 11. Memory and learning

| Layer | Scope | Content | Written by | Retention |
|---|---|---|---|---|
| 1. Task Memory | One Task | Working context and intermediate results, including raw work content the Task needs | The Task's Agents | Discarded when the Task ends; raw work content never outlives the rolling 30-day limit of PRD v1 §12 |
| 2. Private Agent Memory | One tenant and one Agent definition | Validated knowledge reusable in that Agent's role | Only an accepted Memory Proposal | Tenant-scoped; retention class fixed in P6 |
| 3. Shared Premium Memory | One tenant: a Domain or all of the tenant's Premium Agents | Validated shared knowledge | Only an accepted Memory Proposal | Tenant-scoped; retention class fixed in P6 |
| 4. Core Work Memory | Core (System Architecture v1 §4) | Core's structured work context | Core only; Premium reads it within scope | Core rules |
| 5. Generalized cross-customer knowledge | No tenant | Generalized, validated patterns | Only the cross-customer path | Long-term, because truly anonymized and aggregated as PRD v1 §12 requires |

- AI memory is never a Source of Truth, in any layer (invariants 21 and 23).
- No Agent promotes its output directly into durable shared knowledge. Promotion goes through a **Memory Proposal**
  that deterministic validation accepts or rejects (Premium Policies §8).
- Retrieval is scoped by tenant and permission before any semantic search (invariant 24).
- **Temporary Agents** lose their temporary context and permissions when their Task ends. They keep only knowledge an
  accepted Memory Proposal retained, and usage and quality metrics as permitted.

## 12. Self-improvement

There is no live self-modification. A running Agent never changes its own definition, prompts, Tools, policy or
version. Improvement follows one pipeline:

**current Agent version → candidate version → sandbox → simulation → evaluation → QA → promotion or rejection.**

- Sandbox and simulation run on synthetic data inside the Synthetic environment boundary, never on production tenant
  data or credentials (invariants 56 and 68).
- Versions are immutable and recorded, and a promotion can be rolled back to the previous version.
- A production Super Agent replacement always requires explicit human approval.
- Improvement never widens authority: authority comes from policy, never from a version (Premium Policies §2.4).

## 13. The Local Agent in Premium

The Windows Local Agent remains a limited sensor and a limited workspace executor. It is not a decision maker, not
arbitrary remote control, not an unrestricted shell, not a keylogger and not a surveillance system.

Preferred hierarchy for obtaining information or acting, highest first:

1. official API or Connector;
2. structured system state;
3. documented protocol or deep link;
4. OS or application metadata;
5. structured UI, DOM or accessibility tree;
6. justified, allowlisted UI automation.

Levels 1, 3, 4 and 6 are the hierarchy of System Architecture v1 §7, where UI automation is already a limited
fallback. Levels 2 and 5 are Premium extensions, approved for Premium v1: level 2, structured system state, by
clarification C1, as a separate preferred level above structured UI; level 5, structured UI state and DOM or
accessibility reading where applicable, and the fixed, allowlisted UI automation of level 6, by decision D3. Each is off
by default, policy-checked and scoped, and none expands Local Agent authority beyond the D3 constraints. Screenshots
are never a default state source: Premium v1 defines no screenshot, screen-recording, keylogging, clipboard-harvesting,
arbitrary content-scanning or unrestricted shell or script capability, and no direct send or delete operation.

Agents never talk to the Local Agent. Every Local Agent action is a typed, allowlisted command dispatched by the Core
Action Engine after a Decision, and business mutations continue to prefer official Connectors and APIs. The rules,
including what the Local Agent may never do in Premium, are in Premium Policies §10.

## 14. The Support Domain

Support is the first Premium Domain (decision D4). It is reactive only: it starts when the tenant's helpdesk or CRM
raises an authorized request, and it never discovers issues proactively.

```text
Authorized ticket
→ policy
→ Adaptive Router
→ diagnosis
→ a single Support Agent, or coordinated Support Agents
→ temporary or reversible fix
→ authoritative verification
→ resolved, or escalated
```

- A multi-system incident uses one Agent per external system where practical, sharing a Shared Incident Context scoped
  to one tenant and one incident. The deterministic Orchestrator owns dependencies, order, locks and version checks;
  the Agents understand the shared logical context but never overwrite each other's state blindly.
- Missing critical data goes to a Temporary Specialist first and then, if still unresolved, to a human service
  representative of the tenant. No Agent questions the end customer directly.
- The customer-facing result is short — the problem is solved and what was fixed — and is posted only after
  verification. Support does not manage the full CRM ticket lifecycle, and every triggered ticket is handled
  independently.
- The Support Domain acts for the tenant under an explicit Service Identity of the tenant. End-customer information it
  processes is Tenant Work Data, under tenant isolation, Minimum Necessary Data and the Core privacy, retention,
  audit, deletion and revocation rules. It is not internal operator or support access, which invariants 50 and 51
  govern unchanged, and it gives no one outside the tenant access to tenant data.

The full rules are in Premium Policies §11.

## 15. Verification and recovery

Agent self-QA is useful but never enough for a meaningful external action. Where applicable:

**execute → authoritative reread → deterministic verification → SUCCEEDED.** An Agent saying it succeeded is not a
verified success.

Failure path: **bounded retry → alternative strategy → Temporary Specialist → Domain Manager → Super Agent or human**,
according to scope and risk. Rollback, where possible, is itself verified the same way. UNKNOWN is reconciled before
anything is retried.

## 16. Cost and observability

Autonomy is always measurable, bounded, auditable and, where possible, reversible. Premium tracks:

- cost per Task, per Agent and per model or provider;
- token usage, Tool usage, retries, Sub-Agents and time;
- budget variance;
- success and failure, escalations and rollbacks;
- the Human Interruption Rate.

One trace connects **trigger → Router → Agents → policy → Tools → execution → verification → result**, extending the
correlation of System Architecture v1 §10. Telemetry carries ids, codes, counts, durations and costs; it never stores
unnecessary raw work content, secrets or sensitive data (Premium Policies §15).

## 17. Kill switches

Hierarchical kill switches stop Premium execution for one **Agent**, one **Task**, one **User**, one **Domain**, one
**Tenant**, or **all Premium execution**. The Core execution-layer kill switch of invariant 42 stays independent and
stops Premium execution too, because Premium executes only through the Core Action Engine.

A kill revokes or deactivates the dependent temporary grants and stops downstream work safely at its checkpoints. The
rules are in Premium Policies §13.

## 18. Core failure isolation

A Premium failure never disables Core's Fact Layer or its work-continuity behavior (invariants 9 and 29). If the Agent
Runtime, a model provider or orchestration is unavailable, Premium Tasks pause at their checkpoints, and Core keeps its
safe fact, Snapshot and work-continuity behavior, including Safe Mode where Core itself needs it. Premium consumes Core
events only after Core has committed them and never delays Core's own processing. The rules are in Premium Policies §16.

## 19. Deployment placement

Fully stays an event-driven Modular Monolith (invariant 60). Agents are units of work inside the product, not
microservices. Premium modules live in the existing solution with boundaries enforced by the architecture tests
(invariant 72), run in the existing Web/API and Background Worker deployables, and use Container Apps Jobs for sandbox
and simulation work (System Architecture v1 §9). Premium uses the existing logical queues first (`ai-jobs` and
`action-jobs` beside `event-processing`); a new queue, deployable or datastore is decided in the Premium Gate that
needs it, with product-owner approval. No Kubernetes (invariant 65), and PostgreSQL stays the primary operational store
(invariant 61).

## 20. Constraints on Core Gate 3

Premium P0 is a **planning prerequisite** to Core Gate 3, not a technical dependency of the Event Backbone. Gate 3
builds the monday connector — real authentication, webhooks, source mapping, API reconciliation and initial sync —
and implements none of Premium. Its connector contract must nevertheless take a shape that Premium can extend without
rebuilding it:

1. **Credentials.** Tokens and secrets are held only by the connector layer, in Key Vault or behind managed identity,
   bound to the tenant and the connection. They never reach AI, prompts, Agents or logs.
2. **Identity.** Every provider call records whose authority it uses — the connection's own Service Identity or a
   named user's delegation — so the Premium delegation chain (tenant → user or Service Identity → Task → Agent
   Instance → Policy Grant → Tool → Resource) can extend it without a contract change.
3. **Typed operations.** Each connector operation is declared with purpose, input and output schema, action type,
   risk, resource scope, side-effect and recovery classification, timeout and retry policy, even where Gate 3 only
   reads.
4. **Authoritative reread.** Reconciliation exposes a per-object read of current authoritative state, with the
   provider's revision where one exists, usable later by verification.
5. **Writes.** Gate 3 needs none. When writes arrive, they go through the Core Action Engine only, carry an idempotency
   key or can detect an already-applied effect, and use the provider's version or concurrency checks where available.
6. **Untrusted content.** Provider text is data: it is normalized with provenance and a trust classification and can
   never change policy, permissions or instructions.
7. **Revocation.** Disabling a connection or revoking a token takes effect at the next call, at access time, not only
   at ingestion.

These constrain how Gate 3 shapes its connector contract; they add no scope to its acceptance criterion in Development
Gates v1. Gate 3's own Definition of Done is written when the product owner authorizes it.

## 21. Decisions

The product owner approved decisions D1 to D4 (D2 with a clarification) and clarification C1 on 2026-09-29, and closed
Premium Gate P0 as PASS the same day. Later Premium Gates need decisions L1 to L4. All of them are recorded, with the
Core rules they affect, in `docs/gates/gate-P0-report.md`.
