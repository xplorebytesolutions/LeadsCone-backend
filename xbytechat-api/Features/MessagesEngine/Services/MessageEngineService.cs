// 📄 File: Features/MessagesEngine/Services/MessageEngineService.cs
using Newtonsoft.Json;
using Microsoft.EntityFrameworkCore;
using xbytechat.api.Features.MessagesEngine.DTOs;
using xbytechat.api.Features.MessagesEngine.PayloadBuilders;
using xbytechat.api.Features.PlanManagement.Services;
using xbytechat.api.Helpers;
using xbytechat.api.Shared;
using xbytechat.api;
using xbytechat_api.WhatsAppSettings.Models;
using xbytechat.api.Features.CampaignModule.DTOs;
using xbytechat.api.Features.CampaignTracking.Models;
using System.Net.Http;
using xbytechat.api.Shared.utility;
using Microsoft.AspNetCore.SignalR;
using xbytechat.api.Features.Inbox.Hubs;
using System.Text.Json;
using xbytechat.api.Features.Webhooks.Services.Resolvers;
using xbytechat.api.CRM.Interfaces;
using xbytechat.api.Features.MessageManagement.DTOs;
using xbytechat.api.Features.ReportingModule.DTOs;

// ✅ provider abstraction + factory
using xbytechat.api.Features.MessagesEngine.Abstractions;
using xbytechat.api.Features.MessagesEngine.Factory;
using System.Net.Http.Headers;
using System.Text;
using xbytechat.api.CRM.Models;
using System.Collections.Concurrent;
using xbytechat.api.WhatsAppSettings.DTOs;
using xbytechat.api.Features.CampaignModule.Models;
using xbytechat.api.Features.CTAFlowBuilder.Services;

namespace xbytechat.api.Features.MessagesEngine.Services
{
    public class MessageEngineService : IMessageEngineService
    {
        private readonly AppDbContext _db;
        private readonly HttpClient _http; // kept for other internal calls if any
        private readonly TextMessagePayloadBuilder _textBuilder;
        private readonly ImageMessagePayloadBuilder _imageBuilder;
        private readonly TemplateMessagePayloadBuilder _templateBuilder;
        private readonly CtaMessagePayloadBuilder _ctaBuilder;
        private readonly IPlanManager _planManager;
        private readonly IHubContext<InboxHub> _hubContext;
        private readonly IMessageIdResolver _messageIdResolver;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IContactService _contactService;
        private readonly ConcurrentDictionary<Guid, (IReadOnlyList<WhatsAppSettingEntity> setting, DateTime expiresAt)>
 _settingsCache = new();
        // 🔄 Basic cache for WhatsApp settings to reduce DB load (kept for other paths)
        //private static readonly Dictionary<Guid, (WhatsAppSettingEntity setting, DateTime expiresAt)> _settingsCache = new();

        private readonly IWhatsAppProviderFactory _providerFactory;
        private readonly ILogger<MessageEngineService> _logger;
        public MessageEngineService(
            AppDbContext db,
            HttpClient http,
            TextMessagePayloadBuilder textBuilder,
            ImageMessagePayloadBuilder imageBuilder,
            TemplateMessagePayloadBuilder templateBuilder,
            CtaMessagePayloadBuilder ctaBuilder,
            IPlanManager planManager,
            IHubContext<InboxHub> hubContext,
            IMessageIdResolver messageIdResolver,
            IHttpContextAccessor httpContextAccessor,
            IContactService contactService,
            IWhatsAppProviderFactory providerFactory,
            ILogger<MessageEngineService> logger
        )
        {
            _db = db;
            _http = http;
            _textBuilder = textBuilder;
            _imageBuilder = imageBuilder;
            _templateBuilder = templateBuilder;
            _ctaBuilder = ctaBuilder;
            _planManager = planManager;
            _hubContext = hubContext;
            _messageIdResolver = messageIdResolver;
            _httpContextAccessor = httpContextAccessor;
            _contactService = contactService;
            _providerFactory = providerFactory;
            _logger = logger;
        }

        // INSERT: near other helpers / utilities
        private static string ResolveGreeting(string? profileName, string? contactName)
        {
            var s = (profileName ?? contactName)?.Trim();
            return string.IsNullOrEmpty(s) ? "there" : s;
        }

        private static void EnsureArgsLength(List<string> args, int slot1Based)
        {
            while (args.Count < slot1Based) args.Add(string.Empty);
        }

        // ✅ Public helper so both Flow + Campaign send paths can use it
        public async Task<List<string>> ApplyProfileNameAsync(
            Guid businessId,
            Guid contactId,
            bool useProfileName,
            int? profileNameSlot,
            List<string> args,
            CancellationToken ct = default)
        {
            if (!useProfileName || !(profileNameSlot is int slot) || slot < 1)
                return args;

            // pull once from DB (cheap; uses your existing index on BusinessId/Id)
            var contact = await _db.Contacts
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.BusinessId == businessId && c.Id == contactId, ct);

            var greet = ResolveGreeting(contact?.ProfileName, contact?.Name);

            EnsureArgsLength(args, slot);
            args[slot - 1] = greet;

            return args;
        }


        public async Task<ResponseResult> SendPayloadAsync(Guid businessId, string provider, object payload,         // "PINNACLE" or "META_CLOUD"object payload,
        string? phoneNumberId = null)
        {
            // Validate provider (no server-side normalization)
            if (string.IsNullOrWhiteSpace(provider) ||
                (provider != "PINNACLE" && provider != "META_CLOUD"))
            {
                return ResponseResult.ErrorInfo("❌ Invalid provider.",
                    "Provider must be exactly 'PINNACLE' or 'META_CLOUD'.");
            }

            // Route to the correct provider + optional sender override
            return await SendViaProviderAsync(
                businessId,
                provider,
                p => p.SendInteractiveAsync(payload),
                phoneNumberId
            );
        }





        // MessageEngineService.cs
       
        private static string NormalizeProviderOrThrow(string? p)
        {
            if (string.IsNullOrWhiteSpace(p))
                throw new ArgumentException("Provider is required.");

            var u = p.Trim().ToUpperInvariant();
            // Map common aliases -> canonical constants
            return u switch
            {
                "META" => "META_CLOUD",
                "META_CLOUD" => "META_CLOUD",
                "PINNACLE" => "PINNACLE",
                _ => throw new ArgumentException($"Invalid provider: {p}")
            };
        }

        private async Task<ResponseResult> SendViaProviderAsync(
        Guid businessId,
        string provider,                                // explicit
        Func<IWhatsAppProvider, Task<WaSendResult>> action,
        string? phoneNumberId = null)
        {
            try
            {
                // normalize + validate provider once
                var normalizedProvider = NormalizeProviderOrThrow(provider);   // 👈

                // For both META_CLOUD and PINNACLE we require a sender id
                if (string.IsNullOrWhiteSpace(phoneNumberId))
                    return ResponseResult.ErrorInfo(
                        "❌ Campaign has no sender number.",
                        "Missing PhoneNumberId");

                // Build the right provider instance bound to this business + number
                var wa = await _providerFactory.CreateAsync(
                    businessId,
                    normalizedProvider,
                    phoneNumberId);

                var res = await action(wa);

                if (!res.Success)
                    return ResponseResult.ErrorInfo("❌ WhatsApp API returned an error.", res.Error, res.RawResponse);

                var rr = ResponseResult.SuccessInfo("✅ Message sent successfully", data: null, raw: res.RawResponse);
                rr.MessageId = string.IsNullOrWhiteSpace(res.ProviderMessageId)
                    ? TryExtractMetaWamid(res.RawResponse)
                    : res.ProviderMessageId;
                return rr;
            }
            catch (ArgumentException ex) // from NormalizeProviderOrThrow
            {
                return ResponseResult.ErrorInfo("❌ Invalid provider.", ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return ResponseResult.ErrorInfo("❌ Provider configuration error.", ex.Message);
            }
            catch (Exception ex)
            {
                return ResponseResult.ErrorInfo("❌ Provider call failed.", ex.Message);
            }
        }

        private static string? TryExtractMetaWamid(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var s = raw.TrimStart();
            if (!s.StartsWith("{")) return null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(s);
                if (doc.RootElement.TryGetProperty("messages", out var msgs) &&
                    msgs.ValueKind == System.Text.Json.JsonValueKind.Array &&
                    msgs.GetArrayLength() > 0 &&
                    msgs[0].TryGetProperty("id", out var idProp))
                {
                    return idProp.GetString();
                }
            }
            catch { }
            return null;
        }
        // ---------- CSV-materialized variables helpers (for campaign recipients) ----------
        private static string[] ReadBodyParams(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
            }
            catch { return Array.Empty<string>(); }
        }

        private static Dictionary<string, string> ReadVarDict(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                       ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static List<string> BuildHeaderTextParams(IDictionary<string, string> kv)
        {
            // Accept either "header.text.{n}" or "headerpara{n}"
            var matches = kv.Keys
                .Select(k => new
                {
                    k,
                    m = System.Text.RegularExpressions.Regex.Match(
                        k, @"^(?:header(?:\.text)?\.)?(\d+)$|^header(?:\.text)?\.(\d+)$|^headerpara(\d+)$",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                })
                .Where(x => x.m.Success)
                .Select(x =>
                {
                    // pull the first captured number
                    for (int g = 1; g < x.m.Groups.Count; g++)
                        if (x.m.Groups[g].Success) return int.Parse(x.m.Groups[g].Value);
                    return 0;
                })
                .Where(n => n > 0)
                .Distinct()
                .OrderBy(n => n)
                .ToList();

            if (matches.Count == 0) return new List<string>();

            var list = new List<string>(new string[matches.Last()]);
            for (int i = 1; i <= list.Count; i++)
            {
                var k1 = $"header.text.{i}";
                var k2 = $"headerpara{i}";
                if (!kv.TryGetValue(k1, out var v))
                    kv.TryGetValue(k2, out v);
                list[i - 1] = v ?? string.Empty;
            }

            return list;
        }

        private static IReadOnlyDictionary<string, string> BuildButtonUrlParams(IDictionary<string, string> kv)
        {
            // Normalize to "button{pos}.url_param"
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int pos = 1; pos <= 3; pos++)
            {
                var k1 = $"button{pos}.url_param"; // canonical
                var k2 = $"buttonpara{pos}";       // alias fallback
                if (kv.TryGetValue(k1, out var v1) && !string.IsNullOrWhiteSpace(v1))
                    map[k1] = v1;
                else if (kv.TryGetValue(k2, out var v2) && !string.IsNullOrWhiteSpace(v2))
                    map[k1] = v2;
            }
            return map;
        }


        public async Task<ResponseResult> SendTemplateMessageAsync(SendMessageDto dto)
        {
            try
            {
                Console.WriteLine($"📨 Sending template message to {dto.RecipientNumber} via BusinessId {dto.BusinessId}");

                if (dto.MessageType != MessageTypeEnum.Template)
                    return ResponseResult.ErrorInfo("Only template messages are supported in this method.");

                // ✅ Validate provider (UPPERCASE only, no normalization)
                if (string.IsNullOrWhiteSpace(dto.Provider) ||
                    (dto.Provider != "PINNACLE" && dto.Provider != "META_CLOUD"))
                {
                    return ResponseResult.ErrorInfo("❌ Invalid provider.",
                        "Provider must be exactly 'PINNACLE' or 'META_CLOUD'.");
                }

                // ✅ Quota
                var quotaCheck = await _planManager.CheckQuotaBeforeSendingAsync(dto.BusinessId);
                if (!quotaCheck.Success)
                {
                    Console.WriteLine($"❌ Quota check failed: {quotaCheck.Message}");
                    return quotaCheck;
                }

                // ✅ Build template components from dto.TemplateParameters
                var bodyParams = (dto.TemplateParameters?.Values?.ToList() ?? new List<string>())
                    .Select(p => new { type = "text", text = p })
                    .ToArray();

                var components = new List<object>();
                if (bodyParams.Length > 0)
                {
                    components.Add(new { type = "body", parameters = bodyParams });
                }

                // 🚀 Send to provider — explicit provider + optional sender override
                var sendResult = await SendViaProviderAsync(
                    dto.BusinessId,
                    dto.Provider, // <-- REQUIRED now
                    p => p.SendTemplateAsync(dto.RecipientNumber, dto.TemplateName!, "en_US", components),
                    dto.PhoneNumberId // <-- optional; relies on default if null
                );

                // ✅ Build rendered body
                var resolvedBody = TemplateParameterHelper.FillPlaceholders(
                    dto.TemplateBody ?? "",
                    dto.TemplateParameters?.Values.ToList() ?? new List<string>());

                // 📝 Log result (store provider raw payload)
                var log = new MessageLog
                {
                    Id = Guid.NewGuid(),
                    BusinessId = dto.BusinessId,
                    RecipientNumber = dto.RecipientNumber,
                    MessageContent = dto.TemplateName ?? "N/A",
                    RenderedBody = resolvedBody,
                    MediaUrl = null,
                    Status = sendResult.Success ? "Sent" : "Failed",
                    ErrorMessage = sendResult.Success ? null : sendResult.Message,
                    RawResponse = sendResult.RawResponse, // <-- not JsonConvert of wrapper
                    MessageId = sendResult.MessageId,
                    CreatedAt = DateTime.UtcNow,
                    SentAt = DateTime.UtcNow,
                    CTAFlowConfigId = dto.CTAFlowConfigId,
                    CTAFlowStepId = dto.CTAFlowStepId,
                };

                await _db.MessageLogs.AddAsync(log);

                // 📉 Decrement remaining quota
                var planInfo = await _db.BusinessPlanInfos
                    .FirstOrDefaultAsync(p => p.BusinessId == dto.BusinessId);

                if (planInfo != null && planInfo.RemainingMessages > 0)
                {
                    planInfo.RemainingMessages -= 1;
                    planInfo.UpdatedAt = DateTime.UtcNow;
                }

                await _db.SaveChangesAsync();

                // 📡 SignalR push
                await _hubContext.Clients
                    .Group($"business_{dto.BusinessId}")
                    .SendAsync("ReceiveMessage", new
                    {
                        Id = log.Id,
                        RecipientNumber = log.RecipientNumber,
                        MessageContent = log.RenderedBody,
                        MediaUrl = log.MediaUrl,
                        Status = log.Status,
                        CreatedAt = log.CreatedAt,
                        SentAt = log.SentAt
                    });

                return ResponseResult.SuccessInfo("✅ Template message sent successfully.", sendResult, log.RawResponse);
            }
            catch (Exception ex)
            {
                var errorId = Guid.NewGuid();
                Console.WriteLine($"🧨 Error ID: {errorId}\n{ex}");

                await _db.MessageLogs.AddAsync(new MessageLog
                {
                    Id = Guid.NewGuid(),
                    BusinessId = dto.BusinessId,
                    RecipientNumber = dto.RecipientNumber,
                    MessageContent = dto.TemplateName ?? "N/A",
                    RenderedBody = TemplateParameterHelper.FillPlaceholders(
                        dto.TemplateBody ?? "",
                        dto.TemplateParameters?.Values.ToList() ?? new List<string>()),
                    Status = "Failed",
                    ErrorMessage = ex.Message,
                    RawResponse = ex.ToString(),
                    CreatedAt = DateTime.UtcNow
                });

                await _db.SaveChangesAsync();

                return ResponseResult.ErrorInfo(
                    $"❌ Exception occurred while sending template message. [Ref: {errorId}]",
                    ex.ToString());
            }
        }

        //public async Task<ResponseResult> SendVideoTemplateMessageAsync(VideoTemplateMessageDto dto, Guid businessId)
        //{
        //    try
        //    {
        //        if (string.IsNullOrWhiteSpace(dto.Provider) || (dto.Provider != "PINNACLE" && dto.Provider != "META_CLOUD"))
        //            return ResponseResult.ErrorInfo("❌ Invalid provider.", "Provider must be exactly 'PINNACLE' or 'META_CLOUD'.");

        //        var components = new List<object>();

        //        if (!string.IsNullOrWhiteSpace(dto.HeaderVideoUrl))
        //        {
        //            components.Add(new
        //            {
        //                type = "header",
        //                parameters = new[] { new { type = "video", video = new { link = dto.HeaderVideoUrl! } } }
        //            });
        //        }

        //        components.Add(new
        //        {
        //            type = "body",
        //            parameters = (dto.TemplateParameters ?? new List<string>())
        //                .Select(p => new { type = "text", text = p })
        //                .ToArray()
        //        });

        //        var btns = dto.ButtonParameters ?? new List<CampaignButtonDto>();
        //        for (int i = 0; i < btns.Count && i < 3; i++)
        //        {
        //            var btn = btns[i];
        //            var subType = btn.ButtonType?.ToLowerInvariant();
        //            if (string.IsNullOrWhiteSpace(subType)) continue;

        //            var button = new Dictionary<string, object>
        //            {
        //                ["type"] = "button",
        //                ["sub_type"] = subType,
        //                ["index"] = i.ToString()
        //            };

        //            if (subType == "quick_reply" && !string.IsNullOrWhiteSpace(btn.TargetUrl))
        //                button["parameters"] = new[] { new { type = "payload", payload = btn.TargetUrl! } };
        //            else if (subType == "url" && !string.IsNullOrWhiteSpace(btn.TargetUrl))
        //                button["parameters"] = new[] { new { type = "text", text = btn.TargetUrl! } };

        //            components.Add(button);
        //        }

        //        var lang = string.IsNullOrWhiteSpace(dto.LanguageCode) ? "en_US" : dto.LanguageCode!;
        //        //var sendResult = await SendViaProviderAsync(
        //        //    businessId,
        //        //    dto.Provider,
        //        //    p => p.SendTemplateAsync(dto.RecipientNumber, dto.TemplateName, lang, components),
        //        //    dto.PhoneNumberId
        //        //);
        //        // ✅ build proper WhatsApp payload (language must be an object)
        //        var payload = new
        //        {
        //            messaging_product = "whatsapp",
        //            to = dto.RecipientNumber,
        //            type = "template",
        //            template = new
        //            {
        //                name = dto.TemplateName,
        //                language = new { code = lang },   // <-- key fix
        //                components = components
        //            }
        //        };

        //        var sendResult = await SendPayloadAsync(
        //            businessId,
        //            dto.Provider,
        //            payload,
        //            dto.PhoneNumberId
        //        );

        //        var renderedBody = TemplateParameterHelper.FillPlaceholders(
        //            dto.TemplateBody ?? "",
        //            dto.TemplateParameters ?? new List<string>());

        //        var log = new MessageLog
        //        {
        //            Id = Guid.NewGuid(),
        //            BusinessId = businessId,
        //            RecipientNumber = dto.RecipientNumber,
        //            MessageContent = dto.TemplateName,
        //            MediaUrl = dto.HeaderVideoUrl,
        //            RenderedBody = renderedBody,
        //            Status = sendResult.Success ? "Sent" : "Failed",
        //            ErrorMessage = sendResult.Success ? null : sendResult.Message,
        //            RawResponse = sendResult.RawResponse,
        //            MessageId = sendResult.MessageId,
        //            SentAt = DateTime.UtcNow,
        //            CreatedAt = DateTime.UtcNow,
        //            CTAFlowConfigId = dto.CTAFlowConfigId,
        //            CTAFlowStepId = dto.CTAFlowStepId
        //        };

        //        await _db.MessageLogs.AddAsync(log);
        //        await _db.SaveChangesAsync();

        //        return new ResponseResult
        //        {
        //            Success = sendResult.Success,
        //            Message = sendResult.Success ? "✅ Template sent successfully." : (sendResult.Message ?? "❌ WhatsApp API returned an error."),
        //            Data = new { Success = sendResult.Success, MessageId = sendResult.MessageId, LogId = log.Id },
        //            RawResponse = sendResult.RawResponse,
        //            MessageId = sendResult.MessageId,
        //            LogId = log.Id
        //        };
        //    }
        //    catch (Exception ex)
        //    {
        //        try
        //        {
        //            await _db.MessageLogs.AddAsync(new MessageLog
        //            {
        //                Id = Guid.NewGuid(),
        //                BusinessId = businessId,
        //                RecipientNumber = dto.RecipientNumber,
        //                MessageContent = dto.TemplateName,
        //                RenderedBody = TemplateParameterHelper.FillPlaceholders(dto.TemplateBody ?? "", dto.TemplateParameters ?? new List<string>()),
        //                MediaUrl = dto.HeaderVideoUrl,
        //                Status = "Failed",
        //                ErrorMessage = ex.Message,
        //                CreatedAt = DateTime.UtcNow,
        //                CTAFlowConfigId = dto.CTAFlowConfigId,
        //                CTAFlowStepId = dto.CTAFlowStepId
        //            });
        //            await _db.SaveChangesAsync();
        //        }
        //        catch { /* ignore */ }

        //        return ResponseResult.ErrorInfo("❌ Template send failed", ex.Message);
        //    }
        //}
        public async Task<ResponseResult> SendVideoTemplateMessageAsync(VideoTemplateMessageDto dto, Guid businessId)
        {
            try
            {
                // ── 0) Basic validation + normalization
                var provider = (dto.Provider ?? "META_CLOUD").Trim().ToUpperInvariant();
                if (provider != "PINNACLE" && provider != "META_CLOUD")
                    return ResponseResult.ErrorInfo("❌ Invalid provider.", "Provider must be exactly 'PINNACLE' or 'META_CLOUD'.");

                if (string.IsNullOrWhiteSpace(dto.RecipientNumber))
                    return ResponseResult.ErrorInfo("❌ Missing recipient number.");

                if (string.IsNullOrWhiteSpace(dto.TemplateName))
                    return ResponseResult.ErrorInfo("❌ Missing template name.");

                if (string.IsNullOrWhiteSpace(dto.HeaderVideoUrl))
                    return ResponseResult.ErrorInfo("🚫 Missing HeaderVideoUrl (must be a direct HTTPS link to a video file).");

                var langCode = string.IsNullOrWhiteSpace(dto.LanguageCode) ? "en_US" : dto.LanguageCode!.Trim();

                // ── 1) Build WhatsApp components
                var components = new List<object>();

                // header → video
                components.Add(new
                {
                    type = "header",
                    parameters = new object[]
                    {
                new { type = "video", video = new { link = dto.HeaderVideoUrl! } }
                    }
                });

                // body → text params
                var bodyParams = (dto.TemplateParameters ?? new List<string>())
                    .Select(p => new { type = "text", text = p })
                    .ToArray();

                components.Add(new { type = "body", parameters = bodyParams });

                // buttons (max 3)
                var btns = (dto.ButtonParameters ?? new List<CampaignButtonDto>()).Take(3).ToList();
                for (int i = 0; i < btns.Count; i++)
                {
                    var b = btns[i];
                    var sub = (b.ButtonType ?? "").Trim().ToLowerInvariant();
                    if (string.IsNullOrEmpty(sub)) continue;

                    var button = new Dictionary<string, object>
                    {
                        ["type"] = "button",
                        ["sub_type"] = sub,
                        ["index"] = i.ToString()
                    };

                    if (sub == "url" && !string.IsNullOrWhiteSpace(b.TargetUrl))
                    {
                        button["parameters"] = new object[] { new { type = "text", text = b.TargetUrl! } };
                    }
                    else if (sub == "quick_reply" && !string.IsNullOrWhiteSpace(b.TargetUrl))
                    {
                        // For quick replies, providers expect a payload string
                        button["parameters"] = new object[] { new { type = "payload", payload = b.TargetUrl! } };
                    }

                    components.Add(button);
                }

                // ── 2) Full WhatsApp template payload (language is an OBJECT)
                var payload = new
                {
                    messaging_product = "whatsapp",
                    to = dto.RecipientNumber!,
                    type = "template",
                    template = new
                    {
                        name = dto.TemplateName!,
                        language = new { code = langCode },  // ✅ required object shape
                        components = components
                    }
                };

                // ── 3) Send via provider (pass through PhoneNumberId when supplied)
                //var sendResult = await _messageEngineService.SendPayloadAsync(
                //    businessId,
                //    provider,
                //    payload,
                //    dto.PhoneNumberId
                //);
                var sendResult = await SendPayloadAsync(businessId, provider, payload, dto.PhoneNumberId);
                // ── 4) Persist message log (best-effort)
                var renderedBody = TemplateParameterHelper.FillPlaceholders(
                    dto.TemplateBody ?? "",
                    dto.TemplateParameters ?? new List<string>());

                var log = new MessageLog
                {
                    Id = Guid.NewGuid(),
                    BusinessId = businessId,
                    RecipientNumber = dto.RecipientNumber!,
                    MessageContent = dto.TemplateName!,
                    MediaUrl = dto.HeaderVideoUrl,        // mirrors header media
                    RenderedBody = renderedBody,
                    Status = sendResult.Success ? "Sent" : "Failed",
                    ErrorMessage = sendResult.ErrorMessage ?? (sendResult.Success ? null : "WhatsApp API returned an error."),
                    RawResponse = sendResult.RawResponse,
                    MessageId = sendResult.MessageId,
                    SentAt = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow,
                    CTAFlowConfigId = dto.CTAFlowConfigId,
                    CTAFlowStepId = dto.CTAFlowStepId
                };

                await _db.MessageLogs.AddAsync(log);
                await _db.SaveChangesAsync();

                return new ResponseResult
                {
                    Success = sendResult.Success,
                    Message = sendResult.Success ? "✅ Template sent successfully." : (sendResult.ErrorMessage ?? "❌ WhatsApp API returned an error."),
                    Data = new { Success = sendResult.Success, MessageId = sendResult.MessageId, LogId = log.Id },
                    RawResponse = sendResult.RawResponse,
                    MessageId = sendResult.MessageId,
                    LogId = log.Id
                };
            }
            catch (Exception ex)
            {
                // failure log (best effort)
                try
                {
                    await _db.MessageLogs.AddAsync(new MessageLog
                    {
                        Id = Guid.NewGuid(),
                        BusinessId = businessId,
                        RecipientNumber = dto.RecipientNumber ?? "",
                        MessageContent = dto.TemplateName ?? "",
                        RenderedBody = TemplateParameterHelper.FillPlaceholders(dto.TemplateBody ?? "", dto.TemplateParameters ?? new List<string>()),
                        MediaUrl = dto.HeaderVideoUrl,
                        Status = "Failed",
                        ErrorMessage = ex.Message,
                        CreatedAt = DateTime.UtcNow,
                        CTAFlowConfigId = dto.CTAFlowConfigId,
                        CTAFlowStepId = dto.CTAFlowStepId
                    });
                    await _db.SaveChangesAsync();
                }
                catch { /* ignore */ }

                return ResponseResult.ErrorInfo("❌ Template send failed", ex.Message);
            }
        }

        private async Task<IReadOnlyList<WhatsAppSettingEntity>> GetBusinessWhatsAppSettingsAsync(Guid businessId)
        {
            if (_settingsCache.TryGetValue(businessId, out var cached) && cached.expiresAt > DateTime.UtcNow)
                return cached.setting;

            // Load all settings rows for this business (supports multiple providers)
            var items = await _db.WhatsAppSettings
                .Where(s => s.BusinessId == businessId)
                .ToListAsync();

            if (items == null || items.Count == 0)
                throw new Exception("WhatsApp settings not found.");

            var ro = items.AsReadOnly();
            _settingsCache[businessId] = (ro, DateTime.UtcNow.AddMinutes(5));
            return ro;
        }


        public async Task<ResponseResult> SendTextDirectAsync(TextMessageSendDto dto)
        {
            try
            {
                var businessId = _httpContextAccessor.HttpContext?.User?.GetBusinessId()
                    ?? throw new UnauthorizedAccessException("❌ Cannot resolve BusinessId from context.");

                // --- Resolve/validate provider & sender -------------------------------
                // Normalize inbound (trim+upper) but DO NOT silently map unknown values
                string? provider = dto.Provider?.Trim().ToUpperInvariant();
                string? phoneNumberId = string.IsNullOrWhiteSpace(dto.PhoneNumberId)
                    ? null
                    : dto.PhoneNumberId!.Trim();

                // If provider not supplied, try to resolve from active settings:
                // - Prefer a row that already has a default sender (PhoneNumberId not null)
                // - If multiple rows and none has default, ask the caller to specify
                xbytechat_api.WhatsAppSettings.Models.WhatsAppSettingEntity? chosenSetting = null;

                if (string.IsNullOrWhiteSpace(provider))
                {
                    var candidates = await _db.WhatsAppSettings
                        .AsNoTracking()
                        .Where(s => s.BusinessId == businessId && s.IsActive)
                        .OrderByDescending(s => s.PhoneNumberId != null)          // prefer defaulted
                        //.ThenByDescending(s => s.UpdatedAt ?? s.CreatedAt)
                        .ThenByDescending(s => (s.UpdatedAt > s.CreatedAt ? s.UpdatedAt : s.CreatedAt))

                        .ToListAsync();

                    if (candidates.Count == 0)
                        return ResponseResult.ErrorInfo("❌ WhatsApp settings not found for this business.");

                    if (candidates.Count > 1 && candidates.All(s => s.PhoneNumberId == null))
                        return ResponseResult.ErrorInfo("❌ Multiple providers are active but no default sender is set. Please pass Provider (PINNACLE/META_CLOUD) or set a Default number.");

                    chosenSetting = candidates[0];
                    provider = (chosenSetting.Provider ?? "").Trim().ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(phoneNumberId) && !string.IsNullOrWhiteSpace(chosenSetting.PhoneNumberId))
                        phoneNumberId = chosenSetting.PhoneNumberId!.Trim();
                }

                // Final provider check (must be one of the two)
                if (provider != "PINNACLE" && provider != "META_CLOUD")
                {
                    return ResponseResult.ErrorInfo(
                        "❌ Invalid provider.",
                        "Provider must be exactly 'PINNACLE' or 'META_CLOUD'."
                    );
                }

                // If provider was supplied but no sender, we can still inherit the default
                if (string.IsNullOrWhiteSpace(phoneNumberId))
                {
                    // Try to find a default sender for the chosen provider
                    var defaultRow = await _db.WhatsAppSettings
                        .AsNoTracking()
                        .Where(s => s.BusinessId == businessId &&
                                    s.IsActive &&
                                    s.Provider == provider &&
                                    s.PhoneNumberId != null)
                        .OrderByDescending(s => (s.UpdatedAt > s.CreatedAt ? s.UpdatedAt : s.CreatedAt))

                        //.OrderByDescending(s => s.UpdatedAt ?? s.CreatedAt)

                        .FirstOrDefaultAsync();

                    if (defaultRow != null)
                        phoneNumberId = defaultRow.PhoneNumberId!.Trim();
                }
                // ----------------------------------------------------------------------

                Guid? contactId = null;

                // 1) Try to find contact by business + phone (indexed)
                var contact = await _db.Contacts.FirstOrDefaultAsync(c =>
                    c.BusinessId == businessId && c.PhoneNumber == dto.RecipientNumber);

                if (contact != null)
                {
                    // 2) Touch last-contacted when reusing an existing contact
                    contactId = contact.Id;
                    contact.LastContactedAt = DateTime.UtcNow;
                }
                else if (dto.IsSaveContact)
                {
                    // 3) Create a new contact if requested
                    contact = new Contact
                    {
                        Id = Guid.NewGuid(),
                        BusinessId = businessId,
                        Name = "WhatsApp User",
                        PhoneNumber = dto.RecipientNumber,
                        CreatedAt = DateTime.UtcNow,
                        LastContactedAt = DateTime.UtcNow
                    };
                    _db.Contacts.Add(contact);
                    contactId = contact.Id;
                }

                // 4) Persist contact changes (create or timestamp update)
                await _db.SaveChangesAsync();

                // 🚀 Send via provider — explicit provider + optional sender override
                var sendResult = await SendViaProviderAsync(
                    businessId,
                    provider!,                                                     // validated value
                    p => p.SendTextAsync(dto.RecipientNumber, dto.TextContent),
                    phoneNumberId                                                  // optional
                );

                // 🔎 Extract provider message id (fallback to Meta messages[0].id if needed)
                string? messageId = sendResult.MessageId;
                if (string.IsNullOrWhiteSpace(messageId) && !string.IsNullOrWhiteSpace(sendResult.RawResponse))
                {
                    try
                    {
                        var raw = sendResult.RawResponse!.TrimStart();
                        if (raw.StartsWith("{"))
                        {
                            using var parsed = System.Text.Json.JsonDocument.Parse(raw);
                            if (parsed.RootElement.TryGetProperty("messages", out var msgs)
                                && msgs.ValueKind == System.Text.Json.JsonValueKind.Array
                                && msgs.GetArrayLength() > 0
                                && msgs[0].TryGetProperty("id", out var idProp))
                            {
                                messageId = idProp.GetString();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠️ JSON parsing failed: {ex.Message} | Raw: {sendResult.RawResponse}");
                    }
                }

                // 📝 Log the message
                var log = new MessageLog
                {
                    Id = Guid.NewGuid(),
                    BusinessId = businessId,
                    RecipientNumber = dto.RecipientNumber,
                    MessageContent = dto.TextContent,
                    RenderedBody = dto.TextContent,
                    ContactId = contactId,
                    MediaUrl = null,
                    Status = sendResult.Success ? "Sent" : "Failed",
                    ErrorMessage = sendResult.Success ? null : sendResult.Message,
                    RawResponse = sendResult.RawResponse,
                    CreatedAt = DateTime.UtcNow,
                    SentAt = DateTime.UtcNow,
                    MessageId = messageId
                };

                await _db.MessageLogs.AddAsync(log);
                await _db.SaveChangesAsync();

                // 🔗 Optional campaign mapping
                Guid? campaignSendLogId = null;
                if (dto.Source == "campaign" && !string.IsNullOrEmpty(messageId))
                {
                    try
                    {
                        campaignSendLogId = await _messageIdResolver.ResolveCampaignSendLogIdAsync(messageId);
                        Console.WriteLine($"📦 CampaignSendLog resolved: {campaignSendLogId}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠️ Failed to resolve campaign log for {messageId}: {ex.Message}");
                    }
                }

                return new ResponseResult
                {
                    Success = sendResult.Success,
                    Message = sendResult.Success
                                ? "✅ Text message sent successfully."
                                : (sendResult.Message ?? "❌ WhatsApp API returned an error."),
                    Data = new
                    {
                        Success = sendResult.Success,
                        MessageId = messageId,
                        LogId = log.Id,
                        CampaignSendLogId = campaignSendLogId
                    },
                    RawResponse = sendResult.RawResponse,
                    MessageId = messageId,
                    LogId = log.Id
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Exception in SendTextDirectAsync: {ex.Message}");

                try
                {
                    var businessId = _httpContextAccessor.HttpContext?.User?.GetBusinessId()
                        ?? throw new UnauthorizedAccessException("❌ Cannot resolve BusinessId in failure path.");

                    await _db.MessageLogs.AddAsync(new MessageLog
                    {
                        Id = Guid.NewGuid(),
                        BusinessId = businessId,
                        RecipientNumber = dto.RecipientNumber,
                        MessageContent = dto.TextContent,
                        Status = "Failed",
                        ErrorMessage = ex.Message,
                        CreatedAt = DateTime.UtcNow
                    });

                    await _db.SaveChangesAsync();
                }
                catch (Exception logEx)
                {
                    Console.WriteLine($"❌ Failed to log failure to DB: {logEx.Message}");
                }

                return ResponseResult.ErrorInfo("❌ Failed to send text message.", ex.ToString());
            }
        }

        public async Task<ResponseResult> SendAutomationReply(TextMessageSendDto dto)
        {
            try
            {
                var businessId =
                    dto.BusinessId != Guid.Empty
                        ? dto.BusinessId
                        : _httpContextAccessor.HttpContext?.User?.GetBusinessId()
                          ?? throw new UnauthorizedAccessException("❌ Cannot resolve BusinessId from context or DTO.");

                // ✅ Validate provider (no server-side normalization)
                if (string.IsNullOrWhiteSpace(dto.Provider) ||
                    (dto.Provider != "PINNACLE" && dto.Provider != "META_CLOUD"))
                {
                    return ResponseResult.ErrorInfo("❌ Invalid provider.",
                        "Provider must be exactly 'PINNACLE' or 'META_CLOUD'.");
                }

                Guid? contactId = null;
                try
                {
                    var contact = await _contactService.FindOrCreateAsync(businessId, dto.RecipientNumber);
                    contactId = contact.Id;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Failed to resolve or create contact: {ex.Message}");
                }

                // 🚀 Send via provider — explicit provider + optional sender override
                var sendResult = await SendViaProviderAsync(
                    businessId,
                    dto.Provider,
                    p => p.SendTextAsync(dto.RecipientNumber, dto.TextContent),
                    dto.PhoneNumberId
                );

                // 🔎 Try to get a provider message id (use provider-provided id first, then Meta fallback)
                string? messageId = sendResult.MessageId;
                var raw = sendResult.RawResponse;
                if (string.IsNullOrWhiteSpace(messageId) && !string.IsNullOrWhiteSpace(raw))
                {
                    try
                    {
                        var s = raw.TrimStart();
                        if (s.StartsWith("{"))
                        {
                            using var parsed = JsonDocument.Parse(s);
                            if (parsed.RootElement.TryGetProperty("messages", out var messages) &&
                                messages.ValueKind == JsonValueKind.Array &&
                                messages.GetArrayLength() > 0 &&
                                messages[0].TryGetProperty("id", out var idProp))
                            {
                                messageId = idProp.GetString();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠️ JSON parsing failed: {ex.Message} | Raw: {raw}");
                    }
                }

                // 📝 Log result (store raw provider payload; don’t serialize the wrapper)
                var log = new MessageLog
                {
                    Id = Guid.NewGuid(),
                    BusinessId = businessId,
                    RecipientNumber = dto.RecipientNumber,
                    MessageContent = dto.TextContent,
                    RenderedBody = dto.TextContent,
                    ContactId = contactId,
                    MediaUrl = null,
                    Status = sendResult.Success ? "Sent" : "Failed",
                    ErrorMessage = sendResult.Success ? null : sendResult.Message,
                    RawResponse = sendResult.RawResponse,
                    CreatedAt = DateTime.UtcNow,
                    SentAt = DateTime.UtcNow,
                    MessageId = messageId
                };

                await _db.MessageLogs.AddAsync(log);
                await _db.SaveChangesAsync();

                Guid? campaignSendLogId = null;
                if (dto.Source == "campaign" && !string.IsNullOrEmpty(messageId))
                {
                    try
                    {
                        campaignSendLogId = await _messageIdResolver.ResolveCampaignSendLogIdAsync(messageId);
                        Console.WriteLine($"📦 CampaignSendLog resolved: {campaignSendLogId}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠️ Failed to resolve campaign log for {messageId}: {ex.Message}");
                    }
                }

                return new ResponseResult
                {
                    Success = sendResult.Success,
                    Message = sendResult.Success
                        ? "✅ Text message sent successfully."
                        : (sendResult.Message ?? "❌ WhatsApp API returned an error."),
                    Data = new
                    {
                        Success = sendResult.Success,
                        MessageId = messageId,
                        LogId = log.Id,
                        CampaignSendLogId = campaignSendLogId
                    },
                    RawResponse = sendResult.RawResponse,
                    MessageId = messageId,
                    LogId = log.Id
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Exception in SendAutomationReply: {ex.Message}");

                try
                {
                    var businessId =
                        dto.BusinessId != Guid.Empty
                            ? dto.BusinessId
                            : _httpContextAccessor.HttpContext?.User?.GetBusinessId()
                              ?? throw new UnauthorizedAccessException("❌ Cannot resolve BusinessId in failure path.");

                    await _db.MessageLogs.AddAsync(new MessageLog
                    {
                        Id = Guid.NewGuid(),
                        BusinessId = businessId,
                        RecipientNumber = dto.RecipientNumber,
                        MessageContent = dto.TextContent,
                        Status = "Failed",
                        ErrorMessage = ex.Message,
                        CreatedAt = DateTime.UtcNow
                    });

                    await _db.SaveChangesAsync();
                }
                catch (Exception logEx)
                {
                    Console.WriteLine($"❌ Failed to log failure to DB: {logEx.Message}");
                }

                return ResponseResult.ErrorInfo("❌ Failed to send text message.", ex.ToString());
            }
        }
        public async Task<ResponseResult> SendTemplateMessageSimpleAsync(Guid businessId, SimpleTemplateMessageDto dto)
        {
            try
            {
                // 0) Soft-resolve provider & sender (no hard early return)
                var provider = (dto.Provider ?? string.Empty).Trim().ToUpperInvariant();
                var phoneNumberId = dto.PhoneNumberId;

                if (provider != "PINNACLE" && provider != "META_CLOUD")
                {
                    // Try active WhatsAppSettings first (usually the “current” sender)
                    var ws = await _db.WhatsAppSettings
                        .AsNoTracking()
                        .Where(x => x.BusinessId == businessId && x.IsActive)
                        .OrderByDescending(x => x.UpdatedAt ?? x.CreatedAt)
                        .FirstOrDefaultAsync();

                    if (ws != null)
                    {
                        provider = (ws.Provider ?? string.Empty).Trim().ToUpperInvariant();
                        if (string.IsNullOrWhiteSpace(phoneNumberId))
                            phoneNumberId = ws.PhoneNumberId;
                    }

                    // Fallback to WhatsAppPhoneNumbers (prefer default, then stable order)
                    if (provider != "PINNACLE" && provider != "META_CLOUD")
                    {
                        var pn = await _db.WhatsAppPhoneNumbers
                            .AsNoTracking()
                            .Where(n => n.BusinessId == businessId && n.IsActive)
                            .OrderByDescending(n => n.IsDefault)
                            .ThenBy(n => n.WhatsAppBusinessNumber)
                            .Select(n => new { n.Provider, n.PhoneNumberId })
                            .FirstOrDefaultAsync();

                        if (pn != null)
                        {
                            provider = (pn.Provider ?? string.Empty).Trim().ToUpperInvariant();
                            if (string.IsNullOrWhiteSpace(phoneNumberId))
                                phoneNumberId = pn.PhoneNumberId;
                        }
                    }
                }

                // If still unknown, fail clearly (we tried our best)
                if (provider != "PINNACLE" && provider != "META_CLOUD")
                {
                    return ResponseResult.ErrorInfo("❌ Missing provider.",
                        "No active WhatsApp sender found. Configure a PINNACLE or META_CLOUD sender for this business.");
                }

                // 1) Build minimal components (body only)
                var components = new List<object>();
                var parameters = (dto.TemplateParameters ?? new List<string>())
                    .Select(p => new { type = "text", text = p })
                    .ToArray();

                if (parameters.Length > 0)
                {
                    components.Add(new { type = "body", parameters });
                }

                // 2) Always send via provider — explicit provider + sender override (if given/resolved)
                var lang = string.IsNullOrWhiteSpace(dto.LanguageCode) ? "en_US" : dto.LanguageCode!;
                _logger?.LogInformation("➡️ SEND-INTENT tmpl={Template} to={To} provider={Provider} pnid={PhoneNumberId}",
                    dto.TemplateName, dto.RecipientNumber, provider, phoneNumberId ?? "(default)");

                var sendResult = await SendViaProviderAsync(
                    businessId,
                    provider, // explicit, resolved above
                    p => p.SendTemplateAsync(dto.RecipientNumber, dto.TemplateName, lang, components),
                    phoneNumberId // explicit; lets the adapter pick the correct sender
                );

                // 3) Log message (store provider raw response, not the whole wrapper)
                var log = new MessageLog
                {
                    Id = Guid.NewGuid(),
                    BusinessId = businessId,
                    RecipientNumber = dto.RecipientNumber,
                    MessageContent = dto.TemplateName,
                    RenderedBody = TemplateParameterHelper.FillPlaceholders(
                        dto.TemplateBody ?? string.Empty,
                        dto.TemplateParameters ?? new List<string>()),

                    // Optional flow context from DTO (if this simple send was triggered by a flow)
                    CTAFlowConfigId = dto.CTAFlowConfigId,
                    CTAFlowStepId = dto.CTAFlowStepId,

                    // Helpful for downstream analysis
                    Provider = provider,
                    ProviderMessageId = sendResult.MessageId,

                    Status = sendResult.Success ? "Sent" : "Failed",
                    ErrorMessage = sendResult.Success ? null : sendResult.Message,
                    RawResponse = sendResult.RawResponse,
                    MessageId = sendResult.MessageId,
                    SentAt = sendResult.Success ? DateTime.UtcNow : (DateTime?)null,
                    CreatedAt = DateTime.UtcNow,
                    Source = "api" // or "simple_send"
                };

                await _db.MessageLogs.AddAsync(log);
                await _db.SaveChangesAsync();

                return new ResponseResult
                {
                    Success = sendResult.Success,
                    Message = sendResult.Success
                        ? "✅ Template sent successfully."
                        : (sendResult.Message ?? "❌ WhatsApp API returned an error."),
                    Data = new
                    {
                        Success = sendResult.Success,
                        MessageId = sendResult.MessageId,
                        LogId = log.Id
                    },
                    RawResponse = sendResult.RawResponse,
                    MessageId = sendResult.MessageId,
                    LogId = log.Id
                };
            }
            catch (Exception ex)
            {
                // Best-effort failure log
                try
                {
                    await _db.MessageLogs.AddAsync(new MessageLog
                    {
                        Id = Guid.NewGuid(),
                        BusinessId = businessId,
                        RecipientNumber = dto.RecipientNumber,
                        MessageContent = dto.TemplateName,
                        RenderedBody = TemplateParameterHelper.FillPlaceholders(
                            dto.TemplateBody ?? string.Empty,
                            dto.TemplateParameters ?? new List<string>()),
                        Status = "Failed",
                        ErrorMessage = ex.Message,
                        CreatedAt = DateTime.UtcNow,
                        Source = "api"
                    });
                    await _db.SaveChangesAsync();
                }
                catch { /* ignore log errors */ }

                return ResponseResult.ErrorInfo("❌ Template send failed", ex.Message);
            }
        }


        //public async Task<ResponseResult> SendTemplateMessageSimpleAsync(Guid businessId, SimpleTemplateMessageDto dto)
        //{
        //    try
        //    {
        //        // 0) Validate provider (no server-side normalization)
        //        if (string.IsNullOrWhiteSpace(dto.Provider) ||
        //            (dto.Provider != "PINNACLE" && dto.Provider != "META_CLOUD"))
        //        {
        //            return ResponseResult.ErrorInfo("❌ Invalid provider.",
        //                "Provider must be exactly 'PINNACLE' or 'META_CLOUD'.");
        //        }

        //        // 1) Build minimal components (body only)
        //        var components = new List<object>();
        //        var parameters = (dto.TemplateParameters ?? new List<string>())
        //            .Select(p => new { type = "text", text = p })
        //            .ToArray();

        //        if (parameters.Length > 0)
        //        {
        //            components.Add(new { type = "body", parameters });
        //        }

        //        // 2) Send via provider — explicit provider + optional sender override
        //        var lang = string.IsNullOrWhiteSpace(dto.LanguageCode) ? "en_US" : dto.LanguageCode!;
        //        var sendResult = await SendViaProviderAsync(
        //            businessId,
        //            dto.Provider, // <-- explicit provider
        //            p => p.SendTemplateAsync(dto.RecipientNumber, dto.TemplateName, lang, components),
        //            dto.PhoneNumberId // <-- optional sender override
        //        );

        //        // 3) Log message (store provider raw response, not the whole wrapper)
        //        var log = new MessageLog
        //        {
        //            Id = Guid.NewGuid(),
        //            BusinessId = businessId,
        //            RecipientNumber = dto.RecipientNumber,
        //            MessageContent = dto.TemplateName,
        //            RenderedBody = TemplateParameterHelper.FillPlaceholders(dto.TemplateBody ?? "", dto.TemplateParameters ?? new List<string>()),
        //            Status = sendResult.Success ? "Sent" : "Failed",
        //            ErrorMessage = sendResult.Success ? null : sendResult.Message,
        //            RawResponse = sendResult.RawResponse,
        //            MessageId = sendResult.MessageId,     // capture if available
        //            SentAt = DateTime.UtcNow,
        //            CreatedAt = DateTime.UtcNow
        //        };

        //        await _db.MessageLogs.AddAsync(log);
        //        await _db.SaveChangesAsync();

        //        return new ResponseResult
        //        {
        //            Success = sendResult.Success,
        //            Message = sendResult.Success
        //                ? "✅ Template sent successfully."
        //                : (sendResult.Message ?? "❌ WhatsApp API returned an error."),
        //            Data = new
        //            {
        //                Success = sendResult.Success,
        //                MessageId = sendResult.MessageId,
        //                LogId = log.Id
        //            },
        //            RawResponse = sendResult.RawResponse,
        //            MessageId = sendResult.MessageId,
        //            LogId = log.Id
        //        };
        //    }
        //    catch (Exception ex)
        //    {
        //        // Best-effort failure log
        //        try
        //        {
        //            await _db.MessageLogs.AddAsync(new MessageLog
        //            {
        //                Id = Guid.NewGuid(),
        //                BusinessId = businessId,
        //                RecipientNumber = dto.RecipientNumber,
        //                MessageContent = dto.TemplateName,
        //                RenderedBody = TemplateParameterHelper.FillPlaceholders(dto.TemplateBody ?? "", dto.TemplateParameters ?? new List<string>()),
        //                Status = "Failed",
        //                ErrorMessage = ex.Message,
        //                CreatedAt = DateTime.UtcNow
        //            });
        //            await _db.SaveChangesAsync();
        //        }
        //        catch { /* ignore log errors */ }

        //        return ResponseResult.ErrorInfo("❌ Template send failed", ex.Message);
        //    }
        //}

        public async Task<ResponseResult> SendImageCampaignAsync(Guid campaignId, Guid businessId, string sentBy)
        {
            try
            {
                var campaign = await _db.Campaigns
                    .Include(c => c.MultiButtons)
                    .FirstOrDefaultAsync(c => c.Id == campaignId && c.BusinessId == businessId);

                if (campaign == null)
                    return ResponseResult.ErrorInfo("❌ Campaign not found or unauthorized.");

                var recipients = await _db.CampaignRecipients
                    .Include(r => r.Contact)
                    .Where(r => r.CampaignId == campaignId && r.BusinessId == businessId)
                    .ToListAsync();

                if (recipients.Count == 0)
                    return ResponseResult.ErrorInfo("⚠️ No recipients assigned to this campaign.");

                if (string.IsNullOrWhiteSpace(campaign.ImageCaption))
                    return ResponseResult.ErrorInfo("❌ Campaign caption (ImageCaption) is required.");

                var validButtons = campaign.MultiButtons
                    ?.Where(b => !string.IsNullOrWhiteSpace(b.Title))
                    .Select(b => new CtaButtonDto { Title = b.Title, Value = b.Value })
                    .ToList();

                if (validButtons == null || validButtons.Count == 0)
                    return ResponseResult.ErrorInfo("❌ At least one CTA button with a valid title is required.");

                int successCount = 0, failCount = 0;

                foreach (var recipient in recipients)
                {
                    if (recipient.Contact == null || string.IsNullOrWhiteSpace(recipient.Contact.PhoneNumber))
                    {
                        Console.WriteLine($"⚠️ Skipping invalid contact: {recipient.Id}");
                        failCount++;
                        continue;
                    }

                    var dto = new SendMessageDto
                    {
                        BusinessId = businessId,
                        RecipientNumber = recipient.Contact.PhoneNumber,
                        MessageType = MessageTypeEnum.Image,
                        MediaUrl = campaign.ImageUrl,
                        TextContent = campaign.MessageTemplate,
                        CtaButtons = validButtons,

                        CampaignId = campaign.Id,
                        SourceModule = "image-campaign",
                        CustomerId = recipient.Contact.Id.ToString(),
                        CustomerName = recipient.Contact.Name,
                        CustomerPhone = recipient.Contact.PhoneNumber,
                        CTATriggeredFrom = "campaign"
                    };

                    var result = await SendImageWithCtaAsync(dto);

                    var sendLog = new CampaignSendLog
                    {
                        Id = Guid.NewGuid(),
                        CampaignId = campaign.Id,
                        ContactId = recipient.Contact.Id,
                        RecipientId = recipient.Id,
                        MessageLogId = result?.LogId,
                        SendStatus = result.Success ? "Sent" : "Failed",
                        SentAt = DateTime.UtcNow,
                        CreatedBy = sentBy,
                        BusinessId = businessId,
                    };

                    await _db.CampaignSendLogs.AddAsync(sendLog);

                    if (result.Success) successCount++;
                    else failCount++;
                }

                await _db.SaveChangesAsync();

                return ResponseResult.SuccessInfo($"✅ Campaign sent.\n📤 Success: {successCount}, ❌ Failed: {failCount}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error sending image campaign: {ex.Message}");
                return ResponseResult.ErrorInfo("❌ Unexpected error while sending image campaign.", ex.ToString());
            }
        }
        // Features/MessageManagement/Services/MessageEngineService.cs
        /// <summary>
        /// Sends a TEMPLATE campaign using CSV-materialized variables stored on CampaignRecipients:
        /// - ResolvedParametersJson (string[] for body {{n}})
        /// - ResolvedButtonUrlsJson (dict for header.text.{n} and button{pos}.url_param)
        /// Uses campaign-level HeaderKind + HeaderMediaUrl for media headers.
        /// </summary>
        // Sends a TEMPLATE campaign using materialized per-recipient values from CSV.
        // - Reads CampaignRecipients.ResolvedParametersJson (string[] for body {{n}})
        // - Reads CampaignRecipients.ResolvedButtonUrlsJson (dict: "button{1..3}.url_param", optional "header.image_url")
        // - Uses Campaign.ImageUrl as header media if present; otherwise uses header.image_url from the dict if provided.
        public async Task<ResponseResult> SendTemplateCampaignAsync(Guid campaignId, Guid businessId, string sentBy)
        {
            try
            {
                // 1) Load campaign (minimal fields we need)
                var campaign = await _db.Campaigns
                    .AsNoTracking()
                    .Where(c => c.Id == campaignId && c.BusinessId == businessId)
                    .Select(c => new
                    {
                        c.Id,
                        c.BusinessId,
                        c.MessageTemplate,
                        c.TemplateId,
                        c.Provider,
                        c.PhoneNumberId,
                        c.ImageUrl // used as header media if template expects image
                    })
                    .FirstOrDefaultAsync();

                if (campaign == null)
                    return ResponseResult.ErrorInfo("❌ Campaign not found or unauthorized.");

                // Template name
                var templateName = !string.IsNullOrWhiteSpace(campaign.TemplateId)
                    ? campaign.TemplateId!
                    : (campaign.MessageTemplate ?? "").Trim();

                if (string.IsNullOrWhiteSpace(templateName))
                    return ResponseResult.ErrorInfo("❌ Campaign has no template selected.");

                // 2) Determine language (fallback en_US)
                var lang = await _db.WhatsAppTemplates
                    .AsNoTracking()
                    .Where(w => w.BusinessId == businessId && w.Name == templateName)
                    .OrderByDescending(w => (w.UpdatedAt > w.CreatedAt ? w.UpdatedAt : w.CreatedAt))
                    .Select(w => w.Language)
                    .FirstOrDefaultAsync();

                if (string.IsNullOrWhiteSpace(lang)) lang = "en_US";

                // 3) Load recipients with materialized vars + phone
                var recipients = await _db.CampaignRecipients
                    .AsNoTracking()
                    .Include(r => r.AudienceMember)
                    .Include(r => r.Contact) // optional fallback for phone
                    .Where(r => r.CampaignId == campaignId && r.BusinessId == businessId)
                    .Select(r => new
                    {
                        r.Id,
                        r.ResolvedParametersJson,   // string[]
                        r.ResolvedButtonUrlsJson,   // dict
                        Phone = r.AudienceMember != null && !string.IsNullOrEmpty(r.AudienceMember.PhoneE164)
                                ? r.AudienceMember.PhoneE164
                                : (r.Contact != null ? r.Contact.PhoneNumber : null)
                    })
                    .ToListAsync();

                if (recipients.Count == 0)
                    return ResponseResult.ErrorInfo("⚠️ No recipients materialized for this campaign.");

                // 4) Provider and sender validation (no normalization here)
                var provider = (campaign.Provider ?? "").Trim().ToUpperInvariant();
                if (provider != "PINNACLE" && provider != "META_CLOUD")
                    return ResponseResult.ErrorInfo("❌ Invalid provider on campaign. Must be 'PINNACLE' or 'META_CLOUD'.");

                var phoneNumberId = string.IsNullOrWhiteSpace(campaign.PhoneNumberId)
                    ? null
                    : campaign.PhoneNumberId!.Trim();

                if (string.IsNullOrWhiteSpace(phoneNumberId))
                    return ResponseResult.ErrorInfo("❌ Campaign has no sender number (PhoneNumberId).");

                int success = 0, fail = 0;

                foreach (var r in recipients)
                {
                    if (string.IsNullOrWhiteSpace(r.Phone))
                    {
                        fail++;
                        continue;
                    }

                    // Deserialize materialized values
                    string[] bodyParams;
                    try
                    {
                        bodyParams = string.IsNullOrWhiteSpace(r.ResolvedParametersJson)
                            ? Array.Empty<string>()
                            : System.Text.Json.JsonSerializer.Deserialize<string[]>(r.ResolvedParametersJson!) ?? Array.Empty<string>();
                    }
                    catch { bodyParams = Array.Empty<string>(); }

                    Dictionary<string, string> buttonVars;
                    try
                    {
                        buttonVars = string.IsNullOrWhiteSpace(r.ResolvedButtonUrlsJson)
                            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(r.ResolvedButtonUrlsJson!)
                              ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        buttonVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    }

                    // 5) Build WhatsApp components
                    var components = new List<object>();

                    // Header (image) — priority: Campaign.ImageUrl -> header.image_url in dict
                    var headerImage = !string.IsNullOrWhiteSpace(campaign.ImageUrl) ? campaign.ImageUrl
                                   : (buttonVars.TryGetValue("header.image_url", out var hv) && !string.IsNullOrWhiteSpace(hv) ? hv : null);

                    if (!string.IsNullOrWhiteSpace(headerImage))
                    {
                        components.Add(new
                        {
                            type = "header",
                            parameters = new object[]
                            {
                        new { type = "image", image = new { link = headerImage! } }
                            }
                        });
                    }

                    // Body params
                    if (bodyParams.Length > 0)
                    {
                        var bp = bodyParams.Select(p => new { type = "text", text = p ?? "" }).ToArray();
                        components.Add(new { type = "body", parameters = bp });
                    }

                    // Dynamic URL buttons (button{1..3}.url_param)
                    for (int pos = 1; pos <= 3; pos++)
                    {
                        var key = $"button{pos}.url_param";
                        if (buttonVars.TryGetValue(key, out var urlParam) && !string.IsNullOrWhiteSpace(urlParam))
                        {
                            components.Add(new
                            {
                                type = "button",
                                sub_type = "url",
                                index = (pos - 1).ToString(), // Meta expects 0-based string index
                                parameters = new object[] { new { type = "text", text = urlParam } }
                            });
                        }
                    }

                    // 6) Full payload
                    var payload = new
                    {
                        messaging_product = "whatsapp",
                        to = r.Phone!,
                        type = "template",
                        template = new
                        {
                            name = templateName,
                            language = new { code = lang },
                            components = components
                        }
                    };

                    // 7) Send via provider
                    var result = await SendPayloadAsync(businessId, provider, payload, phoneNumberId);
                    if (result.Success) success++; else fail++;

                    // OPTIONAL: you can write a CampaignSendLog here, mirroring your image path.
                    // (Omitted to keep this tight; add if you want parity with image campaigns)
                }

                return ResponseResult.SuccessInfo($"✅ Template campaign sent. 📤 Success: {success}, ❌ Failed: {fail}");
            }
            catch (Exception ex)
            {
                return ResponseResult.ErrorInfo("❌ Error sending template campaign.", ex.ToString());
            }
        }





        public async Task<ResponseResult> SendImageWithCtaAsync(SendMessageDto dto)
        {
            try
            {
                Console.WriteLine($"📤 Sending image+CTA to {dto.RecipientNumber}");

                // ✅ Validate inputs
                if (string.IsNullOrWhiteSpace(dto.TextContent))
                    return ResponseResult.ErrorInfo("❌ Image message caption (TextContent) cannot be empty.");

                if (string.IsNullOrWhiteSpace(dto.Provider) ||
                    (dto.Provider != "PINNACLE" && dto.Provider != "META_CLOUD"))
                {
                    return ResponseResult.ErrorInfo("❌ Invalid provider.",
                        "Provider must be exactly 'PINNACLE' or 'META_CLOUD'.");
                }

                // ✅ CTA buttons (1–3)
                var validButtons = dto.CtaButtons?
                    .Where(b => !string.IsNullOrWhiteSpace(b.Title))
                    .Take(3)
                    .Select((btn, index) => new
                    {
                        type = "reply",
                        reply = new
                        {
                            id = $"btn_{index + 1}_{Guid.NewGuid():N}".Substring(0, 16),
                            title = btn.Title
                        }
                    })
                    .ToList();

                if (validButtons == null || validButtons.Count == 0)
                    return ResponseResult.ErrorInfo("❌ At least one CTA button with a valid title is required.");

                // 📦 Interactive payload (Meta-friendly; other providers can proxy)
                var payload = new
                {
                    messaging_product = "whatsapp",
                    to = dto.RecipientNumber,
                    type = "interactive",
                    interactive = new
                    {
                        type = "button",
                        body = new { text = dto.TextContent },
                        action = new { buttons = validButtons }
                    },
                    image = string.IsNullOrWhiteSpace(dto.MediaUrl) ? null : new { link = dto.MediaUrl }
                };

                // 🚀 Send via provider — EXPLICIT provider + optional sender override
                var sendResult = await SendViaProviderAsync(
                    dto.BusinessId,
                    dto.Provider,
                    p => p.SendInteractiveAsync(payload),
                    dto.PhoneNumberId  // null → use provider’s default sender
                );

                // 🔎 MessageId: provider id first, then Meta fallback
                string? messageId = sendResult.MessageId;
                if (string.IsNullOrWhiteSpace(messageId) && !string.IsNullOrWhiteSpace(sendResult.RawResponse))
                {
                    try
                    {
                        var raw = sendResult.RawResponse.TrimStart();
                        if (raw.StartsWith("{"))
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(raw);
                            if (doc.RootElement.TryGetProperty("messages", out var msgs) &&
                                msgs.ValueKind == System.Text.Json.JsonValueKind.Array &&
                                msgs.GetArrayLength() > 0 &&
                                msgs[0].TryGetProperty("id", out var idProp))
                            {
                                messageId = idProp.GetString();
                            }
                        }
                    }
                    catch { /* best-effort */ }
                }

                // 📝 Log (store RAW provider payload)
                var log = new MessageLog
                {
                    Id = Guid.NewGuid(),
                    BusinessId = dto.BusinessId,
                    RecipientNumber = dto.RecipientNumber,
                    MessageContent = dto.TextContent ?? "[Image with CTA]",
                    RenderedBody = dto.TextContent ?? "",
                    MediaUrl = dto.MediaUrl,
                    Status = sendResult.Success ? "Sent" : "Failed",
                    ErrorMessage = sendResult.Success ? null : sendResult.Message,
                    RawResponse = sendResult.RawResponse,
                    MessageId = messageId,
                    CreatedAt = DateTime.UtcNow,
                    SentAt = DateTime.UtcNow,
                    CTAFlowConfigId = dto.CTAFlowConfigId,
                    CTAFlowStepId = dto.CTAFlowStepId,
                };

                await _db.MessageLogs.AddAsync(log);
                await _db.SaveChangesAsync();

                return new ResponseResult
                {
                    Success = sendResult.Success,
                    Message = sendResult.Success
                        ? "✅ Image+CTA message sent."
                        : (sendResult.Message ?? "❌ WhatsApp API returned an error."),
                    Data = new
                    {
                        Success = sendResult.Success,
                        MessageId = messageId,
                        LogId = log.Id
                    },
                    RawResponse = sendResult.RawResponse,
                    MessageId = messageId,
                    LogId = log.Id
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine("❌ Exception in SendImageWithCtaAsync: " + ex.Message);

                await _db.MessageLogs.AddAsync(new MessageLog
                {
                    Id = Guid.NewGuid(),
                    BusinessId = dto.BusinessId,
                    RecipientNumber = dto.RecipientNumber,
                    MessageContent = dto.TextContent ?? "[Image CTA Failed]",
                    RenderedBody = dto.TextContent ?? "[Failed image CTA]",
                    Status = "Failed",
                    ErrorMessage = ex.Message,
                    RawResponse = ex.ToString(),
                    CreatedAt = DateTime.UtcNow,
                    CTAFlowConfigId = dto.CTAFlowConfigId,
                    CTAFlowStepId = dto.CTAFlowStepId,
                });

                await _db.SaveChangesAsync();

                return ResponseResult.ErrorInfo("❌ Failed to send image+CTA.", ex.ToString());
            }
        }


        public async Task<ResponseResult> SendImageTemplateMessageAsync(ImageTemplateMessageDto dto, Guid businessId)
        {
            try
            {
                // ✅ Validate provider (no normalization here)
                if (string.IsNullOrWhiteSpace(dto.Provider) ||
                    (dto.Provider != "PINNACLE" && dto.Provider != "META_CLOUD"))
                {
                    return ResponseResult.ErrorInfo("❌ Invalid provider.",
                        "Provider must be exactly 'PINNACLE' or 'META_CLOUD'.");
                }

                // 1) Build components
                var components = new List<object>();

                if (!string.IsNullOrWhiteSpace(dto.HeaderImageUrl))
                {
                    components.Add(new
                    {
                        type = "header",
                        parameters = new[]
                        {
                    new { type = "image", image = new { link = dto.HeaderImageUrl! } }
                }
                    });
                }

                components.Add(new
                {
                    type = "body",
                    parameters = (dto.TemplateParameters ?? new List<string>())
                        .Select(p => new { type = "text", text = p })
                        .ToArray()
                });

                // Buttons (dynamic up to 3; null-safe)
                var btns = dto.ButtonParameters ?? new List<CampaignButtonDto>();
                for (int i = 0; i < btns.Count && i < 3; i++)
                {
                    var btn = btns[i];
                    var subType = btn.ButtonType?.ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(subType)) continue;

                    var button = new Dictionary<string, object>
                    {
                        ["type"] = "button",
                        ["sub_type"] = subType,
                        ["index"] = i.ToString()
                    };

                    if (subType == "quick_reply" && !string.IsNullOrWhiteSpace(btn.TargetUrl))
                    {
                        button["parameters"] = new[] { new { type = "payload", payload = btn.TargetUrl! } };
                    }
                    else if (subType == "url" && !string.IsNullOrWhiteSpace(btn.TargetUrl))
                    {
                        button["parameters"] = new[] { new { type = "text", text = btn.TargetUrl! } };
                    }

                    components.Add(button);
                }

                var lang = string.IsNullOrWhiteSpace(dto.LanguageCode) ? "en_US" : dto.LanguageCode!;

                // 2) Send via provider — EXPLICIT provider + optional sender override
                var sendResult = await SendViaProviderAsync(
                    businessId,
                    dto.Provider,
                    p => p.SendTemplateAsync(dto.RecipientNumber, dto.TemplateName, lang, components),
                    dto.PhoneNumberId // null => provider default sender
                );

                // 3) Build rendered body
                var renderedBody = TemplateParameterHelper.FillPlaceholders(
                    dto.TemplateBody ?? "",
                    dto.TemplateParameters ?? new List<string>());

                // 4) Log raw provider payload and message id (if any)
                var log = new MessageLog
                {
                    Id = Guid.NewGuid(),
                    BusinessId = businessId,
                    RecipientNumber = dto.RecipientNumber,
                    MessageContent = dto.TemplateName,
                    MediaUrl = dto.HeaderImageUrl,
                    RenderedBody = renderedBody,
                    Status = sendResult.Success ? "Sent" : "Failed",
                    ErrorMessage = sendResult.Success ? null : sendResult.Message,
                    RawResponse = sendResult.RawResponse,     // store raw provider payload, not wrapper
                    MessageId = sendResult.MessageId,
                    CreatedAt = DateTime.UtcNow,
                    SentAt = DateTime.UtcNow,
                    CTAFlowConfigId = dto.CTAFlowConfigId,
                    CTAFlowStepId = dto.CTAFlowStepId,
                };

                await _db.MessageLogs.AddAsync(log);
                await _db.SaveChangesAsync();

                return new ResponseResult
                {
                    Success = sendResult.Success,
                    Message = sendResult.Success
                        ? "✅ Image template sent successfully."
                        : (sendResult.Message ?? "❌ WhatsApp API returned an error."),
                    Data = new { Success = sendResult.Success, MessageId = sendResult.MessageId, LogId = log.Id },
                    RawResponse = sendResult.RawResponse,
                    MessageId = sendResult.MessageId,
                    LogId = log.Id
                };
            }
            catch (Exception ex)
            {
                await _db.MessageLogs.AddAsync(new MessageLog
                {
                    Id = Guid.NewGuid(),
                    BusinessId = dto.BusinessId,
                    RecipientNumber = dto.RecipientNumber,
                    MessageContent = dto.TemplateName,
                    RenderedBody = TemplateParameterHelper.FillPlaceholders(dto.TemplateBody ?? "", dto.TemplateParameters ?? new List<string>()),
                    MediaUrl = dto.HeaderImageUrl,
                    Status = "Failed",
                    ErrorMessage = ex.Message,
                    RawResponse = ex.ToString(),
                    CreatedAt = DateTime.UtcNow,
                    CTAFlowConfigId = dto.CTAFlowConfigId,
                    CTAFlowStepId = dto.CTAFlowStepId,
                });

                await _db.SaveChangesAsync();
                return ResponseResult.ErrorInfo("❌ Error sending image template.", ex.ToString());
            }
        }

        public async Task<IEnumerable<RecentMessageLogDto>> GetLogsByBusinessIdAsync(Guid businessId)
        {
            var logs = await _db.MessageLogs
                .Where(m => m.BusinessId == businessId)
                .OrderByDescending(m => m.CreatedAt)
                .Take(1000)
                .Select(m => new RecentMessageLogDto
                {
                    Id = m.Id,
                    RecipientNumber = m.RecipientNumber,
                    MessageContent = m.MessageContent,
                    Status = m.Status,
                    CreatedAt = m.CreatedAt,
                    SentAt = m.SentAt,
                    ErrorMessage = m.ErrorMessage
                })
                .ToListAsync();

            return logs;
        }

        public Task<ResponseResult> SendDocumentTemplateMessageAsync(DocumentTemplateMessageDto dto, Guid businessId)
        {
            throw new NotImplementedException();
        }
    
    }
}




