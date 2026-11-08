-- Runs once, the first time the Postgres volume is created.
-- Database-per-service: each service owns its data and no service reads another's tables.
-- (To re-run: docker compose -f docker-compose.infra.yml down -v, then up again.)

CREATE DATABASE walletpk_identity;
CREATE DATABASE walletpk_ledger;
CREATE DATABASE walletpk_payments;
CREATE DATABASE walletpk_bills;
CREATE DATABASE walletpk_topup_merchant;
CREATE DATABASE walletpk_agents;
CREATE DATABASE walletpk_fraud;
CREATE DATABASE walletpk_notifications;
CREATE DATABASE walletpk_reconciliation;
CREATE DATABASE walletpk_simulation;
CREATE DATABASE walletpk_mockproviders;
