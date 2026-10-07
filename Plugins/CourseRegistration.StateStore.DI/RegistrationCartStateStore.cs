using CourseRegistration.UseCases.PluginInterfaces.StateStore;
using CourseRegistration.UseCases.PluginInterfaces.UI;

namespace CourseRegistration.StateStore.DI
{
    public class RegistrationCartStateStore : StateStore, IRegistrationCartStateStore
    {
        private readonly IRegistrationCart _registrationCart;

        public RegistrationCartStateStore(IRegistrationCart registrationCart)
        {
            _registrationCart = registrationCart;
        }

        public async Task<int> GetItemsCount()
        {
            var cart = await _registrationCart.GetRegistrationCartAsync();
            return cart?.EnrollmentLines?.Count ?? 0;
        }
    }
}
