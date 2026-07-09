using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeontecSyncLogSystem.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialMySql : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    address = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    worker_id = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    first_seen_utc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    last_seen_utc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    last_frame_utc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    last_heartbeat_utc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    frames_received = table.Column<long>(type: "bigint", nullable: false),
                    records_ingested = table.Column<long>(type: "bigint", nullable: false),
                    sessions = table.Column<long>(type: "bigint", nullable: false),
                    heartbeats = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_devices", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "csv_uploads",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    device_id = table.Column<long>(type: "bigint", nullable: false),
                    received_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    source = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    type = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    log_date = table.Column<DateTime>(type: "date", nullable: true),
                    term_id = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    upload_index = table.Column<int>(type: "int", nullable: false),
                    superseded = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    row_count = table.Column<int>(type: "int", nullable: false),
                    raw_csv = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_csv_uploads", x => x.id);
                    table.ForeignKey(
                        name: "fk_csv_uploads_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "direct_entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    upload_id = table.Column<long>(type: "bigint", nullable: false),
                    start_time = table.Column<TimeOnly>(type: "time(6)", nullable: true),
                    end_time = table.Column<TimeOnly>(type: "time(6)", nullable: true),
                    customer = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    delivery_to = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ship_date = table.Column<DateOnly>(type: "date", nullable: true),
                    part_no = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    capacity = table.Column<int>(type: "int", nullable: false),
                    boxes = table.Column<int>(type: "int", nullable: false),
                    delivery_qty = table.Column<int>(type: "int", nullable: false),
                    factory_code = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    yokoo_part_no = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_direct_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_direct_entries_csv_uploads_upload_id",
                        column: x => x.upload_id,
                        principalTable: "csv_uploads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "monitor_entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    upload_id = table.Column<long>(type: "bigint", nullable: false),
                    start_time = table.Column<TimeOnly>(type: "time(6)", nullable: true),
                    end_time = table.Column<TimeOnly>(type: "time(6)", nullable: true),
                    slip_no = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    customer_code = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    item_code = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    boxes = table.Column<int>(type: "int", nullable: false),
                    quantity = table.Column<int>(type: "int", nullable: false),
                    loaded_boxes = table.Column<int>(type: "int", nullable: false),
                    status = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    status_code = table.Column<string>(type: "varchar(8)", maxLength: 8, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_monitor_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_monitor_entries_csv_uploads_upload_id",
                        column: x => x.upload_id,
                        principalTable: "csv_uploads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "pallet_ops",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    upload_id = table.Column<long>(type: "bigint", nullable: false),
                    op_type = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    start_time = table.Column<TimeOnly>(type: "time(6)", nullable: true),
                    end_time = table.Column<TimeOnly>(type: "time(6)", nullable: true),
                    pl_no = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    customer = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    delivery_run = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    item_detail_raw = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    status_code = table.Column<string>(type: "varchar(8)", maxLength: 8, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pallet_ops", x => x.id);
                    table.ForeignKey(
                        name: "fk_pallet_ops_csv_uploads_upload_id",
                        column: x => x.upload_id,
                        principalTable: "csv_uploads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "pallet_op_items",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    pallet_op_id = table.Column<long>(type: "bigint", nullable: false),
                    item_code = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    boxes = table.Column<int>(type: "int", nullable: false),
                    quantity = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pallet_op_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_pallet_op_items_pallet_ops_pallet_op_id",
                        column: x => x.pallet_op_id,
                        principalTable: "pallet_ops",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "ix_csv_uploads_device_id",
                table: "csv_uploads",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_csv_uploads_term_id_type",
                table: "csv_uploads",
                columns: new[] { "term_id", "type" });

            migrationBuilder.CreateIndex(
                name: "ix_csv_uploads_type_log_date",
                table: "csv_uploads",
                columns: new[] { "type", "log_date" });

            migrationBuilder.CreateIndex(
                name: "ix_devices_address",
                table: "devices",
                column: "address",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_direct_entries_upload_id",
                table: "direct_entries",
                column: "upload_id");

            migrationBuilder.CreateIndex(
                name: "ix_monitor_entries_upload_id",
                table: "monitor_entries",
                column: "upload_id");

            migrationBuilder.CreateIndex(
                name: "ix_pallet_op_items_pallet_op_id",
                table: "pallet_op_items",
                column: "pallet_op_id");

            migrationBuilder.CreateIndex(
                name: "ix_pallet_ops_upload_id",
                table: "pallet_ops",
                column: "upload_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "direct_entries");

            migrationBuilder.DropTable(
                name: "monitor_entries");

            migrationBuilder.DropTable(
                name: "pallet_op_items");

            migrationBuilder.DropTable(
                name: "pallet_ops");

            migrationBuilder.DropTable(
                name: "csv_uploads");

            migrationBuilder.DropTable(
                name: "devices");
        }
    }
}
