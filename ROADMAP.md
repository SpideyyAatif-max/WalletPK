# WalletPK — Roadmap & Build Plan

Distributed mobile wallet and payments platform (JazzCash / Easypaisa style).
Backend-first, with a full frontend for the wallet, operations console and simulation control center.

The build is split into 17 modules (M0–M16). Each submodule is built, tested, committed to git, then ticked off.
By the end of M4 a full working slice exists: sign up -> wallet -> transfer -> live balance.

---

## 1. The Big Picture: 5 Phases

| Phase | Weeks | Modules | What works at the end |
| --- | --- | --- | --- |
| 1. Foundation | 1–2 | M0, M1, M2 | Infrastructure runs in Docker; users sign up and log in through the gateway |
| 2. Money core | 3–6 | M3, M4, part of M11 | Wallets, safe transfers, events flowing through Kafka, live balance in the UI |
| 3. Outside world | 6–9 | M5, M6, M7, M8, M9 | Bank transfers, bills, top-ups, QR and agents, with failures and refunds |
| 4. Intelligence & control | 10–14 | M10, rest of M11, M13, M12 | Fraud detection, the simulator, chaos testing, reconciliation |
| 5. Polish & demo | 15–16 | M16 (M15 runs throughout) | Tuned, documented, demo-ready |

---

## 2. Technologies: What Each Does and Where It Enters

| Technology | Its job in WalletPK | First used | Used again in |
| --- | --- | --- | --- |
| ASP.NET Core (.NET 10) | Every backend service | M0 | All modules |
| YARP | API gateway, the single entry point | M1 | — |
| PostgreSQL | Source of truth; one schema per service in one server (saves RAM) | M0 (container), M2 (first tables) | M3 ledger, M6–M9 Saga state, M12 recon |
| EF Core | Writes: migrations, transactions, row-version concurrency | M2 | M3 onwards |
| Dapper | Fast reads from read models | M4 | M10, M12, ops dashboards |
| Redis | Short-lived, fast state (see 2.1) | M1 | M2, M3, M10, M11 |
| Kafka | Durable event log connecting services | M0 (container), M4 (real use) | Every service after M4 |
| MassTransit | Saga state machines | M6 | M7, M8 |
| Polly | Retries, timeouts, circuit breakers, bulkheads | M6 | M7, M8, M2 (NADRA calls) |
| gRPC | Fast internal service-to-service calls | M2 (limits check) | M3, M10 (fraud score) |
| SignalR | Pushes live updates to the browser | M4.7 (first live balance) | M11 (full hubs), M13, M14 |
| OpenTelemetry, Jaeger, Prometheus, Grafana, Seq | Traces, metrics, logs | M0 (setup) | Every module (M15) |
| Next.js, TypeScript, Tailwind, shadcn/ui | One web app with three portals | M2 (login screens) | Grows with every backend module |
| k6 | Load testing | M3 (race tests) | M13, M16 |
| Testcontainers, xUnit, FsCheck | Integration and property-based tests | M3 | All modules |

### 2.1 Where Redis Is Used

| Use | Module |
| --- | --- |
| Rate-limit counters (per user, device, IP) | M1.3 |
| OTP codes (hashed, 2-minute expiry) | M2.2 |
| Distributed lock per wallet (stops double-spend) | M3.6 |
| Idempotency fast-path cache | M3.7 |
| Fraud features: rolling counts and amounts per wallet | M10.1 |
| SignalR backplane so live updates work across services | M11.1 |

### 2.2 Kafka Topics

| Topic | Producer | Consumers | Starts in |
| --- | --- | --- | --- |
| identity.events | Identity | Ledger (creates wallet), Notifications | M4 |
| ledger.events | Ledger | Read models, Fraud, Notifications, Ops dashboard | M4 |
| payments.events | Payments | Notifications, Ops | M6 |
| bills.events / topup.events | Bills, Top-up | Notifications, Ops | M7, M8 |
| fraud.alerts | Fraud | Ops console, Notifications | M10 |
| *.dlq (dead-letter queues) | Any Saga | Ops DLQ console | M7 |
| sim.metrics | Simulator | Control Center | M13 |

---

## 3. Backend Techniques: Where Each Is Implemented

| Technique | Problem it solves | Module |
| --- | --- | --- |
| Clean Architecture (Domain / Application / Infrastructure / API) | Keeps business rules separate from frameworks | M0, every service |
| JWT + rotating refresh tokens | Authentication | M1.2, M2.3 |
| Token-bucket rate limiting | Abuse, OTP brute force | M1.3 |
| HMAC SHA-256 webhooks | Fake provider callbacks | M1.4, M5.2 |
| Argon2id hashing | MPIN storage | M2.2 |
| AES-256-GCM field encryption | CNIC and IBAN at rest | M2.5 |
| Double-entry bookkeeping | Money can never appear or vanish | M3.4 |
| Database constraints and triggers | Last line of defence against bugs | M3.2 |
| Holds (authorise -> capture/release) | Safe multi-step payments | M3.5 |
| Redis distributed lock + optimistic concurrency | Double-spend under parallel requests | M3.6 |
| Idempotency keys | Double taps and client retries | M3.7 |
| Sharded hot accounts | Fee/settlement accounts becoming a lock bottleneck | M3.3 |
| Hash-chained audit log | Tamper detection | M3.10 |
| Transactional Outbox | Database and Kafka falling out of sync | M3.4 (write), M4.1 (relay) |
| Inbox / idempotent consumers | Duplicate event delivery | M4.3 |
| CQRS + read models | Slow reads on a busy ledger | M4.4, M4.5 |
| Event replay | Rebuilding a broken read model | M4.6 |
| Fault injection | Testing failures on purpose | M5.1 |
| Orchestrated Saga + compensation | Multi-step bank and biller payments | M6.3, M6.4, M7.3, M8.1 |
| Retry with backoff, circuit breaker, bulkhead | Flaky providers, cascading failures | M6.5 |
| Status query before retry | Paying a bill twice after a timeout | M7.3 |
| Dead-letter queue + replay | Messages that fail permanently | M7.5 |
| Signed QR payloads | Tampered or forged QR codes | M8.3 |
| Real-time rules engine | Fraud | M10.2–M10.5 |
| Reconciliation matching | Mismatches with banks | M12.2 |
| Chaos engineering | Proving the system survives crashes | M13.4, M13.5 |
| PostgreSQL row-level security | Limits which rows each role can read | M16.2 |

---

## 4. Phase by Phase: Backend, Redis, Kafka and Frontend Together

### Phase 1 — Foundation (weeks 1–2)
- **Backend:** scaffold and shared libraries (M0); gateway with JWT, rate limits, HMAC (M1); Identity service: OTP, MPIN, tokens, devices, KYC tiers (M2).
- **Redis:** rate limiting, OTP storage.
- **Kafka:** container running, not used yet.
- **Frontend:** Next.js setup, shared layout, wallet sign-up and login screens.
- **Demo:** sign up with a phone number, verify OTP, log in.

### Phase 2 — Money core (weeks 3–6)
- **Backend:** ledger with double-entry, holds, locks, idempotency, fees, audit chain, invariant checker (M3); outbox relay, Kafka, inbox, read models (M4).
- **Redis:** wallet locks, idempotency cache.
- **Kafka:** first real topics, identity.events and ledger.events; a new user automatically gets a wallet.
- **Frontend:** wallet home with live balance (SignalR), send money, history.
- **Demo:** two browsers send money to each other and both balances update live; 50 parallel debits never go negative.

### Phase 3 — Outside world (weeks 6–9)
- **Backend:** mock providers with fault controls (M5); Raast transfers with Sagas and Polly (M6); bills with DLQ (M7); top-up and QR (M8); agents (M9).
- **Redis:** used as before; nothing new.
- **Kafka:** payment, bill and top-up events, plus dead-letter topics.
- **Frontend:** bank transfer, bill payment, top-up, QR pay, Saga tracker, merchant and agent modes; first Ops pages (Saga explorer, DLQ console).
- **Demo:** break the K-Electric mock and watch the payment retry, then refund automatically.

### Phase 4 — Intelligence & control (weeks 10–14)
- **Backend:** fraud engine (M10); full notification hubs (M11); simulator and chaos engine (M13); reconciliation, statements, reports (M12).
- **Redis:** fraud features, SignalR backplane.
- **Kafka:** fraud.alerts, sim.metrics, cross-account consumers that detect fraud rings.
- **Frontend:** Ops dashboard, fraud queue and rule editor, customer 360, reconciliation, and the full Simulation Control Center (traffic, chaos panel, scenarios, money-flow map, invariant tiles).
- **Demo:** run scenarios S1–S8 from the Control Center and get pass/fail reports.

### Phase 5 — Polish & demo (weeks 15–16)
- Performance tuning, security review (including row-level security), seed data, documentation, demo recording.

### Throughout — M15
Each module ships with its tests, traces and metrics, never added at the end.

### How the Frontend Tracks the Backend

| Backend module done | Frontend screens added |
| --- | --- |
| M2 Identity | Sign up, OTP, MPIN, login, KYC page |
| M3–M4 Ledger + CQRS | Home with live balance, send to wallet, history |
| M6 Payments | Bank transfer, Saga tracker, request money |
| M7 Bills | Pay bills, saved billers, DLQ console (Ops) |
| M8–M9 Top-up, QR, Agents | Top-up, scan QR, merchant mode, agent mode |
| M10 Fraud | Fraud queue, case view, rule editor (Ops) |
| M11 Notifications | Notification centre, Ops live dashboard |
| M12 Reconciliation | Recon results, statements, audit viewer |
| M13 Simulation | The whole Control Center |

---

## 5. Repository Structure

```text
WalletPK/
├── WalletPK.sln
├── docker-compose.yml              # infra + all services
├── docker-compose.infra.yml        # Postgres, Redis, Kafka, Jaeger, Prometheus, Grafana, Seq
├── src/
│   ├── BuildingBlocks/
│   │   ├── WalletPK.Common/        # Result, Money, errors, clock, IDs
│   │   ├── WalletPK.Messaging/     # Outbox, Inbox, Kafka producer/consumer
│   │   ├── WalletPK.Web/           # idempotency, correlation ID, problem details, auth
│   │   ├── WalletPK.Observability/ # OpenTelemetry + Serilog setup
│   │   └── WalletPK.Contracts/     # event and gRPC contracts (.proto)
│   ├── Gateway/WalletPK.Gateway/
│   ├── Services/
│   │   ├── Identity/   Ledger/   Payments/   Bills/   TopupMerchant/
│   │   └── Agents/     Fraud/    Notifications/   Reconciliation/   Simulation/
│   └── MockProviders/WalletPK.MockProviders/
├── web/                            # Next.js app: /wallet, /ops, /sim
├── tests/                          # unit, integration, architecture, contract
├── load/                           # k6 scripts
├── ops/                            # Grafana dashboards, Prometheus config
└── docs/                           # ADRs, API docs, diagrams
```

Each service uses Clean Architecture: Domain, Application, Infrastructure, Api.

---

## 6. Full Module and Submodule Checklist

### M0 — Foundation & BuildingBlocks (week 1)
- [x] M0.1 Solution scaffold: .sln, Directory.Build.props, central package versions, .editorconfig, .gitignore
- [ ] M0.2 Infrastructure compose: Postgres 17, Redis 7, Kafka (KRaft) + Kafka UI, Jaeger, Prometheus, Grafana, Seq
- [ ] M0.3 WalletPK.Common: Money (paisa as long), Result<T>, domain errors, IClock, strongly-typed IDs
- [ ] M0.4 WalletPK.Web: correlation ID, problem details, exception handler, JWT helpers, health checks
- [ ] M0.5 WalletPK.Observability: OpenTelemetry + Serilog in one call
- [ ] M0.6 WalletPK.Messaging skeleton: IOutbox, IInbox, Kafka abstractions
- [ ] M0.7 CI pipeline (GitHub Actions)

### M1 — API Gateway (week 1)
- [ ] M1.1 YARP route table and cluster health checks
- [ ] M1.2 JWT validation at the edge; forward user, device and KYC tier
- [ ] M1.3 Token-bucket rate limiting per user, device, IP
- [ ] M1.4 HMAC SHA-256 webhook validation with replay window
- [ ] M1.5 Correlation ID, request logging, CORS

### M2 — Identity & KYC (week 2)
- [ ] M2.1 Domain: User, Device, KycProfile, KycTier (L0/L1/L2)
- [ ] M2.2 Registration: phone -> OTP (Redis) -> MPIN (Argon2id)
- [ ] M2.3 MPIN login, JWT, rotating refresh tokens with reuse detection
- [ ] M2.4 Device binding, step-up OTP, 24-hour cooling-off limit
- [ ] M2.5 KYC upgrade via mock NADRA; CNIC encrypted (AES-256-GCM)
- [ ] M2.6 gRPC CheckLimit used by the ledger
- [ ] M2.7 Events: UserRegistered, DeviceBound, KycUpgraded

### M3 — Wallet & Ledger Core (weeks 3–4)
- [ ] M3.1 Schema: accounts, journal_transactions, journal_entries, holds, idempotency_keys, outbox_events, audit_log
- [ ] M3.2 Database guards: balanced-journal trigger, non-negative check, no UPDATE/DELETE on entries
- [ ] M3.3 Chart of accounts incl. sharded hot system accounts
- [ ] M3.4 Posting engine: entries + balances + outbox in one transaction
- [ ] M3.5 Holds: place, capture, release, expiry sweeper
- [ ] M3.6 Concurrency: Redis lock (SETNX + TTL + fencing token) + row version
- [ ] M3.7 Idempotency-Key middleware (Redis + Postgres)
- [ ] M3.8 Wallet APIs: create, balance, P2P, history
- [ ] M3.9 Fee engine
- [ ] M3.10 Hash-chained audit log
- [ ] M3.11 Invariant checker (debits = credits, no negatives)
- [ ] M3.12 Property-based and 50-parallel-debit race tests

### M4 — Event Pipeline & CQRS (weeks 5–6)
- [ ] M4.1 Outbox relay (FOR UPDATE SKIP LOCKED, batch publish)
- [ ] M4.2 Kafka topics keyed by wallet ID
- [ ] M4.3 Inbox: idempotent consumers
- [ ] M4.4 Read models: balances, history, per-minute metrics
- [ ] M4.5 Dapper query API (p95 < 10 ms)
- [ ] M4.6 Read-model replay from Kafka
- [ ] M4.7 End-to-end slice with live balance in the UI

### M5 — Mock External Providers (week 6)
- [ ] M5.1 Runtime fault injection (latency, errors, timeouts, outages)
- [ ] M5.2 Mock Raast and banks (HBL, Meezan, UBL, MCB) with signed callbacks
- [ ] M5.3 Mock billers (K-Electric, LESCO, SNGPL, PTCL)
- [ ] M5.4 Mock telcos (Jazz, Zong, Telenor, Ufone)
- [ ] M5.5 Mock NADRA
- [ ] M5.6 Settlement files with injectable mismatches
- [ ] M5.7 Accept-but-timeout mode

### M6 — Payments: P2P & Raast (week 7)
- [ ] M6.1 P2P transfer flow
- [ ] M6.2 Request money and split bill
- [ ] M6.3 Saga infrastructure (MassTransit, persisted state)
- [ ] M6.4 Raast transfer Saga
- [ ] M6.5 Polly per bank: timeout, retry + jitter, circuit breaker, bulkhead
- [ ] M6.6 Queue transfers while a bank is down
- [ ] M6.7 Saga status timeline API

### M7 — Bill Payment (week 8)
- [ ] M7.1 Biller catalog and validation
- [ ] M7.2 Bill inquiry
- [ ] M7.3 Payment Saga with status query before retry
- [ ] M7.4 Pending-verification state
- [ ] M7.5 Dead-letter queue, replay, manual refund
- [ ] M7.6 Saved billers and reminders

### M8 — Top-up & Merchant QR (week 9)
- [ ] M8.1 Mobile top-up Saga
- [ ] M8.2 Merchant onboarding and fee plans
- [ ] M8.3 Signed dynamic QR
- [ ] M8.4 QR payment with fee split
- [ ] M8.5 Nightly merchant settlement

### M9 — Agent Network (week 9)
- [ ] M9.1 Agent profile and float
- [ ] M9.2 Cash-in
- [ ] M9.3 Cash-out
- [ ] M9.4 Commissions

### M10 — Fraud & Risk Engine (weeks 10–11)
- [ ] M10.1 Redis feature windows
- [ ] M10.2 JSON rules engine with hot reload
- [ ] M10.3 Built-in rules (velocity, new device, mule pattern, odd hours, new recipient)
- [ ] M10.4 Synchronous gRPC score (< 20 ms)
- [ ] M10.5 Async ring detection on Kafka
- [ ] M10.6 Freeze/unfreeze and alerts
- [ ] M10.7 Case management API

### M11 — Notifications & Realtime (weeks 6, 11)
- [ ] M11.1 SignalR WalletHub and OpsHub with Redis backplane
- [ ] M11.2 Event consumers
- [ ] M11.3 English/Urdu templates
- [ ] M11.4 In-app, simulated SMS and email
- [ ] M11.5 Idempotent sends

### M12 — Reconciliation & Reporting (week 14)
- [ ] M12.1 Settlement file ingestion
- [ ] M12.2 Matching engine and mismatch classes
- [ ] M12.3 Resolution workflow
- [ ] M12.4 Daily invariant report
- [ ] M12.5 PDF statements (QuestPDF)
- [ ] M12.6 Audit chain verification
- [ ] M12.7 Business and suspicious-activity reports

### M13 — Simulation & Chaos Engine (weeks 12–13)
- [ ] M13.1 Virtual user engine through the real gateway
- [ ] M13.2 Behaviour profiles (student, salaried, merchant, agent, fraudster)
- [ ] M13.3 Traffic controller (TPS, ramp, pause)
- [ ] M13.4 Chaos controller (provider faults, stop containers)
- [ ] M13.5 Scenario presets S1–S8 with assertions
- [ ] M13.6 Run reports
- [ ] M13.7 k6 scripts

### M14 — Frontend Portals (built alongside the backend)
**M14.A Shared**
- [ ] M14.A1 Next.js + TypeScript + Tailwind + shadcn/ui scaffold
- [ ] M14.A2 Typed API client from OpenAPI + TanStack Query
- [ ] M14.A3 Auth flow and role-based route guards
- [ ] M14.A4 SignalR client hook

**M14.B Customer Wallet (/wallet)**
- [ ] M14.B1 Onboarding: phone -> OTP -> MPIN -> CNIC
- [ ] M14.B2 Home: live balance, quick actions, recent activity
- [ ] M14.B3 Send money (wallet / bank)
- [ ] M14.B4 Bills, top-up, QR pay
- [ ] M14.B5 Live Saga tracker
- [ ] M14.B6 History, statements, limits, notifications
- [ ] M14.B7 Merchant mode and agent mode

**M14.C Ops & Compliance (/ops)**
- [ ] M14.C1 Live operations dashboard
- [ ] M14.C2 Saga explorer with trace links
- [ ] M14.C3 DLQ console
- [ ] M14.C4 Fraud queue, cases, rule editor
- [ ] M14.C5 Customer 360
- [ ] M14.C6 Reconciliation and invariant history
- [ ] M14.C7 Audit log viewer

**M14.D Simulation Control Center (/sim)**
- [ ] M14.D1 Virtual users and traffic controls
- [ ] M14.D2 Chaos panel
- [ ] M14.D3 Scenario launcher S1–S8
- [ ] M14.D4 Live money-flow map of Pakistan
- [ ] M14.D5 Invariant tiles
- [ ] M14.D6 Run report page and PDF export

### M15 — Observability & Testing (throughout)
- [ ] M15.1 OpenTelemetry tracing across services, Kafka and Sagas
- [ ] M15.2 Prometheus metrics
- [ ] M15.3 Grafana dashboards
- [ ] M15.4 Unit tests
- [ ] M15.5 Integration tests (Testcontainers)
- [ ] M15.6 Architecture tests (NetArchTest)
- [ ] M15.7 Contract tests
- [ ] M15.8 k6 load tests

### M16 — Hardening, Docs & Demo (weeks 15–16)
- [ ] M16.1 Performance tuning
- [ ] M16.2 Security review (OWASP API Top 10, row-level security, secrets)
- [ ] M16.3 Seed data
- [ ] M16.4 Documentation: README, ADRs, OpenAPI, ER diagram, runbook
- [ ] M16.5 Demo rehearsal and video

---

## 7. How Each Submodule Is Delivered
1. Code is written into this folder, one submodule at a time.
2. Build and tests run before it is marked done.
3. One git commit per submodule with a clear message.
4. The checkbox in this file (ROADMAP.md) is ticked.
