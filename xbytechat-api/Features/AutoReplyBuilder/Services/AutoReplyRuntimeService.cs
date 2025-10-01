using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using xbytechat.api.DTOs.Messages;
using xbytechat.api.Features.AutoReplyBuilder.Flows.DTOs.FlowNodeConfigs;
using xbytechat.api.Features.AutoReplyBuilder.Flows.Repositories;
using xbytechat.api.Features.AutoReplyBuilder.Models;
using xbytechat.api.Features.AutoReplyBuilder.Repositories;
using xbytechat.api.Features.MessagesEngine.DTOs;
using xbytechat.api.Features.MessagesEngine.Services;
using xbytechat.api.Services;
using xbytechat.api;
using xbytechat.api.CRM.Models;
using xbytechat.api.CRM.Interfaces;
using Microsoft.EntityFrameworkCore;
using xbytechat.api.Features.CampaignModule.Models;

namespace xbytechat.api.Features.AutoReplyBuilder.Services
{
    public class AutoReplyRuntimeService : IAutoReplyRuntimeService
    {
        private readonly IAutoReplyRepository _autoReplyRepo;
        private readonly IAutoReplyFlowRepository _flowRepo;
        private readonly IMessageEngineService _messageEngine;
        private readonly AppDbContext _context;
        private readonly ILogger<AutoReplyRuntimeService> _logger;
        private readonly IContactService _contactService;
        private readonly ITagService _tagService;
        private readonly ITemplateMessageSender _templateSender;
        public AutoReplyRuntimeService(
            IAutoReplyRepository autoReplyRepo,
            IAutoReplyFlowRepository flowRepo,
            IMessageEngineService messageEngine,
            AppDbContext context,
            ILogger<AutoReplyRuntimeService> logger, IContactService contactService, ITagService tagService, ITemplateMessageSender templateSender)
        {
            _autoReplyRepo = autoReplyRepo;
            _flowRepo = flowRepo;
            _messageEngine = messageEngine;
            _context = context;
            _logger = logger;
            _contactService = contactService;
            _tagService = tagService;
            _templateSender = templateSender;
        }

        public async Task<bool> TryRunAutoReplyFlowAsync(Guid businessId, string keyword, Guid contactId, string phone)
        {
            _logger.LogInformation("🔍 Auto-reply trigger: '{Keyword}' from {Phone}", keyword, phone);

            try
            {
                // 1️⃣ Try matching a flow by keyword
                var flow = await _flowRepo.FindFlowByKeywordAsync(businessId, keyword);
                if (flow != null)
                {
                    _logger.LogInformation("✅ Flow matched: {FlowName}", flow.Name);
                    await RunFlowAsync(flow.Id, businessId, contactId, phone, keyword, flow.Name);
                    return true;
                }

                // 2️⃣ Fallback: Try matching auto-reply rule
                var rule = await _autoReplyRepo.MatchByKeywordAsync(businessId, keyword);
                if (rule != null)
                {
                    _logger.LogInformation("🔁 Fallback auto-reply triggered: {Rule}", rule.TriggerKeyword);

                    var messageDto = new TextMessageSendDto
                    {
                        BusinessId = businessId,
                        RecipientNumber = phone,
                        TextContent = rule.ReplyMessage
                    };

                    var result = await _messageEngine.SendTextDirectAsync(messageDto);

                    await LogAutoReplyAsync(
                        businessId,
                        contactId,
                        keyword,
                        "fallback",
                        rule.ReplyMessage,
                        null,
                        result?.LogId
                    );

                    return true;
                }

                _logger.LogWarning("❌ No flow or fallback rule matched for: {Keyword}", keyword);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error in TryRunAutoReplyFlowAsync");
                return false;
            }
        }

        public async Task RunFlowAsync(Guid flowId, Guid businessId, Guid contactId, string phone, string keyword, string flowName)
        {
            var nodes = await _flowRepo.GetNodesByFlowIdAsync(flowId);
            var edges = await _flowRepo.GetEdgesByFlowIdAsync(flowId);

            var nodeDict = nodes.ToDictionary(n => n.Id.ToString(), n => n);
            var edgeLookup = edges.GroupBy(e => e.SourceNodeId)
                                  .ToDictionary(g => g.Key, g => g.ToList());

            var currentNodeId = nodes.FirstOrDefault(n => n.NodeType == "start")?.Id.ToString();
            if (string.IsNullOrEmpty(currentNodeId)) return;

            // ✅ Ensure contact exists
            var contact = await _contactService.FindOrCreateAsync(businessId, phone);

            while (!string.IsNullOrEmpty(currentNodeId))
            {
                if (!nodeDict.TryGetValue(currentNodeId, out var node)) break;

                _logger.LogInformation("⚙️ Executing node {NodeId} [{NodeType}]", node.Id, node.NodeType);

                try
                {
                    switch (node.NodeType)
                    {
                        case "start":
                            _logger.LogInformation("🚦 Start node reached: {NodeId}", node.Id);

                            // 🛑 If Start node contains buttons, STOP and wait for user interaction
                            try
                            {
                                var config = JsonConvert.DeserializeObject<Dictionary<string, object>>(node.ConfigJson ?? "{}");
                                if (config != null && config.TryGetValue("multiButtons", out var rawButtons))
                                {
                                    var buttons = JsonConvert.DeserializeObject<List<object>>(rawButtons.ToString() ?? "[]");
                                    if (buttons.Count > 0)
                                    {
                                        _logger.LogInformation("🛑 Start node has buttons – waiting for user interaction. Halting flow.");
                                        return;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "❌ Failed to parse start node config for button detection.");
                            }
                            break;

                        case "message":
                            await ExecuteMessageNodeAsync(node, businessId, contactId, phone, keyword, flowName);
                            break;

                        case "template":
                            await ExecuteTemplateNodeAsync(node, businessId, contactId, phone, keyword, flowName);

                          
                                try
                                {
                                    var cfg = JsonConvert.DeserializeObject<TemplateConfig>(node.ConfigJson ?? "{}");
                                    if (cfg?.MultiButtons?.Any(b => !string.IsNullOrWhiteSpace(b.ButtonText)) == true)
                                    {
                                        _logger.LogInformation("🛑 Template node has buttons – halting flow for user click.");
                                        return;
                                    }
                                }

                            
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "❌ Failed to parse template config for button detection.");
                            }
                            break;

                        case "tag":
                            await ExecuteTagNodeAsync(businessId, contactId, node);
                            break;

                        case "wait":
                            try
                            {
                                var waitCfg = JsonConvert.DeserializeObject<WaitConfig>(node.ConfigJson ?? "{}");
                                var delayMs = (waitCfg?.Seconds ?? 1) * 1000;
                                _logger.LogInformation("⏳ Wait node delay: {Seconds}s", waitCfg?.Seconds ?? 1);
                                await Task.Delay(delayMs);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "❌ Failed to parse wait config for node {NodeId}", node.Id);
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Error while executing node {NodeId} of type {NodeType}", node.Id, node.NodeType);
                }

                currentNodeId = edgeLookup.TryGetValue(currentNodeId, out var next)
                    ? next.FirstOrDefault()?.TargetNodeId
                    : null;
            }
        }

        private bool TryNodeHasButtons(string? configJson)
        {
            if (string.IsNullOrWhiteSpace(configJson)) return false;

            try
            {
                var config = JsonConvert.DeserializeObject<Dictionary<string, object>>(configJson);
                if (config != null && config.TryGetValue("multiButtons", out var rawButtons))
                {
                    var buttons = JsonConvert.DeserializeObject<List<object>>(rawButtons.ToString() ?? "[]");
                    return buttons.Count > 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "⚠️ Failed to parse buttons from template config");
            }

            return false;
        }

        private async Task LogAutoReplyAsync(Guid businessId, Guid contactId, string keyword, string type, string replyText, string? flowName, Guid? messageLogId)
        {
            var log = new AutoReplyLog
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId,
                ContactId = contactId,
                TriggerKeyword = keyword,
                TriggerType = type,
                ReplyContent = replyText,
                FlowName = flowName,
                MessageLogId = messageLogId,
                TriggeredAt = DateTime.UtcNow
            };

            _context.AutoReplyLogs.Add(log);
            await _context.SaveChangesAsync();
        }


        //    private async Task ExecuteTemplateNodeAsync(
        //    AutoReplyFlowNode node,
        //    Guid businessId,
        //    Guid contactId,
        //    string phone,
        //    string keyword,
        //    string? flowName)
        //    {
        //        _logger.LogInformation("🧠 Raw config JSON for template node: {Json}", node.ConfigJson);

        //        TemplateConfig? tmpl;
        //        try
        //        {
        //            tmpl = JsonConvert.DeserializeObject<TemplateConfig>(node.ConfigJson ?? "{}");
        //        }
        //        catch (Exception ex)
        //        {
        //            _logger.LogError(ex, "❌ Failed to deserialize TemplateConfig for node {NodeId}", node.Id);
        //            return;
        //        }

        //        if (tmpl == null || string.IsNullOrWhiteSpace(tmpl.TemplateName))
        //        {
        //            _logger.LogWarning("❌ Template node config is missing or invalid.");
        //            return;
        //        }

        //        var contact = await _context.Contacts
        //            .FirstOrDefaultAsync(c => c.Id == contactId && c.BusinessId == businessId);

        //        if (contact == null)
        //        {
        //            _logger.LogWarning("❌ Contact not found for AutoReply.");
        //            return;
        //        }

        //        //var buttons = tmpl.MultiButtons?.Select(b => new CampaignButton
        //        //{
        //        //    Title = b.ButtonText,
        //        //    Type = b.ButtonType,
        //        //    Value = b.TargetUrl
        //        //}).ToList();
        //        var buttons = tmpl.MultiButtons?
        //.Where(b => !string.IsNullOrWhiteSpace(b.ButtonText)) // ✅ Avoid empty
        //.Select((b, idx) => new
        //{
        //    type = "button",
        //    sub_type = b.ButtonType.ToLowerInvariant(), // must be 'quick_reply' or 'url'
        //    index = idx.ToString(),
        //    parameters = new List<object>
        //    {
        //        new {
        //            type = "text",
        //            text = b.ButtonText
        //        }
        //    }
        //}).ToList();

        //        var response = await _templateSender.SendTemplateMessageToContactAsync(
        //            businessId: businessId,
        //            contact: contact,
        //            templateName: tmpl.TemplateName,
        //            templateParams: tmpl.Placeholders ?? new List<string>(),
        //            imageUrl: tmpl.ImageUrl,
        //            buttons: buttons,
        //            source: "auto_reply",
        //            refMessageId: null
        //        );

        //        await LogAutoReplyAsync(
        //            businessId,
        //            contactId,
        //            keyword,
        //            "flow",
        //            $"Template: {tmpl.TemplateName}",
        //            flowName,
        //            response.LogId
        //        );
        //    }


        private async Task ExecuteTemplateNodeAsync(
    AutoReplyFlowNode node,
    Guid businessId,
    Guid contactId,
    string phone,
    string keyword,
    string? flowName)
        {
            _logger.LogInformation("🧠 Raw config JSON for template node: {Json}", node.ConfigJson);

            TemplateConfig? tmpl;
            try
            {
                tmpl = JsonConvert.DeserializeObject<TemplateConfig>(node.ConfigJson ?? "{}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to deserialize TemplateConfig for node {NodeId}", node.Id);
                return;
            }

            if (tmpl == null || string.IsNullOrWhiteSpace(tmpl.TemplateName))
            {
                _logger.LogWarning("❌ Template node config is missing or invalid.");
                return;
            }

            var contact = await _context.Contacts
                .FirstOrDefaultAsync(c => c.Id == contactId && c.BusinessId == businessId);

            if (contact == null)
            {
                _logger.LogWarning("❌ Contact not found for AutoReply.");
                return;
            }

            var buttons = tmpl.MultiButtons?
                .Where(b => !string.IsNullOrWhiteSpace(b.ButtonText))
                .Select(b => new CampaignButton
                {
                    Title = b.ButtonText,
                    Type = b.ButtonType,
                    Value = b.TargetUrl
                })
                .ToList();

            var response = await _templateSender.SendTemplateMessageToContactAsync(
                businessId: businessId,
                contact: contact,
                templateName: tmpl.TemplateName,
                templateParams: tmpl.Placeholders ?? new List<string>(),
                imageUrl: tmpl.ImageUrl,
                buttons: buttons,
                source: "auto_reply",
                refMessageId: null
            );

            await LogAutoReplyAsync(
                businessId,
                contactId,
                keyword,
                "flow",
                $"Template: {tmpl.TemplateName}",
                flowName,
                response.LogId
            );
        }

        private async Task ExecuteMessageNodeAsync( AutoReplyFlowNode node, Guid businessId, Guid contactId,string phone, string keyword, string? flowName)
        {
            _logger.LogInformation("🧠 Raw config JSON for message node: {Json}", node.ConfigJson);

            MessageConfig? config = null;
            try
            {
                config = JsonConvert.DeserializeObject<MessageConfig>(node.ConfigJson ?? "{}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to parse config for message node {NodeId}", node.Id);
                return;
            }

            if (config == null || string.IsNullOrWhiteSpace(config.Text))
            {
                _logger.LogWarning("⚠️ Message node config missing or empty.");
                return;
            }

            var result = await _messageEngine.SendTextDirectAsync(new TextMessageSendDto
            {
                BusinessId = businessId,
                RecipientNumber = phone,
                TextContent = config.Text
            });

            await LogAutoReplyAsync(
                businessId,
                contactId,
                keyword,
                "flow",
                config.Text,
                flowName,
                result?.LogId
            );
        }
        private async Task ExecuteTagNodeAsync(Guid businessId, Guid contactId, AutoReplyFlowNode node)
        {
            if (node == null || string.IsNullOrWhiteSpace(node.ConfigJson))
                return;

            try
            {
                var config = System.Text.Json.JsonSerializer.Deserialize<TagNodeConfig>(node.ConfigJson);

                if (config?.Tags != null && config.Tags.Any())
                {
                    // ✅ Load contact from DB
                    var contact = await _context.Contacts.FirstOrDefaultAsync(c => c.Id == contactId && c.BusinessId == businessId);
                    if (contact == null)
                    {
                        _logger.LogWarning("⚠️ TagNode: Contact not found for {ContactId}", contactId);
                        return;
                    }

                    await _tagService.AssignTagsAsync(businessId, contact.PhoneNumber, config.Tags);
                    _logger.LogInformation("✅ TagNode: Tags [{Tags}] assigned to contact {Phone}", string.Join(", ", config.Tags), contact.PhoneNumber);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ TagNode: Failed to execute for contact {ContactId}", contactId);
            }
        }

        public async Task TryRunAutoReplyFlowByButtonAsync(Guid businessId, string phone, string buttonText, Guid? refMessageId = null)
        {
            var contact = await _contactService.FindOrCreateAsync(businessId, phone);
            if (contact == null)
            {
                _logger.LogWarning("❌ Contact not found or could not be created for phone: {Phone}", phone);
                return;
            }

            _logger.LogInformation("📩 Button clicked: '{ButtonText}' by {Phone}", buttonText, phone);

            var flows = await _flowRepo.GetAllByBusinessIdAsync(businessId);
            if (flows == null || !flows.Any())
            {
                _logger.LogInformation("📭 No flows found for business {BusinessId}", businessId);
                return;
            }

            foreach (var flow in flows)
            {
                var nodes = await _flowRepo.GetNodesByFlowIdAsync(flow.Id);
                var edges = await _flowRepo.GetEdgesByFlowIdAsync(flow.Id);

                var matchedStartNode = nodes
                    .Where(n => n.NodeType == "start")
                    .FirstOrDefault(n =>
                    {
                        try
                        {
                            var config = JsonConvert.DeserializeObject<Dictionary<string, object>>(n.ConfigJson ?? "{}");

                            if (config != null && config.TryGetValue("triggerKeywords", out var raw))
                            {
                                var keywordArray = JsonConvert.DeserializeObject<List<string>>(raw.ToString() ?? "[]");

                                return keywordArray.Any(k =>
                                    string.Equals(k?.Trim(), buttonText.Trim(), StringComparison.OrdinalIgnoreCase));
                            }

                            return false;
                        }
                        catch
                        {
                            return false;
                        }
                    });

                if (matchedStartNode != null)
                {
                    _logger.LogInformation("✅ Matched flow {FlowName} by button '{ButtonText}'", flow.Name, buttonText);
                   // await RunFlowAsync(flow.Id, businessId, contact.Id, phone, buttonText, flow.Name);
                  await RunFlowFromButtonAsync(flow.Id, businessId, contact.Id, phone, buttonText);
                    return;
                }
            }

            _logger.LogInformation("❌ No flow matched for button: {ButtonText}", buttonText);
        }
        public async Task RunFlowFromButtonAsync(Guid flowId, Guid businessId, Guid contactId, string phone, string buttonText)
        {
            var nodes = await _flowRepo.GetNodesByFlowIdAsync(flowId);
            var edges = await _flowRepo.GetEdgesByFlowIdAsync(flowId);

            var nodeMap = nodes.ToDictionary(n => n.Id.ToString(), n => n);
            var edgeMap = edges.GroupBy(e => e.SourceNodeId)
                               .ToDictionary(g => g.Key, g => g.ToList());

            // 🟢 1. Find start node
            var startNode = nodes.FirstOrDefault(n => n.NodeType == "start");
            if (startNode == null)
            {
                _logger.LogWarning("❌ No start node found in flow {FlowId}", flowId);
                return;
            }

            // 🔍 2. Find button index from triggerKeywords
            int matchedIndex = -1;
            try
            {
                var config = JsonConvert.DeserializeObject<Dictionary<string, object>>(startNode.ConfigJson ?? "{}");

                if (config != null && config.TryGetValue("triggerKeywords", out var raw))
                {
                    var keywordList = JsonConvert.DeserializeObject<List<string>>(raw.ToString() ?? "[]");
                    matchedIndex = keywordList.FindIndex(k =>
                        string.Equals(k?.Trim(), buttonText.Trim(), StringComparison.OrdinalIgnoreCase));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to parse Start node config");
                return;
            }

            if (matchedIndex < 0)
            {
                _logger.LogWarning("❌ No trigger match for buttonText '{Button}'", buttonText);
                return;
            }

            // ✅ 3. Lookup edge from StartNode using SourceHandle = button-{index}
            var nextNodeId = edgeMap.TryGetValue(startNode.Id.ToString(), out var list)
                ? list.FirstOrDefault(e => e.SourceHandle == $"button-{matchedIndex}")?.TargetNodeId
                : null;

            if (string.IsNullOrEmpty(nextNodeId))
            {
                _logger.LogWarning("❌ No outgoing edge found for button index {Index}", matchedIndex);
                return;
            }

            var visited = new HashSet<string>();

            while (!string.IsNullOrEmpty(nextNodeId) && !visited.Contains(nextNodeId))
            {
                visited.Add(nextNodeId);

                if (!nodeMap.TryGetValue(nextNodeId, out var node))
                    break;

                _logger.LogInformation("⚙️ Executing node {NodeId} [{NodeType}]", node.Id, node.NodeType);

                try
                {
                    switch (node.NodeType)
                    {
                        case "message":
                            await ExecuteMessageNodeAsync(node, businessId, contactId, phone, buttonText, null);
                            break;

                        case "template":
                            await ExecuteTemplateNodeAsync(node, businessId, contactId, phone, buttonText, null);

                            // ✅ Check buttons and halt if any button exists
                            try
                            {
                                var cfg = JsonConvert.DeserializeObject<TemplateConfig>(node.ConfigJson ?? "{}");
                                if (cfg?.MultiButtons?.Any(b => !string.IsNullOrWhiteSpace(b.ButtonText)) == true)
                                {
                                    _logger.LogInformation("🛑 Template node has buttons – halting flow for user click.");
                                    return;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "❌ Failed to parse template config for button detection.");
                            }
                            break;


                        case "tag":
                            await ExecuteTagNodeAsync(businessId, contactId, node);
                            break;

                        case "wait":
                            var waitCfg = JsonConvert.DeserializeObject<WaitConfig>(node.ConfigJson ?? "{}");
                            await Task.Delay((waitCfg?.Seconds ?? 1) * 1000);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Error executing node {NodeId}", node.Id);
                }

                nextNodeId = edgeMap.TryGetValue(nextNodeId, out var nextList)
                    ? nextList.FirstOrDefault()?.TargetNodeId
                    : null;
            }
        }

    }
}




