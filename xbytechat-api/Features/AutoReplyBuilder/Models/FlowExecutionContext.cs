using xbytechat.api.Features.AutoReplyBuilder.Flows.Models;

namespace xbytechat.api.Features.AutoReplyBuilder.Models
{
    public class FlowExecutionContext
    {
        public AutoReplyFlow Flow { get; set; } = null!;
        public Guid BusinessId { get; set; }
        public Guid ContactId { get; set; }
        public string ContactPhone { get; set; } = null!;
        public string SourceChannel { get; set; } = "whatsapp";
        public string IndustryTag { get; set; } = "";
    }
}
