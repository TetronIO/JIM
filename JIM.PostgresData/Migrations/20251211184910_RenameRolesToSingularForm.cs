using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class RenameRolesToSingularForm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rename built-in roles from plural to singular form
            migrationBuilder.Sql("UPDATE \"Roles\" SET \"Name\" = 'Administrator' WHERE \"Name\" = 'Administrators'");
            migrationBuilder.Sql("UPDATE \"Roles\" SET \"Name\" = 'User' WHERE \"Name\" = 'Users'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revert to plural form
            migrationBuilder.Sql("UPDATE \"Roles\" SET \"Name\" = 'Administrators' WHERE \"Name\" = 'Administrator'");
            migrationBuilder.Sql("UPDATE \"Roles\" SET \"Name\" = 'Users' WHERE \"Name\" = 'User'");
        }
    }
}
