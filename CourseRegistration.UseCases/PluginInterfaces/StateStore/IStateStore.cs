namespace CourseRegistration.UseCases.PluginInterfaces.StateStore
{
    public interface IStateStore
    {
        void AddStateChangeListeners(Action listener);
        void RemoveStateChangeListeners(Action listener);
        void BroadcastStateChange();
    }
}
