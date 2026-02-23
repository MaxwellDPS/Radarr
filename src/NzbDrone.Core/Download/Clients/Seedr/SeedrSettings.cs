using System.Text.RegularExpressions;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace NzbDrone.Core.Download.Clients.Seedr
{
    public class SeedrSettingsValidator : AbstractValidator<SeedrSettings>
    {
        public SeedrSettingsValidator()
        {
            RuleFor(c => c.Email).NotEmpty().EmailAddress();
            RuleFor(c => c.Password).NotEmpty();
            RuleFor(c => c.DownloadDirectory).IsValidPath();
            RuleFor(c => c.InstanceTag).NotEmpty().When(c => c.SharedAccount);
            RuleFor(c => c.InstanceTag).Matches(@"^[a-zA-Z0-9_-]+$", RegexOptions.None)
                .When(c => !string.IsNullOrWhiteSpace(c.InstanceTag));
        }
    }

    public class SeedrSettings : DownloadClientSettingsBase<SeedrSettings>
    {
        private static readonly SeedrSettingsValidator Validator = new ();

        public SeedrSettings()
        {
            DeleteFromCloud = true;
            SharedAccount = false;
            RedisConnectionString = "172.20.0.19:6379";
        }

        [FieldDefinition(0, Label = "Email", Type = FieldType.Textbox, Privacy = PrivacyLevel.UserName)]
        public string Email { get; set; }

        [FieldDefinition(1, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password)]
        public string Password { get; set; }

        [FieldDefinition(2, Label = "DownloadClientSeedrSettingsDownloadDirectory", Type = FieldType.Path, HelpText = "DownloadClientSeedrSettingsDownloadDirectoryHelpText")]
        public string DownloadDirectory { get; set; }

        [FieldDefinition(3, Label = "DownloadClientSeedrSettingsDeleteFromCloud", Type = FieldType.Checkbox, HelpText = "DownloadClientSeedrSettingsDeleteFromCloudHelpText", Advanced = true)]
        public bool DeleteFromCloud { get; set; }

        [FieldDefinition(4, Label = "DownloadClientSeedrSettingsSharedAccount", Type = FieldType.Checkbox, HelpText = "DownloadClientSeedrSettingsSharedAccountHelpText")]
        public bool SharedAccount { get; set; }

        [FieldDefinition(5, Label = "DownloadClientSeedrSettingsInstanceTag", Type = FieldType.Textbox, HelpText = "DownloadClientSeedrSettingsInstanceTagHelpText")]
        public string InstanceTag { get; set; }

        [FieldDefinition(6, Label = "DownloadClientSeedrSettingsRedisConnection", Type = FieldType.Textbox, HelpText = "DownloadClientSeedrSettingsRedisConnectionHelpText", Privacy = PrivacyLevel.Password)]
        public string RedisConnectionString { get; set; }

        public override NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
