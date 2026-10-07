using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;
using CourseRegistration.UseCases.StudentPortal.SearchSectionScreen.Interfaces;

namespace CourseRegistration.UseCases.StudentPortal.SearchSectionScreen
{
    public class SearchSectionUseCase : ISearchSectionUseCase
    {
        private readonly ISectionRepository _sectionRepository;

        public SearchSectionUseCase(ISectionRepository sectionRepository)
        {
            _sectionRepository = sectionRepository;
        }

        public async Task<IEnumerable<Section>> ExecuteAsync(string keyword = "", string semester = "")
        {
            if (string.IsNullOrWhiteSpace(keyword) && string.IsNullOrWhiteSpace(semester))
            {
                return await _sectionRepository.GetAllSectionsAsync();
            }

            return await _sectionRepository.SearchSectionsAsync(keyword, semester);
        }
    }
}
