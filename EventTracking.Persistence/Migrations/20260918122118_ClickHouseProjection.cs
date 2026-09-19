using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTracking.Persistence.Migrations
{
    public partial class ClickHouseProjection : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "clickhouse_projection",
                columns: table => new
                {
                    project_id = table.Column<string>(type: "character varying(100)", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_clickhouse_projection", x => new { x.project_id, x.event_id });
                    table.ForeignKey(
                        name: "FK_clickhouse_projection_event_identity_project_id_event_id",
                        columns: x => new { x.project_id, x.event_id },
                        principalTable: "event_identity",
                        principalColumns: new[] { "project_id", "event_id" },
                        onDelete: ReferentialAction.Cascade);
                });
            migrationBuilder.CreateIndex(
                name: "IX_clickhouse_projection_completed_at_created_at",
                table: "clickhouse_projection",
                columns: new[] { "completed_at", "created_at" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
            => migrationBuilder.DropTable(name: "clickhouse_projection");
    }
}
