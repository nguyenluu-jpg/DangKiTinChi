using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.AdminPortal.ManageSectionsScreen.Interfaces;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;

namespace CourseRegistration.UseCases.AdminPortal.ManageSectionsScreen
{
    public class ViewSectionsUseCase : IViewSectionsUseCase
    {
        private readonly ISectionRepository _sectionRepository;

        public ViewSectionsUseCase(ISectionRepository sectionRepository)
        {
            _sectionRepository = sectionRepository;
        }

        public async Task<IEnumerable<Section>> ExecuteAsync()
        {
            return await _sectionRepository.GetAllSectionsAsync();
        }
    }

    public class AddSectionUseCase : IAddSectionUseCase
    {
        private readonly ISectionRepository _sectionRepository;

        public AddSectionUseCase(ISectionRepository sectionRepository)
        {
            _sectionRepository = sectionRepository;
        }

        public async Task ExecuteAsync(Section section)
        {
            await _sectionRepository.AddSectionAsync(section);
        }
    }
}
