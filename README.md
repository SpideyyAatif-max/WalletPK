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

## Service Ports

| Service | Local port (`dotnet run`) | Gateway path |
| --- | --- | --- |
| API Gateway | 5000 | `/` |
| Identity & KYC | 5101 (gRPC: 5201, internal only) | `/api/identity/*` |
| Wallet & Ledger | 5102 | `/api/wallets/*`, `/api/ledger/*` (staff only), `/hubs/wallet` (live balance) |
| Payments | 5103 | `/api/payments/*`, `/webhooks/raast/*` |
| Bill Payment | 5104 | `/api/bills/*`, `/webhooks/billers/*` |
| Top-up & Merchant | 5105 | `/api/topups/*`, `/api/merchants/*` |
| Agent Network | 5106 | `/api/agents/*` |
| Fraud & Risk | 5107 | `/api/fraud/*` (staff only) |
| Notifications | 5108 | `/api/notifications/*`, `/hubs/*` (except `/hubs/wallet`) |
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
