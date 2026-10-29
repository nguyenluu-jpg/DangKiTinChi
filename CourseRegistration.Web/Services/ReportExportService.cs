using ClosedXML.Excel;
using CourseRegistration.CoreBusiness.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace CourseRegistration.Web.Services
{
    public class ReportExportService : IReportExportService
    {
        public ReportExportService()
        {
            // Thiết lập bản quyền Community cho QuestPDF trong mục đích học tập & đồ án
            QuestPDF.Settings.License = LicenseType.Community;
            QuestPDF.Settings.ThrowOnMissingFontFamilies = false;
        }

        // ==========================================
        // 1. XUẤT EXCEL DANH SÁCH SINH VIÊN THEO LỚP
        // ==========================================
        public byte[] GenerateSectionStudentsExcel(Section section, List<Enrollment> enrollments)
        {
            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("Danh Sách Sinh Viên");

            // Tiêu đề trường & khoa
            ws.Cell(1, 1).Value = "TRƯỜNG ĐẠI HỌC KINH TẾ - ĐẠI HỌC HUẾ";
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontSize = 11;

            ws.Cell(2, 1).Value = "PHÒNG ĐÀO TẠO & QUẢN LÝ SINH VIÊN";
            ws.Cell(2, 1).Style.Font.FontSize = 10;
            ws.Cell(2, 1).Style.Font.Italic = true;

            // Tiêu đề báo cáo
            ws.Range(4, 1, 4, 8).Merge();
            ws.Cell(4, 1).Value = "DANH SÁCH SINH VIÊN ĐĂNG KÝ LỚP HỌC PHẦN";
            ws.Cell(4, 1).Style.Font.Bold = true;
            ws.Cell(4, 1).Style.Font.FontSize = 16;
            ws.Cell(4, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(4, 1).Style.Font.FontColor = XLColor.FromArgb(30, 58, 138);

            // Thông tin lớp học phần
            ws.Cell(6, 1).Value = "Học kỳ:";
            ws.Cell(6, 2).Value = section.Semester;
            ws.Cell(6, 4).Value = "Mã lớp HP:";
            ws.Cell(6, 5).Value = section.SectionCode;

            ws.Cell(7, 1).Value = "Học phần:";
            ws.Cell(7, 2).Value = $"{section.Course?.CourseCode} - {section.Course?.CourseName} ({section.Course?.Credits} TC)";
            ws.Cell(7, 4).Value = "Giảng viên:";
            ws.Cell(7, 5).Value = section.Instructor;

            ws.Cell(8, 1).Value = "Phòng học:";
            ws.Cell(8, 2).Value = section.Room;
            ws.Cell(8, 4).Value = "Sĩ số hiện tại:";
            ws.Cell(8, 5).Value = $"{enrollments.Count} / {section.MaxCapacity} sinh viên";

            for (int r = 6; r <= 8; r++)
            {
                ws.Cell(r, 1).Style.Font.Bold = true;
                ws.Cell(r, 4).Style.Font.Bold = true;
            }

            // Header bảng
            int headerRow = 10;
            string[] headers = { "STT", "Mã Sinh Viên", "Họ và Tên", "Ngành Học", "Khóa", "Email", "Ngày Đăng Ký", "Trạng Thái" };
            for (int i = 0; i < headers.Length; i++)
            {
                var cell = ws.Cell(headerRow, i + 1);
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Fill.BackgroundColor = XLColor.FromArgb(30, 58, 138);
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            }

            // Dữ liệu sinh viên
            int currentRow = headerRow + 1;
            int stt = 1;
            foreach (var enr in enrollments)
            {
                ws.Cell(currentRow, 1).Value = stt;
                ws.Cell(currentRow, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                ws.Cell(currentRow, 2).Value = enr.Student?.StudentCode ?? "";
                ws.Cell(currentRow, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                ws.Cell(currentRow, 3).Value = enr.Student?.FullName ?? "";

                ws.Cell(currentRow, 4).Value = enr.Student?.Major ?? "";
                ws.Cell(currentRow, 5).Value = enr.Student?.AcademicYear ?? "";
                ws.Cell(currentRow, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                ws.Cell(currentRow, 6).Value = enr.Student?.Email ?? "";

                ws.Cell(currentRow, 7).Value = enr.DateRegistered?.ToString("dd/MM/yyyy HH:mm") ?? "";
                ws.Cell(currentRow, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                ws.Cell(currentRow, 8).Value = enr.Status == "Confirmed" ? "Đã xác nhận" : enr.Status;
                ws.Cell(currentRow, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 8).Style.Font.FontColor = XLColor.FromArgb(22, 101, 52); // Xanh lá đậm

                // Kẻ viền và màu xen kẽ
                for (int c = 1; c <= 8; c++)
                {
                    var cell = ws.Cell(currentRow, c);
                    cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    cell.Style.Border.OutsideBorderColor = XLColor.FromArgb(203, 213, 225);
                    if (stt % 2 == 0)
                    {
                        cell.Style.Fill.BackgroundColor = XLColor.FromArgb(248, 250, 252);
                    }
                }

                currentRow++;
                stt++;
            }

            // Phần ký tên cuối trang
            int footerRow = currentRow + 2;
            ws.Cell(footerRow, 6).Value = $"TP. Hồ Chí Minh, ngày {DateTime.Now:dd} tháng {DateTime.Now:MM} năm {DateTime.Now:yyyy}";
            ws.Cell(footerRow, 6).Style.Font.Italic = true;

            ws.Cell(footerRow + 1, 2).Value = "GIẢNG VIÊN PHỤ TRÁCH";
            ws.Cell(footerRow + 1, 2).Style.Font.Bold = true;

            ws.Cell(footerRow + 1, 6).Value = "CÁN BỘ LẬP BẢNG";
            ws.Cell(footerRow + 1, 6).Style.Font.Bold = true;

            ws.Cell(footerRow + 4, 2).Value = section.Instructor;
            ws.Cell(footerRow + 4, 2).Style.Font.Bold = true;

            ws.Columns().AdjustToContents();

            using var ms = new MemoryStream();
            workbook.SaveAs(ms);
            return ms.ToArray();
        }

        // ==========================================
        // 2. XUẤT PDF DANH SÁCH SINH VIÊN THEO LỚP
        // ==========================================
        public byte[] GenerateSectionStudentsPdf(Section section, List<Enrollment> enrollments)
        {
            var document = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(1.5f, Unit.Centimetre);
                    page.DefaultTextStyle(x => x.FontSize(10));

                    // Header
                    page.Header().Column(col =>
                    {
                        col.Item().Row(row =>
                        {
                            row.RelativeItem().Column(c =>
                            {
                                c.Item().Text("TRƯỜNG ĐẠI HỌC KINH TẾ - ĐẠI HỌC HUẾ").Bold().FontSize(10);
                                c.Item().Text("PHÒNG ĐÀO TẠO ĐẠI HỌC").FontSize(9).Italic();
                            });
                            row.RelativeItem().AlignRight().Column(c =>
                            {
                                c.Item().Text("CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM").Bold().FontSize(10);
                                c.Item().Text("Độc lập - Tự do - Hạnh phúc").FontSize(9).Italic();
                            });
                        });

                        col.Item().PaddingTop(15).AlignCenter().Text("DANH SÁCH SINH VIÊN ĐĂNG KÝ HỌC PHẦN")
                            .Bold().FontSize(15).FontColor("#1e3a8a");
                        col.Item().AlignCenter().Text($"Học kỳ: {section.Semester}").FontSize(10).Italic();

                        // Hộp thông tin lớp
                        col.Item().PaddingTop(10).Border(1).BorderColor("#cbd5e1").Background("#f8fafc").Padding(8).Row(r =>
                        {
                            r.RelativeItem().Column(c =>
                            {
                                c.Item().Text($"Mã lớp: {section.SectionCode}").Bold();
                                c.Item().Text($"Môn học: {section.Course?.CourseCode} - {section.Course?.CourseName} ({section.Course?.Credits} TC)");
                                c.Item().Text($"Phòng học: {section.Room}");
                            });
                            r.RelativeItem().Column(c =>
                            {
                                c.Item().Text($"Giảng viên: {section.Instructor}").Bold();
                                c.Item().Text($"Sĩ số đăng ký: {enrollments.Count} / {section.MaxCapacity} SV");
                                c.Item().Text($"Thời gian xuất: {DateTime.Now:dd/MM/yyyy HH:mm}");
                            });
                        });

                        col.Item().PaddingTop(10);
                    });

                    // Bảng sinh viên
                    page.Content().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.ConstantColumn(30);  // STT
                            columns.ConstantColumn(70);  // MSSV
                            columns.RelativeColumn(3);   // Họ tên
                            columns.RelativeColumn(2);   // Ngành
                            columns.ConstantColumn(40);  // Khóa
                            columns.RelativeColumn(3);   // Email
                            columns.ConstantColumn(70);  // Trạng thái
                        });

                        // Table Header
                        table.Header(header =>
                        {
                            header.Cell().Background("#1e3a8a").Padding(4).AlignCenter().Text("STT").Bold().FontColor("#ffffff");
                            header.Cell().Background("#1e3a8a").Padding(4).AlignCenter().Text("MSSV").Bold().FontColor("#ffffff");
                            header.Cell().Background("#1e3a8a").Padding(4).Text("Họ và Tên").Bold().FontColor("#ffffff");
                            header.Cell().Background("#1e3a8a").Padding(4).Text("Chuyên Ngành").Bold().FontColor("#ffffff");
                            header.Cell().Background("#1e3a8a").Padding(4).AlignCenter().Text("Khóa").Bold().FontColor("#ffffff");
                            header.Cell().Background("#1e3a8a").Padding(4).Text("Email").Bold().FontColor("#ffffff");
                            header.Cell().Background("#1e3a8a").Padding(4).AlignCenter().Text("Trạng Thái").Bold().FontColor("#ffffff");
                        });

                        // Table Rows
                        int stt = 1;
                        foreach (var enr in enrollments)
                        {
                            var bg = stt % 2 == 0 ? "#f8fafc" : "#ffffff";

                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor("#e2e8f0").Padding(4).AlignCenter().Text(stt.ToString());
                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor("#e2e8f0").Padding(4).AlignCenter().Text(enr.Student?.StudentCode ?? "");
                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor("#e2e8f0").Padding(4).Text(enr.Student?.FullName ?? "");
                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor("#e2e8f0").Padding(4).Text(enr.Student?.Major ?? "");
                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor("#e2e8f0").Padding(4).AlignCenter().Text(enr.Student?.AcademicYear ?? "");
                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor("#e2e8f0").Padding(4).Text(enr.Student?.Email ?? "").FontSize(9);
                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor("#e2e8f0").Padding(4).AlignCenter().Text("Đã đăng ký").FontColor("#166534").Bold().FontSize(9);

                            stt++;
                        }
                    });

                    // Footer & Chữ ký
                    page.Footer().Column(col =>
                    {
                        col.Item().PaddingTop(20).Row(r =>
                        {
                            r.RelativeItem().AlignCenter().Column(c =>
                            {
                                c.Item().Text("GIẢNG VIÊN GIẢNG DẠY").Bold();
                                c.Item().Text("(Ký và ghi rõ họ tên)").FontSize(8).Italic();
                                c.Item().PaddingTop(35).Text(section.Instructor).Bold();
                            });
                            r.RelativeItem().AlignCenter().Column(c =>
                            {
                                c.Item().Text($"TP.HCM, ngày {DateTime.Now:dd} tháng {DateTime.Now:MM} năm {DateTime.Now:yyyy}").Italic().FontSize(9);
                                c.Item().Text("XÁC NHẬN PHÒNG ĐÀO TẠO").Bold();
                                c.Item().Text("(Ký và đóng dấu)").FontSize(8).Italic();
                                c.Item().PaddingTop(35).Text("Cán bộ Quản trị").Bold();
                            });
                        });

                        col.Item().PaddingTop(10).Row(r =>
                        {
                            r.RelativeItem().Text("Hệ thống Đăng ký Tín chỉ Trực tuyến").FontSize(8).FontColor("#64748b");
                            r.RelativeItem().AlignRight().Text(x =>
                            {
                                x.Span("Trang ");
                                x.CurrentPageNumber();
                                x.Span(" / ");
                                x.TotalPages();
                            });
                        });
                    });
                });
            });

            return document.GeneratePdf();
        }

        // ==========================================
        // 3. XUẤT PDF PHIẾU ĐĂNG KÝ HỌC PHẦN CHO SINH VIÊN
        // ==========================================
        public byte[] GenerateStudentEnrollmentPdf(Student student, Enrollment enrollment, List<Section> sections)
        {
            var document = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(1.5f, Unit.Centimetre);
                    page.DefaultTextStyle(x => x.FontSize(10));

                    page.Header().Column(col =>
                    {
                        col.Item().Row(r =>
                        {
                            r.RelativeItem().Column(c =>
                            {
                                c.Item().Text("TRƯỜNG ĐẠI HỌC KINH TẾ - ĐẠI HỌC HUẾ").Bold().FontSize(10);
                                c.Item().Text("PHÒNG ĐÀO TẠO ĐẠI HỌC").FontSize(9).Italic();
                            });
                            r.RelativeItem().AlignRight().Column(c =>
                            {
                                c.Item().Text("Mã phiếu: " + (string.IsNullOrEmpty(enrollment.UniqueId) ? $"ENR-{enrollment.EnrollmentId:D5}" : enrollment.UniqueId)).Bold();
                                c.Item().Text($"Ngày cấp: {enrollment.DateRegistered:dd/MM/yyyy HH:mm}").FontSize(9).Italic();
                            });
                        });

                        col.Item().PaddingTop(15).AlignCenter().Text("PHIẾU KẾT QUẢ ĐĂNG KÝ HỌC PHẦN")
                            .Bold().FontSize(16).FontColor("#1e3a8a");
                        col.Item().AlignCenter().Text($"Học kỳ: {enrollment.Semester}").FontSize(11).Italic();

                        // Thông tin sinh viên
                        col.Item().PaddingTop(10).Border(1).BorderColor("#cbd5e1").Background("#f8fafc").Padding(10).Row(r =>
                        {
                            r.RelativeItem().Column(c =>
                            {
                                c.Item().Text($"Họ và tên: {student.FullName}").Bold().FontSize(11);
                                c.Item().Text($"Mã số sinh viên: {student.StudentCode}").Bold();
                                c.Item().Text($"Chuyên ngành: {student.Major}");
                            });
                            r.RelativeItem().Column(c =>
                            {
                                c.Item().Text($"Khóa đào tạo: {student.AcademicYear}");
                                c.Item().Text($"Email: {student.Email}");
                                c.Item().Text($"Trạng thái phiếu: {(enrollment.Status == "Confirmed" ? "Đã xác nhận chính thức" : enrollment.Status)}").Bold().FontColor("#166534");
                            });
                        });

                        col.Item().PaddingTop(10);
                    });

                    // Danh sách môn
                    page.Content().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.ConstantColumn(30); // STT
                            columns.ConstantColumn(65); // Mã HP
                            columns.RelativeColumn(3);  // Tên HP
                            columns.ConstantColumn(35); // TC
                            columns.ConstantColumn(65); // Nhóm Lớp
                            columns.RelativeColumn(2);  // Giảng viên
                            columns.RelativeColumn(3);  // Lịch học & Phòng
                        });

                        table.Header(h =>
                        {
                            h.Cell().Background("#1e3a8a").Padding(5).AlignCenter().Text("STT").Bold().FontColor("#fff");
                            h.Cell().Background("#1e3a8a").Padding(5).AlignCenter().Text("Mã HP").Bold().FontColor("#fff");
                            h.Cell().Background("#1e3a8a").Padding(5).Text("Tên Học Phần").Bold().FontColor("#fff");
                            h.Cell().Background("#1e3a8a").Padding(5).AlignCenter().Text("TC").Bold().FontColor("#fff");
                            h.Cell().Background("#1e3a8a").Padding(5).AlignCenter().Text("Lớp HP").Bold().FontColor("#fff");
                            h.Cell().Background("#1e3a8a").Padding(5).Text("Giảng Viên").Bold().FontColor("#fff");
                            h.Cell().Background("#1e3a8a").Padding(5).Text("Thời Khóa Biểu").Bold().FontColor("#fff");
                        });

                        int stt = 1;
                        int totalCredits = 0;
                        foreach (var sec in sections)
                        {
                            var bg = stt % 2 == 0 ? "#f8fafc" : "#ffffff";
                            int credits = sec.Course?.Credits ?? 0;
                            totalCredits += credits;

                            string schSummary = (sec.Schedules != null && sec.Schedules.Any())
                                ? string.Join(", ", sec.Schedules.Select(s => $"T{s.DayOfWeek} (T{s.StartPeriod}-{s.EndPeriod}) - {s.Room}"))
                                : sec.Room;

                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor("#e2e8f0").Padding(4).AlignCenter().Text(stt.ToString());
                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor("#e2e8f0").Padding(4).AlignCenter().Text(sec.Course?.CourseCode ?? "");
                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor("#e2e8f0").Padding(4).Text(sec.Course?.CourseName ?? "");
                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor("#e2e8f0").Padding(4).AlignCenter().Text(credits.ToString()).Bold();
                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor("#e2e8f0").Padding(4).AlignCenter().Text(sec.SectionCode);
                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor("#e2e8f0").Padding(4).Text(sec.Instructor);
                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor("#e2e8f0").Padding(4).Text(schSummary).FontSize(9);

                            stt++;
                        }

                        // Dòng tổng cộng tín chỉ
                        table.Cell().ColumnSpan(3).Background("#eff6ff").Padding(5).AlignRight().Text("TỔNG SỐ TÍN CHỈ ĐÃ ĐĂNG KÝ:").Bold();
                        table.Cell().Background("#eff6ff").Padding(5).AlignCenter().Text(totalCredits.ToString()).Bold().FontColor("#1e3a8a").FontSize(11);
                        table.Cell().ColumnSpan(3).Background("#eff6ff").Padding(5).Text($"({sections.Count} lớp học phần hợp lệ)").Italic();

                        // Dòng thông tin học phí & thanh toán VietQR
                        decimal fee = totalCredits * 450000m;
                        string paymentText = enrollment.IsPaid ? $"ĐÃ THANH TOÁN (Mã GD: {enrollment.TransactionReference ?? "VietQR"})" : "CHƯA THANH TOÁN";
                        string paymentColor = enrollment.IsPaid ? "#166534" : "#b45309";
                        table.Cell().ColumnSpan(3).Background("#f8fafc").Padding(5).AlignRight().Text("TỔNG HỌC PHÍ PHẢI NỘP (450.000 đ/TC):").Bold();
                        table.Cell().ColumnSpan(4).Background("#f8fafc").Padding(5).Text($"{fee:N0} VNĐ  |  Trạng thái: {paymentText}").Bold().FontColor(paymentColor);
                    });

                    page.Footer().Column(col =>
                    {
                        col.Item().PaddingTop(15).Row(r =>
                        {
                            r.RelativeItem().Column(c =>
                            {
                                c.Item().Text("Ghi chú quan trọng:").Bold().Underline();
                                c.Item().Text("- Sinh viên đi học đúng thời khóa biểu và phòng học quy định.").FontSize(9);
                                c.Item().Text("- Lưu giữ phiếu này làm căn cứ điểm danh và đối chiếu học phí.").FontSize(9);
                            });
                            r.RelativeItem().AlignRight().Column(c =>
                            {
                                c.Item().Text($"TP.HCM, ngày {DateTime.Now:dd} tháng {DateTime.Now:MM} năm {DateTime.Now:yyyy}").Italic().FontSize(9);
                                c.Item().Text("SINH VIÊN XÁC NHẬN").Bold();
                                c.Item().Text("(Ký và ghi rõ họ tên)").FontSize(8).Italic();
                                c.Item().PaddingTop(35).Text(student.FullName).Bold();
                            });
                        });

                        col.Item().PaddingTop(10).Row(r =>
                        {
                            r.RelativeItem().Text("Hệ Thống Đăng Ký Tín Chỉ").FontSize(8).FontColor("#64748b");
                            r.RelativeItem().AlignRight().Text(x =>
                            {
                                x.Span("Trang ");
                                x.CurrentPageNumber();
                                x.Span(" / ");
                                x.TotalPages();
                            });
                        });
                    });
                });
            });

            return document.GeneratePdf();
        }

        // ==========================================
        // 4. XUẤT EXCEL THỜI KHÓA BIỂU CHO SINH VIÊN
        // ==========================================
        public byte[] GenerateStudentEnrollmentExcel(Student student, Enrollment enrollment, List<Section> sections)
        {
            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("ThoiKhoaBieu");

            ws.Cell(1, 1).Value = "TRƯỜNG ĐẠI HỌC KINH TẾ - ĐẠI HỌC HUẾ";
            ws.Cell(1, 1).Style.Font.Bold = true;

            ws.Range(3, 1, 3, 7).Merge();
            ws.Cell(3, 1).Value = $"KẾT QUẢ ĐĂNG KÝ HỌC PHẦN & THỜI KHÓA BIỂU - {enrollment.Semester}";
            ws.Cell(3, 1).Style.Font.Bold = true;
            ws.Cell(3, 1).Style.Font.FontSize = 14;
            ws.Cell(3, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(3, 1).Style.Font.FontColor = XLColor.FromArgb(30, 58, 138);

            ws.Cell(5, 1).Value = "Mã sinh viên:";
            ws.Cell(5, 2).Value = student.StudentCode;
            ws.Cell(5, 4).Value = "Họ và tên:";
            ws.Cell(5, 5).Value = student.FullName;

            ws.Cell(6, 1).Value = "Ngành:";
            ws.Cell(6, 2).Value = student.Major;
            ws.Cell(6, 4).Value = "Khóa:";
            ws.Cell(6, 5).Value = student.AcademicYear;

            for (int r = 5; r <= 6; r++)
            {
                ws.Cell(r, 1).Style.Font.Bold = true;
                ws.Cell(r, 4).Style.Font.Bold = true;
            }

            int headerRow = 8;
            string[] headers = { "STT", "Mã Lớp HP", "Mã Môn", "Tên Học Phần", "Số TC", "Giảng Viên", "Thời Khóa Biểu & Phòng" };
            for (int i = 0; i < headers.Length; i++)
            {
                var cell = ws.Cell(headerRow, i + 1);
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Fill.BackgroundColor = XLColor.FromArgb(30, 58, 138);
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            }

            int currRow = headerRow + 1;
            int stt = 1;
            int totalCredits = 0;
            foreach (var sec in sections)
            {
                int tc = sec.Course?.Credits ?? 0;
                totalCredits += tc;
                string schSummary = (sec.Schedules != null && sec.Schedules.Any())
                    ? string.Join(", ", sec.Schedules.Select(s => $"T{s.DayOfWeek} (tiết {s.StartPeriod}-{s.EndPeriod}) - {s.Room}"))
                    : sec.Room;

                ws.Cell(currRow, 1).Value = stt;
                ws.Cell(currRow, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(currRow, 2).Value = sec.SectionCode;
                ws.Cell(currRow, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(currRow, 3).Value = sec.Course?.CourseCode ?? "";
                ws.Cell(currRow, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(currRow, 4).Value = sec.Course?.CourseName ?? "";
                ws.Cell(currRow, 5).Value = tc;
                ws.Cell(currRow, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(currRow, 6).Value = sec.Instructor;
                ws.Cell(currRow, 7).Value = schSummary;

                for (int c = 1; c <= 7; c++)
                {
                    ws.Cell(currRow, c).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    ws.Cell(currRow, c).Style.Border.OutsideBorderColor = XLColor.FromArgb(203, 213, 225);
                }

                currRow++;
                stt++;
            }

            // Dòng tổng kết
            ws.Cell(currRow, 4).Value = "TỔNG SỐ TÍN CHỈ:";
            ws.Cell(currRow, 4).Style.Font.Bold = true;
            ws.Cell(currRow, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            ws.Cell(currRow, 5).Value = totalCredits;
            ws.Cell(currRow, 5).Style.Font.Bold = true;
            ws.Cell(currRow, 5).Style.Font.FontColor = XLColor.FromArgb(30, 58, 138);

            // Dòng học phí & thanh toán VietQR
            currRow++;
            ws.Cell(currRow, 4).Value = "HỌC PHÍ (450.000 đ/TC):";
            ws.Cell(currRow, 4).Style.Font.Bold = true;
            ws.Cell(currRow, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            ws.Cell(currRow, 5).Value = $"{totalCredits * 450000:N0} VNĐ";
            ws.Cell(currRow, 5).Style.Font.Bold = true;
            ws.Cell(currRow, 5).Style.Font.FontColor = XLColor.FromArgb(185, 28, 28);

            ws.Cell(currRow, 6).Value = "Trạng thái:";
            ws.Cell(currRow, 6).Style.Font.Bold = true;
            ws.Cell(currRow, 7).Value = enrollment.IsPaid ? $"ĐÃ NỘP ({enrollment.TransactionReference ?? "VietQR"})" : "CHƯA THANH TOÁN";
            ws.Cell(currRow, 7).Style.Font.Bold = true;
            ws.Cell(currRow, 7).Style.Font.FontColor = enrollment.IsPaid ? XLColor.FromArgb(22, 101, 52) : XLColor.FromArgb(180, 83, 9);

            ws.Columns().AdjustToContents();

            using var ms = new MemoryStream();
            workbook.SaveAs(ms);
            return ms.ToArray();
        }
    }
}
