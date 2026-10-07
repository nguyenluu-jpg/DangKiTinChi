using CourseRegistration.CoreBusiness.Models;

namespace CourseRegistration.UseCases.StudentPortal.SearchSectionScreen.Interfaces
{
    public interface ISearchSectionUseCase
    {
        Task<IEnumerable<Section>> ExecuteAsync(string keyword = "", string semester = "");
    }
}
