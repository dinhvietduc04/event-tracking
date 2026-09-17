using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTracking.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    event_count = table.Column<long>(type: "bigint", nullable: false),
                    stored_bytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "storage_state",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    profile = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_storage_state", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "credentials",
                columns: table => new
                {
                    key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    project_id = table.Column<string>(type: "character varying(100)", nullable: false),
                    permissions = table.Column<string[]>(type: "text[]", nullable: false),
                    revoked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credentials", x => x.key_hash);
                    table.ForeignKey(
                        name: "FK_credentials_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "event_identity",
                columns: table => new
                {
                    project_id = table.Column<string>(type: "character varying(100)", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    stored_bytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_identity", x => new { x.project_id, x.event_id });
                    table.ForeignKey(
                        name: "FK_event_identity_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    project_id = table.Column<string>(type: "character varying(100)", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    user_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    anonymous_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    session_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    properties = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => new { x.project_id, x.event_id });
                    table.ForeignKey(
                        name: "FK_events_event_identity_project_id_event_id",
                        columns: x => new { x.project_id, x.event_id },
                        principalTable: "event_identity",
                        principalColumns: new[] { "project_id", "event_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "inbox",
                columns: table => new
                {
                    project_id = table.Column<string>(type: "character varying(100)", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox", x => new { x.project_id, x.event_id });
                    table.ForeignKey(
                        name: "FK_inbox_event_identity_project_id_event_id",
                        columns: x => new { x.project_id, x.event_id },
                        principalTable: "event_identity",
                        principalColumns: new[] { "project_id", "event_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_credentials_project_id",
                table: "credentials",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_event_identity_occurred_at",
                table: "event_identity",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "IX_events_project_id_event_type_occurred_at",
                table: "events",
                columns: new[] { "project_id", "event_type", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_events_project_id_occurred_at_event_id",
                table: "events",
                columns: new[] { "project_id", "occurred_at", "event_id" });

            migrationBuilder.CreateIndex(
                name: "IX_events_project_id_user_id_occurred_at_event_id",
                table: "events",
                columns: new[] { "project_id", "user_id", "occurred_at", "event_id" });

            migrationBuilder.CreateIndex(
                name: "IX_events_properties",
                table: "events",
                column: "properties")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_inbox_received_at",
                table: "inbox",
                column: "received_at",
                filter: "processed_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "credentials");

            migrationBuilder.DropTable(
                name: "events");

            migrationBuilder.DropTable(
                name: "inbox");

            migrationBuilder.DropTable(
                name: "storage_state");

            migrationBuilder.DropTable(
                name: "event_identity");

            migrationBuilder.DropTable(
                name: "projects");
        }
    }
}
