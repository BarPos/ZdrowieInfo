using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZdrowieInfo.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReportPdfError : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "pdf_error",
                table: "reports",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pdf_error",
                table: "reports");
        }
    }
}
