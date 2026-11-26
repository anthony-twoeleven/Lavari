using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lavari.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiSpendReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiSpendReservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InitiatingUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ServicePrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientRequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: true),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", nullable: false),
                    BudgetDateUtc = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", nullable: false),
                    ReservedCents = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    InputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    OutputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    ReasoningTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    TotalTokens = table.Column<int>(type: "INTEGER", nullable: true),
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
                    table.PrimaryKey("PK_AiSpendReservations", x => x.Id);
                    table.CheckConstraint("CK_AiSpendReservation_Amount", "ReservedCents > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiSpendReservations_BudgetDateUtc_InitiatingUserId",
                table: "AiSpendReservations",
                columns: new[] { "BudgetDateUtc", "InitiatingUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_AiSpendReservations_InitiatingUserId_ClientRequestId",
                table: "AiSpendReservations",
                columns: new[] { "InitiatingUserId", "ClientRequestId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Rolling back this migration would delete AI reservation history.");
        }
    }
}
