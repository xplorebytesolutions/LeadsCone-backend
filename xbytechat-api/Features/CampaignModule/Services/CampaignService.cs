using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Serilog;
using xbytechat.api.Features.CampaignModule.DTOs;
using xbytechat.api.Features.CampaignModule.Models;
using xbytechat.api.Shared;
using xbytechat.api.Features.CampaignTracking.Models;
using xbytechat.api.Services.Messages.Interfaces;
using xbytechat.api.Features.xbTimeline.Services;
using xbytechat.api.Features.MessagesEngine.DTOs;
using xbytechat.api.Features.MessagesEngine.Services;
using xbytechat.api.CRM.Dtos;
using xbytechat.api.Helpers;
using xbytechat_api.WhatsAppSettings.Services;
using xbytechat.api.Shared.utility;
using xbytechat.api.WhatsAppSettings.DTOs;
using xbytechat.api.CRM.Models;
using xbytechat.api.Features.Tracking.Services;
using xbytechat.api.Features.CTAFlowBuilder.Models;
using Newtonsoft.Json;
using xbytechat.api.WhatsAppSettings.Services;
using xbytechat_api.Features.Billing.Services;
using System.Text.RegularExpressions;
using xbytechat.api.Common.Utils;
using xbytechat.api.Features.TemplateModule.Services;
using System.Linq;

namespace xbytechat.api.Features.CampaignModule.Services
{
    public partial class CampaignService : ICampaignService
    {
        private readonly AppDbContext _context;
        private readonly IMessageService _messageService;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILeadTimelineService _timelineService;
        private readonly IMessageEngineService _messageEngineService;
        private readonly IWhatsAppTemplateFetcherService _templateFetcherService;
        private readonly IUrlBuilderService _urlBuilderService;
        private readonly IWhatsAppSenderService _whisatsAppSenderService;
        private readonly IBillingIngestService _billingIngest;
        // private readonly Serilog.ILogger _logger = Log.ForContext<CampaignService>();

        private readonly ILogger<WhatsAppTemplateService> _logger;
        public CampaignService(AppDbContext context, IMessageService messageService,
                               IServiceProvider serviceProvider,
                               ILeadTimelineService timelineService,
                               IMessageEngineService messageEngineService,
                               IWhatsAppTemplateFetcherService templateFetcherService,
                               IUrlBuilderService urlBuilderService,
                               IWhatsAppSenderService whatsAppSenderService, IBillingIngestService billingIngest,
                               ILogger<WhatsAppTemplateService> logger
                               )
        {
            _context = context;
            _messageService = messageService;
            _serviceProvider = serviceProvider;
            _timelineService = timelineService; // ✅ new
            _messageEngineService = messageEngineService;
            _templateFetcherService = templateFetcherService;
            _urlBuilderService = urlBuilderService;
            _whisatsAppSenderService = whatsAppSenderService;
            _billingIngest = billingIngest;
            _logger = logger;

        }


        #region Get All Types of Get and Update and Delete Methods
        // Reads per-recipient variables (header/button canonical keys)
        private static string? ResolvePerRecipientValue(CampaignRecipient r, string key)
        {
            if (string.IsNullOrWhiteSpace(r.ResolvedButtonUrlsJson)) return null;
            try
            {
                var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(r.ResolvedButtonUrlsJson)
                           ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                return dict.TryGetValue(key, out var v) ? v : null;
            }
            catch { return null; }
        }

        // Returns ordered {{1}}..{{N}} values for a recipient; falls back to campaign snapshot
        private static List<string> BuildBodyParametersForRecipient(Campaign campaign, CampaignRecipient r)
        {
            // Preferred: frozen params on recipient (string[])
            if (!string.IsNullOrWhiteSpace(r.ResolvedParametersJson))
            {
                try
                {
                    var arr = JsonConvert.DeserializeObject<string[]>(r.ResolvedParametersJson);
                    if (arr != null) return arr.ToList();
                }
                catch { /* ignore */ }
            }

            // Fallback: campaign.TemplateParameters (stored as JSON array of strings)
            try
            {
                return TemplateParameterHelper.ParseTemplateParams(campaign.TemplateParameters).ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        // Builds canonical dict for header.* and buttonN.url_param with safe fallbacks
        // ✅ Works with your current Campaign model (ImageUrl only). No migration needed.
        private static Dictionary<string, string> BuildButtonParametersForRecipient(Campaign campaign, CampaignRecipient r)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 1) Recipient-specific vars (from CSV materialization)
            if (!string.IsNullOrWhiteSpace(r.ResolvedButtonUrlsJson))
            {
                try
                {
                    var d = JsonConvert.DeserializeObject<Dictionary<string, string>>(r.ResolvedButtonUrlsJson);
                    if (d != null)
                    {
                        foreach (var kv in d)
                            dict[kv.Key] = kv.Value ?? string.Empty;
                    }
                }
                catch { /* ignore */ }
            }

            // 2) Header fallbacks from campaign (only ImageUrl exists in this branch)
            if (!dict.ContainsKey("header.image_url") && !string.IsNullOrWhiteSpace(campaign.ImageUrl))
                dict["header.image_url"] = campaign.ImageUrl!;

            // NOTE:
            // We do NOT touch header.video_url/header.document_url here,
            // because Campaign.VideoUrl/DocumentUrl do not exist in this branch.

            // 3) Button URL fallbacks from campaign buttons
            if (campaign.MultiButtons != null)
            {
                foreach (var b in campaign.MultiButtons.OrderBy(b => b.Position).Take(3))
                {
                    var key = $"button{b.Position}.url_param";
                    if (!dict.ContainsKey(key) && !string.IsNullOrWhiteSpace(b.Value))
                        dict[key] = b.Value!;
                }
            }

            return dict;
        }

        public async Task<List<CampaignSummaryDto>> GetAllCampaignsAsync(Guid businessId)
        {
            return await _context.Campaigns
                .Where(c => c.BusinessId == businessId)
                .OrderByDescending(c => c.CreatedAt)
                .Select(c => new CampaignSummaryDto
                {
                    Id = c.Id,
                    Name = c.Name,
                    Status = c.Status,
                    ScheduledAt = c.ScheduledAt,
                    CreatedAt = c.CreatedAt,

                })
                .ToListAsync();
        }
        public async Task<CampaignDto?> GetCampaignByIdAsync(Guid campaignId, Guid businessId)
        {
            var campaign = await _context.Campaigns
                .Include(c => c.Cta)
                .Include(c => c.MultiButtons)
                .Include(c => c.CTAFlowConfig)
                .FirstOrDefaultAsync(c => c.Id == campaignId && c.BusinessId == businessId);

            if (campaign == null) return null;

            return new CampaignDto
            {
                Id = campaign.Id,
                Name = campaign.Name,
                MessageTemplate = campaign.MessageTemplate,
                MessageBody = campaign.MessageBody,
                TemplateId = campaign.TemplateId,
                CampaignType = campaign.CampaignType,
                Status = campaign.Status,
                ImageUrl = campaign.ImageUrl,
                ImageCaption = campaign.ImageCaption,
                CreatedAt = campaign.CreatedAt,
                ScheduledAt = campaign.ScheduledAt,
                CtaId = campaign.CtaId,
                Cta = campaign.Cta == null ? null : new CtaPreviewDto
                {
                    Title = campaign.Cta.Title,
                    ButtonText = campaign.Cta.ButtonText
                },
                MultiButtons = campaign.MultiButtons?
                    .Select(b => new CampaignButtonDto
                    {
                        ButtonText = b.Title,
                        ButtonType = b.Type,
                        TargetUrl = b.Value
                    }).ToList() ?? new List<CampaignButtonDto>(),
                // ✅ Flow surface to UI
                CTAFlowConfigId = campaign.CTAFlowConfigId,
                CTAFlowName = campaign.CTAFlowConfig?.FlowName
            };
        }
        // Returns the entry step (no incoming links) and its template name.
        // If flow is missing/invalid, returns (null, null) and caller should ignore.
        private async Task<(Guid? entryStepId, string? entryTemplate)> ResolveFlowEntryAsync(Guid businessId, Guid? flowId)
        {
            if (!flowId.HasValue || flowId.Value == Guid.Empty) return (null, null);

            var flow = await _context.CTAFlowConfigs
                .Include(f => f.Steps)
                    .ThenInclude(s => s.ButtonLinks)
                .FirstOrDefaultAsync(f => f.Id == flowId.Value && f.BusinessId == businessId && f.IsActive);

            if (flow == null || flow.Steps == null || flow.Steps.Count == 0) return (null, null);

            var incoming = new HashSet<Guid>(
                flow.Steps.SelectMany(s => s.ButtonLinks)
                          .Where(l => l.NextStepId.HasValue)
                          .Select(l => l.NextStepId!.Value)
            );

            var entry = flow.Steps
                .OrderBy(s => s.StepOrder)
                .FirstOrDefault(s => !incoming.Contains(s.Id));

            return entry == null ? (null, null) : (entry.Id, entry.TemplateToSend);
        }

        public async Task<List<CampaignSummaryDto>> GetAllCampaignsAsync(Guid businessId, string? type = null)
        {
            var query = _context.Campaigns
                .Where(c => c.BusinessId == businessId)
                .AsQueryable();

            if (!string.IsNullOrEmpty(type))
                query = query.Where(c => c.CampaignType == type);

            return await query
                .OrderByDescending(c => c.CreatedAt)
                .Select(c => new CampaignSummaryDto
                {
                    Id = c.Id,
                    Name = c.Name,
                    Status = c.Status,
                    ScheduledAt = c.ScheduledAt,
                    CreatedAt = c.CreatedAt,
                    ImageUrl = c.ImageUrl,            // ✅ Now mapped
                    ImageCaption = c.ImageCaption,    // ✅ Now mapped
                    CtaTitle = c.Cta != null ? c.Cta.Title : null,  // optional
                    RecipientCount = c.Recipients.Count()
                })
                .ToListAsync();
        }

        public async Task<List<ContactDto>> GetRecipientsByCampaignIdAsync(Guid campaignId, Guid businessId)
        {
            var recipients = await _context.CampaignRecipients
                .Include(r => r.Contact)
                .Where(r => r.CampaignId == campaignId && r.Contact.BusinessId == businessId)
                .Select(r => new ContactDto
                {
                    Id = r.Contact.Id,
                    Name = r.Contact.Name,
                    PhoneNumber = r.Contact.PhoneNumber,
                    Email = r.Contact.Email,
                    LeadSource = r.Contact.LeadSource,
                    CreatedAt = r.Contact.CreatedAt
                })
                .ToListAsync();

            return recipients;
        }

        public async Task<PaginatedResponse<CampaignSummaryDto>> GetPaginatedCampaignsAsync(Guid businessId, PaginatedRequest request)
        {
            var query = _context.Campaigns
                .Where(c => c.BusinessId == businessId)
                .OrderByDescending(c => c.CreatedAt);

            var total = await query.CountAsync();

            var items = await query
                .Skip((request.Page - 1) * request.PageSize)
                .Take(request.PageSize)
                .Select(c => new CampaignSummaryDto
                {
                    Id = c.Id,
                    Name = c.Name,
                    Status = c.Status,
                    ScheduledAt = c.ScheduledAt,
                    CreatedAt = c.CreatedAt
                })
                .ToListAsync();

            return new PaginatedResponse<CampaignSummaryDto>
            {
                Items = items,
                TotalCount = total,
                Page = request.Page,
                PageSize = request.PageSize
            };
        }
        public async Task<bool> UpdateCampaignAsync(Guid id, CampaignCreateDto dto)
        {
            var campaign = await _context.Campaigns.FindAsync(id);
            if (campaign == null || campaign.Status != "Draft")
                return false;

            // ✅ Extract BusinessId from current campaign
            var businessId = campaign.BusinessId;

            // ✅ Optional CTA ownership validation
            if (dto.CtaId.HasValue)
            {
                var cta = await _context.CTADefinitions
                    .FirstOrDefaultAsync(c => c.Id == dto.CtaId && c.BusinessId == businessId && c.IsActive);

                if (cta == null)
                    throw new UnauthorizedAccessException("❌ The selected CTA does not belong to your business or is inactive.");
            }

            // ✏️ Update campaign fields
            campaign.Name = dto.Name;
            campaign.MessageTemplate = dto.MessageTemplate;
            campaign.TemplateId = dto.TemplateId;
            campaign.FollowUpTemplateId = dto.FollowUpTemplateId;
            campaign.CampaignType = dto.CampaignType;
            campaign.CtaId = dto.CtaId;
            campaign.ImageUrl = dto.ImageUrl;
            campaign.ImageCaption = dto.ImageCaption;
            campaign.UpdatedAt = DateTime.UtcNow;
            // 🔒 Step 2.1: Refresh snapshot on update when template may have changed
            try
            {
                var effectiveTemplateName = !string.IsNullOrWhiteSpace(campaign.TemplateId)
                    ? campaign.TemplateId!
                    : (campaign.MessageTemplate ?? "");

                if (!string.IsNullOrWhiteSpace(effectiveTemplateName))
                {
                    var snapshotMeta = await _templateFetcherService.GetTemplateMetaAsync(
                        businessId,
                        effectiveTemplateName,
                        language: null,
                        provider: campaign.Provider
                    );

                    campaign.TemplateSchemaSnapshot = snapshotMeta != null
                        ? JsonConvert.SerializeObject(snapshotMeta)
                        : JsonConvert.SerializeObject(new
                        {
                            Provider = campaign.Provider ?? "",
                            TemplateName = effectiveTemplateName,
                            Language = "" // unknown if not in provider meta
                        });
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "⚠️ Template schema snapshot (update) failed for campaign {CampaignId}", id);
            }

            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<bool> DeleteCampaignAsync(Guid id)
        {
            var campaign = await _context.Campaigns
                .Include(c => c.Recipients)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (campaign == null || campaign.Status != "Draft")
                return false;

            _context.CampaignRecipients.RemoveRange(campaign.Recipients);
            _context.Campaigns.Remove(campaign);

            await _context.SaveChangesAsync();
            Log.Information("🗑️ Campaign deleted: {Id}", id);
            return true;
        }

        #endregion

        #region // 🆕 CreateCampaignAsync(Text/Image)


        //public async Task<Guid?> CreateTextCampaignAsync(CampaignCreateDto dto, Guid businessId, string createdBy)
        //{
        //    try
        //    {
        //        var campaignId = Guid.NewGuid();

        //        // 🔁 Parse/normalize template parameters once
        //        var parsedParams = TemplateParameterHelper.ParseTemplateParams(
        //            JsonConvert.SerializeObject(dto.TemplateParameters ?? new List<string>())
        //        );

        //        // 🔒 Validate + resolve sender (optional but recommended)
        //        string? providerNorm = null;
        //        if (!string.IsNullOrWhiteSpace(dto.PhoneNumberId))
        //        {
        //            // ask WhatsAppSenderService to validate ownership
        //            var pair = await _whisatsAppSenderService.ResolveSenderPairAsync(businessId, dto.PhoneNumberId);
        //            if (pair == null)
        //                throw new InvalidOperationException("❌ Selected sender is invalid or does not belong to this business.");
        //            providerNorm = pair.Value.Provider; // already normalized to UPPER
        //        }

        //        // 🔄 Flow id from UI (null/empty => no flow). We will persist this as-is.
        //        Guid? incomingFlowId = (dto.CTAFlowConfigId.HasValue && dto.CTAFlowConfigId.Value != Guid.Empty)
        //            ? dto.CTAFlowConfigId.Value
        //            : (Guid?)null;

        //        Guid? savedFlowId = incomingFlowId;

        //        // 🧩 FLOW VALIDATION (only to align the starting template)
        //        string selectedTemplateName = dto.TemplateId ?? dto.MessageTemplate ?? string.Empty;

        //        CTAFlowConfig? flow = null;
        //        CTAFlowStep? entryStep = null;

        //        if (incomingFlowId.HasValue)
        //        {
        //            flow = await _context.CTAFlowConfigs
        //                .Include(f => f.Steps).ThenInclude(s => s.ButtonLinks)
        //                .FirstOrDefaultAsync(f =>
        //                    f.Id == incomingFlowId.Value &&
        //                    f.BusinessId == businessId &&
        //                    f.IsActive);

        //            if (flow != null)
        //            {
        //                var allIncoming = new HashSet<Guid>(flow.Steps
        //                    .SelectMany(s => s.ButtonLinks)
        //                    .Where(l => l.NextStepId.HasValue)
        //                    .Select(l => l.NextStepId!.Value));

        //                entryStep = flow.Steps
        //                    .OrderBy(s => s.StepOrder)
        //                    .FirstOrDefault(s => !allIncoming.Contains(s.Id));

        //                if (entryStep != null &&
        //                    !string.Equals(selectedTemplateName, entryStep.TemplateToSend, StringComparison.OrdinalIgnoreCase))
        //                {
        //                    selectedTemplateName = entryStep.TemplateToSend;
        //                }
        //            }
        //        }

        //        var template = await _templateFetcherService.GetTemplateByNameAsync(
        //            businessId,
        //            selectedTemplateName,
        //            includeButtons: true
        //        );

        //        var templateBody = template?.Body ?? dto.MessageTemplate ?? string.Empty;
        //        var resolvedBody = TemplateParameterHelper.FillPlaceholders(templateBody, parsedParams);

        //        var campaign = new Campaign
        //        {
        //            Id = campaignId,
        //            BusinessId = businessId,
        //            Name = dto.Name,

        //            MessageTemplate = dto.MessageTemplate,
        //            TemplateId = selectedTemplateName,

        //            FollowUpTemplateId = dto.FollowUpTemplateId,
        //            CampaignType = dto.CampaignType ?? "text",
        //            CtaId = dto.CtaId,
        //            CTAFlowConfigId = savedFlowId,

        //            ScheduledAt = dto.ScheduledAt,
        //            CreatedBy = createdBy,
        //            CreatedAt = DateTime.UtcNow,
        //            UpdatedAt = DateTime.UtcNow,
        //            Status = "Draft",
        //            ImageUrl = dto.ImageUrl,
        //            ImageCaption = dto.ImageCaption,
        //            TemplateParameters = JsonConvert.SerializeObject(dto.TemplateParameters ?? new List<string>()),
        //            MessageBody = resolvedBody,

        //            // 🟢 Persist sender choice (nullable if not selected)
        //            Provider = providerNorm,
        //            PhoneNumberId = dto.PhoneNumberId
        //        };
        //        // 🔒 Step 2.1: Snapshot template schema (text path)
        //        try
        //        {
        //            var snapshotMeta = await _templateFetcherService.GetTemplateMetaAsync(
        //                businessId,
        //                selectedTemplateName,
        //                language: null,
        //                provider: providerNorm
        //            );

        //            campaign.TemplateSchemaSnapshot = snapshotMeta != null
        //                ? JsonConvert.SerializeObject(snapshotMeta)
        //                : JsonConvert.SerializeObject(new
        //                {
        //                    Provider = providerNorm ?? "",
        //                    TemplateName = selectedTemplateName,
        //                    Language = template?.Language ?? ""
        //                });
        //        }
        //        catch (Exception ex)
        //        {
        //            Log.Warning(ex, "⚠️ Template schema snapshot failed for campaign {CampaignId}", campaignId);
        //        }

        //        await _context.Campaigns.AddAsync(campaign);

        //        if (dto.ContactIds != null && dto.ContactIds.Any())
        //        {
        //            var recipients = dto.ContactIds.Select(contactId => new CampaignRecipient
        //            {
        //                Id = Guid.NewGuid(),
        //                CampaignId = campaignId,
        //                ContactId = contactId,
        //                BusinessId = businessId,
        //                Status = "Pending",
        //                SentAt = null,
        //                UpdatedAt = DateTime.UtcNow
        //            });

        //            await _context.CampaignRecipients.AddRangeAsync(recipients);
        //        }

        //        if (dto.MultiButtons != null && dto.MultiButtons.Any())
        //        {
        //            var buttons = dto.MultiButtons
        //                .Where(btn => !string.IsNullOrWhiteSpace(btn.ButtonText) && !string.IsNullOrWhiteSpace(btn.TargetUrl))
        //                .Take(3)
        //                .Select((btn, index) => new CampaignButton
        //                {
        //                    Id = Guid.NewGuid(),
        //                    CampaignId = campaignId,
        //                    Title = btn.ButtonText,
        //                    Type = btn.ButtonType ?? "url",
        //                    Value = btn.TargetUrl,
        //                    Position = index + 1,
        //                    IsFromTemplate = false
        //                });

        //            await _context.CampaignButtons.AddRangeAsync(buttons);
        //        }

        //        if (template != null && template.ButtonParams?.Count > 0)
        //        {
        //            var buttonsToSave = new List<CampaignButton>();
        //            var userButtons = dto.ButtonParams ?? new List<CampaignButtonParamFromMetaDto>();

        //            var total = Math.Min(3, template.ButtonParams.Count);
        //            for (int i = 0; i < total; i++)
        //            {
        //                var tplBtn = template.ButtonParams[i];
        //                var isDynamic = (tplBtn.ParameterValue?.Contains("{{1}}") ?? false);

        //                var userBtn = userButtons.FirstOrDefault(b => b.Position == i + 1);
        //                var valueToSave = (isDynamic && userBtn != null)
        //                    ? userBtn.Value?.Trim()
        //                    : tplBtn.ParameterValue;

        //                buttonsToSave.Add(new CampaignButton
        //                {
        //                    Id = Guid.NewGuid(),
        //                    CampaignId = campaignId,
        //                    Title = tplBtn.Text,
        //                    Type = tplBtn.Type,
        //                    Value = valueToSave,
        //                    Position = i + 1,
        //                    IsFromTemplate = true
        //                });
        //            }

        //            await _context.CampaignButtons.AddRangeAsync(buttonsToSave);
        //        }

        //        await _context.SaveChangesAsync();

        //        Log.Information("✅ Campaign '{Name}' created | FlowId: {Flow} | EntryTemplate: {Entry} | Sender: {Provider}/{PhoneId} | Recipients: {Contacts}",
        //            dto.Name,
        //            savedFlowId,
        //            entryStep?.TemplateToSend ?? selectedTemplateName,
        //            providerNorm,
        //            dto.PhoneNumberId,
        //            dto.ContactIds?.Count ?? 0);

        //        return campaignId;
        //    }
        //    catch (Exception ex)
        //    {
        //        Log.Error(ex, "❌ Failed to create campaign");
        //        return null;
        //    }
        //}

        public async Task<Guid?> CreateTextCampaignAsync(CampaignCreateDto dto, Guid businessId, string createdBy)
        {
            try
            {
                var campaignId = Guid.NewGuid();

                // 🔁 Parse/normalize template parameters once
                var parsedParams = TemplateParameterHelper.ParseTemplateParams(
                    JsonConvert.SerializeObject(dto.TemplateParameters ?? new List<string>())
                );

                // 🔒 Validate + resolve sender (optional but recommended)
                string? providerNorm = null;
                if (!string.IsNullOrWhiteSpace(dto.PhoneNumberId))
                {
                    var pair = await _whisatsAppSenderService.ResolveSenderPairAsync(businessId, dto.PhoneNumberId);
                    if (pair == null)
                        throw new InvalidOperationException("❌ Selected sender is invalid or does not belong to this business.");
                    providerNorm = pair.Value.Provider; // already normalized to UPPER
                }

                // 🔄 Flow id from UI (null/empty => no flow)
                Guid? incomingFlowId = (dto.CTAFlowConfigId.HasValue && dto.CTAFlowConfigId.Value != Guid.Empty)
                    ? dto.CTAFlowConfigId.Value
                    : (Guid?)null;

                Guid? savedFlowId = incomingFlowId;

                // 🧩 FLOW VALIDATION (only to align the starting template)
                string selectedTemplateName = dto.TemplateId ?? dto.MessageTemplate ?? string.Empty;

                CTAFlowConfig? flow = null;
                CTAFlowStep? entryStep = null;

                if (incomingFlowId.HasValue)
                {
                    flow = await _context.CTAFlowConfigs
                        .Include(f => f.Steps).ThenInclude(s => s.ButtonLinks)
                        .FirstOrDefaultAsync(f =>
                            f.Id == incomingFlowId.Value &&
                            f.BusinessId == businessId &&
                            f.IsActive);

                    if (flow != null)
                    {
                        var allIncoming = new HashSet<Guid>(flow.Steps
                            .SelectMany(s => s.ButtonLinks)
                            .Where(l => l.NextStepId.HasValue)
                            .Select(l => l.NextStepId!.Value));

                        entryStep = flow.Steps
                            .OrderBy(s => s.StepOrder)
                            .FirstOrDefault(s => !allIncoming.Contains(s.Id));

                        if (entryStep != null &&
                            !string.Equals(selectedTemplateName, entryStep.TemplateToSend, StringComparison.OrdinalIgnoreCase))
                        {
                            selectedTemplateName = entryStep.TemplateToSend;
                        }
                    }
                }

                var template = await _templateFetcherService.GetTemplateByNameAsync(
                    businessId,
                    selectedTemplateName,
                    includeButtons: true
                );

                var templateBody = template?.Body ?? dto.MessageTemplate ?? string.Empty;
                var resolvedBody = TemplateParameterHelper.FillPlaceholders(templateBody, parsedParams);

                // =========================
                // 🆕 Header kind + URL logic
                // =========================
                string headerKind = (dto.HeaderKind ?? "").Trim().ToLowerInvariant(); // "image" | "video" | "document" | "text" | "none"
                bool isMediaHeader = headerKind == "image" || headerKind == "video" || headerKind == "document";

                // Prefer new unified HeaderMediaUrl; fall back to ImageUrl for legacy image campaigns
                string? headerUrl = string.IsNullOrWhiteSpace(dto.HeaderMediaUrl)
                    ? (headerKind == "image" ? dto.ImageUrl : null)
                    : dto.HeaderMediaUrl;

                // ✅ Campaign type: headerKind ALWAYS wins (FE may still send "text")
                string finalCampaignType = isMediaHeader
                    ? headerKind                               // "image" | "video" | "document"
                    : (dto.CampaignType ?? "text").Trim().ToLowerInvariant();

                // clamp to known values
                if (finalCampaignType != "image" &&
                    finalCampaignType != "video" &&
                    finalCampaignType != "document")
                {
                    finalCampaignType = "text";
                }

                // Validate media header needs URL
                if (isMediaHeader && string.IsNullOrWhiteSpace(headerUrl))
                    throw new InvalidOperationException("❌ Header media URL is required for this template.");

                // =========================================
                // Create entity with correct media fields set
                // =========================================
                var campaign = new Campaign
                {
                    Id = campaignId,
                    BusinessId = businessId,
                    Name = dto.Name,

                    MessageTemplate = dto.MessageTemplate,
                    TemplateId = selectedTemplateName,

                    FollowUpTemplateId = dto.FollowUpTemplateId,
                    CampaignType = finalCampaignType,
                    CtaId = dto.CtaId,
                    CTAFlowConfigId = savedFlowId,

                    ScheduledAt = dto.ScheduledAt,
                    CreatedBy = createdBy,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    Status = "Draft",

                    // Media fields (set exactly one depending on header kind)
                    ImageUrl = headerKind == "image" ? headerUrl : null,
                    ImageCaption = dto.ImageCaption,
                    VideoUrl = headerKind == "video" ? headerUrl : null,
                    DocumentUrl = headerKind == "document" ? headerUrl : null,

                    TemplateParameters = JsonConvert.SerializeObject(dto.TemplateParameters ?? new List<string>()),
                    MessageBody = resolvedBody,

                    // 🟢 Persist sender choice (nullable if not selected)
                    Provider = providerNorm,
                    PhoneNumberId = dto.PhoneNumberId
                };

                // 🔒 Step 2.1: Snapshot template schema (text path)
                try
                {
                    var snapshotMeta = await _templateFetcherService.GetTemplateMetaAsync(
                        businessId,
                        selectedTemplateName,
                        language: null,
                        provider: providerNorm?.ToLowerInvariant() // normalize to match DB ("meta_cloud"/"pinnacle")
                    );

                    campaign.TemplateSchemaSnapshot = snapshotMeta != null
                        ? JsonConvert.SerializeObject(snapshotMeta)
                        : JsonConvert.SerializeObject(new
                        {
                            Provider = providerNorm ?? "",
                            TemplateName = selectedTemplateName,
                            Language = template?.Language ?? ""
                        });
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "⚠️ Template schema snapshot failed for campaign {CampaignId}", campaignId);
                }

                await _context.Campaigns.AddAsync(campaign);

                if (dto.ContactIds != null && dto.ContactIds.Any())
                {
                    var recipients = dto.ContactIds.Select(contactId => new CampaignRecipient
                    {
                        Id = Guid.NewGuid(),
                        CampaignId = campaignId,
                        ContactId = contactId,
                        BusinessId = businessId,
                        Status = "Pending",
                        SentAt = null,
                        UpdatedAt = DateTime.UtcNow
                    });

                    await _context.CampaignRecipients.AddRangeAsync(recipients);
                }

                if (dto.MultiButtons != null && dto.MultiButtons.Any())
                {
                    var buttons = dto.MultiButtons
                        .Where(btn => !string.IsNullOrWhiteSpace(btn.ButtonText) && !string.IsNullOrWhiteSpace(btn.TargetUrl))
                        .Take(3)
                        .Select((btn, index) => new CampaignButton
                        {
                            Id = Guid.NewGuid(),
                            CampaignId = campaignId,
                            Title = btn.ButtonText,
                            Type = btn.ButtonType ?? "url",
                            Value = btn.TargetUrl,
                            Position = index + 1,
                            IsFromTemplate = false
                        });

                    await _context.CampaignButtons.AddRangeAsync(buttons);
                }

                if (template != null && template.ButtonParams?.Count > 0)
                {
                    var buttonsToSave = new List<CampaignButton>();
                    var userButtons = dto.ButtonParams ?? new List<CampaignButtonParamFromMetaDto>();

                    var total = Math.Min(3, template.ButtonParams.Count);
                    for (int i = 0; i < total; i++)
                    {
                        var tplBtn = template.ButtonParams[i];
                        var isDynamic = (tplBtn.ParameterValue?.Contains("{{1}}") ?? false);

                        var userBtn = userButtons.FirstOrDefault(b => b.Position == i + 1);
                        var valueToSave = (isDynamic && userBtn != null)
                            ? userBtn.Value?.Trim()
                            : tplBtn.ParameterValue;

                        buttonsToSave.Add(new CampaignButton
                        {
                            Id = Guid.NewGuid(),
                            CampaignId = campaignId,
                            Title = tplBtn.Text,
                            Type = tplBtn.Type,
                            Value = valueToSave,
                            Position = i + 1,
                            IsFromTemplate = true
                        });
                    }

                    await _context.CampaignButtons.AddRangeAsync(buttonsToSave);
                }

                await _context.SaveChangesAsync();

                Log.Information("✅ Campaign '{Name}' created | Type:{Type} | Header:{HeaderKind} | FlowId:{Flow} | EntryTemplate:{Entry} | Sender:{Provider}/{PhoneId} | Recipients:{Contacts}",
                    dto.Name, finalCampaignType, headerKind,
                    savedFlowId,
                    entryStep?.TemplateToSend ?? selectedTemplateName,
                    providerNorm,
                    dto.PhoneNumberId,
                    dto.ContactIds?.Count ?? 0);

                return campaignId;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "❌ Failed to create campaign");
                return null;
            }
        }

        public async Task<Guid> CreateImageCampaignAsync(Guid businessId, CampaignCreateDto dto, string createdBy)
        {
            var campaignId = Guid.NewGuid();

            // 🔁 Parse/normalize template parameters once
            var parsedParams = TemplateParameterHelper.ParseTemplateParams(
                JsonConvert.SerializeObject(dto.TemplateParameters ?? new List<string>())
            );

            // 🔄 Flow id from UI (null/empty => no flow). We will persist this as-is.
            Guid? incomingFlowId = (dto.CTAFlowConfigId.HasValue && dto.CTAFlowConfigId.Value != Guid.Empty)
                ? dto.CTAFlowConfigId.Value
                : (Guid?)null;

            // We will save this value regardless of validation outcome
            Guid? savedFlowId = incomingFlowId;

            // ============================================================
            // 🧩 FLOW VALIDATION (only to align the starting template)
            // ============================================================
            string selectedTemplateName = dto.TemplateId ?? dto.MessageTemplate ?? string.Empty;

            CTAFlowConfig? flow = null;
            CTAFlowStep? entryStep = null;

            if (incomingFlowId.HasValue)
            {
                // load flow with steps+links and verify ownership
                flow = await _context.CTAFlowConfigs
                    .Include(f => f.Steps).ThenInclude(s => s.ButtonLinks)
                    .FirstOrDefaultAsync(f =>
                        f.Id == incomingFlowId.Value &&
                        f.BusinessId == businessId &&
                        f.IsActive);

                if (flow == null)
                {
                    Log.Warning("❌ Flow {FlowId} not found/active for business {Biz}. Will persist FlowId but not align template.",
                        incomingFlowId, businessId);
                }
                else
                {
                    // compute entry step: step with NO incoming links
                    var allIncoming = new HashSet<Guid>(flow.Steps
                        .SelectMany(s => s.ButtonLinks)
                        .Where(l => l.NextStepId.HasValue)
                        .Select(l => l.NextStepId!.Value));

                    entryStep = flow.Steps
                        .OrderBy(s => s.StepOrder)
                        .FirstOrDefault(s => !allIncoming.Contains(s.Id));

                    if (entryStep == null)
                    {
                        Log.Warning("❌ Flow {FlowId} has no entry step. Persisting FlowId but not aligning template.", flow.Id);
                    }
                    else if (!string.Equals(selectedTemplateName, entryStep.TemplateToSend, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Information("ℹ️ Aligning selected template '{Sel}' to flow entry '{Entry}'.",
                            selectedTemplateName, entryStep.TemplateToSend);
                        selectedTemplateName = entryStep.TemplateToSend;
                    }
                }
            }
            else
            {
                Log.Information("ℹ️ No flow attached to image campaign '{Name}'. Proceeding as plain template campaign.", dto.Name);
            }

            // 🧠 Fetch template (for body + buttons) using the aligned/selected template name
            var template = await _templateFetcherService.GetTemplateByNameAsync(
                businessId,
                selectedTemplateName,
                includeButtons: true
            );

            // 🧠 Resolve message body using template body (if available) else dto.MessageTemplate
            var templateBody = template?.Body ?? dto.MessageTemplate ?? string.Empty;
            var resolvedBody = TemplateParameterHelper.FillPlaceholders(templateBody, parsedParams);

            // 🎯 Step 1: Create campaign (CTAFlowConfigId now always = savedFlowId)
            var campaign = new Campaign
            {
                Id = campaignId,
                BusinessId = businessId,
                Name = dto.Name,

                // store the (possibly aligned) template name
                MessageTemplate = dto.MessageTemplate,      // keep original text for UI if you use it
                TemplateId = selectedTemplateName,          // ensure start template matches flow entry when available

                FollowUpTemplateId = dto.FollowUpTemplateId,
                CampaignType = "image",
                CtaId = dto.CtaId,
                CTAFlowConfigId = savedFlowId,              // 👈 persist what UI sent (or null if no flow)

                ScheduledAt = dto.ScheduledAt,
                CreatedBy = createdBy,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Status = "Draft",

                // image-specific fields
                ImageUrl = dto.ImageUrl,
                ImageCaption = dto.ImageCaption,

                // params/body snapshot (useful for previews & auditing)
                TemplateParameters = JsonConvert.SerializeObject(dto.TemplateParameters ?? new List<string>()),
                MessageBody = resolvedBody
            };
            // 🔒 Step 2.1: Snapshot template schema (image path)
            try
            {
                var snapshotMeta = await _templateFetcherService.GetTemplateMetaAsync(
                    businessId,
                    selectedTemplateName,
                    language: null,
                    provider: null
                );

                campaign.TemplateSchemaSnapshot = snapshotMeta != null
                    ? JsonConvert.SerializeObject(snapshotMeta)
                    : JsonConvert.SerializeObject(new
                    {
                        Provider = "",
                        TemplateName = selectedTemplateName,
                        Language = template?.Language ?? ""
                    });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "⚠️ Template schema snapshot failed for (image) campaign {CampaignId}", campaignId);
            }

            await _context.Campaigns.AddAsync(campaign);

            // ✅ Step 2: Assign contacts (leave SentAt null until send)
            if (dto.ContactIds != null && dto.ContactIds.Any())
            {
                var recipients = dto.ContactIds.Select(contactId => new CampaignRecipient
                {
                    Id = Guid.NewGuid(),
                    CampaignId = campaignId,
                    ContactId = contactId,
                    BusinessId = businessId,
                    Status = "Pending",
                    SentAt = null,
                    UpdatedAt = DateTime.UtcNow
                });

                await _context.CampaignRecipients.AddRangeAsync(recipients);
            }

            // ✅ Step 3a: Save manual buttons from frontend
            if (dto.MultiButtons != null && dto.MultiButtons.Any())
            {
                var buttons = dto.MultiButtons
                    .Where(btn => !string.IsNullOrWhiteSpace(btn.ButtonText) && !string.IsNullOrWhiteSpace(btn.TargetUrl))
                    .Take(3)
                    .Select((btn, index) => new CampaignButton
                    {
                        Id = Guid.NewGuid(),
                        CampaignId = campaignId,
                        Title = btn.ButtonText,
                        Type = btn.ButtonType ?? "url",
                        Value = btn.TargetUrl,
                        Position = index + 1,
                        IsFromTemplate = false
                    });

                await _context.CampaignButtons.AddRangeAsync(buttons);
            }

            // ======================== Dynamic buttons merge ========================
            // EXACTLY mirrors your text-creator pattern to avoid type issues with ButtonMetadataDto
            if (template != null && template.ButtonParams?.Count > 0)
            {
                var buttonsToSave = new List<CampaignButton>();
                var userButtons = dto.ButtonParams ?? new List<CampaignButtonParamFromMetaDto>();

                var total = Math.Min(3, template.ButtonParams.Count);
                for (int i = 0; i < total; i++)
                {
                    var tplBtn = template.ButtonParams[i];                         // ButtonMetadataDto: Text, Type, SubType, Index, ParameterValue
                    var isDynamic = (tplBtn.ParameterValue?.Contains("{{1}}") ?? false);

                    var userBtn = userButtons.FirstOrDefault(b => b.Position == i + 1);
                    var valueToSave = (isDynamic && userBtn != null)
                        ? userBtn.Value?.Trim()                                    // user override for dynamic URL
                        : tplBtn.ParameterValue;                                   // pattern or static value from meta

                    buttonsToSave.Add(new CampaignButton
                    {
                        Id = Guid.NewGuid(),
                        CampaignId = campaignId,
                        Title = tplBtn.Text,                                       // from ButtonMetadataDto
                        Type = tplBtn.Type,                                        // from ButtonMetadataDto
                        Value = valueToSave,
                        Position = i + 1,
                        IsFromTemplate = true
                    });
                }

                await _context.CampaignButtons.AddRangeAsync(buttonsToSave);
            }
            // ======================================================================

            await _context.SaveChangesAsync();

            Log.Information("✅ Image campaign '{Name}' created | FlowId: {Flow} | EntryTemplate: {Entry} | Recipients: {Contacts} | UserButtons: {ManualButtons} | TemplateButtons: {TemplateButtons} | Params: {Params}",
                dto.Name,
                savedFlowId,
                entryStep?.TemplateToSend ?? selectedTemplateName,
                dto.ContactIds?.Count ?? 0,
                dto.MultiButtons?.Count ?? 0,
                template?.ButtonParams?.Count ?? 0,
                dto.TemplateParameters?.Count ?? 0
            );

            return campaignId;
        }
        #endregion

        //public async Task<bool> SendCampaignAsync(Guid campaignId, string ipAddress, string userAgent)
        //{
        //    // 1) Load campaign (no tracking)
        //    var campaign = await _context.Campaigns
        //        .Where(c => c.Id == campaignId)
        //        .Select(c => new { c.Id, c.BusinessId, c.TemplateId, MessageTemplate = c.MessageTemplate })
        //        .AsNoTracking()
        //        .FirstOrDefaultAsync();

        //    if (campaign == null)
        //    {
        //        Log.Warning("🚫 Campaign {CampaignId} not found", campaignId);
        //        return false;
        //    }

        //    // 1.1) Resolve active WA settings → Provider + sender (optional)
        //    var wa = await _context.WhatsAppSettings
        //        .AsNoTracking()
        //        .Where(w => w.BusinessId == campaign.BusinessId && w.IsActive)
        //        .FirstOrDefaultAsync();

        //    var provider = wa?.Provider ?? "META_CLOUD";     // must be "PINNACLE" or "META_CLOUD"
        //    var phoneNumberId = wa?.PhoneNumberId;           // optional

        //    // 2) Load recipients with explicit LEFT JOINs to Contact and AudienceMember
        //    var recipients = await (
        //        from r in _context.CampaignRecipients.AsNoTracking()
        //        where r.CampaignId == campaignId

        //        // LEFT JOIN Contact
        //        join c in _context.Contacts.AsNoTracking()
        //            on r.ContactId equals c.Id into cg
        //        from c in cg.DefaultIfEmpty()

        //            // LEFT JOIN AudienceMember
        //        join am in _context.AudiencesMembers.AsNoTracking()
        //            on r.AudienceMemberId equals am.Id into amg
        //        from am in amg.DefaultIfEmpty()

        //        select new
        //        {
        //            r.Id,
        //            r.ContactId,
        //            Phone = c != null && c.PhoneNumber != null ? c.PhoneNumber : am!.PhoneE164,
        //            Name = c != null && c.Name != null ? c.Name : am!.Name,
        //            ParamsJson = r.ResolvedParametersJson
        //        })
        //        .Where(x => !string.IsNullOrWhiteSpace(x.Phone))
        //        .ToListAsync();

        //    if (recipients.Count == 0)
        //    {
        //        Log.Warning("🚫 Campaign {CampaignId} has no recipients", campaignId);
        //        return false;
        //    }

        //    // 3) Mark Sending
        //    var campaignRow = await _context.Campaigns.FirstAsync(c => c.Id == campaign.Id);
        //    campaignRow.Status = "Sending";
        //    campaignRow.UpdatedAt = DateTime.UtcNow;
        //    await _context.SaveChangesAsync();

        //    // 4) Parallel send
        //    var throttleLimit = 5;
        //    var total = recipients.Count;
        //    var sent = 0;
        //    var failed = 0;

        //    await Parallel.ForEachAsync(
        //        recipients,
        //        new ParallelOptions { MaxDegreeOfParallelism = throttleLimit },
        //        async (r, ct) =>
        //        {
        //            try
        //            {
        //                var phone = r.Phone!;
        //                var name = string.IsNullOrWhiteSpace(r.Name) ? "Customer" : r.Name;

        //                using var scope = _serviceProvider.CreateScope();
        //                var scopedDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        //                // If you froze parameters at materialization, you can parse r.ParamsJson here.
        //                var dto = new SimpleTemplateMessageDto
        //                {
        //                    Provider = provider,                 // ✅ REQUIRED by your send method
        //                    PhoneNumberId = phoneNumberId,       // optional
        //                    RecipientNumber = phone,
        //                    TemplateName = campaign.TemplateId ?? campaign.MessageTemplate,
        //                    TemplateParameters = new List<string> { name },
        //                    TemplateParameters = args,
        //                };

        //                var result = await _messageEngineService
        //                    .SendTemplateMessageSimpleAsync(campaign.BusinessId, dto);

        //                var sendLog = new CampaignSendLog
        //                {
        //                    Id = Guid.NewGuid(),
        //                    BusinessId = campaign.BusinessId,
        //                    CampaignId = campaign.Id,
        //                    ContactId = r.ContactId,            // Guid? OK to be null
        //                    RecipientId = r.Id,
        //                    TemplateId = campaign.TemplateId,
        //                    MessageBody = campaign.MessageTemplate,
        //                    MessageId = null,
        //                    SendStatus = result.Success ? "Sent" : "Failed",
        //                    ErrorMessage = result.Message,
        //                    SentAt = DateTime.UtcNow,
        //                    CreatedAt = DateTime.UtcNow,
        //                    SourceChannel = "whatsapp",
        //                    IpAddress = ipAddress,
        //                    DeviceInfo = userAgent
        //                };

        //                await scopedDb.CampaignSendLogs.AddAsync(sendLog, ct);

        //                var rec = await scopedDb.CampaignRecipients.FirstOrDefaultAsync(x => x.Id == r.Id, ct);
        //                if (rec != null)
        //                {
        //                    rec.Status = result.Success ? "Sent" : "Failed";
        //                    rec.MessagePreview = campaign.MessageTemplate;
        //                    rec.SentAt = DateTime.UtcNow;
        //                    rec.UpdatedAt = DateTime.UtcNow;
        //                }

        //                await scopedDb.SaveChangesAsync(ct);

        //                if (result.Success) Interlocked.Increment(ref sent);
        //                else Interlocked.Increment(ref failed);
        //            }
        //            catch (Exception ex)
        //            {
        //                Interlocked.Increment(ref failed);
        //                Log.Error(ex, "❌ Send failed for recipient: {RecipientId}", r.Id);
        //            }
        //        });

        //    // 5) Finalize
        //    campaignRow = await _context.Campaigns.FirstAsync(c => c.Id == campaignId);
        //    campaignRow.Status = "Sent";
        //    campaignRow.UpdatedAt = DateTime.UtcNow;
        //    await _context.SaveChangesAsync();

        //    Log.Information("📤 Campaign {CampaignId} sent via template to {Count} recipients (✅ {Sent}, ❌ {Failed})",
        //        campaignId, total, sent, failed);

        //    return sent > 0;
        //}


        //public async Task<bool> SendCampaignAsync(Guid campaignId, string ipAddress, string userAgent)
        //    {
        //        // 1) Load campaign (no tracking)
        //        var campaign = await _context.Campaigns
        //            .Where(c => c.Id == campaignId)
        //            .Select(c => new { c.Id, c.BusinessId, c.TemplateId, MessageTemplate = c.MessageTemplate })
        //            .AsNoTracking()
        //            .FirstOrDefaultAsync();

        //        if (campaign == null)
        //        {
        //            Log.Warning("🚫 Campaign {CampaignId} not found", campaignId);
        //            return false;
        //        }

        //        // 1.1) Resolve active WA settings → Provider + sender (optional)
        //        var wa = await _context.WhatsAppSettings
        //            .AsNoTracking()
        //            .Where(w => w.BusinessId == campaign.BusinessId && w.IsActive)
        //            .FirstOrDefaultAsync();

        //        var provider = wa?.Provider ?? "META_CLOUD";   // must be "PINNACLE" or "META_CLOUD"
        //        var phoneNumberId = wa?.PhoneNumberId;         // optional

        //        // 2) Load recipients with explicit LEFT JOINs to Contact and AudienceMember
        //        var recipients = await (
        //            from r in _context.CampaignRecipients.AsNoTracking()
        //            where r.CampaignId == campaignId

        //            join c in _context.Contacts.AsNoTracking()
        //                on r.ContactId equals c.Id into cg
        //            from c in cg.DefaultIfEmpty()

        //            join am in _context.AudiencesMembers.AsNoTracking()
        //                on r.AudienceMemberId equals am.Id into amg
        //            from am in amg.DefaultIfEmpty()

        //            select new
        //            {
        //                r.Id,
        //                r.ContactId,
        //                Phone = c != null && c.PhoneNumber != null ? c.PhoneNumber : am!.PhoneE164,
        //                Name = c != null && c.Name != null ? c.Name : am!.Name,
        //                ParamsJson = r.ResolvedParametersJson
        //            })
        //            .Where(x => !string.IsNullOrWhiteSpace(x.Phone))
        //            .ToListAsync();

        //        if (recipients.Count == 0)
        //        {
        //            Log.Warning("🚫 Campaign {CampaignId} has no recipients", campaignId);
        //            return false;
        //        }

        //        // 3) Mark Sending
        //        var campaignRow = await _context.Campaigns.FirstAsync(c => c.Id == campaign.Id);
        //        campaignRow.Status = "Sending";
        //        campaignRow.UpdatedAt = DateTime.UtcNow;
        //        await _context.SaveChangesAsync();

        //        // 4) Parallel send
        //        var throttleLimit = 5;
        //        var total = recipients.Count;
        //        var sent = 0;
        //        var failed = 0;

        //        await Parallel.ForEachAsync(
        //            recipients,
        //            new ParallelOptions { MaxDegreeOfParallelism = throttleLimit },
        //            async (r, ct) =>
        //            {
        //                try
        //                {
        //                    var phone = r.Phone!;
        //                    // NOTE: we intentionally do NOT inject profile name here.
        //                    // Parameters come from frozen ResolvedParametersJson (if any).
        //                    var parameters = ParseParams(r.ParamsJson);

        //                    using var scope = _serviceProvider.CreateScope();
        //                    var scopedDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        //                    var dto = new SimpleTemplateMessageDto
        //                    {
        //                        Provider = provider,                 // ✅ REQUIRED by send method
        //                        PhoneNumberId = phoneNumberId,       // optional sender override
        //                        RecipientNumber = phone,
        //                        TemplateName = campaign.TemplateId ?? campaign.MessageTemplate,
        //                        TemplateParameters = parameters      // ✅ use frozen params (or empty list)
        //                    };

        //                    var result = await _messageEngineService
        //                        .SendTemplateMessageSimpleAsync(campaign.BusinessId, dto);

        //                    var sendLog = new CampaignSendLog
        //                    {
        //                        Id = Guid.NewGuid(),
        //                        BusinessId = campaign.BusinessId,
        //                        CampaignId = campaign.Id,
        //                        ContactId = r.ContactId,            // Guid? OK to be null
        //                        RecipientId = r.Id,
        //                        TemplateId = campaign.TemplateId,
        //                        MessageBody = campaign.MessageTemplate,
        //                        MessageId = result.MessageId,       // ✅ capture WAMID
        //                        SendStatus = result.Success ? "Sent" : "Failed",
        //                        ErrorMessage = result.Message,
        //                        SentAt = DateTime.UtcNow,
        //                        CreatedAt = DateTime.UtcNow,
        //                        SourceChannel = "whatsapp",
        //                        IpAddress = ipAddress,
        //                        DeviceInfo = userAgent
        //                        // (Optional) ButtonBundleJson = SnapshotTemplateButtons(...);
        //                    };

        //                    await scopedDb.CampaignSendLogs.AddAsync(sendLog, ct);

        //                    var rec = await scopedDb.CampaignRecipients.FirstOrDefaultAsync(x => x.Id == r.Id, ct);
        //                    if (rec != null)
        //                    {
        //                        rec.Status = result.Success ? "Sent" : "Failed";
        //                        rec.MessagePreview = campaign.MessageTemplate;
        //                        rec.SentAt = DateTime.UtcNow;
        //                        rec.UpdatedAt = DateTime.UtcNow;
        //                    }

        //                    await scopedDb.SaveChangesAsync(ct);

        //                    if (result.Success) Interlocked.Increment(ref sent);
        //                    else Interlocked.Increment(ref failed);
        //                }
        //                catch (Exception ex)
        //                {
        //                    Interlocked.Increment(ref failed);
        //                    Log.Error(ex, "❌ Send failed for recipient: {RecipientId}", r.Id);
        //                }
        //            });

        //        // 5) Finalize
        //        campaignRow = await _context.Campaigns.FirstAsync(c => c.Id == campaignId);
        //        campaignRow.Status = "Sent";
        //        campaignRow.UpdatedAt = DateTime.UtcNow;
        //        await _context.SaveChangesAsync();

        //        Log.Information("📤 Campaign {CampaignId} sent via template to {Count} recipients (✅ {Sent}, ❌ {Failed})",
        //            campaignId, total, sent, failed);

        //        return sent > 0;

        //        // ---- local helpers ----
        //        static List<string> ParseParams(string? json)
        //        {
        //            if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        //            try
        //            {
        //                var arr = JsonSerializer.Deserialize<List<string>>(json);
        //                return arr ?? new List<string>();
        //            }
        //            catch
        //            {
        //                return new List<string>();
        //            }
        //        }
        //    }

        public async Task<bool> SendCampaignAsync(Guid campaignId, string ipAddress, string userAgent)
        {
            // 1) Load campaign (no tracking)
            var campaign = await _context.Campaigns
                .Where(c => c.Id == campaignId)
                .Select(c => new { c.Id, c.BusinessId, c.TemplateId, MessageTemplate = c.MessageTemplate })
                .AsNoTracking()
                .FirstOrDefaultAsync();

            if (campaign == null)
            {
                Log.Warning("🚫 Campaign {CampaignId} not found", campaignId);
                return false;
            }

            // 1.1) Resolve active WA settings → Provider + sender (optional)
            var wa = await _context.WhatsAppSettings
                .AsNoTracking()
                .Where(w => w.BusinessId == campaign.BusinessId && w.IsActive)
                .FirstOrDefaultAsync();

            var provider = wa?.Provider ?? "META_CLOUD";   // must be "PINNACLE" or "META_CLOUD"
            var phoneNumberId = wa?.PhoneNumberId;         // optional

            // 2) Load recipients with explicit LEFT JOINs to Contact and AudienceMember
            var recipients = await (
                from r in _context.CampaignRecipients.AsNoTracking()
                where r.CampaignId == campaignId

                join c in _context.Contacts.AsNoTracking()
                    on r.ContactId equals c.Id into cg
                from c in cg.DefaultIfEmpty()

                join am in _context.AudiencesMembers.AsNoTracking()
                    on r.AudienceMemberId equals am.Id into amg
                from am in amg.DefaultIfEmpty()

                select new
                {
                    r.Id,
                    r.ContactId,
                    Phone = c != null && c.PhoneNumber != null ? c.PhoneNumber : am!.PhoneE164,
                    Name = c != null && c.Name != null ? c.Name : am!.Name,
                    ParamsJson = r.ResolvedParametersJson
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Phone))
                .ToListAsync();

            if (recipients.Count == 0)
            {
                Log.Warning("🚫 Campaign {CampaignId} has no recipients", campaignId);
                return false;
            }

            // 3) Mark Sending
            var campaignRow = await _context.Campaigns.FirstAsync(c => c.Id == campaign.Id);
            campaignRow.Status = "Sending";
            campaignRow.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            // 4) Parallel send
            var throttleLimit = 5;
            var total = recipients.Count;
            var sent = 0;
            var failed = 0;

            await Parallel.ForEachAsync(
                recipients,
                new ParallelOptions { MaxDegreeOfParallelism = throttleLimit },
                async (r, ct) =>
                {
                    try
                    {
                        var phone = r.Phone!;
                        // NOTE: we intentionally do NOT inject profile name here.
                        // Parameters come from frozen ResolvedParametersJson (if any).
                        var parameters = ParseParams(r.ParamsJson);

                        using var scope = _serviceProvider.CreateScope();
                        var scopedDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                        var dto = new SimpleTemplateMessageDto
                        {
                            Provider = provider,                 // ✅ REQUIRED by send method
                            PhoneNumberId = phoneNumberId,       // optional sender override
                            RecipientNumber = phone,
                            TemplateName = campaign.TemplateId ?? campaign.MessageTemplate,
                            TemplateParameters = parameters      // ✅ use frozen params (or empty list)
                        };

                        var result = await _messageEngineService
                            .SendTemplateMessageSimpleAsync(campaign.BusinessId, dto);

                        var sendLog = new CampaignSendLog
                        {
                            Id = Guid.NewGuid(),
                            BusinessId = campaign.BusinessId,
                            CampaignId = campaign.Id,
                            ContactId = r.ContactId,            // Guid? OK to be null
                            RecipientId = r.Id,
                            TemplateId = campaign.TemplateId,
                            MessageBody = campaign.MessageTemplate,
                            MessageId = result.MessageId,       // ✅ capture WAMID
                            SendStatus = result.Success ? "Sent" : "Failed",
                            ErrorMessage = result.Message,
                            SentAt = DateTime.UtcNow,
                            CreatedAt = DateTime.UtcNow,
                            SourceChannel = "whatsapp",
                            IpAddress = ipAddress,
                            DeviceInfo = userAgent
                            // (Optional) ButtonBundleJson = SnapshotTemplateButtons(...);
                        };

                        await scopedDb.CampaignSendLogs.AddAsync(sendLog, ct);

                        var rec = await scopedDb.CampaignRecipients.FirstOrDefaultAsync(x => x.Id == r.Id, ct);
                        if (rec != null)
                        {
                            rec.Status = result.Success ? "Sent" : "Failed";
                            rec.MessagePreview = campaign.MessageTemplate;
                            rec.SentAt = DateTime.UtcNow;
                            rec.UpdatedAt = DateTime.UtcNow;
                        }

                        await scopedDb.SaveChangesAsync(ct);

                        if (result.Success) Interlocked.Increment(ref sent);
                        else Interlocked.Increment(ref failed);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        Log.Error(ex, "❌ Send failed for recipient: {RecipientId}", r.Id);
                    }
                });

            // 5) Finalize
            campaignRow = await _context.Campaigns.FirstAsync(c => c.Id == campaignId);
            campaignRow.Status = "Sent";
            campaignRow.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            Log.Information("📤 Campaign {CampaignId} sent via template to {Count} recipients (✅ {Sent}, ❌ {Failed})",
                campaignId, total, sent, failed);

            return sent > 0;

            // ---- local helpers ----
            static List<string> ParseParams(string? json)
            {
                if (string.IsNullOrWhiteSpace(json)) return new List<string>();
                try
                {
                    var arr = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
                    return arr ?? new List<string>();
                }
                catch
                {
                    return new List<string>();
                }
            }
        }



        public async Task<bool> SendCampaignInParallelAsync(Guid campaignId, string ipAddress, string userAgent)
        {
            var campaign = await _context.Campaigns
                .Include(c => c.Recipients)
                .ThenInclude(r => r.Contact)
                .FirstOrDefaultAsync(c => c.Id == campaignId);

            if (campaign == null || campaign.Recipients.Count == 0)
            {
                Log.Warning("🚫 Campaign not found or has no recipients");
                return false;
            }

            campaign.Status = "Sending";
            campaign.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            int maxParallelism = 5;

#if NET6_0_OR_GREATER
            await Parallel.ForEachAsync(campaign.Recipients, new ParallelOptions
            {
                MaxDegreeOfParallelism = maxParallelism
            },
            async (recipient, cancellationToken) =>
            {
                await SendToRecipientAsync(campaign, recipient, ipAddress, userAgent);
            });
#else
    var tasks = campaign.Recipients.Select(recipient =>
        SendToRecipientAsync(campaign, recipient, ipAddress, userAgent)
    );
    await Task.WhenAll(tasks);
#endif

            campaign.Status = "Sent";
            campaign.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            Log.Information("📤 Campaign {CampaignId} sent in parallel to {Count} recipients", campaign.Id, campaign.Recipients.Count);
            return true;
        }
        private async Task SendToRecipientAsync(Campaign campaign, CampaignRecipient recipient, string ip, string ua)
        {
            try
            {
                var dto = new SimpleTemplateMessageDto
                {
                    RecipientNumber = recipient.Contact.PhoneNumber,
                    TemplateName = campaign.MessageTemplate,
                    TemplateParameters = new List<string> { recipient.Contact.Name ?? "Customer" }
                };

                var result = await _messageEngineService.SendTemplateMessageSimpleAsync(campaign.BusinessId, dto);


                var log = new CampaignSendLog
                {
                    Id = Guid.NewGuid(),
                    CampaignId = campaign.Id,
                    ContactId = recipient.ContactId,
                    RecipientId = recipient.Id,
                    TemplateId = campaign.TemplateId,
                    MessageBody = campaign.MessageTemplate,
                    MessageId = null,
                    SendStatus = result.Success ? "Sent" : "Failed",
                    ErrorMessage = result.Message,
                    SentAt = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow,
                    SourceChannel = "whatsapp",
                    IpAddress = ip,
                    DeviceInfo = ua
                };

                lock (_context)
                {
                    _context.CampaignSendLogs.Add(log);
                    recipient.Status = result.Success ? "Sent" : "Failed";
                    recipient.MessagePreview = campaign.MessageTemplate;
                    recipient.SentAt = DateTime.UtcNow;
                    recipient.UpdatedAt = DateTime.UtcNow;
                }

                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "❌ Failed to send template to recipient: {RecipientId}", recipient.Id);
            }
        }

        public async Task<bool> RemoveRecipientAsync(Guid businessId, Guid campaignId, Guid contactId)
        {
            var entry = await _context.CampaignRecipients
                .FirstOrDefaultAsync(r =>
                    r.CampaignId == campaignId &&
                    r.ContactId == contactId &&
                    r.Campaign.BusinessId == businessId); // ✅ Filter by related Campaign.BusinessId

            if (entry == null)
                return false;

            _context.CampaignRecipients.Remove(entry);
            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<bool> AssignContactsToCampaignAsync(Guid campaignId, Guid businessId, List<Guid> contactIds)
        {
            var campaign = await _context.Campaigns
                .Include(c => c.Recipients)
                .FirstOrDefaultAsync(c => c.Id == campaignId && c.BusinessId == businessId);

            if (campaign == null)
                return false;

            var newRecipients = contactIds.Select(id => new CampaignRecipient
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                ContactId = id,
                BusinessId = businessId,
                Status = "Pending",
                SentAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });

            _context.CampaignRecipients.AddRange(newRecipients);
            await _context.SaveChangesAsync();
            return true;
        }

        // This is the Entry point to send Temaplte (Text Based and Image Based)
        public async Task<ResponseResult> SendTemplateCampaignAsync(Guid campaignId)
        {
            try
            {
                var campaign = await _context.Campaigns
                    .Include(c => c.Recipients)
                        .ThenInclude(r => r.Contact) // 🧠 include contact details
                    .Include(c => c.MultiButtons)
                    .FirstOrDefaultAsync(c => c.Id == campaignId && !c.IsDeleted);

                if (campaign == null)
                    return ResponseResult.ErrorInfo("❌ Campaign not found.");

                if (campaign.Recipients == null || !campaign.Recipients.Any())
                    return ResponseResult.ErrorInfo("❌ No recipients assigned to this campaign.");

                var templateName = campaign.MessageTemplate;
                var templateId = campaign.TemplateId;
                var language = "en_US"; // Optional: make dynamic later
                var isImageTemplate = !string.IsNullOrEmpty(campaign.ImageUrl);

                var templateParams = JsonConvert.DeserializeObject<List<string>>(campaign.TemplateParameters ?? "[]");

                int success = 0, failed = 0;

                foreach (var recipient in campaign.Recipients)
                {
                    var messageDto = new ImageTemplateMessageDto
                    {
                        // BusinessId = campaign.BusinessId,
                        RecipientNumber = recipient.Contact.PhoneNumber,
                        TemplateName = templateName,
                        LanguageCode = language,
                        HeaderImageUrl = isImageTemplate ? campaign.ImageUrl : null,
                        TemplateParameters = templateParams,
                        ButtonParameters = campaign.MultiButtons
                            .OrderBy(b => b.Position)
                            .Take(3)
                            .Select(btn => new CampaignButtonDto
                            {
                                ButtonText = btn.Title,
                                ButtonType = btn.Type,
                                TargetUrl = btn.Value
                            }).ToList()
                    };

                    // ✅ Call the image/template sender
                    var sendResult = await _messageEngineService.SendImageTemplateMessageAsync(messageDto, campaign.BusinessId);
                    var isSuccess = sendResult.ToString().ToLower().Contains("messages");

                    var log = new MessageLog
                    {
                        Id = Guid.NewGuid(),
                        BusinessId = campaign.BusinessId,
                        RecipientNumber = recipient.Contact.PhoneNumber,
                        MessageContent = templateName,
                        MediaUrl = campaign.ImageUrl,
                        Status = isSuccess ? "Sent" : "Failed",
                        ErrorMessage = isSuccess ? null : "API Failure",
                        RawResponse = JsonConvert.SerializeObject(sendResult),
                        CreatedAt = DateTime.UtcNow,
                        SentAt = DateTime.UtcNow
                    };

                    await _context.MessageLogs.AddAsync(log);

                    if (isSuccess) success++;
                    else failed++;
                }

                await _context.SaveChangesAsync();
                return ResponseResult.SuccessInfo($"✅ Sent: {success}, ❌ Failed: {failed}");
            }
            catch (Exception ex)
            {
                return ResponseResult.ErrorInfo("❌ Unexpected error during campaign send.", ex.ToString());
            }
        }

        #region  This region include all the code related to sending text and image based

        public async Task<ResponseResult> SendTemplateCampaignWithTypeDetectionAsync(Guid campaignId)
        {
            string? GetPhone(CampaignRecipient r)
                => r?.Contact?.PhoneNumber
                ?? r?.AudienceMember?.PhoneE164
                ?? r?.AudienceMember?.PhoneRaw;

            var campaign = await _context.Campaigns
                .AsNoTracking()
                .AsSplitQuery()
                .Include(c => c.Recipients).ThenInclude(r => r.Contact)
                .Include(c => c.Recipients).ThenInclude(r => r.AudienceMember)
                .Include(c => c.MultiButtons)
                .FirstOrDefaultAsync(c => c.Id == campaignId && !c.IsDeleted);

            if (campaign == null)
                return ResponseResult.ErrorInfo("❌ Campaign not found.");

            var recipients = campaign.Recipients ?? new List<CampaignRecipient>();
            var total = recipients.Count;
            var recipientsWithPhone = recipients.Where(r => !string.IsNullOrWhiteSpace(GetPhone(r))).ToList();
            if (recipientsWithPhone.Count == 0)
            {
                _logger.LogWarning("[SendDetect] No valid recipients with phone. total={Total}", total);
                return ResponseResult.ErrorInfo(
                    "⚠️ No valid recipients with phone numbers (checked Contact.PhoneNumber and AudienceMember.PhoneE164/PhoneRaw).");
            }

            // normalize incoming type
            var type = (campaign.CampaignType ?? string.Empty).Trim().ToLowerInvariant();

            // === Infer type from template meta when empty/auto ===
            if (string.IsNullOrEmpty(type) || type == "auto")
            {
                var tplName =
                    !string.IsNullOrWhiteSpace(campaign.TemplateId) ? campaign.TemplateId! :
                    !string.IsNullOrWhiteSpace(campaign.MessageTemplate) ? campaign.MessageTemplate! :
                    string.Empty;

                if (string.IsNullOrWhiteSpace(tplName))
                    return ResponseResult.ErrorInfo("❌ Campaign has no template name (TemplateId/MessageTemplate is empty).");

                // IMPORTANT: do NOT over-filter by provider here; your templates table stores "meta_cloud"/"pinnacle"
                // while Campaign.Provider is UPPER ("META_CLOUD"). Passing provider can cause a miss.
                var meta = await _templateFetcherService.GetTemplateMetaAsync(
                    campaign.BusinessId, tplName, language: null, provider: null); // 👈 provider=null on purpose

                var headerType = (meta?.HeaderType ?? string.Empty).ToUpperInvariant();
                type = headerType switch
                {
                    "IMAGE" => "image",
                    "VIDEO" => "video",
                    "DOCUMENT" => "document",
                    "PDF" => "document",
                    _ => "text"
                };

                _logger.LogInformation("[SendDetect] Inferred type. campaignId={CampaignId} template={Template} header={Header} -> type={Type}",
                    campaign.Id, tplName, headerType, type);
            }

            // === Validate required media URL for media types ===
            if (type == "image" && string.IsNullOrWhiteSpace(campaign.ImageUrl))
                return ResponseResult.ErrorInfo("🚫 Image template requires ImageUrl on the campaign.");
            if (type == "video" && string.IsNullOrWhiteSpace(campaign.VideoUrl))
                return ResponseResult.ErrorInfo("🚫 Video template requires VideoUrl on the campaign.");
            if (type == "document" && string.IsNullOrWhiteSpace(campaign.DocumentUrl))
                return ResponseResult.ErrorInfo("🚫 Document template requires DocumentUrl on the campaign.");

            // === Dispatch ===
            return type switch
            {
                "image" => await SendImageTemplateCampaignAsync(campaign),
                "video" => await SendVideoTemplateCampaignAsync(campaign),
                "document" => await SendDocumentTemplateCampaignAsync(campaign),
                "text" => await SendTextTemplateCampaignAsync(campaign),
                _ => ResponseResult.ErrorInfo($"❌ Unsupported campaign type '{campaign.CampaignType}'.")
            };
        }


        //public async Task<ResponseResult> SendTemplateCampaignWithTypeDetectionAsync(Guid campaignId)
        //{
        //    string? GetPhone(CampaignRecipient r)
        //        => r?.Contact?.PhoneNumber
        //        ?? r?.AudienceMember?.PhoneE164
        //        ?? r?.AudienceMember?.PhoneRaw;

        //    var campaign = await _context.Campaigns
        //        .AsNoTracking()
        //        .AsSplitQuery()
        //        .Include(c => c.Recipients).ThenInclude(r => r.Contact)
        //        .Include(c => c.Recipients).ThenInclude(r => r.AudienceMember)
        //        .Include(c => c.MultiButtons)
        //        .FirstOrDefaultAsync(c => c.Id == campaignId && !c.IsDeleted);

        //    if (campaign == null)
        //        return ResponseResult.ErrorInfo("❌ Campaign not found.");

        //    var recipients = campaign.Recipients ?? new List<CampaignRecipient>();
        //    var total = recipients.Count;
        //    var recipientsWithPhone = recipients
        //        .Where(r => !string.IsNullOrWhiteSpace(GetPhone(r)))
        //        .ToList();

        //    if (recipientsWithPhone.Count == 0)
        //    {
        //        _logger.LogWarning("[SendDetect] No valid recipients with phone. total={Total}", total);
        //        return ResponseResult.ErrorInfo(
        //            "⚠️ No valid recipients with phone numbers (checked Contact.PhoneNumber and AudienceMember.PhoneE164/PhoneRaw).");
        //    }

        //    var type = (campaign.CampaignType ?? string.Empty).Trim().ToLowerInvariant();

        //    if (string.IsNullOrEmpty(type) || type == "auto")
        //    {
        //        var tplName =
        //            !string.IsNullOrWhiteSpace(campaign.TemplateId) ? campaign.TemplateId! :
        //            !string.IsNullOrWhiteSpace(campaign.MessageTemplate) ? campaign.MessageTemplate! :
        //            string.Empty;

        //        if (string.IsNullOrWhiteSpace(tplName))
        //            return ResponseResult.ErrorInfo("❌ Campaign has no template name (TemplateId/MessageTemplate is empty).");

        //        var provider = (campaign.Provider ?? "META").ToUpperInvariant();

        //        var meta = await _templateFetcherService.GetTemplateMetaAsync(
        //            campaign.BusinessId, tplName, language: null, provider: provider);

        //        var headerType = (meta?.HeaderType ?? string.Empty).ToUpperInvariant();

        //        type = headerType switch
        //        {
        //            "IMAGE" => "image",
        //            "VIDEO" => "video",
        //            "DOCUMENT" => "document",
        //            "PDF" => "document",
        //            _ => "text"
        //        };

        //        _logger.LogInformation("[SendDetect] Inferred type. campaignId={CampaignId} template={Template} header={Header} -> type={Type}",
        //            campaign.Id, tplName, headerType, type);
        //    }

        //    return type switch
        //    {
        //        "image" => await SendImageTemplateCampaignAsync(campaign),
        //        "video" => await SendVideoTemplateCampaignAsync(campaign),
        //        "document" => await SendDocumentTemplateCampaignAsync(campaign),
        //        "text" => await SendTextTemplateCampaignAsync(campaign),
        //        _ => ResponseResult.ErrorInfo($"❌ Unsupported campaign type '{campaign.CampaignType}'.")
        //    };
        //}

        // Uses the per-recipient frozen params if present; otherwise falls back to campaign-level params.
        // Ensures the list length == placeholderCount (pads/truncates).
        private static List<string> GetRecipientBodyParams(
            CampaignRecipient recipient,
            int placeholderCount,
            string? campaignTemplateParameters)
        {
            // Try recipient-specific params first
            try
            {
                if (!string.IsNullOrWhiteSpace(recipient.ResolvedParametersJson))
                {
                    var fromRecipient = JsonConvert.DeserializeObject<List<string>>(recipient.ResolvedParametersJson)
                                        ?? new List<string>();
                    while (fromRecipient.Count < placeholderCount) fromRecipient.Add("");
                    if (fromRecipient.Count > placeholderCount) fromRecipient = fromRecipient.Take(placeholderCount).ToList();
                    return fromRecipient;
                }
            }
            catch { /* ignore and fall back */ }

            // Fallback: campaign-level params (old behavior), padded
            var fromCampaign = TemplateParameterHelper.ParseTemplateParams(campaignTemplateParameters).ToList();
            while (fromCampaign.Count < placeholderCount) fromCampaign.Add("");
            if (fromCampaign.Count > placeholderCount) fromCampaign = fromCampaign.Take(placeholderCount).ToList();
            return fromCampaign;
        }
        public async Task<ResponseResult> SendTextTemplateCampaignAsync(Campaign campaign)
        {
            try
            {
                if (campaign == null || campaign.IsDeleted)
                    return ResponseResult.ErrorInfo("❌ Invalid campaign.");
                if (campaign.Recipients == null || campaign.Recipients.Count == 0)
                    return ResponseResult.ErrorInfo("❌ No recipients to send.");

                var businessId = campaign.BusinessId;

                // 0) Build a concrete list of recipients that actually have a phone
                static string? ResolveRecipientPhone(CampaignRecipient r) =>
                    r?.Contact?.PhoneNumber ?? r?.AudienceMember?.PhoneE164 ?? r?.AudienceMember?.PhoneRaw;

                var recipients = campaign.Recipients
                    .Where(r => !string.IsNullOrWhiteSpace(ResolveRecipientPhone(r)))
                    .ToList();

                if (!recipients.Any())
                    return ResponseResult.ErrorInfo("⚠️ No valid recipients with phone numbers (Contact/AudienceMember).");

                // 1) Flow/template selection
                var (_, entryTemplate) = await ResolveFlowEntryAsync(businessId, campaign.CTAFlowConfigId);
                var templateName = !string.IsNullOrWhiteSpace(entryTemplate)
                    ? entryTemplate!
                    : (campaign.TemplateId ?? campaign.MessageTemplate ?? "");
                if (string.IsNullOrWhiteSpace(templateName))
                    return ResponseResult.ErrorInfo("❌ No template selected.");

                // 2) Provider template meta
                var templateMeta = await _templateFetcherService.GetTemplateByNameAsync(businessId, templateName, includeButtons: true);
                if (templateMeta == null)
                    return ResponseResult.ErrorInfo("❌ Template metadata not found.");

                var languageCode = (templateMeta.Language ?? "").Trim();
                if (string.IsNullOrWhiteSpace(languageCode))
                    return ResponseResult.ErrorInfo("❌ Template language not resolved from provider meta.");

                // 3) Buttons only (body params are built per-recipient below)
                var buttons = campaign.MultiButtons?.OrderBy(b => b.Position).ToList()
                              ?? new List<CampaignButton>();

                // 4) Resolve provider (normalize + default)
                string provider;
                if (!string.IsNullOrWhiteSpace(campaign.Provider))
                {
                    if (campaign.Provider != "PINNACLE" && campaign.Provider != "META_CLOUD")
                        return ResponseResult.ErrorInfo("❌ Invalid provider on campaign. Must be 'PINNACLE' or 'META_CLOUD'.");
                    provider = campaign.Provider;
                }
                else
                {
                    var settings = await _context.WhatsAppSettings.AsNoTracking()
                        .Where(s => s.BusinessId == businessId && s.IsActive)
                        .OrderByDescending(s => s.PhoneNumberId != null)
                        .ThenByDescending(s => s.UpdatedAt ?? s.CreatedAt)
                        .ToListAsync();

                    if (settings.Count == 0)
                        return ResponseResult.ErrorInfo("❌ WhatsApp settings not found.");
                    if (settings.Count > 1 && settings[0].PhoneNumberId == null)
                        return ResponseResult.ErrorInfo("❌ Multiple providers are active but no default sender is set.");

                    provider = settings[0].Provider;
                    if (provider != "PINNACLE" && provider != "META_CLOUD")
                        return ResponseResult.ErrorInfo($"❌ Unsupported provider configured: {provider}");
                }

                // Sender override; if missing, try to pull from active settings for this provider
                string? phoneNumberIdOverride = campaign.PhoneNumberId;
                if (string.IsNullOrWhiteSpace(phoneNumberIdOverride))
                {
                    phoneNumberIdOverride = await _context.WhatsAppSettings.AsNoTracking()
                        .Where(s => s.BusinessId == businessId && s.IsActive && s.Provider == provider && s.PhoneNumberId != null)
                        .OrderByDescending(s => s.UpdatedAt ?? s.CreatedAt)
                        .Select(s => s.PhoneNumberId)
                        .FirstOrDefaultAsync();
                }

                // 5) Optional flow entry step id
                Guid? entryStepId = null;
                if (campaign.CTAFlowConfigId.HasValue)
                {
                    entryStepId = await _context.CTAFlowSteps
                        .Where(s => s.CTAFlowConfigId == campaign.CTAFlowConfigId.Value)
                        .OrderBy(s => s.StepOrder)
                        .Select(s => (Guid?)s.Id)
                        .FirstOrDefaultAsync();
                }

                // 6) Freeze button bundle for click-tracking
                string? buttonBundleJson = null;
                if (templateMeta.ButtonParams is { Count: > 0 })
                {
                    var bundle = templateMeta.ButtonParams.Take(3)
                        .Select((b, i) => new { i, position = i + 1, text = (b.Text ?? "").Trim(), type = b.Type, subType = b.SubType })
                        .ToList();
                    buttonBundleJson = JsonConvert.SerializeObject(bundle);
                }

                // 7) Preload AudienceMember phone/name for recipients that don’t have a Contact
                var neededMemberIds = recipients
                    .Where(x => x.ContactId == null && x.AudienceMemberId != null)
                    .Select(x => x.AudienceMemberId!.Value)
                    .Distinct()
                    .ToList();

                var audienceLookup = neededMemberIds.Count == 0
                    ? new Dictionary<Guid, (string Phone, string? Name)>()
                    : await _context.AudiencesMembers.AsNoTracking()
                        .Where(m => m.BusinessId == businessId && neededMemberIds.Contains(m.Id))
                        .Select(m => new { m.Id, m.PhoneE164, m.PhoneRaw, m.Name })
                        .ToDictionaryAsync(
                            x => x.Id,
                            x => (Phone: string.IsNullOrWhiteSpace(x.PhoneE164) ? (x.PhoneRaw ?? "") : x.PhoneE164,
                                  Name: x.Name)
                        );

                int successCount = 0, failureCount = 0;
                var now = DateTime.UtcNow;

                foreach (var r in recipients)
                {
                    // Resolve actual phone + fallback name
                    var phone = ResolveRecipientPhone(r);
                    string? name = r.Contact?.Name;

                    if (string.IsNullOrWhiteSpace(phone) && r.AudienceMemberId != null &&
                        audienceLookup.TryGetValue(r.AudienceMemberId.Value, out var a) &&
                        !string.IsNullOrWhiteSpace(a.Phone))
                    {
                        phone = a.Phone;
                        name ??= a.Name ?? "Customer";
                    }

                    if (string.IsNullOrWhiteSpace(phone))
                    {
                        failureCount++;
                        continue; // nothing to send to
                    }

                    var contactForTemplating = r.Contact ?? new Contact
                    {
                        Id = Guid.Empty,
                        BusinessId = businessId,
                        PhoneNumber = phone,
                        Name = name ?? "Customer"
                    };

                    var runId = Guid.NewGuid();
                    var campaignSendLogId = Guid.NewGuid();

                    // ✅ Build BODY params per recipient (never clobber with campaign-level when CSV/recipient values exist)
                    var resolvedParams = GetRecipientBodyParams(r, templateMeta.PlaceholderCount, campaign.TemplateParameters);

                    // If template expects body placeholders, prevent a Meta 131008 by refusing to send when any required value is blank
                    if (templateMeta.PlaceholderCount > 0 && resolvedParams.Any(string.IsNullOrWhiteSpace))
                    {
                        failureCount++;
                        var why = $"Missing body parameter(s): expected {templateMeta.PlaceholderCount}, got " +
                                  $"{resolvedParams.Count(x => !string.IsNullOrWhiteSpace(x))} filled.";
                        if (_context.Entry(r).State == EntityState.Detached) _context.Attach(r);
                        r.MaterializedAt = now;
                        r.UpdatedAt = now;
                        r.ResolvedParametersJson = JsonConvert.SerializeObject(resolvedParams);

                        // Log locally as a failed send without calling provider
                        var logIdLocal = Guid.NewGuid();
                        _context.MessageLogs.Add(new MessageLog
                        {
                            Id = logIdLocal,
                            BusinessId = businessId,
                            CampaignId = campaign.Id,
                            ContactId = r.ContactId, // may be null
                            RecipientNumber = phone,
                            MessageContent = templateName,
                            Status = "Failed",
                            ErrorMessage = why,
                            RawResponse = "{\"local_error\":\"missing_template_body_params\"}",
                            CreatedAt = now,
                            Source = "campaign",
                            CTAFlowConfigId = campaign.CTAFlowConfigId,
                            CTAFlowStepId = entryStepId,
                            ButtonBundleJson = buttonBundleJson,
                            RunId = runId
                        });

                        await _context.CampaignSendLogs.AddAsync(new CampaignSendLog
                        {
                            Id = campaignSendLogId,
                            CampaignId = campaign.Id,
                            BusinessId = businessId,
                            ContactId = r.ContactId,  // may be null
                            RecipientId = r.Id,
                            MessageBody = campaign.MessageBody ?? templateName,
                            TemplateId = templateName,
                            SendStatus = "Failed",
                            MessageLogId = logIdLocal,
                            ErrorMessage = why,
                            CreatedAt = now,
                            CreatedBy = campaign.CreatedBy,
                            CTAFlowConfigId = campaign.CTAFlowConfigId,
                            CTAFlowStepId = entryStepId,
                            ButtonBundleJson = buttonBundleJson,
                            RunId = runId
                        });

                        continue; // skip provider call
                    }

                    // ✅ Build components using the per-recipient params
                    List<string> resolvedButtonUrls;
                    object components = provider == "PINNACLE"
                        ? BuildTextTemplateComponents_Pinnacle(resolvedParams, buttons, templateMeta, campaignSendLogId, contactForTemplating, out resolvedButtonUrls)
                        : BuildTextTemplateComponents_Meta(resolvedParams, buttons, templateMeta, campaignSendLogId, contactForTemplating, out resolvedButtonUrls);

                    var payload = new
                    {
                        messaging_product = "whatsapp",
                        to = phone,
                        type = "template",
                        template = new
                        {
                            name = templateName,
                            language = new { code = languageCode },
                            components
                        }
                    };

                    // Freeze recipient materialization BEFORE send (ensure entity is tracked)
                    if (_context.Entry(r).State == EntityState.Detached)
                        _context.Attach(r);

                    r.ResolvedParametersJson = JsonConvert.SerializeObject(resolvedParams); // ✅ save what we actually sent
                    r.ResolvedButtonUrlsJson = JsonConvert.SerializeObject(resolvedButtonUrls);
                    r.MaterializedAt = now;
                    r.UpdatedAt = now;
                    // deterministic idempotency fingerprint
                    r.IdempotencyKey = Idempotency.Sha256($"{campaign.Id}|{phone}|{templateName}|{r.ResolvedParametersJson}|{r.ResolvedButtonUrlsJson}");

                    var result = await _messageEngineService.SendPayloadAsync(businessId, provider, payload, phoneNumberIdOverride);

                    var logId = Guid.NewGuid();
                    _context.MessageLogs.Add(new MessageLog
                    {
                        Id = logId,
                        BusinessId = businessId,
                        CampaignId = campaign.Id,
                        ContactId = r.ContactId, // may be null
                        RecipientNumber = phone,
                        MessageContent = templateName,
                        Status = result.Success ? "Sent" : "Failed",
                        MessageId = result.MessageId,
                        ErrorMessage = result.ErrorMessage,
                        RawResponse = result.RawResponse,
                        CreatedAt = now,
                        SentAt = result.Success ? now : (DateTime?)null,
                        Source = "campaign",
                        CTAFlowConfigId = campaign.CTAFlowConfigId,
                        CTAFlowStepId = entryStepId,
                        ButtonBundleJson = buttonBundleJson,
                        RunId = runId
                    });

                    await _billingIngest.IngestFromSendResponseAsync(
                        businessId: businessId,
                        messageLogId: logId,
                        provider: provider,
                        rawResponseJson: result.RawResponse ?? "{}"
                    );

                    await _context.CampaignSendLogs.AddAsync(new CampaignSendLog
                    {
                        Id = campaignSendLogId,
                        CampaignId = campaign.Id,
                        BusinessId = businessId,
                        ContactId = r.ContactId,  // may be null
                        RecipientId = r.Id,
                        MessageBody = campaign.MessageBody ?? templateName,
                        TemplateId = templateName,
                        SendStatus = result.Success ? "Sent" : "Failed",
                        MessageLogId = logId,
                        MessageId = result.MessageId,
                        ErrorMessage = result.ErrorMessage,
                        CreatedAt = now,
                        SentAt = result.Success ? now : (DateTime?)null,
                        CreatedBy = campaign.CreatedBy,
                        CTAFlowConfigId = campaign.CTAFlowConfigId,
                        CTAFlowStepId = entryStepId,
                        ButtonBundleJson = buttonBundleJson,
                        RunId = runId
                    });

                    if (result.Success) successCount++; else failureCount++;
                }

                await _context.SaveChangesAsync();
                return ResponseResult.SuccessInfo($"📤 Sent to {successCount} recipients. ❌ Failed for {failureCount}.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error while sending text template campaign");
                return ResponseResult.ErrorInfo("🚨 Unexpected error while sending campaign.", ex.ToString());
            }
        }


        private static bool IsHttpsMp4Url(string? url, out string? normalizedError)
        {
            normalizedError = null;
            if (string.IsNullOrWhiteSpace(url))
            {
                normalizedError = "Missing VideoUrl.";
                return false;
            }

            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            {
                normalizedError = "VideoUrl is not a valid absolute URL.";
                return false;
            }

            if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                normalizedError = "VideoUrl must use HTTPS.";
                return false;
            }

            var ext = Path.GetExtension(uri.AbsolutePath);
            if (!ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                normalizedError = "VideoUrl must point to an .mp4 file.";
                return false;
            }

            return true;
        }



        public async Task<ResponseResult> SendVideoTemplateCampaignAsync(Campaign campaign)
        {
            try
            {
                if (campaign == null || campaign.IsDeleted)
                    return ResponseResult.ErrorInfo("❌ Invalid campaign.");
                if (campaign.Recipients == null || campaign.Recipients.Count == 0)
                    return ResponseResult.ErrorInfo("❌ No recipients to send.");

                var businessId = campaign.BusinessId;

                static string? PhoneOf(CampaignRecipient r) =>
                    r?.Contact?.PhoneNumber ?? r?.AudienceMember?.PhoneE164 ?? r?.AudienceMember?.PhoneRaw;

                var recipients = campaign.Recipients.Where(r => !string.IsNullOrWhiteSpace(PhoneOf(r))).ToList();
                if (recipients.Count == 0)
                    return ResponseResult.ErrorInfo("⚠️ No valid recipients with phone numbers.");

                // Flow/template selection
                var (_, entryTemplate) = await ResolveFlowEntryAsync(businessId, campaign.CTAFlowConfigId);
                var templateName = !string.IsNullOrWhiteSpace(entryTemplate)
                    ? entryTemplate!
                    : (campaign.TemplateId ?? campaign.MessageTemplate ?? "");
                if (string.IsNullOrWhiteSpace(templateName))
                    return ResponseResult.ErrorInfo("❌ No template selected.");

                // Validate header media URL (direct https mp4)
                var videoUrl = (campaign.VideoUrl ?? campaign.ImageUrl ?? "").Trim();
                if (!IsHttpsMp4Url(videoUrl, out var vErr))
                    return ResponseResult.ErrorInfo("🚫 Invalid VideoUrl", vErr);

                // Template meta
                var templateMeta = await _templateFetcherService.GetTemplateByNameAsync(businessId, templateName, includeButtons: true);
                if (templateMeta == null)
                    return ResponseResult.ErrorInfo("❌ Template metadata not found.");
                var languageCode = (templateMeta.Language ?? "").Trim();
                if (string.IsNullOrWhiteSpace(languageCode))
                    return ResponseResult.ErrorInfo("❌ Template language not resolved from provider meta.");

                // Resolve provider
                string provider;
                if (!string.IsNullOrWhiteSpace(campaign.Provider))
                {
                    if (campaign.Provider != "PINNACLE" && campaign.Provider != "META_CLOUD")
                        return ResponseResult.ErrorInfo("❌ Invalid provider on campaign. Must be 'PINNACLE' or 'META_CLOUD'.");
                    provider = campaign.Provider;
                }
                else
                {
                    var settings = await _context.WhatsAppSettings.AsNoTracking()
                        .Where(s => s.BusinessId == businessId && s.IsActive)
                        .OrderByDescending(s => s.PhoneNumberId != null)
                        .ThenByDescending(s => s.UpdatedAt ?? s.CreatedAt)
                        .ToListAsync();

                    if (settings.Count == 0)
                        return ResponseResult.ErrorInfo("❌ WhatsApp settings not found.");
                    if (settings.Count > 1 && settings[0].PhoneNumberId == null)
                        return ResponseResult.ErrorInfo("❌ Multiple providers are active but no default sender is set.");

                    provider = settings[0].Provider;
                    if (provider != "PINNACLE" && provider != "META_CLOUD")
                        return ResponseResult.ErrorInfo($"❌ Unsupported provider configured: {provider}");
                }

                // Sender override
                string? phoneNumberIdOverride = campaign.PhoneNumberId;
                if (string.IsNullOrWhiteSpace(phoneNumberIdOverride))
                {
                    phoneNumberIdOverride = await _context.WhatsAppSettings.AsNoTracking()
                        .Where(s => s.BusinessId == businessId && s.IsActive && s.Provider == provider && s.PhoneNumberId != null)
                        .OrderByDescending(s => s.UpdatedAt ?? s.CreatedAt)
                        .Select(s => s.PhoneNumberId)
                        .FirstOrDefaultAsync();
                }

                // Optional flow entry step id
                Guid? entryStepId = null;
                if (campaign.CTAFlowConfigId.HasValue)
                {
                    entryStepId = await _context.CTAFlowSteps
                        .Where(s => s.CTAFlowConfigId == campaign.CTAFlowConfigId.Value)
                        .OrderBy(s => s.StepOrder)
                        .Select(s => (Guid?)s.Id)
                        .FirstOrDefaultAsync();
                }

                // Freeze button bundle for UI click tracking
                string? buttonBundleJson = null;
                if (templateMeta.ButtonParams is { Count: > 0 })
                {
                    var bundle = templateMeta.ButtonParams.Take(3)
                        .Select((b, i) => new { i, position = i + 1, text = (b.Text ?? "").Trim(), type = b.Type, subType = b.SubType })
                        .ToList();
                    buttonBundleJson = JsonConvert.SerializeObject(bundle);
                }

                // Audience lookup for missing contacts
                var neededMemberIds = recipients
                    .Where(x => x.ContactId == null && x.AudienceMemberId != null)
                    .Select(x => x.AudienceMemberId!.Value)
                    .Distinct()
                    .ToList();

                var audienceLookup = neededMemberIds.Count == 0
                    ? new Dictionary<Guid, (string Phone, string? Name)>()
                    : await _context.AudiencesMembers.AsNoTracking()
                        .Where(m => m.BusinessId == businessId && neededMemberIds.Contains(m.Id))
                        .Select(m => new { m.Id, m.PhoneE164, m.PhoneRaw, m.Name })
                        .ToDictionaryAsync(
                            x => x.Id,
                            x => (Phone: string.IsNullOrWhiteSpace(x.PhoneE164) ? (x.PhoneRaw ?? "") : x.PhoneE164, Name: x.Name)
                        );

                int successCount = 0, failureCount = 0;
                var now = DateTime.UtcNow;

                foreach (var r in recipients)
                {
                    var phone = PhoneOf(r);
                    string? name = r.Contact?.Name;

                    if (string.IsNullOrWhiteSpace(phone) && r.AudienceMemberId != null &&
                        audienceLookup.TryGetValue(r.AudienceMemberId.Value, out var a) &&
                        !string.IsNullOrWhiteSpace(a.Phone))
                    {
                        phone = a.Phone;
                        name ??= a.Name ?? "Customer";
                    }
                    if (string.IsNullOrWhiteSpace(phone))
                    {
                        failureCount++;
                        continue;
                    }

                    var contactForTemplating = r.Contact ?? new Contact
                    {
                        Id = Guid.Empty,
                        BusinessId = businessId,
                        PhoneNumber = phone,
                        Name = name ?? "Customer"
                    };

                    var runId = Guid.NewGuid();
                    var campaignSendLogId = Guid.NewGuid();

                    // ✅ Provider-specific component builder (uses recipient-resolved shapes)
                    bool built;
                    List<object> components;
                    string? buildErr;

                    if (provider == "META_CLOUD")
                        built = TryBuildVideoTemplateComponents_Meta(videoUrl, templateMeta, r, out components, out buildErr);
                    else
                        built = TryBuildVideoTemplateComponents_Pinnacle(videoUrl, templateMeta, r, out components, out buildErr);

                    if (!built)
                    {
                        failureCount++;
                        _logger.LogWarning("[VideoTpl] Component build failed campaign={CampaignId} phone={Phone}: {Err}",
                            campaign.Id, phone, buildErr);
                        _context.CampaignSendLogs.Add(new CampaignSendLog
                        {
                            Id = campaignSendLogId,
                            CampaignId = campaign.Id,
                            BusinessId = businessId,
                            ContactId = r.ContactId,
                            RecipientId = r.Id,
                            MessageBody = campaign.MessageBody ?? templateName,
                            TemplateId = templateName,
                            SendStatus = "Failed",
                            ErrorMessage = $"component-build: {buildErr}",
                            CreatedAt = now,
                            CreatedBy = campaign.CreatedBy,
                            CTAFlowConfigId = campaign.CTAFlowConfigId,
                            CTAFlowStepId = entryStepId,
                            ButtonBundleJson = buttonBundleJson,
                            RunId = runId,
                            SourceChannel = "video_template"
                        });
                        continue;
                    }

                    var payload = new
                    {
                        messaging_product = "whatsapp",
                        to = phone,
                        type = "template",
                        template = new
                        {
                            name = templateName,
                            language = new { code = languageCode },
                            components
                        }
                    };

                    // Snapshot (keep truthful; materializer should already have set these)
                    if (_context.Entry(r).State == EntityState.Detached)
                        _context.Attach(r);
                    r.MaterializedAt = r.MaterializedAt ?? now;
                    r.UpdatedAt = now;
                    r.IdempotencyKey = Idempotency.Sha256(
                        $"{campaign.Id}|{phone}|{templateName}|{videoUrl}|{r.ResolvedParametersJson}|{r.ResolvedButtonUrlsJson}");

                    var result = await _messageEngineService.SendPayloadAsync(businessId, provider, payload, phoneNumberIdOverride);

                    var logId = Guid.NewGuid();
                    _context.MessageLogs.Add(new MessageLog
                    {
                        Id = logId,
                        BusinessId = businessId,
                        CampaignId = campaign.Id,
                        ContactId = r.ContactId,
                        RecipientNumber = phone,
                        MessageContent = templateName,
                        MediaUrl = videoUrl,
                        Status = result.Success ? "Sent" : "Failed",
                        MessageId = result.MessageId,
                        ErrorMessage = result.ErrorMessage,
                        RawResponse = result.RawResponse,
                        CreatedAt = now,
                        SentAt = result.Success ? now : (DateTime?)null,
                        Source = "campaign",
                        CTAFlowConfigId = campaign.CTAFlowConfigId,
                        CTAFlowStepId = entryStepId,
                        ButtonBundleJson = buttonBundleJson,
                        RunId = runId,
                        Provider = provider,
                        ProviderMessageId = result.MessageId
                    });

                    await _billingIngest.IngestFromSendResponseAsync(
                        businessId: businessId,
                        messageLogId: logId,
                        provider: provider,
                        rawResponseJson: result.RawResponse ?? "{}"
                    );

                    await _context.CampaignSendLogs.AddAsync(new CampaignSendLog
                    {
                        Id = campaignSendLogId,
                        CampaignId = campaign.Id,
                        BusinessId = businessId,
                        ContactId = r.ContactId,
                        RecipientId = r.Id,
                        MessageBody = campaign.MessageBody ?? templateName,
                        TemplateId = templateName,
                        SendStatus = result.Success ? "Sent" : "Failed",
                        MessageLogId = logId,
                        MessageId = result.MessageId,
                        ErrorMessage = result.ErrorMessage,
                        CreatedAt = now,
                        SentAt = result.Success ? now : (DateTime?)null,
                        CreatedBy = campaign.CreatedBy,
                        CTAFlowConfigId = campaign.CTAFlowConfigId,
                        CTAFlowStepId = entryStepId,
                        ButtonBundleJson = buttonBundleJson,
                        RunId = runId,
                        SourceChannel = "video_template"
                    });

                    if (result.Success) successCount++; else failureCount++;
                }

                await _context.SaveChangesAsync();
                return ResponseResult.SuccessInfo($"🎬 Video template sent to {successCount} recipients. ❌ Failed for {failureCount}.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error while sending video template campaign");
                return ResponseResult.ErrorInfo("🚨 Unexpected error while sending video campaign.", ex.ToString());
            }
        }

        private List<object> BuildVideoTemplateComponents(
            string provider,
            string headerVideoUrl,
            List<string> templateParams,
            List<CampaignButton>? buttonList,
            TemplateMetadataDto templateMeta,
            Guid campaignSendLogId,
            Contact contact,
            out List<string> resolvedButtonUrls)
        {
            // Reuse your current builders to get BODY + BUTTONS
            List<object> nonHeaderComponents;
            if (string.Equals(provider, "PINNACLE", StringComparison.Ordinal))
                nonHeaderComponents = BuildTextTemplateComponents_Pinnacle(
                    templateParams, buttonList, templateMeta, campaignSendLogId, contact, out resolvedButtonUrls);
            else // META_CLOUD
                nonHeaderComponents = BuildTextTemplateComponents_Meta(
                    templateParams, buttonList, templateMeta, campaignSendLogId, contact, out resolvedButtonUrls);

            // Prepend the HEADER/VIDEO piece (WhatsApp shape for both providers)
            var components = new List<object>
    {
        new
        {
            type = "header",
            parameters = new object[] {
                new { type = "video", video = new { link = headerVideoUrl } }
            }
        }
    };

            components.AddRange(nonHeaderComponents);
            return components;
        }
        private bool TryBuildVideoTemplateComponents_Meta(
    string videoUrl,
    TemplateMetadataDto templateMeta,
    CampaignRecipient r,
    out List<object> components,
    out string? error)
        {
            components = new List<object>();
            error = null;

            if (string.IsNullOrWhiteSpace(videoUrl))
            {
                error = "required header VIDEO url is missing";
                return false;
            }

            // HEADER (video)
            components.Add(new Dictionary<string, object>
            {
                ["type"] = "header",
                ["parameters"] = new object[]
                {
            new Dictionary<string, object>
            {
                ["type"] = "video",
                ["video"] = new Dictionary<string, object>
                {
                    ["link"] = videoUrl
                }
            }
                }
            });

            // BODY {{1..N}}
            var count = Math.Max(0, templateMeta.PlaceholderCount);
            var bodyParams = DeserializeBodyParams(r.ResolvedParametersJson, count);
            if (count > 0)
            {
                // If template expects text params, enforce presence
                var missing = MissingIndices(bodyParams, count);
                if (missing.Count > 0)
                {
                    error = $"missing body params at {{ {string.Join(",", missing)} }}";
                    return false;
                }

                components.Add(new
                {
                    type = "body",
                    parameters = bodyParams.Select(p => new Dictionary<string, object>
                    {
                        ["type"] = "text",
                        ["text"] = p ?? string.Empty
                    }).ToList()
                });
            }

            // URL BUTTON parameters (only when template declares dynamic pieces)
            if (templateMeta.ButtonParams != null && templateMeta.ButtonParams.Count > 0)
            {
                var urlDict = DeserializeButtonDict(r.ResolvedButtonUrlsJson);
                var total = Math.Min(3, templateMeta.ButtonParams.Count);

                for (int i = 0; i < total; i++)
                {
                    var bp = templateMeta.ButtonParams[i];
                    var subType = (bp.SubType ?? "url").ToLowerInvariant();
                    var paramMask = bp.ParameterValue?.Trim();

                    // Only dynamic URL buttons need a "text" parameter
                    if (!string.Equals(subType, "url", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var isDynamic = !string.IsNullOrWhiteSpace(paramMask) && paramMask.Contains("{{");
                    if (!isDynamic) continue;

                    // materializer persisted: button{1..3}.url_param
                    var key = $"button{i + 1}.url_param";
                    if (!urlDict.TryGetValue(key, out var dyn) || string.IsNullOrWhiteSpace(dyn))
                    {
                        error = $"missing dynamic URL param for {key}";
                        return false;
                    }

                    components.Add(new Dictionary<string, object>
                    {
                        ["type"] = "button",
                        ["sub_type"] = "url",
                        ["index"] = i.ToString(), // "0","1","2"
                        ["parameters"] = new object[]
                        {
                    new Dictionary<string, object> { ["type"] = "text", ["text"] = dyn }
                        }
                    });
                }
            }

            return true;
        }

        private bool TryBuildVideoTemplateComponents_Pinnacle(
    string videoUrl,
    TemplateMetadataDto templateMeta,
    CampaignRecipient r,
    out List<object> components,
    out string? error)
        {
            // If Pinnacle uses same structure as Meta for templates, we can reuse Meta logic.
            // If they require a different header/media envelope, adapt here.
            return TryBuildVideoTemplateComponents_Meta(videoUrl, templateMeta, r, out components, out error);
        }
        private static Dictionary<string, string> DeserializeButtonDict(string? json)
        {
            try
            {
                return string.IsNullOrWhiteSpace(json)
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : JsonConvert.DeserializeObject<Dictionary<string, string>>(json!)
                      ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
        private static List<int> MissingIndices(List<string> bodyParams, int count)
        {
            var miss = new List<int>();
            for (int i = 0; i < count; i++)
            {
                if (string.IsNullOrWhiteSpace(i < bodyParams.Count ? bodyParams[i] : null))
                    miss.Add(i + 1); // 1-based for readability
            }
            return miss;
        }
        // ---------- helpers ----------
        private static List<string> DeserializeBodyParams(string? json, int expectedCount)
        {
            try
            {
                var arr = string.IsNullOrWhiteSpace(json)
                    ? Array.Empty<string>()
                    : JsonConvert.DeserializeObject<string[]>(json!) ?? Array.Empty<string>();

                // pad/trim to template placeholder count
                var list = new List<string>(Enumerable.Repeat(string.Empty, Math.Max(expectedCount, 0)));
                for (int i = 0; i < Math.Min(expectedCount, arr.Length); i++)
                    list[i] = arr[i] ?? string.Empty;
                return list;
            }
            catch
            {
                return new List<string>(Enumerable.Repeat(string.Empty, Math.Max(expectedCount, 0)));
            }
        }
        private static readonly Regex PlaceholderRe = new(@"\{\{\s*(\d+)\s*\}\}", RegexOptions.Compiled);

        private string BuildTokenParam(Guid campaignSendLogId, int buttonIndex, string? buttonTitle, string destinationUrlAbsolute)
        {
            var full = _urlBuilderService.BuildTrackedButtonUrl(campaignSendLogId, buttonIndex, buttonTitle, destinationUrlAbsolute);
            var pos = full.LastIndexOf("/r/", StringComparison.OrdinalIgnoreCase);
            return (pos >= 0) ? full[(pos + 3)..] : full; // fallback: if not found, return full (rare)
        }

        private static string NormalizeAbsoluteUrlOrThrowForButton(string input, string buttonTitle, int buttonIndex)
        {
            if (string.IsNullOrWhiteSpace(input))
                throw new ArgumentException($"Destination is required for button '{buttonTitle}' (index {buttonIndex}).");

            // Trim + strip control chars
            var cleaned = new string(input.Trim().Where(c => !char.IsControl(c)).ToArray());
            if (cleaned.Length == 0)
                throw new ArgumentException($"Destination is required for button '{buttonTitle}' (index {buttonIndex}).");

            // Allow tel: and WhatsApp deep links
            if (cleaned.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
                cleaned.StartsWith("wa:", StringComparison.OrdinalIgnoreCase) ||
                cleaned.StartsWith("https://wa.me/", StringComparison.OrdinalIgnoreCase))
            {
                return cleaned; // Accept as-is
            }

            // Normal web links
            if (Uri.TryCreate(cleaned, UriKind.Absolute, out var uri) &&
                (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                return uri.ToString();
            }

            // Reject everything else
            throw new ArgumentException(
                $"Destination must be an absolute http/https/tel/wa URL for button '{buttonTitle}' (index {buttonIndex}). Got: '{input}'");
        }

        private static bool LooksLikeAbsoluteBaseUrlWithPlaceholder(string? templateUrl)
        {
            if (string.IsNullOrWhiteSpace(templateUrl)) return false;
            var s = templateUrl.Trim();
            if (!s.Contains("{{")) return false;

            // Probe by replacing common placeholders with a char
            var probe = PlaceholderRe.Replace(s, "x");
            return Uri.TryCreate(probe, UriKind.Absolute, out var abs) &&
                   (abs.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                    abs.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
        }

        private static object[] BuildBodyParameters(List<string>? templateParams, int requiredCount)
        {
            if (requiredCount <= 0) return Array.Empty<object>();

            var src = templateParams ?? new List<string>();
            if (src.Count > requiredCount) src = src.Take(requiredCount).ToList();
            while (src.Count < requiredCount) src.Add(string.Empty);

            return src.Select(p => (object)new { type = "text", text = p ?? string.Empty }).ToArray();
        }

        private static string NormalizePhoneForTel(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var p = raw.Trim();
            var digits = new string(p.Where(char.IsDigit).ToArray());
            // keep leading + if present initially; always output +<digits>
            return "+" + digits;
        }

        private static string ReplaceAllPlaceholdersWith(string template, string replacement)
        {
            if (string.IsNullOrWhiteSpace(template)) return string.Empty;
            return PlaceholderRe.Replace(template, _ => replacement ?? string.Empty);
        }

        // ======================================================
        // META — TEXT TEMPLATE COMPONENTS
        // ======================================================

        // Back-compat wrapper (old signature)
        private List<object> BuildTextTemplateComponents_Meta(
            List<string> templateParams,
            List<CampaignButton>? buttonList,
            TemplateMetadataDto templateMeta,
            Guid campaignSendLogId,
            Contact contact)
        {
            return BuildTextTemplateComponents_Meta(
                templateParams, buttonList, templateMeta, campaignSendLogId, contact, out _);
        }

        // New overload with resolvedButtonUrls
        private List<object> BuildTextTemplateComponents_Meta(
            List<string> templateParams,
            List<CampaignButton>? buttonList,
            TemplateMetadataDto templateMeta,
            Guid campaignSendLogId,
            Contact contact,
            out List<string> resolvedButtonUrls)
        {
            var components = new List<object>();
            resolvedButtonUrls = new List<string>();

            // BODY: send exactly PlaceholderCount
            if (templateMeta.PlaceholderCount > 0)
            {
                var bodyParams = BuildBodyParameters(templateParams, templateMeta.PlaceholderCount);
                components.Add(new { type = "body", parameters = bodyParams });
            }

            // No buttons or template has no button params
            if (buttonList == null || buttonList.Count == 0 ||
                templateMeta.ButtonParams == null || templateMeta.ButtonParams.Count == 0)
                return components;

            // Ensure index alignment with the template by ordering by Position (then original index)
            var orderedButtons = buttonList
                .Select((b, idx) => new { Btn = b, idx })
                .OrderBy(x => (int?)x.Btn.Position ?? int.MaxValue)
                .ThenBy(x => x.idx)
                .Select(x => x.Btn)
                .ToList();

            var total = Math.Min(3, Math.Min(orderedButtons.Count, templateMeta.ButtonParams.Count));

            // Phone normalization (for optional {{1}} substitution on campaign button value)
            var phone = NormalizePhoneForTel(contact?.PhoneNumber);
            var encodedPhone = Uri.EscapeDataString(phone);

            for (int i = 0; i < total; i++)
            {
                var meta = templateMeta.ButtonParams[i];
                var subType = (meta.SubType ?? "url").ToLowerInvariant();
                var metaParam = meta.ParameterValue?.Trim();

                // Meta needs parameters ONLY for dynamic URL buttons
                if (!string.Equals(subType, "url", StringComparison.OrdinalIgnoreCase))
                    continue;

                var isDynamic = !string.IsNullOrWhiteSpace(metaParam) && metaParam.Contains("{{");
                if (!isDynamic)
                    continue;

                var btn = orderedButtons[i];
                var btnType = (btn?.Type ?? "URL").ToUpperInvariant();
                if (!string.Equals(btnType, "URL", StringComparison.OrdinalIgnoreCase))
                {
                    // If template expects dynamic URL at this index and your campaign button isn't URL, skip to avoid provider error
                    continue;
                }

                var valueRaw = btn.Value?.Trim();
                if (string.IsNullOrWhiteSpace(valueRaw))
                {
                    throw new InvalidOperationException(
                        $"Template requires a dynamic URL at button index {i}, but campaign button value is empty.");
                }

                // Optional phone substitution in destination (support any {{n}})
                var resolvedDestination = PlaceholderRe.Replace(valueRaw, m =>
                {
                    if (!int.TryParse(m.Groups[1].Value, out var n)) return "";
                    if (n == 1) return encodedPhone; // convention: {{1}} can be phone
                    var idx = n - 1;
                    return (idx >= 0 && idx < templateParams.Count) ? (templateParams[idx] ?? "") : "";
                });

                resolvedDestination = NormalizeAbsoluteUrlOrThrowForButton(resolvedDestination, btn.Title ?? "", i);

                // Build both; choose which to send based on template base style
                var fullTrackedUrl = _urlBuilderService.BuildTrackedButtonUrl(campaignSendLogId, i, btn.Title, resolvedDestination);
                var tokenParam = BuildTokenParam(campaignSendLogId, i, btn.Title, resolvedDestination);

                var templateHasAbsoluteBase = LooksLikeAbsoluteBaseUrlWithPlaceholder(metaParam);
                var valueToSend = templateHasAbsoluteBase ? tokenParam : fullTrackedUrl;

                components.Add(new Dictionary<string, object>
                {
                    ["type"] = "button",
                    ["sub_type"] = "url",
                    ["index"] = i.ToString(), // "0"/"1"/"2"
                    ["parameters"] = new[] {
                new Dictionary<string, object> { ["type"] = "text", ["text"] = valueToSend }
            }
                });

                // Provider-resolved URL (what the client actually clicks):
                // replace all placeholders in provider template with the parameter we sent.
                var providerResolved = ReplaceAllPlaceholdersWith(metaParam ?? "", valueToSend);
                resolvedButtonUrls.Add(providerResolved);
            }

            return components;
        }

        // ======================================================
        // PINNACLE — TEXT TEMPLATE COMPONENTS
        // ======================================================

        // Back-compat wrapper (old signature)
        private List<object> BuildTextTemplateComponents_Pinnacle(
            List<string> templateParams,
            List<CampaignButton>? buttonList,
            TemplateMetadataDto templateMeta,
            Guid campaignSendLogId,
            Contact contact)
        {
            return BuildTextTemplateComponents_Pinnacle(
                templateParams, buttonList, templateMeta, campaignSendLogId, contact, out _);
        }

        // New overload with resolvedButtonUrls
        private List<object> BuildTextTemplateComponents_Pinnacle(
            List<string> templateParams,
            List<CampaignButton>? buttonList,
            TemplateMetadataDto templateMeta,
            Guid campaignSendLogId,
            Contact contact,
            out List<string> resolvedButtonUrls)
        {
            var components = new List<object>();
            resolvedButtonUrls = new List<string>();

            // BODY: Pinnacle is strict → always send exactly PlaceholderCount
            if (templateMeta.PlaceholderCount > 0)
            {
                var bodyParams = BuildBodyParameters(templateParams, templateMeta.PlaceholderCount);
                components.Add(new { type = "body", parameters = bodyParams });
            }

            // No buttons to map → return body-only
            if (buttonList == null || buttonList.Count == 0 ||
                templateMeta.ButtonParams == null || templateMeta.ButtonParams.Count == 0)
                return components;

            // Ensure index alignment with the template by ordering by Position (then original index)
            var orderedButtons = buttonList
                .Select((b, idx) => new { Btn = b, idx })
                .OrderBy(x => (int?)x.Btn.Position ?? int.MaxValue)
                .ThenBy(x => x.idx)
                .Select(x => x.Btn)
                .ToList();

            var total = Math.Min(3, Math.Min(orderedButtons.Count, templateMeta.ButtonParams.Count));

            // Phone normalization (for optional {{1}} substitution on campaign button value)
            var phone = NormalizePhoneForTel(contact?.PhoneNumber);
            var encodedPhone = Uri.EscapeDataString(phone);

            for (int i = 0; i < total; i++)
            {
                var meta = templateMeta.ButtonParams[i];
                var subType = (meta.SubType ?? "url").ToLowerInvariant();
                var metaParam = meta.ParameterValue?.Trim();

                // This path supports dynamic URL params only
                if (!string.Equals(subType, "url", StringComparison.OrdinalIgnoreCase))
                    continue;

                var isDynamic = !string.IsNullOrWhiteSpace(metaParam) && metaParam.Contains("{{");
                if (!isDynamic)
                    continue;

                var btn = orderedButtons[i];
                var btnType = (btn?.Type ?? "URL").ToUpperInvariant();
                if (!string.Equals(btnType, "URL", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Template expects a dynamic URL at button index {i}, but campaign button type is '{btn?.Type}'.");
                }

                var valueRaw = btn?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(valueRaw))
                {
                    throw new InvalidOperationException(
                        $"Template requires a dynamic URL at button index {i}, but campaign button value is empty.");
                }

                // Optional phone + param substitution (support any {{n}})
                var resolvedDestination = PlaceholderRe.Replace(valueRaw, m =>
                {
                    if (!int.TryParse(m.Groups[1].Value, out var n)) return "";
                    if (n == 1) return encodedPhone;
                    var idx = n - 1;
                    return (idx >= 0 && idx < templateParams.Count) ? (templateParams[idx] ?? "") : "";
                });

                // Validate + normalize absolute URL
                resolvedDestination = NormalizeAbsoluteUrlOrThrowForButton(resolvedDestination, btn!.Title ?? "", i);

                // Build both options: full tracked URL vs token param (for absolute-base placeholders)
                var fullTrackedUrl = _urlBuilderService.BuildTrackedButtonUrl(campaignSendLogId, i, btn.Title, resolvedDestination);
                var tokenParam = BuildTokenParam(campaignSendLogId, i, btn.Title, resolvedDestination);

                var templateHasAbsoluteBase = LooksLikeAbsoluteBaseUrlWithPlaceholder(metaParam);
                var valueToSend = templateHasAbsoluteBase ? tokenParam : fullTrackedUrl;

                // Pinnacle payload shape (kept aligned with Meta)
                components.Add(new Dictionary<string, object>
                {
                    ["type"] = "button",
                    ["sub_type"] = "url",
                    ["index"] = i.ToString(),
                    ["parameters"] = new[] {
                new Dictionary<string, object> { ["type"] = "text", ["text"] = valueToSend }
            }
                });

                // Provider-resolved URL (what the user will open)
                var providerResolved = ReplaceAllPlaceholdersWith(metaParam ?? "", valueToSend);
                resolvedButtonUrls.Add(providerResolved);
            }

            return components;
        }


        #region SendImagetemplate

        //public async Task<ResponseResult> SendImageTemplateCampaignAsync(Campaign campaign)
        //{
        //    try
        //    {
        //        if (campaign == null || campaign.IsDeleted)
        //            return ResponseResult.ErrorInfo("❌ Invalid campaign.");
        //        if (campaign.Recipients == null || !campaign.Recipients.Any())
        //            return ResponseResult.ErrorInfo("❌ No recipients to send.");

        //        var businessId = campaign.BusinessId;

        //        // 🔧 Resolve provider for billing (prefer campaign.Provider, else active setting)
        //        string? provider = !string.IsNullOrWhiteSpace(campaign.Provider)
        //            ? campaign.Provider
        //            : await _context.WhatsAppSettings
        //                .AsNoTracking()
        //                .Where(s => s.BusinessId == businessId && s.IsActive)
        //                .OrderByDescending(s => s.PhoneNumberId != null)          // prefer default sender
        //                .ThenByDescending(s => s.UpdatedAt ?? s.CreatedAt)
        //                .Select(s => s.Provider)
        //                .FirstOrDefaultAsync();

        //        if (string.IsNullOrWhiteSpace(provider)) provider = "META_CLOUD";


        //        // 🔑 Flow entry → template name
        //        var (_, entryTemplate) = await ResolveFlowEntryAsync(businessId, campaign.CTAFlowConfigId);
        //        var templateName = !string.IsNullOrWhiteSpace(entryTemplate)
        //            ? entryTemplate!
        //            : (campaign.TemplateId ?? campaign.MessageTemplate ?? "");

        //        var language = "en_US";
        //        var templateParams = JsonConvert.DeserializeObject<List<string>>(campaign.TemplateParameters ?? "[]");

        //        int success = 0, failed = 0;

        //        foreach (var r in campaign.Recipients)
        //        {
        //            var dto = new ImageTemplateMessageDto
        //            {
        //                RecipientNumber = r.Contact.PhoneNumber,
        //                TemplateName = templateName,
        //                LanguageCode = language,
        //                HeaderImageUrl = campaign.ImageUrl,
        //                TemplateParameters = templateParams,
        //                ButtonParameters = campaign.MultiButtons
        //                    .OrderBy(b => b.Position)
        //                    .Take(3)
        //                    .Select(b => new CampaignButtonDto
        //                    {
        //                        ButtonText = b.Title,
        //                        ButtonType = b.Type,
        //                        TargetUrl = b.Value
        //                    }).ToList()
        //            };

        //            var res = await _messageEngineService.SendImageTemplateMessageAsync(dto, businessId);
        //            var ok = res.ToString().ToLower().Contains("messages");

        //            // keep ids + raw json for billing
        //            var payloadJson = JsonConvert.SerializeObject(res);
        //            var messageLogId = Guid.NewGuid();

        //            _context.MessageLogs.Add(new MessageLog
        //            {
        //                Id = messageLogId,
        //                BusinessId = businessId,
        //                CampaignId = campaign.Id,
        //                ContactId = r.ContactId,
        //                RecipientNumber = r.Contact.PhoneNumber,
        //                MessageContent = templateName,
        //                MediaUrl = campaign.ImageUrl,
        //                Status = ok ? "Sent" : "Failed",
        //                ErrorMessage = ok ? null : "API Failure",
        //                RawResponse = payloadJson,
        //                CreatedAt = DateTime.UtcNow,
        //                SentAt = DateTime.UtcNow,
        //                Source = "campaign"
        //            });

        //            // 🔎 Billing capture (send response)
        //            await _billingIngest.IngestFromSendResponseAsync(
        //                businessId: businessId,
        //                messageLogId: messageLogId,
        //                provider: provider!,
        //                rawResponseJson: payloadJson
        //            );


        //            //var res = await _messageEngineService.SendImageTemplateMessageAsync(dto, businessId);
        //            //var ok = res.ToString().ToLower().Contains("messages");

        //            //_context.MessageLogs.Add(new MessageLog
        //            //{
        //            //    Id = Guid.NewGuid(),
        //            //    BusinessId = businessId,
        //            //    CampaignId = campaign.Id,
        //            //    ContactId = r.ContactId,
        //            //    RecipientNumber = r.Contact.PhoneNumber,
        //            //    MessageContent = templateName,
        //            //    MediaUrl = campaign.ImageUrl,
        //            //    Status = ok ? "Sent" : "Failed",
        //            //    ErrorMessage = ok ? null : "API Failure",
        //            //    RawResponse = JsonConvert.SerializeObject(res),
        //            //    CreatedAt = DateTime.UtcNow,
        //            //    SentAt = DateTime.UtcNow,
        //            //    Source = "campaign"
        //            //});

        //            if (ok) success++; else failed++;
        //        }

        //        await _context.SaveChangesAsync();
        //        return ResponseResult.SuccessInfo($"✅ Sent: {success}, ❌ Failed: {failed}");
        //    }
        //    catch (Exception ex)
        //    {
        //        return ResponseResult.ErrorInfo("❌ Unexpected error during campaign send.", ex.ToString());
        //    }
        //}

        public async Task<ResponseResult> SendImageTemplateCampaignAsync(Campaign campaign)
        {
            try
            {
                if (campaign == null || campaign.IsDeleted)
                    return ResponseResult.ErrorInfo("❌ Invalid campaign.");
                if (campaign.Recipients == null || campaign.Recipients.Count == 0)
                    return ResponseResult.ErrorInfo("❌ No recipients to send.");

                var businessId = campaign.BusinessId;

                // --- helper identical to text flow ---
                static string? ResolveRecipientPhone(CampaignRecipient r) =>
                    r?.Contact?.PhoneNumber ?? r?.AudienceMember?.PhoneE164 ?? r?.AudienceMember?.PhoneRaw;

                // Keep only recipients that actually have a phone
                var recipients = campaign.Recipients
                    .Where(r => !string.IsNullOrWhiteSpace(ResolveRecipientPhone(r)))
                    .ToList();

                if (!recipients.Any())
                    return ResponseResult.ErrorInfo("⚠️ No valid recipients with phone numbers (Contact/AudienceMember).");

                // --- Flow/template selection (same as text flow) ---
                var (_, entryTemplate) = await ResolveFlowEntryAsync(businessId, campaign.CTAFlowConfigId);
                var templateName = !string.IsNullOrWhiteSpace(entryTemplate)
                    ? entryTemplate!
                    : (campaign.TemplateId ?? campaign.MessageTemplate ?? "");
                if (string.IsNullOrWhiteSpace(templateName))
                    return ResponseResult.ErrorInfo("❌ No template selected.");

                // --- Provider template meta (language, placeholder count, buttons) ---
                var tmplMeta = await _templateFetcherService.GetTemplateByNameAsync(
                    businessId, templateName, includeButtons: true);
                if (tmplMeta == null)
                    return ResponseResult.ErrorInfo("❌ Template metadata not found.");

                var languageCode = (tmplMeta.Language ?? "").Trim();
                if (string.IsNullOrWhiteSpace(languageCode))
                    return ResponseResult.ErrorInfo("❌ Template language not resolved from provider meta.");

                // --- Provider normalize (strict) ---
                string provider;
                if (!string.IsNullOrWhiteSpace(campaign.Provider))
                {
                    var p = campaign.Provider.Trim().ToUpperInvariant();
                    if (p != "PINNACLE" && p != "META_CLOUD")
                        return ResponseResult.ErrorInfo("❌ Invalid provider on campaign. Must be 'PINNACLE' or 'META_CLOUD'.");
                    provider = p;
                }
                else
                {
                    var settings = await _context.WhatsAppSettings.AsNoTracking()
                        .Where(s => s.BusinessId == businessId && s.IsActive)
                        .OrderByDescending(s => s.PhoneNumberId != null)               // prefer default sender
                        .ThenByDescending(s => s.UpdatedAt ?? s.CreatedAt)
                        .ToListAsync();

                    if (settings.Count == 0)
                        return ResponseResult.ErrorInfo("❌ WhatsApp settings not found.");
                    if (settings.Count > 1 && settings[0].PhoneNumberId == null)
                        return ResponseResult.ErrorInfo("❌ Multiple providers are active but no default sender is set.");

                    var p = settings[0].Provider?.Trim().ToUpperInvariant();
                    if (p != "PINNACLE" && p != "META_CLOUD")
                        return ResponseResult.ErrorInfo($"❌ Unsupported provider configured: {settings[0].Provider}");
                    provider = p!;
                }

                // --- Sender override (PNI): campaign override → else latest active for this provider ---
                string? phoneNumberIdOverride = campaign.PhoneNumberId;
                if (string.IsNullOrWhiteSpace(phoneNumberIdOverride))
                {
                    phoneNumberIdOverride = await _context.WhatsAppSettings.AsNoTracking()
                        .Where(s => s.BusinessId == businessId && s.IsActive && s.Provider == provider && s.PhoneNumberId != null)
                        .OrderByDescending(s => s.UpdatedAt ?? s.CreatedAt)
                        .Select(s => s.PhoneNumberId)
                        .FirstOrDefaultAsync();
                }

                // --- Flow entry step id (for logs) ---
                Guid? entryStepId = null;
                if (campaign.CTAFlowConfigId.HasValue)
                {
                    entryStepId = await _context.CTAFlowSteps
                        .Where(s => s.CTAFlowConfigId == campaign.CTAFlowConfigId.Value)
                        .OrderBy(s => s.StepOrder)
                        .Select(s => (Guid?)s.Id)
                        .FirstOrDefaultAsync();
                }

                // --- Freeze button bundle (provider meta) for analytics ---
                string? buttonBundleJson = null;
                if (tmplMeta.ButtonParams is { Count: > 0 })
                {
                    var bundle = tmplMeta.ButtonParams.Take(3)
                        .Select((b, i) => new { i, position = i + 1, text = (b.Text ?? "").Trim(), type = b.Type, subType = b.SubType })
                        .ToList();
                    buttonBundleJson = JsonConvert.SerializeObject(bundle);
                }

                // --- Prefetch AudienceMembers for recipients without Contact ---
                var neededMemberIds = recipients
                    .Where(x => x.ContactId == null && x.AudienceMemberId != null)
                    .Select(x => x.AudienceMemberId!.Value)
                    .Distinct()
                    .ToList();

                var audienceLookup = neededMemberIds.Count == 0
                    ? new Dictionary<Guid, (string Phone, string? Name)>()
                    : await _context.AudiencesMembers.AsNoTracking()
                        .Where(m => m.BusinessId == businessId && neededMemberIds.Contains(m.Id))
                        .Select(m => new { m.Id, m.PhoneE164, m.PhoneRaw, m.Name })
                        .ToDictionaryAsync(
                            x => x.Id,
                            x => (Phone: string.IsNullOrWhiteSpace(x.PhoneE164) ? (x.PhoneRaw ?? "") : x.PhoneE164,
                                  Name: x.Name)
                        );

                // --- Ordered campaign buttons (align with template) ---
                var buttons = campaign.MultiButtons?
                    .Select((b, idx) => new { Btn = b, idx })
                    .OrderBy(x => (int?)x.Btn.Position ?? int.MaxValue)
                    .ThenBy(x => x.idx)
                    .Select(x => x.Btn)
                    .ToList() ?? new List<CampaignButton>();

                int successCount = 0, failureCount = 0;
                var now = DateTime.UtcNow;

                foreach (var r in recipients)
                {
                    // Resolve phone + friendly name with Audience fallback
                    var phone = ResolveRecipientPhone(r);
                    string? name = r.Contact?.Name;

                    if (string.IsNullOrWhiteSpace(phone) && r.AudienceMemberId != null &&
                        audienceLookup.TryGetValue(r.AudienceMemberId.Value, out var a) &&
                        !string.IsNullOrWhiteSpace(a.Phone))
                    {
                        phone = a.Phone;
                        name ??= a.Name ?? "Customer";
                    }

                    if (string.IsNullOrWhiteSpace(phone))
                    {
                        failureCount++;
                        continue; // no destination
                    }

                    // Synthetic contact to avoid any null derefs downstream
                    var contactForTemplating = r.Contact ?? new Contact
                    {
                        Id = Guid.Empty,
                        BusinessId = businessId,
                        PhoneNumber = phone,
                        Name = name ?? "Customer"
                    };

                    // Per-recipient params (keep CSV/recipient overrides)
                    var resolvedParams = GetRecipientBodyParams(r, tmplMeta.PlaceholderCount, campaign.TemplateParameters);

                    // Hard guard: if template expects placeholders, refuse to send if any blank (prevents Meta 131008)
                    if (tmplMeta.PlaceholderCount > 0 && resolvedParams.Any(string.IsNullOrWhiteSpace))
                    {
                        failureCount++;

                        var why = $"Missing body parameter(s): expected {tmplMeta.PlaceholderCount}, got " +
                                  $"{resolvedParams.Count(x => !string.IsNullOrWhiteSpace(x))} filled.";

                        if (_context.Entry(r).State == EntityState.Detached) _context.Attach(r);
                        r.MaterializedAt = now;
                        r.UpdatedAt = now;
                        r.ResolvedParametersJson = JsonConvert.SerializeObject(resolvedParams);

                        var logIdLocal = Guid.NewGuid();
                        _context.MessageLogs.Add(new MessageLog
                        {
                            Id = logIdLocal,
                            BusinessId = businessId,
                            CampaignId = campaign.Id,
                            ContactId = r.ContactId,
                            RecipientNumber = phone,
                            MessageContent = templateName,
                            MediaUrl = campaign.ImageUrl,
                            Status = "Failed",
                            ErrorMessage = why,
                            RawResponse = "{\"local_error\":\"missing_template_body_params\"}",
                            CreatedAt = now,
                            Source = "campaign",
                            CTAFlowConfigId = campaign.CTAFlowConfigId,
                            CTAFlowStepId = entryStepId,
                            ButtonBundleJson = buttonBundleJson
                        });

                        await _context.CampaignSendLogs.AddAsync(new CampaignSendLog
                        {
                            Id = Guid.NewGuid(),
                            CampaignId = campaign.Id,
                            BusinessId = businessId,
                            ContactId = r.ContactId,
                            RecipientId = r.Id,
                            MessageBody = campaign.MessageBody ?? templateName,
                            TemplateId = templateName,
                            SendStatus = "Failed",
                            MessageLogId = logIdLocal,
                            ErrorMessage = why,
                            CreatedAt = now,
                            CTAFlowConfigId = campaign.CTAFlowConfigId,
                            CTAFlowStepId = entryStepId,
                            ButtonBundleJson = buttonBundleJson
                        });

                        continue;
                    }

                    // Build provider-style button components to freeze provider-resolved URLs
                    var runId = Guid.NewGuid();
                    var campaignSendLogId = Guid.NewGuid();
                    List<string> resolvedButtonUrls;

                    _ = (provider == "PINNACLE")
                        ? BuildImageTemplateComponents_Pinnacle(
                            campaign.ImageUrl, resolvedParams, buttons, tmplMeta, campaignSendLogId, contactForTemplating, out resolvedButtonUrls)
                        : BuildImageTemplateComponents_Meta(
                            campaign.ImageUrl, resolvedParams, buttons, tmplMeta, campaignSendLogId, contactForTemplating, out resolvedButtonUrls);

                    // Freeze recipient materialization BEFORE send
                    if (_context.Entry(r).State == EntityState.Detached) _context.Attach(r);
                    r.ResolvedParametersJson = JsonConvert.SerializeObject(resolvedParams);
                    r.ResolvedButtonUrlsJson = JsonConvert.SerializeObject(resolvedButtonUrls);
                    r.MaterializedAt = now;
                    r.UpdatedAt = now;
                    r.IdempotencyKey = Idempotency.Sha256(
                        $"{campaign.Id}|{phone}|{templateName}|{r.ResolvedParametersJson}|{r.ResolvedButtonUrlsJson}|{campaign.ImageUrl}|{campaign.ImageCaption}");

                    // Build DTO for engine (engine composes components from dto fields)
                    var dto = new ImageTemplateMessageDto
                    {
                        BusinessId = businessId,
                        Provider = provider,                 // <<< REQUIRED
                        PhoneNumberId = phoneNumberIdOverride,    // may be null → provider default sender
                        RecipientNumber = phone,
                        TemplateName = templateName,
                        LanguageCode = languageCode,
                        HeaderImageUrl = campaign.ImageUrl,
                        TemplateBody = campaign.MessageBody,     // for RenderedBody
                        TemplateParameters = resolvedParams,
                        ButtonParameters = buttons.Take(3).Select(b => new CampaignButtonDto
                        {
                            ButtonText = b.Title,
                            ButtonType = b.Type,
                            TargetUrl = b.Value
                        }).ToList(),
                        CTAFlowConfigId = campaign.CTAFlowConfigId,
                        CTAFlowStepId = entryStepId
                    };

                    // Send via message engine
                    var result = await _messageEngineService.SendImageTemplateMessageAsync(dto, businessId);

                    // Persist logs
                    var logId = Guid.NewGuid();
                    _context.MessageLogs.Add(new MessageLog
                    {
                        Id = logId,
                        BusinessId = businessId,
                        CampaignId = campaign.Id,
                        ContactId = r.ContactId,
                        RecipientNumber = phone,
                        MessageContent = templateName,
                        MediaUrl = campaign.ImageUrl,
                        Status = result.Success ? "Sent" : "Failed",
                        MessageId = result.MessageId,
                        ErrorMessage = result.ErrorMessage,
                        RawResponse = result.RawResponse,
                        CreatedAt = now,
                        SentAt = result.Success ? now : (DateTime?)null,
                        Source = "campaign",
                        CTAFlowConfigId = campaign.CTAFlowConfigId,
                        CTAFlowStepId = entryStepId,
                        ButtonBundleJson = buttonBundleJson,
                        RunId = runId
                    });

                    await _billingIngest.IngestFromSendResponseAsync(
                        businessId: businessId,
                        messageLogId: logId,
                        provider: provider,
                        rawResponseJson: result.RawResponse ?? "{}"
                    );

                    await _context.CampaignSendLogs.AddAsync(new CampaignSendLog
                    {
                        Id = campaignSendLogId,
                        CampaignId = campaign.Id,
                        BusinessId = businessId,
                        ContactId = r.ContactId,
                        RecipientId = r.Id,
                        MessageBody = campaign.MessageBody ?? templateName,
                        TemplateId = templateName,
                        SendStatus = result.Success ? "Sent" : "Failed",
                        MessageLogId = logId,
                        MessageId = result.MessageId,
                        ErrorMessage = result.ErrorMessage,
                        CreatedAt = now,
                        SentAt = result.Success ? now : (DateTime?)null,
                        CTAFlowConfigId = campaign.CTAFlowConfigId,
                        CTAFlowStepId = entryStepId,
                        ButtonBundleJson = buttonBundleJson,
                        RunId = runId
                    });

                    if (result.Success) successCount++; else failureCount++;
                }

                await _context.SaveChangesAsync();
                return ResponseResult.SuccessInfo($"📤 Sent to {successCount} recipients. ❌ Failed for {failureCount}.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error while sending image template campaign");
                return ResponseResult.ErrorInfo("🚨 Unexpected error while sending campaign.", ex.ToString());
            }
        }

        private List<object> BuildImageTemplateComponents_Pinnacle(
       string? imageUrl,
       List<string> templateParams,
       List<CampaignButton>? buttonList,
       TemplateMetadataDto templateMeta,
       Guid campaignSendLogId,
       Contact contact,
       out List<string> resolvedButtonUrls)
        {
            var components = new List<object>();
            resolvedButtonUrls = new List<string>();

            // Header
            if (!string.IsNullOrWhiteSpace(imageUrl) && templateMeta.HasImageHeader)
            {
                components.Add(new
                {
                    type = "header",
                    parameters = new object[]
                    {
                new { type = "image", image = new { link = imageUrl } }
                    }
                });
            }

            // Body
            if (templateMeta.PlaceholderCount > 0 && templateParams?.Count > 0)
            {
                components.Add(new
                {
                    type = "body",
                    parameters = templateParams.Select(p => new { type = "text", text = p }).ToArray()
                });
            }

            // Buttons
            if (buttonList == null || buttonList.Count == 0 ||
                templateMeta.ButtonParams == null || templateMeta.ButtonParams.Count == 0)
                return components;

            var total = Math.Min(3, Math.Min(buttonList.Count, templateMeta.ButtonParams.Count));

            // phone for optional {{1}}
            var phone = string.IsNullOrWhiteSpace(contact?.PhoneNumber) ? "" :
                        (contact.PhoneNumber.StartsWith("+") ? contact.PhoneNumber : "+" + contact.PhoneNumber);
            var encodedPhone = Uri.EscapeDataString(phone);

            for (int i = 0; i < total; i++)
            {
                var btn = buttonList[i];
                var meta = templateMeta.ButtonParams[i];
                var subtype = (meta.SubType ?? "url").ToLowerInvariant();
                var metaParam = meta.ParameterValue?.Trim() ?? string.Empty; // e.g. "/r/{{1}}"
                var isDynamic = metaParam.Contains("{{");

                if (!isDynamic)
                {
                    // static provider button at this index — no parameters to send
                    components.Add(new Dictionary<string, object>
                    {
                        ["type"] = "button",
                        ["sub_type"] = subtype,
                        ["index"] = i
                    });
                    continue;
                }

                var valueRaw = btn?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(valueRaw)) continue;

                // Optional phone substitution + body params {{n}}
                var resolvedDestination = PlaceholderRe.Replace(valueRaw, m =>
                {
                    if (!int.TryParse(m.Groups[1].Value, out var n)) return "";
                    if (n == 1) return encodedPhone;
                    var idx = n - 1;
                    return (idx >= 0 && idx < templateParams.Count) ? (templateParams[idx] ?? "") : "";
                });

                // Track + token (same pattern as text path)
                var fullTrackedUrl = _urlBuilderService.BuildTrackedButtonUrl(campaignSendLogId, i, btn.Title, resolvedDestination);
                var tokenParam = BuildTokenParam(campaignSendLogId, i, btn.Title, resolvedDestination);

                var templateHasAbsoluteBase = LooksLikeAbsoluteBaseUrlWithPlaceholder(metaParam);
                var valueToSend = templateHasAbsoluteBase ? tokenParam : fullTrackedUrl;

                components.Add(new Dictionary<string, object>
                {
                    ["type"] = "button",
                    ["sub_type"] = subtype,
                    ["index"] = i,
                    ["parameters"] = new[] { new Dictionary<string, object> { ["type"] = "text", ["text"] = valueToSend } }
                });

                // what the client will actually open once provider composes the URL
                var providerResolved = ReplaceAllPlaceholdersWith(metaParam, valueToSend);
                resolvedButtonUrls.Add(providerResolved);
            }

            return components;
        }


        private List<object> BuildImageTemplateComponents_Meta(
       string? imageUrl,
       List<string> templateParams,
       List<CampaignButton>? buttonList,
       TemplateMetadataDto templateMeta,
       Guid campaignSendLogId,
       Contact contact,
       out List<string> resolvedButtonUrls)
        {
            var components = new List<object>();
            resolvedButtonUrls = new List<string>();

            // Header
            if (!string.IsNullOrWhiteSpace(imageUrl) && templateMeta.HasImageHeader)
            {
                components.Add(new
                {
                    type = "header",
                    parameters = new[]
                    {
                new { type = "image", image = new { link = imageUrl } }
            }
                });
            }

            // Body
            if (templateMeta.PlaceholderCount > 0 && templateParams?.Count > 0)
            {
                components.Add(new
                {
                    type = "body",
                    parameters = templateParams.Select(p => new { type = "text", text = p }).ToArray()
                });
            }

            // Dynamic URL buttons only
            if (buttonList == null || buttonList.Count == 0 ||
                templateMeta.ButtonParams == null || templateMeta.ButtonParams.Count == 0)
                return components;

            var total = Math.Min(3, Math.Min(buttonList.Count, (templateMeta.ButtonParams?.Count() ?? 0)));
            var phone = string.IsNullOrWhiteSpace(contact?.PhoneNumber) ? "" :
                        (contact.PhoneNumber.StartsWith("+") ? contact.PhoneNumber : "+" + contact.PhoneNumber);
            var encodedPhone = Uri.EscapeDataString(phone);

            for (int i = 0; i < total; i++)
            {
                var meta = templateMeta.ButtonParams[i];
                var metaParam = meta.ParameterValue?.Trim();
                var isDynamic = !string.IsNullOrWhiteSpace(metaParam) && metaParam.Contains("{{");
                if (!isDynamic) continue;

                var btn = buttonList[i];
                var valueRaw = btn.Value?.Trim();
                if (string.IsNullOrWhiteSpace(valueRaw)) continue;

                var subtype = (meta.SubType ?? "url").ToLowerInvariant();

                // {{n}} substitution ({{1}} := phone)
                var resolvedDestination = PlaceholderRe.Replace(valueRaw, m =>
                {
                    if (!int.TryParse(m.Groups[1].Value, out var n)) return "";
                    if (n == 1) return encodedPhone;
                    var idx = n - 1;
                    return (idx >= 0 && idx < templateParams.Count) ? (templateParams[idx] ?? "") : "";
                });

                var fullTrackedUrl = _urlBuilderService.BuildTrackedButtonUrl(campaignSendLogId, i, btn.Title, resolvedDestination);
                var tokenParam = BuildTokenParam(campaignSendLogId, i, btn.Title, resolvedDestination);

                var templateHasAbsoluteBase = LooksLikeAbsoluteBaseUrlWithPlaceholder(metaParam);
                var valueToSend = templateHasAbsoluteBase ? tokenParam : fullTrackedUrl;

                components.Add(new Dictionary<string, object>
                {
                    ["type"] = "button",
                    ["sub_type"] = subtype,      // "url"
                    ["index"] = i.ToString(), // "0"/"1"/"2" for Meta
                    ["parameters"] = new[] { new Dictionary<string, object> { ["type"] = "text", ["text"] = valueToSend } }
                });

                var providerResolved = ReplaceAllPlaceholdersWith(metaParam ?? "", valueToSend);
                resolvedButtonUrls.Add(providerResolved);
            }

            return components;
        }







        #endregion

        #endregion

        // Features/CampaignModule/Services/CampaignsService.cs


        private List<object> BuildVideoTemplateComponents_Pinnacle(
            string? videoUrl,
            List<string> templateParams,
            List<CampaignButton>? buttonList,
            TemplateMetadataDto templateMeta,
            Guid campaignSendLogId,
            Contact contact)
        {
            var components = new List<object>();

            // --- Header (VIDEO) ---
            // TemplateMetadataDto has no HeaderType/HasVideoHeader → emit header when URL is present.
            if (!string.IsNullOrWhiteSpace(videoUrl))
            {
                components.Add(new
                {
                    type = "header",
                    parameters = new object[]
                    {
                new { type = "video", video = new { link = videoUrl } }
                    }
                });
            }

            // --- Body ---
            var bodyCount = templateMeta?.PlaceholderCount ?? 0;
            if (templateParams != null && templateParams.Count > 0 && bodyCount > 0)
            {
                components.Add(new
                {
                    type = "body",
                    parameters = templateParams.Select(p => new { type = "text", text = p ?? string.Empty }).ToArray()
                });
            }

            // --- Buttons (URL buttons only; indexes 0..2) ---
            if (buttonList != null && buttonList.Count > 0)
            {
                components.AddRange(BuildPinnacleUrlButtons(buttonList));
            }

            return components;
        }

        // Works with either CampaignButton (Type/Value) or CampaignButtonDto (ButtonType/TargetUrl).
        private static IEnumerable<object> BuildPinnacleUrlButtons(IEnumerable<object> rawButtons)
        {
            // keep incoming order; cap at 3
            var ordered = (rawButtons ?? Enumerable.Empty<object>()).Take(3).ToList();
            var n = ordered is ICollection<object> col ? col.Count : ordered.Count();

            for (int i = 0; i < n; i++)
            {
                var b = ordered[i];

                // Try to read "Type" or "ButtonType"
                var typeProp = b.GetType().GetProperty("Type") ?? b.GetType().GetProperty("ButtonType");
                var typeVal = (typeProp?.GetValue(b) as string)?.Trim().ToLowerInvariant() ?? "url";
                if (typeVal != "url") continue;

                // Try to read "Value" (CampaignButton) or "TargetUrl" (CampaignButtonDto)
                var valueProp = b.GetType().GetProperty("Value") ?? b.GetType().GetProperty("TargetUrl");
                var paramText = (valueProp?.GetValue(b) as string) ?? string.Empty;

                // If there is a per-recipient URL param, include it; otherwise emit static URL button (no parameters).
                if (!string.IsNullOrWhiteSpace(paramText))
                {
                    yield return new
                    {
                        type = "button",
                        sub_type = "url",
                        index = i, // 0-based
                        parameters = new object[]
                        {
                    new { type = "text", text = paramText }
                        }
                    };
                }
                else
                {
                    yield return new
                    {
                        type = "button",
                        sub_type = "url",
                        index = i
                    };
                }
            }
        }

        private static List<object> BuildVideoTemplateComponents_Meta(
                string? videoUrl,
                List<string>? templateParams,
                List<CampaignButtonDto>? buttonParams,
                TemplateMetadataDto? templateMeta)
        {
            var components = new List<object>();

            // We’re in the VIDEO sender path, so add header only if a URL is present.
            if (!string.IsNullOrWhiteSpace(videoUrl))
            {
                components.Add(new
                {
                    type = "header",
                    parameters = new object[]
                    {
                new { type = "video", video = new { link = videoUrl } }
                    }
                });
            }

            // Body placeholders: use meta.PlaceholderCount if available, otherwise list length.
            var bodyCount = templateMeta?.PlaceholderCount ?? templateParams?.Count ?? 0;
            if (bodyCount > 0 && (templateParams?.Count ?? 0) > 0)
            {
                components.Add(new
                {
                    type = "body",
                    parameters = templateParams!.Select(p => new { type = "text", text = p ?? string.Empty }).ToArray()
                });
            }

            // Buttons (URL buttons only). See helper below.
            if (buttonParams != null && buttonParams.Count > 0)
            {
                components.AddRange(BuildMetaTemplateButtons(buttonParams, templateMeta));
            }

            return components;
        }


        // CampaignButtonDto (your real one)

        private static IEnumerable<object> BuildMetaTemplateButtons(
            List<CampaignButtonDto> buttons,
            TemplateMetadataDto? templateMeta)   // meta unused here; kept for future expansion
        {
            // Keep incoming order; cap at 3
            var ordered = (buttons ?? new List<CampaignButtonDto>())
                .Take(3)
                .ToList();

            // Avoid Count ambiguity by caching n
            int n = ordered is ICollection<CampaignButtonDto> col ? col.Count : ordered.Count();

            for (int i = 0; i < n; i++)
            {
                var b = ordered[i];

                // Only URL buttons are supported for parameterized Meta buttons
                var isUrl = string.Equals(b?.ButtonType, "url", StringComparison.OrdinalIgnoreCase);
                if (!isUrl) continue;

                // If we have a per-recipient param (TargetUrl), include a parameter; else emit static button
                var paramText = b?.TargetUrl ?? string.Empty;
                var needsParam = !string.IsNullOrWhiteSpace(paramText);

                if (needsParam)
                {
                    yield return new
                    {
                        type = "button",
                        sub_type = "url",
                        index = i, // Meta uses 0-based indexes
                        parameters = new object[]
                        {
                    new { type = "text", text = paramText }
                        }
                    };
                }
                else
                {
                    yield return new
                    {
                        type = "button",
                        sub_type = "url",
                        index = i
                    };
                }
            }
        }




        public async Task<List<FlowListItemDto>> GetAvailableFlowsAsync(Guid businessId, bool onlyPublished = true)
        {
            return await _context.CTAFlowConfigs
                .AsNoTracking()
                .Where(f => f.BusinessId == businessId && f.IsActive && (!onlyPublished || f.IsPublished))
                .OrderByDescending(f => f.UpdatedAt)
                .Select(f => new FlowListItemDto
                {
                    Id = f.Id,
                    FlowName = f.FlowName,
                    IsPublished = f.IsPublished
                })
                .ToListAsync();
        }
        // ===================== DRY RUN (Step 2.3) =====================

        public async Task<CampaignDryRunResponseDto> DryRunTemplateCampaignAsync(Guid campaignId, int maxRecipients = 20)
        {
            var resp = new CampaignDryRunResponseDto { CampaignId = campaignId };

            // Load campaign + recipients (+Contact +AudienceMember) + buttons
            var campaign = await _context.Campaigns
                .Include(c => c.Recipients).ThenInclude(r => r.Contact)
                .Include(c => c.Recipients).ThenInclude(r => r.AudienceMember)
                .Include(c => c.MultiButtons)
                .FirstOrDefaultAsync(c => c.Id == campaignId && !c.IsDeleted);

            if (campaign == null)
            {
                resp.Notes.Add("Campaign not found.");
                return resp;
            }

            resp.CampaignType = campaign.CampaignType ?? "text";

            // Resolve entry template name from flow if present, else fall back
            var (_, entryTemplate) = await ResolveFlowEntryAsync(campaign.BusinessId, campaign.CTAFlowConfigId);
            var templateName = !string.IsNullOrWhiteSpace(entryTemplate)
                ? entryTemplate!
                : (campaign.TemplateId ?? campaign.MessageTemplate ?? "");

            if (string.IsNullOrWhiteSpace(templateName))
            {
                resp.Notes.Add("Template name is missing.");
                return resp;
            }

            // Fetch provider template metadata once (language, placeholders, button schema)
            var templateMeta = await _templateFetcherService.GetTemplateByNameAsync(
                campaign.BusinessId, templateName, includeButtons: true);

            resp.TemplateName = templateName;

            if (templateMeta == null)
            {
                resp.Notes.Add($"Template metadata not found for business. Name='{templateName}'.");
                return resp;
            }

            resp.Language = (templateMeta.Language ?? "").Trim();
            resp.HasHeaderMedia = templateMeta.HasImageHeader;

            if (string.IsNullOrWhiteSpace(resp.Language))
                resp.Notes.Add("Template language is not specified on metadata.");

            // Ensure non-null param list for builders (snapshot provided params)
            var providedParams = TemplateParameterHelper.ParseTemplateParams(campaign.TemplateParameters)
                                 ?? new List<string>();

            resp.RequiredPlaceholders = Math.Max(0, templateMeta.PlaceholderCount);
            resp.ProvidedPlaceholders = providedParams.Count;

            if (resp.RequiredPlaceholders != resp.ProvidedPlaceholders)
                resp.Notes.Add($"Placeholder mismatch: template requires {resp.RequiredPlaceholders}, provided {resp.ProvidedPlaceholders}. Consider re-snapshotting parameters.");

            // Dynamic URL button check (template expects params) vs campaign button values
            var templButtons = templateMeta.ButtonParams ?? new List<ButtonMetadataDto>();
            bool templateHasDynamicUrl = templButtons.Any(b =>
                string.Equals(b.SubType ?? "url", "url", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(b.ParameterValue) &&
                b.ParameterValue!.Contains("{{"));

            if (templateHasDynamicUrl)
            {
                var hasCampaignUrlValues = (campaign.MultiButtons ?? new List<CampaignButton>())
                    .Any(cb => !string.IsNullOrWhiteSpace(cb.Value));
                if (!hasCampaignUrlValues)
                    resp.Notes.Add("Template defines dynamic URL button(s) with placeholders, but campaign has no URL button values configured.");
            }

            // Provider normalization for preview
            var provider = (campaign.Provider ?? "META_CLOUD").Trim().ToUpperInvariant();
            if (provider != "PINNACLE" && provider != "META_CLOUD")
            {
                resp.Notes.Add($"Invalid provider on campaign: '{campaign.Provider}'. Dry run will assume META_CLOUD.");
                provider = "META_CLOUD";
            }

            // Slice some recipients (prefer latest activity; CreatedAt is not on CampaignRecipient)
            var recipients = (campaign.Recipients ?? new List<CampaignRecipient>())
     .OrderByDescending(r => (DateTime?)r.UpdatedAt
                              ?? r.MaterializedAt
                              ?? r.SentAt
                              ?? DateTime.MinValue)
     .Take(Math.Clamp(maxRecipients, 1, 200))
     .ToList();

            resp.RecipientsConsidered = recipients.Count;

            // Helper: resolve a phone for a recipient
            static string? ResolveRecipientPhone(CampaignRecipient r) =>
                r?.Contact?.PhoneNumber ?? r?.AudienceMember?.PhoneE164 ?? r?.AudienceMember?.PhoneRaw;

            int okCount = 0, errCount = 0;

            foreach (var r in recipients)
            {
                var phoneResolved = ResolveRecipientPhone(r) ?? "";
                var contactName = r.Contact?.Name ?? r.AudienceMember?.Name;

                var one = new CampaignDryRunRecipientResultDto
                {
                    ContactId = r.ContactId,
                    ContactName = contactName,
                    PhoneNumber = phoneResolved
                };

                // Phone checks (presence + basic shape)
                var phone = (one.PhoneNumber ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(phone))
                {
                    one.Errors.Add("Recipient phone missing (no Contact and no AudienceMember phone).");
                }
                else if (!Regex.IsMatch(phone, @"^\+?\d{8,15}$"))
                {
                    one.Warnings.Add("Recipient phone may be invalid (basic format check failed).");
                }

                try
                {
                    // Always synthesize a contact to avoid null derefs in builders
                    var contactForTemplating = r.Contact ?? new Contact
                    {
                        Id = Guid.Empty,
                        BusinessId = campaign.BusinessId,
                        PhoneNumber = phoneResolved,
                        Name = contactName ?? "Customer"
                    };

                    // Buttons ordered like send path: by Position then original index; limit 3
                    var buttons = (campaign.MultiButtons ?? new List<CampaignButton>())
                        .Select((b, idx) => new { Btn = b, idx })
                        .OrderBy(x => (int?)x.Btn.Position ?? int.MaxValue)
                        .ThenBy(x => x.idx)
                        .Select(x => x.Btn)
                        .Take(3)
                        .ToList();

                    // Build components for preview (match send path) — single call, discard out URLs
                    List<object> components;
                    var isImage = (campaign.CampaignType ?? "text")
                        .Equals("image", StringComparison.OrdinalIgnoreCase);

                    if (isImage)
                    {
                        components = (provider == "PINNACLE")
                            ? BuildImageTemplateComponents_Pinnacle(
                                campaign.ImageUrl, providedParams, buttons, templateMeta, Guid.NewGuid(), contactForTemplating, out _)
                            : BuildImageTemplateComponents_Meta(
                                campaign.ImageUrl, providedParams, buttons, templateMeta, Guid.NewGuid(), contactForTemplating, out _);
                    }
                    else
                    {
                        components = (provider == "PINNACLE")
                            ? BuildTextTemplateComponents_Pinnacle(
                                providedParams, buttons, templateMeta, Guid.NewGuid(), contactForTemplating, out _)
                            : BuildTextTemplateComponents_Meta(
                                providedParams, buttons, templateMeta, Guid.NewGuid(), contactForTemplating, out _);
                    }

                    // Additional validations like the send path: blank required params
                    if (templateMeta.PlaceholderCount > 0 &&
                        (providedParams.Count < templateMeta.PlaceholderCount ||
                         providedParams.Take(templateMeta.PlaceholderCount).Any(string.IsNullOrWhiteSpace)))
                    {
                        one.Errors.Add($"Missing body parameter(s): template requires {templateMeta.PlaceholderCount}, provided {providedParams.Count} (or some blank).");
                    }

                    one.ProviderComponents = components;
                    one.WouldSend = one.Errors.Count == 0;
                    if (one.WouldSend) okCount++; else errCount++;
                }
                catch (Exception ex)
                {
                    one.Errors.Add(ex.Message);
                    one.WouldSend = false;
                    errCount++;
                }

                resp.Results.Add(one);
            }

            resp.WouldSendCount = okCount;
            resp.ErrorCount = errCount;

            // Billability (best-effort)
            resp.EstimatedChargeable = true;
            resp.EstimatedConversationCategory = "template_outbound";
            if (!resp.Notes.Any(n => n.Contains("Template messages are typically chargeable")))
                resp.Notes.Add("Estimation: Template messages are typically chargeable and start a new conversation unless covered by free-entry flows.");

            return resp;
        }

        //public async Task<CampaignDryRunResponseDto> DryRunTemplateCampaignAsync(Guid campaignId, int maxRecipients = 20)
        //{
        //    var resp = new CampaignDryRunResponseDto { CampaignId = campaignId };

        //    // Load campaign + recipients (+Contact +AudienceMember) + buttons
        //    var campaign = await _context.Campaigns
        //        .Include(c => c.Recipients).ThenInclude(r => r.Contact)
        //        .Include(c => c.Recipients).ThenInclude(r => r.AudienceMember)
        //        .Include(c => c.MultiButtons)
        //        .FirstOrDefaultAsync(c => c.Id == campaignId && !c.IsDeleted);

        //    if (campaign == null)
        //    {
        //        resp.Notes.Add("Campaign not found.");
        //        return resp;
        //    }

        //    resp.CampaignType = campaign.CampaignType ?? "text";

        //    // Resolve entry template name from flow if present, else fall back
        //    var (_, entryTemplate) = await ResolveFlowEntryAsync(campaign.BusinessId, campaign.CTAFlowConfigId);
        //    var templateName = !string.IsNullOrWhiteSpace(entryTemplate)
        //        ? entryTemplate!
        //        : (campaign.TemplateId ?? campaign.MessageTemplate ?? "");

        //    if (string.IsNullOrWhiteSpace(templateName))
        //    {
        //        resp.Notes.Add("Template name is missing.");
        //        return resp;
        //    }

        //    // Fetch provider template metadata once (language, placeholders, button schema)
        //    var templateMeta = await _templateFetcherService.GetTemplateByNameAsync(
        //        campaign.BusinessId, templateName, includeButtons: true);

        //    resp.TemplateName = templateName;

        //    if (templateMeta == null)
        //    {
        //        resp.Notes.Add($"Template metadata not found for business. Name='{templateName}'.");
        //        return resp;
        //    }

        //    resp.Language = (templateMeta.Language ?? "").Trim();
        //    resp.HasHeaderMedia = templateMeta.HasImageHeader;

        //    if (string.IsNullOrWhiteSpace(resp.Language))
        //        resp.Notes.Add("Template language is not specified on metadata.");

        //    // Ensure non-null param list for builders (snapshot provided params)
        //    var providedParams = TemplateParameterHelper.ParseTemplateParams(campaign.TemplateParameters)
        //                         ?? new List<string>();

        //    resp.RequiredPlaceholders = Math.Max(0, templateMeta.PlaceholderCount);
        //    resp.ProvidedPlaceholders = providedParams.Count;

        //    if (resp.RequiredPlaceholders != resp.ProvidedPlaceholders)
        //        resp.Notes.Add($"Placeholder mismatch: template requires {resp.RequiredPlaceholders}, provided {resp.ProvidedPlaceholders}. Consider re-snapshotting parameters.");

        //    // Dynamic URL button check (template expects params) vs campaign button values
        //    var templButtons = templateMeta.ButtonParams ?? new List<ButtonMetadataDto>();
        //    bool templateHasDynamicUrl = templButtons.Any(b =>
        //        string.Equals(b.SubType ?? "url", "url", StringComparison.OrdinalIgnoreCase) &&
        //        !string.IsNullOrWhiteSpace(b.ParameterValue) &&
        //        b.ParameterValue!.Contains("{{"));

        //    if (templateHasDynamicUrl)
        //    {
        //        var hasCampaignUrlValues = (campaign.MultiButtons ?? new List<CampaignButton>())
        //            .Any(cb => !string.IsNullOrWhiteSpace(cb.Value));
        //        if (!hasCampaignUrlValues)
        //            resp.Notes.Add("Template defines dynamic URL button(s) with placeholders, but campaign has no URL button values configured.");
        //    }

        //    // Provider normalization for preview
        //    var provider = (campaign.Provider ?? "META_CLOUD").Trim().ToUpperInvariant();
        //    if (provider != "PINNACLE" && provider != "META_CLOUD")
        //    {
        //        resp.Notes.Add($"Invalid provider on campaign: '{campaign.Provider}'. Dry run will assume META_CLOUD.");
        //        provider = "META_CLOUD";
        //    }

        //    // Slice some recipients (prefer most recently updated)
        //    var recipients = (campaign.Recipients ?? new List<CampaignRecipient>())
        //        .OrderByDescending(r => r.UpdatedAt ?? r.CreatedAt)
        //        .Take(Math.Clamp(maxRecipients, 1, 200))
        //        .ToList();

        //    resp.RecipientsConsidered = recipients.Count;

        //    // Helper: resolve a phone for a recipient
        //    static string? ResolveRecipientPhone(CampaignRecipient r) =>
        //        r?.Contact?.PhoneNumber ?? r?.AudienceMember?.PhoneE164 ?? r?.AudienceMember?.PhoneRaw;

        //    int okCount = 0, errCount = 0;

        //    foreach (var r in recipients)
        //    {
        //        var phoneResolved = ResolveRecipientPhone(r) ?? "";
        //        var contactName = r.Contact?.Name ?? r.AudienceMember?.Name;

        //        var one = new CampaignDryRunRecipientResultDto
        //        {
        //            ContactId = r.ContactId,
        //            ContactName = contactName,
        //            PhoneNumber = phoneResolved
        //        };

        //        // Phone checks (presence + basic shape)
        //        var phone = (one.PhoneNumber ?? string.Empty).Trim();
        //        if (string.IsNullOrEmpty(phone))
        //        {
        //            one.Errors.Add("Recipient phone missing (no Contact and no AudienceMember phone).");
        //        }
        //        else if (!Regex.IsMatch(phone, @"^\+?\d{8,15}$"))
        //        {
        //            one.Warnings.Add("Recipient phone may be invalid (basic format check failed).");
        //        }

        //        try
        //        {
        //            // Always synthesize a contact to avoid null derefs in builders
        //            var contactForTemplating = r.Contact ?? new Contact
        //            {
        //                Id = Guid.Empty,
        //                BusinessId = campaign.BusinessId,
        //                PhoneNumber = phoneResolved,
        //                Name = contactName ?? "Customer"
        //            };

        //            // Buttons ordered like send path: by Position then original index; limit 3
        //            var buttons = (campaign.MultiButtons ?? new List<CampaignButton>())
        //                .Select((b, idx) => new { Btn = b, idx })
        //                .OrderBy(x => (int?)x.Btn.Position ?? int.MaxValue)
        //                .ThenBy(x => x.idx)
        //                .Select(x => x.Btn)
        //                .Take(3)
        //                .ToList();

        //            // Build components for preview (match send path)
        //            List<object> components;
        //            var isImage = (campaign.CampaignType ?? "text")
        //                .Equals("image", StringComparison.OrdinalIgnoreCase);

        //            if (isImage)
        //            {
        //                // use the image builders; discard resolvedButtonUrls for dry run
        //                _ = (provider == "PINNACLE")
        //                    ? BuildImageTemplateComponents_Pinnacle(
        //                        campaign.ImageUrl, providedParams, buttons, templateMeta, Guid.NewGuid(), contactForTemplating, out var _)
        //                    : BuildImageTemplateComponents_Meta(
        //                        campaign.ImageUrl, providedParams, buttons, templateMeta, Guid.NewGuid(), contactForTemplating, out var _);

        //                // We need the components object; the builders return it
        //                components = (provider == "PINNACLE")
        //                    ? BuildImageTemplateComponents_Pinnacle(
        //                        campaign.ImageUrl, providedParams, buttons, templateMeta, Guid.NewGuid(), contactForTemplating, out var __)
        //                    : BuildImageTemplateComponents_Meta(
        //                        campaign.ImageUrl, providedParams, buttons, templateMeta, Guid.NewGuid(), contactForTemplating, out var ___);
        //            }
        //            else
        //            {
        //                components = (provider == "PINNACLE")
        //                    ? BuildTextTemplateComponents_Pinnacle(
        //                        providedParams, buttons, templateMeta, Guid.NewGuid(), contactForTemplating, out var _)
        //                    : BuildTextTemplateComponents_Meta(
        //                        providedParams, buttons, templateMeta, Guid.NewGuid(), contactForTemplating, out var _);
        //            }

        //            // Additional validations like the send path: blank required params
        //            if (templateMeta.PlaceholderCount > 0 &&
        //                (providedParams.Count < templateMeta.PlaceholderCount ||
        //                 providedParams.Take(templateMeta.PlaceholderCount).Any(string.IsNullOrWhiteSpace)))
        //            {
        //                one.Errors.Add($"Missing body parameter(s): template requires {templateMeta.PlaceholderCount}, provided {providedParams.Count} (or some blank).");
        //            }

        //            one.ProviderComponents = components;
        //            one.WouldSend = one.Errors.Count == 0;
        //            if (one.WouldSend) okCount++; else errCount++;
        //        }
        //        catch (Exception ex)
        //        {
        //            one.Errors.Add(ex.Message);
        //            one.WouldSend = false;
        //            errCount++;
        //        }

        //        resp.Results.Add(one);
        //    }

        //    resp.WouldSendCount = okCount;
        //    resp.ErrorCount = errCount;

        //    // Billability (best-effort)
        //    resp.EstimatedChargeable = true;
        //    resp.EstimatedConversationCategory = "template_outbound";
        //    if (!resp.Notes.Any(n => n.Contains("Template messages are typically chargeable")))
        //        resp.Notes.Add("Estimation: Template messages are typically chargeable and start a new conversation unless covered by free-entry flows.");

        //    return resp;
        //}

        // in your CampaignService (same file as SendVideoTemplateCampaignAsync)
        private static List<CampaignButtonDto> MapButtonVarsToButtonDtos(Dictionary<string, string>? vars)
        {
            var list = new List<CampaignButtonDto>();
            if (vars == null || vars.Count == 0) return list;

            // We only care about URL buttons 1..3; take the param text
            for (var i = 1; i <= 3; i++)
            {
                if (vars.TryGetValue($"button{i}.url_param", out var param) && !string.IsNullOrWhiteSpace(param))
                {
                    list.Add(new CampaignButtonDto
                    {
                        ButtonText = $"Button {i}",   // optional; purely cosmetic
                        ButtonType = "url",
                        TargetUrl = param
                    });
                }
            }
            return list;
        }
        private async Task<ResponseResult> SendDocumentTemplateCampaignAsync(Campaign campaign)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInformation("[DocSend] Begin. campaignId={CampaignId}", campaign.Id);

            // force an IEnumerable → List and use a distinct name to avoid symbol collisions
            var recipientsList = (campaign.Recipients ?? Enumerable.Empty<CampaignRecipient>())
                    .Where(r =>
                    !string.IsNullOrWhiteSpace(r.Contact?.PhoneNumber) ||
                    !string.IsNullOrWhiteSpace(r.AudienceMember?.PhoneE164))
                         .ToList();

            // Use Any() (robust even if someone shadows Count somewhere)
            if (!recipientsList.Any())
                return ResponseResult.ErrorInfo("⚠️ No valid recipients with phone numbers.");

            var templateName = campaign.MessageTemplate;
            var languageCode = "en_US"; // keep consistent with your image/video path
            var provider = (campaign.Provider ?? "META").ToUpperInvariant();
            var phoneNumberId = campaign.PhoneNumberId;

            // optional static fallback (we don't have Campaign.DocumentUrl in this branch)
            var staticDocUrl = campaign.ImageUrl;

            var ok = 0; var fail = 0;

            foreach (var r in recipientsList)
            {
                var to = r.Contact?.PhoneNumber ?? r.AudienceMember?.PhoneE164 ?? "";
                if (string.IsNullOrWhiteSpace(to)) continue;

                try
                {
                    // These helpers were added earlier:
                    var templateParams = BuildBodyParametersForRecipient(campaign, r);
                    var buttonVars = BuildButtonParametersForRecipient(campaign, r);
                    var buttonsDto = MapButtonVarsToButtonDtos(buttonVars);
                    // Per-recipient doc header; no campaign-level DocumentUrl in this branch
                    var headerDocUrl = ResolvePerRecipientValue(r, "header.document_url") ?? staticDocUrl;

                    var dto = new DocumentTemplateMessageDto
                    {
                        BusinessId = campaign.BusinessId,
                        RecipientNumber = to,
                        TemplateName = templateName,
                        LanguageCode = languageCode,
                        HeaderDocumentUrl = headerDocUrl,
                        // match your DTO property names exactly — use the ones your MessageEngine expects:
                        Parameters = templateParams,   // or TemplateParameters if that's your DTO
                        Buttons = buttonsDto,      // or ButtonParameters if that's your DTO
                        Provider = provider,
                        PhoneNumberId = phoneNumberId,
                        CTAFlowConfigId = campaign.CTAFlowConfigId,
                        TemplateBody = campaign.MessageBody
                    };

                    var sent = await _messageEngineService.SendDocumentTemplateMessageAsync(dto, campaign.BusinessId);
                    var success = sent.Success;

                    if (success) ok++; else fail++;

                    await LogSendAsync(campaign, r, to, provider, success, headerDocUrl, "document");
                    _logger.LogInformation("[DocSend] to={To} success={Success}", to, success);
                }
                catch (Exception ex)
                {
                    fail++;
                    _logger.LogError(ex, "[DocSend] failed to={To}", to);
                    await LogSendAsync(campaign, r, to, provider, false, staticDocUrl, "document", ex.Message);
                }
            }

            sw.Stop();
            var msg = $"Document campaign finished. Success={ok}, Failed={fail}";
            _logger.LogInformation("[DocSend] Done. campaignId={CampaignId} {Msg}", campaign.Id, msg);

            return fail == 0 ? ResponseResult.SuccessInfo(msg) : ResponseResult.ErrorInfo(msg);
        }
        private Task LogSendAsync(
                    Campaign campaign,
                    CampaignRecipient recipient,
                    string to,
                    string provider,
                    bool success,
                    string? headerUrl,
                    string channel,
                    string? error = null)
        {
            _logger.LogInformation(
                "[SendLog] campaignId={CampaignId} to={To} provider={Provider} channel={Channel} success={Success} headerUrl={HeaderUrl} error={Error}",
                campaign.Id, to, provider, channel, success, headerUrl, error);

            // If/when you have a CampaignSendLogs table, insert there instead.
            return Task.CompletedTask;
        }



        private static string[] ReadResolvedParams(CampaignRecipient r)
        {
            var s = r?.ResolvedParametersJson;
            if (string.IsNullOrWhiteSpace(s)) return Array.Empty<string>();
            try
            {
                return JsonConvert.DeserializeObject<string[]>(s) ?? Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static Dictionary<string, string> ReadResolvedButtonVars(CampaignRecipient r)
        {
            var s = r?.ResolvedButtonUrlsJson;
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(s)) return dict;
            try
            {
                return JsonConvert.DeserializeObject<Dictionary<string, string>>(s)
                       ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return dict;
            }
        }

        // Optional: unified accessor for header media (image/video/document) if your materializer
        // saved canonical keys like "header.image_url" / "header.video_url" / "header.document_url".
        private static string? TryGetHeaderMedia(Dictionary<string, string> vars, params string[] keys)
        {
            foreach (var k in keys)
                if (!string.IsNullOrWhiteSpace(k) && vars.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
                    return v;
            return null;
        }

        public Task<object> SendVideoTemplateMessageAsync(VideoTemplateMessageDto dto, Guid businessId)
        {
            throw new NotImplementedException();
        }
    }


}


