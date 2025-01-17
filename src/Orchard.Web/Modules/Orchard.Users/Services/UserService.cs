using Orchard.ContentManagement;
using Orchard.DisplayManagement;
using Orchard.Environment.Configuration;
using Orchard.Localization;
using Orchard.Logging;
using Orchard.Messaging.Services;
using Orchard.Mvc.Html;
using Orchard.Security;
using Orchard.Services;
using Orchard.Settings;
using Orchard.Users.Constants;
using Orchard.Users.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Orchard.Users.Services {
    public class UserService : IUserService {
        private static readonly TimeSpan DelayToValidate = new TimeSpan(7, 0, 0, 0); // one week to validate email
        private static readonly TimeSpan DelayToResetPassword = new TimeSpan(1, 0, 0, 0); // 24 hours to reset password

        private readonly IContentManager _contentManager;
        private readonly IMembershipService _membershipService;
        private readonly IClock _clock;
        private readonly IMessageService _messageService;
        private readonly IEncryptionService _encryptionService;
        private readonly IShapeFactory _shapeFactory;
        private readonly IShapeDisplay _shapeDisplay;
        private readonly ISiteService _siteService;
        private readonly IPasswordHistoryService _passwordHistoryService;

        public UserService(
            IContentManager contentManager,
            IMembershipService membershipService,
            IClock clock,
            IMessageService messageService,
            ShellSettings shellSettings,
            IEncryptionService encryptionService,
            IShapeFactory shapeFactory,
            IShapeDisplay shapeDisplay,
            ISiteService siteService,
            IPasswordHistoryService passwordHistoryService) {

            _contentManager = contentManager;
            _membershipService = membershipService;
            _clock = clock;
            _messageService = messageService;
            _encryptionService = encryptionService;
            _shapeFactory = shapeFactory;
            _shapeDisplay = shapeDisplay;
            _siteService = siteService;
            _passwordHistoryService = passwordHistoryService;
            Logger = NullLogger.Instance;
            T = NullLocalizer.Instance;
        }

        public ILogger Logger { get; set; }
        public Localizer T { get; set; }

        public bool VerifyUserUnicity(string userName, string email) {
            string normalizedUserName = userName.ToLowerInvariant();

            if (_contentManager.Query<UserPart, UserPartRecord>()
                                   .Where(user =>
                                          user.NormalizedUserName == normalizedUserName ||
                                          user.Email == email)
                                   .List().Any()) {
                return false;
            }

            return true;
        }

        public bool VerifyUserUnicity(int id, string userName, string email) {
            string normalizedUserName = userName.ToLowerInvariant();

            if (_contentManager.Query<UserPart, UserPartRecord>()
                                   .Where(user =>
                                          user.NormalizedUserName == normalizedUserName ||
                                          user.Email == email)
                                   .List().Any(user => user.Id != id)) {
                return false;
            }

            return true;
        }

        public string CreateNonce(IUser user, TimeSpan delay) {
            var challengeToken = new XElement("n", new XAttribute("un", user.UserName), new XAttribute("utc", _clock.UtcNow.ToUniversalTime().Add(delay).ToString(CultureInfo.InvariantCulture))).ToString();
            var data = Encoding.UTF8.GetBytes(challengeToken);
            return Convert.ToBase64String(_encryptionService.Encode(data));
        }

        public bool DecryptNonce(string nonce, out string username, out DateTime validateByUtc) {
            username = null;
            validateByUtc = _clock.UtcNow;

            try {
                var data = _encryptionService.Decode(Convert.FromBase64String(nonce));
                var xml = Encoding.UTF8.GetString(data);
                var element = XElement.Parse(xml);
                username = element.Attribute("un").Value;
                validateByUtc = DateTime.Parse(element.Attribute("utc").Value, CultureInfo.InvariantCulture);
                return _clock.UtcNow <= validateByUtc;
            }
            catch {
                return false;
            }

        }

        public IUser ValidateChallenge(string nonce) {
            string username;
            DateTime validateByUtc;

            if (!DecryptNonce(nonce, out username, out validateByUtc)) {
                return null;
            }

            if (validateByUtc < _clock.UtcNow)
                return null;

            var user = _membershipService.GetUser(username);
            if (user == null)
                return null;
            
            if (user.As<UserPart>().EmailStatus == UserStatus.Approved) {
                return null;
            }
            
            user.As<UserPart>().EmailStatus = UserStatus.Approved;

            return user;
        }

        public void SendChallengeEmail(IUser user, Func<string, string> createUrl) {
            string nonce = CreateNonce(user, DelayToValidate);
            string url = createUrl(nonce);

            if (user != null) {
                var site = _siteService.GetSiteSettings();

                var template = _shapeFactory.Create("Template_User_Validated", Arguments.From(new {
                    RegisteredWebsite = site.As<RegistrationSettingsPart>().ValidateEmailRegisteredWebsite,
                    ContactEmail = site.As<RegistrationSettingsPart>().ValidateEmailContactEMail,
                    ChallengeUrl = url
                }));
                template.Metadata.Wrappers.Add("Template_User_Wrapper");

                var parameters = new Dictionary<string, object> {
                            {"Subject", T("Verification E-Mail").Text},
                            {"Body", _shapeDisplay.Display(template)},
                            {"Recipients", user.Email}
                        };

                _messageService.Send("Email", parameters);
            }
        }

        public bool SendLostPasswordEmail(string usernameOrEmail, Func<string, string> createUrl) {
            var user = GetUserByNameOrEmail(usernameOrEmail);

            if (user != null) {
                string nonce = CreateNonce(user, DelayToResetPassword);
                string url = createUrl(nonce);

                var template = _shapeFactory.Create("Template_User_LostPassword", Arguments.From(new {
                    User = user,
                    LostPasswordUrl = url
                }));
                template.Metadata.Wrappers.Add("Template_User_Wrapper");

                var parameters = new Dictionary<string, object> {
                            {"Subject", T("Lost password").Text},
                            {"Body", _shapeDisplay.Display(template)},
                            {"Recipients", user.Email }
                        };

                _messageService.Send("Email", parameters);
                return true;
            }

            return false;
        }

        public IUser ValidateLostPassword(string nonce) {
            string username;
            DateTime validateByUtc;

            if (!DecryptNonce(nonce, out username, out validateByUtc)) {
                return null;
            }

            if (validateByUtc < _clock.UtcNow)
                return null;

            var user = _membershipService.GetUser(username);
            if (user == null)
                return null;

            return user;
        }

        public bool PasswordMeetsPolicies(string password, IUser user, out IDictionary<string, LocalizedString> validationErrors) {
            validationErrors = new Dictionary<string, LocalizedString>();
            var settings = _siteService.GetSiteSettings().As<RegistrationSettingsPart>();

            if (string.IsNullOrEmpty(password)) {
                validationErrors.Add(UserPasswordValidationResults.PasswordIsTooShort,
                    T("The password can't be empty."));
                return false;
            }

            if (password.Length < settings.GetMinimumPasswordLength()) {
                validationErrors.Add(UserPasswordValidationResults.PasswordIsTooShort,
                    T("You must specify a password of {0} or more characters.", settings.MinimumPasswordLength));
            }

            if (settings.EnableCustomPasswordPolicy) {
                if (settings.EnablePasswordNumberRequirement && !Regex.Match(password, "[0-9]").Success) {
                    validationErrors.Add(UserPasswordValidationResults.PasswordDoesNotContainNumbers,
                        T("The password must contain at least one number."));
                }
                if (settings.EnablePasswordUppercaseRequirement && !password.Any(c => char.IsUpper(c))) {
                    validationErrors.Add(UserPasswordValidationResults.PasswordDoesNotContainUppercase,
                        T("The password must contain at least one uppercase letter."));
                }
                if (settings.EnablePasswordLowercaseRequirement && !password.Any(c => char.IsLower(c))) {
                    validationErrors.Add(UserPasswordValidationResults.PasswordDoesNotContainLowercase,
                        T("The password must contain at least one lowercase letter."));
                }
                if (settings.EnablePasswordSpecialRequirement && !Regex.Match(password, "[^a-zA-Z0-9]").Success) {
                    validationErrors.Add(UserPasswordValidationResults.PasswordDoesNotContainSpecialCharacters,
                        T("The password must contain at least one special character."));
                }
                if (settings.EnablePasswordHistoryPolicy) {
                    var enforcePasswordHistory = settings.GetPasswordReuseLimit();
                    if (_passwordHistoryService.PasswordMatchLastOnes(password, user, enforcePasswordHistory)) {
                        validationErrors.Add(UserPasswordValidationResults.PasswordDoesNotMeetHistoryPolicy,
                            T.Plural("You cannot reuse the last password.", "You cannot reuse none of last {0} passwords.", enforcePasswordHistory));
                    }
                }
            }

            return validationErrors.Count == 0;
        }



        public bool UsernameMeetsPolicies(string username, string email,  out List<UsernameValidationError> validationErrors) {
            validationErrors = new List<UsernameValidationError>();
            var settings = _siteService.GetSiteSettings().As<RegistrationSettingsPart>();

            if (string.IsNullOrEmpty(username)) {
                validationErrors.Add(new UsernameValidationError(Severity.Fatal, UsernameValidationResults.UsernameIsTooShort,
                    T("The username must not be empty."))); 
                return false;
            }

            // Validate username length to check it's not over 255.
            if (username.Length > UserPart.MaxUserNameLength) {
                validationErrors.Add(new UsernameValidationError(Severity.Fatal, UsernameValidationResults.UsernameIsTooLong,
                    T("The username can't be longer than {0} characters.", UserPart.MaxUserNameLength)));
                return false;
            }

            var usernameIsEmail = Regex.IsMatch(username, UserPart.EmailPattern, RegexOptions.IgnoreCase);

            if (usernameIsEmail && !username.Equals(email, StringComparison.OrdinalIgnoreCase)){
                validationErrors.Add(new UsernameValidationError(Severity.Fatal, UsernameValidationResults.UsernameAndEmailMustMatch,
                        T("If the username is an email it must match the specified email address.")));
                return false;
            }

            if (settings.EnableCustomUsernamePolicy) {                

                /// If the Maximum username length is smaller than the Minimum username length settings ignore this setting 
                if (settings.GetMaximumUsernameLength() >= settings.GetMinimumUsernameLength() && username.Length < settings.GetMinimumUsernameLength()) {
                    if (!settings.AllowEmailAsUsername || !usernameIsEmail) {
                        validationErrors.Add(new UsernameValidationError(Severity.Warning, UsernameValidationResults.UsernameIsTooShort,
                        T("You must specify a username of {0} or more characters.", settings.GetMinimumUsernameLength())));
                    }                    
                }

                /// If the Minimum username length is greater than the Maximum username length settings ignore this setting 
                if (settings.GetMaximumUsernameLength() >= settings.GetMinimumUsernameLength() && username.Length > settings.GetMaximumUsernameLength()) {
                    if (!settings.AllowEmailAsUsername || !usernameIsEmail) {
                        validationErrors.Add(new UsernameValidationError(Severity.Warning, UsernameValidationResults.UsernameIsTooLong,
                        T("You must specify a username of at most {0} characters.", settings.GetMaximumUsernameLength())));
                    }
                }

                if (settings.ForbidUsernameWhitespace && username.Any(x => char.IsWhiteSpace(x))) {
                    validationErrors.Add(new UsernameValidationError(Severity.Warning, UsernameValidationResults.UsernameContainsWhitespaces,
                        T("The username must not contain whitespaces.")));
                }

                if (settings.ForbidUsernameSpecialChars && Regex.Match(username, "[^a-zA-Z0-9]").Success) {
                    if (!settings.AllowEmailAsUsername || !usernameIsEmail) {
                        validationErrors.Add(new UsernameValidationError(Severity.Warning, UsernameValidationResults.UsernameContainsSpecialChars,
                        T("The username must not contain special characters.")));
                    }
                }
            }
            return validationErrors.Count == 0;
        }

        public UserPart GetUserByNameOrEmail(string usernameOrEmail) {
            var lowerName = usernameOrEmail.ToLowerInvariant();
            return _contentManager
                .Query<UserPart, UserPartRecord>()
                .Where(u => u.NormalizedUserName == lowerName || u.Email == lowerName)
                .Slice(0, 1)
                .FirstOrDefault();
        }
    }
}

