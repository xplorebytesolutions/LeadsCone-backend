using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Numerics;
using System.Text.Json;
using xbytechat.api.CRM.Interfaces;
using xbytechat.api.Features.AutoReplyBuilder.Flows.DTOs;
using xbytechat.api.Features.AutoReplyBuilder.Flows.DTOs.FlowNodeConfigs;
using xbytechat.api.Features.AutoReplyBuilder.Flows.Models;
using xbytechat.api.Features.AutoReplyBuilder.Flows.Repositories;
using xbytechat.api.Features.AutoReplyBuilder.Models;
using xbytechat.api.Features.AutoReplyBuilder.Repositories;
using xbytechat.api.Features.MessagesEngine.DTOs;
using xbytechat.api.Features.MessagesEngine.Services;

namespace xbytechat.api.Features.AutoReplyBuilder.Flows.Services
{
    public class AutoReplyFlowService : IAutoReplyFlowService
    {
        private readonly IAutoReplyFlowRepository _flowRepository;
        private readonly ILogger<AutoReplyFlowService> _logger;
        private readonly IMessageEngineService _messageService;
        private readonly ITagService _tagService;
        private readonly IAutoReplyRepository _autoReplyRepository;
        public AutoReplyFlowService(IAutoReplyFlowRepository flowrepository, ILogger<AutoReplyFlowService> logger,
            IMessageEngineService messageService, ITagService tagService, IAutoReplyRepository autoReplyRepository)
        {
            _flowRepository = flowrepository;
            _logger = logger;
            _messageService = messageService;
            _tagService = tagService;
            _autoReplyRepository = autoReplyRepository;
        }

        //public async Task<Guid> SaveFlowAsync(SaveFlowDto dto)
        //{
        //    _logger.LogInformation("🔄 Starting flow save for business {BusinessId} with keyword '{Keyword}'", dto.BusinessId, dto.TriggerKeyword);

        //    var flow = new AutoReplyFlow
        //    {
        //        Id = Guid.NewGuid(),
        //        BusinessId = dto.BusinessId,
        //        Name = dto.Name,
        //        NodesJson = JsonConvert.SerializeObject(dto.Nodes),
        //        EdgesJson = JsonConvert.SerializeObject(dto.Edges),
        //        TriggerKeyword = dto.TriggerKeyword?.Trim().ToLower(),
        //        IsActive = true,
        //        CreatedAt = DateTime.UtcNow
        //    };

        //    var saved = await _flowRepository.SaveAsync(flow);
        //    _logger.LogInformation("✅ Flow saved: {FlowId}", saved.Id);

        //    // ✅ Save parsed nodes
        //    var parsedNodes = new List<AutoReplyFlowNode>();
        //    var nodes = dto.Nodes as List<Dictionary<string, object>>;

        //    if (nodes != null)
        //    {
        //        foreach (var nodeDict in nodes)
        //        {
        //            if (!nodeDict.ContainsKey("type") || !nodeDict.ContainsKey("data") || !nodeDict.ContainsKey("position"))
        //            {
        //                _logger.LogWarning("⚠️ Skipped malformed node during flow save: {Node}", JsonConvert.SerializeObject(nodeDict));
        //                continue;
        //            }

        //            var data = nodeDict["data"] as Dictionary<string, object>;
        //            var positionDict = nodeDict["position"] as Dictionary<string, object>;

        //            var position = new Position
        //            {
        //                X = Convert.ToDouble(positionDict?["x"] ?? 0),
        //                Y = Convert.ToDouble(positionDict?["y"] ?? 0)
        //            };

        //            parsedNodes.Add(new AutoReplyFlowNode
        //            {
        //                Id = Guid.NewGuid(),
        //                FlowId = saved.Id,
        //                NodeType = nodeDict["type"]?.ToString() ?? "",
        //                Label = data?["label"]?.ToString() ?? "",
        //                ConfigJson = JsonConvert.SerializeObject(data?["config"] ?? new { }),
        //                Position = position // ✅ strongly typed
        //            });
        //        }
        //    }

        //    _logger.LogInformation("🧩 Parsed {NodeCount} nodes", parsedNodes.Count);

        //    // ✅ Save parsed edges
        //    var parsedEdges = new List<AutoReplyFlowEdge>();
        //    var edges = dto.Edges as List<Dictionary<string, object>>;

        //    if (edges != null)
        //    {
        //        foreach (var edgeDict in edges)
        //        {
        //            if (!edgeDict.ContainsKey("source") || !edgeDict.ContainsKey("target"))
        //            {
        //                _logger.LogWarning("⚠️ Skipped malformed edge during flow save: {Edge}", JsonConvert.SerializeObject(edgeDict));
        //                continue;
        //            }

        //            parsedEdges.Add(new AutoReplyFlowEdge
        //            {
        //                Id = Guid.NewGuid(),
        //                FlowId = saved.Id,
        //                SourceNodeId = edgeDict["source"]?.ToString() ?? "",
        //                TargetNodeId = edgeDict["target"]?.ToString() ?? "",
        //                CreatedAt = DateTime.UtcNow
        //            });
        //        }
        //    }

        //    _logger.LogInformation("🔗 Parsed {EdgeCount} edges", parsedEdges.Count);

        //    await _flowRepository.SaveNodesAndEdgesAsync(parsedNodes, parsedEdges);

        //    _logger.LogInformation("✅ Node + edge persistence complete for flow {FlowId}", saved.Id);

        //    return saved.Id;
        //}

        //public async Task<Guid> SaveFlowAsync(SaveFlowDto dto)
        //{
        //    _logger.LogInformation("🔄 Starting flow save for business {BusinessId} with keyword '{Keyword}'", dto.BusinessId, dto.TriggerKeyword);

        //    // ✅ Step 1: Save main flow
        //    var flow = new AutoReplyFlow
        //    {
        //        Id = Guid.NewGuid(),
        //        BusinessId = dto.BusinessId,
        //        Name = dto.Name?.Trim() ?? "",
        //        NodesJson = JsonConvert.SerializeObject(dto.Nodes),
        //        EdgesJson = JsonConvert.SerializeObject(dto.Edges),
        //        TriggerKeyword = dto.TriggerKeyword?.Trim().ToLower(),
        //        IsActive = true,
        //        CreatedAt = DateTime.UtcNow
        //    };

        //    var saved = await _flowRepository.SaveAsync(flow);
        //    _logger.LogInformation("✅ Flow saved: {FlowId}", saved.Id);

        //    // ✅ Step 2: Build ID map and parse nodes
        //    var nodeIdMap = new Dictionary<string, Guid>();
        //    var parsedNodes = new List<AutoReplyFlowNode>();

        //    foreach (var n in dto.Nodes)
        //    {
        //        if (string.IsNullOrWhiteSpace(n.Id))
        //        {
        //            _logger.LogWarning("⚠️ Skipped node with missing Id");
        //            continue;
        //        }

        //        var internalNodeId = Guid.NewGuid();
        //        nodeIdMap[n.Id] = internalNodeId;

        //        parsedNodes.Add(new AutoReplyFlowNode
        //        {
        //            Id = internalNodeId,
        //            FlowId = saved.Id,
        //            NodeType = n.Type,
        //            Label = n.Data?.Label ?? "",
        //            ConfigJson = JsonConvert.SerializeObject(n.Data?.Config ?? new { }),
        //            Position = new Position
        //            {
        //                X = n.Position?.X ?? 0,
        //                Y = n.Position?.Y ?? 0
        //            },
        //            CreatedAt = DateTime.UtcNow
        //        });
        //    }

        //    _logger.LogInformation("🧩 Parsed {NodeCount} nodes", parsedNodes.Count);

        //    // ✅ Step 3: Map Source/TargetNodeId from external → internal GUIDs
        //    var parsedEdges = new List<AutoReplyFlowEdge>();

        //    foreach (var e in dto.Edges)
        //    {
        //        if (!nodeIdMap.TryGetValue(e.SourceNodeId ?? "", out var sourceId))
        //        {
        //            _logger.LogWarning("⚠️ Edge skipped: SourceNodeId '{Source}' not found", e.SourceNodeId);
        //            continue;
        //        }

        //        if (!nodeIdMap.TryGetValue(e.TargetNodeId ?? "", out var targetId))
        //        {
        //            _logger.LogWarning("⚠️ Edge skipped: TargetNodeId '{Target}' not found", e.TargetNodeId);
        //            continue;
        //        }

        //        parsedEdges.Add(new AutoReplyFlowEdge
        //        {
        //            Id = Guid.NewGuid(),
        //            FlowId = saved.Id,
        //            SourceNodeId = sourceId.ToString(),
        //            TargetNodeId = targetId.ToString(),
        //            CreatedAt = DateTime.UtcNow
        //        });
        //    }

        //    _logger.LogInformation("🔗 Parsed {EdgeCount} edges", parsedEdges.Count);

        //    // ✅ Final Save
        //    await _flowRepository.SaveNodesAndEdgesAsync(parsedNodes, parsedEdges);
        //    _logger.LogInformation("✅ Node + edge persistence complete for flow {FlowId}", saved.Id);

        //    return saved.Id;
        //}
        //public async Task<Guid> SaveFlowAsync(SaveFlowDto dto)
        //{
        //    _logger.LogInformation("🔄 Starting flow save for business {BusinessId} with keyword '{Keyword}'", dto?.BusinessId, dto?.TriggerKeyword);

        //    if (dto == null) throw new ArgumentNullException(nameof(dto));
        //    if (dto.BusinessId == Guid.Empty) throw new ArgumentException("BusinessId is required.");
        //    if (string.IsNullOrWhiteSpace(dto.TriggerKeyword)) throw new ArgumentException("TriggerKeyword is required.");
        //    if (dto.Nodes == null || !dto.Nodes.Any()) throw new ArgumentException("At least one node is required.");

        //    dto.Edges ??= new List<EdgeDto>();

        //    // ✅ Step 1: Save Flow
        //    var flow = new AutoReplyFlow
        //    {
        //        Id = Guid.NewGuid(),
        //        BusinessId = dto.BusinessId,
        //        Name = dto.Name?.Trim() ?? "",
        //        NodesJson = JsonConvert.SerializeObject(dto.Nodes),
        //        EdgesJson = JsonConvert.SerializeObject(dto.Edges),
        //        TriggerKeyword = dto.TriggerKeyword?.Trim().ToLower(),
        //        IsActive = true,
        //        CreatedAt = DateTime.UtcNow
        //    };

        //    var savedFlow = await _flowRepository.SaveAsync(flow);
        //    _logger.LogInformation("✅ Flow saved: {FlowId}", savedFlow.Id);

        //    // ✅ Step 2: Parse Nodes
        //    var nodeIdMap = new Dictionary<string, Guid>();
        //    var parsedNodes = new List<AutoReplyFlowNode>();

        //    foreach (var n in dto.Nodes)
        //    {
        //        if (string.IsNullOrWhiteSpace(n.Id)) continue;

        //        var internalNodeId = Guid.NewGuid();
        //        nodeIdMap[n.Id] = internalNodeId;

        //        string configJson = n.Data?.Config is JsonElement elem
        //            ? elem.GetRawText()
        //            : JsonConvert.SerializeObject(n.Data?.Config ?? new { });

        //        parsedNodes.Add(new AutoReplyFlowNode
        //        {
        //            Id = internalNodeId,
        //            FlowId = savedFlow.Id,
        //            NodeType = n.Type,
        //            Label = n.Data?.Label ?? "",
        //            ConfigJson = configJson,
        //            Position = new Position
        //            {
        //                X = n.Position?.X ?? 0,
        //                Y = n.Position?.Y ?? 0
        //            },
        //            CreatedAt = DateTime.UtcNow
        //        });
        //    }

        //    _logger.LogInformation("🧩 Parsed {NodeCount} nodes", parsedNodes.Count);

        //    // ✅ Step 3: Parse Edges
        //    var parsedEdges = new List<AutoReplyFlowEdge>();

        //    foreach (var e in dto.Edges)
        //    {
        //        if (!nodeIdMap.TryGetValue(e.SourceNodeId ?? "", out var sourceId)) continue;
        //        if (!nodeIdMap.TryGetValue(e.TargetNodeId ?? "", out var targetId)) continue;

        //        parsedEdges.Add(new AutoReplyFlowEdge
        //        {
        //            Id = Guid.NewGuid(),
        //            FlowId = savedFlow.Id,
        //            SourceNodeId = sourceId.ToString(),
        //            TargetNodeId = targetId.ToString(),
        //            SourceHandle = e.SourceHandle, 
        //            TargetHandle = e.TargetHandle, 
        //            CreatedAt = DateTime.UtcNow
        //        });
        //    }

        //    _logger.LogInformation("🔗 Parsed {EdgeCount} edges", parsedEdges.Count);

        //    // ✅ Step 4: Save Nodes + Edges
        //    await _flowRepository.SaveNodesAndEdgesAsync(parsedNodes, parsedEdges);
        //    _logger.LogInformation("✅ Node + edge persistence complete for flow {FlowId}", savedFlow.Id);

        //    // ✅ Step 5: Link to Rule via Repository
        //    var keyword = dto.TriggerKeyword.Trim().ToLower();
        //    var rule = await _autoReplyRepository.UpsertRuleLinkedToFlowAsync(dto.BusinessId, keyword, savedFlow.Id, dto.Name);

        //    _logger.LogInformation("🔁 Linked flow to auto-reply rule: {RuleId}", rule.Id);

        //    return savedFlow.Id;
        //}
        public async Task<Guid> SaveFlowAsync(SaveFlowDto dto, Guid businessId)
        {
            _logger.LogInformation("🔄 Starting flow save for business {BusinessId} with keyword '{Keyword}'", businessId, dto?.TriggerKeyword);

            if (dto == null) throw new ArgumentNullException(nameof(dto));
            if (string.IsNullOrWhiteSpace(dto.TriggerKeyword)) throw new ArgumentException("TriggerKeyword is required.");
            if (dto.Nodes == null || !dto.Nodes.Any()) throw new ArgumentException("At least one node is required.");

            dto.Edges ??= new List<EdgeDto>();

            // ✅ Step 1: Save Flow
            var flow = new AutoReplyFlow
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId, // << Use parameter, not from dto!
                Name = dto.Name?.Trim() ?? "",
                NodesJson = JsonConvert.SerializeObject(dto.Nodes),
                EdgesJson = JsonConvert.SerializeObject(dto.Edges),
                TriggerKeyword = dto.TriggerKeyword?.Trim().ToLower(),
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            var savedFlow = await _flowRepository.SaveAsync(flow);
            _logger.LogInformation("✅ Flow saved: {FlowId}", savedFlow.Id);

            // ... rest of code unchanged ...

            // ✅ Step 5: Link to Rule via Repository
            var keyword = dto.TriggerKeyword.Trim().ToLower();
            var rule = await _autoReplyRepository.UpsertRuleLinkedToFlowAsync(
                businessId, // << Use parameter, not from dto!
                keyword, savedFlow.Id, dto.Name);

            _logger.LogInformation("🔁 Linked flow to auto-reply rule: {RuleId}", rule.Id);

            return savedFlow.Id;
        }

        public async Task<List<SaveFlowDto>> GetFlowsByBusinessIdAsync(Guid businessId)
        {
            _logger.LogInformation("📥 Fetching auto-reply flows for business {BusinessId}", businessId);

            var flows = await _flowRepository.GetAllByBusinessIdAsync(businessId);

            var results = flows.Select(f => new SaveFlowDto
            {
                Id = f.Id,
                BusinessId = f.BusinessId,
                Name = f.Name,
                Nodes = string.IsNullOrEmpty(f.NodesJson)
                    ? new()
                    : JsonConvert.DeserializeObject<List<NodeDto>>(f.NodesJson),

                Edges = string.IsNullOrEmpty(f.EdgesJson)
                    ? new()
                    : JsonConvert.DeserializeObject<List<EdgeDto>>(f.EdgesJson),


                CreatedAt = f.CreatedAt
            }).ToList();

            _logger.LogInformation("📤 Returned {Count} auto-reply flows for business {BusinessId}", results.Count, businessId);

            return results;
        }

        public async Task<SaveFlowDto?> GetFlowByIdAsync(Guid flowId, Guid businessId)
        {
            var flow = await _flowRepository.GetByIdAsync(flowId, businessId);
            if (flow == null)
            {
                _logger.LogWarning("❌ No flow found for FlowId {FlowId} and BusinessId {BusinessId}", flowId, businessId);
                return null;
            }

            var nodes = await _flowRepository.GetNodesByFlowIdAsync(flowId);
            var edges = await _flowRepository.GetEdgesByFlowIdAsync(flowId);

            var mappedNodes = nodes.Select(n => new Dictionary<string, object>
            {
                ["id"] = n.Id,
                ["type"] = n.NodeType,
                ["position"] = new Dictionary<string, object>
                {
                    ["x"] = n.Position?.X ?? 0,
                    ["y"] = n.Position?.Y ?? 0
                },
                ["data"] = new Dictionary<string, object>
                {
                    ["label"] = n.Label,
                    ["config"] = string.IsNullOrEmpty(n.ConfigJson)
                        ? null
                        : JsonConvert.DeserializeObject<object>(n.ConfigJson)
                }
            }).ToList();

            var mappedEdges = edges.Select(e => new Dictionary<string, object>
            {
                ["id"] = e.Id,
                ["source"] = e.SourceNodeId,
                ["target"] = e.TargetNodeId
            }).ToList();

            _logger.LogInformation("📤 Returning flow {FlowId} with {NodeCount} nodes and {EdgeCount} edges", flow.Id, mappedNodes.Count, mappedEdges.Count);

            return new SaveFlowDto
            {
                Id = flow.Id,
                BusinessId = flow.BusinessId,
                Name = flow.Name,
                Nodes = JsonConvert.DeserializeObject<List<NodeDto>>(JsonConvert.SerializeObject(mappedNodes)),
                Edges = JsonConvert.DeserializeObject<List<EdgeDto>>(JsonConvert.SerializeObject(mappedEdges)),
                CreatedAt = flow.CreatedAt
            };

        }

        public async Task<int> GetFlowCountForBusinessAsync(Guid businessId)
        {
            return await _flowRepository.GetFlowCountAsync(businessId);
        }

        public async Task<bool> RenameFlowAsync(Guid id, string newName)
        {
            return await _flowRepository.RenameFlowAsync(id, newName);
        }
        public async Task<bool> DeleteFlowAsync(Guid id, Guid businessId)
        {
            return await _flowRepository.DeleteFlowAsync(id, businessId);
        }
        public async Task ExecuteFlowAsync(Guid businessId, string triggerKeyword, string customerPhone)
        {
            var flow = await _flowRepository.FindFlowByKeywordAsync(businessId, triggerKeyword);
            if (flow == null) return;

            var nodes = await _flowRepository.GetNodesByFlowIdAsync(flow.Id);
            var edges = await _flowRepository.GetEdgesByFlowIdAsync(flow.Id);

            var nodeMap = nodes.ToDictionary(n => n.Id, n => n);
            var edgeMap = edges.GroupBy(e => e.SourceNodeId)
                               .ToDictionary(g => g.Key, g => g.ToList());

            var current = nodes.FirstOrDefault(n => n.NodeType == "start");
            while (current != null)
            {
                switch (current.NodeType)
                {
                    case "message":
                        var msgCfg = JsonConvert.DeserializeObject<MessageConfig>(current.ConfigJson);
                        await _messageService.SendTextDirectAsync(new TextMessageSendDto
                        {
                            BusinessId = businessId,
                            RecipientNumber = customerPhone,
                            TextContent = msgCfg.Text
                        });

                        break;
                    case "template":
                        var tempCfg = JsonConvert.DeserializeObject<TemplateConfig>(current.ConfigJson);

                        var dto = new SimpleTemplateMessageDto
                        {
                            RecipientNumber = customerPhone,
                            TemplateName = tempCfg.TemplateName,
                            TemplateParameters = tempCfg.Placeholders ?? new List<string>()
                        };

                        await _messageService.SendTemplateMessageSimpleAsync(businessId, dto);
                        break;


                    case "wait":
                        var waitCfg = JsonConvert.DeserializeObject<WaitConfig>(current.ConfigJson);
                        await Task.Delay(TimeSpan.FromSeconds(waitCfg.Seconds));
                        break;

                    case "tag":
                        var tagCfg = JsonConvert.DeserializeObject<TagNodeConfig>(current.ConfigJson);
                        await _tagService.AssignTagsAsync(businessId, customerPhone, tagCfg.Tags);
                        break;
                }

                var nextEdge = edgeMap.ContainsKey(current.Id.ToString())
                     ? edgeMap[current.Id.ToString()].FirstOrDefault()
                        : null;

                if (nextEdge == null) break;

                current = nodeMap.ContainsKey(Guid.Parse(nextEdge.TargetNodeId))
                    ? nodeMap[Guid.Parse(nextEdge.TargetNodeId)]
                    : null;

            }
        }
        public async Task TriggerAutoReplyAsync(Guid businessId, string incomingText, string phone)
        {
            // Step 1: Find flow matching keyword
            var flow = await _flowRepository.FindFlowByKeywordAsync(businessId, incomingText.ToLower());
            if (flow == null) return;

            // Step 2: Load flow nodes + edges
            var nodes = await _flowRepository.GetStructuredNodesAsync(flow.Id);
            var edges = await _flowRepository.GetStructuredEdgesAsync(flow.Id);

            // Step 3: Find start node
            var startNode = nodes.FirstOrDefault(n => n.NodeType == "start");
            if (startNode == null) return;

            var visited = new HashSet<string>();
            var currentNodeId = startNode.Id.ToString();

            while (!string.IsNullOrEmpty(currentNodeId) && !visited.Contains(currentNodeId))
            {
                visited.Add(currentNodeId);

                var currentNode = nodes.FirstOrDefault(n => n.Id.ToString() == currentNodeId);
                if (currentNode == null) break;

                // Step 4: Handle current node
                switch (currentNode.NodeType)
                {
                    case "message":
                        var config = JsonConvert.DeserializeObject<MessageConfig>(currentNode.ConfigJson);
                        await _messageService.SendTextDirectAsync(new TextMessageSendDto
                        {
                            BusinessId = businessId,
                            RecipientNumber = phone,
                            TextContent = config.Text
                        });
                        break;

                    case "template":
                        var tpl = JsonConvert.DeserializeObject<TemplateConfig>(currentNode.ConfigJson);

                        var dto = new SimpleTemplateMessageDto
                        {
                            RecipientNumber = phone,
                            TemplateName = tpl.TemplateName,
                            TemplateParameters = tpl.Placeholders ?? new List<string>()
                        };

                        await _messageService.SendTemplateMessageSimpleAsync(businessId, dto);
                        break;


                    case "wait":
                        var waitConfig = JsonConvert.DeserializeObject<WaitConfig>(currentNode.ConfigJson);
                        await Task.Delay(waitConfig.Seconds * 1000); // Can replace with async scheduling later
                        break;

                    case "tag":
                        var tagConfig = JsonConvert.DeserializeObject<TagNodeConfig>(currentNode.ConfigJson);
                        await _tagService.AssignTagsAsync(businessId,phone, tagConfig.Tags);
                        break;

                }

                // Step 5: Find next node
                var nextEdge = edges.FirstOrDefault(e => e.SourceNodeId == currentNodeId);
                currentNodeId = nextEdge?.TargetNodeId;
            }
        }


    }
}
