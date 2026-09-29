# Premium Gate P0 Report — Architecture Freeze

## Status

**Premium Gate P0 (Architecture Freeze): PASS — architecture frozen; closed by the product owner on 2026-09-29.**
Decisions D1 to D4 and clarification C1 are approved, and the audit was corrected and rerun with no unresolved
contradiction (see *Decision*).

- Core Gate 1 (Foundation) and Core Gate 2 (Event Backbone): PASS, closed.
- Core Gate 3 (monday Connector): **NEXT / NOT STARTED.** It may begin only after this P0 closure is committed, and
  only on the product owner's explicit authorization.
- Premium P1 to P11: planned; **NOT STARTED**; not authorized.

## Scope and Definition of Done

P0 freezes the architecture of Fully Premium before Core Gate 3 starts, so that Gate 3's connector design can respect
the final Premium contracts. It is documentation only.

1. The Premium architecture, its normative policies and its Gate plan are documented in `docs/premium/`,
   understandable without any other context.
2. Every material overlap with PRD v1, System Architecture v1, all 83 Architecture Invariants v1 and Development Gates
   v1 is classified.
3. Every intentional difference names the Core rule it differs from, and every decision the product owner must take is
   listed with the rules it affects.
4. The relationship between P0 and Core Gate 3 is recorded.
5. The status and handoff documents state the truth: P0's review and closure state, P1 to P11 planned and not
   started, and Core Gate 3 not started before the P0 closure is committed.
6. No frozen Core document, source file, test, migration, workflow, IaC or runtime configuration changes; nothing
   touches Azure; nothing is committed or pushed without the product owner's explicit approval.

## Deliverables

| File | Content |
|---|---|
| `docs/premium/FULLY-PREMIUM-ARCHITECTURE-v1.md` | Product model, terminology, principles, reuse of Core, components, the Tool call path, adaptive orchestration, durable state, Tools, untrusted content, memory, self-improvement, the Local Agent, the Support Domain, verification and recovery, cost and observability, kill switches, Core isolation, deployment placement, the constraints on Core Gate 3 |
| `docs/premium/PREMIUM-POLICIES-v1.md` | The normative rules: precedence, action authority with the Premium Autonomous Action Policy and the ALLOW / REQUIRE_APPROVAL / DENY Decisions, entitlement and feature control, delegated identity and Policy Grants, freedom boundaries, Tool guardrails, untrusted content, memory, self-improvement, Local Agent, Support, verification and recovery, kill switches, cost, audit and telemetry, Core isolation |
| `docs/premium/PREMIUM-DEVELOPMENT-GATES-v1.md` | P0 to P11 with their workflow, Core prerequisites, indicative acceptance and status, and the relationship to the Core Gates |
| `docs/gates/gate-P0-report.md` | This report: the Definition of Done, the contradiction audit and the product-owner decisions |

The status and handoff documents `CLAUDE.md`, `README.md`, `INIT.md` and `docs/canonical/PROJECT-STATUS.md` record P0's
state. The report sits in `docs/gates/` with every other Gate report; the Premium documents themselves have their own
folder, beside the frozen Core set in `docs/canonical/`.

## Contradiction audit

### Method

Every element of the Premium architecture and policies was compared with every section of PRD v1 and System
Architecture v1, each of the 83 Architecture Invariants v1, and Development Gates v1. Each material overlap is
classified:

- **COMPATIBLE** — Premium follows the Core rule as written.
- **PREMIUM EXTENSION** — Premium adds capability the Core documents do not describe, within their rules. Where the
  extension changes product scope or privacy, PRD v1 §23 still requires the product owner's approval, and the decision
  is listed.
- **INTENTIONAL PREMIUM POLICY DIFFERENCE** — for tenants with proven Premium entitlement only, Premium deliberately
  departs from a Core rule. It is not in force until the product owner approves it (PRD v1 §23).
- **UNRESOLVED CONTRADICTION / BLOCKER** — a conflict the stated Premium intent does not resolve.

In the tables below, "Policies" means Premium Policies v1 (`docs/premium/PREMIUM-POLICIES-v1.md`) and "Premium
architecture" means Fully Premium Architecture v1 (`docs/premium/FULLY-PREMIUM-ARCHITECTURE-v1.md`). The Core
documents are always named in full.

### Result

- **UNRESOLVED CONTRADICTION / BLOCKER: none.**
- **INTENTIONAL PREMIUM POLICY DIFFERENCE: one** — the Premium Autonomous Action Policy, which the product owner
  approved as decision D1 on 2026-09-29. It departs from PRD v1 §7 ("Any external write, change, or send requires
  explicit approval"; a customer may only make the defaults stricter) and §9 (only read, prepare, open and organize
  actions may ever become fully automatic), and from the Core action and approval flow of System Architecture v1 §6
  (the Approval Check, with approvals bound to the employee). It contradicts no Architecture Invariant: invariant 35
  governs the Core "Approve All" control, which Premium leaves unchanged (see the audit correction below).
- **PREMIUM EXTENSIONS decided under PRD v1 §23: three**, all approved on 2026-09-29: the identity behind Premium
  actions (D2), the Local Agent extensions for Premium v1 (D3), and Support as the first Premium Domain (D4).
- Everything else is COMPATIBLE, or a PREMIUM EXTENSION within the Core rules. No frozen document changed.

### Audit correction and rerun (2026-09-29)

The first version of this audit classified invariant 35, "Approve All approves only a closed action set", as an
INTENTIONAL PREMIUM POLICY DIFFERENCE. The owner review questioned that, and re-examination found no concrete semantic
contradiction. Invariant 35 and PRD v1 §7 govern the Core "Approve All" control: it approves only the closed set of
currently displayed actions and is never a wildcard authorization. The Premium Autonomous Action Policy is a different
mechanism: a closed policy that an authorized Tenant Admin configures in advance, which the Policy Engine evaluates for
each action at execution time. Premium leaves Approve All unchanged: a human approving a batch of REQUIRE_APPROVAL
actions still approves only that displayed set. Invariant 35 is therefore COMPATIBLE, and D1 differs from PRD v1 §7
and §9 and from System Architecture v1 §6 only.

The full audit was then rerun against all four Core documents with decisions D1 to D4 applied. It changed:

1. invariant 35 to COMPATIBLE, and moved PRD v1 §7's "Approve All" rule from the D1 row to a COMPATIBLE row of its own;
2. PRD v1 §16 and System Architecture v1 §8 to PREMIUM EXTENSION: delegated user authority and explicit Service
   Identities (D2) are identity capabilities the Core documents do not describe, which the first version named only in
   its summary, and the PRD v1 §16 row now also records the end-customer information of D4;
3. the PRD v1 §5 row, which said Premium adds "no authority path": Premium adds no Source-of-Truth path, while D1 adds
   an approved action-authorization path;
4. every row that depended on a decision, which now records the decision as approved, with its conditions.

No other classification changed. Clarification C1, approved at closure, changes none either: structured system state
belongs to the D3 extension and falls under the same constraints (PRD v1 §14, System Architecture v1 §7, invariants
8 and 57).

Special attention, as requested:

| Topic | Classification | Where |
|---|---|---|
| CHANGE and SEND human approval | INTENTIONAL PREMIUM POLICY DIFFERENCE (D1): only for verified Premium tenants, LOW or MEDIUM risk, a closed policy an authorized Tenant Admin configured in advance, and every execution-time check; SEND bounded by recipient, channel and content scope | Policies §2.2 |
| HIGH-risk approval | COMPATIBLE: HIGH risk always requires human approval, in Premium too | Policies §2.2, §2.3 |
| Learned auto-execution | COMPATIBLE: Premium autonomy is policy-granted, never learned; the learned-automation flag stays OFF | Policies §2.4 |
| Local Agent authority | PREMIUM EXTENSION (D3): still a limited sensor and executor, never a decider; no screenshot state source, recording, keylogging, clipboard harvesting, unrestricted shell, or direct send or delete | Policies §10 |
| AI not a Source of Truth | COMPATIBLE: no memory, Task state or Agent claim is ever a fact | Premium architecture §1, §11 |
| External content as untrusted data | COMPATIBLE, with trust classes | Policies §7 |
| Tenant isolation | COMPATIBLE: every Premium record, context, message and trace is tenant-scoped, end-customer information included | Policies §4, §8, §11 |
| Support access | COMPATIBLE: the Support Domain (D4) processes end-customer information as Tenant Work Data; internal operator and support access stays governed by invariants 50 and 51, unchanged | Policies §11 |
| Retention | COMPATIBLE: raw content, end-customer information included, follows PRD v1 §12; memory retention classes are decision L2 | Policies §8, §11 |
| Audit | COMPATIBLE: append-only, no raw content, the full delegated-authority chain of a user or a Service Identity | Policies §4.1, §15 |
| Execution kill switches | PREMIUM EXTENSION: the Core switch stays independent and stops Premium too | Policies §13 |
| Idempotency | COMPATIBLE: every effect idempotent or effect-detecting; no blind replay | Premium architecture §8 |
| Verification | COMPATIBLE: execute, reread, verify; an Agent's claim is never success, and D1 requires authoritative verification after every autonomous action | Policies §2.2, §12 |

### PRD v1

| Section | Material overlap | Classification | Premium position |
|---|---|---|---|
| §1 Product purpose | Premium adds bounded execution to understanding, remembering and preparing; PRD v1 §1 calls the whole product a "White-Label/Premium capability" | PREMIUM EXTENSION | The Core purpose and promise are unchanged. The autonomy and the new Domain Premium brings were approved as D1 and D4; the double meaning of "Premium" is resolved in the terminology (Premium architecture §2) |
| §2 Primary end-user experience | None changed | COMPATIBLE | Premium adds no step to the start-of-day flow |
| §3 Mobile and desktop | Same cloud state | COMPATIBLE | Unchanged |
| §4 Work memory and factuality | Fact, Inference, Unknown | COMPATIBLE | Premium memory is never a fact; Unknowns never silently become facts |
| §5 Source of Truth | Authority by data type; AI never chooses | COMPATIBLE | Unchanged: Premium adds no Source-of-Truth path. The employee remains the authority for their own approve and reject; an autonomous action under D1 is authorized by the tenant's policy, never by an inferred approval |
| §6 Overload and planning | A true REJECT only from Source-of-Truth data | COMPATIBLE | Premium's DENY comes only from deterministic policy |
| §7 Action model: safe READ and PREPARE automatic; HIGH risk always approval; AI may only raise risk | Same rules | COMPATIBLE | Kept for every tenant; HIGH risk is never autonomous in Premium |
| §7 Action model: "CHANGE → requires approval", "SEND → requires approval", "Any external write, change, or send requires explicit approval", the customer may only make defaults stricter | Premium lets some CHANGE and SEND run without per-action approval | INTENTIONAL PREMIUM POLICY DIFFERENCE (D1) | Approved: only for verified Premium tenants, only LOW or MEDIUM risk, only under a closed policy an authorized Tenant Admin configured in advance, and only when every execution-time check passes, followed by authoritative verification (Policies §2.2). SEND is bounded by recipient, channel and content scope. Core tenants and uncovered actions are unchanged |
| §7 "Approve All" approves only a closed set of currently displayed actions and is not a wildcard authorization | Batch approval in Premium | COMPATIBLE | Unchanged: approving a batch of REQUIRE_APPROVAL actions approves only that displayed set. The Premium Autonomous Action Policy is a separate mechanism, not an Approve All, and is itself closed (Policies §2.1, §2.2) |
| §7 UI grouping | Autonomous executions must be visible | PREMIUM EXTENSION | Autonomous actions are visible to the affected user and the Tenant Admin (Policies §15); the UI itself comes later |
| §8 Start Work behavior | Unsaved content never closed automatically | COMPATIBLE | The Local Agent never closes unsaved content in Premium either |
| §9 Learning behavior: 21-day learning, the 3-rejection rule, learned auto-execution OFF for the Human MVP | Premium's relation to learning | COMPATIBLE | Unchanged; learning never grants authority (Policies §2.4) |
| §9 "Only read/prepare/open/organize actions may ultimately become fully automatic. Writes/changes/sends always require approval even if learned." | Premium's policy-granted autonomy covers some writes, changes and sends | INTENTIONAL PREMIUM POLICY DIFFERENCE (D1) | Premium autonomy is never learned, but it does let some writes, changes and sends run without per-action approval; approved as D1 |
| §10 Integration and connector model | Adapter per SaaS; monday first | COMPATIBLE | Tools wrap connector operations; Support needs helpdesk or CRM connectors, decided before P8 (L3) |
| §11 Canonical event model | Envelope and fields | COMPATIBLE | Premium triggers and results use Core events |
| §12 Data retention and memory layers | Raw content at most 30 days; cross-customer learning only truly anonymized and aggregated | COMPATIBLE | Raw content in any Premium layer, end-customer information in Support included (D4), follows §12; generalized knowledge meets its standard; retention classes of derived Premium memory are decision L2 |
| §13 AI architecture requirements | AI responsibilities; deterministic responsibilities; validated structured output; Safe Mode | PREMIUM EXTENSION | Adds planning, diagnosis and Tool-use proposals to the AI responsibilities. Authorization, approval requirements, action policy, security boundaries and retention stay deterministic; one AI Gateway; structured, validated output |
| §14 Local Windows Agent | Default metadata; deeper access only through explicitly approved connectors or sources; no screenshots, recording, keylogging, clipboard harvesting or scanning by default; may not independently send, mutate business data where an API exists, delete, close unsafe content or decide priority; no shell | PREMIUM EXTENSION (D3) | Approved for Premium v1 as explicitly approved deeper-access mechanisms: structured UI state, DOM or accessibility reading and fixed, allowlisted UI automation, off by default, policy-checked and task-scoped, with no new authority; clarification C1 adds structured system state as a separate preferred level above structured UI. The Local Agent still never sends, deletes, closes unsaved content, decides priority or runs a shell, has no screenshot or surveillance capability, and leaves business mutations to official Connectors and APIs |
| §15 Offline behavior | Offline grants no new authority | COMPATIBLE | Expired commands are authorized again, never replayed |
| §16 Authentication and privacy roles | Controller and processor roles; employee sees personal detail; manager aggregate; support access scoped and logged | PREMIUM EXTENSION (D2, D4) | Adds delegated authority: an action in a user's name needs the tenant's policy and the user's own revocable delegation, and a service workflow acts under an explicit Service Identity, never a user's (D2). End-customer information in Support is Tenant Work Data under the same controller and processor model (D4). The privacy model and internal support access are unchanged |
| §17 Security principles | Tenant boundary, server-side tenant, secrets, logs without raw content | COMPATIBLE | Unchanged; Premium telemetry and audit carry no raw content |
| §18 MVP deployment choices | Modular Monolith; the stack | COMPATIBLE | Existing stack and deployables; any new one needs approval |
| §19 MVP exclusions | Full learned automation execution excluded; multiple production connectors deferred | COMPATIBLE | Learned automation stays excluded; Premium autonomy is policy-granted; Support connectors are decision L3 |
| §20 Measurement | Startup-time KPI and ROI | PREMIUM EXTENSION | The Core KPI is unchanged; Premium adds cost, budget variance and the Human Interruption Rate |
| §21 Synthetic pilot | Deterministic ground truth; prompt injection, unauthorized send, cross-tenant attacks | PREMIUM EXTENSION | The P10 synthetic agentic pilot follows the same principles and adds agentic scenarios |
| §22 Go / No-Go | Immediate No-Go: unauthorized action, false fact as certain, cross-employee or cross-tenant leakage | COMPATIBLE | Inherited by P10 and P11. With D1, any action outside a grant or an approval is an unauthorized action |
| §23 Product freeze rule | Changes to product behavior, privacy, the approval model, action authority or retention need explicit owner approval; new features are post-MVP backlog unless they unblock a critical MVP requirement | COMPATIBLE: it governs P0 | The product owner gave exactly those approvals, D1 to D4, on 2026-09-29; when Premium implementation may run is decision L1 |

### System Architecture v1

| Section | Material overlap | Classification | Premium position |
|---|---|---|---|
| §1 System context | Core planes; execution through Action Engine → Policy → Approval → Executor | PREMIUM EXTENSION | Premium is a plane above Core; all its execution goes through the Core Action Engine |
| §2 Integration plane | Adapters; webhooks the fast path, reconciliation the correctness path | COMPATIBLE | The constraints on Gate 3 (Premium architecture §20) shape the connector contract and add no scope |
| §3 Event and data plane | Acknowledge after durable receipt; ordering; dedupe; Snapshot provenance; intent is never fact; quarantine | COMPATIBLE | Premium never writes the Snapshot and consumes Core events only after they are committed |
| §4 Work Memory and intelligence plane | Memory layers; minimal context bundles; LLM self-confidence advisory; the validator keeps AI from bypassing action policy; Safe Mode | PREMIUM EXTENSION | Adds Premium memory layers 1 to 3 and 5; Core Work Memory is unchanged; Agent context stays minimal; the deterministic validator and the Policy Engine still guard every action |
| §5 Planning and uncertainty | AI cannot hard-reject | COMPATIBLE | Unchanged |
| §6 Action engine: the only path to execution; lifecycle; UNKNOWN reconciled first; material change invalidates approval | Same | COMPATIBLE | Every Tool call is an Action Request on this path |
| §6 Action engine: Approval Check; approvals bind to the employee | Autonomous actions have no per-action employee approval | INTENTIONAL PREMIUM POLICY DIFFERENCE (D1) | Approved: an ALLOW under the Premium Autonomous Action Policy and a Policy Grant authorizes instead, bound to the same fields, with the grant's user or Service Identity in place of the employee (Policies §4.3, §4.5) |
| §7 Windows Local Agent | Typed, allowlisted actions; the integration hierarchy; business mutations through the API; enrollment; outbound-only transport | PREMIUM EXTENSION (D3) | The hierarchy gains structured system state (level 2, approved by C1) and structured UI, DOM or accessibility reading (level 5, approved by D3), both under the D3 constraints; business mutations stay on the connector or API; everything else is unchanged |
| §8 Security and multi-tenancy | Server-side tenant; role and resource authorization; manager aggregate; the Tenant Admin gains no automatic content access; isolation; Key Vault; separate retention boundaries | PREMIUM EXTENSION (D2) | Adds delegated user authority and explicit tenant Service Identities as the identities of Premium actions; tenant context stays server-side and authorization role- and resource-level. The Tenant Admin configures Premium policy but gains no content access through it |
| §9 MVP deployment architecture | Modular Monolith; deployables; three queues; the database is the truth; inbox and outbox; environments | COMPATIBLE | Existing deployables and queues first; a new one needs approval (Premium architecture §19) |
| §10 Observability and traceability | Correlation across the chain; metrics; no secrets or payloads in logs | PREMIUM EXTENSION | Adds the Premium trace and cost metrics under the same hygiene |
| §11 Synthetic Pilot architecture | Fault injection, security probes, seeds and versions | PREMIUM EXTENSION | P10 adds agentic scenarios |
| §12 Initial performance targets | Core targets | COMPATIBLE | Premium must not degrade them (Policies §16); its own targets come later |
| §13 Open risks | Legal review before the Human Pilot | COMPATIBLE | The same kind of review covers Premium before P11 (L4) |
| §14 Development rule | Stop and report any change to authority, approval, retention, privacy, isolation or Source of Truth | COMPATIBLE: it governs P0 | P0 stopped and reported D1 to D4 instead of changing any canonical document, and the product owner approved them on 2026-09-29 |

### Development Gates v1

| Element | Classification | Premium position |
|---|---|---|
| One Gate at a time; the product owner approves movement between Gates | COMPATIBLE | P0 comes before Core Gate 3 by the product owner's decision; a Core Gate and a Premium Gate never run together unless the owner decides otherwise (Premium Development Gates §1) |
| Gate 3 — monday Connector | COMPATIBLE | Its scope and acceptance are unchanged; P0 adds design constraints only; Gate 3 may begin only after the P0 closure is committed |
| Gates 4, 6, 7, 8 and 9 | COMPATIBLE | They deliver the Core prerequisites of P2, P4, P6, P7, P10 and P11; no Premium Gate builds them early |
| The Gate workflow | PREMIUM EXTENSION | The Premium workflow is a superset: it adds attack tests, a security review and a cost and observability check |

### Architecture Invariants v1 — all 83

| # | Invariant | Classification | Premium position |
|---|---|---|---|
| 1 | Every event belongs to a tenant before meaningful processing | COMPATIBLE | The tenant is resolved server-side before any Task exists; every Task, grant and memory is tenant-owned |
| 2 | Every fact shown to a user must be traceable to a source | COMPATIBLE | Results state only verified facts with provenance |
| 3 | AI never executes an action directly | COMPATIBLE | Agents only request Tool calls; the Policy Engine decides and the Core Action Engine executes |
| 4 | Every action passes through the Policy Engine | COMPATIBLE | Every Tool call, READ included, is checked by the single Core Policy Engine |
| 5 | Source of Truth is defined by code/configuration, not by AI | COMPATIBLE | Core authority rules unchanged |
| 6 | Mobile and desktop consume the same cloud state | COMPATIBLE | Unchanged |
| 7 | Work Memory is updated continuously; Morning Brief is final assembly | COMPATIBLE | Premium writes Core Work Memory only through Core's paths |
| 8 | Local Agent is a limited sensor/executor, not a decision maker | PREMIUM EXTENSION (D3) | Still limited and never a decider; D3 and C1 add structured system state, structured UI state, DOM or accessibility reading and fixed, allowlisted UI automation, off by default and policy-checked, with no new authority |
| 9 | Failure of the Intelligence Layer does not disable the Fact Layer | COMPATIBLE | A Premium failure pauses Premium only |
| 10 | Failure in any component must not create an unauthorized external action | COMPATIBLE | Fail closed; checkpoints; no blind replay; kill switches |
| 11 | Fact Events and AI Insights use separate logical stores/paths | COMPATIBLE | Premium memory and Task state are separate from facts |
| 12 | Receipt time alone never determines business ordering | COMPATIBLE | Unchanged |
| 13 | Unknown/invalid/untrusted events do not reach Snapshot | COMPATIBLE | Premium never writes the Snapshot; its effects arrive only as validated source events |
| 14 | Every critical Snapshot field stores provenance | COMPATIBLE | Unchanged |
| 15 | Outbound intent is not fact until authoritative source confirmation | COMPATIBLE | Execute, reread, verify |
| 16 | Mutation retries are idempotent | COMPATIBLE | Every effect is idempotent or effect-detecting; retries are bounded |
| 17 | Webhooks provide speed; reconciliation provides correctness | COMPATIBLE | Verification uses the authoritative reread path |
| 18 | There is no blind Last-Write-Wins | COMPATIBLE | Version checks before external writes; no blind overwrite between Agents |
| 19 | User identity mapping is never guessed by AI | COMPATIBLE | Support takes identity from the helpdesk's authoritative record; ambiguity is escalated |
| 20 | Full personal event history is not retained indefinitely | COMPATIBLE | Raw content follows PRD v1 §12; memory retention classes are decision L2 |
| 21 | AI is never a Source of Truth | COMPATIBLE | No memory layer or Agent output is a Source of Truth |
| 22 | A fact without valid evidence is not presented as a confirmed fact | COMPATIBLE | An Agent's success is not verified success |
| 23 | Vector Memory is retrieval-only and never authoritative truth | COMPATIBLE | Premium memories are retrieval aids only |
| 24 | Retrieval is scoped by tenant/permission before semantic search | COMPATIBLE | Applies to every Premium memory layer |
| 25 | External content is treated as data, not instruction | COMPATIBLE | Untrusted-content rules with trust classes |
| 26 | Model confidence alone cannot drive critical decisions | COMPATIBLE | Decisions are deterministic; a model may help route, never authorize |
| 27 | Significant AI output must pass schema and policy validation | COMPATIBLE | Tool inputs are schema-validated before policy; outputs after execution |
| 28 | User clarification may convert an Unknown into a human-confirmed fact | COMPATIBLE | Missing data goes to a Specialist, then a human; Agents never question end customers |
| 29 | AI failure triggers Safe Mode rather than Fact Layer failure | COMPATIBLE | Premium pauses; Core continues, with Safe Mode where needed |
| 30 | Hard Reject cannot be based only on an AI estimate/inference | COMPATIBLE | DENY comes only from deterministic policy |
| 31 | AI never communicates directly with the Local Agent | COMPATIBLE | Local Agent commands go only through the Action Engine |
| 32 | Agent accepts only typed/allowlisted actions | COMPATIBLE | Every Premium Local Agent command, structured system state reads and UI automation included, is typed and allowlisted |
| 33 | MVP has no arbitrary remote-code execution capability | COMPATIBLE | No shell or script Tool exists |
| 34 | Critical policies are enforced both in cloud and locally where applicable | COMPATIBLE | The Local Policy Guard rechecks Premium commands |
| 35 | Approve All approves only a closed action set | COMPATIBLE | Approve All is unchanged in Premium and still approves only the closed set displayed. The Premium Autonomous Action Policy is a different mechanism, not an Approve All, and is itself a closed, enumerated policy, never a wildcard |
| 36 | Material change to an approved action invalidates the approval | COMPATIBLE | Human approvals keep the Core binding; a payload outside a grant's bounds needs a new Decision |
| 37 | SaaS business mutations use the official connector/API when available | COMPATIBLE | The hierarchy starts with the official API |
| 38 | Local Agent is not a business-authority source | COMPATIBLE | Unchanged |
| 39 | Unsafe close of unsaved work is not an automatic preparation action | COMPATIBLE | Never done in Premium either |
| 40 | Sent command is not considered success until verified | COMPATIBLE | Verified success only |
| 41 | Offline Agent receives no new authority | COMPATIBLE | Expired commands are authorized again, never replayed |
| 42 | Execution Layer has an independent kill switch | PREMIUM EXTENSION | The Core switch stays independent and stops Premium too; Premium adds Agent, Task, User, Domain, Tenant and all-Premium switches |
| 43 | Commands are sent only to an Agent version declaring the required capability | COMPATIBLE | Premium Local Agent commands need a declared capability |
| 44 | Tenant is a security boundary across the full product | COMPATIBLE | Every Premium record, context and trace is tenant-scoped |
| 45 | Tenant context is resolved server-side | COMPATIBLE | From the trigger's Core tenant, never from content |
| 46 | Authentication is never sufficient without authorization | COMPATIBLE | Every Tool call is authorized when it happens |
| 47 | Email is not a security identifier | COMPATIBLE | Support never treats an email address as proof of identity or authority |
| 48 | Every resource access is checked for tenant and resource permission | COMPATIBLE | Grants name the Resource; every call is checked |
| 49 | Manager visibility is aggregate by default | COMPATIBLE | Per-user Premium metrics reach managers only in aggregate |
| 50 | Internal support has no persistent cross-tenant raw-data access | COMPATIBLE | The Support Domain is not internal support; internal support access is unchanged |
| 51 | Support elevation is scoped, time-bound and audited | COMPATIBLE | Unchanged; Premium adds no elevation |
| 52 | Tenant isolation applies to DB, cache, queues, vector retrieval, logs and AI context | COMPATIBLE | Tasks, memories, messages, traces and Agent contexts are per tenant; a Shared Incident Context is one tenant's |
| 53 | Secrets are not stored in source code, Local Agent config, ordinary DB fields or logs | COMPATIBLE | Agents never receive credentials; connectors hold them |
| 54 | SaaS credentials and device credentials are separate trust domains | COMPATIBLE | Unchanged |
| 55 | Raw Work Payload retention and Security Audit retention are separate lifecycles | COMPATIBLE | Audit and telemetry carry no raw content |
| 56 | Production/Pilot and Demo/Synthetic data do not share a trust boundary | COMPATIBLE | Sandbox and simulation use synthetic data only |
| 57 | Unnecessary surveillance capabilities do not exist in the Agent | PREMIUM EXTENSION (D3) | No screenshot state source, recording, keylogging, clipboard harvesting, arbitrary scanning or unrestricted shell; structured system state and structured UI reading are task-scoped, policy-checked and off by default |
| 58 | Permission revocation is enforced at access/retrieval time, not only at ingestion time | COMPATIBLE | Revocation takes effect at the next access or execution |
| 59 | Any cross-tenant leakage is a No-Go, including in synthetic testing | COMPATIBLE | Premium Gates carry attack tests for it |
| 60 | MVP is a Modular Monolith, not a microservice swarm | COMPATIBLE | Agents are units of work inside the monolith; a new deployable needs approval |
| 61 | PostgreSQL is the primary operational data store | COMPATIBLE | Durable Task state and grants live in PostgreSQL |
| 62 | MVP vector retrieval uses pgvector | COMPATIBLE | Premium memory retrieval uses pgvector |
| 63 | Service Bus is transport, not Source of Truth | COMPATIBLE | Task state lives in PostgreSQL |
| 64 | Application-level idempotency remains mandatory even when broker dedupe is enabled | COMPATIBLE | Every Premium effect is idempotent or effect-detecting |
| 65 | No Kubernetes/AKS in MVP without measured need | COMPATIBLE | Unchanged |
| 66 | Local Agent does not require SYSTEM privileges by default | COMPATIBLE | Every Local Agent capability, structured system state and UI automation included, runs in the user's session without SYSTEM privileges |
| 67 | Mobile MVP is a PWA behind a notification abstraction | COMPATIBLE | Unchanged |
| 68 | Dev, Synthetic and Human Pilot use separate data/secrets boundaries | COMPATIBLE | The improvement pipeline runs inside the Synthetic boundary |
| 69 | Infrastructure is defined as code | COMPATIBLE | Unchanged |
| 70 | Production/Human-Pilot Agent packages must be signed | COMPATIBLE | Unchanged |
| 71 | Deployment authentication uses short-lived/federated identity | COMPATIBLE | Unchanged |
| 72 | Logical module boundaries remain enforceable | COMPATIBLE | Premium modules get architecture-test-enforced boundaries (P1) |
| 73 | Synthetic Pilot Ground Truth exists outside the system under test | COMPATIBLE | The P10 synthetic agentic pilot keeps its ground truth outside |
| 74 | Deterministic simulator and real monday integration are tested separately | COMPATIBLE | Unchanged |
| 75 | Every simulation run is reproducible via seed and version metadata | COMPATIBLE | P10 also records Agent, model and prompt versions |
| 76 | Critical fact hallucination in Golden Scenarios is a No-Go | COMPATIBLE | Applies to Premium results |
| 77 | Correct Unknown/abstention is preferred to an invented fact | COMPATIBLE | Agents escalate rather than guess |
| 78 | Synthetic ROI is not represented as real commercial ROI proof | COMPATIBLE | Applies to Premium metrics |
| 79 | Learned auto-execution remains feature-flagged OFF in Human MVP | COMPATIBLE | Premium autonomy is policy-granted, never learned; learning never grants authority |
| 80 | Synthetic Pilot becomes a permanent regression suite | COMPATIBLE | Premium agentic scenarios join it |
| 81 | A real Windows Agent must pass a smoke test before Human Pilot | COMPATIBLE | A prerequisite of P11 |
| 82 | The real monday Connector must pass end-to-end testing before Human Pilot | COMPATIBLE | A prerequisite of P11 |
| 83 | Fault Injection is part of the Go Gate, not an optional test | COMPATIBLE | Every Premium Gate has failure and attack tests |

Invariant totals: 80 COMPATIBLE, 3 PREMIUM EXTENSION (8, 42, 57), 0 INTENTIONAL PREMIUM POLICY DIFFERENCE, 0
UNRESOLVED CONTRADICTION.

## Unresolved blockers

None. The product owner approved D1 to D4 and clarification C1 on 2026-09-29, so no part of the architecture had to
be removed, and closed P0 as PASS.

## Product-owner decisions

### Taken on 2026-09-29

**D1 — Adopt the Premium Autonomous Action Policy. APPROVED.**
- Decision: for verified Premium tenants, a LOW or MEDIUM-risk CHANGE or SEND may execute without per-action human
  approval only when all of the following are true: the action type is part of a closed, preconfigured Tenant Admin
  policy; the target and resource scope are explicit; the current delegated authority is valid; the Policy Engine
  returns ALLOW at execution time; the current state is revalidated where required; the budget and resource limits
  are valid; the Tool guardrails pass; and authoritative verification follows execution. HIGH-risk actions always
  require human approval. Core tenant behavior is unchanged. For SEND, the policy can constrain the recipient or
  domain, the channel, the content or action category and any other relevant scope, rather than granting
  unrestricted external messaging authority.
- Affected Core rules: PRD v1 §7 ("CHANGE → requires approval"; "SEND → requires approval"; "Any external write,
  change, or send requires explicit approval"; the customer may only make the defaults stricter); PRD v1 §9 ("Only
  read/prepare/open/organize actions may ultimately become fully automatic. Writes/changes/sends always require
  approval even if learned."); System Architecture v1 §6 (the Core action and approval flow: the Approval Check, with
  approvals bound to the employee). No Architecture Invariant is affected: invariant 35 is COMPATIBLE (see the audit
  correction). PRD v1 §23 requires explicit product-owner approval for any change to the approval model or action
  authority, and D1 is that approval.
- Applied in: Premium Policies §2.1 to §2.4 and §4.3.

**D2 — The identity behind a Premium action. APPROVED WITH CLARIFICATION.**
- Decision: by default, an action performed in a user's name needs the tenant's policy AND that user's own revocable
  delegation. A legitimate system or service workflow uses an explicit Service Identity (Service Principal) authority
  chain instead of inventing or impersonating a user identity. Every action stays traceable through its
  delegated-authority chain.
- Affected Core rules: PRD v1 §16 (the privacy model; the employee sees personal detail), PRD v1 §7 (in Core the
  employee approves), System Architecture v1 §6 (approvals bound to the employee) and §8 (identity and authorization);
  invariants 45, 46 and 48, which stay COMPATIBLE.
- Applied in: Premium Policies §4.1 to §4.3; the Service Identity term of Premium architecture §2.

**D3 — The Local Agent extensions. APPROVED FOR PREMIUM V1.**
- Decision: the Local Agent may support structured UI state, DOM or accessibility reading where applicable, and fixed,
  allowlisted UI automation, off by default, policy-checked and scoped. The Premium v1 Local Agent must not introduce
  screenshots as a default state source, screen recording, keylogging, clipboard harvesting, unrestricted shell or
  script execution, or direct send or delete operations. Business mutations continue to prefer official Connectors
  and APIs.
- Affected Core rules: PRD v1 §14 (the default metadata; deeper content access only through explicitly approved
  connectors or sources; no screenshots, recording, keylogging, clipboard harvesting or arbitrary scanning by default;
  the Local Agent may not independently send, mutate business data where an API connector exists, delete, close unsafe
  content or decide priority; no generic shell), PRD v1 §16 (privacy), System Architecture v1 §7 (the integration
  hierarchy), invariants 8 and 57.
- Applied in: Premium Policies §10; Premium architecture §13. Clarification C1 adds structured system state.

**D4 — Support as the first Premium Domain. APPROVED.**
- Decision: Support is the first Premium Domain. End-customer information processed by the Premium Support Domain is
  Tenant Work Data and remains subject to tenant isolation, Minimum Necessary Data, the Core privacy controls, the
  Core retention framework, the audit rules, and the deletion and revocation requirements. The Premium Support Domain
  is not the internal operator or support access that the Core support-access invariants govern.
- Affected Core rules: PRD v1 §1 (the product's purpose), §10 (the connector plan), §12 (retention: ticket content is
  raw work data), §16 (the controller and processor model now covers the tenant's end-customer data), §19 (multiple
  production connectors deferred), §23 (new product behavior); invariants 50 and 51, which stay COMPATIBLE.
- Applied in: Premium Policies §11; Premium architecture §14.

**C1 — Structured system state. APPROVED AT CLOSURE (2026-09-29).**
- Decision: structured system state is explicitly allowed as a Premium v1 information source and stays a separate
  preferred level above structured UI, DOM or accessibility. The preferred Local Agent information hierarchy is:
  1. official API or Connector; 2. structured system state; 3. documented protocol or deep link; 4. OS or application
  metadata; 5. structured UI, DOM or accessibility; 6. justified, allowlisted UI automation. C1 does not expand Local
  Agent authority beyond the D3 constraints.
- Applied in: Premium Policies §10; Premium architecture §13.

### Needed before later Premium Gates

These are recorded now; P0 does not wait for them.

| Decision | Needed before | Affected Core rules | Question |
|---|---|---|---|
| L1 | P1 | PRD v1 §23 (new features are post-MVP backlog unless they unblock a critical MVP requirement); Development Gates v1 (one Gate at a time) | Does Premium implementation start after the Core MVP (Core Gate 9), or interleave with Core Gates 4 to 9 one Gate at a time, as an explicit exception? |
| L2 | P6; for the audit of autonomous actions, P2 | PRD v1 §12 and §17; invariants 20 and 55 | The retention classes of Private Agent Memory, Shared Premium Memory and the audit of autonomous actions |
| L3 | P8 | PRD v1 §10 and §19; the scope discipline of `CLAUDE.md` | Which helpdesk or CRM connectors the Support Domain uses |
| L4 | P11 | System Architecture v1 §13; PRD v1 §16 | The legal and privacy review of autonomous execution and of end-customer data before a human Premium pilot |

## Validation

Rerun on 2026-09-29 at closure, against the working tree before the closure commit. Every check is read-only.

| # | Check | Result |
|---|---|---|
| 1 | `git status` | Modified: `CLAUDE.md`, `README.md`, `INIT.md` and `docs/canonical/PROJECT-STATUS.md`. New: the three documents in `docs/premium/` and this report. Nothing staged |
| 2 | Exact diff | Against `9988f54`, the four modified files change by 49 insertions and 12 deletions, all status or handoff wording; `git diff --check` is clean. Each file keeps its line endings, and the new files use LF |
| 3 | Frozen Core documents | PRD v1, System Architecture v1, Architecture Invariants v1 and Development Gates v1 in `docs/canonical/` are byte-identical to the SHA-256 baseline taken before P0 began, and git shows no change to them |
| 4 | Code, tests, migrations, configuration, workflows, IaC | Nothing changed under `src/`, `tests/`, `.github/`, `infra/` or `eng/`, and no configuration file changed |
| 5 | Links and paths | Every repository path cited in the new and updated documents exists (36 distinct paths). Every section reference that names its document resolves (132), and the references within a single document were checked by hand. Two `INIT.md` citations older than P0 are a file name whose folder the sentence gives and a wildcard that matches three files; both are valid |
| 6 | Terminology | The defined terms keep one spelling: Policy Grant, Premium Autonomous Action Policy, REQUIRE_APPROVAL, Super Agent, Domain Manager, Local Agent, Service Identity, Tenant Work Data and Tool guardrails. Every reference to the Premium architecture names it as such. No wording from before the owner review or before the closure remains |
| 7 | Gate numbering and decisions | P0 to P11 appear once each and in order, in the Gate table and in the acceptance list, with the names the product owner authorized; no other Premium Gate number appears. D1 to D4 and C1 are each recorded once, as approved; L1 to L4 are each defined once, in this report |
| 8 | Status | P0 is PASS, closed 2026-09-29, in every status and handoff document; the Premium architecture and policies are FROZEN. Core Gate 3 is NEXT / NOT STARTED everywhere and may begin only after this closure is committed. P1 to P11 are planned and NOT STARTED |
| 9 | Azure | No Azure, deployment, job or CD command ran; Azure Dev is untouched |
| 10 | Commit and push | None before the product owner's go: `HEAD`, `origin/main` and the remote `main` were all `9988f54`, the Gate 2 closure commit |

The audit tables were also checked for completeness: all 83 invariants appear once and in order (80 COMPATIBLE,
3 PREMIUM EXTENSION for invariants 8, 42 and 57, 0 INTENTIONAL PREMIUM POLICY DIFFERENCE, 0 UNRESOLVED), invariant 35
is COMPATIBLE and no decision lists it as affected, C1 changes no classification, and the PRD v1 quotations in D1 match
the frozen text word for word.

## Decision

**Premium Gate P0 (Architecture Freeze): PASS — architecture frozen; closed by the product owner on 2026-09-29.** The
Premium architecture, policies and Gate plan are documented and audited against every frozen Core document with no
unresolved contradiction, and every decision P0 needed is taken: D1 to D4 and clarification C1 are approved. The
documents in `docs/premium/` are the frozen Premium baseline, subordinate to the frozen Core documents; a change to
them needs the product owner's explicit approval. Nothing of Premium is implemented, and each Premium rule takes effect
for a tenant only when the capability it governs has passed its Premium Gate.

Core Gate 3 (monday Connector) is **NEXT / NOT STARTED**. It may begin only after this closure is committed, and only
on the product owner's explicit authorization; its Definition of Done then includes the connector constraints of
Premium architecture §20. Premium Gates P1 to P11 are planned and **NOT STARTED**; each needs explicit authorization,
and P1 also needs decision L1.
