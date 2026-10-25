using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;

namespace CourseRegistration.UseCases.AdminPortal.ManageTuitionScreen
{
    public class ManageTuitionUseCase : IManageTuitionUseCase
    {
        private readonly IEnrollmentRepository _enrollmentRepository;

        public ManageTuitionUseCase(IEnrollmentRepository enrollmentRepository)
        {
            _enrollmentRepository = enrollmentRepository;
        }

        public async Task<IEnumerable<Enrollment>> GetEnrollmentsBySemesterAsync(string semester)
        {
            return await _enrollmentRepository.GetEnrollmentsBySemesterAsync(semester);
        }

        public async Task<TuitionStatistics> GetTuitionStatisticsAsync(string semester)
        {
            var enrollments = (await _enrollmentRepository.GetEnrollmentsBySemesterAsync(semester))
                .Where(e => e.Status == "Confirmed")
                .ToList();

            var stats = new TuitionStatistics
            {
                TotalRegisteredStudents = enrollments.Select(e => e.StudentId).Distinct().Count(),
                TotalCredits = enrollments.Sum(e => e.TotalCredits),
                TotalTuitionFee = enrollments.Sum(e => e.TuitionFee),
                PaidStudentsCount = enrollments.Where(e => e.IsPaid).Select(e => e.StudentId).Distinct().Count(),
                TotalPaidAmount = enrollments.Where(e => e.IsPaid).Sum(e => e.TuitionFee),
                UnpaidStudentsCount = enrollments.Where(e => !e.IsPaid).Select(e => e.StudentId).Distinct().Count(),
                TotalUnpaidAmount = enrollments.Where(e => !e.IsPaid).Sum(e => e.TuitionFee)
            };

            return stats;
        }

        public async Task<bool> ConfirmTuitionPaymentAsync(int enrollmentId, string paymentMethod, string? referenceNote = null)
        {
            string txRef = !string.IsNullOrWhiteSpace(referenceNote)
                ? referenceNote
                : $"ADM{DateTime.Now:yyyyMMddHHmmss}{enrollmentId:D3}";

            return await _enrollmentRepository.UpdatePaymentStatusAsync(enrollmentId, "Paid", paymentMethod, txRef);
        }

        public async Task<bool> ResetTuitionPaymentAsync(int enrollmentId)
        {
            return await _enrollmentRepository.UpdatePaymentStatusAsync(enrollmentId, "Unpaid", "Chưa thanh toán", string.Empty);
        }
    }
}
