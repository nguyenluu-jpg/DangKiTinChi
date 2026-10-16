using CourseRegistration.CoreBusiness.Models;

namespace CourseRegistration.UseCases.Account
{
    public interface ILoginUseCase
    {
        Task<UserAccount?> ExecuteAsync(string username, string password);
    }
}
