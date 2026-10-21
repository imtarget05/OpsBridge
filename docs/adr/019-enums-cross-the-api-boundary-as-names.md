# ADR-019: Enums cross the API boundary as names, not ordinals

- **Status**: Accepted
- **Date**: 2026-10-20
- **Supersedes**: nothing. Extends ADR-017 (OpsBridge bounded context) and is
  independent of ADR-018 (migration strategy).

## Context

The browser journey in `src/OpsBridge.Web/e2e/` is the first test that drives
`OpsBridge.Api` from a click to a row in PostgreSQL. Its first run failed on the
invoices screen — not because of the test, but because the screen **crashed**:

```
[PAGE ERROR] status?.toLowerCase is not a function
```

`src/OpsBridge.Web/src/components/ui/Badge.tsx:29` calls `status?.toLowerCase()`
on the invoice `status` it receives. The API was serialising that enum as an
**ordinal**:

```json
{ "invoiceNumber": "INV-…", "status": 1, "lines": [ … ] }
```

With an empty database the page never rendered a badge, so nothing was ever
exercised. As soon as a single invoice existed, `status` was a number,
`.toLowerCase()` threw, React unmounted the view, and `/invoices` and
`/reconciliation` rendered nothing at all.

The mismatch was not one-directional. The web client was already written against
**names** in both directions:

- its types are string-literal unions (`src/OpsBridge.Web/src/types/index.ts`),
  and 6 of the 7 unions match the API enums member-for-member;
- the pages compare with string literals (`d.status === 'Open'`,
  `i.status === 'PendingReconciliation'`, `reconResult.status === 'Matched'`);
- the client **sends** names — `{"action":"ApprovedWithTolerance"}`,
  `{"ruleType":"SkuAlias"}` — and the API could not bind them: the request
  failed with `400 JsonException: The JSON value could not be converted to
  …CreateMappingRuleRequest. Path: $.ruleType`.

So the API was the outlier, and the damage was wider than the crash. Under the
ordinal contract **every** web-side status comparison was silently false: the
discrepancy KPI cards were permanently zero, the reconciliation workspace never
auto-selected a pending invoice, and status badges were styled from the wrong
value. Nothing failed; the UI just under-reported.

## Decision

**Enums cross the API boundary as their names.**

- `src/OpsBridge.Api/OpsBridgeJsonContract.cs` (new) owns a single
  `JsonSerializerOptions` configured with `JsonStringEnumConverter`, and
  `src/OpsBridge.Api/Program.cs` applies it to the pipeline through
  `ConfigureHttpJsonOptions` — globally, not per DTO. A new enum therefore
  cannot ship as an ordinal by omission: there is one place to forget, and it
  fails the first time a client reads it.
- The integration-test client binds with the **same** contract
  (`tests/OpsBridge.IntegrationTests/OpsBridgeJsonExtensions.cs`), so the tests
  and the server can never drift apart again.

Compatibility, measured:

| Direction | Before | After |
|---|---|---|
| Response | `"status": 1` | `"status": "PendingReconciliation"` |
| Request, name form (`{"ruleType":"SkuAlias"}`) | `400` | `201` |
| Request, ordinal form (`{"ruleType":1}`) | `201` | `201` |

`JsonStringEnumConverter` reads names **and** ordinals, so existing callers that
send ordinals keep working. Callers that **read** the response see a different
shape; that is a breaking change to a public surface and is the reason this ADR
exists.

## Consequences

- The invoices and reconciliation screens render with data present, and the
  status-based UI logic works as written.
- The browser suite can now assert what the API reports: the smoke spec seeds
  through the product's own API and compares the rendered count against the live
  count, so a screen showing a wrong number fails instead of passing vacuously.
- **Breaking for external consumers.** Any consumer outside this repository that
  parses `OpsBridge.Api` responses must now expect names. The API has no version
  prefix, so the change was delivered with a commit that states it in the message
  body and this ADR — not silently.
- The integration tests were **not** weakened: 15 deserialisation call sites
  changed and **no assertion** was added, removed or relaxed. The six failures
  seen mid-change were the client using the old default options, not the
  assertions being wrong.

## Alternatives rejected

- **Make the UI tolerate both shapes** (`String(status).toLowerCase()`). It
  would have fixed the crash in one line, but it leaves the request direction
  broken (the client sends names the server cannot bind), keeps the OpenAPI
  document wrong, and preserves a contract in which two representations of one
  value exist. The client was already correct; the server was not.
- **Change only the DTOs that the UI reads.** Six enums are exposed today, and
  the seventh would be forgotten. A global converter makes the rule a property
  of the pipeline rather than of anyone's memory.
- **Leave it and let the ordinal contract stand.** Then the UI's status
  comparisons remain silently false. The crash is the visible symptom; the
  under-reporting is the real defect.

## Known follow-up (not fixed here)

`ReviewAction` is declared in the web client as
`'ApprovedWithTolerance' | 'Adjusted' | 'Rejected' | 'HoldForInvestigation'`
(`src/OpsBridge.Web/src/types/index.ts:117`), and the discrepancies screen
offers **"HoldForInvestigation (Keep open for vendor inquiry)"** as a choice
(`src/OpsBridge.Web/src/features/discrepancies/DiscrepanciesPage.tsx:363`).
The server enum has only three members
(`src/OpsBridge.Domain/Reconciliation/Discrepancy.cs:47-52`) — there is no
`HoldForInvestigation`. Selecting that option therefore cannot bind and is
rejected with `400`.

This is a **pre-existing** client/server mismatch, not a regression from this
ADR (an unknown name still fails to bind). It is deliberately not fixed here
because the right answer is a domain decision — either the review action exists
server-side and the handler must implement it, or the client must not offer it —
and that decision belongs to the bounded context, not to a JSON contract ADR.
