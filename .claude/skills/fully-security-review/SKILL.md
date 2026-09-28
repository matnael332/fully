---
name: fully-security-review
description: Use when reviewing pending FULLY changes or a branch for FULLY-specific security issues, before a commit or at a Gate boundary, especially changes touching tenant context, persistence or row-level security, managed identity, RBAC or Bicep role assignments, Key Vault or secrets, CI/CD credentials, logs or telemetry that could carry payloads, or any Azure write or approval boundary.
---

# Security Review for FULLY

## Overview

Review the change, not the intent. Every finding cites a file and line and the rule it breaks. The
frozen invariants are the bar, especially 1, 44-48, 50-56, 58, 59, 63, 64, 68 and 71. A change that
would weaken one is stopped and reported, never quietly fixed. It coexists with the built-in
`/security-review`; the generic pass below keeps it self-contained.

## Scope first

Collect the pending change: `git status`, `git diff`, `git diff --cached`, and every new file. List
the areas it touches: tenant and persistence, identity and RBAC, secrets, logging and telemetry,
workflows and Bicep, external input.

## Checklist

**Tenant isolation**
- Tenant comes only from trusted server-side context, never from a request query, header, route, body
  or form. A tenant id is never parsed from untrusted text; any adapter that must parse one is named
  and pinned by a test.
- Database access goes through `ITenantScopedDatabase`. Every new table is tenant-owned with row-level
  security forced. The runtime role is never a superuser and never bypasses row-level security.
- Code that runs without a tenant uses only the approved narrow functions of Gate 2 decision 1, which
  return ids, status or counts.
- Every message carries its tenant, the consumer re-establishes it, and no transaction spans tenants.

**Identity, RBAC and secrets**
- Each workload uses its own managed identity. No keyed connection strings; Service Bus local
  authentication stays disabled.
- Role assignments are least privilege at the narrowest scope. Any change to them, to identities, to
  networking or to Key Vault access is an owner approval boundary. The CD identity never gains rights
  on the jobs.
- Secrets live only in Key Vault, behind per-secret RBAC and secret references. Never in code,
  configuration, parameter files, logs, errors, telemetry or realistic-looking test fixtures.
- GitHub authenticates to Azure only by OIDC, stores no Azure secret, and pins actions by SHA.

**Payload and log hygiene**
- Logs, telemetry, exceptions and errors carry ids, reason codes and counts only: no payload bodies,
  field values, actor names or tokens.
- Error messages are fixed text, and no exception detail reaches a client.

**Generic pass**
- SQL is parameterised; server-side DDL uses `format` with `%L` or `%I`.
- No unsafe deserialisation or type-name handling, no request to a URL taken from provider data, no
  path traversal, and no disabled TLS validation.
- Every new package is justified, pinned and allowed by the architecture tests.

**Tests and boundaries**
- Security tests are tightened, never weakened or deleted. A new guard is mutation-checked: removing
  the guard must make its test fail.
- Azure writes, `deploy: true`, job starts, and RBAC, identity, network or Key Vault changes stop for
  explicit owner approval.

## Output

List findings from most to least severe, each with file and line, the violated rule and a concrete fix.
Then name the areas checked with no findings. Report only; nothing is changed during the review.
