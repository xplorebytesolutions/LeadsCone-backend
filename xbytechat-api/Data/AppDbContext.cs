using Microsoft.EntityFrameworkCore;
using System.Globalization;
using xbytechat.api.CRM.Models;
using xbytechat.api.Features.Catalog.Models;
using xbytechat.api.Models.BusinessModel;
using xbytechat.api.Features.CampaignModule.Models;
using xbytechat.api.Features.CampaignTracking.Models;
using xbytechat.api.AuthModule.Models;
using xbytechat.api.Features.AccessControl.Models;
using xbytechat.api.Features.AccessControl.Seeder;
using xbytechat.api.Features.AuditTrail.Models;
using xbytechat.api.Features.xbTimelines.Models;
using xbytechat_api.WhatsAppSettings.Models;
using xbytechat.api.Features.CTAManagement.Models;
using xbytechat.api.Features.Tracking.Models;
using xbytechat.api.Features.MessageManagement.DTOs;
using xbytechat.api.Features.Webhooks.Models;
using xbytechat.api.Features.CTAFlowBuilder.Models;
using xbytechat.api.Features.Inbox.Models;
using xbytechat.api.Features.AutoReplyBuilder.Models;
using xbytechat.api.Features.AutoReplyBuilder.Flows.Models;
using xbytechat.api.Features.BusinessModule.Models;
using xbytechat.api.Features.FeatureAccessModule.Models;
using xbytechat.api.Features.PlanManagement.Models;
using xbytechat.api.Features.Automation.Models;
using xbytechat.api.Features.CampaignTracking.Worker;
using xbytechat.api.Features.WhatsAppSettings.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using xbytechat_api.Features.Billing.Models;
using xbytechat.api.Features.CustomeApi.Models;

namespace xbytechat.api
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options)
            : base(options) { }

        // ✅ Table Registrations
        public DbSet<Business> Businesses { get; set; }
        public DbSet<User> Users { get; set; }
        public DbSet<MessageLog> MessageLogs { get; set; }
        public DbSet<Product> Products { get; set; }
        public DbSet<CatalogClickLog> CatalogClickLogs { get; set; }
        public DbSet<Contact> Contacts { get; set; }
        public DbSet<Tag> Tags { get; set; }
        public DbSet<Reminder> Reminders { get; set; }
        public DbSet<Note> Notes { get; set; }
        public DbSet<LeadTimeline> LeadTimelines { get; set; }
        public DbSet<ContactTag> ContactTags { get; set; }
        public DbSet<Campaign> Campaigns { get; set; }
        public DbSet<CampaignRecipient> CampaignRecipients { get; set; }
        public DbSet<CampaignSendLog> CampaignSendLogs { get; set; }
        public DbSet<MessageStatusLog> MessageStatusLogs { get; set; }

        // 🧩 Access Control
        public DbSet<Role> Roles { get; set; }
        public DbSet<Permission> Permissions { get; set; }
        public DbSet<RolePermission> RolePermissions { get; set; }
        public DbSet<UserPermission> UserPermissions { get; set; }
        public DbSet<AuditLog> AuditLogs { get; set; }
        public DbSet<WhatsAppSettingEntity> WhatsAppSettings { get; set; }
        public DbSet<BusinessPlanInfo> BusinessPlanInfos { get; set; }

        public DbSet<TrackingLog> TrackingLogs { get; set; }
        public DbSet<CTADefinition> CTADefinitions { get; set; }
        public DbSet<CampaignButton> CampaignButtons { get; set; }
        public DbSet<FailedWebhookLog> FailedWebhookLogs { get; set; }
        public DbSet<WebhookSettings> WebhookSettings { get; set; }

        public DbSet<CTAFlowConfig> CTAFlowConfigs { get; set; }
        public DbSet<CTAFlowStep> CTAFlowSteps { get; set; }
        public DbSet<FlowButtonLink> FlowButtonLinks { get; set; }

        public DbSet<CampaignFlowOverride> CampaignFlowOverrides { get; set; }
        public DbSet<FlowExecutionLog> FlowExecutionLogs { get; set; }
        public DbSet<ContactRead> ContactReads { get; set; }

        public DbSet<AutoReplyRule> AutoReplyRules { get; set; }
        public DbSet<AutoReplyFlow> AutoReplyFlows { get; set; }
        public DbSet<AutoReplyFlowNode> AutoReplyFlowNodes { get; set; }
        public DbSet<AutoReplyFlowEdge> AutoReplyFlowEdges { get; set; }
        public DbSet<AutoReplyLog> AutoReplyLogs { get; set; }
        public DbSet<ChatSessionState> ChatSessionStates { get; set; }
        public DbSet<Plan> Plans { get; set; }
        public DbSet<PlanPermission> PlanPermissions { get; set; }
        public DbSet<FeatureAccess> FeatureAccess { get; set; }
        public DbSet<PlanFeatureMatrix> PlanFeatureMatrix { get; set; }
        public DbSet<UserFeatureAccess> UserFeatureAccess { get; set; }
        public DbSet<FeatureMaster> FeatureMasters { get; set; }
        public DbSet<AutomationFlow> AutomationFlows { get; set; }
        public DbSet<WhatsAppTemplate> WhatsAppTemplates { get; set; }

        public DbSet<CampaignClickLog> CampaignClickLogs => Set<CampaignClickLog>();
        public DbSet<CampaignClickDailyAgg> CampaignClickDailyAgg => Set<CampaignClickDailyAgg>();

        public DbSet<QuickReply> QuickReplies { get; set; } = null!;
        public DbSet<WhatsAppPhoneNumber> WhatsAppPhoneNumbers { get; set; }

        public DbSet<Audience> Audiences { get; set; }
        public DbSet<AudienceMember> AudiencesMembers { get; set; }
        public DbSet<CsvBatch> CsvBatches { get; set; }
        public DbSet<CsvRow> CsvRows { get; set; }
        public DbSet<CampaignVariableMap> CampaignVariableMaps { get; set; }

        public DbSet<ProviderBillingEvent> ProviderBillingEvents { get; set; } = default!;
        public DbSet<OutboundCampaignJob> OutboundCampaignJobs { get; set; }
        public DbSet<ApiKey> ApiKeys { get; set; } = null!;

    //    public DbSet<CustomerWebhookConfig> CustomerWebhookConfigs
    //=> Set<CustomerWebhookConfig>();
        public DbSet<xbytechat.api.Features.CustomeApi.Models.CustomerWebhookConfig> CustomerWebhookConfigs { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // ────────────────────── DETERMINISTIC SEED TIMESTAMPS ──────────────────────
            var seedCreatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var planCreatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var created = new DateTime(2025, 9, 13, 0, 0, 0, DateTimeKind.Utc);

            // ────────────────────── SEEDS (unchanged GUIDs) ──────────────────────
            var superadminRoleId = Guid.Parse("00000000-0000-0000-0000-000000000001");
            var partnerRoleId = Guid.Parse("00000000-0000-0000-0000-000000000002");
            var resellerRoleId = Guid.Parse("00000000-0000-0000-0000-000000000003");
            var businessRoleId = Guid.Parse("00000000-0000-0000-0000-000000000004");
            var agentRoleId = Guid.Parse("00000000-0000-0000-0000-000000000005");

            modelBuilder.Entity<Role>().HasData(
                new Role { Id = superadminRoleId, Name = "admin", Description = "Super Admin", CreatedAt = seedCreatedAt },
                new Role { Id = partnerRoleId, Name = "partner", Description = "Business Partner", CreatedAt = seedCreatedAt },
                new Role { Id = resellerRoleId, Name = "reseller", Description = "Reseller Partner", CreatedAt = seedCreatedAt },
                new Role { Id = businessRoleId, Name = "business", Description = "Business Owner", CreatedAt = seedCreatedAt },
                new Role { Id = agentRoleId, Name = "staff", Description = "Staff", CreatedAt = seedCreatedAt }
            );

            var superAdminUserId = Guid.Parse("62858aa2-3a54-4fd5-8696-c343d9af7634");
            modelBuilder.Entity<User>().HasData(new User
            {
                Id = superAdminUserId,
                Name = "Super Admin",
                Email = "admin@xbytechat.com",
                RoleId = superadminRoleId,
                Status = "active",
                CreatedAt = seedCreatedAt,
                DeletedAt = null,
                IsDeleted = false,
                BusinessId = null,
                PasswordHash = "JAvlGPq9JyTdtvBO6x2llnRI1+gxwIyPqCKAn3THIKk=",
                RefreshToken = null,
                RefreshTokenExpiry = null
            });

            var basicPlanId = Guid.Parse("5f9f5de1-a0b2-48ba-b03d-77b27345613f");
            modelBuilder.Entity<Plan>().HasData(new Plan
            {
                Id = basicPlanId,
                Code = "basic",
                Name = "Basic",
                Description = "Default free plan",
                IsActive = true,
                CreatedAt = planCreatedAt
            });

            modelBuilder.Entity<Permission>().HasData(
                new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000000"), Code = "dashboard.view", Name = "dashboard.view", Group = "Dashboard", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000001"), Code = "campaign.view", Name = "campaign.view", Group = "Campaign", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000002"), Code = "campaign.create", Name = "campaign.create", Group = "Campaign", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000003"), Code = "campaign.delete", Name = "campaign.delete", Group = "Campaign", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000004"), Code = "product.view", Name = "product.view", Group = "Catalog", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000005"), Code = "product.create", Name = "product.create", Group = "Catalog", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000006"), Code = "product.delete", Name = "product.delete", Group = "Catalog", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000007"), Code = "contacts.view", Name = "contacts.view", Group = "CRM", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000008"), Code = "tags.edit", Name = "tags.edit", Group = null, IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000009"), Code = "admin.business.approve", Name = "admin.business.approve", Group = "Admin", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000010"), Code = "admin.logs.view", Name = "admin.logs.view", Group = null, IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000011"), Code = "admin.plans.view", Name = "admin.plans.view", Group = "Admin", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000012"), Code = "admin.plans.create", Name = "admin.plans.create", Group = "Admin", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000013"), Code = "admin.plans.update", Name = "admin.plans.update", Group = "Admin", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000014"), Code = "admin.plans.delete", Name = "admin.plans.delete", Group = "Admin", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("74c8034f-d9cb-4a17-8578-a9f765bd845c"), Code = "messaging.report.view", Name = "messaging.report.view", Group = "Messaging", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("c819f1bd-422d-4609-916c-cc185fe44ab0"), Code = "messaging.status.view", Name = "messaging.status.view", Group = "Messaging", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("eecd0fac-223c-4dba-9fa1-2a6e973d61d1"), Code = "messaging.inbox.view", Name = "messaging.inbox.view", Group = "Messaging", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("974af1f9-3caa-4857-a1a7-48462c389332"), Code = "messaging.send.text", Name = "messaging.send.text", Group = "Messaging", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("0485154c-dde5-4732-a7aa-a379c77a5b27"), Code = "messaging.send.template", Name = "messaging.send.template", Group = "Messaging", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("29461562-ef9c-48c0-a606-482ff57b8f95"), Code = "messaging.send", Name = "messaging.send", Group = "Messaging", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("bbc5202a-eac9-40bb-aa78-176c677dbf5b"), Code = "messaging.whatsappsettings.view", Name = "messaging.whatsappsettings.view", Group = "Messaging", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("205b87c7-b008-4e51-9fea-798c2dc4f9c2"), Code = "admin.whatsappsettings.view", Name = "admin.whatsappsettings.view", Group = "Admin", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("6e4d3a86-7cf9-4ac2-b8a7-ed10c9f0173d"), Code = "settings.whatsapp.view", Name = "Settings - WhatsApp View", Group = "Settings", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("ad36cdb7-5221-448b-a6a6-c35c9f88d021"), Code = "inbox.view", Name = "inbox.view", Group = "Inbox", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("74828fc0-e358-4cfc-b924-13719a0d9f50"), Code = "inbox.menu", Name = "inbox.menu", Group = "Inbox", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("98572fe7-d142-475a-b990-f248641809e2"), Code = "settings.profile.view", Name = "settings.profile.view", Group = "Settings", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("821480c6-1464-415e-bba8-066fcb4e7e63"), Code = "automation.menu", Name = "automation.menu", Group = "Automation", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("918a61d0-5ab6-46af-a3d3-41e37b7710f9"), Code = "automation.Create.Template.Flow", Name = "automation.Create.Template.Flow", Group = "Automation", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("9ae90cfe-3fea-4307-b024-3083c2728148"), Code = "automation.View.Template.Flow", Name = "automation.View.Template.Flow", Group = "Automation", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("93c5d5a7-f8dd-460a-8c7b-e3788440ba3a"), Code = "automation.Create.TemplatePlusFreetext.Flow", Name = "automation.Create.TemplatePlusFreetext.Flow", Group = "Automation", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("7d7cbceb-4ce7-4835-85cd-59562487298d"), Code = "automation.View.TemplatePlusFreetext.Flow", Name = "automation.View.TemplatePlusFreetext.Flow", Group = "Automation", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("636b17f2-1c54-4e26-a8cd-dbf561dcb522"), Code = "automation.View.Template.Flow_analytics", Name = "automation.View.Template.Flow_analytics", Group = "Automation", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("adfa8490-9705-4a36-a86e-d5bff7ddc220"), Code = "automation.View.TemplatePlusFreeText.Flow_analytics", Name = "automation.View.TemplatePlusFreeText.Flow_analytics", Group = "Automation", IsActive = true, CreatedAt = created },
                new Permission { Id = Guid.Parse("0dedac5b-81c8-44c3-8cfe-76c58e29c6db"), Code = "automation_trigger_test", Name = "automation_trigger_test", Group = "Automation", IsActive = true, CreatedAt = created }
                //new Permission { Id = Guid.Parse("0d7dac5b-81c8-44c3-8cfe-76c66669c6db"), Code = "campaign.send.template.simple", Name = "Simple Temlate Send : extra", Group = "Campaign", IsActive = true, CreatedAt = created }
                //"campaign-tracking-logs": [FK.CAMPAIGN_TRACKING_LOGS_VIEW], -- NEDD TO ADD
                );

            // ───────────────── Relationships (clean and deduped) ─────────────────

            // Access-control
            modelBuilder.Entity<RolePermission>()
                .HasOne(rp => rp.Role).WithMany(r => r.RolePermissions)
                .HasForeignKey(rp => rp.RoleId).OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<RolePermission>()
                .HasOne(rp => rp.Permission).WithMany(p => p.RolePermissions)
                .HasForeignKey(rp => rp.PermissionId).OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<UserPermission>()
                .HasOne(up => up.User).WithMany(u => u.UserPermissions)
                .HasForeignKey(up => up.UserId).OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<UserPermission>()
                .HasOne(up => up.Permission).WithMany(p => p.UserPermissions)
                .HasForeignKey(up => up.PermissionId).OnDelete(DeleteBehavior.Cascade);

            // Campaign core
            modelBuilder.Entity<Campaign>()
                .HasOne(c => c.Business).WithMany(b => b.Campaigns)
                .HasForeignKey(c => c.BusinessId).IsRequired();

            modelBuilder.Entity<Campaign>()
                .HasMany(c => c.MultiButtons).WithOne(b => b.Campaign)
                .HasForeignKey(b => b.CampaignId).OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Campaign>(e =>
            {
                e.Property(x => x.TemplateSchemaSnapshot).HasColumnType("jsonb");
                e.HasMany(c => c.Audiences).WithOne(a => a.Campaign)
                 .HasForeignKey(a => a.CampaignId).OnDelete(DeleteBehavior.SetNull);
                e.HasMany(c => c.SendLogs).WithOne(s => s.Campaign)
                 .HasForeignKey(s => s.CampaignId).OnDelete(DeleteBehavior.Cascade);
                e.HasMany(c => c.MessageLogs).WithOne(m => m.SourceCampaign)
                 .HasForeignKey(m => m.CampaignId).OnDelete(DeleteBehavior.Restrict);
            });

            // Audience / CSV
            modelBuilder.Entity<CsvBatch>(e =>
            {
                e.ToTable("CsvBatches");
                e.HasKey(x => x.Id);
                e.Property(x => x.HeadersJson).HasColumnType("jsonb");
                e.HasIndex(x => x.Checksum).HasDatabaseName("ix_csvbatch_checksum");
                e.HasIndex(x => new { x.BusinessId, x.CreatedAt }).HasDatabaseName("ix_csvbatch_biz_created");
                e.HasIndex(x => new { x.BusinessId, x.AudienceId });
                e.HasOne<Audience>().WithMany()
                 .HasForeignKey(x => x.AudienceId).OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<CsvRow>(e =>
            {
                e.ToTable("CsvRows");
                e.HasKey(x => x.Id);
                e.Property(x => x.RowJson).HasColumnType("jsonb");
                e.HasIndex(x => new { x.BatchId, x.RowIndex }).IsUnique().HasDatabaseName("ux_csvrow_batch_rowidx");
                e.HasIndex(x => x.PhoneE164).HasDatabaseName("ix_csvrow_phone");
                e.HasIndex(x => new { x.BusinessId, x.BatchId });
                e.HasOne(x => x.Batch).WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<Audience>(e =>
            {
                e.ToTable("Audiences");
                e.HasKey(x => x.Id);
                e.HasIndex(x => new { x.BusinessId, x.IsDeleted }).HasDatabaseName("ix_audiences_biz_deleted");
                e.HasIndex(x => new { x.BusinessId, x.CampaignId });
                e.HasIndex(x => new { x.BusinessId, x.CsvBatchId });
                e.HasOne(x => x.CsvBatch).WithMany().HasForeignKey(x => x.CsvBatchId).OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<AudienceMember>(e =>
            {
                e.ToTable("AudienceMembers");
                e.HasKey(x => x.Id);
                e.Property(x => x.AttributesJson).HasColumnType("jsonb");
                e.HasIndex(x => new { x.AudienceId, x.PhoneE164 }).IsUnique().HasDatabaseName("ux_audmember_audience_phone");
                e.HasIndex(x => x.ContactId).HasDatabaseName("ix_audmember_contact");
                e.HasOne(x => x.Audience).WithMany(a => a.Members)
                 .HasForeignKey(x => x.AudienceId).OnDelete(DeleteBehavior.Cascade);
            });

            // Recipients — OPTIONAL AudienceMember, OPTIONAL Contact
            modelBuilder.Entity<CampaignRecipient>(e =>
            {
                e.ToTable("CampaignRecipients");
                e.HasKey(x => x.Id);

                e.Property(x => x.ResolvedParametersJson).HasColumnType("jsonb");
                e.Property(x => x.ResolvedButtonUrlsJson).HasColumnType("jsonb");
                e.HasIndex(x => x.IdempotencyKey).HasDatabaseName("ix_campaignrecipients_idempotency");
                e.HasIndex(x => new { x.CampaignId, x.ContactId }).HasDatabaseName("ix_recipients_campaign_contact");

                e.HasOne(r => r.AudienceMember)
                 .WithMany()
                 .HasForeignKey(r => r.AudienceMemberId)
                 .IsRequired(false)
                 .OnDelete(DeleteBehavior.SetNull);

                e.HasOne(r => r.Contact)
                 .WithMany()
                 .HasForeignKey(r => r.ContactId)
                 .IsRequired(false)
                 .OnDelete(DeleteBehavior.SetNull);

                e.HasOne(r => r.Campaign)
                 .WithMany(c => c.Recipients)
                 .HasForeignKey(r => r.CampaignId)
                 .OnDelete(DeleteBehavior.Cascade);

                e.HasOne(r => r.Business)
                 .WithMany()
                 .HasForeignKey(r => r.BusinessId)
                 .OnDelete(DeleteBehavior.Restrict);
            });

            // Send logs — OPTIONAL Contact, REQUIRED Campaign
            modelBuilder.Entity<CampaignSendLog>(e =>
            {
                e.ToTable("CampaignSendLogs");
                e.HasKey(x => x.Id);

                e.Property(x => x.BusinessId).IsRequired();
                e.HasIndex(x => x.MessageId);
                e.HasIndex(x => x.RunId);
                e.HasIndex(x => new { x.BusinessId, x.MessageId }).HasDatabaseName("IX_CampaignSendLogs_Business_MessageId");

                e.HasOne(s => s.Recipient).WithMany(r => r.SendLogs)
                 .HasForeignKey(s => s.RecipientId);

                // ✅ allow null ContactId (fixes 23502 once column is nullable)
                e.HasOne(s => s.Contact).WithMany()
                 .HasForeignKey(s => s.ContactId)
                 .IsRequired(false)
                 .OnDelete(DeleteBehavior.SetNull);

                e.HasOne(s => s.Campaign).WithMany(c => c.SendLogs)
                 .HasForeignKey(s => s.CampaignId)
                 .IsRequired()
                 .OnDelete(DeleteBehavior.Cascade);

                e.HasOne(s => s.MessageLog).WithMany()
                 .HasForeignKey(s => s.MessageLogId)
                 .OnDelete(DeleteBehavior.Restrict);
            });

            // Message logs — helpful indexes + computed column
            modelBuilder.Entity<MessageLog>(b =>
            {
                b.HasIndex(x => x.MessageId);
                b.HasIndex(x => x.RunId);
                b.HasIndex(x => new { x.BusinessId, x.MessageId }).HasDatabaseName("IX_MessageLogs_Business_MessageId");
                b.HasIndex(x => new { x.BusinessId, x.RecipientNumber }).HasDatabaseName("IX_MessageLogs_Business_Recipient");
                b.Property<DateTime?>("MessageTime").HasComputedColumnSql("COALESCE(\"SentAt\", \"CreatedAt\")", stored: true);
                b.HasIndex("BusinessId", "IsIncoming", "ContactId", "MessageTime").HasDatabaseName("ix_msglogs_biz_in_contact_msgtime");
            });

            // QuickReplies
            modelBuilder.Entity<QuickReply>(e =>
            {
                e.HasIndex(x => new { x.BusinessId, x.Scope, x.IsActive });
                e.HasIndex(x => new { x.BusinessId, x.OwnerUserId, x.IsActive });
                e.HasIndex(x => x.UpdatedAt);
                e.Property(x => x.Title).HasMaxLength(120).IsRequired();
                e.Property(x => x.Language).HasMaxLength(8);
                e.Property(q => q.UpdatedAt).HasDefaultValueSql("NOW()");
            });

            // Contacts — uniqueness
            modelBuilder.Entity<Contact>()
                .HasIndex(c => new { c.BusinessId, c.PhoneNumber }).IsUnique();

            modelBuilder.Entity<ContactRead>()
                .HasIndex(cr => new { cr.ContactId, cr.UserId }).IsUnique();

            modelBuilder.Entity<ContactRead>()
                .HasIndex(cr => new { cr.BusinessId, cr.UserId, cr.ContactId })
                .IsUnique().HasDatabaseName("ux_contactreads_biz_user_contact");

            // WhatsApp settings (principal with composite AK)
            modelBuilder.Entity<WhatsAppSettingEntity>(b =>
            {
                b.ToTable("WhatsAppSettings");
                b.HasAlternateKey(s => new { s.BusinessId, s.Provider })
                 .HasName("AK_WhatsAppSettings_BusinessId_Provider");

                // Remove redundant unique index on the same columns; keep other helpful indexes
                b.HasIndex(x => new { x.Provider, x.PhoneNumberId }).HasDatabaseName("IX_WhatsAppSettings_Provider_PhoneNumberId");
                b.HasIndex(x => new { x.Provider, x.WhatsAppBusinessNumber }).HasDatabaseName("IX_WhatsAppSettings_Provider_BusinessNumber");
                b.HasIndex(x => new { x.Provider, x.WabaId }).HasDatabaseName("IX_WhatsAppSettings_Provider_WabaId");
                b.HasIndex(x => new { x.BusinessId, x.Provider, x.IsActive }).HasDatabaseName("IX_WhatsAppSettings_Business_Provider_IsActive");
                b.HasIndex(x => new { x.Provider, x.WebhookCallbackUrl }).HasDatabaseName("IX_WhatsAppSettings_Provider_CallbackUrl");
            });

            modelBuilder.Entity<Business>()
                .HasMany(b => b.WhatsAppSettings).WithOne()
                .HasForeignKey(s => s.BusinessId).OnDelete(DeleteBehavior.Cascade);

            // WhatsApp phone numbers → principal (BusinessId, Provider)
            modelBuilder.Entity<WhatsAppPhoneNumber>(e =>
            {
                e.ToTable("WhatsAppPhoneNumbers");
                e.HasKey(x => x.Id);
                e.Property(x => x.Provider).IsRequired();
                e.Property(x => x.PhoneNumberId).IsRequired();

                e.HasOne<WhatsAppSettingEntity>()
                 .WithMany(s => s.WhatsAppBusinessNumbers)
                 .HasForeignKey(x => new { x.BusinessId, x.Provider })
                 .HasPrincipalKey(s => new { s.BusinessId, s.Provider })
                 .OnDelete(DeleteBehavior.Cascade);

                e.HasIndex(x => new { x.BusinessId, x.Provider, x.PhoneNumberId })
                 .IsUnique().HasDatabaseName("UX_WhatsappPhoneNumbers_Bus_Prov_PhoneId");
            });

            // CTA / Tracking misc
            modelBuilder.Entity<CampaignClickLog>(e =>
            {
                e.HasIndex(x => new { x.CampaignId, x.ClickType, x.ClickedAt });
                e.HasIndex(x => new { x.CampaignId, x.ButtonIndex });
                e.HasIndex(x => new { x.CampaignId, x.ContactId });
            });

            modelBuilder.Entity<CampaignClickDailyAgg>(e =>
            {
                e.HasIndex(x => new { x.CampaignId, x.Day, x.ButtonIndex }).IsUnique();
                e.Property(x => x.Day).HasColumnType("date");
            });

            // Flow graph bits
            modelBuilder.Entity<FlowButtonLink>().HasKey(b => b.Id);
            modelBuilder.Entity<AutoReplyFlowNode>().OwnsOne(n => n.Position);

            // Features/Plans
            modelBuilder.Entity<FeatureAccess>()
                .HasIndex(f => new { f.BusinessId, f.FeatureName }).IsUnique();

            // Outbound worker
            modelBuilder.Entity<OutboundCampaignJob>(e =>
            {
                e.ToTable("OutboundCampaignJobs");
                e.HasIndex(x => new { x.Status, x.NextAttemptAt });
                e.HasIndex(x => x.CampaignId);
                e.Property(x => x.Status).HasMaxLength(32);
                e.Property(x => x.LastError).HasMaxLength(4000);
            });

            modelBuilder.Entity<Contact>(entity =>
            {
                // Composite index for fast lookups during sends/clicks
                entity.HasIndex(e => new { e.BusinessId, e.PhoneNumber })
                      .HasDatabaseName("IX_Contacts_BusinessId_PhoneNumber");
                // .IsUnique(false); // optional (default is non-unique)
            });
            modelBuilder.Entity<Campaign>()
         .HasOne(c => c.CTAFlowConfig)      // ✅ use the nav that exists on Campaign
         .WithMany()
         .HasForeignKey(c => c.CTAFlowConfigId)
         .OnDelete(DeleteBehavior.Restrict);


            modelBuilder.Entity<CTAFlowConfig>(e =>
            {
                e.HasMany(f => f.Steps)
                 .WithOne(s => s.Flow)
                 .HasForeignKey(s => s.CTAFlowConfigId)   // 👈 use the existing FK name here
                 .OnDelete(DeleteBehavior.Cascade);

                e.HasIndex(f => new { f.BusinessId, f.IsActive, f.FlowName })
                 .HasDatabaseName("ix_ctaflowconfigs_biz_active_name");
            });

            modelBuilder.Entity<CTAFlowStep>(e =>
            {
                e.HasMany(s => s.ButtonLinks)
                 .WithOne(b => b.Step)                 // only if FlowButtonLink has a 'Step' nav
                 .HasForeignKey(b => b.CTAFlowStepId)  // ✅ use existing FK name
                 .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<CTAFlowConfig>()
              .HasIndex(f => new { f.BusinessId, f.FlowName, f.IsActive })
              .IsUnique();

            // ----- ProviderBillingEvents (core for billing dedupe + reads) -----
            modelBuilder.Entity<ProviderBillingEvent>(e =>
            {
                // Hard dedupe (webhook replays, same message/event). Filter keeps NULLs out of the unique constraint.
                e.HasIndex(x => new { x.BusinessId, x.Provider, x.ProviderMessageId, x.EventType })
                 .HasDatabaseName("UX_ProviderBillingEvents_UniqueEvent")
                 .IsUnique()
                 .HasFilter("\"ProviderMessageId\" IS NOT NULL");

                // Time-range scans by event type (used by snapshot)
                e.HasIndex(x => new { x.BusinessId, x.EventType, x.OccurredAt })
                 .HasDatabaseName("IX_Billing_BizEventTime");

                // Group/lookup by conversation window
                e.HasIndex(x => new { x.BusinessId, x.ConversationId })
                 .HasDatabaseName("IX_Billing_BizConversation")
                 .HasFilter("\"ConversationId\" IS NOT NULL");

                // Direct lookups by provider message id
                e.HasIndex(x => new { x.BusinessId, x.ProviderMessageId })
                 .HasDatabaseName("IX_Billing_BizProviderMessage")
                 .HasFilter("\"ProviderMessageId\" IS NOT NULL");
            });

            // ----- MessageLogs (snapshot volume + joins from billing) -----
            modelBuilder.Entity<MessageLog>(e =>
            {
                // Period queries
                e.HasIndex(x => new { x.BusinessId, x.CreatedAt })
                 .HasDatabaseName("IX_MessageLogs_BizCreatedAt");

                // Join from billing by provider message id
                e.HasIndex(x => new { x.BusinessId, x.ProviderMessageId })
                 .HasDatabaseName("IX_MessageLogs_BizProviderMessage")
                 .HasFilter("\"ProviderMessageId\" IS NOT NULL");

                // Conversation aggregation / backfills
                e.HasIndex(x => new { x.BusinessId, x.ConversationId })
                 .HasDatabaseName("IX_MessageLogs_BizConversation")
                 .HasFilter("\"ConversationId\" IS NOT NULL");
            });

            // ----- CampaignSendLogs (status updater lookups) -----
            modelBuilder.Entity<CampaignSendLog>(e =>
            {
                e.HasIndex(x => new { x.BusinessId, x.SendStatus, x.SentAt })
                 .HasDatabaseName("IX_CampaignSendLogs_StatusTime");
            });

            // ----- Optional: provider config lookups used during send/status -----
            // If your models differ, just delete this block or update the type/namespace.
            modelBuilder.Entity<WhatsAppSettingEntity>(e =>
            {
                // Matches queries like: WHERE BusinessId = ? AND Provider = ? AND IsActive
                e.HasIndex(x => new { x.BusinessId, x.Provider, x.IsActive })
                 .HasDatabaseName("IX_WhatsAppSettings_BizProviderActive");
            });

            modelBuilder.Entity<WhatsAppPhoneNumber>(e =>
            {
                e.HasIndex(x => new { x.BusinessId, x.Provider, x.PhoneNumberId })
                 .HasDatabaseName("IX_WhatsAppPhoneNumbers_BizProviderPhone");
            });
            modelBuilder.Entity<xbytechat.api.Features.CustomeApi.Models.ApiKey>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => x.Prefix).IsUnique();
                e.Property(x => x.SecretHash).IsRequired();
                e.Property(x => x.Scopes).HasMaxLength(512);
            });
            // NOTE: removed duplicate mapping of CampaignRecipient.AudienceMember at the bottom.
           

            //modelBuilder.Entity<CustomerWebhookConfig>()
            //    .HasIndex(x => new { x.BusinessId, x.IsActive });
        }

    }
}
//protected override void OnModelCreating(ModelBuilder modelBuilder)
//{
//    base.OnModelCreating(modelBuilder);

//    // ✅ Seed Role IDs (keep them consistent)
//    var superadminRoleId = Guid.Parse("00000000-0000-0000-0000-000000000001");
//    var partnerRoleId = Guid.Parse("00000000-0000-0000-0000-000000000002");
//    var resellerRoleId = Guid.Parse("00000000-0000-0000-0000-000000000003");
//    var businessRoleId = Guid.Parse("00000000-0000-0000-0000-000000000004");
//    var agentRoleId = Guid.Parse("00000000-0000-0000-0000-000000000005");

//    // ✅ Roles
//    modelBuilder.Entity<Role>().HasData(
//        new Role { Id = superadminRoleId, Name = "admin", Description = "Super Admin", CreatedAt = DateTime.UtcNow },
//        new Role { Id = partnerRoleId, Name = "partner", Description = "Business Partner", CreatedAt = DateTime.UtcNow },
//        new Role { Id = resellerRoleId, Name = "reseller", Description = "Reseller Partner", CreatedAt = DateTime.UtcNow },
//        new Role { Id = businessRoleId, Name = "business", Description = "Business Owner", CreatedAt = DateTime.UtcNow },
//        new Role { Id = agentRoleId, Name = "staff", Description = "Staff", CreatedAt = DateTime.UtcNow }
//    );

//    // ✅ Permissions from RolePermissionMapping


//    //// ✅ RolePermission mappings


//    // ✅ Seed Super Admin user (Id + RoleId are fixed)
//    var superAdminUserId = Guid.Parse("62858aa2-3a54-4fd5-8696-c343d9af7634");
//    modelBuilder.Entity<User>().HasData(new User
//    {
//        Id = superAdminUserId,
//        Name = "Super Admin",
//        Email = "admin@xbytechat.com",
//        RoleId = superadminRoleId,     // uses the constant defined above
//        Status = "active",
//        CreatedAt = DateTime.UtcNow,   // will be snapshotted into the migration
//        DeletedAt = null,
//        IsDeleted = false,
//        BusinessId = null,
//        PasswordHash = "JAvlGPq9JyTdtvBO6x2llnRI1+gxwIyPqCKAn3THIKk=",
//        RefreshToken = null,
//        RefreshTokenExpiry = null
//    });


//    // ===== Plans seed (idempotent) =====
//    var basicPlanId = Guid.Parse("5f9f5de1-a0b2-48ba-b03d-77b27345613f");
//    var planCreatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
//    modelBuilder.Entity<Plan>().HasData(new Plan
//    {
//        Id = basicPlanId,
//        Code = "basic",
//        Name = "Basic",
//        Description = "Default free plan",
//        // MonthlyQuota = 1000,
//        IsActive = true,
//        CreatedAt = planCreatedAt
//    });
//    // ===== Permissions seed

//    var created = new DateTime(2025, 9, 13, 0, 0, 0, DateTimeKind.Utc);

//    modelBuilder.Entity<Permission>().HasData(
//        new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000000"), Code = "dashboard.view", Name = "dashboard.view", Group = "Dashboard", Description = "Permission for dashboard.view", IsActive = true, CreatedAt = created },
//        new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000001"), Code = "campaign.view", Name = "campaign.view", Group = "Campaign", Description = "Permission for campaign.view", IsActive = true, CreatedAt = created },
//        new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000002"), Code = "campaign.create", Name = "campaign.create", Group = "Campaign", Description = "Permission for campaign.create", IsActive = true, CreatedAt = created },
//        new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000003"), Code = "campaign.delete", Name = "campaign.delete", Group = "Campaign", Description = "Permission for campaign.delete", IsActive = true, CreatedAt = created },
//        new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000004"), Code = "product.view", Name = "product.view", Group = "Catalog", Description = "Permission for product.view", IsActive = true, CreatedAt = created },
//        new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000005"), Code = "product.create", Name = "product.create", Group = "Catalog", Description = "Permission for product.create", IsActive = true, CreatedAt = created },
//        new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000006"), Code = "product.delete", Name = "product.delete", Group = "Catalog", Description = "Permission for product.delete", IsActive = true, CreatedAt = created },
//        new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000007"), Code = "contacts.view", Name = "contacts.view", Group = "CRM", Description = "Permission for contacts.view", IsActive = true, CreatedAt = created },
//        new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000008"), Code = "tags.edit", Name = "tags.edit", Group = null, Description = "Permission for tags.edit", IsActive = true, CreatedAt = created },
//        new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000009"), Code = "admin.business.approve", Name = "admin.business.approve", Group = "Admin", Description = "Permission for admin.business.approve", IsActive = true, CreatedAt = created },
//        new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000010"), Code = "admin.logs.view", Name = "admin.logs.view", Group = null, Description = "Permission for admin.logs.view", IsActive = true, CreatedAt = created },
//        new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000014"), Code = "admin.plans.delete", Name = "admin.plans.delete", Group = "Admin", Description = "Permission to delete plans", IsActive = true, CreatedAt = created },

//        new Permission { Id = Guid.Parse("74c8034f-d9cb-4a17-8578-a9f765bd845c"), Code = "messaging.report.view", Name = "messaging.report.view", Group = "Messaging", Description = "Permission for messaging.report.view", IsActive = true, CreatedAt = created },
//        new Permission { Id = Guid.Parse("c819f1bd-422d-4609-916c-cc185fe44ab0"), Code = "messaging.status.view", Name = "messaging.status.view", Group = "Messaging", Description = "Permission for messaging.status.view", IsActive = true, CreatedAt = created },
//        new Permission { Id = Guid.Parse("eecd0fac-223c-4dba-9fa1-2a6e973d61d1"), Code = "messaging.inbox.view", Name = "messaging.inbox.view", Group = "Messaging", Description = "Permission for messaging.inbox.view", IsActive = true, CreatedAt = created },
//        new Permission { Id = Guid.Parse("974af1f9-3caa-4857-a1a7-48462c389332"), Code = "messaging.send.text", Name = "messaging.send.text", Group = "Messaging", Description = "Permission for messaging.send.text", IsActive = true, CreatedAt = created },
//        new Permission { Id = Guid.Parse("0485154c-dde5-4732-a7aa-a379c77a5b27"), Code = "messaging.send.template", Name = "messaging.send.template", Group = "Messaging", Description = "Permission for messaging.send.template", IsActive = true, CreatedAt = created },
//        new Permission { Id = Guid.Parse("29461562-ef9c-48c0-a606-482ff57b8f95"), Code = "messaging.send", Name = "messaging.send", Group = "Messaging", Description = "Permission for messaging.send", IsActive = true, CreatedAt = created },
//        new Permission { Id = Guid.Parse("bbc5202a-eac9-40bb-aa78-176c677dbf5b"), Code = "messaging.whatsappsettings.view", Name = "messaging.whatsappsettings.view", Group = "Messaging", Description = "Permission for admin.whatsappsettings.view", IsActive = true, CreatedAt = created },

//        new Permission { Id = Guid.Parse("205b87c7-b008-4e51-9fea-798c2dc4f9c2"), Code = "admin.whatsappsettings.view", Name = "admin.whatsappsettings.view", Group = "Admin", Description = "Permission for admin.whatsappsettings.view", IsActive = true, CreatedAt = created },
//        new Permission { Id = Guid.Parse("6e4d3a86-7cf9-4ac2-b8a7-ed10c9f0173d"), Code = "settings.whatsapp.view", Name = "Settings - WhatsApp View", Group = "Settings", Description = "Permission for users to view & manage WhatsApp Settings", IsActive = true, CreatedAt = created },

//        new Permission { Id = Guid.Parse("ad36cdb7-5221-448b-a6a6-c35c9f88d021"), Code = "inbox.view", Name = "inbox.view", Group = "Inbox", Description = "Permission to Inbox View", IsActive = true, CreatedAt = created },
//        new Permission { Id = Guid.Parse("74828fc0-e358-4cfc-b924-13719a0d9f50"), Code = "inbox.menu", Name = "inbox.menu", Group = "Inbox", Description = "Permission to View Menu", IsActive = true, CreatedAt = created },

//        new Permission { Id = Guid.Parse("98572fe7-d142-475a-b990-f248641809e2"), Code = "settings.profile.view", Name = "Complete Profile", Group = "Settings", Description = "Permission to Complete Profile", IsActive = true, CreatedAt = created },

//        new Permission { Id = Guid.Parse("821480c6-1464-415e-bba8-066fcb4e7e63"), Code = "automation.menu", Name = "automation.menu", Group = "Automation", Description = "Permission to view automation menu", IsActive = true, CreatedAt = created },
//        new Permission { Id = Guid.Parse("918a61d0-5ab6-46af-a3d3-41e37b7710f9"), Code = "automation.Create.Template.Flow", Name = "automation.Create.Template.Flow", Group = "Automation", Description = "Permission to Create Template Flow", IsActive = true, CreatedAt = created },
//        new Permission { Id = Guid.Parse("9ae90cfe-3fea-4307-b024-3083c2728148"), Code = "automation.View.Template.Flow", Name = "automation.View.Template.Flow", Group = "Automation", Description = "Permission to View Template Flow", IsActive = true, CreatedAt = created },
//        new Permission { Id = Guid.Parse("93c5d5a7-f8dd-460a-8c7b-e3788440ba3a"), Code = "automation.Create.TemplatePlusFreetext.Flow", Name = "automation.Create.TemplatePlusFreetext.Flow", Group = "Automation", Description = "Permission to Create Template + Freetext Flow", IsActive = true, CreatedAt = created },
//        new Permission { Id = Guid.Parse("7d7cbceb-4ce7-4835-85cd-59562487298d"), Code = "automation.View.TemplatePlusFreetext.Flow", Name = "automation.View.TemplatePlusFreetext.Flow", Group = "Automation", Description = "Permission to View Template + Freetext Flow", IsActive = true, CreatedAt = created },
//        new Permission { Id = Guid.Parse("636b17f2-1c54-4e26-a8cd-dbf561dcb522"), Code = "automation.View.Template.Flow_analytics", Name = "automation.View.Template.Flow_analytics", Group = "Automation", Description = "Permission to View Flow AnaLytics", IsActive = true, CreatedAt = created },
//        new Permission { Id = Guid.Parse("adfa8490-9705-4a36-a86e-d5bff7ddc220"), Code = "automation.View.TemplatePlusFreeText.Flow_analytics", Name = "automation.View.TemplatePlusFreeText.Flow_analytics", Group = "Automation", Description = "Permission to View Templat Plus free text Flow AnaLytics", IsActive = true, CreatedAt = created },
//        new Permission { Id = Guid.Parse("0dedac5b-81c8-44c3-8cfe-76c58e29c6db"), Code = "automation_trigger_test", Name = "automation_trigger_test", Group = "Automation", Description = "Permission to to trigger manual test", IsActive = true, CreatedAt = created },

//        new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000011"), Code = "admin.plans.view", Name = "admin.plans.view", Group = "Admin", Description = "Permission to view plan manager", IsActive = true, CreatedAt = created },
//        new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000012"), Code = "admin.plans.create", Name = "admin.plans.create", Group = "Admin", Description = "Permission to create new plans", IsActive = true, CreatedAt = created },
//        new Permission { Id = Guid.Parse("30000000-0000-0000-0000-000000000013"), Code = "admin.plans.update", Name = "admin.plans.update", Group = "Admin", Description = "Permission to update existing plans", IsActive = true, CreatedAt = created }
//    );



//    // ========== 🧩 CORRECT RELATIONSHIPS ==========

//    // Role ↔️ RolePermission (One-to-Many)
//    modelBuilder.Entity<RolePermission>()
//        .HasOne(rp => rp.Role)
//        .WithMany(r => r.RolePermissions)
//        .HasForeignKey(rp => rp.RoleId)
//        .OnDelete(DeleteBehavior.Cascade);

//    // Permission ↔️ RolePermission (One-to-Many)
//    modelBuilder.Entity<RolePermission>()
//        .HasOne(rp => rp.Permission)
//        .WithMany(p => p.RolePermissions)
//        .HasForeignKey(rp => rp.PermissionId)
//        .OnDelete(DeleteBehavior.Cascade);

//    // User ↔️ UserPermission (One-to-Many)
//    modelBuilder.Entity<UserPermission>()
//        .HasOne(up => up.User)
//        .WithMany(u => u.UserPermissions)
//        .HasForeignKey(up => up.UserId)
//        .OnDelete(DeleteBehavior.Cascade);

//    // Permission ↔️ UserPermission (One-to-Many)
//    modelBuilder.Entity<UserPermission>()
//        .HasOne(up => up.Permission)
//        .WithMany(p => p.UserPermissions)
//        .HasForeignKey(up => up.PermissionId)
//        .OnDelete(DeleteBehavior.Cascade);

//    // ========== (Rest of your model mappings below remain the same) ==========

//    modelBuilder.Entity<CampaignSendLog>()
//        .HasOne(s => s.MessageLog)
//        .WithMany()
//        .HasForeignKey(s => s.MessageLogId)
//        .OnDelete(DeleteBehavior.Restrict);

//    modelBuilder.Entity<LeadTimeline>()
//        .HasOne(t => t.Contact)
//        .WithMany()
//        .HasForeignKey(t => t.ContactId);

//    modelBuilder.Entity<Campaign>()
//        .HasOne(c => c.Business)
//        .WithMany(b => b.Campaigns)
//        .HasForeignKey(c => c.BusinessId)
//        .IsRequired();

//    modelBuilder.Entity<CampaignRecipient>()
//        .HasOne(r => r.Campaign)
//        .WithMany(c => c.Recipients)
//        .HasForeignKey(r => r.CampaignId);

//    modelBuilder.Entity<CampaignRecipient>()
//        .HasOne(r => r.Contact)
//        .WithMany()
//        .HasForeignKey(r => r.ContactId)
//        .IsRequired(false)                      // <- optional FK
//        .OnDelete(DeleteBehavior.SetNull);

//    modelBuilder.Entity<CampaignRecipient>()
//        .HasOne(r => r.Business)
//        .WithMany()
//        .HasForeignKey(r => r.BusinessId)
//        .OnDelete(DeleteBehavior.Restrict);

//    modelBuilder.Entity<CampaignSendLog>()
//        .HasOne(s => s.Recipient)
//        .WithMany(r => r.SendLogs)
//        .HasForeignKey(s => s.RecipientId);

//    modelBuilder.Entity<CampaignSendLog>()
//        .HasOne(s => s.Contact)
//        .WithMany()
//        .HasForeignKey(s => s.ContactId);

//    modelBuilder.Entity<CampaignSendLog>()
//        .HasOne(s => s.Campaign)
//        .WithMany(c => c.SendLogs)
//        .HasForeignKey(s => s.CampaignId)
//        .OnDelete(DeleteBehavior.Cascade);

//    modelBuilder.Entity<ContactTag>()
//        .HasOne(ct => ct.Contact)
//        .WithMany(c => c.ContactTags)
//        .HasForeignKey(ct => ct.ContactId)
//        .OnDelete(DeleteBehavior.Cascade);

//    modelBuilder.Entity<ContactTag>()
//        .HasOne(ct => ct.Tag)
//        .WithMany(t => t.ContactTags)
//        .HasForeignKey(ct => ct.TagId)
//        .OnDelete(DeleteBehavior.Cascade);

//    modelBuilder.Entity<Role>()
//        .HasMany(r => r.Users)
//        .WithOne(u => u.Role)
//        .HasForeignKey(u => u.RoleId)
//        .OnDelete(DeleteBehavior.Restrict);

//    modelBuilder.Entity<Campaign>()
//        .HasMany(c => c.MultiButtons)
//        .WithOne(b => b.Campaign)
//        .HasForeignKey(b => b.CampaignId)
//        .OnDelete(DeleteBehavior.Cascade);

//    modelBuilder.Entity<MessageLog>()
//        .HasOne(m => m.SourceCampaign)
//        .WithMany(c => c.MessageLogs)
//        .HasForeignKey(m => m.CampaignId)
//        .OnDelete(DeleteBehavior.Restrict);

//    modelBuilder.Entity<CampaignSendLog>()
//        .Property(s => s.BusinessId)
//        .IsRequired();

//    modelBuilder.Entity<FlowButtonLink>()
//        .HasKey(b => b.Id);


//    modelBuilder.Entity<Business>()
//                   .HasMany(b => b.WhatsAppSettings)
//                   .WithOne()
//                   .HasForeignKey(s => s.BusinessId)
//                   .OnDelete(DeleteBehavior.Cascade);

//    modelBuilder.Entity<ContactRead>()
//        .HasIndex(cr => new { cr.ContactId, cr.UserId })
//        .IsUnique();

//    modelBuilder.Entity<AutoReplyFlowNode>()
//        .OwnsOne(n => n.Position);

//    modelBuilder.Entity<FeatureAccess>()
//    .HasIndex(f => new { f.BusinessId, f.FeatureName })
//    .IsUnique();

//    modelBuilder.Entity<WhatsAppTemplate>(e =>
//    {
//        e.Property(x => x.Body).HasColumnType("text");
//        e.Property(x => x.ButtonsJson).HasColumnType("text");
//        e.Property(x => x.RawJson).HasColumnType("text");
//    });
//    modelBuilder.Entity<CampaignClickLog>(e =>
//    {
//        e.HasIndex(x => new { x.CampaignId, x.ClickType, x.ClickedAt });
//        e.HasIndex(x => new { x.CampaignId, x.ButtonIndex });
//        e.HasIndex(x => new { x.CampaignId, x.ContactId });
//    });

//    modelBuilder.Entity<CampaignClickDailyAgg>(e =>
//    {
//        e.HasIndex(x => new { x.CampaignId, x.Day, x.ButtonIndex }).IsUnique();
//        e.Property(x => x.Day).HasColumnType("date");
//    });

//    modelBuilder.Entity<MessageLog>()
//    .HasIndex(x => x.MessageId);
//    modelBuilder.Entity<MessageLog>()
//        .HasIndex(x => x.RunId);

//    modelBuilder.Entity<CampaignSendLog>()
//        .HasIndex(x => x.MessageId);
//    modelBuilder.Entity<CampaignSendLog>()
//        .HasIndex(x => x.RunId);


//    // WhatsAppSettingEntity (principal)
//    modelBuilder.Entity<WhatsAppSettingEntity>(b =>
//    {
//        b.ToTable("WhatsAppSettings");

//        // SINGLE canonical principal key for the composite FK
//        b.HasAlternateKey(s => new { s.BusinessId, s.Provider })
//         .HasName("AK_WhatsAppSettings_BusinessId_Provider");

//        // Optional: also keep an index on it (not strictly needed if you have the AK)
//        b.HasIndex(s => new { s.BusinessId, s.Provider })
//         .IsUnique()
//         .HasDatabaseName("UX_WhatsAppSettings_BusinessId_Provider");

//        // (You can keep your other helper indexes if you want)
//        b.HasIndex(x => new { x.Provider, x.PhoneNumberId })
//         .HasDatabaseName("IX_WhatsAppSettings_Provider_PhoneNumberId");

//        b.HasIndex(x => new { x.Provider, x.WhatsAppBusinessNumber })
//         .HasDatabaseName("IX_WhatsAppSettings_Provider_BusinessNumber");

//        b.HasIndex(x => new { x.Provider, x.WabaId })
//         .HasDatabaseName("IX_WhatsAppSettings_Provider_WabaId");

//        b.HasIndex(x => new { x.BusinessId, x.Provider, x.IsActive })
//         .HasDatabaseName("IX_WhatsAppSettings_Business_Provider_IsActive");

//        b.HasIndex(x => new { x.Provider, x.WebhookCallbackUrl })
//         .HasDatabaseName("IX_WhatsAppSettings_Provider_CallbackUrl");

//        // REMOVE this if you previously had it: the Provider_ci computed column
//        // REMOVE the unique (BusinessId, Provider_ci) index as well
//    });
//    // ---------- CampaignSendLog composite index (fast status reconciliation) ----------
//    modelBuilder.Entity<CampaignSendLog>(b =>
//    {
//        b.HasIndex(x => new { x.BusinessId, x.MessageId })
//         .HasDatabaseName("IX_CampaignSendLogs_Business_MessageId");
//    });

//    // ---------- MessageLog composite indexes (fast joins & inbound lookups) ----------
//    modelBuilder.Entity<MessageLog>(b =>
//    {
//        b.HasIndex(x => new { x.BusinessId, x.MessageId })
//         .HasDatabaseName("IX_MessageLogs_Business_MessageId");

//        b.HasIndex(x => new { x.BusinessId, x.RecipientNumber })
//         .HasDatabaseName("IX_MessageLogs_Business_Recipient");
//    });

//    modelBuilder.Entity<Contact>()
//        .HasIndex(c => new { c.BusinessId, c.PhoneNumber })
//        .IsUnique();
//    // -------- ContactReads: one row per (Business, User, Contact) --------
//    modelBuilder.Entity<ContactRead>()
//        .HasIndex(cr => new { cr.BusinessId, cr.UserId, cr.ContactId })
//        .IsUnique()
//        .HasDatabaseName("ux_contactreads_biz_user_contact");

//    // -------- MessageLogs: computed column + composite index for unread --------
//    // Create a shadow computed column MessageTime = COALESCE(SentAt, CreatedAt)
//    modelBuilder.Entity<MessageLog>()
//        .Property<DateTime?>("MessageTime")
//        .HasComputedColumnSql("COALESCE(\"SentAt\", \"CreatedAt\")", stored: true);

//    // Composite index used by unread query:
//    // WHERE BusinessId = ? AND IsIncoming AND ContactId IS NOT NULL
//    // AND (SentAt ?? CreatedAt) > LastReadAt
//    modelBuilder.Entity<MessageLog>()
//        .HasIndex("BusinessId", "IsIncoming", "ContactId", "MessageTime")
//        .HasDatabaseName("ix_msglogs_biz_in_contact_msgtime");
//    // Quick Reply
//    modelBuilder.Entity<QuickReply>()
//        .HasIndex(q => new { q.BusinessId, q.Scope, q.IsActive });

//    modelBuilder.Entity<QuickReply>()
//        .HasIndex(q => new { q.BusinessId, q.OwnerUserId, q.IsActive });

//    modelBuilder.Entity<QuickReply>()
//        .Property(q => q.UpdatedAt)
//        .HasDefaultValueSql("NOW()");

//    modelBuilder.Entity<QuickReply>(e =>
//    {
//        e.HasIndex(x => new { x.BusinessId, x.Scope, x.IsDeleted, x.IsActive });
//        e.HasIndex(x => new { x.OwnerUserId, x.IsDeleted, x.IsActive });
//        e.HasIndex(x => x.UpdatedAt);
//        e.Property(x => x.Title).HasMaxLength(120).IsRequired();
//        e.Property(x => x.Language).HasMaxLength(8);
//    });

//    modelBuilder.Entity<WhatsAppPhoneNumber>(e =>
//    {
//        e.ToTable("WhatsAppPhoneNumbers");
//        e.HasKey(x => x.Id);

//        e.Property(x => x.Provider).IsRequired();
//        e.Property(x => x.PhoneNumberId).IsRequired();

//        // Composite FK → principal (BusinessId, Provider)
//        e.HasOne<WhatsAppSettingEntity>()
//         .WithMany(s => s.WhatsAppBusinessNumbers)           // keep your nav if you have it
//         .HasForeignKey(x => new { x.BusinessId, x.Provider })
//         .HasPrincipalKey(s => new { s.BusinessId, s.Provider }) // <-- expression overload
//         .OnDelete(DeleteBehavior.Cascade);

//        // Unique idempotency for upsert
//        e.HasIndex(x => new { x.BusinessId, x.Provider, x.PhoneNumberId })
//         .IsUnique()
//         .HasDatabaseName("UX_WhatsappPhoneNumbers_Bus_Prov_PhoneId");
//    });
//    modelBuilder.Entity<Campaign>(e =>
//    {
//        // Freeze provider schema snapshot on the campaign
//        e.Property(x => x.TemplateSchemaSnapshot).HasColumnType("jsonb");
//    });

//    modelBuilder.Entity<CampaignRecipient>(e =>
//    {
//        // Per-recipient frozen data produced by materializer
//        e.Property(x => x.ResolvedParametersJson).HasColumnType("jsonb");
//        e.Property(x => x.ResolvedButtonUrlsJson).HasColumnType("jsonb");

//        // Fast idempotency lookup to prevent duplicate sends
//        e.HasIndex(x => x.IdempotencyKey).HasDatabaseName("ix_campaignrecipients_idempotency");

//        e.HasOne<xbytechat.api.Features.CampaignModule.Models.AudienceMember>()
//         .WithMany().HasForeignKey(x => x.AudienceMemberId).OnDelete(DeleteBehavior.SetNull);
//    });

//    modelBuilder.Entity<xbytechat.api.Features.CampaignModule.Models.CampaignVariableMap>(e =>
//    {
//        e.ToTable("CampaignVariableMaps");

//        e.HasKey(x => x.Id);

//        // Scope: ensure uniqueness per (Campaign, Component, Index)
//        e.HasIndex(x => new { x.CampaignId, x.Component, x.Index })
//         .IsUnique()
//         .HasDatabaseName("ux_cvm_campaign_component_index");

//        // Relationship
//        e.HasOne(x => x.Campaign)
//         .WithMany(c => c.VariableMaps)             // add ICollection<CampaignVariableMap> VariableMaps { get; set; } to Campaign if you want navs later (optional)
//         .HasForeignKey(x => x.CampaignId)
//         .OnDelete(DeleteBehavior.Cascade);
//    });

//    modelBuilder.Entity<xbytechat.api.Features.CampaignModule.Models.CsvBatch>(e =>
//    {
//        e.ToTable("CsvBatches");
//        e.HasKey(x => x.Id);

//        e.Property(x => x.HeadersJson).HasColumnType("jsonb");

//        e.HasIndex(x => x.Checksum).HasDatabaseName("ix_csvbatch_checksum");
//        e.HasIndex(x => new { x.BusinessId, x.CreatedAt }).HasDatabaseName("ix_csvbatch_biz_created");
//    });

//    modelBuilder.Entity<CsvRow>(e =>
//    {
//        e.ToTable("CsvRows");
//        e.HasKey(x => x.Id);

//        e.Property(x => x.RowJson).HasColumnType("jsonb");

//        // Unique within a batch
//        e.HasIndex(x => new { x.BatchId, x.RowIndex })
//         .IsUnique()
//         .HasDatabaseName("ux_csvrow_batch_rowidx");

//        // Useful for fast joins/normalization checks
//        e.HasIndex(x => x.PhoneE164).HasDatabaseName("ix_csvrow_phone");

//        // FK → CsvBatch
//        e.HasOne(x => x.Batch)
//         .WithMany()
//         .HasForeignKey(x => x.BatchId)
//         .OnDelete(DeleteBehavior.Cascade);
//    });

//    modelBuilder.Entity<xbytechat.api.Features.CampaignModule.Models.Audience>(e =>
//    {
//        e.ToTable("Audiences");
//        e.HasKey(x => x.Id);

//        e.HasIndex(x => new { x.BusinessId, x.IsDeleted })
//         .HasDatabaseName("ix_audiences_biz_deleted");

//        // Optional link to the batch this audience came from
//        e.HasOne(x => x.CsvBatch)
//         .WithMany()
//         .HasForeignKey(x => x.CsvBatchId)
//         .OnDelete(DeleteBehavior.SetNull);
//    });

//    modelBuilder.Entity<AudienceMember>(e =>
//    {
//        e.ToTable("AudienceMembers");
//        e.HasKey(x => x.Id);

//        e.Property(x => x.AttributesJson).HasColumnType("jsonb");

//        // Prevent duplicate phone rows inside a single audience
//        e.HasIndex(x => new { x.AudienceId, x.PhoneE164 })
//         .IsUnique()
//         .HasDatabaseName("ux_audmember_audience_phone");

//        e.HasIndex(x => x.ContactId).HasDatabaseName("ix_audmember_contact");

//        e.HasOne(x => x.Audience)
//         .WithMany(a => a.Members)
//         .HasForeignKey(x => x.AudienceId)
//         .OnDelete(DeleteBehavior.Cascade);
//    });
//    modelBuilder.Entity<OutboundCampaignJob>(e =>
//    {
//        e.ToTable("OutboundCampaignJobs");
//        e.HasIndex(x => new { x.Status, x.NextAttemptAt });
//        e.HasIndex(x => x.CampaignId);
//        e.Property(x => x.Status).HasMaxLength(32);
//        e.Property(x => x.LastError).HasMaxLength(4000);
//    });
//    modelBuilder.Entity<CsvBatch>(b =>
//    {
//        b.HasIndex(x => new { x.BusinessId, x.AudienceId }); // helpful for lookups
//        b.HasOne<Audience>()                                  // no nav prop needed right now
//         .WithMany()
//         .HasForeignKey(x => x.AudienceId)
//         .OnDelete(DeleteBehavior.SetNull);                   // if audience is deleted, keep batch
//    });
//    modelBuilder.Entity<CsvRow>(b =>
//    {
//        // Unique per batch row
//        b.HasIndex(x => new { x.BatchId, x.RowIndex }).IsUnique();

//        // Helpful for multi-tenant queries by batch
//        b.HasIndex(x => new { x.BusinessId, x.BatchId });
//    });
//    // Audience FKs + helpful indices
//    modelBuilder.Entity<Audience>(b =>
//    {
//        // Helpful indexes
//        b.HasIndex(x => new { x.BusinessId, x.CampaignId });
//        b.HasIndex(x => new { x.BusinessId, x.CsvBatchId });

//        // Many Audiences -> one Campaign (dependent = Audience via CampaignId)
//        // Requires: Audience.Campaign (nav) and Campaign.Audiences (collection nav)
//        b.HasOne(a => a.Campaign)
//         .WithMany(c => c.Audiences)
//         .HasForeignKey(a => a.CampaignId)
//         .OnDelete(DeleteBehavior.SetNull);

//        // Optional backlink to source CSV batch (no back-collection needed)
//        b.HasOne(a => a.CsvBatch)
//         .WithMany()
//         .HasForeignKey(a => a.CsvBatchId)
//         .OnDelete(DeleteBehavior.SetNull);
//    });


//}
//}
//}
