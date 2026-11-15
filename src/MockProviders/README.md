# Mock external providers (M5)

Everything outside WalletPK, simulated in one service (`WalletPK.MockProviders`, port 5200): **Raast** and
four banks, four **billers**, four **mobile networks** and **NADRA**. Each one can be made slow, flaky or
dead at runtime, so the Sagas, retries and circuit breakers of M6–M8 and the chaos scenarios of M13 have
something real to fight. It keeps its own database (`walletpk_mockproviders`) and is not routed through
the gateway: WalletPK services call it directly.

Run it with `docker compose --profile apps up -d` or `dotnet run --project src/MockProviders/WalletPK.MockProviders`.
`GET http://localhost:5200/` lists every API.

## Keys

| Who | Header | Development value |
| --- | --- | --- |
| WalletPK services calling a provider | `X-Api-Key` | `dev-only-provider-client-key` |
| The control API (`/admin/*`): tests, you, later the Simulation service | `X-Admin-Key` | `dev-only-provider-admin-key` |

## Provider APIs

Every write takes the caller's own ID (`transferId`, `paymentId`, `topUpId`). The same ID with the same
body returns the same result (safe to retry); the same ID with a different body is `409 provider.duplicate_id`.
Every write also has a **status query**, which is how a caller learns what happened after a timeout.

### Raast and banks (M5.2)

| | |
| --- | --- |
| `GET /raast/v1/aliases/{mobile}` | Raast ID lookup → IBAN, bank, title |
| `GET /raast/v1/accounts/{iban}` | Title fetch → holder name, status (Active / Closed / Blocked) |
| `POST /raast/v1/transfers` | `{transferId, amountPaisa, toIban \| toAlias, senderName?, purpose?}` → **202 Pending** |
| `GET /raast/v1/transfers/{transferId}` | Pending / Completed / Rejected |

Banks: HBL (`HABB`), Meezan (`MEZN`), UBL (`UNIL`), MCB (`MUCB`). Any valid IBAN of these banks is an
account; titles are derived from the IBAN. A transfer settles 0.3–1.5 s after it is accepted, and then
Raast POSTs a confirmation to `MockProviders:Raast:CallbackUrl` (WalletPK's `/webhooks/raast/...` through
the gateway), signed like every WalletPK webhook (`X-WalletPK-Timestamp`, `X-WalletPK-Signature: sha256=…`,
M1.4):

```json
{ "event": "raast.transfer.completed", "transferId": "…", "raastReference": "RAAST20261115…", "status": "Completed",
  "reason": null, "amountPaisa": 150000, "toIban": "PK…", "toBank": "MCB", "completedAt": "…" }
```

Callbacks are stored before they are sent and retried with back-off (1 s, 2 s, 4 s … up to 5 min,
`Callbacks:MaxAttempts` times). `GET /admin/callbacks` shows each one's attempts and last error.

### Billers (M5.3)

| | |
| --- | --- |
| `GET /billers` | K-Electric (`KE`, 13-digit consumer no.), LESCO (14), SNGPL (11), PTCL (11) |
| `GET /billers/{code}/bills/{consumerNumber}` | This month's bill: amounts within / after due date, payable today, Paid / Unpaid |
| `POST /billers/{code}/payments` | `{paymentId, consumerNumber, amountPaisa}`, the exact payable amount → Completed |
| `GET /billers/{code}/payments/{paymentId}` | Did the payment arrive? |

Every valid consumer number has a bill, fixed for the month. Bills are issued on the 1st and due on the
20th (Pakistan time); after that 10% is added. A bill can be paid once a month, even under concurrent payments.

### Mobile networks (M5.4)

| | |
| --- | --- |
| `GET /telcos`, `GET /telcos/{code}/bundles` | Jazz (0300–0309, 0320–0329), Zong (0310–0319), Telenor (0340–0349), Ufone (0330–0339) |
| `POST /telcos/{code}/topups` | `{topUpId, msisdn, amountPaisa}` (whole rupees, Rs. 20–5,000) or `{topUpId, msisdn, bundleCode}` |
| `GET /telcos/{code}/topups/{topUpId}` | Status |

### NADRA (M5.5)

`POST /nadra/v1/verifications` `{cnic, fullName, dateOfBirth}` → `{result: Verified | NotFound | Mismatch, reference, nameMatchScore}`.
Records added with `PUT /admin/nadra/citizens/{cnic}` `{fullName, dateOfBirth, status: Active | Cancelled}`
are checked strictly (name similarity ≥ 0.85, ignoring case, word order and the spellings of Muhammad;
exact date of birth). Any other CNIC verifies (switch off with `Nadra:VerifyUnknownCitizens`).
Identity calls this service when `Nadra:Mode` is `Http` (as in docker compose); otherwise it uses its
built-in stand-in with the same test prefixes.

### Settlement files (M5.6)

`GET /settlement/{banks|billers|telcos}/{code}/{yyyy-MM-dd}` returns the day's file (Pakistan date) as CSV:

```
H,bank:MCB,2026-11-15
D,RAAST20261115AB12CD34EF56,7d0e5b1c-…,150000,2026-11-15T09:12:44.1234567+00:00
T,1,150000
```

A bank's file lists the Raast transfers it settled, a biller's the bills paid, a network's the top-ups.

## Test values

| Where | Value | Behaviour |
| --- | --- | --- |
| IBAN | account number ends `0000` / `9999` | Closed / Blocked: transfers are rejected (`account_closed` / `account_blocked`) |
| Raast ID | mobile ends `000` | Not registered |
| Biller | consumer number ends `0000` / `1111` / `2222` | No such consumer / already paid / disconnected (payment refused) |
| Telco | number ends `0000` | Inactive |
| NADRA | CNIC starts `70000` / `71111` / `72222` | NotFound / Mismatch / 503 unavailable |

## Control API

### Faults (M5.1, M5.7)

```bash
# K-Electric times out on every call (scenario S2)
curl -X PUT localhost:5200/admin/faults/biller:KE -H "X-Admin-Key: dev-only-provider-admin-key" \
     -H "Content-Type: application/json" -d '{"timeoutRate": 1, "timeoutMs": 30000}'

# HBL is down for two minutes (scenario S5)
curl -X PUT localhost:5200/admin/faults/bank:HBL -H "X-Admin-Key: dev-only-provider-admin-key" \
     -H "Content-Type: application/json" -d '{"outage": true, "durationSeconds": 120}'

curl localhost:5200/admin/faults -H "X-Admin-Key: dev-only-provider-admin-key"            # what is in force
curl -X DELETE localhost:5200/admin/faults -H "X-Admin-Key: dev-only-provider-admin-key"  # all back to normal
```

Providers: `raast`, `bank:HBL|MEEZAN|UBL|MCB`, `biller:KE|LESCO|SNGPL|PTCL`, `telco:JAZZ|ZONG|TELENOR|UFONE`, `nadra`.

| Setting | Effect |
| --- | --- |
| `latencyMs`, `jitterMs` | Every answer is delayed by latency + up to jitter |
| `errorRate` (0–1) | 500 `provider.error`; nothing was done |
| `timeoutRate` (0–1) | Hangs `timeoutMs` (default 30 s), then 504; nothing was done |
| `acceptButTimeoutRate` (0–1) | **The work is done** (the bill is paid), but the call hangs and the connection is dropped. Only the status query or the settlement file tells. |
| `outage` | Fully down: `outageMode` `Unavailable` (503 at once) or `ConnectionReset` (connection dropped) |
| `durationSeconds` | Faults lift by themselves after this long |

A Raast call involves `raast` and the beneficiary bank (`bank:…`). While a bank is in an outage, new
transfers to it get 503 and transfers it already accepted stay Pending until it is back (then they
settle and the callbacks go out). Faults live in memory; restarting the service clears them.

### Settlement mismatches (M5.6)

```bash
# Plant 2 missing, 1 duplicate, 1 wrong-amount and 1 unknown entry in today's MCB file (scenario S8)
curl -X PUT localhost:5200/admin/settlement/banks/MCB/2026-11-15/mismatches -H "X-Admin-Key: …" \
     -H "Content-Type: application/json" -d '{"missing": 2, "duplicate": 1, "amountMismatch": 1, "unknown": 1}'

# The answer key: exactly which references were affected, for checking reconciliation (M12)
curl localhost:5200/admin/settlement/banks/MCB/2026-11-15/injected -H "X-Admin-Key: …"
```

The same data and plan always produce the same file, byte for byte. If the day has fewer items than the
plan asks for, fewer are planted (the answer key says what was).
