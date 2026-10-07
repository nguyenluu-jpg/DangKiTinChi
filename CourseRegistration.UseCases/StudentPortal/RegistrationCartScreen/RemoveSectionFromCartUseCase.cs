using CourseRegistration.UseCases.PluginInterfaces.StateStore;
using CourseRegistration.UseCases.PluginInterfaces.UI;
using CourseRegistration.UseCases.StudentPortal.RegistrationCartScreen.Interfaces;

namespace CourseRegistration.UseCases.StudentPortal.RegistrationCartScreen
{
    public class RemoveSectionFromCartUseCase : IRemoveSectionFromCartUseCase
    {
        private readonly IRegistrationCart _registrationCart;
        private readonly IRegistrationCartStateStore _cartStateStore;

        public RemoveSectionFromCartUseCase(
            IRegistrationCart registrationCart,
            IRegistrationCartStateStore cartStateStore)
        {
            _registrationCart = registrationCart;
            _cartStateStore = cartStateStore;
        }

        public async Task ExecuteAsync(int sectionId)
        {
            await _registrationCart.RemoveSectionAsync(sectionId);
            _cartStateStore.BroadcastStateChange();
        }
    }
}
