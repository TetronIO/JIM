using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class ChangeHistoryAttributePlurality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AttributePlurality",
                table: "MetaverseObjectChangeAttributes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AttributePlurality",
                table: "ConnectedSystemObjectChangeAttributes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // A change record's attribute rows copy the attribute's plurality from now on, beside its name and type (#2936),
            // so a deleted multi-valued attribute's history no longer reads as single-valued replacements. Existing rows
            // take it from the attribute while it is still there; only multi-valued ones need writing, since the column
            // defaults to single-valued. Rows whose attribute has already gone stay single-valued, as they read today.
            migrationBuilder.Sql(
                @"UPDATE ""ConnectedSystemObjectChangeAttributes"" ca SET ""AttributePlurality"" = a.""AttributePlurality""
                  FROM ""ConnectedSystemAttributes"" a
                  WHERE a.""Id"" = ca.""AttributeId"" AND a.""AttributePlurality"" <> 0;");
            migrationBuilder.Sql(
                @"UPDATE ""MetaverseObjectChangeAttributes"" ca SET ""AttributePlurality"" = a.""AttributePlurality""
                  FROM ""MetaverseAttributes"" a
                  WHERE a.""Id"" = ca.""AttributeId"" AND a.""AttributePlurality"" <> 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttributePlurality",
                table: "MetaverseObjectChangeAttributes");

            migrationBuilder.DropColumn(
                name: "AttributePlurality",
                table: "ConnectedSystemObjectChangeAttributes");
        }
    }
}
