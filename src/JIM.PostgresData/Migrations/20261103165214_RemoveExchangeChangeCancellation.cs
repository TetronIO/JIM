using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class RemoveExchangeChangeCancellation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "PendingExchangeChanges");

            migrationBuilder.DropColumn(
                name: "CancelledById",
                table: "PendingExchangeChanges");

            migrationBuilder.DropColumn(
                name: "CancelledByName",
                table: "PendingExchangeChanges");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAt",
                table: "PendingExchangeChanges",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CancelledById",
                table: "PendingExchangeChanges",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancelledByName",
                table: "PendingExchangeChanges",
                type: "text",
                nullable: true);
        }
    }
}
