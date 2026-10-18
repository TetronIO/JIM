using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddTrustedForToTrustedCertificates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every certificate stored before #2363 was trusted by Connectors and by nothing else, so existing rows
            // become Connectors only (CertificateTrustPurpose.Connectors = 1). Nothing starts vouching for sign-in
            // without an administrator choosing it.
            migrationBuilder.AddColumn<int>(
                name: "TrustedFor",
                table: "TrustedCertificates",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TrustedFor",
                table: "TrustedCertificates");
        }
    }
}
