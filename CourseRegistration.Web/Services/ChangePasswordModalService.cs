namespace CourseRegistration.Web.Services
{
    public interface IChangePasswordModalService
    {
        bool IsVisible { get; }
        event Action? OnChange;
        void Open();
        void Close();
    }

    public class ChangePasswordModalService : IChangePasswordModalService
    {
        public bool IsVisible { get; private set; } = false;

        public event Action? OnChange;

        public void Open()
        {
            if (!IsVisible)
            {
                IsVisible = true;
                OnChange?.Invoke();
            }
        }

        public void Close()
        {
            if (IsVisible)
            {
                IsVisible = false;
                OnChange?.Invoke();
            }
        }
    }
}
