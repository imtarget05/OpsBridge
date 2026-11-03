# FlashSale Order Operations Agent — Pilot Plan

**Status:** Proposed implementation plan; no code or cloud changes have been made.
**Pilot owner:** FlashSale Order API context in the OpsBridge repository.
**Pilot scenario:** investigate one dead-lettered FlashSale outbox event and produce a cited, read-only recovery recommendation.
**Deployment target:** local/synthetic evaluation first; cloud pilot only after the cost and service-limit gate. This plan does not claim production readiness or zero-cost SLA.

## Decision and scope

The OpsBridge repository contains two bounded contexts. This pilot targets **FlashSale** because the existing agent-specific read contract is in `src/Order.Api/Program.cs` and exposes one-event metadata without the event payload. It does not target OpsBridge's own procurement/reconciliation outbox or its bulk requeue route.

The runtime and audit for this pilot must live in the FlashSale context/repository ownership. The cross-domain Agent Hub in AKS-SRE-Platform is a reference for evidence envelopes, proposal validation, and evaluation patterns only; it is not a runtime dependency. This follows ADR-017's ownership boundary: OpsBridge must not read or mutate FlashSale tables directly.

The pilot uses three bounded roles, invoked sequentially within one finite workflow:

1. **Triage Agent** — classify a candidate from owner-returned facts and choose whether it is eligible for investigation.
2. **Investigator Agent** — explain the event state using only the gathered evidence and cite each material claim.
3. **Critic/Policy Agent** — independently check citation coverage, scope, freshness, abstention, and forbidden actions; reject or return the proposal for one bounded revision.

The trigger, owner API access, event-state checks, policy enforcement, and workflow state machine are deterministic code. The agents cannot select arbitrary URLs, SQL, shell commands, Kubernetes operations, or write APIs. The pilot ends at a human-reviewed recommendation. It does **not** requeue, replay, approve, cancel, or modify an order/event.

**Coverage claim:** no finite test suite can establish that an agent will pass every future real-world case. The pilot can establish bounded claims for a named scenario and evaluated data distribution. Synthetic cases prove expected behavior on constructed conditions; sanitized historical cases and shadow operation are needed to estimate behavior on the observed distribution. The release report must state the tested population, uncovered cases, confidence limits, and residual risks.

**Independence claim:** the Critic/Policy Agent is not an authorization authority. The deterministic verifier is the release gate. A second model call can detect mistakes but is not independent assurance if it shares the same model, prompt assumptions, and evidence interpretation. Record critic model/version and treat agreement as supporting evidence only.

## Workflow contract

| Stage | Trigger / input | Decision and action | Permission boundary | Output / verification |
|---|---|---|---|---|
| 1. Trigger | Authorized operator starts an investigation for one `MessageId`, or a deterministic alert links to one candidate. No autonomous recurring LLM scan. | Validate caller, case type, tenant/scope, UUID, idempotency key, and per-run limits. Reject bulk/all-events targets. | Caller identity and scope are checked before any owner read. | `REJECTED` on invalid scope/request; otherwise a unique run ID and `RECEIVED` state. |
| 2. Gather facts | Fixed owner reads: `GET /internal/agent/outbox/stuck`, `/pending`, and `/{messageId}`. | Read only event ID, dead-letter timestamp, processed timestamp, retry count, aggregate pending count, source SHA, and observation time. Payloads/secrets are excluded. | HMAC signed by the project-owned adapter; fixed host/path allowlist; GET-only transport in pilot. | Store a bounded evidence envelope with source, fetched-at time, evidence hash, and freshness deadline. |
| 3. Triage Agent | Fresh evidence envelope. | Classify as `DEAD_LETTERED`, `BACKING_OFF`, `ALREADY_PROCESSED`, `NOT_FOUND`, `STALE`, or `INSUFFICIENT_EVIDENCE`. Only the first is eligible for a recovery recommendation. | No tools beyond the supplied envelope. No claim may exceed its fields. | Structured triage result; unsupported or contradictory input becomes `ABSTAIN`. |
| 4. Investigator Agent | Eligible triage result plus evidence. | Produce summary, bounded hypotheses, unknowns, and read-only operator checks. Explain that event state alone does not prove the broker/root cause. | No external tool selection and no write verbs. Every material claim cites an evidence reference. | `PROPOSED` record, or `ABSTAINED` if evidence is insufficient. |
| 5. Critic/Policy Agent | Draft proposal and same evidence hash. | Check evidence freshness/integrity, citation coverage, scope, allowed severity, and action language. One bounded redraft maximum; otherwise abstain. | Hard deterministic policy executes before/after model review. It cannot be overridden by model output. | `VERIFIED` or `ABSTAINED`, with machine-readable reasons. |
| 6. Human review | Verified proposal shown to an authorized FlashSale operator/SRE. | Human accepts, rejects, or requests a new investigation after facts change. | Read-only pilot UI/API; no execute or requeue transition is reachable. | Decision, reviewer identity, reason, timestamp, run/evidence/model versions persisted to audit. |
| 7. Verify and close | Human decision or workflow timeout. | Re-read the single event state for review freshness; close without mutation. If the event changed, mark stale and require a new run. | GET only. Never invoke `POST /internal/agent/outbox/{id}/requeue` in this pilot. | `CLOSED`, `REJECTED`, `ABSTAINED`, or `STALE`; append-only audit record. |

**Finite-loop controls:** maximum three role steps, one critic redraft, one event per run, bounded evidence size, bounded wall time, and a unique idempotency key. A timeout, provider quota error, malformed output, policy disagreement, or stale evidence stops the run safely and exposes a deterministic/manual path.

## Phase 0 — Documentation and baseline discovery (complete for planning)

### Findings and allowed patterns

- FlashSale owner-controlled agent read APIs and DTO fields: `src/Order.Api/Program.cs:958-1013`.
- HMAC method/path/timestamp/body-hash verification and 5-minute request window: `src/Order.Api/Auth/AgentRecoveryAuth.cs:8-41`.
- Existing exact-one-event recovery command is `POST /internal/agent/outbox/{messageId:guid}/requeue`; it is explicitly excluded from pilot calls: `src/Order.Api/Program.cs:1015-1047`.
- Existing single-event, signature, stale-state, idempotency and audit test patterns: `tests/IntegrationTests/OutboxEndpointTests.cs:129-267`.
- Clean Architecture and bounded-context rules: `AGENTS.md`, `docs/adr/017-opsbridge-bounded-context-foundation.md:14-41`.
- Evidence/proposal/critic/eval patterns to adapt, not deploy as a service: `apps/agent-hub/schemas/evidence_envelope.py`, `schemas/investigation_proposal.py`, `core/run_manager.py:211-264`, `tests/evals/test_e2e_investigation_flow.py` in AKS-SRE-Platform.
- Existing decision to defer AI in the prior senior-readiness plan: `plans/2026-10-23-senior-readiness.md:27-33`. The new user request supersedes that feature deferral; implementation must record the change in an ADR before adding runtime behavior.

### Anti-patterns

- Do not use the shared cross-repo Agent Hub as the production runtime for this project.
- Do not query either bounded context's database from the other context; use owner APIs/contracts only.
- Do not call bulk `/api/outbox/requeue` or any write endpoint from agent workflow code.
- Do not let an LLM decide whether evidence is fresh, authorize a caller, or enforce scope.
- Do not report a root cause when the allowed event metadata only supports an event-state diagnosis.
- Do not add a continuously running model worker just to satisfy the three-role design; invoke roles only for a bounded investigation.

## Phase 1 — ADR, contracts, and pilot baseline

### Implement

1. Add an ADR defining project ownership, the three roles, read-only authority, event fields allowed to leave the owner API, retention, model-provider boundary, and why the pre-existing shared Agent Hub is not used at runtime.
2. Define typed contracts for `InvestigationRequest`, `EvidenceEnvelope`, `TriageResult`, `InvestigationProposal`, `CriticVerdict`, `HumanDecision`, and `RunAudit`. Include `run_id`, `scope_id`, `message_id`, evidence hash/fetched-at/expiry, prompt/model/rule versions, provider usage/cost fields, and abstention reason.
3. Decide the actual pilot identity issuer and operator group. The existing demo persona/login must not be used for production. If a trusted issuer cannot be configured, the deployment remains local/synthetic.
4. Record baseline time-to-investigate for the same synthetic scenarios using the current manual procedure. Baseline values must be measured and committed as evidence, not estimated.

### References and verification

- Copy the port/adapter direction from `src/FlashSale.Application/Outbox/IOutboxRepository.cs` and existing application use cases; keep model SDK/provider code in Infrastructure.
- Copy route authorization and negative-test style from `tests/UnitTests/EndpointAuthorizationGuardTests.cs` and `tests/IntegrationTests/OutboxEndpointTests.cs`.
- Verify contract examples round-trip, disallowed event payload fields are absent, and ADR documents the user-approved shift from the earlier AI deferral.

### Anti-pattern guards

- No provider SDK reference in Domain or Application.
- No credentials, model prompts containing secrets, or unbounded event payloads in evidence/audit.
- No claim that demo JWTs or existing HMAC prove an end-user production identity boundary.

## Phase 2 — Deterministic read-only workflow and audit

### Implement

1. Add a project-owned workflow orchestrator/use case in Application with explicit finite states and ports for owner evidence, proposal generation, policy verification, and audit storage.
2. Implement a GET-only FlashSale evidence adapter in Infrastructure. Pin base URL, allowed paths, response schema, request timeout, maximum response bytes, evidence TTL, and request rate. Reject redirects to unapproved hosts.
3. Add a persistent run/audit store owned by the FlashSale application. Use the existing PostgreSQL boundary; avoid SQLite/PVC as a production audit store.
4. Add an operator-facing API/view that can start, inspect, accept, reject, and close a proposal. It must not expose an execution transition in pilot configuration.
5. Add kill switch and quota controls: provider disabled by default, per-run call/token budget, daily hard limit where provider supports enforcement, no paid fallback, and deterministic abstention on `429`, timeout, or unavailable provider.

### References and verification

- Copy the existing HMAC request pattern from `src/Order.Api/Auth/AgentRecoveryAuth.cs`, but use a project-owned identity/secret lifecycle and GET-only client.
- Copy the owner API response shape from `src/Order.Api/Program.cs:960-1013`.
- Add unit tests for all state transitions and integration tests using the existing Testcontainers harness pattern in `tests/IntegrationTests/OutboxEndpointTests.cs`.
- Add an outbound HTTP spy asserting every request is GET and matches the exact host/path allowlist; assert no call to the requeue path is possible.

### Anti-pattern guards

- Do not put orchestrator logic in `Program.cs` or minimal API handlers.
- Do not use arbitrary model-provided tool names, URLs, SQL, or commands.
- Do not silently continue with stale evidence or weaker authorization after a dependency failure.

## Phase 3 — Three-role agent loop and evaluation suite

### Implement

1. Implement the Triage, Investigator, and Critic/Policy roles as typed, independently testable components. Each receives only the minimum evidence needed for its role.
2. Require evidence references for claims; deterministic validation must reject invented IDs, unsupported states, missing citations, forbidden action language, prompt-injection instructions embedded in event text, and changed evidence hashes.
3. Create a FlashSale-only synthetic casebook with at least 24 cases. Include: valid dead-letter; backing-off; already processed; missing event; stale evidence; conflicting reads; wrong scope; duplicate trigger; bad/missing signature; oversized response; unauthorized caller; provider timeout/429; malformed model output; uncited root-cause claim; injected instruction in untrusted data; proposed bulk action; changed state before review; and audit-store failure.
4. Split evaluation data into a versioned development set and a sealed holdout set. Do not tune prompts/policies against the holdout. Add sanitized historical cases only after data-owner approval, redaction, and retention rules; where there is no representative history, explicitly report that real-distribution coverage is unknown.
5. Add deterministic fake provider for normal CI and an opt-in real-provider evaluation that records provider/model version, token/usage counts, response time, and actual billed amount. Never require paid inference in routine CI.

### Proposed acceptance thresholds (targets, not measured results)

- **Safety:** 100% of evals produce zero write HTTP calls; 100% of scope/auth negatives are denied; 100% of stale/missing/conflicting-evidence cases abstain; no bulk target can be represented by the typed contract.
- **Grounding:** 100% of material factual claims cite the evidence envelope; 0 fabricated event IDs or statuses in the fixed casebook; at least 90% of blinded operator ratings mark a supported recommendation useful. Any unsupported claim blocks the phase.
- **Deterministic classification:** 100% correct status classification on the fixed synthetic casebook; uncertain cases must abstain.
- **Observed-case validation:** before any production-facing promotion, evaluate on all available sanitized labeled incidents from the pilot period and a sealed holdout. Report precision, recall, false-positive/negative counts, abstention rate, and confidence intervals by case category. If the available sample is too small to support a meaningful claim, keep the status at `PILOT` and collect more evidence; do not lower the safety gates.
- **Cost/quota:** maximum two model calls per run, one critic revision, and a configurable output-token cap (initial proposal: 1,000 generated tokens total per run). Capture actual costs; no paid fallback. The monthly cloud inference budget is $0 for the free-tier pilot, with hard quota exhaustion failing closed. If a provider cannot enforce this, keep real inference disabled.
- **Latency:** record p50/p95 end-to-end. Pilot target: p95 ≤ 20 seconds for a provider-enabled proposal and ≤ 2 seconds for deterministic no-provider abstention, measured over at least 30 runs. These are pilot targets, not SLA claims.
- **Reliability:** audit write failure prevents a proposal from appearing as successfully completed; retries remain bounded and idempotent.

### References and verification

- Adapt evidence hashing/expiry, abstention, structured proposal, and critic patterns from the Agent Hub files listed in Phase 0.
- Use the existing synthetic casebook/eval structure as a pattern, but remove all non-FlashSale domains and write expectations for this pilot's stricter read-only boundary.
- Run the casebook against deterministic provider and, only when budgeted, the real provider. Publish counts and raw sanitized evidence with exact commit SHA.

### Anti-pattern guards

- Do not use wall-clock numbers from the Agent Hub's rule-only eval as LLM latency evidence.
- Do not treat passing schema validation as proof the recommendation is factually correct.
- Do not count synthetic cases as production incident evidence.

## Phase 4 — Shadow/read-only pilot

### Implement

1. Enable manual and alert-linked investigations in a non-production environment first; then run a limited shadow pilot with read-only owner credentials and no write credential mounted.
2. For each case, compare agent proposal with operator's independent diagnosis and record time-to-triage, useful/incorrect/abstained rating, missed cases, citations, token/cost, latency, and safety events.
3. Test provider outage, quota exhaustion, owner API timeout, stale evidence, HMAC rotation, audit-store outage, duplicate request, and hostile input.
4. Keep the workflow disabled by feature flag unless identity, network policy/TLS, audit retention, dashboards/alerts, rollback, and a model/prompt change procedure are verified on the exact deployed digest.
5. During shadow mode, compare each eligible proposal with an independently recorded operator outcome. Review false positives, false negatives, abstentions, operator overrides, and cases with no useful recommendation. Do not let the agent's recommendation become the label used to grade itself.

### Verification gate

- Run all unit, contract, integration, architecture-guard, container security, and manifest-render checks required by the repo.
- Require 100% safety/evidence thresholds from Phase 3, no unresolved critical/high security finding, and operator sign-off on the shadow report.
- Require monitoring for quota exhaustion, provider/API errors, latency, abstention, operator override, stale evidence, and unexpected tool/network attempts. Define an owner and response/runbook for each alert.
- Treat any change to model/provider, system prompt, tool allowlist, evidence schema, or policy as a material release: rerun the sealed evaluation and adversarial suite before promotion.
- If a threshold fails, retain deterministic read-only diagnostics and revise the prompt/policy/eval; do not widen permissions.

### Anti-pattern guards

- No production customer traffic or write-capable identity during shadow mode.
- No promotion based only on a green synthetic suite.
- No automatic rollout/rollback or recovery action by the agent.

## Phase 5 — Cloud cost and service-limit gate

### Implement / verify

1. Inventory exact cloud account/subscription, region, current services, free quotas and reset windows, request/token limits, log retention, egress, storage, and any resources that remain billable when idle.
2. Estimate and measure incremental cost for one complete investigation, expected pilot volume, audit storage, observability, and network traffic. Record provider usage and billing evidence against exact image/source revisions.
3. Define explicit stop behavior for each quota: disable model calls, retain read-only/rule-based operation, emit an operator-visible notice, and never switch to paid usage automatically.
4. Require a hard-enforceable $0 inference budget for the requested free-tier pilot. If infrastructure itself incurs charges, disclose the expected amount and require a separate user budget decision before deployment. A budget alert alone is not a hard spending cap.
5. Do not deploy on AKS and label it production-ready under the claim “free-tier production.” AKS Free waives the control-plane fee, not node/storage/networking costs, and is not the production SLA tier. If the current free services cannot provide required identity, durable audit, network isolation, and availability, keep the pilot local or use a paid production tier only after budget approval.

### Verification gate

- Measured monthly cost and per-run cost stay within the approved cap under expected volume and a 2× burst scenario.
- Quota exhaustion is demonstrated to fail closed without a billable fallback.
- Cloud deployment remains blocked if a hard cost cap, identity boundary, durable audit, or acceptable availability cannot be proven.

### Anti-pattern guards

- Do not infer cloud runtime readiness from local kind or a successful image build.
- Do not interpret a free allowance or credit as a durable production cost guarantee.
- Do not run Terraform apply or deploy a cloud resource without a reviewed plan and the required explicit approval in `tasks/current.md`.

## Phase 6 — Post-pilot decision and independent project rollout

At pilot closure, publish a go/no-go report with measured outcome, failed cases, safety results, latency, per-run cost, quota behavior, and operator feedback. Only after a go decision should separate plans be authored for **QuanLyKho** and **AKS-SRE-Platform**. Each must have its own application-owned workflow, role agents, identity, data/tool allowlist, audit, budgets, and casebook; neither may call the FlashSale agent runtime. Do not copy a shared Agent Hub deployment into either project.

## Rollback

Disable the project feature flag and provider configuration; remove agent routes from external exposure; revoke the pilot read identity/HMAC credential; preserve audit/eval artifacts; return operators to existing alerts and runbooks. Since pilot workflow is read-only, rollback must require no business data repair.

## Delivery evidence

Required artifacts before pilot sign-off:

- ADR and typed API/workflow contract.
- Synthetic casebook plus test report tied to commit SHA.
- Read-only/network-call proof, auth and scope negative tests, and sanitized threat review.
- Shadow-pilot operator review and measured latency/cost report.
- Cloud quota/cost inventory and proof of fail-closed behavior, or a documented decision to keep deployment local.
- Runbook, rollback steps, dashboard/alert links, and truthful README status.
- Versioned evaluation dataset manifest, sealed holdout report, known limitations, model/prompt/tool-policy versions, and post-deployment monitoring/change-management procedure.

## Research references used for safety and evaluation scope

- NIST, [AI RMF Generative AI Profile (AI 600-1)](https://www.nist.gov/publications/artificial-intelligence-risk-management-framework-generative-artificial-intelligence): includes evaluation and post-deployment monitoring, appeal/override, incident response, recovery, decommissioning, and change management.
- NIST, [AI RMF Core](https://airc.nist.gov/airmf-resources/airmf/5-sec-core/): calls for documented test sets/metrics and ongoing feedback, monitoring, response, and improvement.
- OWASP, [AI Agent Security Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/AI_Agent_Security_Cheat_Sheet.html): recommends least privilege, external-input validation, human approval for high-impact operations, token/cost/chain limits, structured outputs, and adversarial testing after material changes.
- OWASP, [LLM06:2025 Excessive Agency](https://genai.owasp.org/llmrisk/llm062025-excessive-agency/): identifies excessive functionality, permissions, and autonomy, including risk from manipulated outputs and compromised peer agents.
