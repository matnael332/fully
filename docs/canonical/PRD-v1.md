# PRD v1 — Work Continuity / Employee Start Engine

Status: **FROZEN**

This document is canonical for product behavior. New ideas do not enter MVP unless they solve a critical blocker. Any change that conflicts with this document requires explicit product-owner approval.

## 1. Product purpose

The product is an AI-powered **Work Continuity / Employee Start Engine** embedded into B2B SaaS products as a White-Label/Premium capability.

Core promise:

> **אפס זמן התנעה: כל עובד מתחיל את היום כשהוא כבר בעניינים.**

“Zero startup time” is a marketing hook, not a literal technical promise. The measurable KPI is reduction in time from start-of-day trigger to meaningful work.

The product continuously ingests authorized work events, maintains a work-context model, prepares a concise morning brief, identifies material uncertainty and overload, prepares the relevant work environment, and enables one-click transition into meaningful work.

## 2. Primary end-user experience

### Start-of-day flow

1. A start trigger is received. The host SaaS decides what qualifies as start-of-work: attendance event, application login, PC login, task open, or another configured API trigger.
2. The cloud already holds an updated work state built from prior events.
3. Phone receives a very short brief.
4. Desktop receives the detailed interactive brief from the same cloud state.
5. Employee sees:
   - concise overview;
   - what changed;
   - what matters today;
   - material clarification questions only;
   - overload warning when applicable;
   - what the system already prepared;
   - what requires approval.
6. Employee can accept a proposed agenda or create their own.
7. System validates the employee agenda using deterministic rules.
8. Employee selects what to start.
9. “Start Work” prepares and focuses the relevant work environment.
10. System measures time until meaningful work begins.

Regular morning flow target: **up to 2 minutes**. If the system detects an unrealistic day, it may explicitly recommend spending additional time planning.

## 3. Mobile and desktop

### Phone

Phone is for fast orientation, not full task management.

It shows:
- very short overview;
- only the most important information;
- urgent/exception condition if present;
- a clarification question only when it is critical to the beginning of the workday.

### Desktop

Desktop is the Hero Screen and full work-start experience.

Above the fold it includes:
- overview;
- what changed;
- what matters today;
- Start Work action;
- clear overload warning when relevant.

Clarification questions are shown at the end of the morning flow by default.

Phone and desktop consume the same cloud state. The phone does not “transfer” the brief to the PC.

## 4. Work memory and factuality

The product must not let an LLM decide what “really happened.”

Information is separated into:
- **Fact** — confirmed by an authoritative source;
- **Inference** — plausible interpretation based on evidence;
- **Unknown** — unresolved or insufficiently supported.

Unknowns do not silently become facts.

If uncertainty can materially affect today, it becomes a clarification question. Otherwise it remains unresolved in the background.

Question priority:
1. deadline / criticality;
2. dependency;
3. time feasibility.

Questions should first offer quick multiple-choice where possible, then free text.

## 5. Source of Truth

There is no universal “latest source wins.” Authority is defined by data type.

Canonical examples:
- task status → source task/SaaS system;
- PC activity → local agent;
- user approve/reject → employee;
- identity/permissions → organization identity system;
- deadline → system in which the deadline was defined;
- action result → authoritative destination/executor confirmation.

The AI never chooses which source is authoritative.

## 6. Overload and planning

If the workload is unrealistic, the system:

1. warns the employee;
2. briefly explains why;
3. offers two agenda options:
   - urgency-first;
   - efficiency-first / reduced context switching;
4. allows the employee to build their own agenda;
5. validates that agenda.

Hard validation order:
1. deadline / critical conflict;
2. dependency/order conflict;
3. time infeasibility.

A true **REJECT** may be based only on confirmed source-of-truth data.

AI inference alone may produce a warning, not a hard reject.

On reject, the system:
- explains briefly;
- proposes one minimal correction;
- preserves the employee’s plan as much as possible.

Employee may still ignore the correction. The system records the risk prediction and later compares prediction vs. outcome for learning.

## 7. Action model

Every action has both:
- **Action Type**: READ / PREPARE / CHANGE / SEND;
- **Risk Level**: LOW / MEDIUM / HIGH.

Default behavior:
- safe READ → may be automatic;
- safe PREPARE → may be automatic;
- CHANGE → requires approval;
- SEND → requires approval;
- HIGH risk → always requires approval.

Risk governance:
- our product defines safe defaults;
- host SaaS/customer may make them stricter;
- AI may only raise risk, never lower a rule/policy-defined risk.

### Automatic preparation

The system may automatically:
- read authorized metadata/data;
- collect and organize;
- open documents/tabs/apps;
- prepare drafts internally;
- arrange the workspace.

Any external write, change, or send requires explicit approval.

### UI grouping

Morning workspace shows:
- **בוצע אוטומטית** — safe read/prepare already executed;
- **מוכן עבורך** — drafts/resources prepared but not externally applied;
- **דורש אישור** — changes/writes/sends awaiting approval.

“Approve All” approves only a closed set of currently displayed actions. It is not a wildcard authorization.

## 8. Start Work behavior

On “Start Work,” the system can:
- open the selected task;
- open relevant source system;
- open authorized documents/tabs;
- restore relevant applications;
- bring the main work surface to focus;
- minimize/hide unrelated windows.

Automatic closing of third-party content is not required for MVP. Anything that can lose unsaved state must not be closed automatically.

## 9. Learning behavior

System quietly records:
- accept;
- reject;
- edit;
- ignore;
- override;
- actual outcome.

Advanced personal automation uses a **21 active-day** learning period and requires:
- sufficient repetitions;
- high consistency;
- high confidence.

Then the employee receives a one-time explicit choice: automate or keep as suggestion.

Only read/prepare/open/organize actions may ultimately become fully automatic.

Writes/changes/sends always require approval even if learned.

If an approved personal automation is rejected/corrected 3 times, it is suspended and not automatically reactivated.

For Human MVP, advanced learned auto-execution remains **feature-flagged OFF**. The infrastructure/signals may exist and be tested synthetically.

## 10. First integration and connector model

First real SaaS integration: **monday.com**.

Selection rationale: best balance of implementation speed, useful event richness, recognizable product, and demo clarity.

Architecture remains SaaS-independent. Each SaaS uses an Adapter/Connector that maps provider events into our canonical event model.

Future examples: Jira, Asana, CRM, HR/attendance, other work systems.

## 11. Canonical event model

Use a **CloudEvents-compatible envelope plus product-specific extensions**.

Core fields include conceptually:
- event ID;
- source;
- type;
- occurred time;
- received time;
- tenant;
- actor;
- object;
- before/after change;
- provider/source reference;
- schema version;
- correlation ID;
- causation ID;
- domain-specific payload.

## 12. Data retention and memory layers

Four distinct layers:

1. **Raw work events/payloads** — rolling maximum of 30 days.
2. **Current work snapshot** — retained while needed for active product use.
3. **Personal vector memory** — retained only while relevant/needed; old/unneeded personal semantic memory can be distilled, deleted or anonymized.
4. **Truly anonymous aggregate product knowledge** — may be retained long-term.

Pseudonymized data is not treated as anonymous if it can be re-identified.

Cross-customer learning uses only truly anonymized and aggregated outputs.

## 13. AI architecture requirements

Use an **AI Gateway** with multiple provider/model adapters. No single-provider lock-in.

AI responsibilities:
- context understanding;
- summarization;
- classification;
- recommendation;
- ambiguity detection;
- draft generation.

Deterministic code responsibilities:
- source-of-truth rules;
- authorization;
- approval requirements;
- 21-day threshold;
- 3-rejection rule;
- reject logic;
- retention;
- security boundaries;
- action policy.

AI output must be structured and validated before affecting product state.

If primary AI provider fails:
1. try configured fallback provider;
2. if all fail, use **Safe Mode** based only on confirmed Snapshot + deterministic rules/templates.

## 14. Local Windows Agent

MVP OS: **Windows only**.

Agent is a limited Sensor + Workspace Executor, not a decision maker.

Default metadata allowed:
- active application;
- transitions;
- authorized task/document identity;
- navigation/search patterns as approved;
- timing/duration metadata.

Not part of the product by default:
- screenshots;
- screen recording;
- keylogging;
- clipboard harvesting;
- arbitrary content scanning.

Deeper content access comes only through explicitly approved connectors/sources.

Agent may:
- open URL/task/document;
- restore/focus app/window;
- minimize/hide unrelated windows;
- report permitted activity;
- queue events offline.

Agent may not independently:
- send messages;
- mutate business SaaS data when cloud API connector is available;
- delete data;
- close unsafe/unsaved content;
- decide priority.

No generic remote shell/PowerShell execution capability in MVP.

## 15. Offline behavior

Agent keeps:
- last usable local snapshot/state;
- encrypted temporary unsynced event queue;
- pending command state.

On reconnect:
- authenticate;
- upload queued events;
- deduplicate;
- reconcile with source;
- delete successfully synced local payload.

No local LLM is required.

Offline mode grants no new autonomous authority.

## 16. Authentication and privacy roles

MVP authentication:
- enterprise work-account SSO;
- local/demo account for demo/synthetic use.

Production direction remains identity-provider abstraction friendly.

Privacy model by default:
- employer/customer = controller / בעל שליטה;
- host SaaS and our engine = processors/service providers, subject to contract and legal review.

Employee sees personal detail.

Manager sees aggregate team metrics/trends/ROI, not individual surveillance data by default.

Authorized tenant/admin/support access to raw information is only for justified troubleshooting/audit with scoped and logged elevation.

## 17. Security principles

- Tenant is a security boundary.
- Tenant context is resolved server-side, never trusted from client input alone.
- Authentication is not authorization.
- Email is not a security identifier.
- Resource access requires tenant + resource-level permission.
- Cross-tenant access is a No-Go condition.
- Secrets live in a dedicated secret store, not code/config/logs.
- Encryption in transit and at rest.
- Shared infrastructure with strong logical isolation by default; future enterprise dedicated data plane possible.

Raw work-data retention and long-lived technical/security audit logs are separate lifecycles. Security logs must not become shadow storage of raw work content.

## 18. MVP deployment choices

Canonical MVP stack:
- backend: C# / .NET 10 LTS;
- API: ASP.NET Core;
- Web UI: React + TypeScript;
- mobile: responsive PWA;
- Windows Agent: .NET 10 + Windows App SDK / WinUI 3;
- packaging: MSIX;
- database: Azure Database for PostgreSQL Flexible Server;
- vector: pgvector inside PostgreSQL;
- broker: Azure Service Bus;
- compute: Azure Container Apps;
- scheduled/synthetic jobs: Azure Container Apps Jobs;
- secrets: Azure Key Vault;
- identity: Microsoft Entra ID first, OIDC abstraction;
- large/temp content: Azure Blob Storage;
- observability: OpenTelemetry + Application Insights + Log Analytics;
- edge for human pilot: Azure Front Door + WAF;
- registry: Azure Container Registry;
- CI/CD: GitHub Actions;
- IaC: Bicep.

Architecture style: **Modular Monolith, event-driven**, not microservice swarm.

## 19. MVP exclusions / deferred features

Not required before proving core hook and ROI:
- multiple production SaaS connectors;
- macOS agent;
- native iOS/Android apps;
- Kubernetes/AKS;
- Kafka;
- Redis;
- Elasticsearch;
- dedicated vector DB;
- data warehouse/data lake;
- local LLM;
- advanced progress/gamification UI;
- full learned automation execution.

## 20. Measurement

Primary KPI:

> **Startup Time = MeaningfulWorkAt − StartOfDayTriggerAt**

Meaningful work is an action that advances work, not merely page open. Examples:
- work actually started on a task;
- meaningful status transition;
- document opened for editing;
- another mapped productive source-system event.

First pilot measurement design:
- same employee before vs. after;
- 10 working days baseline;
- 21 active days after activation.

Secondary KPI:
- fewer searches/revisits/context switches used to understand current work state.

ROI reporting:
- time saved;
- reduced searching/resetting;
- more consistent structured start;
- estimated monetary value from time saved.

Synthetic ROI only validates measurement mechanics. It is not a commercial claim.

## 21. Synthetic pilot

Before human pilot:
- 20 synthetic workers;
- 31 days each;
- 620 synthetic worker-days;
- deterministic ground truth;
- real monday test workspace flow;
- virtual agent plus at least one real Windows Agent smoke test;
- functional, chaos and security runs;
- reproducible simulation seed/versioning.

Core synthetic scenarios include:
- normal day;
- heavy workload;
- missing event;
- duplicate event;
- delayed/out-of-order event;
- conflicting source data;
- monday outage;
- AI outage;
- agent offline/reconnect;
- new employee;
- long absence;
- task transfer;
- permission removal;
- duplicate/wrong identity mapping;
- unsafe unsaved window;
- rejected actions;
- repeated rejection logic;
- prompt injection;
- unauthorized send;
- cross-tenant/vector attacks;
- action timeout and reconciliation;
- hallucinated deadline.

## 22. Go / No-Go

Synthetic → Human Pilot requires all of:
- core flow passes;
- safety passes;
- factuality passes;
- authorization passes;
- tenant isolation passes;
- failure recovery passes;
- audit traceability passes;
- performance targets pass;
- real monday integration passes;
- real Windows Agent smoke test passes.

Immediate No-Go:
- unauthorized action;
- critical false fact presented as certain;
- cross-employee leakage;
- cross-tenant leakage.

Human pilot Go criteria:
- reduced startup time;
- fewer context-search/reset behaviors;
- real Morning Brief adoption;
- measurable organizational ROI potential.

## 23. Product freeze rule

PRD v1 is frozen.

During development:
- technical decisions may be made within the architecture;
- any change to product behavior, privacy, approval model, KPI definition, retention, Source of Truth, action authority, or user experience contract requires explicit product-owner approval;
- new feature ideas are recorded as Post-MVP backlog unless they unblock a critical MVP requirement.
