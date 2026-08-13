using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class ConnectedSystemAttributeTypeSetByAdministrator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Defaults to false, which is the correct reading of every existing row: until now a data type
            // could only be whatever schema discovery inferred, so nothing already stored was chosen by an
            // administrator. A refresh therefore keeps refreshing every existing attribute exactly as before.
            migrationBuilder.AddColumn<bool>(
                name: "TypeSetByAdministrator",
                table: "ConnectedSystemAttributes",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TypeSetByAdministrator",
                table: "ConnectedSystemAttributes");
        }
    }
}
