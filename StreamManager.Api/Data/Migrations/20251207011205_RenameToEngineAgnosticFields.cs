using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamManager.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameToEngineAgnosticFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KsqlStreamName",
                table: "StreamDefinitions");

            migrationBuilder.RenameColumn(
                name: "KsqlScript",
                table: "StreamDefinitions",
                newName: "SqlScript");

            migrationBuilder.RenameColumn(
                name: "KsqlQueryId",
                table: "StreamDefinitions",
                newName: "StreamName");

            migrationBuilder.AddColumn<string>(
                name: "JobId",
                table: "StreamDefinitions",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "JobId",
                table: "StreamDefinitions");

            migrationBuilder.RenameColumn(
                name: "StreamName",
                table: "StreamDefinitions",
                newName: "KsqlQueryId");

            migrationBuilder.RenameColumn(
                name: "SqlScript",
                table: "StreamDefinitions",
                newName: "KsqlScript");

            migrationBuilder.AddColumn<string>(
                name: "KsqlStreamName",
                table: "StreamDefinitions",
                type: "text",
                nullable: true);
        }
    }
}
