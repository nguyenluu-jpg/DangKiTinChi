using System;
using System.Threading.Tasks;
using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;

namespace CourseRegistration.UseCases.StudentPortal.Payment
{
    public class ProcessTuitionPaymentUseCase : IProcessTuitionPaymentUseCase
    {
        private readonly IEnrollmentRepository _enrollmentRepository;

        public ProcessTuitionPaymentUseCase(IEnrollmentRepository enrollmentRepository)
        {
            _enrollmentRepository = enrollmentRepository;
        }

        public async Task<TuitionPaymentResult> ExecuteAsync(int enrollmentId, string paymentMethod = "VietQR Napas247", string? customReference = null)
        {
            var enrollment = await _enrollmentRepository.GetEnrollmentByIdAsync(enrollmentId);
            if (enrollment == null)
            {
                return new TuitionPaymentResult
                {
                    IsSuccess = false,
                    Message = $"Không tìm thấy phiếu đăng ký học phần #{enrollmentId}."
                };
            }

            if (enrollment.IsPaid)
            {
                return new TuitionPaymentResult
                {
                    IsSuccess = true,
                    Message = $"Phiếu đăng ký #{enrollmentId} đã được thanh toán học phí trước đó.",
                    TransactionReference = enrollment.TransactionReference ?? "N/A",
                    PaymentDate = enrollment.PaymentDate ?? DateTime.Now,
                    AmountPaid = enrollment.TuitionFee
                };
            }

            if (enrollment.TotalCredits <= 0)
            {
                return new TuitionPaymentResult
                {
                    IsSuccess = false,
                    Message = "Phiếu đăng ký không có tín chỉ hợp lệ để phát sinh học phí."
                };
            }

            // Sinh mã giao dịch chuẩn Napas / Ngân hàng (Ví dụ: VQR2026102214300588)
            var now = DateTime.Now;
            string txRef = !string.IsNullOrWhiteSpace(customReference)
                ? customReference
                : $"VQR{now:yyyyMMddHHmmss}{enrollment.EnrollmentId:D3}";

            bool updated = await _enrollmentRepository.UpdatePaymentStatusAsync(enrollmentId, "Paid", paymentMethod, txRef);
            if (!updated)
            {
                return new TuitionPaymentResult
                {
                    IsSuccess = false,
                    Message = "Lỗi trong quá trình cập nhật trạng thái thanh toán vào cơ sở dữ liệu."
                };
            }

            return new TuitionPaymentResult
            {
                IsSuccess = true,
                Message = $"Thanh toán học phí thành công qua {paymentMethod}!",
                TransactionReference = txRef,
                PaymentDate = now,
                AmountPaid = enrollment.TuitionFee
            };
        }
    }
}
