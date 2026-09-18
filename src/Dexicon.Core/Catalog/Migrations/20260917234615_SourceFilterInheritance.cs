using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dexicon.Core.Catalog.Migrations
{
    /// <summary>
    /// A source's filters become nullable so "unset" is expressible, and a corpus gains
    /// the defaults an unset field inherits.
    ///
    /// Widening only. Every existing source row keeps the value it had, so it stays an
    /// explicit override and indexes exactly as before; the new corpus columns start null,
    /// which resolves to the configured fallback. Nothing re-indexes on upgrade.
    /// </summary>
    public partial class SourceFilterInheritance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "UseGitignore",
                table: "sources",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(bool),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<int>(
                name: "MaxFileBytes",
                table: "sources",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<string>(
                name: "DefaultExcludeGlobs",
                table: "corpora",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultIncludeGlobs",
                table: "corpora",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DefaultMaxFileBytes",
                table: "corpora",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DefaultUseGitignore",
                table: "corpora",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultExcludeGlobs",
                table: "corpora");

            migrationBuilder.DropColumn(
                name: "DefaultIncludeGlobs",
                table: "corpora");

            migrationBuilder.DropColumn(
                name: "DefaultMaxFileBytes",
                table: "corpora");

            migrationBuilder.DropColumn(
                name: "DefaultUseGitignore",
                table: "corpora");

            migrationBuilder.AlterColumn<bool>(
                name: "UseGitignore",
                table: "sources",
                type: "INTEGER",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "MaxFileBytes",
                table: "sources",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);
        }
    }
}
