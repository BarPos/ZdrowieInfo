using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZdrowieInfo.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class UpdateRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "update_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    to_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    requested_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_update_runs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_update_runs_requested_at",
                table: "update_runs",
                column: "requested_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "update_runs");
        }
    }
}
