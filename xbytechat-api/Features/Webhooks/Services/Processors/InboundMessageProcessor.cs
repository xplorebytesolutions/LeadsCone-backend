using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using xbytechat.api;
using xbytechat.api.Features.Inbox.DTOs;
using xbytechat.api.CRM.Models;
using xbytechat.api.Features.Inbox.Hubs;
using Microsoft.Extensions.DependencyInjection;
using xbytechat.api.CRM.Interfaces;
using xbytechat.api.Features.AutoReplyBuilder.Services;
using xbytechat.api.Features.Inbox.Services;
using xbytechat.api.Features.MessagesEngine.DTOs;
using xbytechat.api.Features.MessagesEngine.Services;
using xbytechat.api.CRM.Services;
using xbytechat.api.Features.Automation.Services;
using xbytechat.api.Features.Contacts.Services;


namespace xbytechat.api.Features.Webhooks.Services.Processors
{
    public class InboundMessageProcessor : IInboundMessageProcessor
    {
        private readonly AppDbContext _context;
        private readonly IHubContext<InboxHub> _hubContext;
        private readonly ILogger<InboundMessageProcessor> _logger;
        private readonly IInboxService _inboxService;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly IHubContext<InboxHub> _hub;
        private readonly IContactProfileService _contactProfile;
        public InboundMessageProcessor(
            AppDbContext context,
            IHubContext<InboxHub> hubContext,
            ILogger<InboundMessageProcessor> logger,
            IInboxService inboxService,
            IServiceScopeFactory serviceScopeFactory,
            IHubContext<InboxHub> hub, IContactProfileService contactProfile)
        {
            _context = context;
            _hubContext = hubContext;
            _logger = logger;
            _inboxService = inboxService;
            _serviceScopeFactory = serviceScopeFactory;
            _hub = hub;
            _contactProfile = contactProfile;
        }

        public async Task ProcessChatAsync(JsonElement value)
        {
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var contactService = scope.ServiceProvider.GetRequiredService<IContactService>();
                var chatSessionStateService = scope.ServiceProvider.GetRequiredService<IChatSessionStateService>();
                var automationService = scope.ServiceProvider.GetRequiredService<IAutomationService>();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<InboundMessageProcessor>>();

                // ✅ INSERT: resolve profile updater
                var contactProfileService = scope.ServiceProvider.GetRequiredService<IContactProfileService>();

                string Normalize(string? number) =>
                    string.IsNullOrWhiteSpace(number)
                        ? ""
                        : new string(number.Where(char.IsDigit).ToArray());

                // 1) Extract WA metadata and message
                var msg = value.GetProperty("messages")[0];
                var rawContactPhone = msg.GetProperty("from").GetString()!;
                var contactPhone = Normalize(rawContactPhone);
                var content = msg.GetProperty("text").GetProperty("body").GetString();
                var rawBusinessNumber = value.GetProperty("metadata").GetProperty("display_phone_number").GetString()!;
                var cleanIncomingBusiness = Normalize(rawBusinessNumber);

                // 2) Resolve business
                var candidateBusinesses = await db.Businesses
                    .Include(b => b.WhatsAppSettings)
                    .Where(b => b.WhatsAppSettings != null &&
                                b.WhatsAppSettings.Any(s => s.WhatsAppBusinessNumber != null))
                    .ToListAsync();

                var business = candidateBusinesses.FirstOrDefault(b =>
                    b.WhatsAppSettings.Any(s => Normalize(s.WhatsAppBusinessNumber!) == cleanIncomingBusiness));

                if (business == null)
                {
                    logger.LogWarning("❌ Business not found for WhatsApp number: {Number}", rawBusinessNumber);
                    return;
                }

                var businessId = business.Id;

                // 3) Find or create contact
                var contact = await contactService.FindOrCreateAsync(businessId, contactPhone);
                if (contact == null)
                {
                    logger.LogWarning("❌ Could not resolve contact for phone: {Phone}", contactPhone);
                    return;
                }

                // ✅ INSERT: Extract profile.name (Meta shape) and upsert into Contacts
                string? TryGetProfileName(JsonElement root)
                {
                    // Safe TryGetProperty chain for: contacts[0].profile.name
                    if (root.TryGetProperty("contacts", out var contactsEl) &&
                        contactsEl.ValueKind == JsonValueKind.Array &&
                        contactsEl.GetArrayLength() > 0)
                    {
                        var c0 = contactsEl[0];
                        if (c0.TryGetProperty("profile", out var profileEl) &&
                            profileEl.ValueKind == JsonValueKind.Object &&
                            profileEl.TryGetProperty("name", out var nameEl) &&
                            nameEl.ValueKind == JsonValueKind.String)
                        {
                            var n = nameEl.GetString();
                            return string.IsNullOrWhiteSpace(n) ? null : n!.Trim();
                        }
                    }
                    return null;
                }

                var profileName = TryGetProfileName(value);
                if (!string.IsNullOrWhiteSpace(profileName))
                {
                    try
                    {
                        await contactProfileService.UpsertProfileNameAsync(businessId, contactPhone, profileName!, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "⚠️ Failed to upsert ProfileName for {Phone}", contactPhone);
                        // non-fatal; continue processing
                    }
                }

                // 4) Check chat mode…
                var mode = await chatSessionStateService.GetChatModeAsync(businessId, contact.Id);
                var isAgentMode = mode == "agent";

                // 5) Log incoming message
                var messageLog = new MessageLog
                {
                    Id = Guid.NewGuid(),
                    BusinessId = businessId,
                    ContactId = contact.Id,
                    RecipientNumber = contactPhone,
                    MessageContent = content,
                    Status = "received",
                    CreatedAt = DateTime.UtcNow,
                    SentAt = DateTime.UtcNow,
                    IsIncoming = true
                };

                db.MessageLogs.Add(messageLog);
                await db.SaveChangesAsync();

                await _hub.Clients
                    .Group($"business_{businessId}")
                    .SendAsync("ReceiveInboxMessage", new
                    {
                        contactId = contact.Id,
                        message = messageLog.MessageContent,
                        isIncoming = true,
                        senderId = (Guid?)null,
                        sentAt = messageLog.CreatedAt
                    });

                // 6) Try to trigger automation by keyword
                try
                {
                    var triggerKeyword = (content ?? string.Empty).Trim().ToLowerInvariant();
                    var handled = await automationService.TryRunFlowByKeywordAsync(
                        businessId,
                        triggerKeyword,
                        contact.PhoneNumber,
                        sourceChannel: "whatsapp",
                        industryTag: "default"
                    );

                    if (!handled)
                    {
                        logger.LogInformation("🕵️ No automation flow matched keyword: {Keyword}", triggerKeyword);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "❌ Automation flow execution failed.");
                }

                // 7) Sync to inbox only if agent mode
                if (isAgentMode)
                {
                    var inboxService = scope.ServiceProvider.GetRequiredService<IInboxService>();
                    await inboxService.SaveIncomingMessageAsync(new InboxMessageDto
                    {
                        BusinessId = businessId,
                        ContactId = contact.Id,
                        RecipientPhone = contact.PhoneNumber,
                        MessageBody = messageLog.MessageContent,
                        IsIncoming = true,
                        Status = messageLog.Status,
                        SentAt = messageLog.CreatedAt
                    });

                    logger.LogInformation("📥 Message synced to inbox for contact {Phone}", contactPhone);
                }
                else
                {
                    logger.LogInformation("🚫 Skipping inbox sync: chat mode is not 'agent'");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to process inbound WhatsApp chat.");
            }
        }
        public async Task ProcessInteractiveAsync(JsonElement value, CancellationToken ct = default)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var contactProfileService = scope.ServiceProvider.GetRequiredService<IContactProfileService>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<InboundMessageProcessor>>();

            string Normalize(string? number) =>
                string.IsNullOrWhiteSpace(number) ? "" : new string(number.Where(char.IsDigit).ToArray());

            // Extract Meta-shaped fields safely:
            string? TryGetProfileName(JsonElement root)
            {
                if (root.TryGetProperty("contacts", out var contactsEl) &&
                    contactsEl.ValueKind == JsonValueKind.Array &&
                    contactsEl.GetArrayLength() > 0)
                {
                    var c0 = contactsEl[0];
                    if (c0.TryGetProperty("profile", out var profileEl) &&
                        profileEl.ValueKind == JsonValueKind.Object &&
                        profileEl.TryGetProperty("name", out var nameEl) &&
                        nameEl.ValueKind == JsonValueKind.String)
                    {
                        var n = nameEl.GetString();
                        return string.IsNullOrWhiteSpace(n) ? null : n!.Trim();
                    }
                }
                return null;
            }

            // messages[0].from is always present for interactive/button
            if (!value.TryGetProperty("messages", out var msgs) || msgs.GetArrayLength() == 0)
                return;

            var msg0 = msgs[0];
            var fromRaw = msg0.GetProperty("from").GetString() ?? "";
            var fromE164 = Normalize(fromRaw);

            // Resolve Business via metadata.display_phone_number (same as chat path)
            var displayNumberRaw = value.GetProperty("metadata").GetProperty("display_phone_number").GetString() ?? "";
            var displayNumber = Normalize(displayNumberRaw);

            var business = await db.Businesses
                .Include(b => b.WhatsAppSettings)
                .Where(b => b.WhatsAppSettings != null && b.WhatsAppSettings.Any(s => s.WhatsAppBusinessNumber != null))
                .ToListAsync(ct);

            var biz = business.FirstOrDefault(b => b.WhatsAppSettings!.Any(s => Normalize(s.WhatsAppBusinessNumber!) == displayNumber));
            if (biz == null)
            {
                logger.LogWarning("❌ Business not found for interactive webhook number: {Num}", displayNumberRaw);
                return;
            }

            // Upsert profile name if present
            var profileName = TryGetProfileName(value);
            if (!string.IsNullOrWhiteSpace(profileName))
            {
                try
                {
                    await contactProfileService.UpsertProfileNameAsync(biz.Id, fromE164, profileName!, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "⚠️ Failed to upsert ProfileName on interactive webhook for {Phone}", fromE164);
                }
            }

            // … continue your existing interactive handling (routing to next step, etc.)
        }

    }
}
