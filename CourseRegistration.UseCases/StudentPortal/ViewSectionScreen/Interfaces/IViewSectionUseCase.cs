using CourseRegistration.CoreBusiness.Models;

namespace CourseRegistration.UseCases.StudentPortal.ViewSectionScreen.Interfaces
{
    public interface IViewSectionUseCase
    {
        Task<Section?> ExecuteAsync(int sectionId);
    }
}
