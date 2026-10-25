namespace CourseRegistration.CoreBusiness.Models
{
    // Phiếu đăng ký (Header) - Enrollment

    public class Enrollment
    {
        public Enrollment()
        {
            EnrollmentLines = new List<EnrollmentLine>();
        }

        public int EnrollmentId { get; set; }
        public int StudentId { get; set; }
        public DateTime? DateRegistered { get; set; }
        public DateTime? DateCancelled { get; set; }
        public string Semester { get; set; } = string.Empty; //Học kì 
        public string Status { get; set; } = "Pending"; // Pending, Confirmed, Cancelled
        public string? UniqueId { get; set; }

        // Quản lý Học Phí & Thanh Toán Trực Tuyến (VietQR)
        public string PaymentStatus { get; set; } = "Unpaid"; // Unpaid (Chưa thanh toán), Paid (Đã thanh toán)
        public DateTime? PaymentDate { get; set; }
        public string? PaymentMethod { get; set; } = "VietQR Napas247"; // VietQR Napas247, Chuyển khoản, Tiền mặt
        public string? TransactionReference { get; set; }

        // Navigation properties
        public Student? Student { get; set; }
        public List<EnrollmentLine> EnrollmentLines { get; set; } = new List<EnrollmentLine>();

        // Đơn giá tín chỉ quy định (450.000 VNĐ / 1 tín chỉ)
        public decimal PricePerCredit => 450000m;

        // Tổng số tín chỉ trong phiếu đăng ký
        public int TotalCredits => EnrollmentLines.Where(el => el.Section?.Course != null).Sum(el => el.Section!.Course!.Credits);

        // Tổng học phí phải nộp
        public decimal TuitionFee => TotalCredits * PricePerCredit;

        // Kiểm tra đã thanh toán hay chưa
        public bool IsPaid => string.Equals(PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase);

        // Thêm lớp học phần vào phiếu đăng ký
        public void AddSection(int sectionId, Section section)
        {
            var existingLine = EnrollmentLines.FirstOrDefault(el => el.SectionId == sectionId);
            if (existingLine == null)
            {
                EnrollmentLines.Add(new EnrollmentLine
                {
                    SectionId = sectionId,
                    EnrollmentId = EnrollmentId,
                    Section = section
                });
            }
        }

        // Xóa lớp học phần khỏi phiếu đăng ký
        public void RemoveSection(int sectionId)
        {
            var line = EnrollmentLines.FirstOrDefault(el => el.SectionId == sectionId);
            if (line != null)
            {
                EnrollmentLines.Remove(line);
            }
        }
    }
}
