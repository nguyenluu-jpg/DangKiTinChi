using CourseRegistration.CoreBusiness.Models;

namespace CourseRegistration.UseCases.PluginInterfaces.StateStore
{
    public interface ICurrentUserStateStore : IStateStore
    {
        Task<int> GetCurrentStudentIdAsync();
        Task SetCurrentStudentIdAsync(int studentId);
        Task<string> GetCurrentRoleAsync();
        Task SetCurrentRoleAsync(string role);
        Task<UserAccount?> GetCurrentUserAsync();
        Task SetCurrentUserAsync(UserAccount? account);
        Task<bool> IsAuthenticatedAsync();
        Task LogoutAsync();
    }
}
