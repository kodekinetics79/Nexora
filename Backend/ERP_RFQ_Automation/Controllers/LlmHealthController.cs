using ERP_RFQ_Automation.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers
{
    /// <summary>
    /// Manager-gated diagnostic for the configured LLM provider. Answers, from
    /// inside the deployment's own network/config whether provider authentication
    /// succeeds. It deliberately performs no billable completion and never echoes the key.
    /// </summary>
    [ApiController]
    [Route("api/llm-health")]
    [Authorize]
    [RequireManagerRole]
    public class LlmHealthController : ControllerBase
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly IConfiguration _config;

        public LlmHealthController(IHttpClientFactory httpFactory, IConfiguration config)
        {
            _httpFactory = httpFactory;
            _config = config;
        }

        [HttpGet]
        public async Task<IActionResult> Check(CancellationToken ct)
        {
            var baseUrl = _config["Ollama:BaseUrl"] ?? "(unset)";
            var key = _config["Ollama:ApiKey"] ?? "";
            var result = new Dictionary<string, object?>
            {
                ["provider"] = "Ollama-compatible",
            };

            var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(60);
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var root))
                client.BaseAddress = root;
            else
            {
                result["error"] = "Ollama:BaseUrl is not a valid absolute URL.";
                return Ok(result);
            }
            if (!string.IsNullOrWhiteSpace(key))
                client.DefaultRequestHeaders.Authorization = new("Bearer", key);

            // 1. Auth check
            try
            {
                var tags = await client.GetAsync("api/tags", ct);
                result["authCheck"] = $"{(int)tags.StatusCode} {tags.StatusCode}";
            }
            catch (Exception)
            {
                result["authCheck"] = "EXCEPTION";
                return Ok(result);
            }

            return Ok(result);
        }
    }
}
