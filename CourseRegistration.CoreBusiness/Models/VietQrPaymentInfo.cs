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
        public string AccountNumber { get; set; } = "08688889999";
        public string AccountName { get; set; } = "TRUONG DAI HOC KINH TE HUE";
        public decimal Amount { get; set; }
        public string Description { get; set; } = string.Empty;
        public string Template { get; set; } = "compact2";

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
