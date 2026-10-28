using CourseRegistration.UseCases.PluginInterfaces.DataStore;

namespace CourseRegistration.UseCases.Account
{
    public class ChangePasswordUseCase : IChangePasswordUseCase
    {
        private readonly IUserAccountRepository _userAccountRepository;

        public ChangePasswordUseCase(IUserAccountRepository userAccountRepository)
        {
            _userAccountRepository = userAccountRepository;
        }

        public async Task<(bool Success, string Message)> ExecuteAsync(int accountId, string currentPassword, string newPassword, string confirmPassword)
        {
            if (accountId <= 0)
            {
                return (false, "Không tìm thấy thông tin tài khoản hợp lệ.");
            }

            if (string.IsNullOrWhiteSpace(currentPassword))
            {
                return (false, "Vui lòng nhập mật khẩu hiện tại.");
            }

            if (string.IsNullOrWhiteSpace(newPassword))
            {
                return (false, "Vui lòng nhập mật khẩu mới.");
            }

            if (newPassword.Trim().Length < 6)
            {
                return (false, "Mật khẩu mới phải có tối thiểu 6 ký tự.");
            }

            if (string.IsNullOrWhiteSpace(confirmPassword))
            {
                return (false, "Vui lòng xác nhận lại mật khẩu mới.");
            }

            if (newPassword != confirmPassword)
            {
                return (false, "Mật khẩu mới và xác nhận mật khẩu không trùng khớp.");
            }

            if (currentPassword == newPassword)
            {
                return (false, "Mật khẩu mới không được trùng với mật khẩu hiện tại.");
            }

            var account = await _userAccountRepository.GetByIdAsync(accountId);
            if (account == null)
            {
                return (false, "Tài khoản không tồn tại trên hệ thống.");
            }

            if (account.Password != currentPassword)
            {
                return (false, "Mật khẩu hiện tại không chính xác. Vui lòng kiểm tra lại.");
            }

            var updated = await _userAccountRepository.UpdatePasswordAsync(accountId, newPassword.Trim());
            if (!updated)
            {
                return (false, "Không thể cập nhật mật khẩu lúc này. Vui lòng thử lại sau.");
            }

            return (true, "Đổi mật khẩu thành công!");
        }
    }
}
