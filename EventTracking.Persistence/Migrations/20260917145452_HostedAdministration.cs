using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTracking.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HostedAdministration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "can_manage",
                table: "project_memberships",
                type: "boolean",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.AddColumn<int>(
                name: "session_version",
                table: "dashboard_users",
                type: "integer",
                nullable: false,
                defaultValue: 0
            );

            migrationBuilder.CreateTable(
                name: "audit_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    actor = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    project_id = table.Column<string>(
                        type: "character varying(100)",
                        maxLength: 100,
                        nullable: true
                    ),
                    action = table.Column<string>(
                        type: "character varying(100)",
                        maxLength: 100,
                        nullable: false
                    ),
                    target = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    details = table.Column<string>(type: "jsonb", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_records", x => x.id);
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_audit_records_project_id_occurred_at_id",
                table: "audit_records",
                columns: new[] { "project_id", "occurred_at", "id" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "audit_records");

            migrationBuilder.DropColumn(name: "can_manage", table: "project_memberships");

            migrationBuilder.DropColumn(name: "session_version", table: "dashboard_users");
        }
    }
}
