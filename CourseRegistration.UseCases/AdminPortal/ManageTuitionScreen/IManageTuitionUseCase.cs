using CourseRegistration.CoreBusiness.Models;

namespace CourseRegistration.UseCases.AdminPortal.ManageTuitionScreen
{
    public class TuitionStatistics
    {
        public int TotalRegisteredStudents { get; set; }
        public int TotalCredits { get; set; }
        public decimal TotalTuitionFee { get; set; }
        public int PaidStudentsCount { get; set; }
        public decimal TotalPaidAmount { get; set; }
        public int UnpaidStudentsCount { get; set; }
        public decimal TotalUnpaidAmount { get; set; }
        public double CompletionRate => TotalTuitionFee > 0 ? (double)(TotalPaidAmount / TotalTuitionFee) * 100 : 0;
    }

    public interface IManageTuitionUseCase
    {
        Task<IEnumerable<Enrollment>> GetEnrollmentsBySemesterAsync(string semester);
        Task<TuitionStatistics> GetTuitionStatisticsAsync(string semester);
        Task<bool> ConfirmTuitionPaymentAsync(int enrollmentId, string paymentMethod, string? referenceNote = null);
        Task<bool> ResetTuitionPaymentAsync(int enrollmentId);
    }
}
