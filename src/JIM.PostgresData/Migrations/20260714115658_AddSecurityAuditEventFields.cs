using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurityAuditEventFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AggregationWindowStart",
                table: "Activities",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApiKeyPrefix",
                table: "Activities",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "Activities",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientIpAddress",
                table: "Activities",
                type: "character varying(45)",
                maxLength: 45,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FirstSeen",
                table: "Activities",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSeen",
                table: "Activities",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecurityEventReason",
                table: "Activities",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Activities_SecurityAggregation_Unique",
                table: "Activities",
                columns: new[] { "TargetType", "ApiKeyPrefix", "ClientIpAddress", "SecurityEventReason", "AggregationWindowStart" },
                unique: true,
                filter: "\"AggregationWindowStart\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_TargetType_Created",
                table: "Activities",
                columns: new[] { "TargetType", "Created" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Activities_SecurityAggregation_Unique",
                table: "Activities");

            migrationBuilder.DropIndex(
                name: "IX_Activities_TargetType_Created",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "AggregationWindowStart",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "ApiKeyPrefix",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "ClientIpAddress",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "FirstSeen",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "LastSeen",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "SecurityEventReason",
                table: "Activities");
        }
    }
}
