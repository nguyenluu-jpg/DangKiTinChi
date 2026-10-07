using CourseRegistration.CoreBusiness.Models;

namespace CourseRegistration.UseCases.PluginInterfaces.UI
{
    /// <summary>
    /// Interface cho Giỏ đăng ký - ánh xạ IShoppingCart trong eShop
    /// Dùng Scoped service (K1.4)
    /// </summary>
    public interface IRegistrationCart
    {
        Task<Enrollment> GetRegistrationCartAsync();
        Task AddSectionAsync(Section section);
        Task RemoveSectionAsync(int sectionId);
        Task<Enrollment> PlaceEnrollmentAsync();
        Task ClearCartAsync();
    }
}
