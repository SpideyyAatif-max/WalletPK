# WalletPK

[![CI](https://github.com/SpideyyAatif-max/WalletPK/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/SpideyyAatif-max/WalletPK/actions/workflows/ci.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

**Event-driven mobile wallet and payments platform built on .NET 10 microservices, with a double-entry ledger, Saga-orchestrated payments, a Transactional Outbox, CQRS, real-time fraud detection and a chaos-testing simulator.**

WalletPK is a distributed mobile wallet and payments platform modelled on JazzCash and Easypaisa. ASP.NET Core (.NET 10) microservices behind a YARP API gateway keep an immutable double-entry ledger in PostgreSQL, so money is never created or lost. Concurrent debits are made safe by Redis distributed locks, optimistic concurrency and database constraints, and idempotency keys stop duplicate requests. Multi-step payments to external banks, billers and telcos run as orchestrated Sagas with Polly retries, circuit breakers and automatic compensating refunds. A Transactional Outbox publishes every ledger change to Kafka, feeding CQRS read models, a real-time fraud engine and SignalR live updates. A Next.js frontend provides a customer wallet, an operations and compliance console, and a Simulation Control Center that drives thousands of virtual users and injects failures to prove correctness under load.

> **Status:** under active development. Progress is tracked module by module in [`ROADMAP.md`](ROADMAP.md).

---

## Features

- **Payments:** wallet-to-wallet transfers, Raast bank transfers, utility bill payments, mobile top-ups, merchant QR payments, agent cash-in / cash-out
- **Ledger integrity:** append-only double-entry ledger, holds, idempotency keys, hash-chained audit log, invariant checks
- **Resilience:** orchestrated Sagas with compensation, retries, circuit breakers, dead-letter queues, reconciliation
- **Security:** OTP + MPIN, rotating refresh tokens, device binding, tiered KYC limits, field-level encryption, real-time fraud rules
- **Simulation:** up to 10,000 virtual users, runtime fault injection and eight real-world scenarios with automatic pass/fail checks

## Tech Stack

| Layer | Technology |
| --- | --- |
| Backend services | ASP.NET Core (.NET 10), gRPC, Clean Architecture |
| API gateway | YARP |
| Database | PostgreSQL 17, EF Core (writes), Dapper (reads) |
| Cache and locks | Redis 7 |
| Event streaming | Apache Kafka (KRaft) |
| Sagas and resilience | Own Saga engine (Postgres + outbox + Kafka), Polly |
| Real-time | SignalR |
| Frontend | Next.js, TypeScript, Tailwind CSS, shadcn/ui |
| Observability | OpenTelemetry, Jaeger, Prometheus, Grafana, Seq |
| Testing | xUnit, FsCheck, Testcontainers, k6 |
| Runtime | Docker Compose |

## Repository Layout

```text
WalletPK/
├── WalletPK.sln
├── src/
│   ├── BuildingBlocks/   # shared libraries: common types, messaging, web, observability, contracts
│   ├── Gateway/          # YARP API gateway
│   ├── Services/         # Identity, Ledger, Payments, Bills, TopupMerchant, Agents, Fraud, Notifications, Reconciliation, Simulation
│   └── MockProviders/    # simulated Raast, banks, billers, telcos and NADRA
├── web/                  # Next.js app: /wallet, /ops, /sim
├── tests/                # unit, integration, architecture and contract tests
├── load/                 # k6 load-test scripts
├── ops/                  # Prometheus and Grafana configuration
└── docs/                 # project proposal, architecture decisions, API docs, diagrams
```

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/)
- [Node.js](https://nodejs.org/) LTS (22 or newer)
- Git

## Getting Started

```bash
git clone https://github.com/SpideyyAatif-max/WalletPK.git
cd WalletPK
dotnet build WalletPK.sln
dotnet test
```

Every push and pull request to `main` runs the same build and tests on GitHub Actions ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)).

### Test reports

`dotnet test` prints a summary only. For browsable reports, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\test.ps1
```

Each run creates `TestResults/<date_time>/` with one HTML report (open in a browser) and one TRX report per test project. Earlier runs are kept for comparison; the folder is git-ignored and safe to delete. On GitHub, every CI run attaches the same reports as the **test-reports** artifact (kept 30 days).

## Running the Infrastructure

Start Docker Desktop, then from the repository root:

```bash
docker compose up -d        # starts all infrastructure containers
docker compose ps           # every service should show "healthy" (kafka-init exits after creating topics)
```

| Component | URL / connection | Credentials (local only) |
| --- | --- | --- |
| PostgreSQL | `localhost:5432` (one database per service, e.g. `walletpk_ledger`) | `walletpk` / `walletpk_dev` |
| Redis | `localhost:6379` | password `walletpk_dev` |
| Kafka | `localhost:9094` (from your PC), `kafka:9092` (from containers) | — |
| Kafka UI | http://localhost:8085 | — |
| Jaeger (traces) | http://localhost:16686 · OTLP `localhost:4317` (gRPC) / `4318` (HTTP) | — |
| Prometheus | http://localhost:9090 | — |
| Grafana | http://localhost:3001 | `admin` / `walletpk_dev` |
| Seq (logs) | http://localhost:8081 · ingestion `localhost:5341` | — |

Port already in use on your PC? Copy `.env.example` to `.env` and change the port.

```bash
docker compose down         # stop (data is kept in Docker volumes)
docker compose down -v      # stop and delete all data (fresh start)
```

## Running the API Gateway

With the infrastructure running (`docker compose up -d`):

```bash
dotnet run --project src/Gateway/WalletPK.Gateway
```

Open http://localhost:5000 to see every route. Requests appear in Seq (http://localhost:8081) and traces in Jaeger (http://localhost:16686, service `gateway`).
Calls to a service that is not running yet return `503`; protected routes without a token return `401`.

## Running the Web App (live demo)

The customer wallet at http://localhost:3000: sign up, log in, see your balance update live, send money, browse history.

**Everything in Docker** (first build takes a few minutes):

```bash
docker compose --profile apps up -d --build
```

**Or with hot reload** while working on the frontend (services in Docker, the web app from source):

```bash
docker compose --profile apps up -d --build gateway identity ledger
cd web
copy .env.example .env.local     # macOS/Linux: cp .env.example .env.local
npm install
npm run dev
```

Try it with two browsers (or one normal and one private window): sign up as two users. No phone is needed, because in Development the yellow **Dev SMS inbox** shows each code. Tap **Add Rs. 5,000** for test money, then send money from one to the other. The other screen updates by itself within a second. See [web/README.md](web/README.md) for how it works.

## Simulating the Outside World

Banks, Raast, billers, mobile networks and NADRA are simulated by the mock providers service (port 5200, started
with `--profile apps`). You can make any of them slow, flaky or dead while the system runs, and they produce
settlement files with mismatches you plant yourself. See [src/MockProviders/README.md](src/MockProviders/README.md).

## Sending Money to a Bank (Raast)

The Payments service (port 5103, `/api/payments/*` through the gateway) sends money from a wallet to any
account at the four simulated banks. Every transfer is a Saga with a timeline you can watch:

1. **Reserving**: the Ledger holds the amount in the wallet (`ledger.commands` → `ledger.replies` on Kafka).
2. **Sending**: Payments calls Raast through that bank's own resilience pipeline (bulkhead, timeouts,
   retries with jitter, circuit breaker).
3. **AwaitingBank**: Raast accepted it; the bank's signed callback (or, if it never comes, a status query)
   says how it ended.
4. **Settling → Completed**: the hold is captured into the bank float. If the bank refuses it
   (**Returning → Failed**), the money goes back to the wallet.

If the bank is down, the transfer is **Queued** and sent by itself when the bank is back (after 24 hours
it is given up and the money returned). If a send times out, Payments asks Raast whether it arrived before
sending again, so nothing is paid twice.

```bash
# A valid IBAN at MCB, who is behind it, then send Rs. 2,500 (Idempotency-Key required)
POST /api/payments/beneficiaries/lookup   {"iban": "PK76MUCB5512340001234567"}
POST /api/payments/bank-transfers         {"toIban": "PK76MUCB5512340001234567", "amountPaisa": 250000, "note": "Rent"}
GET  /api/payments/bank-transfers/{id}    # status (Processing, Queued, Completed, Failed) and the timeline
GET  /api/payments/banks                  # each bank: Available, Degraded or Down (its circuit breaker)

# Take HBL down, send to an HBL account (it queues), then bring HBL back (it completes)
curl -X PUT localhost:5200/admin/faults/bank:HBL -H "X-Admin-Key: dev-only-provider-admin-key" \
     -H "Content-Type: application/json" -d '{"outage": true}'
curl -X DELETE localhost:5200/admin/faults/bank:HBL -H "X-Admin-Key: dev-only-provider-admin-key"
```

Account numbers ending `0000` are closed accounts: the bank refuses the transfer and the money comes back.

## Sending, Requesting and Splitting (Wallet to Wallet)

Wallet payments also go through Payments, and the money still moves in one Ledger journal, so it is
instant and all-or-nothing:

- **Send**: `POST /api/payments/p2p-transfers` (Idempotency-Key) starts a small Saga that asks the Ledger
  to transfer (`TransferFunds` on Kafka). The transfer ID is the journal ID, and the Ledger remembers a
  refusal, so a resend after a lost answer never pays twice or after a "no".
- **Request money**: `POST /api/payments/requests` asks another number for an amount. They see it under
  `GET /api/payments/requests?direction=incoming` and can **pay**, **decline**, or let it **expire**; the
  requester can **cancel**. A failed payment (not enough balance) leaves the request open to try again.
- **Split a bill**: `POST /api/payments/splits` with a title, a total and the people; equal shares (or
  your own amounts) become one request each, and `GET /api/payments/splits/{id}` shows who has paid.

In the web app (http://localhost:3000/wallet): **Send**, **Bank** (lookup, send, live tracker, bank status),
**Requests** (to pay, I asked, split bills), **Bills**, **Mobile load**, **Scan & pay**, **My shop**, **Cash out**
and **Agent mode**.

## Paying Bills

The Bill Payment service (port 5104, `/api/bills/*`) pays K-Electric, LESCO, SNGPL and PTCL bills:

1. `POST /api/bills/inquiry` `{"biller": "KE", "consumerNumber": "0400012345678"}` shows this month's bill
   (name, due date, amount within / after due date, payable today). The number's format is checked first.
2. `POST /api/bills/payments` (Idempotency-Key) with the payable amount starts a Saga: the money is
   reserved, the biller is paid, then the money is settled to `BILLER_SETTLEMENT` (or returned).
3. If the biller is **down**, it retries with back-off (and gives up, returning the money, after 30 minutes).
   If the biller **times out**, it never pays again blindly: it asks the biller whether the payment arrived.
   With no answer for 10 minutes the payment is **pending verification** (money stays reserved); after 48 hours
   it is **dead-lettered** (`bills.dlq`) for operations, who **replay**, **refund** or **confirm** it
   (`/api/bills/ops/*`, operations roles only).
4. **Saved billers** (`/api/bills/saved`) remember the last bill; a job checks them twice a day and sends one
   `bills.bill_due_reminder` per unpaid bill from three days before its due date.

Consumer numbers ending `0000` do not exist, `1111` are already paid, `2222` are disconnected (refused).
Make a biller time out with `curl -X PUT localhost:5200/admin/faults/biller:KE -H "X-Admin-Key: dev-only-provider-admin-key" -H "Content-Type: application/json" -d '{"acceptButTimeoutRate": 1}'`.

## Mobile Load, QR Payments and Merchants

The Top-up & Merchant service (port 5105, `/api/topups/*`, `/api/merchants/*`):

1. **Mobile load:** `POST /api/topups` (Idempotency-Key) `{"msisdn": "0300 1234567", "amountPaisa": 10000}` for
   airtime (Rs. 20 to Rs. 5,000, whole rupees), or with a `bundleCode` from `/api/topups/networks/{code}/bundles`.
   The network comes from the number's prefix. Like a bill: the money is reserved, the number topped up, then
   settled to `TELCO_SETTLEMENT`. A network that is down is retried (and given up after 10 minutes); a timeout
   is checked with the network's status query, so a number is never topped up twice; unconfirmed for 24 hours,
   it goes to `topup.dlq`. Numbers ending `0000` are inactive.
2. **Merchants:** `POST /api/merchants` `{"name": "Ali Tea Stall", "category": "FOOD"}` registers your shop; the
   Ledger opens its merchant wallet (`WPKM` and six digits). The category sets the fee plan
   (`/api/merchants/fee-plans`): a percentage with a floor and a ceiling, plus 16% tax on the fee.
3. **Dynamic QR:** `POST /api/merchants/me/qr` `{"amountPaisa": 100000, "reference": "INV-42"}` returns a signed
   payload (`WPKQR1.{data}.{HMAC-SHA256}`) for one amount, payable once, for 5 minutes. The till polls
   `/api/merchants/me/qr/{id}` until it is paid.
4. **Scan & pay:** `POST /api/merchants/qr/preview` shows who and how much; `POST /api/merchants/qr/pay`
   (Idempotency-Key) pays it in one Ledger journal: the customer pays the amount, the merchant gets it less
   the fee and tax, which go to `FEE_INCOME` and `TAX_PAYABLE`. Two people paying the same code: one wins.
5. **Settlement:** after midnight (Pakistan time) each merchant's completed sales of the day before are swept
   from its merchant wallet to its bank (`BANK_FLOAT`), once. A refused settlement is retried the next night.
   Operations can run it now: `POST /api/merchants/ops/settlements/run?date=2026-11-18`.

Set `Merchants__Qr__SigningKey` (32+ characters) outside development; docker compose reads `QR_SIGNING_KEY`.

## Agents: Cash-in and Cash-out

The Agent Network service (port 5106, `/api/agents/*`) runs WalletPK's retail agents, the shops where
customers turn cash into e-money and back:

1. **Becoming an agent:** `POST /api/agents` `{"shopName": "Bismillah Easy Cash", "city": "Multan"}` (KYC L1 or
   above). Operations approve it (`/api/agents/ops/{id}/approve`; in Development it is approved at once) and
   the Ledger opens the agent's **float**, a wallet of its own (`WPKA` and six digits, read at
   `/api/wallets/me/agent`). The agent loads it from its own wallet, and moves it back, with
   `POST /api/agents/me/float` `{"direction": "ToFloat", "amountPaisa": 500000}`.
2. **Cash-in:** the agent takes the customer's cash and sends `POST /api/agents/me/cash-ins`
   `{"customerNumber": "0300 1234567", "amountPaisa": 200000}`: the float pays the customer's wallet, free for
   the customer. Below the agent's alert level (Rs. 5,000 by default) `agents.float_low` is sent.
3. **Cash-out:** the customer asks for cash at the agent in the app (`POST /api/agents/cash-outs`
   `{"agentCode": "WPKA123456", "amountPaisa": 100000}`) and gets a six-digit code, good for 15 minutes. The agent
   types it (`POST /api/agents/me/cash-outs`): the customer's wallet pays the amount plus a 1% fee (Rs. 10 to
   Rs. 300) and 16% tax on it, the float gets the amount, and the agent hands over the cash. Five wrong codes
   lock the request; two tries at once pay out once.
4. **Commissions:** 0.4% of each cash-in (Rs. 2 to Rs. 40) and 60% of each cash-out fee, accrued in the same
   Ledger journal (`COMMISSION_EXPENSE` / `COMMISSION_PAYABLE`) and paid to the float every night less 12%
   withholding tax. Operations can pay now: `POST /api/agents/ops/payouts/run?date=2026-11-20`.

Set `Agents__CashOutCodeKey` (32+ characters) outside development; docker compose reads `CASH_OUT_CODE_KEY`.

## Fraud and Risk

The Fraud & Risk service (port 5107, `/api/fraud/*`; gRPC on 5207, internal only) scores every payment before
the Ledger moves money, and gives compliance staff the tools to act:

1. **Scoring:** the Ledger asks `Risk.Score` (gRPC) after the Identity limits check, for wallet transfers,
   Raast and bill holds, merchant QR payments and agent cash-outs. Features come from Redis windows: payments in the
   last 60 seconds, hour and 24 hours, the device's and account's age, whether the recipient is known, how many
   wallets paid the recipient in the last hour, and whether the user was held or blocked recently. The four
   answers (FR-5.1): below 30 the payment goes through; 30 or more needs a one-time code
   (`fraud.step_up_required`, see below); 60 or more holds it for review (`fraud.review`); 90 or more blocks it
   (`fraud.blocked`) and freezes the user's wallets. A typical score takes 1-3 ms. If the service is down or
   slow (past 250 ms), the Ledger lets payments through and logs it (`Services:FraudGrpc:FailOpen`).
2. **Step-up OTP:** the app asks for a code with `POST /api/identity/step-up` (texted to the user's phone),
   checks it with `POST /api/identity/step-up/verify` `{"code": "123456"}`, and sends the payment again with a
   new Idempotency-Key. The confirmation reaches the Fraud service on `identity.events` in about a second and
   lets one such payment from the same phone through within 5 minutes; it never lifts a hold or a block. In the
   web app the send-money screen asks for the code and sends again by itself; bank, bill, top-up and QR payments
   ask for the code and the user sends again.
3. **Rules:** seven built-in rules (velocity burst, a large payment from a new device, SIM-swap drain, mule fan-in,
   odd hours, a large payment to a new recipient, a repeat after a hold or block) are JSON, stored in the database
   and editable without a redeploy: `POST /api/fraud/rules`, `PUT /api/fraud/rules/{id}` (`{"enabled": false}`
   switches one off). Every instance reloads within a second (Redis pub/sub, with a poll as a fallback).
   `POST /api/fraud/rules/test` tries a rule on feature values without saving it; `GET /api/fraud/features`
   lists what rules can test.
4. **Rings:** credits on `ledger.events` and scored transfers feed a ring detector. When five or more wallets
   pay one wallet within 10 minutes and three of them were held or blocked, it opens a Ring case, freezes the mule and
   the refused senders, and raises a High alert on `fraud.alerts`.
5. **Freezes, alerts and cases:** `POST /api/fraud/accounts/{userId}/freeze` and `/unfreeze` with a reason;
   the Ledger confirms on `ledger.replies`. Held and blocked payments raise alerts and open a case per user;
   `/api/fraud/cases` lists them, and `/{id}/assign`, `/notes` and `/close` (`ConfirmedFraud`, or
   `FalsePositive` with `"unfreeze": true`) work them.
6. **Audit log:** every rule change, freeze and case decision is written to a hash-chained log
   (`GET /api/fraud/audit`). `GET /api/fraud/audit/verify` recomputes the chain and reports the first entry
   that was changed.

Scenario S6 (20 compromised accounts draining into 3 mule wallets) is an integration test: every account and
mule is frozen and alerted in under a second, against the proposal's 2 seconds. The compliance API needs the
`compliance` or `admin` role. The fraud queue and rule editor screens come with the Ops console (M14.C4).

## Notifications and Live Updates

The Notifications service (port 5108, `/api/notifications/*`, hubs at `/hubs/wallet` and `/hubs/ops`) tells
users and staff what happens, as it happens (M11):

1. **From every service's events:** it reads `identity.events`, `ledger.events`, `payments.events`,
   `bills.events`, `topup.events`, `agents.events`, `fraud.alerts` and the dead-letter topics. Money in and out
   comes from the Ledger's wallet entries, so every kind of payment is announced once; the payment services add
   failures, money requests and status changes. A new device, a KYC upgrade, a bill falling due, an agent's float
   running low and a wallet put on hold or released are announced too. A frozen customer is never told why.
2. **In English and Urdu:** each message exists in both; the user picks one in
   `PUT /api/notifications/settings` (`{"language": "ur"}`). The notification centre is written in the chosen
   language even for older messages, and SMS and email go out in it.
3. **Three channels:** every notification is in the app (`GET /api/notifications`, unread count, mark read).
   Payments, requests and bills are also texted to the wallet's number; account news goes by email when the user
   adds an address. Both can be turned off, except security messages (a new device, a wallet on hold). Texts and
   emails go to the simulated gateways in the mock providers service; in development,
   `GET /api/notifications/dev/messages` (and the settings page in the app) shows what was sent.
4. **Exactly once:** a redelivered event, or a second event about the same payment, makes one notification. Each
   SMS and email has its own ID that the gateway uses as an idempotency key, so a retry after a lost answer is
   not sent twice. Failed sends are retried with growing waits and reported on `notifications.events`. Events
   more than an hour old (after an outage) show in the app but are not texted.
5. **Live:** `/hubs/wallet` pushes new balances, new notifications and payment status changes to the user's
   open tabs; `/hubs/ops` (staff only) pushes fraud alerts, freezes, cases, stuck payments and low agent float.
   A Redis backplane delivers each push to whichever instance holds the browser's connection, so the service can
   run as several instances. The live balance moved here from the Ledger, which no longer serves a hub.

The web app has a bell with the unread count, a notification centre, and notification settings (language, SMS,
email). The Ops live dashboard that reads `/hubs/ops` comes with the Ops console (M14).

## Confirming Payments with the MPIN

Sending money asks for the MPIN on the confirmation screen (proposal 12.1). The app sends the MPIN and the
payment's Idempotency-Key to `POST /api/identity/mpin/confirm` and gets a token that is valid for five minutes,
for that user, that device and that one payment. The payment carries it in the `X-Mpin-Confirmation` header.
Without it, `POST /api/payments/p2p-transfers`, `POST /api/payments/bank-transfers`,
`POST /api/payments/requests/{id}/pay` (confirmed for `money-request:{id}`) and the Ledger's
`POST /api/wallets/transfers` answer 403 `auth.mpin_confirmation_required`. A refusal is not stored against the
Idempotency-Key, so the same payment can be sent again once confirmed. Wrong MPINs count towards the same
lockout as at login (5 in a row: 30 minutes). Set `MpinConfirmation__Required=false` to turn it off, for
example for load tests.

## Operations APIs

For the `ops`, `compliance`, `finance` and `admin` roles:

- **Saga explorer** (`/api/payments/ops/sagas`, and the same under `/api/bills`, `/api/topups` and
  `/api/agents`): search by type, state, user, correlation ID or how long a Saga has been stuck; open one to see
  its data and full timeline. Each step records the trace it ran in, so it opens in Jaeger
  (http://localhost:16686/trace/{traceId}). `/ops/sagas/counts` shows how many are in each state.
- **Dead-letter queues:** bills at `/api/bills/ops/dead-letters`, top-ups at `/api/topups/ops/dead-letters`
  (FR-4.3). A dead-lettered top-up can be **replayed** (the step that was given up on runs again), **refunded**
  (only if the network is not known to have applied it) or **confirmed** with the network's reference from its
  settlement file.

## Observability

- **Traces:** one trace follows a payment from the gateway through every service, across Kafka (each event
  carries a W3C `traceparent` header, also kept in the outbox) and through each Saga step. Jaeger:
  http://localhost:16686.
- **Metrics:** services push to Prometheus over OTLP (http://localhost:9090): HTTP, Kafka consumption, Saga
  states, timers and transitions, Ledger postings, fraud decisions, provider calls and circuit breakers.
- **Dashboards:** Grafana (http://localhost:3001, admin / walletpk_dev) loads three from `ops/grafana/dashboards`:
  *Operations overview* (transactions per second, success rate, p95 latency per service, active Sagas by state,
  fraud decisions, Ledger health), *Sagas* and *Providers*.
- **API documents:** every service serves its OpenAPI document at `/openapi/v1.json` (for example
  http://localhost:5103/openapi/v1.json), with response and error schemas and the bearer token.

## Tests

`dotnet test` runs unit, integration (Testcontainers: PostgreSQL, Redis, Kafka), architecture and contract tests:

- **Architecture** (`tests/Architecture`, NetArchTest): Domain and Application stay free of frameworks and
  infrastructure, endpoints go through the application layer, no service uses another service's code, and the
  building blocks know no service.
- **Contracts** (`tests/BuildingBlocks/WalletPK.Contracts.Tests`): every Kafka event and gRPC message is recorded
  in `Snapshots/`. A recorded event may only gain optional fields; removing, renaming or retyping a field, or
  adding a required one, needs a higher `Version`. After a deliberate, compatible change, record it with
  `$env:WALLETPK_UPDATE_CONTRACTS=1; dotnet test --project tests/BuildingBlocks/WalletPK.Contracts.Tests` and commit the snapshot.

## Service Ports

| Service | Local port (`dotnet run`) | Gateway path |
| --- | --- | --- |
| API Gateway | 5000 | `/` |
| Identity & KYC | 5101 (gRPC: 5201, internal only) | `/api/identity/*` |
| Wallet & Ledger | 5102 | `/api/wallets/*`, `/api/ledger/*` (staff only) |
| Payments | 5103 | `/api/payments/*`, `/webhooks/raast/*` |
| Bill Payment | 5104 | `/api/bills/*`, `/webhooks/billers/*` |
| Top-up & Merchant | 5105 | `/api/topups/*`, `/api/merchants/*` |
| Agent Network | 5106 | `/api/agents/*` |
| Fraud & Risk | 5107 (gRPC: 5207, internal only) | `/api/fraud/*` (compliance staff only) |
| Notifications | 5108 | `/api/notifications/*`, `/hubs/wallet` (live updates), `/hubs/ops` (staff only) |
| Reconciliation | 5109 | `/api/reconciliation/*` (staff only) |
| Simulation | 5110 | `/api/simulation/*` (staff only) |
| Mock Providers | 5200 | not exposed through the gateway ([README](src/MockProviders/README.md)) |
| Web app (Next.js) | 3000 | |

## Project Conventions

- **Build settings** shared by every project live in `Directory.Build.props` (.NET 10, nullable, warnings as errors).
- **Package versions** are declared once in `Directory.Packages.props` (Central Package Management); projects reference packages without versions.
- **Code style** is defined in `.editorconfig` and enforced on build.
- **Money** is always stored as integer paisa (`long`), never as floating point.
- **Commits** are one per roadmap submodule, prefixed with its ID (for example `M0.1: solution scaffold`).

## Documentation

- [Roadmap and build plan](ROADMAP.md)
- [Project proposal (PDF)](docs/proposal/WalletPK-Proposal.pdf)

## License

Released under the [MIT License](LICENSE).
