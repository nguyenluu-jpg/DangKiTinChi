using CourseRegistration.UseCases.PluginInterfaces.StateStore;

namespace CourseRegistration.StateStore.DI
{
    public class CurrentUserStateStore : StateStore, ICurrentUserStateStore
    {
        private int _currentStudentId = 1; // Default to Student 1: SV21001 - Nguyễn Văn Tuấn
        private string _currentRole = "Student"; // "Student" or "Admin"

        public Task<int> GetCurrentStudentIdAsync()
        {
            return Task.FromResult(_currentStudentId);
        }

        public Task SetCurrentStudentIdAsync(int studentId)
        {
            if (_currentStudentId != studentId)
            {
                _currentStudentId = studentId;
                BroadcastStateChange();
            }
            return Task.CompletedTask;
        }

        public Task<string> GetCurrentRoleAsync()
        {
            return Task.FromResult(_currentRole);
        }

        public Task SetCurrentRoleAsync(string role)
        {
            if (_currentRole != role)
            {
                _currentRole = role;
                BroadcastStateChange();
            }
            return Task.CompletedTask;
        }
    }
}
