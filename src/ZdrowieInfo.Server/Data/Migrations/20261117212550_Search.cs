using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace ZdrowieInfo.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Search : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Wyszukiwanie: unaccent (rozszerzenie „trusted” – wystarczy właściciel bazy) i konfiguracja simple + unaccent.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS unaccent;");
            migrationBuilder.Sql("""
                DO $$ BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_ts_config WHERE cfgname = 'pl_unaccent') THEN
                    CREATE TEXT SEARCH CONFIGURATION pl_unaccent (COPY = simple);
                    ALTER TEXT SEARCH CONFIGURATION pl_unaccent ALTER MAPPING FOR hword, hword_part, word WITH unaccent, simple;
                  END IF;
                END $$;
                """);

            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "search_vector",
                table: "reports",
                type: "tsvector",
                nullable: true,
                computedColumnSql: "setweight(to_tsvector('pl_unaccent'::regconfig, coalesce(title, '')), 'A') || setweight(to_tsvector('pl_unaccent'::regconfig, coalesce(summary, '')), 'B')",
                stored: true);

            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "search_vector",
                table: "report_findings",
                type: "tsvector",
                nullable: true,
                computedColumnSql: "setweight(to_tsvector('pl_unaccent'::regconfig, coalesce(headline, '')), 'A') || setweight(to_tsvector('pl_unaccent'::regconfig, coalesce(body, '') || ' ' || coalesce(action, '')), 'B')",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "ix_reports_search_vector",
                table: "reports",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "ix_report_findings_search_vector",
                table: "report_findings",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "GIN");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_reports_search_vector",
                table: "reports");

            migrationBuilder.DropIndex(
                name: "ix_report_findings_search_vector",
                table: "report_findings");

            migrationBuilder.DropColumn(
                name: "search_vector",
                table: "reports");

            migrationBuilder.DropColumn(
                name: "search_vector",
                table: "report_findings");
            migrationBuilder.Sql("DROP TEXT SEARCH CONFIGURATION IF EXISTS pl_unaccent;");
        }
    }
}
