using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.StateStore;
using CourseRegistration.UseCases.PluginInterfaces.UI;
using CourseRegistration.UseCases.StudentPortal.RegistrationCartScreen.Interfaces;

namespace CourseRegistration.UseCases.StudentPortal.RegistrationCartScreen
{
    public class AddSectionToCartUseCase : IAddSectionToCartUseCase
    {
        private readonly IRegistrationCart _registrationCart;
        private readonly IRegistrationCartStateStore _cartStateStore;

        public AddSectionToCartUseCase(
            IRegistrationCart registrationCart,
            IRegistrationCartStateStore cartStateStore)
        {
            _registrationCart = registrationCart;
            _cartStateStore = cartStateStore;
        }

        public async Task ExecuteAsync(Section section)
        {
            await _registrationCart.AddSectionAsync(section);
            _cartStateStore.BroadcastStateChange();
        }
    }
}
