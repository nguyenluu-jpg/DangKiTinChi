namespace CourseRegistration.UseCases.PluginInterfaces.StateStore
{
    public interface ICurrentUserStateStore : IStateStore
    {
        Task<int> GetCurrentStudentIdAsync();
        Task SetCurrentStudentIdAsync(int studentId);
        Task<string> GetCurrentRoleAsync();
        Task SetCurrentRoleAsync(string role);
    }
}
