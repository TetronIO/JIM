using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddActivityStatCounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RunProfileExecutionStatsFinalised",
                table: "Activities",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ActivityStatCounters",
                columns: table => new
                {
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Dimension = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Count = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityStatCounters", x => new { x.ActivityId, x.Dimension, x.Key });
                    table.ForeignKey(
                        name: "FK_ActivityStatCounters_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityStatCounters");

            migrationBuilder.DropColumn(
                name: "RunProfileExecutionStatsFinalised",
                table: "Activities");
        }
    }
}
