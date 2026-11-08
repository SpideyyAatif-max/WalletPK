# WalletPK

[![CI](https://github.com/SpideyyAatif-max/WalletPK/actions/workflows/ci.yml/badge.svg)](https://github.com/SpideyyAatif-max/WalletPK/actions/workflows/ci.yml)
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
| Sagas and resilience | MassTransit, Polly |
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

## Service Ports

| Service | Local port (`dotnet run`) | Gateway path |
| --- | --- | --- |
| API Gateway | 5000 | `/` |
| Identity & KYC | 5101 | `/api/identity/*` |
| Wallet & Ledger | 5102 | `/api/wallets/*` |
| Payments | 5103 | `/api/payments/*`, `/webhooks/raast/*` |
| Bill Payment | 5104 | `/api/bills/*`, `/webhooks/billers/*` |
| Top-up & Merchant | 5105 | `/api/topups/*`, `/api/merchants/*` |
| Agent Network | 5106 | `/api/agents/*` |
| Fraud & Risk | 5107 | `/api/fraud/*` (staff only) |
| Notifications | 5108 | `/api/notifications/*`, `/hubs/*` |
| Reconciliation | 5109 | `/api/reconciliation/*` (staff only) |
| Simulation | 5110 | `/api/simulation/*` (staff only) |
| Mock Providers | 5200 | not exposed through the gateway |
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
