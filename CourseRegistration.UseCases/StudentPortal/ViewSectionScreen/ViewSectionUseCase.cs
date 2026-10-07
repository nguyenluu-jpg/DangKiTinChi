using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;
using CourseRegistration.UseCases.StudentPortal.ViewSectionScreen.Interfaces;

namespace CourseRegistration.UseCases.StudentPortal.ViewSectionScreen
{
    public class ViewSectionUseCase : IViewSectionUseCase
    {
        private readonly ISectionRepository _sectionRepository;

        public ViewSectionUseCase(ISectionRepository sectionRepository)
        {
            _sectionRepository = sectionRepository;
        }

        public async Task<Section?> ExecuteAsync(int sectionId)
        {
            return await _sectionRepository.GetSectionByIdAsync(sectionId);
        }
    }
}
