namespace WalletPK.Ledger.Infrastructure.Persistence.Sql;

/// <summary>
/// PostgreSQL triggers that enforce the ledger's core rules inside the database itself (M3.2), so they
/// hold even if the application has a bug, two services write at once, or someone runs SQL by hand.
/// Installed by the InitialLedger migration.
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
