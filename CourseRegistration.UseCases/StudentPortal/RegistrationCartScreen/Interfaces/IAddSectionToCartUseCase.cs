using CourseRegistration.CoreBusiness.Models;

namespace CourseRegistration.UseCases.StudentPortal.RegistrationCartScreen.Interfaces
{
    public interface IAddSectionToCartUseCase
    {
        Task ExecuteAsync(Section section);
    }
}
