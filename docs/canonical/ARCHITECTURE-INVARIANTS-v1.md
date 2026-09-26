# Architecture Invariants v1

Status: **FROZEN**

These 83 invariants are non-negotiable constraints for System Architecture v1. Development must not silently weaken or bypass them.

1. Every event belongs to a tenant before meaningful processing.
2. Every fact shown to a user must be traceable to a source.
3. AI never executes an action directly.
4. Every action passes through the Policy Engine.
5. Source of Truth is defined by code/configuration, not by AI.
6. Mobile and desktop consume the same cloud state.
7. Work Memory is updated continuously; Morning Brief is final assembly, not full reconstruction from scratch.
8. Local Agent is a limited sensor/executor, not a decision maker.
9. Failure of the Intelligence Layer does not disable the Fact Layer.
10. Failure in any component must not create an unauthorized external action.
11. Fact Events and AI Insights use separate logical stores/paths.
12. Receipt time alone never determines business ordering.
13. Unknown/invalid/untrusted events do not reach Snapshot.
14. Every critical Snapshot field stores provenance.
15. Outbound intent is not fact until authoritative source confirmation.
16. Mutation retries are idempotent.
17. Webhooks provide speed; reconciliation provides correctness.
18. There is no blind Last-Write-Wins.
19. User identity mapping is never guessed by AI.
20. Full personal event history is not retained indefinitely.
21. AI is never a Source of Truth.
22. A fact without valid evidence is not presented as a confirmed fact.
23. Vector Memory is retrieval-only and never authoritative truth.
24. Retrieval is scoped by tenant/permission before semantic search.
25. External content is treated as data, not instruction.
26. Model confidence alone cannot drive critical decisions.
27. Significant AI output must pass schema and policy validation.
28. User clarification may convert an Unknown into a human-confirmed fact.
29. AI failure triggers Safe Mode rather than Fact Layer failure.
30. Hard Reject cannot be based only on an AI estimate/inference.
31. AI never communicates directly with the Local Agent.
32. Agent accepts only typed/allowlisted actions.
33. MVP has no arbitrary remote-code execution capability.
34. Critical policies are enforced both in cloud and locally where applicable.
35. Approve All approves only a closed action set.
36. Material change to an approved action invalidates the approval.
37. SaaS business mutations use the official connector/API when available.
38. Local Agent is not a business-authority source.
39. Unsafe close of unsaved work is not an automatic preparation action.
40. Sent command is not considered success until verified.
41. Offline Agent receives no new authority.
42. Execution Layer has an independent kill switch.
43. Commands are sent only to an Agent version declaring the required capability.
44. Tenant is a security boundary across the full product.
45. Tenant context is resolved server-side.
46. Authentication is never sufficient without authorization.
47. Email is not a security identifier.
48. Every resource access is checked for tenant and resource permission.
49. Manager visibility is aggregate by default.
50. Internal support has no persistent cross-tenant raw-data access.
51. Support elevation is scoped, time-bound and audited.
52. Tenant isolation applies to DB, cache, queues, vector retrieval, logs and AI context.
53. Secrets are not stored in source code, Local Agent config, ordinary DB fields or logs.
54. SaaS credentials and device credentials are separate trust domains.
55. Raw Work Payload retention and Security Audit retention are separate lifecycles.
56. Production/Pilot and Demo/Synthetic data do not share a trust boundary.
57. Unnecessary surveillance capabilities do not exist in the Agent.
58. Permission revocation is enforced at access/retrieval time, not only at ingestion time.
59. Any cross-tenant leakage is a No-Go, including in synthetic testing.
60. MVP is a Modular Monolith, not a microservice swarm.
61. PostgreSQL is the primary operational data store.
62. MVP vector retrieval uses pgvector rather than a separate authoritative data store.
63. Service Bus is transport, not Source of Truth.
64. Application-level idempotency remains mandatory even when broker dedupe is enabled.
65. No Kubernetes/AKS in MVP without measured need.
66. Local Agent does not require SYSTEM privileges by default.
67. Mobile MVP is a PWA behind a notification abstraction.
68. Dev, Synthetic and Human Pilot use separate data/secrets boundaries.
69. Infrastructure is defined as code.
70. Production/Human-Pilot Agent packages must be signed.
71. Deployment authentication uses short-lived/federated identity, not long-lived GitHub Azure passwords.
72. Logical module boundaries remain enforceable even if modules share a process/deployment.
73. Synthetic Pilot Ground Truth exists outside the system under test.
74. Deterministic simulator and real monday integration are tested separately.
75. Every simulation run is reproducible via seed and version metadata.
76. Critical fact hallucination in Golden Scenarios is a No-Go.
77. Correct Unknown/abstention is preferred to an invented fact.
78. Synthetic ROI is not represented as real commercial ROI proof.
79. Learned auto-execution remains feature-flagged OFF in Human MVP.
80. Synthetic Pilot becomes a permanent regression suite.
81. A real Windows Agent must pass a smoke test before Human Pilot.
82. The real monday Connector must pass end-to-end testing before Human Pilot.
83. Fault Injection is part of the Go Gate, not an optional test.
