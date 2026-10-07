using CourseRegistration.CoreBusiness.Models;

namespace CourseRegistration.UseCases.StudentPortal.ViewScheduleScreen.Interfaces
{
    public interface IViewScheduleUseCase
    {
        Task<IEnumerable<Section>> ExecuteAsync(int studentId, string semester);
    }
}
