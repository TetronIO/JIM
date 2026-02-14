using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduleContextToActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ScheduleExecutionId",
                table: "Activities",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScheduleStepIndex",
                table: "Activities",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ScheduleExecutionId",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "ScheduleStepIndex",
                table: "Activities");
        }
    }
}
