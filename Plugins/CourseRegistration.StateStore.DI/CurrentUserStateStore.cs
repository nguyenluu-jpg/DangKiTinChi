using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.StateStore;

namespace CourseRegistration.StateStore.DI
{
    public class CurrentUserStateStore : StateStore, ICurrentUserStateStore
    {
        // Mặc định tài khoản ban đầu khi mở web / F5: Đăng nhập sẵn Sinh viên 1 (SV21001 - Nguyễn Văn Tuấn)
        private UserAccount? _currentUser = new UserAccount
        {
            AccountId = 2,
            Username = "sv21001",
            FullName = "Nguyễn Văn Tuấn",
            Role = "Student",
            StudentId = 1
        };
        private int _currentStudentId = 1;
        private string _currentRole = "Student";

        public Task<int> GetCurrentStudentIdAsync()
        {
            return Task.FromResult(_currentStudentId);
        }

        public Task SetCurrentStudentIdAsync(int studentId)
        {
            if (_currentStudentId != studentId)
            {
                _currentStudentId = studentId;
                if (_currentUser != null && _currentUser.Role == "Student")
                {
                    _currentUser.StudentId = studentId;
                }
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
                if (_currentUser != null)
                {
                    _currentUser.Role = role;
                }
                BroadcastStateChange();
            }
            return Task.CompletedTask;
        }

        public Task<UserAccount?> GetCurrentUserAsync()
        {
            return Task.FromResult(_currentUser);
        }

        public Task SetCurrentUserAsync(UserAccount? account)
        {
            _currentUser = account;
            if (account != null)
            {
                _currentRole = account.Role;
                _currentStudentId = account.StudentId ?? 0;
            }
            else
            {
                _currentRole = string.Empty;
                _currentStudentId = 0;
            }
            BroadcastStateChange();
            return Task.CompletedTask;
        }

        public Task<bool> IsAuthenticatedAsync()
        {
            return Task.FromResult(_currentUser != null && !string.IsNullOrEmpty(_currentUser.Username));
        }

        public Task LogoutAsync()
        {
            _currentUser = null;
            _currentRole = string.Empty;
            _currentStudentId = 0;
            BroadcastStateChange();
            return Task.CompletedTask;
        }
    }
}
