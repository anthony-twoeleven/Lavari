using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lavari.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierExtractionQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProviderConsentGrantedAtUtc",
                table: "LlmExtractionDrafts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SupplierExtractionWork",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DraftId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InitiatingUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: true),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReservationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", nullable: false),
                    SourceUrl = table.Column<string>(type: "TEXT", nullable: false),
                    SelectedPageText = table.Column<string>(type: "TEXT", nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    DeletedAtUtc = table.Column<string>(type: "TEXT", nullable: true),
                    DeletedBy = table.Column<string>(type: "TEXT", nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierExtractionWork", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierExtractionWork_DraftId",
                table: "SupplierExtractionWork",
                column: "DraftId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierExtractionWork_State_CreatedAtUtc",
                table: "SupplierExtractionWork",
                columns: new[] { "State", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SupplierExtractionWork");

            migrationBuilder.DropColumn(
                name: "ProviderConsentGrantedAtUtc",
                table: "LlmExtractionDrafts");
        }
    }
}
