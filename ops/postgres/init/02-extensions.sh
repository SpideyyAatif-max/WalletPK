#!/bin/bash
# Enables pg_stat_statements (query performance stats) in every WalletPK database.
set -euo pipefail
for db in walletpk walletpk_identity walletpk_ledger walletpk_payments walletpk_bills \
          walletpk_topup_merchant walletpk_agents walletpk_fraud walletpk_notifications \
          walletpk_reconciliation walletpk_simulation walletpk_mockproviders; do
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$db" \
       -c "CREATE EXTENSION IF NOT EXISTS pg_stat_statements;"
done
