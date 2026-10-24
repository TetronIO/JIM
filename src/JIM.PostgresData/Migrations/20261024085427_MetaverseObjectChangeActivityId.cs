using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class MetaverseObjectChangeActivityId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ActivityId",
                table: "MetaverseObjectChanges",
                type: "uuid",
                nullable: true);

            // Changes a synchronisation recorded already name their Activity through their execution item, so they
            // are linked here; before the index exists, which is cheaper than maintaining it row by row. Changes
            // recorded without an item (deletions, sign-in creation) have nothing to link from and stay null.
            migrationBuilder.Sql(@"
                UPDATE ""MetaverseObjectChanges"" c
                SET ""ActivityId"" = r.""ActivityId""
                FROM ""ActivityRunProfileExecutionItems"" r
                WHERE r.""Id"" = c.""ActivityRunProfileExecutionItemId"";");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectChanges_ActivityId",
                table: "MetaverseObjectChanges",
                column: "ActivityId");

            migrationBuilder.AddForeignKey(
                name: "FK_MetaverseObjectChanges_Activities_ActivityId",
                table: "MetaverseObjectChanges",
                column: "ActivityId",
                principalTable: "Activities",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MetaverseObjectChanges_Activities_ActivityId",
                table: "MetaverseObjectChanges");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectChanges_ActivityId",
                table: "MetaverseObjectChanges");

            migrationBuilder.DropColumn(
                name: "ActivityId",
                table: "MetaverseObjectChanges");
        }
    }
}
