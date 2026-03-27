using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class PreventAttributeChangeCascadeDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjectChangeAttributes_ConnectedSystemAttrib~",
                table: "ConnectedSystemObjectChangeAttributes");

            migrationBuilder.DropForeignKey(
                name: "FK_MetaverseObjectChangeAttributes_MetaverseAttributes_Attribu~",
                table: "MetaverseObjectChangeAttributes");

            migrationBuilder.AlterColumn<int>(
                name: "AttributeId",
                table: "MetaverseObjectChangeAttributes",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<string>(
                name: "AttributeName",
                table: "MetaverseObjectChangeAttributes",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "AttributeType",
                table: "MetaverseObjectChangeAttributes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<int>(
                name: "AttributeId",
                table: "ConnectedSystemObjectChangeAttributes",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<string>(
                name: "AttributeName",
                table: "ConnectedSystemObjectChangeAttributes",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "AttributeType",
                table: "ConnectedSystemObjectChangeAttributes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemObjectChangeAttributes_ConnectedSystemAttrib~",
                table: "ConnectedSystemObjectChangeAttributes",
                column: "AttributeId",
                principalTable: "ConnectedSystemAttributes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_MetaverseObjectChangeAttributes_MetaverseAttributes_Attribu~",
                table: "MetaverseObjectChangeAttributes",
                column: "AttributeId",
                principalTable: "MetaverseAttributes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjectChangeAttributes_ConnectedSystemAttrib~",
                table: "ConnectedSystemObjectChangeAttributes");

            migrationBuilder.DropForeignKey(
                name: "FK_MetaverseObjectChangeAttributes_MetaverseAttributes_Attribu~",
                table: "MetaverseObjectChangeAttributes");

            migrationBuilder.DropColumn(
                name: "AttributeName",
                table: "MetaverseObjectChangeAttributes");

            migrationBuilder.DropColumn(
                name: "AttributeType",
                table: "MetaverseObjectChangeAttributes");

            migrationBuilder.DropColumn(
                name: "AttributeName",
                table: "ConnectedSystemObjectChangeAttributes");

            migrationBuilder.DropColumn(
                name: "AttributeType",
                table: "ConnectedSystemObjectChangeAttributes");

            migrationBuilder.AlterColumn<int>(
                name: "AttributeId",
                table: "MetaverseObjectChangeAttributes",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "AttributeId",
                table: "ConnectedSystemObjectChangeAttributes",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemObjectChangeAttributes_ConnectedSystemAttrib~",
                table: "ConnectedSystemObjectChangeAttributes",
                column: "AttributeId",
                principalTable: "ConnectedSystemAttributes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_MetaverseObjectChangeAttributes_MetaverseAttributes_Attribu~",
                table: "MetaverseObjectChangeAttributes",
                column: "AttributeId",
                principalTable: "MetaverseAttributes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
