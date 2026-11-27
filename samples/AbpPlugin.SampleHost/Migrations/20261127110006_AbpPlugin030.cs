using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AbpPlugin.SampleHost.Migrations
{
    /// <inheritdoc />
    public partial class AbpPlugin030 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CountersignerKeyId",
                schema: "plugins",
                table: "plugins",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CountersignerKeyId",
                schema: "plugins",
                table: "plugins");
        }
    }
}
