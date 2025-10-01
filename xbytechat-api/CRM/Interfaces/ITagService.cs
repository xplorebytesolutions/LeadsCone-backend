using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using xbytechat.api.CRM.Dtos;

namespace xbytechat.api.CRM.Interfaces
{
    public interface ITagService
    {
        Task<TagDto> AddTagAsync(Guid businessId, TagDto dto);

        Task<IEnumerable<TagDto>> GetAllTagsAsync(Guid businessId);
        Task<bool> UpdateTagAsync(Guid businessId, Guid tagId, TagDto dto);
        Task<bool> DeleteTagAsync(Guid businessId, Guid tagId);
       // Task AssignTagAsync(Guid businessId, string phone, string tag);
        Task AssignTagsAsync(Guid businessId, string phoneNumber, List<string> tagNames);
    }
}
