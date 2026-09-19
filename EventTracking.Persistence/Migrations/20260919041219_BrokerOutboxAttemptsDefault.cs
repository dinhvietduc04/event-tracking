using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTracking.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BrokerOutboxAttemptsDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "attempts",
                table: "broker_outbox",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "attempts",
                table: "broker_outbox",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 0);
        }
    }
}
