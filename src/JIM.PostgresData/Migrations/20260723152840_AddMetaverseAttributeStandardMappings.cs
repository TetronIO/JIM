using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddMetaverseAttributeStandardMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MetaverseAttributeStandardMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MetaverseAttributeId = table.Column<int>(type: "integer", nullable: false),
                    Standard = table.Column<int>(type: "integer", nullable: false),
                    CounterpartName = table.Column<string>(type: "text", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetaverseAttributeStandardMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetaverseAttributeStandardMappings_MetaverseAttributes_Meta~",
                        column: x => x.MetaverseAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseAttributeStandardMappings_Attribute_Standard_Name",
                table: "MetaverseAttributeStandardMappings",
                columns: new[] { "MetaverseAttributeId", "Standard", "CounterpartName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MetaverseAttributeStandardMappings");
        }
    }
}
