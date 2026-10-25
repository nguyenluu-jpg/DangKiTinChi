using System;
using System.Threading.Tasks;

namespace CourseRegistration.UseCases.StudentPortal.Payment
{
    public class TuitionPaymentResult
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; } = string.Empty;
        public string TransactionReference { get; set; } = string.Empty;
        public DateTime PaymentDate { get; set; }
        public decimal AmountPaid { get; set; }
    }

    public interface IProcessTuitionPaymentUseCase
    {
        Task<TuitionPaymentResult> ExecuteAsync(int enrollmentId, string paymentMethod = "VietQR Napas247", string? customReference = null);
    }
}
