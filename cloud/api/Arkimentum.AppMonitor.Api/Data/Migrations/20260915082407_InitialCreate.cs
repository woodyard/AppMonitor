using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arkimentum.AppMonitor.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Actor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TargetType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TargetId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Details = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrganizationConfigHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConfigVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SettingsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Comment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationConfigHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Organizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EntraTenantId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    EnrollmentKeyHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    EnrollmentKeyRotatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Organizations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReleaseManifests",
                columns: table => new
                {
                    Channel = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReleaseManifests", x => x.Channel);
                });

            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MachineGuid = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DeviceName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    EntraTenantId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    EntraDeviceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    OsVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    AgentVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    DeviceKeyHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    EnrolledUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastScanUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ConfigVersionApplied = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LastLogonUser = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    PrerequisitesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PendingUpdateCount = table.Column<int>(type: "int", nullable: false),
                    FailedUpdateCount = table.Column<int>(type: "int", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Devices_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OrganizationConfigs",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConfigVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SettingsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationConfigs", x => x.OrganizationId);
                    table.ForeignKey(
                        name: "FK_OrganizationConfigs_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceApps",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Version = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Publisher = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    WingetId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    AvailableVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Context = table.Column<int>(type: "int", nullable: false),
                    CatalogAppId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    MonitoredAppId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceApps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceApps_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceCommands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Argument = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IssuedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IssuedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    DeliveredUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AcknowledgedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceCommands", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceCommands_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    AppId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    FromVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ToVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceEvents_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceUpdates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    InstalledVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    AvailableVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    Context = table.Column<int>(type: "int", nullable: false),
                    Mandatory = table.Column<bool>(type: "bit", nullable: false),
                    DeadlineUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeferredUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeferralCount = table.Column<int>(type: "int", nullable: false),
                    FirstDetectedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    InstalledAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceUpdates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceUpdates_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_OrganizationId_Utc",
                table: "AuditLog",
                columns: new[] { "OrganizationId", "Utc" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceApps_DeviceId",
                table: "DeviceApps",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceApps_OrganizationId_DisplayName",
                table: "DeviceApps",
                columns: new[] { "OrganizationId", "DisplayName" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceApps_OrganizationId_WingetId",
                table: "DeviceApps",
                columns: new[] { "OrganizationId", "WingetId" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceCommands_DeviceId_AcknowledgedUtc",
                table: "DeviceCommands",
                columns: new[] { "DeviceId", "AcknowledgedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceEvents_DeviceId_OccurredUtc",
                table: "DeviceEvents",
                columns: new[] { "DeviceId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceEvents_OrganizationId_OccurredUtc",
                table: "DeviceEvents",
                columns: new[] { "OrganizationId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_OrganizationId_IsDeleted_DeviceName",
                table: "Devices",
                columns: new[] { "OrganizationId", "IsDeleted", "DeviceName" });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_OrganizationId_LastSeenUtc",
                table: "Devices",
                columns: new[] { "OrganizationId", "LastSeenUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_OrganizationId_MachineGuid",
                table: "Devices",
                columns: new[] { "OrganizationId", "MachineGuid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceUpdates_DeviceId",
                table: "DeviceUpdates",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceUpdates_OrganizationId_State",
                table: "DeviceUpdates",
                columns: new[] { "OrganizationId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationConfigHistory_OrganizationId_UpdatedUtc",
                table: "OrganizationConfigHistory",
                columns: new[] { "OrganizationId", "UpdatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Organizations_EntraTenantId",
                table: "Organizations",
                column: "EntraTenantId",
                unique: true,
                filter: "[EntraTenantId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Organizations_Name",
                table: "Organizations",
                column: "Name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLog");

            migrationBuilder.DropTable(
                name: "DeviceApps");

            migrationBuilder.DropTable(
                name: "DeviceCommands");

            migrationBuilder.DropTable(
                name: "DeviceEvents");

            migrationBuilder.DropTable(
                name: "DeviceUpdates");

            migrationBuilder.DropTable(
                name: "OrganizationConfigHistory");

            migrationBuilder.DropTable(
                name: "OrganizationConfigs");

            migrationBuilder.DropTable(
                name: "ReleaseManifests");

            migrationBuilder.DropTable(
                name: "Devices");

            migrationBuilder.DropTable(
                name: "Organizations");
        }
    }
}
