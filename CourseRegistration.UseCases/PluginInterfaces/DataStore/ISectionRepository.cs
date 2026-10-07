using CourseRegistration.CoreBusiness.Models;

namespace CourseRegistration.UseCases.PluginInterfaces.DataStore
{
    /// <summary>
    /// Interface Repository cho Section (lớp học phần)
    /// Nằm trong UseCases, implementation ở Plugins (K1.2)
    /// </summary>
    public interface ISectionRepository
    {
        Task<IEnumerable<Section>> GetAllSectionsAsync();
        Task<IEnumerable<Section>> GetSectionsBySemesterAsync(string semester);
        Task<IEnumerable<Section>> SearchSectionsAsync(string keyword, string semester);
        Task<Section?> GetSectionByIdAsync(int sectionId);
        Task AddSectionAsync(Section section);
        Task UpdateSectionAsync(Section section);
        Task DeleteSectionAsync(int sectionId);

        // Schedule management
        Task AddScheduleAsync(SectionSchedule schedule);
        Task DeleteScheduleAsync(int scheduleId);
        Task<IEnumerable<SectionSchedule>> GetSchedulesBySectionIdAsync(int sectionId);

        /// <summary>
        /// Tăng sĩ số lớp (dùng trong transaction - LUẬT 2)
        /// Trả về true nếu còn chỗ, false nếu đầy
        /// </summary>
        Task<bool> IncrementEnrollmentCountAsync(int sectionId);

        /// <summary>
        /// Giảm sĩ số lớp (khi hủy đăng ký)
        /// </summary>
        Task DecrementEnrollmentCountAsync(int sectionId);
    }
}
