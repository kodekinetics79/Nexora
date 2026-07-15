using ERP_RFQ_Automation.DTOs;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MimeKit;
using System;
using System.IO;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class SmtpController : ControllerBase
    {
        private readonly ILogger<SmtpController> _logger;

        public SmtpController(ILogger<SmtpController> logger)
        {
            _logger = logger;
        }

        [HttpPost("send")]
        public async Task<IActionResult> SendEmail([FromForm] SendSupplierEmailRequestDTO request)
        {
            if (request == null || request.SmtpSettings == null)
            {
                return BadRequest("Invalid request data.");
            }

            try
            {
                var message = new MimeMessage();
                message.From.Add(new MailboxAddress(request.SmtpSettings.Username, request.SmtpSettings.Username));
                message.To.Add(new MailboxAddress(request.ToEmail, request.ToEmail));
                message.Subject = request.Subject;

                var builder = new BodyBuilder { HtmlBody = request.Body };

                if (request.Attachments != null)
                {
                    foreach (var file in request.Attachments)
                    {
                        if (file.Length > 0)
                        {
                            using var ms = new MemoryStream();
                            await file.CopyToAsync(ms);
                            builder.Attachments.Add(file.FileName, ms.ToArray(), ContentType.Parse(file.ContentType));
                        }
                    }
                }

                message.Body = builder.ToMessageBody();

                using var client = new SmtpClient();
                // Bypass certificate validation for maximum compatibility in dev/test environments
                client.ServerCertificateValidationCallback = (s, c, h, e) => true;
                client.Timeout = 10000; // 10 second timeout

                _logger.LogInformation("Connecting to SMTP {Host}:{Port} (UseSsl: {UseSsl})", 
                    request.SmtpSettings.Host, request.SmtpSettings.Port, request.SmtpSettings.UseSsl);

                // Use Auto to let MailKit decide, or be explicit based on common ports
                var options = request.SmtpSettings.UseSsl 
                    ? SecureSocketOptions.SslOnConnect 
                    : SecureSocketOptions.StartTls;

                await client.ConnectAsync(request.SmtpSettings.Host, request.SmtpSettings.Port, options);
                
                await client.AuthenticateAsync(request.SmtpSettings.Username, request.SmtpSettings.Password);
                await client.SendAsync(message);
                await client.DisconnectAsync(true);

                return Ok(new { message = "Email sent successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email via SMTP");
                return StatusCode(500, $"Failed to send email: {ex.Message}");
            }
        }
    }
}
