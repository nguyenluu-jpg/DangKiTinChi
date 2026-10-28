namespace CourseRegistration.UseCases.Account
{
    public interface IChangePasswordUseCase
    {
        Task<(bool Success, string Message)> ExecuteAsync(int accountId, string currentPassword, string newPassword, string confirmPassword);
    }
}
