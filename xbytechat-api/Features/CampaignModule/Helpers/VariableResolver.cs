using System;
using System.Collections.Generic;

namespace xbytechat.api.Features.CampaignModule.Helpers
{
    public sealed class VariableResolver : IVariableResolver
    {
        public Dictionary<string, string> ResolveVariables(
            IReadOnlyDictionary<string, string> rowData,
            IReadOnlyDictionary<string, string>? mappings)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (mappings == null || mappings.Count == 0)
            {
                foreach (var kv in rowData)
                    result[kv.Key.Trim()] = kv.Value?.Trim() ?? string.Empty;
                return result;
            }

            foreach (var (token, srcRaw) in mappings)
            {
                if (string.IsNullOrWhiteSpace(token)) continue;

                var src = srcRaw?.Trim() ?? string.Empty;
                if (src.StartsWith("constant:", StringComparison.OrdinalIgnoreCase))
                {
                    result[token] = src.Substring("constant:".Length).Trim();
                    continue;
                }

                if (rowData.TryGetValue(src, out var v) && v != null)
                    result[token] = v.Trim();
                else
                    result[token] = string.Empty;
            }

            return result;
        }
    }
}
