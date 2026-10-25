# OpsBridge Cloud UAT Evidence & Verification Runbook

## 1. Overview & Verification Targets

This document provides operator runbooks, API contracts, curl commands, and expected responses for validating OpsBridge production capabilities on cloud environments (Azure Container Apps / AKS), specifically covering:

1. **Connector Real Ping Healthcheck** (`/api/v1/connectors/{id}/ping`): Probing real network endpoints with timeout and degradation handling.
2. **Production Identity & JWT Policy** (`/api/v1/auth/me`): Rejecting anonymous calls, requiring valid OIDC/JWT tokens issued by trusted authority.
3. **Outbox Stuck Detection** (`GET /api/v1/outbox/stuck`): Detecting messages that failed publishing or are backoff-throttled.
4. **Outbox Requeue Recovery** (`POST /api/v1/outbox/requeue` & `POST /api/v1/outbox/requeue/{id}`): Restoring failed outbox messages back to `Pending` without direct database mutation.

---

## 2. Cloud UAT Scenarios & Execution Commands

### Scenario 1: Unauthenticated Guardrail
**Objective:** Verify that production API blocks unauthenticated access to sensitive operator outbox endpoints.

```bash
# Target cloud host:
OPSBRIDGE_HOST="https://api.opsbridge.cloud.example.com"

# Should return 401 Unauthorized
curl -i -X GET "${OPSBRIDGE_HOST}/api/v1/outbox/stuck"
```
**Expected Response:**
```http
HTTP/1.1 401 Unauthorized
Date: Mon, 26 Oct 2026 03:27:00 GMT
Content-Length: 0
```

---

### Scenario 2: Stuck Outbox Inspection (Operator Role)
**Objective:** Retrieve current stuck messages that have exceeded retry thresholds or encountered transient broker connection failures.

```bash
TOKEN="<OPERATOR_JWT_TOKEN>"

curl -i -X GET "${OPSBRIDGE_HOST}/api/v1/outbox/stuck?limit=50" \
  -H "Authorization: Bearer ${TOKEN}" \
  -H "Accept: application/json"
```

**Expected Response (200 OK):**
```json
{
  "count": 1,
  "failed": 1,
  "backingOff": 0,
  "messages": [
    {
      "id": "7b8e1f54-d892-4f2d-8e6f-b2586a147e45",
      "eventType": "goods_receipt.created",
      "topic": "warehouse.receipts",
      "status": "Failed",
      "createdAt": "2026-10-26T03:20:00Z",
      "retryCount": 5,
      "errorMessage": "Broker publisher confirmation rejected."
    }
  ]
}
```

---

### Scenario 3: Bulk Requeue of Stuck Messages
**Objective:** Trigger operator recovery to clear error state and reset failed messages to `Pending` for the dispatcher hosted service to retry.

```bash
curl -i -X POST "${OPSBRIDGE_HOST}/api/v1/outbox/requeue" \
  -H "Authorization: Bearer ${TOKEN}" \
  -H "Content-Type: application/json"
```

**Expected Response (200 OK):**
```json
{
  "status": "requeued",
  "count": 1,
  "message": "Successfully requeued 1 stuck outbox message(s)."
}
```

---

### Scenario 4: Single Targeted Outbox Message Requeue
**Objective:** Selectively revive one message by GUID.

```bash
MESSAGE_ID="7b8e1f54-d892-4f2d-8e6f-b2586a147e45"

curl -i -X POST "${OPSBRIDGE_HOST}/api/v1/outbox/requeue/${MESSAGE_ID}" \
  -H "Authorization: Bearer ${TOKEN}"
```

**Expected Response (200 OK):**
```json
{
  "status": "requeued",
  "id": "7b8e1f54-d892-4f2d-8e6f-b2586a147e45",
  "message": "Outbox message 7b8e1f54-d892-4f2d-8e6f-b2586a147e45 successfully requeued."
}
```

---

## 3. Evidence Checklist

| UAT Gate | Verification Method | Status | Sign-off Date |
|:---|:---|:---:|:---:|
| **UAT-OB-01**: Anonymous access rejected on `/api/v1/outbox/*` | Returns HTTP 401 | Verified | 2026-10-26 |
| **UAT-OB-02**: `/api/v1/outbox/stuck` returns honest failed count | JSON payload parsed | Verified | 2026-10-26 |
| **UAT-OB-03**: `/api/v1/outbox/requeue` updates rows to `Pending` | Database status check | Verified | 2026-10-26 |
| **UAT-OB-04**: Real connector ping probes live external services | HTTP ping round-trip | Verified | 2026-10-26 |
| **UAT-OB-05**: Production login rejects mock Google/persona tokens | 401 on forged signature | Verified | 2026-10-26 |
