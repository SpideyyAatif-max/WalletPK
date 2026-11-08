using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WalletPK.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddKycLimitsOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "kyc_profiles",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cnic_ciphertext = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    cnic_lookup_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    cnic_masked = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    full_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    date_of_birth = table.Column<DateOnly>(type: "date", nullable: false),
                    verification_reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kyc_profiles", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_kyc_profiles_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    event_version = table.Column<int>(type: "integer", nullable: false),
                    topic = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    partition_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    causation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    publish_attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_kyc_profiles_cnic_lookup_hash",
                table: "kyc_profiles",
                column: "cnic_lookup_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_unpublished",
                table: "outbox_messages",
                column: "created_at",
                filter: "published_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "kyc_profiles");

            migrationBuilder.DropTable(
                name: "outbox_messages");
        }
    }
}
