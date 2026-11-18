using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WalletPK.Payments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWalletPaymentIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Lists of wallet payments (M6.1, M6.2): each a partial expression index over the Saga's JSON data.
            migrationBuilder.Sql("""
                CREATE INDEX ix_saga_instances_p2p_sender
                    ON saga_instances ((data->>'senderUserId'), created_at DESC)
                 WHERE saga_type = 'p2p-transfer';

                CREATE INDEX ix_saga_instances_request_requester
                    ON saga_instances ((data->>'requesterUserId'), created_at DESC)
                 WHERE saga_type = 'money-request';

                CREATE INDEX ix_saga_instances_request_payer
                    ON saga_instances ((data->>'payerWalletNumber'), created_at DESC)
                 WHERE saga_type = 'money-request';

                CREATE INDEX ix_saga_instances_request_split
                    ON saga_instances ((data->>'splitId'), created_at DESC)
                 WHERE saga_type = 'money-request' AND data->>'splitId' IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS ix_saga_instances_request_split;
                DROP INDEX IF EXISTS ix_saga_instances_request_payer;
                DROP INDEX IF EXISTS ix_saga_instances_request_requester;
                DROP INDEX IF EXISTS ix_saga_instances_p2p_sender;
                """);
        }
    }
}
