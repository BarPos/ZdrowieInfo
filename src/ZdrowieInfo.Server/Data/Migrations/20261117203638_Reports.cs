using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ZdrowieInfo.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Reports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "report_id",
                table: "source_items",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "reported_at",
                table: "source_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "api_keys",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    prefix = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    secret_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    scopes = table.Column<List<string>>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "reports",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    is_empty = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    summary = table.Column<string>(type: "text", nullable: false),
                    producer = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    body = table.Column<string>(type: "jsonb", nullable: false),
                    sources_state = table.Column<string>(type: "jsonb", nullable: false),
                    ai_usage = table.Column<string>(type: "jsonb", nullable: true),
                    generation_attempts = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    validation_issues = table.Column<List<string>>(type: "text[]", nullable: false),
                    tex_path = table.Column<string>(type: "text", nullable: true),
                    pdf_path = table.Column<string>(type: "text", nullable: true),
                    push_sent = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reports", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "report_findings",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    report_id = table.Column<long>(type: "bigint", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    importance = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    headline = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_report_findings", x => x.id);
                    table.ForeignKey(
                        name: "fk_report_findings_reports_report_id",
                        column: x => x.report_id,
                        principalTable: "reports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "finding_citations",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    finding_id = table.Column<long>(type: "bigint", nullable: false),
                    source_item_id = table.Column<long>(type: "bigint", nullable: true),
                    title = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    published_on = table.Column<DateOnly>(type: "date", nullable: true),
                    quote = table.Column<string>(type: "text", nullable: false),
                    verified = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_finding_citations", x => x.id);
                    table.ForeignKey(
                        name: "fk_finding_citations_report_findings_finding_id",
                        column: x => x.finding_id,
                        principalTable: "report_findings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_finding_citations_source_items_source_item_id",
                        column: x => x.source_item_id,
                        principalTable: "source_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_source_items_report_id",
                table: "source_items",
                column: "report_id");

            migrationBuilder.CreateIndex(
                name: "ix_source_items_reported_at",
                table: "source_items",
                column: "reported_at");

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_prefix",
                table: "api_keys",
                column: "prefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_finding_citations_finding_id",
                table: "finding_citations",
                column: "finding_id");

            migrationBuilder.CreateIndex(
                name: "ix_finding_citations_source_item_id",
                table: "finding_citations",
                column: "source_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_report_findings_report_id",
                table: "report_findings",
                column: "report_id");

            migrationBuilder.CreateIndex(
                name: "ix_reports_date",
                table: "reports",
                column: "date",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_source_items_reports_report_id",
                table: "source_items",
                column: "report_id",
                principalTable: "reports",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_source_items_reports_report_id",
                table: "source_items");

            migrationBuilder.DropTable(
                name: "api_keys");

            migrationBuilder.DropTable(
                name: "finding_citations");

            migrationBuilder.DropTable(
                name: "report_findings");

            migrationBuilder.DropTable(
                name: "reports");

            migrationBuilder.DropIndex(
                name: "ix_source_items_report_id",
                table: "source_items");

            migrationBuilder.DropIndex(
                name: "ix_source_items_reported_at",
                table: "source_items");

            migrationBuilder.DropColumn(
                name: "report_id",
                table: "source_items");

            migrationBuilder.DropColumn(
                name: "reported_at",
                table: "source_items");
        }
    }
}
