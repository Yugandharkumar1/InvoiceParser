using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceParser.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InvoiceFeedback",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CarrierId = table.Column<int>(type: "int", nullable: false),
                    FeedbackText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PdfText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OriginalFieldsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConfirmedFieldsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OriginalChargesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsProcessed = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceFeedback", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LineFeedback",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RawText = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    NormalizedText = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    PredictedLabel = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CorrectedLabel = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LineFeedback", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_invoice",
                columns: table => new
                {
                    t_invoice_id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    customer_id = table.Column<int>(type: "int", nullable: true),
                    carrier_id = table.Column<int>(type: "int", nullable: true),
                    carrier_code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    carrier_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    carrier_account = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    invoice_number = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    invoice_date = table.Column<DateTime>(type: "datetime2", nullable: true),
                    invoice_st_dtm = table.Column<DateTime>(type: "datetime2", nullable: true),
                    invoice_end_dtm = table.Column<DateTime>(type: "datetime2", nullable: true),
                    invoice_due_dtm = table.Column<DateTime>(type: "datetime2", nullable: true),
                    beg_bal = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    payment = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    prev_adj = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    curr_adj = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    curr_chg = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    curr_tax = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    end_bal = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    is_summary = table.Column<bool>(type: "bit", nullable: false),
                    email_sent = table.Column<bool>(type: "bit", nullable: false),
                    is_active = table.Column<bool>(type: "bit", nullable: false),
                    add_usr = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    add_dtm = table.Column<DateTime>(type: "datetime2", nullable: false),
                    pdf_text = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_invoice", x => x.t_invoice_id);
                });

            migrationBuilder.CreateTable(
                name: "VendorParsingRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CarrierId = table.Column<int>(type: "int", nullable: false),
                    FieldName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RegexPattern = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    FieldType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TargetTable = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Section = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SuccessCount = table.Column<int>(type: "int", nullable: false),
                    FailCount = table.Column<int>(type: "int", nullable: false),
                    ConditionType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ConditionSource = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    TransformationsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DescriptionLineOffset = table.Column<int>(type: "int", nullable: false),
                    AmountPattern = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorParsingRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_charge",
                columns: table => new
                {
                    t_charge_id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    t_invoice_id = table.Column<int>(type: "int", nullable: false),
                    t_usoc_map_id = table.Column<int>(type: "int", nullable: false),
                    charge_desc = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    amount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    line = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    location = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    account_number = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    carrier_id = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_charge", x => x.t_charge_id);
                    table.ForeignKey(
                        name: "FK_t_charge_t_invoice_t_invoice_id",
                        column: x => x.t_invoice_id,
                        principalTable: "t_invoice",
                        principalColumn: "t_invoice_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "t_inventory",
                columns: table => new
                {
                    t_inventory_id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    t_invoice_id = table.Column<int>(type: "int", nullable: false),
                    customer_id = table.Column<int>(type: "int", nullable: false),
                    reference_number = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    service_type = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    inventory_name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    employee_name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    location_name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_inventory", x => x.t_inventory_id);
                    table.ForeignKey(
                        name: "FK_t_inventory_t_invoice_t_invoice_id",
                        column: x => x.t_invoice_id,
                        principalTable: "t_invoice",
                        principalColumn: "t_invoice_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "t_usage",
                columns: table => new
                {
                    t_usage_id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    t_invoice_id = table.Column<int>(type: "int", nullable: false),
                    line_number = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    t_usoc_map_id = table.Column<int>(type: "int", nullable: true),
                    usoc_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    usage_limit = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    usage = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    charge = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    usagetype = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    add_dtm = table.Column<DateTime>(type: "datetime2", nullable: false),
                    account_id = table.Column<int>(type: "int", nullable: true),
                    carrier_id = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_usage", x => x.t_usage_id);
                    table.ForeignKey(
                        name: "FK_t_usage_t_invoice_t_invoice_id",
                        column: x => x.t_invoice_id,
                        principalTable: "t_invoice",
                        principalColumn: "t_invoice_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_t_charge_t_invoice_id",
                table: "t_charge",
                column: "t_invoice_id");

            migrationBuilder.CreateIndex(
                name: "IX_t_inventory_t_invoice_id",
                table: "t_inventory",
                column: "t_invoice_id");

            migrationBuilder.CreateIndex(
                name: "IX_t_usage_t_invoice_id",
                table: "t_usage",
                column: "t_invoice_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InvoiceFeedback");

            migrationBuilder.DropTable(
                name: "LineFeedback");

            migrationBuilder.DropTable(
                name: "t_charge");

            migrationBuilder.DropTable(
                name: "t_inventory");

            migrationBuilder.DropTable(
                name: "t_usage");

            migrationBuilder.DropTable(
                name: "VendorParsingRules");

            migrationBuilder.DropTable(
                name: "t_invoice");
        }
    }
}
