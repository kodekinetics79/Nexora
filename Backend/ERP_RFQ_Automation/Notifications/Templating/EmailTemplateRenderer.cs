using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Notifications.Templating
{
    /// <summary>
    /// Default <see cref="IEmailTemplateRenderer"/>. Resolves an embedded template
    /// by name and performs simple, safe <c>{{token}}</c> replacement against the
    /// model. Unknown tokens left in the template are stripped so raw
    /// <c>{{placeholder}}</c> text never reaches a recipient.
    /// </summary>
    public sealed class EmailTemplateRenderer : IEmailTemplateRenderer
    {
        // Matches {{ token }} allowing surrounding whitespace inside the braces.
        private static readonly Regex TokenRegex = new(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}", RegexOptions.Compiled);

        private readonly ILogger<EmailTemplateRenderer> _logger;

        public EmailTemplateRenderer(ILogger<EmailTemplateRenderer> logger)
        {
            _logger = logger;
        }

        public RenderedEmail Render(string templateName, IDictionary<string, string?> model)
        {
            var definition = EmailTemplates.Get(templateName);
            if (definition is null)
            {
                _logger.LogError("[EmailTemplateRenderer] Unknown template '{TemplateName}'.", templateName);
                throw new KeyNotFoundException($"Email template '{templateName}' is not registered.");
            }

            return new RenderedEmail
            {
                Subject = Substitute(definition.Subject, model),
                HtmlBody = Substitute(definition.Html, model),
                TextBody = Substitute(definition.Text, model)
            };
        }

        private static string Substitute(string template, IDictionary<string, string?> model)
        {
            if (string.IsNullOrEmpty(template))
                return string.Empty;

            return TokenRegex.Replace(template, match =>
            {
                var key = match.Groups[1].Value;
                if (model.TryGetValue(key, out var value))
                    return value ?? string.Empty;

                // Unknown token: strip it rather than leak "{{...}}" to the recipient.
                return string.Empty;
            });
        }
    }
}
