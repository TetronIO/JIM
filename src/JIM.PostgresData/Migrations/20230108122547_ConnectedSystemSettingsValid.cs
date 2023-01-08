using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class ConnectedSystemSettingsValid : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SettingValuesValidated",
                table: "ConnectedSystems",
                newName: "SettingValuesValid");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SettingValuesValid",
                table: "ConnectedSystems",
                newName: "SettingValuesValidated");
        }
    }
}
