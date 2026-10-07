using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.UI;

namespace CourseRegistration.StateStore.DI
{
    /// <summary>
    /// Giỏ đăng ký tạm (Scoped Service) - ánh xạ ShoppingCart trong eShop
    /// </summary>
    public class RegistrationCart : IRegistrationCart
    {
        private readonly Enrollment _cartEnrollment;

        public RegistrationCart()
        {
            _cartEnrollment = new Enrollment
            {
                Status = "Draft",
                DateRegistered = DateTime.Now
            };
        }

        public Task<Enrollment> GetRegistrationCartAsync()
        {
            return Task.FromResult(_cartEnrollment);
        }

        public Task AddSectionAsync(Section section)
        {
            _cartEnrollment.AddSection(section.SectionId, section);
            return Task.CompletedTask;
        }

        public Task RemoveSectionAsync(int sectionId)
        {
            _cartEnrollment.RemoveSection(sectionId);
            return Task.CompletedTask;
        }

        public Task<Enrollment> PlaceEnrollmentAsync()
        {
            return Task.FromResult(_cartEnrollment);
        }

        public Task ClearCartAsync()
        {
            _cartEnrollment.EnrollmentLines.Clear();
            return Task.CompletedTask;
        }
    }
}
