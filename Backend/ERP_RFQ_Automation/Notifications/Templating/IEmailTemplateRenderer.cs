using System.Collections.Generic;

namespace ERP_RFQ_Automation.Notifications.Templating
{
    /// <summary>
    /// Result of rendering a named template: an email subject plus HTML and
    /// plain-text bodies.
    /// </summary>
    public sealed class RenderedEmail
    {
        public string Subject { get; set; } = string.Empty;
        public string HtmlBody { get; set; } = string.Empty;
        public string TextBody { get; set; } = string.Empty;
    }

    /// <summary>
    /// Lightweight template renderer. Renders a named template by replacing
    /// <c>{{placeholder}}</c> tokens in embedded HTML/text with values from the
    /// supplied model. No external templating dependency.
    /// </summary>
    public interface IEmailTemplateRenderer
    {
        /// <summary>
        /// Renders the template identified by <paramref name="templateName"/>
        /// (e.g. "rfq-to-supplier") against <paramref name="model"/>.
        /// </summary>
        RenderedEmail Render(string templateName, IDictionary<string, string?> model);
    }
}
