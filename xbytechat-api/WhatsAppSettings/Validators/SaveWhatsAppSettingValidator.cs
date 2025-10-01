// 📄 File: WhatsAppSettings/Validators/SaveWhatsAppSettingValidator.cs
using FluentValidation;
using xbytechat_api.WhatsAppSettings.DTOs;

namespace xbytechat_api.WhatsAppSettings.Validators
{
    public class SaveWhatsAppSettingValidator : AbstractValidator<SaveWhatsAppSettingDto>
    {
        public SaveWhatsAppSettingValidator()
        {
            RuleFor(x => x.Provider)
                .NotEmpty().WithMessage("Provider is required.")
                .Must(p => p == "pinnacle" || p == "meta_cloud")
                .WithMessage("Provider must be 'pinnacle' or 'meta_cloud'.");

            RuleFor(x => x.ApiUrl)
                .NotEmpty().WithMessage("API URL is required.");

            // Meta Cloud requirements
            When(x => x.Provider == "meta_cloud", () =>
            {
                RuleFor(x => x.ApiKey)
                    .NotEmpty().WithMessage("API ApiKey is required for Meta Cloud.");
                RuleFor(x => x.PhoneNumberId)
                    .NotEmpty().WithMessage("Phone Number ID is required for Meta Cloud.");
            });

            // Pinbot requirements
            When(x => x.Provider == "pinnacle", () =>
            {
                RuleFor(x => x.ApiKey)
                    .NotEmpty().WithMessage("API Key is required for Pinbot.");
                RuleFor(x => x)
                    .Must(x => !string.IsNullOrWhiteSpace(x.PhoneNumberId) || !string.IsNullOrWhiteSpace(x.WabaId))
                    .WithMessage("Provide Phone Number ID or WABA ID for Pinbot.");
            });
        }
    }
}
