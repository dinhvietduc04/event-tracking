using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTracking.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReliableBrokerOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "broker_outbox",
                columns: table => new
                {
                    project_id = table.Column<string>(
                        type: "character varying(100)",
                        nullable: false
                    ),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    published_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    completed_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    next_attempt_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    dead_lettered_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(
                        type: "character varying(1000)",
                        maxLength: 1000,
                        nullable: true
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_broker_outbox", x => new { x.project_id, x.event_id });
                    table.ForeignKey(
                        name: "FK_broker_outbox_event_identity_project_id_event_id",
                        columns: x => new { x.project_id, x.event_id },
                        principalTable: "event_identity",
                        principalColumns: new[] { "project_id", "event_id" },
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_broker_outbox_completed_at_dead_lettered_at_next_attempt_at~",
                table: "broker_outbox",
                columns: new[]
                {
                    "completed_at",
                    "dead_lettered_at",
                    "next_attempt_at",
                    "created_at",
                }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "broker_outbox");
        }
    }
}
