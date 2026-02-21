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
        }
    }

    public class SeedrSettings : DownloadClientSettingsBase<SeedrSettings>
    {
        private static readonly SeedrSettingsValidator Validator = new ();

        public SeedrSettings()
        {
            DeleteFromCloud = true;
        }

        [FieldDefinition(0, Label = "Email", Type = FieldType.Textbox, Privacy = PrivacyLevel.UserName)]
        public string Email { get; set; }

        [FieldDefinition(1, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password)]
        public string Password { get; set; }

        [FieldDefinition(2, Label = "DownloadClientSeedrSettingsDownloadDirectory", Type = FieldType.Path, HelpText = "DownloadClientSeedrSettingsDownloadDirectoryHelpText")]
        public string DownloadDirectory { get; set; }

        [FieldDefinition(3, Label = "DownloadClientSeedrSettingsDeleteFromCloud", Type = FieldType.Checkbox, HelpText = "DownloadClientSeedrSettingsDeleteFromCloudHelpText", Advanced = true)]
        public bool DeleteFromCloud { get; set; }

        public override NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
