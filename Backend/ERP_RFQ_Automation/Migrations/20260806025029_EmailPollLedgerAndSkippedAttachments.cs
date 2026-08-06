using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// ING-08 / ING-06 — the inbound mail door stops lying.
    ///
    /// <para><b>Email_Configurations poll ledger.</b> The poller beat its heartbeat and logged
    /// "Email fetch completed successfully." 1.5 ms after
    /// <c>MailKit.Security.AuthenticationException</c>, every cycle, from 2026-07-30 onward.
    /// Nothing durable recorded that the mailbox had stopped answering, so no health surface
    /// could go red and no operator could learn. <c>LastSuccessfulPollOn</c> also replaces the
    /// fixed 7-day <c>SentSince</c> window: an outage longer than a week used to make every
    /// older message permanently invisible, and now defines the window that recovers it.</para>
    ///
    /// <para><b>EmailIngests.SkippedAttachmentsJson.</b> The list of attachments the intake door
    /// could not process was persisted only when the body ALSO produced a job. A quoted-only
    /// reply carrying one supported and one unsupported attachment recorded the dropped file
    /// nowhere at all.</para>
    ///
    /// Additive and nullable/defaulted throughout: existing rows read as "never polled" and
    /// "nothing skipped", and the Down migration is a clean drop.
    /// </summary>
    public partial class EmailPollLedgerAndSkippedAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SkippedAttachmentsJson",
                table: "EmailIngests",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConsecutivePollFailures",
                table: "Email_Configurations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastPollAttemptOn",
                table: "Email_Configurations",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastPollError",
                table: "Email_Configurations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSuccessfulPollOn",
                table: "Email_Configurations",
                type: "timestamp without time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SkippedAttachmentsJson",
                table: "EmailIngests");

            migrationBuilder.DropColumn(
                name: "ConsecutivePollFailures",
                table: "Email_Configurations");

            migrationBuilder.DropColumn(
                name: "LastPollAttemptOn",
                table: "Email_Configurations");

            migrationBuilder.DropColumn(
                name: "LastPollError",
                table: "Email_Configurations");

            migrationBuilder.DropColumn(
                name: "LastSuccessfulPollOn",
                table: "Email_Configurations");
        }
    }
}
