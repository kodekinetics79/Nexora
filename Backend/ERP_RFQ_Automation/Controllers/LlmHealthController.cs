using ERP_RFQ_Automation.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;

namespace ERP_RFQ_Automation.Controllers
{
    /// <summary>
    /// Manager-gated diagnostic for the configured LLM provider. Answers, from
    /// inside the deployment's own network/config: (1) can we authenticate,
    /// (2) does the configured model accept a completion. Never echoes the key.
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
            var model = _config["Ollama:Model"] ?? "(unset)";
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

            // 2. Model completion check (tiny prompt)
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    model,
                    messages = new[] { new { role = "user", content = "Reply with the single word: ok" } },
                    stream = false,
                });
                var resp = await client.PostAsync("api/chat",
                    new StringContent(payload, Encoding.UTF8, "application/json"), ct);
                result["modelCheck"] = $"{(int)resp.StatusCode} {resp.StatusCode}";
            }
            catch (Exception)
            {
                result["modelCheck"] = "EXCEPTION";
            }

            return Ok(result);
        }
    }
}
