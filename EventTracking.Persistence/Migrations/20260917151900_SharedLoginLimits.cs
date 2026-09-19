using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTracking.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SharedLoginLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "login_rate_limits",
                columns: table => new
                {
                    bucket = table.Column<string>(
                        type: "character varying(20)",
                        maxLength: 20,
                        nullable: false
                    ),
                    window_start = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_login_rate_limits", x => x.bucket);
                }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "login_rate_limits");
        }
    }
}
