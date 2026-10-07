namespace CourseRegistration.UseCases.StudentPortal.RegistrationCartScreen.Interfaces
{
    public interface IPlaceEnrollmentUseCase
    {
        Task<EnrollmentResult> ExecuteAsync(int studentId, string semester);
    }
}
