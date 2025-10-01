
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Net.Http.Headers;
using Newtonsoft.Json.Linq;

namespace xbytechat.api.Features.WhatsAppIntegration.Controllers
{
    [ApiController]
    [Route("api/whatsapp")]
    public class WhatsAppTemplateController : ControllerBase
    {
        private readonly IConfiguration _config;

        public WhatsAppTemplateController(IConfiguration config)
        {
            _config = config;
        }

        [HttpGet]
        public async Task<IActionResult> GetTemplates()
        {
            var wabaId = _config["WhatsApp:WABA_ID"];
            var apiToken = _config["WhatsApp:apiToken"];
            var url = $"https://graph.facebook.com/v22.0/{wabaId}/message_templates";

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);

            try
            {
                var response = await client.GetAsync(url);
                var json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    return StatusCode((int)response.StatusCode, new { success = false, error = json });

                var root = JsonConvert.DeserializeObject<JObject>(json);
                var templatesRaw = root["data"] as JArray;

                if (templatesRaw == null)
                    return BadRequest(new { success = false, error = "Meta API did not return data array." });

                var templates = new List<object>();

                foreach (var tpl in templatesRaw)
                {
                    string name = tpl["name"]?.ToString() ?? "";
                    string language = tpl["language"]?.ToString() ?? "en_US";

                    string bodyText = "";
                    var components = tpl["components"] as JArray;

                    if (components != null)
                    {
                        foreach (var comp in components)
                        {
                            if (comp["type"]?.ToString() == "BODY")
                            {
                                bodyText = comp["text"]?.ToString() ?? "";
                                break;
                            }
                        }
                    }

                    templates.Add(new
                    {
                        name,
                        language,
                        body = bodyText
                    });
                }

                return Ok(new { success = true, templates });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

    }
}


