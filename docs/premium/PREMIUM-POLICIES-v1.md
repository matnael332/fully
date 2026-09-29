# Premium Policies v1

Status: **FROZEN — Premium Gate P0 (Architecture Freeze) PASS, closed by the product owner on 2026-09-29.** These
rules take effect for a tenant only when the capability they govern has passed its Premium Gate; until then every
tenant is a Core tenant. A change to them follows §17.

These are the normative rules of Fully Premium. `docs/premium/FULLY-PREMIUM-ARCHITECTURE-v1.md` describes the
components they govern and defines the terms used here; `docs/gates/gate-P0-report.md` classifies each rule against the
frozen Core documents. "Must", "never" and "only" are binding.

## 1. Precedence

1. The frozen Core documents in `docs/canonical/` govern every tenant. For a Core tenant, and for a Premium tenant
   wherever no Premium rule applies, behavior is exactly as PRD v1, the Architecture Invariants v1, System Architecture
   v1 and Development Gates v1 state.
2. A Premium rule may differ from a Core rule only where the Gate P0 report lists it as an intentional Premium policy
   difference and the product owner has approved it (PRD v1 §23). Every other Premium rule adds to Core and never
   weakens it.
3. Where a Premium rule and a Core rule seem to conflict and no approved difference covers the conflict, the Core rule
   wins and the conflict is reported.
4. Where a rule cannot be evaluated — missing data, an unreachable dependency, an unknown version — the outcome is the
   safer one: DENY, or Core behavior.

## 2. Action authority

### 2.1 Core rules, unchanged

Every action has an action type — READ, PREPARE, CHANGE or SEND — and a risk level — LOW, MEDIUM or HIGH — exactly as
in PRD v1 §7:

- safe READ may be automatic, and safe PREPARE may be automatic;
- CHANGE requires explicit human approval, and SEND requires explicit human approval;
- HIGH risk always requires explicit human approval;
- the product defines safe defaults, a customer may make them stricter, and AI may only raise a risk, never lower a
  rule- or policy-defined one;
- "Approve All" approves only the closed set of actions currently displayed, never a wildcard.

These rules apply to every Core tenant, and to a Premium tenant for every action that §2.2 does not cover. The Premium
Autonomous Action Policy is not an "Approve All": it is a separate mechanism, and Approve All keeps exactly this meaning
in Premium, including for batches of REQUIRE_APPROVAL actions.

### 2.2 The Premium Autonomous Action Policy

This is an **intentional Premium policy difference**: a separate, explicit extension for Premium tenants, not a
modification of PRD v1. The product owner approved it on 2026-09-29 as decision D1 in the Gate P0 report; it differs
from PRD v1 §7 and §9 and from the Core approval flow of System Architecture v1 §6.

For a verified Premium tenant — its entitlement proven and autonomy enabled for the Domain, capability and action type
(§3) — a LOW or MEDIUM-risk CHANGE or SEND action may execute without per-action human approval only when all of the
following are true:

1. the action type, with this Tool and for this kind of Task, is part of the tenant's closed policy, preconfigured by
   an authorized Tenant Admin;
2. the target and Resource scope are explicit;
3. the current delegated authority is valid: the identity and its delegation (§4.2), and a Policy Grant that covers
   the exact identity, Task, Agent Instance, Tool, Resource and action, has not expired and has usage left (§4.3,
   §4.4);
4. the Policy Engine returns ALLOW at execution time, which it never does under an active kill switch (§13);
5. the current state is revalidated where required: the authoritative state still matches the state the action was
   planned against;
6. the budget and resource limits are valid (§5, §14);
7. the Tool guardrails pass (§6);
8. authoritative verification follows execution (§12).

**HIGH-risk actions always require human approval.** Core tenant behavior is unchanged, and so is every action of a
Premium tenant that this policy does not cover (§2.1). The freedom boundaries of §5, including the Recovery boundary,
bind every autonomous action too.

The policy is a closed list of explicitly enumerated action types, Tools and Resource scopes, each with limits. For
SEND, it can constrain — and each SEND grant carries — the allowed recipients or recipient domains, the channels, the
content or action categories and any other relevant scope; it never grants unrestricted external messaging authority.
Wildcards such as "any action", "any resource", "any recipient" or "everything this user can do" are never valid, and
there is no "Fully can do everything" authorization.

### 2.3 Decisions

Every meaningful action resolves to exactly one Decision, made by the Core Policy Engine and never by an Agent, a model
or the Router:

- **ALLOW** — execute now; for CHANGE or SEND only as §2.2 allows;
- **REQUIRE_APPROVAL** — wait for explicit human approval through the Core approval flow (System Architecture v1 §6);
- **DENY** — do not execute; record the reason; the Agent may replan within its boundaries or escalate.

| Action | Core tenant | Premium tenant, no applicable grant | Premium tenant, applicable grant and every check passing |
|---|---|---|---|
| Safe READ or PREPARE within scope | May be automatic | ALLOW | ALLOW |
| CHANGE, LOW or MEDIUM risk | Requires approval | REQUIRE_APPROVAL | ALLOW |
| SEND, LOW or MEDIUM risk | Requires approval | REQUIRE_APPROVAL | ALLOW |
| Any HIGH-risk action | Always requires approval | REQUIRE_APPROVAL | REQUIRE_APPROVAL |
| Outside the tenant, entitlement, scope, identity or budget, or under an active kill switch | Not applicable: no Premium action exists | DENY | DENY |

A tenant may make all of this stricter. For example, it may enable Support Agents while keeping automatic external
SEND disabled; a SEND then resolves to REQUIRE_APPROVAL.

### 2.4 Learning never grants authority

Premium autonomy comes only from the explicitly configured policy of §2.2, never from learning. Learned personal
automation stays exactly as PRD v1 §9 defines it: only read, prepare, open and organize actions may ever become
automatic through learning; writes, changes and sends always need approval even if learned; and for the Human MVP,
advanced learned auto-execution stays feature-flagged OFF (invariant 79). Learning, evaluation results, memory or a
promoted Agent version may at most **propose** a policy change to a Tenant Admin. They never create, widen or renew a
grant.

## 3. Entitlement and feature control

- **System of record.** Entitlement is a server-side record under operator control. A tenant cannot grant it to itself
  and no Agent or model can set it. It is versioned and checked at the moment of use, not only at Task start.
- **Fail closed.** If the system cannot prove valid entitlement for the capability at hand — the record is missing,
  expired, unreadable or inconsistent, or its store is unreachable — the tenant gets Core-safe permissions only: no
  Premium Agent starts or continues, in-flight Tasks are suspended at their checkpoints and escalated to a human, and
  Core behavior is unaffected.
- **Independent feature control** at four levels: **Tenant → Domain → Capability → Action type.** Each level can only
  narrow the one above it. For example, a Premium tenant may enable Support Agents while keeping automatic external
  SEND disabled, or enable a capability for PREPARE only.
- Enabling a capability never enables autonomy for CHANGE or SEND; that needs the explicit policy of §2.2.

## 4. Delegated identity and Policy Grants

### 4.1 The delegation chain

Every external action is traceable through **Tenant → user or Service Identity → Task → Agent Instance → Policy Grant
→ Tool → Resource**, and the chain is recorded in its audit record. An Agent never acts as an independent authority: it
always acts for a named user or an explicit Service Identity of its tenant.

### 4.2 Whose identity

Decision D2, approved with a clarification on 2026-09-29:

- **In a user's name.** By default, an action performed in a user's name needs both the tenant's policy and that
  user's own revocable delegation for the action class. Revoking the delegation takes effect at the next access or
  execution (§4.4). Relaxing this default, for example to the tenant's policy alone, would need a new product-owner
  decision.
- **Service workflows.** A legitimate system or service workflow of the tenant, such as Support, runs under an explicit
  Service Identity (a Service Principal) with its own authority chain, governed by the tenant's policy. It never
  invents or impersonates a user identity.
- **Traceability.** Every action stays traceable through its delegated-authority chain (§4.1), whichever identity it
  uses.

### 4.3 Policy Grants

A Policy Grant names the tenant, the identity (a user or a Service Identity), the Task, the Agent Instance, the Tool,
the Resource or bounded Resource set, the action type, the payload bounds the policy sets (for SEND, including the
recipient, channel and content-category bounds of §2.2), its expiry, its usage limit, and the policy and version it
was issued from.

- Only the Orchestrator issues grants, only from the tenant's configured policy, and only for one Task.
- An Agent never grants authority to itself or to another Agent.
- A Sub-Agent's grants are a subset of its parent's and of its Task's, never more.

### 4.4 Revocation

Revoking a policy, a grant, an entitlement, an identity's permission or a connection takes effect at the next access
or execution: every Tool call rechecks it, and an action not yet dispatched is rechecked at dispatch. Expiry and usage
limits are enforced the same way. A revoked grant is never used again, even by a Task resumed from a checkpoint.

### 4.5 Binding

A human approval keeps its Core binding: approver, action id, target, type, payload hash, time and expiry. A material
payload change invalidates it (invariant 36). An ALLOW under a Policy Grant is bound the same way: a payload outside
the grant's bounds needs a new Decision.

## 5. Freedom boundaries

Agents may reason freely. What they can do is bounded by five boundaries, each enforced by deterministic code:

1. **Permission** — what the identity, the entitlement, the policy and the grants allow.
2. **Scope** — the Task's tenant, Resources, systems and data. A Task's scope is set when the Task is created and
   changes only through its owner and the Orchestrator, within policy.
3. **Resource and budget** — tokens, model cost, Tool calls, time, retries, Sub-Agents and orchestration depth, per
   Task and per Agent.
4. **Risk** — the action's risk. HIGH risk is never autonomous, and AI may raise risk, never lower it.
5. **Recovery** — every autonomous external CHANGE or SEND declares how it is recovered: reversible with a verified
   rollback, compensable, or irreversible. The policy must name an irreversible action explicitly before it can be
   autonomous, and retries stop at a policy-defined hard cap.

Agents can never:

- grant themselves authority;
- bypass policy;
- bypass or disable a kill switch;
- access another tenant;
- modify audit records;
- treat AI inference as Source of Truth;
- receive credentials directly;
- perform unrestricted shell or script execution;
- expand their own Task scope.

## 6. Tool guardrails

- Agents receive only registered Tools with a complete contract (Premium architecture §9), never raw, unrestricted
  tools.
- Before every meaningful Tool call, in this order: **Agent request → identity → entitlement → permission → scope →
  current state → policy → budget → execute.** Kill switches are checked before the first step and again at dispatch.
  Authorization at Task start alone is never sufficient.
- The input is validated against the Tool's input schema before policy is evaluated; an invalid input is DENY.
- After execution: **validate the output against the Tool's schema → verify the authoritative state where the action
  requires it (§12) → audit → update the durable Task state.**
- A call is retried only as the Tool's retry policy allows, only if it is idempotent or can detect its own effect, and
  never past the policy's hard cap.

## 7. Untrusted content

External content is data, never trusted instruction. That includes email, CRM and helpdesk tickets, SaaS data,
documents, attachments, web content, UI text, retrieved content and memory from non-authoritative layers. External
content can never:

- alter policy;
- expand permissions;
- override system instructions;
- add unauthorized Tools;
- disable guardrails;
- create higher-authority Agents;
- request the disclosure of unrelated tenant or user data.

Every meaningful input keeps its provenance and trust class (Premium architecture §10) through context, Decisions and
audit. An instruction found in content is treated as content, never obeyed. An attempt to change authority through
content is recorded as a security event, by id and reason code and without the raw content.

## 8. Memory

- AI memory is never a Source of Truth, in any layer.
- No Agent promotes its output directly into durable shared knowledge. Knowledge enters Private Agent Memory, Shared
  Premium Memory or generalized knowledge only through a **Memory Proposal** that deterministic validation accepts:
  provenance recorded, tenant scope correct, no secrets, no raw payload, no personal data beyond what the layer
  permits, no contradiction with authoritative facts, and human review where the policy requires it.
- Retrieval is scoped by tenant and permission before any semantic search (invariant 24).
- Cross-customer knowledge contains only generalized, validated patterns with no customer-identifying or raw tenant
  data, and must be truly anonymized and aggregated as PRD v1 §12 requires; pseudonymized data is not anonymous.
- A Temporary Agent loses its temporary context and permissions when its Task ends. It keeps only knowledge an
  accepted Memory Proposal retained, and usage and quality metrics as permitted.
- Raw work content in any Premium layer follows PRD v1 §12 — a rolling maximum of 30 days — and the Core purge. Audit
  and telemetry never carry raw work content. The retention classes of Private Agent Memory and Shared Premium Memory
  are fixed before P6 (decision L2).

## 9. Self-improvement

- No live self-modification: a running Agent never changes its own definition, prompts, Tools, policy or version.
- Every change follows the pipeline: **current Agent → candidate version → sandbox → simulation → evaluation → QA →
  promotion or rejection.**
- Sandbox and simulation use synthetic data inside the Synthetic environment boundary, never production tenant data or
  credentials (invariants 56 and 68).
- A production Super Agent replacement always requires explicit human approval.
- Every version is immutable and recorded, and a promotion can be rolled back to the previous version.
- Improvement never widens authority (§2.4).

## 10. Local Agent

- The Local Agent stays a limited sensor and a limited workspace executor (PRD v1 §14): no decisions, no arbitrary
  remote control, no unrestricted shell, no keylogging, no surveillance.
- Agents never communicate with the Local Agent (invariant 31). Every Local Agent action is a typed, allowlisted command
  (invariant 32) dispatched by the Core Action Engine after a Decision, to a Local Agent version that declares the
  capability (invariant 43), and rechecked by its Local Policy Guard (invariant 34). It runs in the user's session
  without SYSTEM privileges (invariant 66).
- The information and action hierarchy of Premium architecture §13 is mandatory. UI automation is used only when no
  higher level can do the job, and only as a fixed, allowlisted command.
- In Premium v1 the Local Agent never sends messages, deletes data, closes unsaved content or decides priority, and it
  has no direct send or delete operation. Business mutations continue to prefer official Connectors and APIs: where a
  cloud API connector is available, the Local Agent never mutates business data (PRD v1 §14, invariant 37).
- **Decision D3 and clarification C1**, approved for Premium v1 on 2026-09-29, allow exactly these sources and
  mechanisms beyond Core: structured system state, a separate preferred level above structured UI (C1); structured UI
  state and DOM or accessibility reading where applicable; and fixed, allowlisted UI automation. Each is off by
  default, enabled per tenant and capability, checked by the Policy Engine for every use, and scoped to the Task, UI
  reading and automation to the Task's application and window. None expands Local Agent authority beyond the D3
  constraints.
- Premium v1 has no screenshot capability — screenshots are never a default state source — and no screen recording,
  keylogging, clipboard harvesting, arbitrary content scanning, or unrestricted shell or script execution (invariants
  33 and 57).
- Offline, the Local Agent receives no new authority (invariant 41). A Premium command that expires before it runs is
  authorized again, never replayed.

## 11. Support Domain

Support is the first Premium Domain, by decision D4, approved on 2026-09-29. Its rules:

- **Reactive only.** A Support Task starts only from an authorized helpdesk or CRM request of the tenant. Support never
  discovers issues proactively.
- **Tenant Work Data.** End-customer information the Support Domain processes is Tenant Work Data. It remains subject
  to tenant isolation, Minimum Necessary Data, the Core privacy controls, the Core retention framework, the audit
  rules, and the deletion and revocation requirements.
- **Minimum Necessary Data**, and **Cheapest Reliable Source First**.
- **One Agent per external system** where practical. A multi-system incident uses a Shared Incident Context scoped to
  one tenant and one incident, holding only what the incident needs.
- **Deterministic orchestration** owns dependencies, order, locks and version checks. Agents share the logical context
  but never overwrite each other's state blindly.
- **No direct questioning of the end customer** by an Agent. Missing critical data goes to a Temporary Specialist
  first and then, if still unresolved, to a human service representative of the tenant.
- **Temporary or reversible fixes only**, each with a verified rollback. Permanent configuration or infrastructure
  changes are outside Support's authority and are escalated.
- **Identity.** The requester's identity comes from the helpdesk's authoritative record, never from AI matching and
  never from an email address alone (invariants 19 and 47). An ambiguous match is escalated.
- **Result.** After verification, the customer-facing result is short: the problem is solved and what was fixed.
- Support does not manage the full CRM ticket lifecycle, and each triggered ticket is handled independently.
- **Bounded retries** with a policy-defined hard cap, then the failure path of §12.
- The Support Domain acts for the tenant under an explicit Service Identity of the tenant (§4.2). It is not the internal
  operator or support access that invariants 50 and 51 govern, which stays unchanged, and it gives no one outside the
  tenant access to tenant data.

## 12. Verification and recovery

- Agent self-QA is useful but never enough for a meaningful external action.
- Where applicable: **execute → authoritative reread → deterministic verification → SUCCEEDED.** An Agent saying it
  succeeded is not a verified success (invariants 15 and 40).
- UNKNOWN is reconciled against the authoritative source before anything is retried.
- Failure path: **bounded retry → alternative strategy → Temporary Specialist → Domain Manager → Super Agent or human**,
  according to scope and risk.
- Rollback, where possible, is itself verified the same way.
- A result shown to a user or a customer states only verified facts.

## 13. Kill switches

- Levels: **Agent, Task, User, Domain, Tenant, and all Premium execution.** The Core execution-layer kill switch
  (invariant 42) stays independent and stops Premium execution too.
- Kill switches are checked before every Tool call and at dispatch. Activating one stops new calls immediately;
  cancels queued and not-yet-dispatched actions; revokes or deactivates the dependent temporary grants; and stops
  downstream Sub-Agents and dependent Tasks safely at their checkpoints. An already-dispatched effect is verified, never
  abandoned in an unknown state.
- Only an authorized human activates or releases a switch: a Tenant Admin for the tenant's switches, the operator for
  all Premium execution. Releasing a switch never resumes work automatically; each suspended Task revalidates its
  authority and state first. No Agent can activate, release or bypass a switch.
- Every activation and release is audited.

## 14. Cost and budgets

- Budgets bound tokens, model cost, Tool calls, time, retries, Sub-Agents and depth per Task, and per Agent, Domain and
  tenant. Exceeding a budget stops or escalates the Task; it never overspends silently.
- Cost is optimized only after security, correctness, policy and authority, and the required quality or SLA.
- Premium tracks cost per Task, per Agent and per model or provider; token and Tool usage; retries; Sub-Agents; time;
  budget variance; success and failure; escalations; rollbacks; and the Human Interruption Rate.

## 15. Audit and telemetry

- Audit is append-only. No Agent can modify or delete an audit record.
- The audit record of every external action holds its delegation chain (§4.1), its Decision with the reasons, the
  policy and grant versions, the payload hash, the verification result and the trace id — never raw work content,
  secrets or credentials.
- One trace connects **trigger → Router → Agents → policy → Tools → execution → verification → result**.
- Telemetry carries ids, codes, counts, durations and costs. It never carries raw work content, prompts containing work
  content, secrets or unnecessary sensitive data: security logs must not become shadow storage of raw work content
  (PRD v1 §17).
- Autonomous actions are visible to the affected user and the Tenant Admin. Per-user metrics reach managers only in
  aggregate by default (invariant 49).

## 16. Core failure isolation

- A Premium failure never disables Core's Fact Layer or work-continuity behavior (invariants 9 and 29).
- Premium never runs inside Core's event-processing transactions and never delays their commit; it consumes Core
  events only after Core committed them.
- If the Agent Runtime, a model provider or orchestration is unavailable, Premium Tasks pause at their checkpoints and
  Core continues, including Safe Mode where Core itself needs it.
- Premium load is bounded — by budgets, concurrency limits, and separate queues or quotas where needed — so that it
  cannot starve Core.

## 17. Changing these policies

P0 closed on 2026-09-29. A change to these rules needs the product owner's explicit approval, and a change that
touches a Core rule, or an intentional Premium policy difference, needs a new contradiction audit first.
