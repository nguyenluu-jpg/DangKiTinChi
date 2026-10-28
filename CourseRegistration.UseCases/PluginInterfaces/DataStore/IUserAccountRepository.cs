using CourseRegistration.CoreBusiness.Models;

namespace CourseRegistration.UseCases.PluginInterfaces.DataStore
{
    /// <summary>
    /// Repository xác thực và quản lý tài khoản người dùng (K1.2 - Interface ở UseCases, Implementation ở Plugins)
    /// </summary>
    public interface IUserAccountRepository
    {
        Task<UserAccount?> GetByUsernameAsync(string username);
        Task<UserAccount?> AuthenticateAsync(string username, string password);
        Task<UserAccount?> GetByIdAsync(int accountId);
        Task<IEnumerable<UserAccount>> GetAllAccountsAsync();
        Task<bool> UpdatePasswordAsync(int accountId, string newPassword);
    }
}
