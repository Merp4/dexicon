using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dexicon.Core.Catalog.Migrations
{
    /// <summary>
    /// A column for what a file's row is settled for, so a pass can leave a file it has
    /// already finished with unopened. Null everywhere to begin with: the first pass after
    /// this reads every file as before, and records the key as it goes. Nothing re-chunks
    /// or re-embeds.
    /// </summary>
    public partial class FilesSettleBySizeAndModifiedTime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SettledFor",
                table: "file_chunk_states",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SettledFor",
                table: "file_chunk_states");
        }
    }
}
