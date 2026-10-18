using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddScimJournalIgnoredAttributes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "IgnoredAttributes",
                table: "ScimStoreJournalEntries",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IgnoredAttributes",
                table: "ScimStoreJournalEntries");
        }
    }
}
