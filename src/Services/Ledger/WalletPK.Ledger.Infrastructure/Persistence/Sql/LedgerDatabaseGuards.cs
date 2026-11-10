namespace WalletPK.Ledger.Infrastructure.Persistence.Sql;

/// <summary>
/// PostgreSQL triggers that enforce the ledger's core rules inside the database itself (M3.2), so they
/// hold even if the application has a bug, two services write at once, or someone runs SQL by hand.
/// Installed by the InitialLedger migration (fencing: AddHoldsLocksIdempotency; audit log: AddAuditChain).
/// <list type="number">
/// <item><b>Balanced journals:</b> at COMMIT, every journal must have at least two entries, its debits
/// must equal its credits, and the header amount must equal the debits. Deferred constraint triggers run
/// after all of a transaction's rows are inserted, so the check sees the complete journal.</item>
/// <item><b>Append-only history:</b> journal entries and journal transactions can never be updated,
/// deleted or truncated. Corrections are new journals (reversals), so history is never rewritten.</item>
/// </list>
/// The "no wallet below zero" rule is a check constraint on accounts (see AccountConfiguration).
/// </summary>
public static class LedgerDatabaseGuards
{
    public const string Install = """
        CREATE OR REPLACE FUNCTION ledger_assert_journal_balanced(p_transaction_id uuid) RETURNS void
        LANGUAGE plpgsql AS $$
        DECLARE
            v_debits  bigint;
            v_credits bigint;
            v_count   integer;
            v_amount  bigint;
        BEGIN
            SELECT COALESCE(SUM(amount) FILTER (WHERE direction = 'Debit'), 0),
                   COALESCE(SUM(amount) FILTER (WHERE direction = 'Credit'), 0),
                   COUNT(*)
              INTO v_debits, v_credits, v_count
              FROM journal_entries
             WHERE transaction_id = p_transaction_id;

            SELECT amount INTO v_amount FROM journal_transactions WHERE id = p_transaction_id;

            IF v_count < 2 OR v_debits <> v_credits OR v_amount IS DISTINCT FROM v_debits THEN
                RAISE EXCEPTION 'Unbalanced journal %: % entries, debits %, credits %, header amount %',
                    p_transaction_id, v_count, v_debits, v_credits, v_amount
                    USING ERRCODE = 'check_violation';
            END IF;
        END;
        $$;

        CREATE OR REPLACE FUNCTION ledger_trg_entry_balanced() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            PERFORM ledger_assert_journal_balanced(NEW.transaction_id);
            RETURN NULL;
        END;
        $$;

        CREATE OR REPLACE FUNCTION ledger_trg_journal_balanced() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            PERFORM ledger_assert_journal_balanced(NEW.id);
            RETURN NULL;
        END;
        $$;

        CREATE CONSTRAINT TRIGGER trg_journal_entries_balanced
            AFTER INSERT ON journal_entries
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW EXECUTE FUNCTION ledger_trg_entry_balanced();

        CREATE CONSTRAINT TRIGGER trg_journal_transactions_balanced
            AFTER INSERT ON journal_transactions
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW EXECUTE FUNCTION ledger_trg_journal_balanced();

        CREATE OR REPLACE FUNCTION ledger_trg_append_only() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION '% is append-only: % is not allowed. Post a reversal journal instead.', TG_TABLE_NAME, TG_OP
                USING ERRCODE = 'restrict_violation';
        END;
        $$;

        CREATE TRIGGER trg_journal_entries_append_only
            BEFORE UPDATE OR DELETE ON journal_entries
            FOR EACH ROW EXECUTE FUNCTION ledger_trg_append_only();

        CREATE TRIGGER trg_journal_entries_no_truncate
            BEFORE TRUNCATE ON journal_entries
            FOR EACH STATEMENT EXECUTE FUNCTION ledger_trg_append_only();

        CREATE TRIGGER trg_journal_transactions_append_only
            BEFORE UPDATE OR DELETE ON journal_transactions
            FOR EACH ROW EXECUTE FUNCTION ledger_trg_append_only();

        CREATE TRIGGER trg_journal_transactions_no_truncate
            BEFORE TRUNCATE ON journal_transactions
            FOR EACH STATEMENT EXECUTE FUNCTION ledger_trg_append_only();
        """;

    /// <summary>
    /// M3.6: an account's lock_token (the fencing token of the last wallet lock that wrote to it) may only
    /// grow. A server whose Redis lock expired carries an older token and is refused by the database itself.
    /// </summary>
    public const string InstallFencing = """
        CREATE OR REPLACE FUNCTION ledger_trg_lock_token_monotonic() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF NEW.lock_token < OLD.lock_token THEN
                RAISE EXCEPTION 'Stale wallet lock on account %: fencing token % is older than %', OLD.id, NEW.lock_token, OLD.lock_token
                    USING ERRCODE = 'check_violation';
            END IF;
            RETURN NEW;
        END;
        $$;

        CREATE TRIGGER trg_accounts_lock_token_monotonic
            BEFORE UPDATE OF lock_token ON accounts
            FOR EACH ROW EXECUTE FUNCTION ledger_trg_lock_token_monotonic();
        """;

    public const string UninstallFencing = """
        DROP TRIGGER IF EXISTS trg_accounts_lock_token_monotonic ON accounts;
        DROP FUNCTION IF EXISTS ledger_trg_lock_token_monotonic();
        """;

    /// <summary>
    /// M3.10: the audit log is append-only. The one change ever allowed is sealing: setting the chain
    /// columns of a row that has none yet, with every content column unchanged. A sealed row can never
    /// change again, and no row can be deleted or truncated.
    /// </summary>
    public const string InstallAudit = """
        CREATE OR REPLACE FUNCTION ledger_trg_audit_log_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'UPDATE'
               AND OLD.hash IS NULL AND NEW.hash IS NOT NULL
               AND NEW.id = OLD.id
               AND NEW.occurred_at = OLD.occurred_at
               AND NEW.event_type = OLD.event_type
               AND NEW.subject_type = OLD.subject_type
               AND NEW.subject_id = OLD.subject_id
               AND NEW.actor = OLD.actor
               AND NEW.correlation_id IS NOT DISTINCT FROM OLD.correlation_id
               AND NEW.payload = OLD.payload THEN
                RETURN NEW;
            END IF;

            RAISE EXCEPTION 'audit_log is append-only: % is not allowed (only sealing an unsealed row is)', TG_OP
                USING ERRCODE = 'restrict_violation';
        END;
        $$;

        CREATE TRIGGER trg_audit_log_guard
            BEFORE UPDATE OR DELETE ON audit_log
            FOR EACH ROW EXECUTE FUNCTION ledger_trg_audit_log_guard();

        CREATE TRIGGER trg_audit_log_no_truncate
            BEFORE TRUNCATE ON audit_log
            FOR EACH STATEMENT EXECUTE FUNCTION ledger_trg_append_only();
        """;

    public const string UninstallAudit = """
        DROP TRIGGER IF EXISTS trg_audit_log_no_truncate ON audit_log;
        DROP TRIGGER IF EXISTS trg_audit_log_guard ON audit_log;
        DROP FUNCTION IF EXISTS ledger_trg_audit_log_guard();
        """;

    /// <summary>
    /// Journals posted before the audit log existed get a "journal.posted" row each (actor
    /// "system:backfill"), in posting order, so every journal is audited and the sealer chains them first.
    /// </summary>
    public const string BackfillAudit = """
        INSERT INTO audit_log (occurred_at, event_type, subject_type, subject_id, actor, correlation_id, payload)
        SELECT j.posted_at,
               'journal.posted',
               'journal',
               j.id::text,
               'system:backfill',
               NULL,
               json_build_object(
                   'type', j.type,
                   'description', j.description,
                   'reference', j.reference,
                   'amount', j.amount,
                   'currency', j.currency,
                   'postedAt', j.posted_at,
                   'entries', (SELECT json_agg(json_build_object(
                                       'line', e.line_number,
                                       'accountId', e.account_id,
                                       'direction', e.direction,
                                       'amount', e.amount,
                                       'balanceAfter', e.balance_after) ORDER BY e.line_number)
                                 FROM journal_entries e
                                WHERE e.transaction_id = j.id))::text
          FROM journal_transactions j
         ORDER BY j.posted_at, j.id;
        """;

    /// <summary>
    /// M4.2: numbers the entries posted before per-account sequences existed (1, 2, 3 ... per account, in
    /// posting order) and sets each account's counter. The append-only guard is paused for this one
    /// statement, inside the migration's transaction.
    /// </summary>
    public const string BackfillEntrySequences = """
        ALTER TABLE journal_entries DISABLE TRIGGER trg_journal_entries_append_only;

        UPDATE journal_entries e
           SET account_sequence = numbered.sequence
          FROM (SELECT id, ROW_NUMBER() OVER (PARTITION BY account_id ORDER BY id) AS sequence
                  FROM journal_entries) numbered
         WHERE numbered.id = e.id;

        ALTER TABLE journal_entries ENABLE TRIGGER trg_journal_entries_append_only;

        -- The 0 default only existed to add the column; every new entry must state its number.
        ALTER TABLE journal_entries ALTER COLUMN account_sequence DROP DEFAULT;

        UPDATE accounts a
           SET entry_sequence = counts.entries
          FROM (SELECT account_id, COUNT(*) AS entries FROM journal_entries GROUP BY account_id) counts
         WHERE counts.account_id = a.id;
        """;

    public const string Uninstall = """
        DROP TRIGGER IF EXISTS trg_journal_transactions_no_truncate ON journal_transactions;
        DROP TRIGGER IF EXISTS trg_journal_transactions_append_only ON journal_transactions;
        DROP TRIGGER IF EXISTS trg_journal_entries_no_truncate ON journal_entries;
        DROP TRIGGER IF EXISTS trg_journal_entries_append_only ON journal_entries;
        DROP TRIGGER IF EXISTS trg_journal_transactions_balanced ON journal_transactions;
        DROP TRIGGER IF EXISTS trg_journal_entries_balanced ON journal_entries;
        DROP FUNCTION IF EXISTS ledger_trg_append_only();
        DROP FUNCTION IF EXISTS ledger_trg_journal_balanced();
        DROP FUNCTION IF EXISTS ledger_trg_entry_balanced();
        DROP FUNCTION IF EXISTS ledger_assert_journal_balanced(uuid);
        """;
}
