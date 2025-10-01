using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Exceptions;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using xbytechat.api;
using xbytechat.api.AuthModule.Services;
using xbytechat.api.CRM.Interfaces;
using xbytechat.api.CRM.Services;
using xbytechat.api.Features.AccessControl.Services;
using xbytechat.api.Features.AuditTrail.Services;
using xbytechat.api.Features.CampaignModule.Services;
using xbytechat.api.Features.CampaignTracking.Services;
using xbytechat.api.Features.Catalog.Services;
using xbytechat.api.Features.MessageManagement.Services;
using xbytechat.api.Features.MessagesEngine.PayloadBuilders;
using xbytechat.api.Features.MessagesEngine.Services;
using xbytechat.api.Features.PlanManagement.Services;
using xbytechat.api.Features.TemplateModule.Services;
using xbytechat.api.Features.Webhooks.Services;
using xbytechat.api.Features.Webhooks.Services.Processors;
using xbytechat.api.Features.Webhooks.Services.Resolvers;
using xbytechat.api.Features.xbTimeline.Services;
using xbytechat.api.Features.xbTimelines.Services;
using xbytechat.api.Helpers;
using xbytechat.api.Middlewares;
using xbytechat.api.PayloadBuilders;
using xbytechat.api.Repositories.Implementations;
using xbytechat.api.Repositories.Interfaces;
using xbytechat.api.Services;
using xbytechat.api.Services.Messages.Implementations;
using xbytechat.api.Services.Messages.Interfaces;
using xbytechat_api.WhatsAppSettings.Services;
using xbytechat_api.WhatsAppSettings.Validators;
using EnginePayloadBuilders = xbytechat.api.Features.MessagesEngine.PayloadBuilders;
using xbytechat.api.Features.CTAManagement.Services;
using xbytechat.api.Features.Tracking.Services;
using xbytechat.api.Features.Webhooks.BackgroundWorkers;
using xbytechat.api.Features.CTAFlowBuilder.Services;
using xbytechat.api.Features.FlowAnalytics.Services;
using xbytechat.api.Features.Inbox.Repositories;
using xbytechat.api.Features.Inbox.Services;
using xbytechat.api.Features.Inbox.Hubs;
using Microsoft.AspNetCore.SignalR;
using xbytechat.api.SignalR;
using xbytechat.api.Features.AutoReplyBuilder.Repositories;
using xbytechat.api.Features.AutoReplyBuilder.Services;
using xbytechat.api.Features.AutoReplyBuilder.Flows.Repositories;
using xbytechat.api.Features.AutoReplyBuilder.Flows.Services;
using xbytechat.api.Features.BusinessModule.Services;
using xbytechat.api.Features.FeatureAccessModule.Services;
using xbytechat.api.Features.ReportingModule.Services;
using xbytechat.api.Features.Automation.Repositories;
using xbytechat.api.Features.Automation.Services;
using Npgsql;
using System.Net;
using xbytechat.api.WhatsAppSettings.Providers;
using xbytechat.api.Features.CampaignTracking.Config;
using xbytechat.api.Features.CampaignTracking.Worker;
using xbytechat.api.Infrastructure.Flows;
using xbytechat.api.Features.Webhooks.Pinnacle.Services.Adapters;
using xbytechat.api.Features.Webhooks.Directory;
using xbytechat.api.Features.Webhooks.Status;
using xbytechat.api.Features.WhatsAppSettings.Services;
using xbytechat.api.WhatsAppSettings.Services;
using xbytechat_api.Features.Billing.Services;
using xbytechat.api.Features.Audiences.Services;
using xbytechat.api.Features.CampaignModule.Helpers;
using xbytechat.api.Features.Queueing.Services;
using xbytechat.api.Features.Contacts.Services;
using Microsoft.AspNetCore.HttpOverrides;
using xbytechat.api.Features.CustomeApi.Services;
using Microsoft.AspNetCore.Authentication;
using xbytechat.api.Features.CustomeApi.Auth;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

#region 🔷 Serilog Configuration
Log.Logger = new LoggerConfiguration()
    .Enrich.WithExceptionDetails()
    .Enrich.FromLogContext()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File("logs/log-.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();
builder.Host.UseSerilog();
#endregion

#region 🔷 Database Setup (PostgreSQL)
var connStr = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connStr).EnableSensitiveDataLogging()
);
//Console.WriteLine($"[DEBUG] Using Connection String: {connStr}");
#endregion

#region 🔷 Generic Repository Pattern
builder.Services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
#endregion

#region 🔷 Core Modules (Business/Auth)
builder.Services.AddScoped<IBusinessService, BusinessService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
#endregion

#region 🔷 Messaging Services & WhatsApp
builder.Services.AddScoped<IMessageEngineService, MessageEngineService>();
builder.Services.AddHttpClient<IMessageService, MessageService>();
builder.Services.AddScoped<WhatsAppService>();
builder.Services.AddScoped<IMessageStatusService, MessageStatusService>();
builder.Services.AddScoped<ITemplateMessageSender, TemplateMessageSender>();
#endregion
builder.Services.AddHttpClient();
#region 🔷 Payload Builders
builder.Services.AddScoped<xbytechat.api.PayloadBuilders.IWhatsAppPayloadBuilder, xbytechat.api.PayloadBuilders.TextMessagePayloadBuilder>();
builder.Services.AddScoped<xbytechat.api.PayloadBuilders.IWhatsAppPayloadBuilder, xbytechat.api.PayloadBuilders.ImageMessagePayloadBuilder>();
builder.Services.AddScoped<xbytechat.api.PayloadBuilders.IWhatsAppPayloadBuilder, xbytechat.api.PayloadBuilders.TemplateMessagePayloadBuilder>();
#endregion

#region 🔷 Catalog & CRM Modules
builder.Services.AddScoped<IProductService, ProductService>();
builder.Services.AddScoped<ICatalogTrackingService, CatalogTrackingService>();
builder.Services.AddScoped<ICatalogDashboardService, CatalogDashboardService>();
builder.Services.AddScoped<IContactService, ContactService>();
builder.Services.AddScoped<ITagService, TagService>();
builder.Services.AddScoped<IReminderService, ReminderService>();
builder.Services.AddScoped<INoteService, NoteService>();
builder.Services.AddScoped<ITimelineService, TimelineService>();
#endregion

#region 🔷 Billing 
builder.Services.AddScoped<IBillingIngestService, BillingIngestService>();
builder.Services.AddScoped<IBillingReadService, BillingReadService>();
builder.Services.AddScoped<IOutboundCampaignQueueService, OutboundCampaignQueueService>();
#endregion

#region 🔷 Campaign Management
builder.Services.AddScoped<ICampaignService, CampaignService>();
builder.Services.AddScoped<ICampaignSendLogService, CampaignSendLogService>();
builder.Services.AddScoped<ICampaignSendLogEnricher, CampaignSendLogEnricher>();
builder.Services.AddScoped<ICampaignAnalyticsService, CampaignAnalyticsService>();
builder.Services.AddScoped<ICampaignRetryService, CampaignRetryService>();
builder.Services.AddScoped<ICampaignTrackingRetryService, CampaignTrackingRetryService>();
builder.Services.AddHttpClient<IWhatsAppTemplateService, WhatsAppTemplateService>();
builder.Services.AddScoped<ICampaignRecipientService, CampaignRecipientService>();
builder.Services.AddScoped<IPlanService, PlanService>();
builder.Services.AddScoped<ITemplatePreviewService, TemplatePreviewService>();
builder.Services.AddScoped<IOutboundCampaignQueueService, OutboundCampaignQueueService>();
builder.Services.AddScoped<ICampaignPreviewService, CampaignPreviewService>();
builder.Services.AddScoped<IAudienceService, AudienceService>();
builder.Services.AddScoped<ICampaignVariableMapService, CampaignVariableMapService>();
builder.Services.AddScoped<IAudienceImportService, AudienceImportService>();
builder.Services.AddScoped<ICampaignMaterializationService, CampaignMaterializationService>();
builder.Services.AddScoped<ICampaignDispatchPlannerService, CampaignDispatchPlannerService>();
builder.Services.AddScoped<ICsvExportService, CsvExportService>();

builder.Services.AddScoped<ICampaignDryRunService, CampaignDryRunService>();
// API
builder.Services.AddScoped<ICustomApiService, CustomApiService>();
// CSV ingest
builder.Services.AddScoped<ICsvBatchService, CsvBatchService>();
builder.Services.AddScoped<IVariableResolver, VariableResolver>();
builder.Services.AddScoped<ICampaignMaterializer, CampaignMaterializer>();
builder.Services.AddScoped<ICampaignDispatcher, CampaignDispatcher>();
builder.Services.AddScoped<IVariableMappingService, NoopVariableMappingService>();
builder.Services.AddScoped<IOutboundCampaignQueueService, NoopOutboundCampaignQueueService>();
builder.Services.AddScoped<IMappingSuggestionService, MappingSuggestionService>();
#endregion

#region 🔷 Webhook Management
builder.Services.AddScoped<IWhatsAppWebhookService, WhatsAppWebhookService>();
builder.Services.AddScoped<IWhatsAppWebhookDispatcher, WhatsAppWebhookDispatcher>();
builder.Services.AddScoped<IStatusWebhookProcessor, StatusWebhookProcessor>();
builder.Services.AddScoped<ITemplateWebhookProcessor, TemplateWebhookProcessor>();
builder.Services.AddScoped<IMessageIdResolver, MessageIdResolver>();
builder.Services.AddScoped<IClickWebhookProcessor, ClickWebhookProcessor>();
builder.Services.AddScoped<ILeadTimelineService, LeadTimelineService>();
builder.Services.AddScoped<IFailedWebhookLogService, FailedWebhookLogService>();
builder.Services.AddSingleton<IWebhookQueueService, WebhookQueueService>();
builder.Services.AddHostedService<WebhookQueueWorker>();
builder.Services.AddHostedService<FailedWebhookLogCleanupService>();
builder.Services.AddScoped<IMaintenanceService, MaintenanceService>();
builder.Services.AddHostedService<WebhookAutoCleanupWorker>();
builder.Services.AddScoped<IProviderDirectory, ProviderDirectory>();
builder.Services.AddScoped<IMessageStatusUpdater, MessageStatusUpdater>();
builder.Services.AddScoped<IPinnacleToMetaAdapter, PinnacleToMetaAdapter>();


#endregion

#region 🔷 Access Control & Permission
builder.Services.AddScoped<IAccessControlService, AccessControlService>();
builder.Services.AddScoped<IFeatureAccessEvaluator, FeatureAccessEvaluator>();
builder.Services.AddScoped<IFeatureAccessService, FeatureAccessService>();
#endregion

#region 🔷 Tracking
builder.Services.AddScoped<ITrackingService, TrackingService>();
builder.Services.AddScoped<IMessageAnalyticsService, MessageAnalyticsService>();
builder.Services.AddScoped<IUrlBuilderService, UrlBuilderService>();
builder.Services.AddScoped<IContactJourneyService, ContactJourneyService>();

builder.Services.Configure<TrackingOptions>(builder.Configuration.GetSection("Tracking"));
builder.Services.AddSingleton<IClickTokenService, ClickTokenService>();
builder.Services.AddSingleton<IClickEventQueue, InProcessClickEventQueue>();
builder.Services.AddHostedService<ClickLogWorker>();

builder.Services.AddScoped<IMessageLogsReportService, MessageLogsReportService>();

#endregion

#region 🔷 Flow Builder
builder.Services.AddScoped<ICTAFlowService, CTAFlowService>();

//builder.Services.Configure<FlowClickTokenOptions>(
//    builder.Configuration.GetSection("FlowClickTokens"));

builder.Services.AddOptions<FlowClickTokenOptions>()
    .BindConfiguration("FlowClickTokens")
    .Validate(o => !string.IsNullOrWhiteSpace(o.Secret) && o.Secret.Length >= 32,
              "Secret required (≥32 chars).")
    .Validate(o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps,
              "BaseUrl must be an absolute https URL.")
    .Validate(o => o.TtlHours > 0, "TtlHours must be positive.")
    .ValidateOnStart();

builder.Services.AddSingleton<IFlowClickTokenService, FlowClickTokenService>();
builder.Services.AddScoped<IFlowRuntimeService, FlowRuntimeService>();  //
#endregion

#region 🔷 Audit Trail Logging
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAuditLogService, AuditLogService>();
#endregion
builder.Services.AddScoped<IContactProfileService, ContactProfileService>();
#region 🔷 WhatsApp settings
builder.Services.AddScoped<IWhatsAppSettingsService, WhatsAppSettingsService>();
builder.Services.AddValidatorsFromAssemblyContaining<SaveWhatsAppSettingValidator>();
builder.Services.AddHttpClient<IMessageEngineService, MessageEngineService>();
builder.Services.AddScoped<IWhatsAppTemplateFetcherService, WhatsAppTemplateFetcherService>();
builder.Services.AddScoped<EnginePayloadBuilders.TextMessagePayloadBuilder>();
builder.Services.AddScoped<EnginePayloadBuilders.ImageMessagePayloadBuilder>();
builder.Services.AddScoped<EnginePayloadBuilders.TemplateMessagePayloadBuilder>();
builder.Services.AddScoped<EnginePayloadBuilders.CtaMessagePayloadBuilder>();
builder.Services.AddScoped<IPlanManager, PlanManager>();
builder.Services.AddScoped<ICTAManagementService, CTAManagementService>();
//builder.Services.AddScoped<IWhatsAppProviderFactory, WhatsAppProviderFactory>();
builder.Services.AddScoped<xbytechat.api.Features.MessagesEngine.Factory.IWhatsAppProviderFactory,
                           xbytechat.api.Features.MessagesEngine.Factory.WhatsAppProviderFactory>();
builder.Services.AddScoped<IWhatsAppSenderService, WhatsAppSenderService>();

builder.Services.AddHttpClient("wa:pincale", c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
});

builder.Services.AddHttpClient("wa:meta_cloud", c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddScoped<MetaTemplateCatalogProvider>();
builder.Services.AddScoped<PinnacleTemplateCatalogProvider>();
builder.Services.AddScoped<ITemplateSyncService, TemplateSyncService>();

// WhatsApp phone number management
builder.Services.AddScoped<IWhatsAppPhoneNumberService, WhatsAppPhoneNumberService>();

#endregion

#region Worker
builder.Services.AddHostedService<TemplateSyncWorker>();
builder.Services.AddHostedService<OutboundCampaignSendWorker>();

#endregion
#region 🔷 Inbox
builder.Services.AddScoped<IUnreadCountService, UnreadCountService>();

builder.Services.AddScoped<IFlowAnalyticsService, FlowAnalyticsService>();
builder.Services.AddScoped<IInboxService, InboxService>();
builder.Services.AddScoped<IInboundMessageProcessor, InboundMessageProcessor>();
builder.Services.AddScoped<IInboxRepository, InboxRepository>();
builder.Services.AddScoped<IQuickReplyService, QuickReplyService>();
#endregion

#region 🔷 Access Control
builder.Services.AddScoped<IPermissionService, PermissionService>();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IPermissionCacheService, PermissionCacheService>();
#endregion

#region 🔷 AutoReplyBuilder Module
builder.Services.AddScoped<IAutoReplyRepository, AutoReplyRepository>();
builder.Services.AddScoped<IAutoReplyService, AutoReplyService>();
builder.Services.AddScoped<IAutoReplyFlowRepository, AutoReplyFlowRepository>();
builder.Services.AddScoped<IAutoReplyFlowService, AutoReplyFlowService>();
builder.Services.AddScoped<IAutoReplyRuntimeService, AutoReplyRuntimeService>();
builder.Services.AddScoped<IChatSessionStateService, ChatSessionStateService>();
builder.Services.AddScoped<IAgentAssignmentService, AgentAssignmentService>();
#endregion

#region 🔷 Automation Module
builder.Services.AddScoped<IAutomationFlowRepository, AutomationFlowRepository>();
builder.Services.AddScoped<IAutomationRunner, AutomationRunner>();
builder.Services.AddScoped<IAutomationService, AutomationService>();
#endregion


#region 🔐 JWT Authentication (Bearer token only, no cookies)
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var jwtSettings = builder.Configuration.GetSection("JwtSettings");

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings["Issuer"],
            ValidAudience = jwtSettings["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings["SecretKey"])),
            ClockSkew = TimeSpan.Zero
        };

        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context =>
            {
                if (context.Exception is SecurityTokenExpiredException)
                {
                    context.Response.StatusCode = 401;
                    context.Response.ContentType = "application/json";
                    return context.Response.WriteAsync("{\"success\":false,\"message\":\"❌ Token expired. Please login again.\"}");
                }
                return Task.CompletedTask;
            }
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];

                // If the request is for our hub...
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) &&
                    (path.StartsWithSegments("/hubs/inbox")))
                {
                    // Read the token from the query string
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();
#endregion
builder.Services.Configure<StaticApiKeyOptions>(
    builder.Configuration.GetSection("ApiKeys:Static"));


//builder.Services.Configure<xbytechat.api.Features.CustomeApi.Auth.StaticApiKeyOptions>(
//    builder.Configuration.GetSection("ApiKeys:Static"));


//builder.Services
//    .AddAuthentication(options =>
//    {
//        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
//        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
//    })
//    .AddJwtBearer(options =>
//    {
//        var jwtSettings = builder.Configuration.GetSection("JwtSettings");

//        options.TokenValidationParameters = new TokenValidationParameters
//        {
//            ValidateIssuer = true,
//            ValidateAudience = true,
//            ValidateLifetime = true,
//            ValidateIssuerSigningKey = true,
//            ValidIssuer = jwtSettings["Issuer"],
//            ValidAudience = jwtSettings["Audience"],
//            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings["SecretKey"])),
//            ClockSkew = TimeSpan.Zero
//        };

//        options.Events = new JwtBearerEvents
//        {
//            OnAuthenticationFailed = context =>
//            {
//                if (context.Exception is SecurityTokenExpiredException)
//                {
//                    context.Response.StatusCode = 401;
//                    context.Response.ContentType = "application/json";
//                    return context.Response.WriteAsync("{\"success\":false,\"message\":\"❌ Token expired. Please login again.\"}");
//                }
//                return Task.CompletedTask;
//            },
//            OnMessageReceived = context =>
//            {
//                var accessToken = context.Request.Query["access_token"];
//                var path = context.HttpContext.Request.Path;
//                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/inbox"))
//                    context.Token = accessToken; // SignalR
//                return Task.CompletedTask;
//            }
//        };
//    });


//builder.Services.AddAuthorization();
//#endregion


#region 🌐 CORS Setup (Bearer mode, no credentials)

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
if (allowedOrigins == null || allowedOrigins.Length == 0)
{
    var raw = builder.Configuration["Cors:AllowedOrigins"];
    if (!string.IsNullOrWhiteSpace(raw))
        allowedOrigins = raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
Console.WriteLine("[CORS] Allowed origins => " + string.Join(", ", allowedOrigins ?? Array.Empty<string>()));



builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy
            .WithOrigins(allowedOrigins ?? Array.Empty<string>())
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});
#endregion

#region ✅ MVC + Swagger + Middleware
builder.Services.AddControllers()
    .AddJsonOptions(opts =>
    {
        opts.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    });

builder.Services.AddEndpointsApiExplorer();

//builder.Services.AddSwaggerGen(options =>
//{
//    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
//    {
//        Title = "xByteChat API",
//        Version = "v1",
//        Description = "API documentation for xByteChat project"
//    });
//});
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "xByteChat API",
        Version = "v1",
        Description = "API documentation for xByteChat project"
    });

    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Description = "JWT Authorization header. Example: Bearer {token}",
        Name = "Authorization",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });

    options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Description = "Paste your static key (no quotes). Example: api_live_DEV_xxx",
        In = ParameterLocation.Header,
        Name = "X-Auth-Key",
        Type = SecuritySchemeType.ApiKey
    });

    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        },
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "ApiKey" }
            },
            Array.Empty<string>()
        }
    });
});

#endregion

builder.Services.Configure<HostOptions>(o =>
    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

builder.Services.AddAutoMapper(AppDomain.CurrentDomain.GetAssemblies());

#region SignalR
builder.Services.AddSignalR();
builder.Services.AddSingleton<IUserIdProvider, NameUserIdProvider>();
#endregion

builder.Services.AddHttpClient("customapi-webhooks", c =>
{
    c.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddScoped<ICtaJourneyPublisher, CtaJourneyPublisher>();
builder.Services.AddScoped<CtaJourneyPublisher>();
//builder.Services.Configure<HostOptions>(o =>
//{
//    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
//});

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Log.Error(e.ExceptionObject as Exception, "Unhandled exception (AppDomain)");
//builder.Services.Configure<ForwardedHeadersOptions>(o =>
//{
//    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
//    o.KnownNetworks.Clear();
//    o.KnownProxies.Clear();
//});

TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Log.Error(e.Exception, "Unobserved task exception");
    e.SetObserved();
};
var app = builder.Build();



app.MapGet("/api/debug/cors", () => Results.Ok(new
{
    Allowed = app.Services.GetRequiredService<IConfiguration>()
              .GetSection("Cors:AllowedOrigins").Get<string[]>()
}));
app.MapGet("/api/debug/db", async (AppDbContext db) =>
{
    try
    {
        await db.Database.OpenConnectionAsync();
        await db.Database.CloseConnectionAsync();
        return Results.Ok("ok");
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});
app.MapGet("/api/debug/_dbping", async (IConfiguration cfg) =>
{
    try
    {
        var cs = cfg.GetConnectionString("DefaultConnection");
        await using var conn = new Npgsql.NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand("select version()", conn);
        var ver = (string?)await cmd.ExecuteScalarAsync();
        return Results.Ok(new { ok = true, version = ver });
    }
    catch (Exception ex)
    {
        return Results.Problem(title: "DB ping failed", detail: ex.ToString(), statusCode: 500);
    }
});
app.MapGet("/api/debug/conn", (IConfiguration cfg) =>
{
    var cs = cfg.GetConnectionString("DefaultConnection") ?? "";
    var b = new NpgsqlConnectionStringBuilder(cs);
    return Results.Ok(new
    {
        host = b.Host,
        port = b.Port,
        database = b.Database,
        username = b.Username,
        sslmode = b.SslMode.ToString(),
        hasPassword = !string.IsNullOrEmpty(b.Password)
    });
});
// Try DNS resolution of the DB host that /api/debug/conn reports
app.MapGet("/api/debug/dns", (IConfiguration cfg) =>
{
    var cs = cfg.GetConnectionString("DefaultConnection") ?? "";
    var b = new NpgsqlConnectionStringBuilder(cs);
    try
    {
        var ips = Dns.GetHostAddresses(b.Host);
        return Results.Ok(new { host = b.Host, addresses = ips.Select(i => i.ToString()).ToArray() });
    }
    catch (Exception ex)
    {
        return Results.Problem($"DNS failed for host '{b.Host}': {ex.Message}");
    }
});


#region 🌐 Middleware Pipeline Setup
AuditLoggingHelper.Configure(app.Services);

app.UseMiddleware<GlobalExceptionMiddleware>();

if (app.Environment.IsDevelopment())
{
    // Dev-specific configs
}

app.UseSwagger();
app.UseSwaggerUI();
if (!app.Environment.IsDevelopment())
    app.UseHsts();
app.UseHttpsRedirection();

// Security headers
//app.Use(async (context, next) =>
//{
//    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
//    context.Response.Headers["X-Frame-Options"] = "DENY";
//    context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
//    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
//    context.Response.Headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
//    await next();
//});

app.UseRouting();
app.UseCors("AllowFrontend");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<InboxHub>("/hubs/inbox");

app.Run();
#endregion






