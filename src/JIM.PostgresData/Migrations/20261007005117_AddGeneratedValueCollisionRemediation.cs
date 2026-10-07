using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddGeneratedValueCollisionRemediation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BaseValue",
                table: "GeneratedValueAssignments",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GeneratedValueRevisionsPending",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MetaverseObjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    MetaverseAttributeId = table.Column<int>(type: "integer", nullable: false),
                    RemediatingActivityRunProfileExecutionItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReasonCode = table.Column<int>(type: "integer", nullable: false),
                    RejectedByConnectedSystemId = table.Column<int>(type: "integer", nullable: true),
                    RejectedByConnectedSystemName = table.Column<string>(type: "text", nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GeneratedValueRevisionsPending", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GeneratedValueRevisionsPending_MetaverseAttributes_Metavers~",
                        column: x => x.MetaverseAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GeneratedValueRevisionsPending_MetaverseObjects_MetaverseOb~",
                        column: x => x.MetaverseObjectId,
                        principalTable: "MetaverseObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedValueRevisionsPending_Created",
                table: "GeneratedValueRevisionsPending",
                column: "Created");

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedValueRevisionsPending_MetaverseAttributeId",
                table: "GeneratedValueRevisionsPending",
                column: "MetaverseAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedValueRevisionsPending_MvoId_AttributeId",
                table: "GeneratedValueRevisionsPending",
                columns: new[] { "MetaverseObjectId", "MetaverseAttributeId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GeneratedValueRevisionsPending");

            migrationBuilder.DropColumn(
                name: "BaseValue",
                table: "GeneratedValueAssignments");
        }
    }
}
