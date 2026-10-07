using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.CoreBusiness.Services.Interfaces;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;
using CourseRegistration.UseCases.PluginInterfaces.StateStore;
using CourseRegistration.UseCases.PluginInterfaces.UI;
using CourseRegistration.UseCases.StudentPortal.RegistrationCartScreen.Interfaces;

namespace CourseRegistration.UseCases.StudentPortal.RegistrationCartScreen
{
    /// <summary>
    /// USE CASE XÁC NHẬN ĐĂNG KÝ HỌC PHẦN
    /// Phối hợp kiểm tra:
    /// - LUẬT 1: Tiên quyết, Trùng lịch, Tín chỉ tối đa (thông qua IEnrollmentValidationService)
    /// - LUẬT 2: Tranh chấp sĩ số chỗ cuối (kiểm tra sĩ số và cập nhật thông qua repository)
    /// </summary>
    public class PlaceEnrollmentUseCase : IPlaceEnrollmentUseCase
    {
        private readonly IRegistrationCart _registrationCart;
        private readonly IRegistrationCartStateStore _cartStateStore;
        private readonly IEnrollmentValidationService _validationService;
        private readonly IEnrollmentRepository _enrollmentRepository;
        private readonly ISectionRepository _sectionRepository;
        private readonly IStudentRepository _studentRepository;
        private readonly ICourseRepository _courseRepository;

        public PlaceEnrollmentUseCase(
            IRegistrationCart registrationCart,
            IRegistrationCartStateStore cartStateStore,
            IEnrollmentValidationService validationService,
            IEnrollmentRepository enrollmentRepository,
            ISectionRepository sectionRepository,
            IStudentRepository studentRepository,
            ICourseRepository courseRepository)
        {
            _registrationCart = registrationCart;
            _cartStateStore = cartStateStore;
            _validationService = validationService;
            _enrollmentRepository = enrollmentRepository;
            _sectionRepository = sectionRepository;
            _studentRepository = studentRepository;
            _courseRepository = courseRepository;
        }

        public async Task<EnrollmentResult> ExecuteAsync(int studentId, string semester)
        {
            var cart = await _registrationCart.GetRegistrationCartAsync();
            if (cart.EnrollmentLines == null || !cart.EnrollmentLines.Any())
            {
                return new EnrollmentResult
                {
                    IsSuccess = false,
                    Errors = new List<string> { "Giỏ đăng ký đang trống. Vui lòng chọn lớp học phần trước." }
                };
            }

            var student = await _studentRepository.GetStudentByIdAsync(studentId);
            if (student == null)
            {
                return new EnrollmentResult
                {
                    IsSuccess = false,
                    Errors = new List<string> { "Không tìm thấy thông tin sinh viên." }
                };
            }

            var completedCourses = (await _studentRepository.GetCompletedCoursesByStudentIdAsync(studentId)).ToList();
            var prerequisites = (await _courseRepository.GetAllPrerequisitesAsync()).ToList();
            var registeredSections = (await _enrollmentRepository.GetRegisteredSectionsByStudentAsync(studentId, semester)).ToList();

            var allErrors = new List<string>();
            var accumulatedSections = new List<Section>(registeredSections);

            // Kiểm tra từng lớp trong giỏ
            foreach (var line in cart.EnrollmentLines)
            {
                var section = line.Section ?? await _sectionRepository.GetSectionByIdAsync(line.SectionId);
                if (section == null)
                {
                    allErrors.Add($"Không tìm thấy thông tin lớp học phần ID: {line.SectionId}");
                    continue;
                }

                // LUẬT 2: Sĩ số không vượt giới hạn (kể cả tranh chấp chỗ cuối)
                if (section.CurrentEnrollment >= section.MaxCapacity)
                {
                    allErrors.Add($"Lớp {section.SectionCode} ({section.Course?.CourseName}) đã hết chỗ (sĩ số {section.CurrentEnrollment}/{section.MaxCapacity}).");
                }

                // LUẬT 1: Tiên quyết, Trùng lịch, Tín chỉ tối đa
                var ruleErrors = _validationService.ValidateEnrollment(
                    section,
                    accumulatedSections,
                    completedCourses,
                    prerequisites,
                    student.MaxCreditsPerSemester);

                if (ruleErrors.Any())
                {
                    allErrors.AddRange(ruleErrors);
                }
                else
                {
                    // Tạm thêm vào danh sách đã tích lũy để kiểm tra trùng chéo giữa các môn trong cùng 1 giỏ
                    accumulatedSections.Add(section);
                }
            }

            // Nếu có bất kỳ vi phạm nào, từ chối giao dịch và trả về toàn bộ lỗi
            if (allErrors.Any())
            {
                return new EnrollmentResult
                {
                    IsSuccess = false,
                    Errors = allErrors
                };
            }

            // Tạo phiếu đăng ký chính thức
            var newEnrollment = new Enrollment
            {
                StudentId = studentId,
                Semester = semester,
                DateRegistered = DateTime.Now,
                Status = "Confirmed",
                UniqueId = Guid.NewGuid().ToString()
            };

            foreach (var line in cart.EnrollmentLines)
            {
                newEnrollment.AddSection(line.SectionId, line.Section!);
            }

            // Lưu Header - Line trong transaction của Repository
            var enrollmentId = await _enrollmentRepository.CreateEnrollmentAsync(newEnrollment);

            // Cập nhật sĩ số các lớp
            foreach (var line in cart.EnrollmentLines)
            {
                await _sectionRepository.IncrementEnrollmentCountAsync(line.SectionId);
            }

            // Xóa giỏ hàng và cập nhật UI State
            await _registrationCart.ClearCartAsync();
            _cartStateStore.BroadcastStateChange();

            return new EnrollmentResult
            {
                IsSuccess = true,
                EnrollmentId = enrollmentId
            };
        }
    }
}
