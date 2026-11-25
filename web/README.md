# WalletPK web app

Next.js 16 (App Router) + TypeScript + Tailwind CSS 4, with TanStack Query for server state and
SignalR for live updates. Today it has the customer wallet (`/wallet`, M4.7). The ops console (`/ops`) and
simulation control centre (`/sim`) come with later modules (see [ROADMAP.md](../ROADMAP.md), M14).

## Run it

```bash
cp .env.example .env.local   # Windows: copy .env.example .env.local
npm install
npm run dev                  # http://localhost:3000
```

It needs the gateway, Identity and Ledger running (see the root README), in Development so the
developer tools work. Or run everything, this app included, with `docker compose --profile apps up -d --build`.

| Script | What it does |
| --- | --- |
| `npm run dev` | Development server with hot reload |
| `npm run lint` / `npm run typecheck` / `npm run test` | ESLint, `tsc --noEmit`, Vitest unit tests |
| `npm run build` | Production build (`.next/standalone`, used by the Dockerfile) |
| `npm run api:fetch` | Saves every running service's `/openapi/v1.json` into `openapi/` (stack must be up) |
| `npm run api:types` | Regenerates `src/lib/api-types/*.ts` from `openapi/*.json` |
| `npm run api:check` | Fails if the generated types are not what the documents produce |
| `npm run check` | Lint, typecheck, API types, tests and build, as CI runs them |

## Screens

| Path | What |
| --- | --- |
| `/signup` | Phone → OTP → MPIN (Identity's three registration steps). The Ledger opens the wallet from `identity.user_registered`. |
| `/login` | Phone + MPIN; a new browser is a new device, so one OTP the first time. |
| `/wallet` | Live balance, quick actions, recent activity. |
| `/wallet/send` | Recipient + amount → fee quote → send (one Idempotency-Key per review, so a retry never sends twice) → receipt. |
| `/wallet/history` | Statement with money-in/out and type filters, keyset paging ("Load older"). |
| `/wallet/account` | KYC tier, CNIC verification with NADRA (L0 → L1), per-transaction, daily and monthly limits with what is used. |
| `/ops` | Operations portal, staff only: live dashboard (Saga counts, dead letters, Ledger invariants, `/hubs/ops` feed). |
| `/ops/sagas` | Saga explorer: search a service's Sagas, open one for its data and timeline, each step linked to its Jaeger trace. |
| `/ops/dead-letters` | Bill payments and top-ups waiting for a person: replay, refund or confirm. |
| `/ops/fraud`, `/ops/fraud/cases`, `/ops/fraud/rules` | Compliance and admin: alerts with freeze/unfreeze, cases, the rule editor and tester. |

## How the pieces fit

**Session.** The refresh token never reaches browser JavaScript. `src/app/session/*` are small route
handlers (a backend-for-frontend): they call Identity through the gateway, keep the refresh token in an
httpOnly, SameSite=Strict cookie scoped to `/session`, and hand the browser only the short-lived access
token. The browser keeps that in memory (`src/lib/session.tsx`), refreshes it a minute before it expires,
and on page load asks `/session/refresh` for a new one. Refresh tokens are single use, so tabs take
turns through a Web Lock; two tabs never present the same token (Identity treats reuse as theft).

**API calls** go straight from the browser to the gateway (`NEXT_PUBLIC_API_URL`) with `Authorization: Bearer`
(`src/lib/api.ts`). Errors are RFC 9457 problem responses; the UI shows their `detail`.

**MPIN confirmation.** Sending money (wallet, bank, paying a request) ends with the MPIN
(`src/components/mpin-confirm.tsx`): Identity checks it and returns a 5-minute token for that payment's
Idempotency-Key, sent as `X-Mpin-Confirmation`. The send screen confirms a second key at the same time, so a
retry after a step-up code does not ask for the MPIN again. The MPIN itself is never kept.

**Typed API client.** `src/lib/api-types/*.ts` are generated with openapi-typescript from the services' OpenAPI
documents (committed in `openapi/`); the screens import their response types from there, so a change to a
service's contract shows up as a type error. `npm run api:check` (part of `npm run check`) keeps the two in step.

**Roles.** The access token's `role` claim decides what the app shows (`src/lib/roles.ts`): staff (ops, compliance,
finance, admin) see `/ops`, compliance and admin also the fraud pages. The services enforce the same roles.

**Live balance.** `src/lib/use-wallet-hub.ts` keeps one SignalR connection to `/hubs/wallet` (WebSockets,
token in the query string, through the gateway). The Ledger pushes every new entry of the user's wallets.
The entry's sequence number decides what is newer (`src/lib/live.ts`): late or repeated pushes change nothing,
and a jump in the sequence (missed pushes while offline) or a reconnect makes the screens refetch instead.

**Developer tools** (`NEXT_PUBLIC_DEV_TOOLS=true`, services in Development only): the yellow *Dev SMS
inbox* shows the OTP the stand-in SMS gateway "sent" (`GET /api/identity/dev/sms`), and *Add Rs. 5,000*
posts a test cash-in (`POST /api/wallets/me/dev/top-up`), and *Developer: role* on the account page switches the
signed-in user's role (`PUT /api/identity/dev/role`) to try the operations portal. None of these endpoints exist
outside Development.

## Configuration

| Variable | Used by | Default |
| --- | --- | --- |
| `NEXT_PUBLIC_API_URL` | browser (baked in at build time) | `http://localhost:5000` |
| `API_URL` | Next.js server (`/session/*`) | `NEXT_PUBLIC_API_URL` |
| `NEXT_PUBLIC_DEV_TOOLS` | browser (build time) | off |
| `NEXT_PUBLIC_JAEGER_URL` | browser (build time), trace links in `/ops` | `http://localhost:16686` |
| `NEXT_PUBLIC_GRAFANA_URL` | browser (build time), dashboard link in `/ops` | `http://localhost:3001` |
| `SESSION_COOKIE_SECURE` | Next.js server | `true` in production builds |
