namespace CourseRegistration.UseCases.StudentPortal.RegistrationCartScreen.Interfaces
{
    public interface IRemoveSectionFromCartUseCase
    {
        Task ExecuteAsync(int sectionId);
    }
}
