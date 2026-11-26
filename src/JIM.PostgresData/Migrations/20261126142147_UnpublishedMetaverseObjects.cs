using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class UnpublishedMetaverseObjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UnpublishedMetaverseObjects",
                columns: table => new
                {
                    MetaverseObjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnpublishedMetaverseObjects", x => x.MetaverseObjectId);
                    table.ForeignKey(
                        name: "FK_UnpublishedMetaverseObjects_MetaverseObjects_MetaverseObjec~",
                        column: x => x.MetaverseObjectId,
                        principalTable: "MetaverseObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UnpublishedMetaverseObjects");
        }
    }
}
