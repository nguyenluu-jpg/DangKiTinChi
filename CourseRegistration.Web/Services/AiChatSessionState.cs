using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.StateStore;

namespace CourseRegistration.Web.Services
{
    /// <summary>
    /// Scoped Service lưu trữ trạng thái phiên hội thoại AI dùng chung giữa Widget thu nhỏ và trang /ai-advisor.
    /// Cho phép chuyển đổi linh hoạt qua lại giữa Widget và toàn màn hình mà không bị mất tin nhắn,
    /// đồng thời tự động dọn dẹp khi người dùng đăng xuất, đổi tài khoản hoặc đổi vai trò.
    /// </summary>
    public class AiChatSessionState : IDisposable
    {
        private readonly ICurrentUserStateStore? _userStateStore;
        private string? _lastUserKey;

        public List<AiConversationMessage> Messages { get; } = new List<AiConversationMessage>();

        /// <summary>
        /// Đánh dấu khi người dùng bấm "Thu nhỏ về chat box" từ trang /ai-advisor,
        /// Widget trên trang đích sẽ tự động mở sẵn cửa sổ chat.
        /// </summary>
        public bool IsWidgetChatOpen { get; set; } = false;

        public event Action? OnChange;

        public AiChatSessionState(ICurrentUserStateStore? userStateStore = null)
        {
            _userStateStore = userStateStore;
            if (_userStateStore != null)
            {
                _lastUserKey = GetCurrentStateKey();
                _userStateStore.AddStateChangeListeners(HandleUserStateChange);
            }
        }

        private string GetCurrentStateKey()
        {
            if (_userStateStore == null) return "Guest_0_";
            try
            {
                var role = _userStateStore.GetCurrentRoleAsync().GetAwaiter().GetResult();
                var studentId = _userStateStore.GetCurrentStudentIdAsync().GetAwaiter().GetResult();
                var user = _userStateStore.GetCurrentUserAsync().GetAwaiter().GetResult();
                return $"{role}_{studentId}_{user?.Username}";
            }
            catch
            {
                return "Guest_0_";
            }
        }

        private void HandleUserStateChange()
        {
            var currentKey = GetCurrentStateKey();
            if (_lastUserKey != currentKey)
            {
                _lastUserKey = currentKey;
                Clear();
            }
        }

        public void AddMessage(AiConversationMessage message)
        {
            Messages.Add(message);
            NotifyStateChanged();
        }

        public void Clear()
        {
            Messages.Clear();
            NotifyStateChanged();
        }

        public void NotifyStateChanged()
        {
            OnChange?.Invoke();
        }

        public void Dispose()
        {
            if (_userStateStore != null)
            {
                _userStateStore.RemoveStateChangeListeners(HandleUserStateChange);
            }
        }
    }
}
