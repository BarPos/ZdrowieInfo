using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZdrowieInfo.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReportPeriodsAndSharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_reports_date",
                table: "reports");

            migrationBuilder.AddColumn<List<long>>(
                name: "input_item_ids",
                table: "reports",
                type: "bigint[]",
                nullable: false,
                defaultValueSql: "'{}'::bigint[]");

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "reports",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Daily");

            migrationBuilder.AddColumn<DateOnly>(
                name: "period_start",
                table: "reports",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.AddColumn<string>(
                name: "share_token",
                table: "reports",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "shared_at",
                table: "reports",
                type: "timestamp with time zone",
                nullable: true);

            // Dotychczasowe raporty są dzienne: początek okresu = data, wejście = wpisy, które raport „zabrał”.
            migrationBuilder.Sql("""
                UPDATE reports r SET period_start = r.date,
                    input_item_ids = coalesce((SELECT array_agg(i.id ORDER BY i.id) FROM source_items i WHERE i.report_id = r.id), '{}');
                """);

            migrationBuilder.CreateIndex(
                name: "ix_reports_date",
                table: "reports",
                column: "date");

            migrationBuilder.CreateIndex(
                name: "ix_reports_kind_period_start_date",
                table: "reports",
                columns: new[] { "kind", "period_start", "date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reports_share_token",
                table: "reports",
                column: "share_token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_reports_date",
                table: "reports");

            migrationBuilder.DropIndex(
                name: "ix_reports_kind_period_start_date",
                table: "reports");

            migrationBuilder.DropIndex(
                name: "ix_reports_share_token",
                table: "reports");

            migrationBuilder.DropColumn(
                name: "input_item_ids",
                table: "reports");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "reports");

            migrationBuilder.DropColumn(
                name: "period_start",
                table: "reports");

            migrationBuilder.DropColumn(
                name: "share_token",
                table: "reports");

            migrationBuilder.DropColumn(
                name: "shared_at",
                table: "reports");

            migrationBuilder.CreateIndex(
                name: "ix_reports_date",
                table: "reports",
                column: "date",
                unique: true);
        }
    }
}
