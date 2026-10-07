using CourseRegistration.UseCases.PluginInterfaces.StateStore;

namespace CourseRegistration.StateStore.DI
{
    public class StateStore : IStateStore
    {
        protected Action? _listeners;

        public void AddStateChangeListeners(Action listener)
        {
            _listeners += listener;
        }

        public void RemoveStateChangeListeners(Action listener)
        {
            _listeners -= listener;
        }

        public void BroadcastStateChange()
        {
            _listeners?.Invoke();
        }
    }
}
