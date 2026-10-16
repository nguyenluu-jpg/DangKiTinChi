using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;

namespace CourseRegistration.UseCases.Account
{
    public class LoginUseCase : ILoginUseCase
    {
        private readonly IUserAccountRepository _userAccountRepository;

        public LoginUseCase(IUserAccountRepository userAccountRepository)
        {
            _userAccountRepository = userAccountRepository;
        }

        public async Task<UserAccount?> ExecuteAsync(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                return null;
            }

            return await _userAccountRepository.AuthenticateAsync(username.Trim(), password.Trim());
        }
    }
}
