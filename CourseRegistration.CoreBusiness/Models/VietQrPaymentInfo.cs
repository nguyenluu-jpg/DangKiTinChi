using System;

namespace CourseRegistration.CoreBusiness.Models
{
    /// <summary>
    /// Thông tin thanh toán học phí qua chuẩn mã phản hồi nhanh VietQR (Napas247)
    /// </summary>
    public class VietQrPaymentInfo
    {
        public string BankCode { get; set; } = "MB";
        public string BankName { get; set; } = "Ngân hàng TMCP Quân Đội (MB Bank)";
        public string AccountNumber { get; set; } = "101705201967";
        public string AccountName { get; set; } = "TRUONG DAI HOC KINH TE";
        public decimal Amount { get; set; }
        public string Description { get; set; } = string.Empty;
        public string Template { get; set; } = "compact2";

        /// <summary>
        /// Tạo cú pháp nội dung chuyển khoản học phí chuẩn:
        /// HP [Mã sinh viên] [Họ tên] [Học kỳ]
        /// Ví dụ: HP SV21001 Nguyen Van Tuan 2026_HK1
        /// </summary>
        public static string BuildDescription(Enrollment? enrollment)
        {
            if (enrollment == null) return "HP";
            return BuildDescription(enrollment.Student?.StudentCode, enrollment.Student?.FullName, enrollment.Semester);
        }

        public static string BuildDescription(string? studentCode, string? fullName, string? semester)
        {
            string code = string.IsNullOrWhiteSpace(studentCode) ? "SV" : studentCode.Trim();
            string name = string.IsNullOrWhiteSpace(fullName) ? "" : RemoveDiacritics(fullName.Trim());
            string sem = string.IsNullOrWhiteSpace(semester) ? "" : semester.Trim();

            return $"HP {code} {name} {sem}".Trim();
        }

        public static string RemoveDiacritics(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var normalizedString = text.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder();

            foreach (var c in normalizedString)
            {
                var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
                if (category != System.Globalization.UnicodeCategory.NonSpacingMark)
                {
                    sb.Append(c);
                }
            }

            return sb.ToString()
                .Normalize(System.Text.NormalizationForm.FormC)
                .Replace('đ', 'd')
                .Replace('Đ', 'D');
        }

        /// <summary>
        /// Đường link sinh ảnh VietQR chuẩn Napas 24/7 theo định dạng QuickLink quốc gia
        /// Quét được bằng tất cả App Ngân hàng Việt Nam (MB, Vietcombank, Techcombank, BIDV, Agribank, VietinBank...)
        /// </summary>
        public string QrImageUrl
        {
            get
            {
                long integerAmount = (long)Math.Round(Amount);
                string encodedDesc = Uri.EscapeDataString(Description);
                string encodedName = Uri.EscapeDataString(AccountName);
                return $"https://img.vietqr.io/image/{BankCode}-{AccountNumber}-{Template}.png?amount={integerAmount}&addInfo={encodedDesc}&accountName={encodedName}";
            }
        }
    }
}
