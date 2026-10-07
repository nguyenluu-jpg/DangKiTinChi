using CourseRegistration.CoreBusiness.Models;

namespace CourseRegistration.UseCases.AdminPortal.ManageSectionsScreen.Interfaces
{
    public interface IViewSectionsUseCase
    {
        Task<IEnumerable<Section>> ExecuteAsync();
    }

    public interface IAddSectionUseCase
    {
        Task ExecuteAsync(Section section);
    }
}
