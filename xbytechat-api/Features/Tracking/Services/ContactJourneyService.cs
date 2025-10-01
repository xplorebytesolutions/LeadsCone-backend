using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using xbytechat.api.Features.CTAFlowBuilder.Models;
using xbytechat.api.Features.Tracking.DTOs; // Updated namespace
// Add other necessary using statements for your project

namespace xbytechat.api.Features.Tracking.Services
{
    public class ContactJourneyService : IContactJourneyService
    {
        private readonly AppDbContext _context;

        public ContactJourneyService(AppDbContext context)
        {
            _context = context;
        }


        //public async Task<JourneyResponseDto> GetJourneyEventsAsync(Guid initialCampaignSendLogId)
        //{
        //    var resp = new JourneyResponseDto();
        //    var events = new List<JourneyEventDto>();

        //    // 0) Load the selected send
        //    var sentLog = await _context.CampaignSendLogs
        //        .AsNoTracking()
        //        .Include(x => x.Campaign)
        //        .Include(x => x.Contact)
        //        .FirstOrDefaultAsync(x => x.Id == initialCampaignSendLogId);

        //    // Require a fully-linked send (campaign + contact) for Journey
        //    // CampaignId is non-nullable now; only check navs + ContactId
        //    if (sentLog is null || sentLog.Campaign is null || sentLog.Contact is null || sentLog.ContactId == null)
        //    {
        //        resp.Events = events;
        //        return resp;
        //    }

        //    // Unwrap once into non-nullable locals
        //    var campaignId = sentLog.CampaignId;          // Guid (non-nullable)
        //    var contactId = sentLog.ContactId.Value;     // Guid (we ensured not null above)

        //    resp.CampaignId = campaignId;
        //    resp.ContactId = contactId;
        //    resp.ContactPhone = sentLog.Contact.PhoneNumber ?? "";
        //    resp.CampaignType = sentLog.CTAFlowConfigId.HasValue ? "flow" : "dynamic_url";
        //    resp.FlowId = sentLog.CTAFlowConfigId;

        //    // 1) Session window for THIS run of the campaign to THIS contact
        //    var sessionStart = sentLog.SentAt ?? sentLog.CreatedAt;

        //    // next send to same contact for same campaign
        //    var nextSameCampaignAt = await _context.CampaignSendLogs
        //        .AsNoTracking()
        //        .Where(x => x.ContactId == contactId &&
        //                    x.CampaignId == campaignId &&
        //                    x.CreatedAt > sessionStart)
        //        .OrderBy(x => x.CreatedAt)
        //        .Select(x => (DateTime?)x.CreatedAt)
        //        .FirstOrDefaultAsync();

        //    // next send to same contact for same flow (if this is a flow)
        //    DateTime? nextSameFlowAt = null;
        //    if (sentLog.CTAFlowConfigId.HasValue)
        //    {
        //        var flowId = sentLog.CTAFlowConfigId.Value;
        //        nextSameFlowAt = await _context.CampaignSendLogs
        //            .AsNoTracking()
        //            .Where(x => x.ContactId == contactId &&
        //                        x.CTAFlowConfigId == flowId &&
        //                        x.CreatedAt > sessionStart)
        //            .OrderBy(x => x.CreatedAt)
        //            .Select(x => (DateTime?)x.CreatedAt)
        //            .FirstOrDefaultAsync();
        //    }

        //    // session end = earliest “next run” OR +24h cap
        //    var sessionEnd = new[] { nextSameCampaignAt, nextSameFlowAt }
        //        .Where(dt => dt.HasValue)
        //        .Select(dt => dt!.Value)
        //        .DefaultIfEmpty(sessionStart.AddHours(24))
        //        .Min();

        //    // 2) Initial "sent" + statuses from CSL
        //    events.Add(new JourneyEventDto
        //    {
        //        Timestamp = sessionStart,
        //        Source = "System",
        //        EventType = "MessageSent",
        //        Title = $"Campaign '{sentLog.Campaign?.Name ?? "Campaign"}' sent",
        //        Details = $"Template '{sentLog.TemplateId}' to {resp.ContactPhone}",
        //        TemplateName = sentLog.TemplateId
        //    });

        //    if (sentLog.DeliveredAt is { } d1 && d1 >= sessionStart && d1 < sessionEnd)
        //        events.Add(new JourneyEventDto
        //        {
        //            Timestamp = d1,
        //            Source = "Provider",
        //            EventType = "Delivered",
        //            Title = "Message delivered",
        //            Details = $"Delivered to {resp.ContactPhone}",
        //            TemplateName = sentLog.TemplateId
        //        });

        //    if (sentLog.ReadAt is { } r1 && r1 >= sessionStart && r1 < sessionEnd)
        //        events.Add(new JourneyEventDto
        //        {
        //            Timestamp = r1,
        //            Source = "Provider",
        //            EventType = "Read",
        //            Title = "Message read",
        //            Details = $"Read by {resp.ContactPhone}",
        //            TemplateName = sentLog.TemplateId
        //        });

        //    // 3) URL clicks for THIS send within the window
        //    var urlClicksInitial = await _context.CampaignClickLogs
        //        .AsNoTracking()
        //        .Where(c => c.CampaignSendLogId == sentLog.Id &&
        //                    c.ClickedAt >= sessionStart &&
        //                    c.ClickedAt < sessionEnd)
        //        .OrderBy(c => c.ClickedAt)
        //        .ToListAsync();

        //    foreach (var c in urlClicksInitial)
        //    {
        //        events.Add(new JourneyEventDto
        //        {
        //            Timestamp = c.ClickedAt,
        //            Source = "User",
        //            EventType = "ButtonClicked",
        //            Title = $"Clicked URL Button: '{c.ButtonTitle}'",
        //            Details = $"Redirected to {c.Destination}",
        //            ButtonIndex = c.ButtonIndex,
        //            ButtonTitle = c.ButtonTitle,
        //            Url = c.Destination
        //        });
        //    }

        //    // 4) FLOW chain (if any) scoped to THIS session window
        //    if (sentLog.CTAFlowConfigId.HasValue)
        //    {
        //        var flowId = sentLog.CTAFlowConfigId.Value;

        //        // Flow label
        //        resp.FlowName = await _context.CTAFlowConfigs
        //            .AsNoTracking()
        //            .Where(f => f.Id == flowId)
        //            .Select(f => f.FlowName)
        //            .FirstOrDefaultAsync();

        //        // All flow sends (CSLs) for same contact+flow within the window
        //        var flowCslChain = await _context.CampaignSendLogs
        //            .AsNoTracking()
        //            .Where(csl => csl.BusinessId == sentLog.BusinessId &&
        //                          csl.ContactId == contactId &&
        //                          csl.CTAFlowConfigId == flowId &&
        //                          csl.CreatedAt >= sessionStart &&
        //                          csl.CreatedAt < sessionEnd)
        //            .OrderBy(csl => csl.CreatedAt)
        //            .Select(csl => new
        //            {
        //                csl.Id,
        //                csl.MessageLogId,
        //                csl.SentAt,
        //                csl.CreatedAt,
        //                csl.TemplateId,
        //                csl.CTAFlowStepId,
        //                csl.DeliveredAt,
        //                csl.ReadAt
        //            })
        //            .ToListAsync();

        //        var chainCslIds = flowCslChain.Select(x => x.Id).ToList();
        //        var chainMsgLogIds = flowCslChain
        //            .Where(x => x.MessageLogId.HasValue)
        //            .Select(x => x.MessageLogId!.Value)
        //            .ToList();

        //        var execByCsl = await _context.FlowExecutionLogs
        //            .AsNoTracking()
        //            .Where(f => f.CampaignSendLogId.HasValue &&
        //                        chainCslIds.Contains(f.CampaignSendLogId.Value) &&
        //                        f.ExecutedAt >= sessionStart &&
        //                        f.ExecutedAt < sessionEnd)
        //            .OrderBy(f => f.ExecutedAt)
        //            .ToListAsync();

        //        var execByMsg = chainMsgLogIds.Count == 0
        //            ? new List<FlowExecutionLog>()
        //            : await _context.FlowExecutionLogs
        //                .AsNoTracking()
        //                .Where(f => f.MessageLogId.HasValue &&
        //                            chainMsgLogIds.Contains(f.MessageLogId.Value) &&
        //                            f.ExecutedAt >= sessionStart &&
        //                            f.ExecutedAt < sessionEnd)
        //                .OrderBy(f => f.ExecutedAt)
        //                .ToListAsync();

        //        // Phone fallback (strictly within the session window; accept + or digits-only)
        //        var phoneA = resp.ContactPhone ?? "";
        //        var phoneB = phoneA.StartsWith("+") ? phoneA[1..] : "+" + phoneA;

        //        var execByPhone = await _context.FlowExecutionLogs
        //            .AsNoTracking()
        //            .Where(f => f.BusinessId == sentLog.BusinessId &&
        //                        f.FlowId == flowId &&
        //                        (f.ContactPhone == phoneA || f.ContactPhone == phoneB) &&
        //                        f.ExecutedAt >= sessionStart &&
        //                        f.ExecutedAt < sessionEnd)
        //            .OrderBy(f => f.ExecutedAt)
        //            .ToListAsync();

        //        var flowExec = execByCsl.Concat(execByMsg).Concat(execByPhone)
        //            .GroupBy(x => x.Id)
        //            .Select(g => g.First())
        //            .OrderBy(x => x.ExecutedAt)
        //            .ToList();

        //        foreach (var fe in flowExec)
        //        {
        //            if (!string.IsNullOrWhiteSpace(fe.TriggeredByButton))
        //            {
        //                events.Add(new JourneyEventDto
        //                {
        //                    Timestamp = fe.ExecutedAt,
        //                    Source = "User",
        //                    EventType = "ButtonClicked",
        //                    Title = $"Clicked Quick Reply: '{fe.TriggeredByButton}'",
        //                    Details = string.IsNullOrWhiteSpace(fe.TemplateName)
        //                        ? $"Advanced in flow at step '{fe.StepName}'"
        //                        : $"Triggered next template: '{fe.TemplateName}'",
        //                    StepId = fe.StepId,
        //                    StepName = fe.StepName,
        //                    ButtonIndex = fe.ButtonIndex,
        //                    ButtonTitle = fe.TriggeredByButton,
        //                    TemplateName = fe.TemplateName
        //                });
        //            }

        //            if (!string.IsNullOrWhiteSpace(fe.TemplateName))
        //            {
        //                events.Add(new JourneyEventDto
        //                {
        //                    Timestamp = fe.ExecutedAt,
        //                    Source = "System",
        //                    EventType = "FlowSend",
        //                    Title = $"Flow sent template '{fe.TemplateName}'",
        //                    Details = $"Step '{fe.StepName}'",
        //                    StepId = fe.StepId,
        //                    StepName = fe.StepName,
        //                    TemplateName = fe.TemplateName
        //                });
        //            }
        //        }

        //        // Include the flow CSLs themselves + statuses (within window)
        //        foreach (var csl in flowCslChain.Where(x => x.Id != sentLog.Id))
        //        {
        //            var ts = csl.SentAt ?? csl.CreatedAt;

        //            events.Add(new JourneyEventDto
        //            {
        //                Timestamp = ts,
        //                Source = "System",
        //                EventType = "FlowSend",
        //                Title = $"Flow sent template '{csl.TemplateId}'",
        //                Details = csl.CTAFlowStepId.HasValue ? $"Step: {csl.CTAFlowStepId}" : null,
        //                StepId = csl.CTAFlowStepId,
        //                TemplateName = csl.TemplateId
        //            });

        //            if (csl.DeliveredAt is { } d2 && d2 >= sessionStart && d2 < sessionEnd)
        //                events.Add(new JourneyEventDto
        //                {
        //                    Timestamp = d2,
        //                    Source = "Provider",
        //                    EventType = "Delivered",
        //                    Title = "Message delivered",
        //                    Details = "",
        //                    TemplateName = csl.TemplateId,
        //                    StepId = csl.CTAFlowStepId
        //                });

        //            if (csl.ReadAt is { } r2 && r2 >= sessionStart && r2 < sessionEnd)
        //                events.Add(new JourneyEventDto
        //                {
        //                    Timestamp = r2,
        //                    Source = "Provider",
        //                    EventType = "Read",
        //                    Title = "Message read",
        //                    Details = "",
        //                    TemplateName = csl.TemplateId,
        //                    StepId = csl.CTAFlowStepId
        //                });
        //        }

        //        // URL clicks during the flow (within window)
        //        if (chainCslIds.Count > 0)
        //        {
        //            var flowClicks = await _context.CampaignClickLogs
        //                .AsNoTracking()
        //                .Where(c => chainCslIds.Contains(c.CampaignSendLogId) &&
        //                            c.ClickedAt >= sessionStart &&
        //                            c.ClickedAt < sessionEnd)
        //                .OrderBy(c => c.ClickedAt)
        //                .ToListAsync();

        //            foreach (var c in flowClicks)
        //            {
        //                events.Add(new JourneyEventDto
        //                {
        //                    Timestamp = c.ClickedAt,
        //                    Source = "User",
        //                    EventType = "ButtonClicked",
        //                    Title = $"Clicked URL: '{c.ButtonTitle}'",
        //                    Details = $"Redirected to {c.Destination}",
        //                    ButtonIndex = c.ButtonIndex,
        //                    ButtonTitle = c.ButtonTitle,
        //                    Url = c.Destination
        //                });
        //            }
        //        }

        //        // Where the user left off in this session
        //        var lastFlowEvent = events
        //            .Where(e => e.EventType == "FlowSend" || e.EventType == "ButtonClicked")
        //            .OrderBy(e => e.Timestamp)
        //            .LastOrDefault();

        //        resp.LeftOffAt = lastFlowEvent?.StepName ?? lastFlowEvent?.Title;
        //    }

        //    resp.Events = events.OrderBy(e => e.Timestamp).ToList();
        //    return resp;
        //}
        public async Task<JourneyResponseDto> GetJourneyEventsAsync(Guid initialCampaignSendLogId)
        {
            var resp = new JourneyResponseDto { Events = new List<JourneyEventDto>() };
            var events = resp.Events;

            // 0) Load the selected send (campaign required; contact optional)
            var sentLog = await _context.CampaignSendLogs
                .AsNoTracking()
                .Include(x => x.Campaign)
                .Include(x => x.Contact)
                .FirstOrDefaultAsync(x => x.Id == initialCampaignSendLogId);

            if (sentLog is null || sentLog.Campaign is null)
                return resp;

            // Envelope (CampaignId is non-nullable now)
            var campaignId = sentLog.CampaignId;
            resp.CampaignId = campaignId;
            resp.CampaignType = sentLog.CTAFlowConfigId.HasValue ? "flow" : "dynamic_url";
            resp.FlowId = sentLog.CTAFlowConfigId;

            if (sentLog.ContactId.HasValue)
                resp.ContactId = sentLog.ContactId.Value;

            // ---- Resolve a phone for display/flow fallback --------------------------------------------
            string? phone = sentLog.Contact?.PhoneNumber;

            // via MessageLog
            if (string.IsNullOrWhiteSpace(phone) && sentLog.MessageLogId.HasValue)
            {
                phone = await _context.MessageLogs.AsNoTracking()
                    .Where(m => m.Id == sentLog.MessageLogId.Value && m.BusinessId == sentLog.BusinessId)
                    .Select(m => m.RecipientNumber)
                    .FirstOrDefaultAsync();
            }

            // via Recipient → Contact or AudienceMember
            if (string.IsNullOrWhiteSpace(phone) && sentLog.RecipientId != Guid.Empty)
            {
                var rec = await _context.CampaignRecipients.AsNoTracking()
                    .Where(r => r.Id == sentLog.RecipientId)
                    .Select(r => new { r.ContactId, r.AudienceMemberId })
                    .FirstOrDefaultAsync();

                if (rec is not null)
                {
                    if (rec.ContactId.HasValue)
                        phone = await _context.Contacts.AsNoTracking()
                            .Where(c => c.Id == rec.ContactId.Value)
                            .Select(c => c.PhoneNumber)
                            .FirstOrDefaultAsync();
                    else if (rec.AudienceMemberId.HasValue)
                        phone = await _context.AudiencesMembers.AsNoTracking()
                            .Where(a => a.Id == rec.AudienceMemberId.Value)
                            .Select(a => a.PhoneE164)
                            .FirstOrDefaultAsync();
                }
            }

            resp.ContactPhone = phone ?? "";

            // ---- 1) Session window ---------------------------------------------------------------------
            var sessionStart = sentLog.SentAt ?? sentLog.CreatedAt;

            DateTime sessionEnd;

            if (sentLog.ContactId.HasValue)
            {
                var contactId = sentLog.ContactId.Value;

                var nextSameCampaignAt = await _context.CampaignSendLogs.AsNoTracking()
                    .Where(x => x.ContactId == contactId &&
                                x.CampaignId == campaignId &&
                                x.CreatedAt > sessionStart)
                    .OrderBy(x => x.CreatedAt)
                    .Select(x => (DateTime?)x.CreatedAt)
                    .FirstOrDefaultAsync();

                DateTime? nextSameFlowAt = null;
                if (sentLog.CTAFlowConfigId.HasValue)
                {
                    var flowId = sentLog.CTAFlowConfigId.Value;
                    nextSameFlowAt = await _context.CampaignSendLogs.AsNoTracking()
                        .Where(x => x.ContactId == contactId &&
                                    x.CTAFlowConfigId == flowId &&
                                    x.CreatedAt > sessionStart)
                        .OrderBy(x => x.CreatedAt)
                        .Select(x => (DateTime?)x.CreatedAt)
                        .FirstOrDefaultAsync();
                }

                sessionEnd = new[] { nextSameCampaignAt, nextSameFlowAt }
                    .Where(dt => dt.HasValue)
                    .Select(dt => dt!.Value)
                    .DefaultIfEmpty(sessionStart.AddHours(24))
                    .Min();
            }
            else
            {
                // No ContactId: keep it simple and robust
                sessionEnd = sessionStart.AddHours(24);
            }

            // ---- 2) Initial "sent" + statuses from CSL -------------------------------------------------
            events.Add(new JourneyEventDto
            {
                Timestamp = sessionStart,
                Source = "System",
                EventType = "MessageSent",
                Title = $"Campaign '{sentLog.Campaign?.Name ?? "Campaign"}' sent",
                Details = string.IsNullOrWhiteSpace(resp.ContactPhone) ? null :
                               $"Template '{sentLog.TemplateId}' to {resp.ContactPhone}",
                TemplateName = sentLog.TemplateId
            });

            if (sentLog.DeliveredAt is { } d1 && d1 >= sessionStart && d1 < sessionEnd)
                events.Add(new JourneyEventDto
                {
                    Timestamp = d1,
                    Source = "Provider",
                    EventType = "Delivered",
                    Title = "Message delivered",
                    Details = string.IsNullOrWhiteSpace(resp.ContactPhone) ? null : $"Delivered to {resp.ContactPhone}",
                    TemplateName = sentLog.TemplateId
                });

            if (sentLog.ReadAt is { } r1 && r1 >= sessionStart && r1 < sessionEnd)
                events.Add(new JourneyEventDto
                {
                    Timestamp = r1,
                    Source = "Provider",
                    EventType = "Read",
                    Title = "Message read",
                    Details = string.IsNullOrWhiteSpace(resp.ContactPhone) ? null : $"Read by {resp.ContactPhone}",
                    TemplateName = sentLog.TemplateId
                });

            // ---- 3) URL clicks for THIS send within the window ----------------------------------------
            var urlClicksInitial = await _context.CampaignClickLogs
                .AsNoTracking()
                .Where(c => c.CampaignSendLogId == sentLog.Id &&
                            c.ClickedAt >= sessionStart && c.ClickedAt < sessionEnd)
                .OrderBy(c => c.ClickedAt)
                .ToListAsync();

            foreach (var c in urlClicksInitial)
            {
                events.Add(new JourneyEventDto
                {
                    Timestamp = c.ClickedAt,
                    Source = "User",
                    EventType = "ButtonClicked",
                    Title = $"Clicked URL Button: '{c.ButtonTitle}'",
                    Details = $"Redirected to {c.Destination}",
                    ButtonIndex = c.ButtonIndex,
                    ButtonTitle = c.ButtonTitle,
                    Url = c.Destination
                });
            }

            // ---- 4) FLOW chain (if any) ---------------------------------------------------------------
            if (sentLog.CTAFlowConfigId.HasValue)
            {
                var flowId = sentLog.CTAFlowConfigId.Value;

                resp.FlowName = await _context.CTAFlowConfigs.AsNoTracking()
                    .Where(f => f.Id == flowId)
                    .Select(f => f.FlowName)
                    .FirstOrDefaultAsync();

                // Build base query for CSLs in this business/flow within the window
                var flowCslQuery = _context.CampaignSendLogs.AsNoTracking()
                    .Where(csl => csl.BusinessId == sentLog.BusinessId &&
                                  csl.CTAFlowConfigId == flowId &&
                                  csl.CreatedAt >= sessionStart &&
                                  csl.CreatedAt < sessionEnd);

                // If we have ContactId, match on it; otherwise match by phone via MessageLogs
                List<Guid> chainCslIds;
                if (sentLog.ContactId.HasValue)
                {
                    var contactId = sentLog.ContactId.Value;
                    chainCslIds = await flowCslQuery.Where(csl => csl.ContactId == contactId)
                        .OrderBy(csl => csl.CreatedAt)
                        .Select(csl => csl.Id)
                        .ToListAsync();
                }
                else if (!string.IsNullOrWhiteSpace(phone))
                {
                    var msgIdsForPhone = await _context.MessageLogs.AsNoTracking()
                        .Where(m => m.BusinessId == sentLog.BusinessId &&
                                    m.RecipientNumber == phone &&
                                    m.CreatedAt >= sessionStart && m.CreatedAt < sessionEnd)
                        .Select(m => m.Id)
                        .ToListAsync();

                    chainCslIds = await flowCslQuery
                        .Where(csl => csl.MessageLogId.HasValue &&
                                      msgIdsForPhone.Contains(csl.MessageLogId.Value))
                        .OrderBy(csl => csl.CreatedAt)
                        .Select(csl => csl.Id)
                        .ToListAsync();

                    if (!chainCslIds.Contains(sentLog.Id))
                        chainCslIds.Add(sentLog.Id);
                }
                else
                {
                    chainCslIds = new List<Guid> { sentLog.Id };
                }

                // Pull minimal data for those CSLs (for statuses)
                var flowCslChain = await _context.CampaignSendLogs.AsNoTracking()
                    .Where(csl => chainCslIds.Contains(csl.Id))
                    .OrderBy(csl => csl.CreatedAt)
                    .Select(csl => new
                    {
                        csl.Id,
                        csl.MessageLogId,
                        csl.SentAt,
                        csl.CreatedAt,
                        csl.TemplateId,
                        csl.CTAFlowStepId,
                        csl.DeliveredAt,
                        csl.ReadAt
                    })
                    .ToListAsync();

                var chainMsgLogIds = flowCslChain
                    .Where(x => x.MessageLogId.HasValue)
                    .Select(x => x.MessageLogId!.Value)
                    .ToList();

                // Flow exec logs by CSL
                var execByCsl = await _context.FlowExecutionLogs.AsNoTracking()
                    .Where(f => f.CampaignSendLogId.HasValue &&
                                chainCslIds.Contains(f.CampaignSendLogId.Value) &&
                                f.ExecutedAt >= sessionStart && f.ExecutedAt < sessionEnd)
                    .OrderBy(f => f.ExecutedAt)
                    .ToListAsync();

                // ... by MessageLog
                var execByMsg = chainMsgLogIds.Count == 0
                    ? new List<FlowExecutionLog>()
                    : await _context.FlowExecutionLogs.AsNoTracking()
                        .Where(f => f.MessageLogId.HasValue &&
                                    chainMsgLogIds.Contains(f.MessageLogId.Value) &&
                                    f.ExecutedAt >= sessionStart && f.ExecutedAt < sessionEnd)
                        .OrderBy(f => f.ExecutedAt)
                        .ToListAsync();

                // ... by Phone fallback (accept + or digits-only)
                var phoneA = phone ?? "";
                var phoneB = string.IsNullOrWhiteSpace(phoneA)
                    ? ""
                    : (phoneA.StartsWith("+") ? phoneA[1..] : "+" + phoneA);

                var execByPhone = string.IsNullOrWhiteSpace(phoneA)
                    ? new List<FlowExecutionLog>()
                    : await _context.FlowExecutionLogs.AsNoTracking()
                        .Where(f => f.BusinessId == sentLog.BusinessId &&
                                    f.FlowId == flowId &&
                                    (f.ContactPhone == phoneA || f.ContactPhone == phoneB) &&
                                    f.ExecutedAt >= sessionStart && f.ExecutedAt < sessionEnd)
                        .OrderBy(f => f.ExecutedAt)
                        .ToListAsync();

                var flowExec = execByCsl.Concat(execByMsg).Concat(execByPhone)
                    .GroupBy(x => x.Id)
                    .Select(g => g.First())
                    .OrderBy(x => x.ExecutedAt)
                    .ToList();

                foreach (var fe in flowExec)
                {
                    if (!string.IsNullOrWhiteSpace(fe.TriggeredByButton))
                    {
                        events.Add(new JourneyEventDto
                        {
                            Timestamp = fe.ExecutedAt,
                            Source = "User",
                            EventType = "ButtonClicked",
                            Title = $"Clicked Quick Reply: '{fe.TriggeredByButton}'",
                            Details = string.IsNullOrWhiteSpace(fe.TemplateName)
                                          ? $"Advanced in flow at step '{fe.StepName}'"
                                          : $"Triggered next template: '{fe.TemplateName}'",
                            StepId = fe.StepId,
                            StepName = fe.StepName,
                            ButtonIndex = fe.ButtonIndex,
                            ButtonTitle = fe.TriggeredByButton,
                            TemplateName = fe.TemplateName
                        });
                    }

                    if (!string.IsNullOrWhiteSpace(fe.TemplateName))
                    {
                        events.Add(new JourneyEventDto
                        {
                            Timestamp = fe.ExecutedAt,
                            Source = "System",
                            EventType = "FlowSend",
                            Title = $"Flow sent template '{fe.TemplateName}'",
                            Details = $"Step '{fe.StepName}'",
                            StepId = fe.StepId,
                            StepName = fe.StepName,
                            TemplateName = fe.TemplateName
                        });
                    }
                }

                // Include the flow CSLs themselves + statuses (within window)
                foreach (var csl in flowCslChain.Where(x => x.Id != sentLog.Id))
                {
                    var ts = csl.SentAt ?? csl.CreatedAt;

                    events.Add(new JourneyEventDto
                    {
                        Timestamp = ts,
                        Source = "System",
                        EventType = "FlowSend",
                        Title = $"Flow sent template '{csl.TemplateId}'",
                        Details = csl.CTAFlowStepId.HasValue ? $"Step: {csl.CTAFlowStepId}" : null,
                        StepId = csl.CTAFlowStepId,
                        TemplateName = csl.TemplateId
                    });

                    if (csl.DeliveredAt is { } d2 && d2 >= sessionStart && d2 < sessionEnd)
                        events.Add(new JourneyEventDto
                        {
                            Timestamp = d2,
                            Source = "Provider",
                            EventType = "Delivered",
                            Title = "Message delivered",
                            Details = "",
                            TemplateName = csl.TemplateId,
                            StepId = csl.CTAFlowStepId
                        });

                    if (csl.ReadAt is { } r2 && r2 >= sessionStart && r2 < sessionEnd)
                        events.Add(new JourneyEventDto
                        {
                            Timestamp = r2,
                            Source = "Provider",
                            EventType = "Read",
                            Title = "Message read",
                            Details = "",
                            TemplateName = csl.TemplateId,
                            StepId = csl.CTAFlowStepId
                        });
                }

                // URL clicks during the flow (within window)
                if (chainCslIds.Count > 0)
                {
                    var flowClicks = await _context.CampaignClickLogs.AsNoTracking()
                        .Where(c => chainCslIds.Contains(c.CampaignSendLogId) &&
                                    c.ClickedAt >= sessionStart && c.ClickedAt < sessionEnd)
                        .OrderBy(c => c.ClickedAt)
                        .ToListAsync();

                    foreach (var c in flowClicks)
                    {
                        events.Add(new JourneyEventDto
                        {
                            Timestamp = c.ClickedAt,
                            Source = "User",
                            EventType = "ButtonClicked",
                            Title = $"Clicked URL: '{c.ButtonTitle}'",
                            Details = $"Redirected to {c.Destination}",
                            ButtonIndex = c.ButtonIndex,
                            ButtonTitle = c.ButtonTitle,
                            Url = c.Destination
                        });
                    }
                }

                // Left-off marker
                var lastFlowEvent = events
                    .Where(e => e.EventType == "FlowSend" || e.EventType == "ButtonClicked")
                    .OrderBy(e => e.Timestamp)
                    .LastOrDefault();

                resp.LeftOffAt = lastFlowEvent?.StepName ?? lastFlowEvent?.Title;
            }

            resp.Events = events.OrderBy(e => e.Timestamp).ToList();
            return resp;
        }

    }

}
