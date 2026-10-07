using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.UI;
using CourseRegistration.UseCases.StudentPortal.RegistrationCartScreen.Interfaces;

namespace CourseRegistration.UseCases.StudentPortal.RegistrationCartScreen
{
    public class ViewRegistrationCartUseCase : IViewRegistrationCartUseCase
    {
        private readonly IRegistrationCart _registrationCart;

        public ViewRegistrationCartUseCase(IRegistrationCart registrationCart)
        {
            _registrationCart = registrationCart;
        }

        public async Task<Enrollment> ExecuteAsync()
        {
            return await _registrationCart.GetRegistrationCartAsync();
        }
    }
}
