namespace CourseRegistration.UseCases.PluginInterfaces.StateStore
{
    public interface IRegistrationCartStateStore : IStateStore
    {
        Task<int> GetItemsCount();
    }
}
