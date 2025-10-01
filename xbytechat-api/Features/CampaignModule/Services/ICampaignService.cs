using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using xbytechat.api.Features.CampaignModule.DTOs;
using xbytechat.api.Shared;
using xbytechat.api.CRM.Dtos;
using xbytechat.api.Helpers;
using xbytechat.api.Features.CampaignModule.Models;

namespace xbytechat.api.Features.CampaignModule.Services
{
    public interface ICampaignService
    {
        /// 🆕 Create a new campaign with recipients
        Task<Guid?> CreateTextCampaignAsync(CampaignCreateDto dto, Guid businessId, string createdBy);

        /// ✏️ Update an existing draft campaign
        Task<bool> UpdateCampaignAsync(Guid id, CampaignCreateDto dto);

        /// 🗑️ Soft-delete a draft campaign
        Task<bool> DeleteCampaignAsync(Guid id);

        /// 📋 Get all campaigns for the business
        Task<List<CampaignSummaryDto>> GetAllCampaignsAsync(Guid businessId);

        /// 📄 Get paginated campaigns
        Task<PaginatedResponse<CampaignSummaryDto>> GetPaginatedCampaignsAsync(Guid businessId, PaginatedRequest request);
        /// 🚀 Trigger campaign send flow (template message to all recipients)
        Task<bool> SendCampaignAsync(Guid campaignId, string ipAddress, string userAgent);
        Task<Guid> CreateImageCampaignAsync(Guid businessId, CampaignCreateDto dto, string createdBy);
        Task<List<CampaignSummaryDto>> GetAllCampaignsAsync(Guid businessId, string? type = null);
        Task<List<ContactDto>> GetRecipientsByCampaignIdAsync(Guid campaignId, Guid businessId);
        Task<bool> RemoveRecipientAsync(Guid businessId, Guid campaignId, Guid contactId);
        Task<CampaignDto?> GetCampaignByIdAsync(Guid campaignId, Guid businessId);
        Task<bool> AssignContactsToCampaignAsync(Guid campaignId, Guid businessId, List<Guid> contactIds);

        Task<ResponseResult> SendTemplateCampaignAsync(Guid campaignId);

        Task<ResponseResult> SendTemplateCampaignWithTypeDetectionAsync(Guid campaignId);

        Task<ResponseResult> SendTextTemplateCampaignAsync(Campaign campaign);
        Task<ResponseResult> SendImageTemplateCampaignAsync(Campaign campaign);

        Task<List<FlowListItemDto>> GetAvailableFlowsAsync(Guid businessId, bool onlyPublished = true);

        Task<CampaignDryRunResponseDto> DryRunTemplateCampaignAsync(Guid campaignId, int maxRecipients = 20);
        Task<object> SendVideoTemplateMessageAsync(VideoTemplateMessageDto dto, Guid businessId);

    }
}
