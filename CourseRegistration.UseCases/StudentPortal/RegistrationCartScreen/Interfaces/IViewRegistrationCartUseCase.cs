using CourseRegistration.CoreBusiness.Models;

namespace CourseRegistration.UseCases.StudentPortal.RegistrationCartScreen.Interfaces
{
    public interface IViewRegistrationCartUseCase
    {
        Task<Enrollment> ExecuteAsync();
    }
}
