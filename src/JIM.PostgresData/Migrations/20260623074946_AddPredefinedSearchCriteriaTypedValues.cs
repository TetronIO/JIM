using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddPredefinedSearchCriteriaTypedValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "StringValue",
                table: "PredefinedSearchCriteria",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<bool>(
                name: "BoolValue",
                table: "PredefinedSearchCriteria",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CaseSensitive",
                table: "PredefinedSearchCriteria",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DateTimeValue",
                table: "PredefinedSearchCriteria",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "GuidValue",
                table: "PredefinedSearchCriteria",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IntValue",
                table: "PredefinedSearchCriteria",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LongValue",
                table: "PredefinedSearchCriteria",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BoolValue",
                table: "PredefinedSearchCriteria");

            migrationBuilder.DropColumn(
                name: "CaseSensitive",
                table: "PredefinedSearchCriteria");

            migrationBuilder.DropColumn(
                name: "DateTimeValue",
                table: "PredefinedSearchCriteria");

            migrationBuilder.DropColumn(
                name: "GuidValue",
                table: "PredefinedSearchCriteria");

            migrationBuilder.DropColumn(
                name: "IntValue",
                table: "PredefinedSearchCriteria");

            migrationBuilder.DropColumn(
                name: "LongValue",
                table: "PredefinedSearchCriteria");

            migrationBuilder.AlterColumn<string>(
                name: "StringValue",
                table: "PredefinedSearchCriteria",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
