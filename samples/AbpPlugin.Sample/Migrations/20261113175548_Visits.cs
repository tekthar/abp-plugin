using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AbpPlugin.Sample.Migrations
{
    /// <inheritdoc />
    public partial class Visits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "plg_acme__abp_sample");

            migrationBuilder.CreateTable(
                name: "visits",
                schema: "plg_acme__abp_sample",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_visits", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "visits",
                schema: "plg_acme__abp_sample");
        }
    }
}
