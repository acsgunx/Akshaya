using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Akshaya.Api.Infrastructure.Migrations;

/// <inheritdoc />
public partial class PersistBrokerLinks : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "broker_links",
            schema: "identity",
            columns: table => new
            {
                Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ConnectorId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Nickname = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                SessionKeyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                SessionWrappedDataKey = table.Column<byte[]>(type: "bytea", nullable: true),
                SessionPayload = table.Column<byte[]>(type: "bytea", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LastAuthenticatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                IsActive = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_broker_links", x => x.Id);
                table.ForeignKey(
                    name: "FK_broker_links_users_UserId",
                    column: x => x.UserId,
                    principalSchema: "identity",
                    principalTable: "users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_broker_links_IsActive",
            schema: "identity",
            table: "broker_links",
            column: "IsActive");

        migrationBuilder.CreateIndex(
            name: "IX_broker_links_TenantId_UserId",
            schema: "identity",
            table: "broker_links",
            columns: new[] { "TenantId", "UserId" });

        migrationBuilder.CreateIndex(
            name: "IX_broker_links_UserId",
            schema: "identity",
            table: "broker_links",
            column: "UserId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "broker_links",
            schema: "identity");
    }
}
