using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddPluralNameToMetaverseObjectType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add PluralName column as nullable first
            migrationBuilder.AddColumn<string>(
                name: "PluralName",
                table: "MetaverseObjectTypes",
                type: "text",
                nullable: true);

            // Copy current Name (which is plural like "Users") to PluralName
            migrationBuilder.Sql("UPDATE \"MetaverseObjectTypes\" SET \"PluralName\" = \"Name\"");

            // Update Name to singular form for built-in types
            // For "Users" -> "User", "Groups" -> "Group"
            migrationBuilder.Sql("UPDATE \"MetaverseObjectTypes\" SET \"Name\" = 'User' WHERE \"Name\" = 'Users'");
            migrationBuilder.Sql("UPDATE \"MetaverseObjectTypes\" SET \"Name\" = 'Group' WHERE \"Name\" = 'Groups'");

            // Make PluralName non-nullable after data migration
            migrationBuilder.AlterColumn<string>(
                name: "PluralName",
                table: "MetaverseObjectTypes",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore plural names from PluralName back to Name
            migrationBuilder.Sql("UPDATE \"MetaverseObjectTypes\" SET \"Name\" = \"PluralName\"");

            migrationBuilder.DropColumn(
                name: "PluralName",
                table: "MetaverseObjectTypes");
        }
    }
}
