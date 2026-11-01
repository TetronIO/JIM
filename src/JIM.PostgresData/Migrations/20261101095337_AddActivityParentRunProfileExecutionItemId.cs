using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddActivityParentRunProfileExecutionItemId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentRunProfileExecutionItemId",
                table: "Activities",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Activities_ParentRunProfileExecutionItemId",
                table: "Activities",
                column: "ParentRunProfileExecutionItemId",
                filter: "\"ParentRunProfileExecutionItemId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Activities_ParentRunProfileExecutionItemId",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "ParentRunProfileExecutionItemId",
                table: "Activities");
        }
    }
}
