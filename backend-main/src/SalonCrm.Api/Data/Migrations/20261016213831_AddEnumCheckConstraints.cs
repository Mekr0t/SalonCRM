using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalonCrm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEnumCheckConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_users_role",
                table: "users",
                sql: "role IN ('Admin', 'User')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_services_category",
                table: "services",
                sql: "category IN ('Skin', 'Nails', 'BrowsLashes', 'Waxing', 'Massage', 'Makeup')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_retention_actions_channel",
                table: "retention_actions",
                sql: "channel IN ('Phone', 'Email', 'Sms', 'InPerson')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payments_method",
                table: "payments",
                sql: "method IN ('Cash', 'Card')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_appointments_source",
                table: "appointments",
                sql: "source IN ('Online', 'Staff')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_appointments_status",
                table: "appointments",
                sql: "status IN ('Scheduled', 'Completed', 'Cancelled', 'NoShow')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_users_role",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_services_category",
                table: "services");

            migrationBuilder.DropCheckConstraint(
                name: "ck_retention_actions_channel",
                table: "retention_actions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payments_method",
                table: "payments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_appointments_source",
                table: "appointments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_appointments_status",
                table: "appointments");
        }
    }
}
