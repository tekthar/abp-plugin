using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AbpPlugin.SampleHost.Migrations
{
    /// <inheritdoc />
    public partial class AbpPlugin010 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "plugins");

            migrationBuilder.CreateTable(
                name: "plugin_files",
                schema: "plugins",
                columns: table => new
                {
                    PluginKey = table.Column<string>(type: "character varying(129)", maxLength: 129, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SavedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugin_files", x => new { x.PluginKey, x.Name });
                });

            migrationBuilder.CreateTable(
                name: "plugin_manifests",
                schema: "plugins",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(129)", maxLength: 129, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Manifest = table.Column<string>(type: "jsonb", nullable: false),
                    Messages = table.Column<string>(type: "jsonb", nullable: false),
                    InstalledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RemovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugin_manifests", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "plugin_publishers",
                schema: "plugins",
                columns: table => new
                {
                    KeyId = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PublicKey = table.Column<byte[]>(type: "bytea", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AddedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugin_publishers", x => x.KeyId);
                });

            migrationBuilder.CreateTable(
                name: "plugin_settings",
                schema: "plugins",
                columns: table => new
                {
                    PluginKey = table.Column<string>(type: "character varying(129)", maxLength: 129, nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Value = table.Column<string>(type: "jsonb", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugin_settings", x => new { x.PluginKey, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "plugin_trust",
                schema: "plugins",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    AllowUnsigned = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugin_trust", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "plugins",
                schema: "plugins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(129)", maxLength: 129, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    InstallPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    PackageHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PackageBlob = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    InstalledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    SignatureKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Signer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SignerKeyId = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugins", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_plugins_Key",
                schema: "plugins",
                table: "plugins",
                column: "Key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plugin_files",
                schema: "plugins");

            migrationBuilder.DropTable(
                name: "plugin_manifests",
                schema: "plugins");

            migrationBuilder.DropTable(
                name: "plugin_publishers",
                schema: "plugins");

            migrationBuilder.DropTable(
                name: "plugin_settings",
                schema: "plugins");

            migrationBuilder.DropTable(
                name: "plugin_trust",
                schema: "plugins");

            migrationBuilder.DropTable(
                name: "plugins",
                schema: "plugins");
        }
    }
}
