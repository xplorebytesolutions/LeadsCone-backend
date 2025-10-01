using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using xbytechat.api;
using xbytechat.api.Features.BusinessModule.Models;
using xbytechat.api.Features.MessagesEngine.Abstractions;
using xbytechat.api.Features.MessagesEngine.Providers;
using xbytechat_api.WhatsAppSettings.Models;
using static System.Net.WebRequestMethods;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace xbytechat.api.Features.MessagesEngine.Factory
{
    public class WhatsAppProviderFactory : IWhatsAppProviderFactory
    {
        private readonly IServiceProvider _sp;
        private readonly AppDbContext _db;
        private readonly ILogger<WhatsAppProviderFactory> _logger;

        public WhatsAppProviderFactory(IServiceProvider sp, AppDbContext db, ILogger<WhatsAppProviderFactory> logger)
        {
            _sp = sp;
            _db = db;
            _logger = logger;
        }

        public async Task<IWhatsAppProvider> CreateAsync(Guid businessId)
        {
            var setting = await _db.WhatsAppSettings
                .FirstOrDefaultAsync(x => x.BusinessId == businessId && x.IsActive)
                ?? throw new InvalidOperationException("WhatsApp settings not found for this business.");

            var providerKey = (setting.Provider ?? "meta_cloud").Trim().ToLowerInvariant();

            using var scope = _sp.CreateScope();

            var httpClientFactory = scope.ServiceProvider.GetService<IHttpClientFactory>();
            var http =
                httpClientFactory != null
                    ? httpClientFactory.CreateClient(providerKey == "meta_cloud" ? "wa:meta_cloud" : "wa:pinnacle")
                    : scope.ServiceProvider.GetRequiredService<HttpClient>();

            return providerKey switch
            {
                //"pinnacle" =>
                //            new PinnacleProvider(http, scope.ServiceProvider.GetRequiredService<ILogger<PinnacleProvider>>(), setting),
                "pinnacle" => new PinnacleProvider(http, scope.ServiceProvider.GetRequiredService<ILogger<PinnacleProvider>>(), setting),
                "meta_cloud" =>
                    new MetaCloudProvider(_db, http, scope.ServiceProvider.GetRequiredService<ILogger<MetaCloudProvider>>(), setting),

                _ => throw new NotSupportedException($"Unsupported WhatsApp provider: {providerKey}")
            };
        }
        //public async Task<IWhatsAppProvider> CreateAsync(Guid businessId, string? phoneNumberId)
        //{
        //    var setting = await _db.WhatsAppSettings
        //        .FirstOrDefaultAsync(x => x.BusinessId == businessId && x.IsActive)
        //        ?? throw new InvalidOperationException("WhatsApp settings not found for this business.");

        //    // per-send override of the sender number (in-memory only)
        //    if (!string.IsNullOrWhiteSpace(phoneNumberId))
        //        setting.PhoneNumberId = phoneNumberId.Trim();

        //    var providerKey = (setting.Provider ?? "meta_cloud").Trim().ToLowerInvariant();

        //    using var scope = _sp.CreateScope();

        //    var httpClientFactory = scope.ServiceProvider.GetService<IHttpClientFactory>();
        //    var http =
        //        httpClientFactory != null
        //            ? httpClientFactory.CreateClient(providerKey == "meta_cloud" ? "wa:meta_cloud" : "wa:pinnacle")
        //            : scope.ServiceProvider.GetRequiredService<HttpClient>();

        //    return providerKey switch
        //    {
        //        "pinnacle" => new PinnacleProvider(http, scope.ServiceProvider.GetRequiredService<ILogger<PinnacleProvider>>(), setting),
        //        "meta_cloud" => new MetaCloudProvider(_db, http, scope.ServiceProvider.GetRequiredService<ILogger<MetaCloudProvider>>(), setting),
        //        _ => throw new NotSupportedException($"Unsupported WhatsApp provider: {providerKey}")
        //    };
        //}
        public async Task<IWhatsAppProvider> CreateAsync(Guid businessId, string provider, string? phoneNumberId)
        {
            if (string.IsNullOrWhiteSpace(provider))
                throw new ArgumentException("Provider is required.", nameof(provider));
            if (provider is not "PINNACLE" and not "META_CLOUD")
                throw new NotSupportedException($"Unsupported provider: {provider}");

            // If a sender was chosen, ensure it belongs to THIS business+provider
            if (!string.IsNullOrWhiteSpace(phoneNumberId))
            {
                var exists = await _db.WhatsAppPhoneNumbers.AsNoTracking().AnyAsync(n =>
                    n.BusinessId == businessId && n.Provider == provider && n.PhoneNumberId == phoneNumberId);
                if (!exists)
                    throw new InvalidOperationException("Selected PhoneNumberId does not belong to this provider/business.");
            }

            // Load the settings row for the exact (BusinessId, Provider)
            var setting = await _db.WhatsAppSettings.FirstOrDefaultAsync(s =>
                s.BusinessId == businessId && s.Provider == provider && s.IsActive)
                ?? throw new InvalidOperationException($"WhatsApp settings not found for provider {provider}.");

            // Per-send override – transient only
            if (!string.IsNullOrWhiteSpace(phoneNumberId))
                setting.PhoneNumberId = phoneNumberId.Trim();

            if (string.IsNullOrWhiteSpace(setting.ApiUrl))
                throw new InvalidOperationException("API URL is empty. Save provider settings first.");
            if (string.IsNullOrWhiteSpace(setting.ApiKey))
                throw new InvalidOperationException("API Key/Token is empty. Save provider settings first.");

            using var scope = _sp.CreateScope();
            var http = scope.ServiceProvider
                .GetRequiredService<IHttpClientFactory>()
                .CreateClient(provider == "META_CLOUD" ? "wa:meta_cloud" : "wa:pinnacle");

            return provider switch
            {
                "PINNACLE" => new PinnacleProvider(http, scope.ServiceProvider.GetRequiredService<ILogger<PinnacleProvider>>(), setting),
                "META_CLOUD" => new MetaCloudProvider(_db, http, scope.ServiceProvider.GetRequiredService<ILogger<MetaCloudProvider>>(), setting),
                _ => throw new NotSupportedException($"Unsupported provider: {provider}")
            };
        }

    }
}


//// 📄 File: Features/MessagesEngine/Factory/WhatsAppProviderFactory.cs
//using System;
//using System.Threading.Tasks;
//using Microsoft.EntityFrameworkCore;
//using Microsoft.Extensions.DependencyInjection;
//using Microsoft.Extensions.Logging;
//using xbytechat.api;
//using xbytechat.api.Features.MessagesEngine.Abstractions;
//using xbytechat.api.Features.MessagesEngine.Providers;

//namespace xbytechat.api.Features.MessagesEngine.Factory
//{

//    public class WhatsAppProviderFactory : IWhatsAppProviderFactory
//    {
//        private readonly IServiceProvider _sp;
//        private readonly AppDbContext _db;
//        private readonly ILogger<WhatsAppProviderFactory> _logger;

//        public WhatsAppProviderFactory(IServiceProvider sp, AppDbContext db, ILogger<WhatsAppProviderFactory> logger)
//        {
//            _sp = sp;
//            _db = db;
//            _logger = logger;
//        }

//        public async Task<IWhatsAppProvider> CreateAsync(Guid businessId)
//        {
//            var setting = await _db.WhatsAppSettings.FirstOrDefaultAsync(x => x.BusinessId == businessId && x.IsActive)
//                          ?? throw new InvalidOperationException("WhatsApp settings not found for this business.");

//            var providerKey = (setting.Provider ?? "meta_cloud").Trim().ToLowerInvariant();

//            // Create a new scope to inject the per-tenant setting into provider constructor
//            var scope = _sp.CreateScope();
//            var http = scope.ServiceProvider.GetRequiredService<HttpClient>();

//            return providerKey switch
//            {
//                "pinnacle" => new PinbotProvider(http, scope.ServiceProvider.GetRequiredService<ILogger<PinbotProvider>>(), setting),
//                "meta_cloud" => new MetaCloudProvider(_db, http, scope.ServiceProvider.GetRequiredService<ILogger<MetaCloudProvider>>(), setting),
//                _ => throw new NotSupportedException($"Unsupported WhatsApp provider: {providerKey}")
//            };
//        }
//    }
//}
