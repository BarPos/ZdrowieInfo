using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZdrowieInfo.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class SourcesDaily : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Domyślnie wszystkie źródła codziennie (częstotliwość nadal można zmienić w panelu dla pojedynczego źródła).
            migrationBuilder.Sql("UPDATE sources SET \"group\" = 'Daily' WHERE \"group\" <> 'Daily';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
