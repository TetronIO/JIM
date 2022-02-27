using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class AttributeValueAdditions : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ServiceSettings_MetaverseAttributes_SSONameIDAttributeId",
                table: "ServiceSettings");

            migrationBuilder.AlterColumn<Guid>(
                name: "SSONameIDAttributeId",
                table: "ServiceSettings",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<DateTime>(
                name: "Created",
                table: "ServiceSettings",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<bool>(
                name: "BuiltIn",
                table: "MetaverseObjectTypes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<int>(
                name: "IntValue",
                table: "MetaverseObjectAttributeValues",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<DateTime>(
                name: "DateTimeValue",
                table: "MetaverseObjectAttributeValues",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<byte[]>(
                name: "ByteValue",
                table: "MetaverseObjectAttributeValues",
                type: "bytea",
                nullable: true,
                oldClrType: typeof(byte[]),
                oldType: "bytea");

            migrationBuilder.AddColumn<bool>(
                name: "BoolValue",
                table: "MetaverseObjectAttributeValues",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "GuidValue",
                table: "MetaverseObjectAttributeValues",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReferenceValueId",
                table: "MetaverseObjectAttributeValues",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_DateTimeValue",
                table: "MetaverseObjectAttributeValues",
                column: "DateTimeValue");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_GuidValue",
                table: "MetaverseObjectAttributeValues",
                column: "GuidValue");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_IntValue",
                table: "MetaverseObjectAttributeValues",
                column: "IntValue");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_ReferenceValueId",
                table: "MetaverseObjectAttributeValues",
                column: "ReferenceValueId");

            migrationBuilder.AddForeignKey(
                name: "FK_MetaverseObjectAttributeValues_MetaverseObjects_ReferenceVa~",
                table: "MetaverseObjectAttributeValues",
                column: "ReferenceValueId",
                principalTable: "MetaverseObjects",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ServiceSettings_MetaverseAttributes_SSONameIDAttributeId",
                table: "ServiceSettings",
                column: "SSONameIDAttributeId",
                principalTable: "MetaverseAttributes",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MetaverseObjectAttributeValues_MetaverseObjects_ReferenceVa~",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropForeignKey(
                name: "FK_ServiceSettings_MetaverseAttributes_SSONameIDAttributeId",
                table: "ServiceSettings");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectAttributeValues_DateTimeValue",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectAttributeValues_GuidValue",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectAttributeValues_IntValue",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectAttributeValues_ReferenceValueId",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropColumn(
                name: "Created",
                table: "ServiceSettings");

            migrationBuilder.DropColumn(
                name: "BuiltIn",
                table: "MetaverseObjectTypes");

            migrationBuilder.DropColumn(
                name: "BoolValue",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropColumn(
                name: "GuidValue",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropColumn(
                name: "ReferenceValueId",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.AlterColumn<Guid>(
                name: "SSONameIDAttributeId",
                table: "ServiceSettings",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "IntValue",
                table: "MetaverseObjectAttributeValues",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "DateTimeValue",
                table: "MetaverseObjectAttributeValues",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified),
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<byte[]>(
                name: "ByteValue",
                table: "MetaverseObjectAttributeValues",
                type: "bytea",
                nullable: false,
                defaultValue: new byte[0],
                oldClrType: typeof(byte[]),
                oldType: "bytea",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ServiceSettings_MetaverseAttributes_SSONameIDAttributeId",
                table: "ServiceSettings",
                column: "SSONameIDAttributeId",
                principalTable: "MetaverseAttributes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
