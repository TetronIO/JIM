using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddCreatedToSyncRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "Created",
                table: "SyncRules",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "NOW()");

            // For existing rows, set Created to LastUpdated if available
            migrationBuilder.Sql(
                @"UPDATE ""SyncRules""
                  SET ""Created"" = COALESCE(""LastUpdated"", NOW())
                  WHERE ""LastUpdated"" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Created",
                table: "SyncRules");
        }
    }
}
