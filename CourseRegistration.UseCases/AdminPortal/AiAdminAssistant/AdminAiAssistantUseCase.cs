using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.AdminPortal.AiAdminAssistant.Interfaces;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;
using Microsoft.Extensions.AI;

namespace CourseRegistration.UseCases.AdminPortal.AiAdminAssistant
{
    public class AdminAiAssistantUseCase : IAdminAiAssistantUseCase
    {
        private readonly IChatClient? _chatClient;
        private readonly ISectionRepository _sectionRepository;
        private readonly IStudentRepository _studentRepository;
        private readonly IEnrollmentRepository _enrollmentRepository;
        private readonly ICourseRepository _courseRepository;

        public AdminAiAssistantUseCase(
            ISectionRepository sectionRepository,
            IStudentRepository studentRepository,
            IEnrollmentRepository enrollmentRepository,
            ICourseRepository courseRepository,
            IChatClient? chatClient = null)
        {
            _sectionRepository = sectionRepository;
            _studentRepository = studentRepository;
            _enrollmentRepository = enrollmentRepository;
            _courseRepository = courseRepository;
            _chatClient = chatClient;
        }

        public async Task<AdminAiQueryResult> ExecuteAsync(string query, string semester = "2026_HK1")
        {
            var result = new AdminAiQueryResult
            {
                Query = query.Trim(),
                Category = "Học Vụ & Đào Tạo"
            };

            // 1. Trích xuất toàn bộ dữ liệu CSDL thời gian thực
            var allSections = (await _sectionRepository.GetSectionsBySemesterAsync(semester)).ToList();
            var allSectionsAcrossSemesters = (await _sectionRepository.GetAllSectionsAsync()).ToList();
            var allStudents = (await _studentRepository.GetAllStudentsAsync()).ToList();
            var allCourses = (await _courseRepository.GetAllCoursesAsync()).ToList();
            var allPrerequisites = (await _courseRepository.GetAllPrerequisitesAsync()).ToList();

            // 2. Thử nghiệm qua LLM nếu IChatClient sẵn sàng
            bool aiSuccess = false;
            if (_chatClient != null && (allSections.Any() || allSectionsAcrossSemesters.Any()))
            {
                try
                {
                    var sectionsBrief = string.Join("\n", allSectionsAcrossSemesters.Select(s =>
                        $"- {s.SectionCode}: {s.Course?.CourseCode} - {s.Course?.CourseName} ({s.Course?.Credits}TC) | GV: {s.Instructor} | Sĩ số: {s.CurrentEnrollment}/{s.MaxCapacity} ({Math.Round((double)s.CurrentEnrollment / Math.Max(1, s.MaxCapacity) * 100, 1)}%) | Phòng: {s.Room} | Học kỳ: {s.Semester}"));

                    var coursesBrief = string.Join(", ", allCourses.Select(c => $"{c.CourseCode}: {c.CourseName} ({c.Credits}TC)"));

                    var prompt = $@"Bạn là Trợ lý AI Quản trị Đào tạo (Admin Copilot) của Phòng Đào Tạo Đại Học.
Cán bộ quản lý hoặc người dùng hỏi: ""{query}""

[DỮ LIỆU TOÀN DIỆN KỲ {semester}]
- Danh sách lớp học phần:
{sectionsBrief}
- Danh sách môn học toàn trường:
{coursesBrief}
- Quy chế: Hạn mức 10-24 tín chỉ; Rút môn tự do trong đợt trực tuyến, hết hạn nộp đơn cho PĐT; Tiên quyết đạt điểm >= 5.0; Trùng lịch bị cấm tuyệt đối.

[YÊU CẦU ĐẦU RA]
Trả về DUY NHẤT một chuỗi JSON hợp lệ:
{{
  ""category"": ""Chủ đề câu hỏi"",
  ""answerSummary"": ""Câu trả lời trực tiếp, rõ ràng, phân tích sâu, trả lời ĐẦY ĐỦ câu hỏi của người dùng, nêu rõ số liệu cụ thể nếu có"",
  ""actionableSuggestions"": [""Gợi ý hành động 1"", ""Gợi ý hành động 2""]
}}";

                    var messages = new List<ChatMessage>
                    {
                        new ChatMessage(ChatRole.System, "Bạn là Trợ lý AI Phòng Đào Tạo thông minh, có thể trả lời toàn bộ các câu hỏi học vụ, quản trị, giảng viên, sinh viên, quy chế đào tạo."),
                        new ChatMessage(ChatRole.User, prompt)
                    };

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                    var response = await _chatClient.GetResponseAsync(messages, new ChatOptions { Temperature = 0.2f }, cts.Token);
                    var text = response.Text ?? string.Empty;

                    var jsonContent = ExtractJson(text);
                    if (!string.IsNullOrEmpty(jsonContent))
                    {
                        using var doc = JsonDocument.Parse(jsonContent);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("category", out var cProp) && !string.IsNullOrEmpty(cProp.GetString()))
                        {
                            result.Category = cProp.GetString()!;
                        }
                        if (root.TryGetProperty("answerSummary", out var aProp))
                        {
                            result.AnswerSummary = aProp.GetString() ?? string.Empty;
                        }
                        if (root.TryGetProperty("actionableSuggestions", out var sProp) && sProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in sProp.EnumerateArray())
                            {
                                var sText = item.GetString();
                                if (!string.IsNullOrEmpty(sText)) result.ActionableSuggestions.Add(sText);
                            }
                        }

                        if (!string.IsNullOrEmpty(result.AnswerSummary))
                        {
                            result.IsAiGenerated = true;
                            result.ModelUsed = "Trí Tuệ Nhân Tạo AI (IChatClient - LLM)";
                            aiSuccess = true;

                            // Trích xuất các lớp học phần liên quan để hiển thị bảng nếu LLM chưa thêm
                            PopulateRelatedSectionsForAi(result, query, allSectionsAcrossSemesters.Any() ? allSectionsAcrossSemesters : allSections, allCourses);
                        }
                    }
                }
                catch
                {
                    aiSuccess = false;
                }
            }

            // 3. Động cơ Tri Thức & Tra Cứu Toàn Diện (Universal Knowledge Engine)
            if (!aiSuccess)
            {
                result.IsAiGenerated = false;
                result.ModelUsed = "Động Cơ Tri Thức & Phân Tích Dữ Liệu SQL Thời Gian Thực";
                result.WarningMessage = "💡 Trợ lý AI đang chạy ở chế độ Động cơ Tri Thức & Tra cứu Học vụ Trực tiếp từ CSDL thời gian thực.";

                await ExecuteUniversalKnowledgeQueryAsync(result, query, allSections, allStudents, allCourses, allPrerequisites, semester, allSectionsAcrossSemesters);
            }

            return result;
        }

        private async Task ExecuteUniversalKnowledgeQueryAsync(
            AdminAiQueryResult result,
            string query,
            List<Section> allSections,
            List<Student> allStudents,
            List<Course> allCourses,
            List<Prerequisite> allPrerequisites,
            string semester,
            List<Section>? allSectionsAcrossSemesters = null)
        {
            string qRaw = query.Trim();
            string qLower = qRaw.ToLowerInvariant();
            string qNorm = RemoveDiacritics(qLower);

            var sectionsPool = allSectionsAcrossSemesters != null && allSectionsAcrossSemesters.Any() ? allSectionsAcrossSemesters : allSections;

            // =========================================================================
            // BƯỚC 1: NHẬN DIỆN THỰC THỂ (ENTITY RECOGNITION) TỪ CÂU HỎI
            // =========================================================================
            var matchedCourse = FindMatchingCourse(qNorm, allCourses);
            var matchedSection = FindMatchingSection(qNorm, sectionsPool);
            var matchedInstructor = FindMatchingInstructor(qNorm, sectionsPool);
            var matchedStudent = FindMatchingStudent(qNorm, allStudents);

            // =========================================================================
            // BƯỚC 2: NHẬN DIỆN Ý ĐỊNH (INTENT RECOGNITION)
            // =========================================================================
            bool isEnrollmentIntent =
                qNorm.Contains("bao nhieu sinh vien") ||
                qNorm.Contains("bao nhieu sv") ||
                qNorm.Contains("bao nhieu ban") ||
                qNorm.Contains("bao nhieu nguoi") ||
                qNorm.Contains("si so") ||
                qNorm.Contains("so luong sinh vien") ||
                qNorm.Contains("luot dang ky") ||
                qNorm.Contains("co bao nhieu") ||
                qNorm.Contains("may sinh vien");

            bool isSectionCountIntent =
                qNorm.Contains("bao nhieu lop") ||
                qNorm.Contains("may lop") ||
                qNorm.Contains("co lop nao") ||
                qNorm.Contains("nhung lop nao") ||
                qNorm.Contains("danh sach lop");

            bool isScheduleIntent =
                qNorm.Contains("lich hoc") ||
                qNorm.Contains("thoi khoa bieu") ||
                qNorm.Contains("thu may") ||
                qNorm.Contains("tiet may") ||
                qNorm.Contains("gio hoc") ||
                qNorm.Contains("khi nao hoc") ||
                qNorm.Contains("phong nao") ||
                qNorm.Contains("o dau");

            bool isPrerequisiteIntent =
                qNorm.Contains("tien quyet") ||
                qNorm.Contains("dieu kien") ||
                qNorm.Contains("hoc truoc") ||
                qNorm.Contains("can hoc mon gi");

            bool isOverloadIntent =
                qNorm.Contains("qua tai") ||
                qNorm.Contains("sap day") ||
                qNorm.Contains("het cho") ||
                qNorm.Contains("day cho") ||
                qNorm.Contains(">75%") ||
                qNorm.Contains("tren 75%") ||
                qNorm.Contains("dong nhat") ||
                qNorm.Contains("chat cho");

            bool isLowEnrollmentIntent =
                qNorm.Contains("it sinh vien") ||
                qNorm.Contains("it sv") ||
                qNorm.Contains("si so thap") ||
                qNorm.Contains("nguy co huy") ||
                qNorm.Contains("huy lop do it") ||
                qNorm.Contains("<50%") ||
                qNorm.Contains("duoi 50%") ||
                (qNorm.Contains("vang") && !qNorm.Contains("giang vien"));

            bool isDropCourseIntent =
                qNorm.Contains("rut mon") ||
                qNorm.Contains("huy mon") ||
                qNorm.Contains("bo mon") ||
                qNorm.Contains("xin rut") ||
                qNorm.Contains("huy dang ky");

            bool isCreditLimitIntent =
                qNorm.Contains("tin chi") && (
                    qNorm.Contains("toi da") ||
                    qNorm.Contains("toi thieu") ||
                    qNorm.Contains("han muc") ||
                    qNorm.Contains("gioi han") ||
                    qNorm.Contains("duoc dang ky bao nhieu"));

            bool isScheduleConflictIntent =
                qNorm.Contains("trung lich") ||
                qNorm.Contains("xung dot") ||
                qNorm.Contains("trung tiet") ||
                qNorm.Contains("trung gio");

            bool isInstructorInquiry =
                qNorm.Contains("thay ") || qNorm.Contains("co ") ||
                qNorm.Contains("giang vien") || qNorm.Contains("gv ") ||
                qNorm.Contains("mon cua thay") || qNorm.Contains("mon cua co") ||
                qNorm.Contains("lop cua thay") || qNorm.Contains("lop cua co") ||
                qNorm.Contains("ai day") || qNorm.Contains("ai giang day") ||
                qNorm.Contains("ai phu trach");

            // LƯU Ý QUAN TRỌNG: Chỉ nhận diện xuất file / in ấn khi có cụm từ rõ ràng,
            // TUYỆT ĐỐI KHÔNG match từ "in" hay "báo" đơn lẻ vì "sinh viên", "lập trình", "thông tin" đều chứa "in"!
            bool isExportReportIntent =
                qNorm.Contains("xuat bao cao") ||
                qNorm.Contains("xuat excel") ||
                qNorm.Contains("xuat pdf") ||
                qNorm.Contains("xuat file") ||
                qNorm.Contains("in danh sach") ||
                qNorm.Contains("in phieu") ||
                qNorm.Contains("in bao cao") ||
                qNorm.Contains("in bang diem danh") ||
                qNorm.Contains("tai file bao cao") ||
                qNorm.Contains("mau bao cao");

            bool isFacilityIntent =
                qNorm.Contains("phong hoc") ||
                qNorm.Contains("giang duong") ||
                qNorm.Contains("co so vat chat") ||
                qNorm.Contains("phong lab");

            bool isSchoolOverviewIntent =
                qNorm.Contains("tong quan") ||
                qNorm.Contains("toan truong") ||
                qNorm.Contains("bao cao tong quan") ||
                (qNorm.Contains("tong so sinh vien") && matchedCourse == null) ||
                (qNorm.Contains("tat ca sinh vien") && matchedCourse == null) ||
                (qNorm.Contains("toan bo sinh vien") && matchedCourse == null);

            bool isGreetingIntent =
                qNorm == "chao" ||
                qNorm.StartsWith("chao ") ||
                qNorm.Contains("xin chao") ||
                qNorm.Contains("hello") ||
                qNorm == "hi" ||
                qNorm.StartsWith("hi ") ||
                qNorm.Contains("ban la ai") ||
                qNorm.Contains("tro giup");

            bool isTuitionIntent =
                qNorm.Contains("hoc phi") ||
                qNorm.Contains("bao nhieu tien") ||
                qNorm.Contains("dong tien") ||
                qNorm.Contains("nop tien") ||
                qNorm.Contains("ngan hang") ||
                qNorm.Contains("stk") ||
                qNorm.Contains("so tai khoan") ||
                qNorm.Contains("vietqr") ||
                qNorm.Contains("mb bank") ||
                qNorm.Contains("chuyen khoan");

            bool isRegistrationPeriodIntent =
                qNorm.Contains("thoi gian mo cong") ||
                qNorm.Contains("khi nao mo cong") ||
                qNorm.Contains("lich mo cong") ||
                qNorm.Contains("han nop") ||
                qNorm.Contains("dot dang ky") ||
                qNorm.Contains("khi nao dang ky") ||
                qNorm.Contains("thoi han dang ky") ||
                qNorm.Contains("dong cong");

            bool isRegistrationGuideIntent =
                qNorm.Contains("huong dan dang ky") ||
                qNorm.Contains("cach dang ky") ||
                qNorm.Contains("lam sao dang ky") ||
                qNorm.Contains("cac buoc dang ky") ||
                qNorm.Contains("quy trinh dang ky");

            bool isGradingIntent =
                qNorm.Contains("thang diem") ||
                qNorm.Contains("gpa") ||
                qNorm.Contains("diem f") ||
                qNorm.Contains("diem d") ||
                qNorm.Contains("hoc lai") ||
                qNorm.Contains("cai thien") ||
                qNorm.Contains("qua mon") ||
                qNorm.Contains("canh bao hoc vu") ||
                qNorm.Contains("buoc thoi hoc") ||
                qNorm.Contains("duoi hoc");

            bool isUniversityInfoIntent =
                qNorm.Contains("truong dai hoc kinh te") ||
                qNorm.Contains("dai hoc hue") ||
                qNorm.Contains("dia chi truong") ||
                qNorm.Contains("cac nganh") ||
                qNorm.Contains("chuyen nganh") ||
                qNorm.Contains("khoa he thong") ||
                qNorm.Contains("khoa cong nghe");

            bool isStudentAffairsIntent =
                qNorm.Contains("giay xac nhan") ||
                qNorm.Contains("nghia vu quan su") ||
                qNorm.Contains("hoan nghia vu") ||
                qNorm.Contains("the sinh vien") ||
                qNorm.Contains("vay von") ||
                qNorm.Contains("bao luu");

            bool isScholarshipIntent =
                qNorm.Contains("hoc bong") ||
                qNorm.Contains("khen thuong");

            // =========================================================================
            // BƯỚC 3: XỬ LÝ THEO TỪNG NHÁNH TƯ DUY RÕ RÀNG
            // =========================================================================

            // -------------------------------------------------------------------------
            // NHÁNH 1: HỎI VỀ MÃ LỚP HỌC PHẦN CỤ THỂ (VD: "WEB301_01", "CSDL201_01")
            // -------------------------------------------------------------------------
            if (matchedSection != null)
            {
                result.Category = "Tra Cứu Chi Tiết Lớp Học Phần";
                double rate = matchedSection.MaxCapacity > 0 ? Math.Round((double)matchedSection.CurrentEnrollment / matchedSection.MaxCapacity * 100, 1) : 0;
                int remainingSlots = Math.Max(0, matchedSection.MaxCapacity - matchedSection.CurrentEnrollment);

                result.Sections.Add(CreateSectionItem(matchedSection));

                result.AnswerSummary = $"Thông tin chi tiết lớp học phần **{matchedSection.SectionCode}**:\n\n" +
                    $"- **Môn học:** {matchedSection.Course?.CourseName ?? "Chưa rõ"} (Mã môn: {matchedSection.Course?.CourseCode}, {matchedSection.Course?.Credits} tín chỉ).\n" +
                    $"- **Giảng viên phụ trách:** **{matchedSection.Instructor}**.\n" +
                    $"- **Sĩ số hiện tại:** **{matchedSection.CurrentEnrollment} / {matchedSection.MaxCapacity} sinh viên** (Đã lấp đầy **{rate}%**).\n" +
                    $"- **Số chỗ còn trống:** **{remainingSlots} chỗ** {(remainingSlots == 0 ? "*(Lớp đã hết chỗ)*" : "")}.\n" +
                    $"- **Thời khóa biểu & Phòng học:** {FormatSchedules(matchedSection)}.\n" +
                    $"- **Học kỳ áp dụng:** {semester}.";

                result.ActionableSuggestions.Add("Cán bộ có thể xuất file Excel điểm danh lớp này tại 'Quản lý Đăng ký' -> 'In danh sách'.");
                result.ActionableSuggestions.Add("Nếu có sinh viên cần cứu xét hoặc bổ sung vào lớp khi đã đầy, PĐT có thể can thiệp tại mục Quản lý Đăng ký.");
            }
            // -------------------------------------------------------------------------
            // NHÁNH 2: HỎI VỀ MÔN HỌC CỤ THỂ (VD: "Lập trình ứng dụng Web có bao nhiêu sinh viên")
            // -------------------------------------------------------------------------
            else if (matchedCourse != null)
            {
                var courseSections = allSections.Where(s => s.CourseId == matchedCourse.CourseId).ToList();
                int totalEnrolled = courseSections.Sum(s => s.CurrentEnrollment);
                int totalCapacity = courseSections.Sum(s => s.MaxCapacity);
                double overallRate = totalCapacity > 0 ? Math.Round((double)totalEnrolled / totalCapacity * 100, 1) : 0;

                // Nạp các lớp học phần vào bảng hiển thị
                foreach (var s in courseSections)
                {
                    result.Sections.Add(CreateSectionItem(s));
                }

                if (isPrerequisiteIntent)
                {
                    result.Category = "Điều Kiện Tiên Quyết Môn Học";
                    var prereqs = allPrerequisites.Where(p => p.CourseId == matchedCourse.CourseId).ToList();
                    var prereqCourses = allCourses.Where(c => prereqs.Any(p => p.PrerequisiteCourseId == c.CourseId)).ToList();
                    var nextCourses = allPrerequisites.Where(p => p.PrerequisiteCourseId == matchedCourse.CourseId)
                        .Join(allCourses, p => p.CourseId, c => c.CourseId, (p, c) => c).ToList();

                    var prereqText = prereqCourses.Any()
                        ? string.Join(", ", prereqCourses.Select(c => $"**{c.CourseCode} - {c.CourseName}**"))
                        : "Không có môn tiên quyết (sinh viên có thể đăng ký tự do)";

                    var nextText = nextCourses.Any()
                        ? string.Join(", ", nextCourses.Select(c => $"**{c.CourseCode} - {c.CourseName}**"))
                        : "Không có môn phụ thuộc";

                    result.AnswerSummary = $"Thông tin điều kiện tiên quyết của môn **{matchedCourse.CourseName} ({matchedCourse.CourseCode})**:\n\n" +
                        $"- **Môn học bắt buộc hoàn thành trước (Điểm >= 5.0):** {prereqText}.\n" +
                        $"- **Môn học phụ thuộc tiếp theo (yêu cầu môn này):** {nextText}.\n\n" +
                        $"Trong học kỳ {semester}, môn học đang mở **{courseSections.Count} lớp học phần** với tổng sĩ số **{totalEnrolled}/{totalCapacity} sinh viên**.";

                    result.ActionableSuggestions.Add($"Sinh viên chưa đạt điểm điều kiện tiên quyết sẽ bị hệ thống tự động từ chối ghi danh vào môn này.");
                    result.ActionableSuggestions.Add($"Cán bộ có thể cấu hình ma trận tiên quyết tại menu 'Quản lý Môn học'.");
                }
                else if (isScheduleIntent)
                {
                    result.Category = "Lịch Học & Phòng Học Môn Học";
                    var scheduleLines = new List<string>();
                    foreach (var s in courseSections)
                    {
                        scheduleLines.Add($"- **Lớp {s.SectionCode}**: GV **{s.Instructor}** | {FormatSchedules(s)} | Sĩ số: **{s.CurrentEnrollment}/{s.MaxCapacity}**");
                    }

                    result.AnswerSummary = $"Thời khóa biểu và phòng học của môn **{matchedCourse.CourseName} ({matchedCourse.CourseCode})** trong kỳ {semester}:\n\n" +
                        (scheduleLines.Any() ? string.Join("\n", scheduleLines) : "Chưa có thông tin lịch học chi tiết cho môn này.") +
                        $"\n\nTổng cộng môn học có **{courseSections.Count} lớp** đang hoạt động.";

                    result.ActionableSuggestions.Add("Sinh viên cần đối chiếu thời khóa biểu cá nhân để tránh đăng ký trùng tiết với lớp học phần khác.");
                    result.ActionableSuggestions.Add("Cán bộ có thể điều chỉnh phòng học và tiết học tại mục 'Quản lý Lớp học phần'.");
                }
                else
                {
                    // MẶC ĐỊNH CHO MÔN HỌC: Trả lời chính xác sĩ số sinh viên, số lớp mở, tỷ lệ lấp đầy
                    result.Category = "Thống Kê Sĩ Số & Lớp Học Phần";

                    var sectionDetails = new List<string>();
                    foreach (var s in courseSections)
                    {
                        double rate = s.MaxCapacity > 0 ? Math.Round((double)s.CurrentEnrollment / s.MaxCapacity * 100, 1) : 0;
                        sectionDetails.Add($"- **Lớp {s.SectionCode}**: Giảng viên **{s.Instructor}** | {FormatSchedules(s)} | Sĩ số: **{s.CurrentEnrollment}/{s.MaxCapacity} sinh viên** ({rate}%)");
                    }

                    result.AnswerSummary = $"Môn học **{matchedCourse.CourseName} (Mã môn: {matchedCourse.CourseCode} &bull; {matchedCourse.Credits} tín chỉ)** trong học kỳ **{semester}** hiện có tổng cộng **{totalEnrolled} sinh viên** đã đăng ký thành công trên tổng chỉ tiêu **{totalCapacity} chỗ** (Tỷ lệ lấp đầy: **{overallRate}%**).\n\n" +
                        $"Hiện tại Phòng Đào Tạo đang mở **{courseSections.Count} lớp học phần** cho môn này:\n" +
                        (sectionDetails.Any() ? string.Join("\n", sectionDetails) : "- Hiện chưa mở lớp học phần nào trong kỳ.") +
                        "\n\n*(Thông tin chi tiết và thanh tiến độ sĩ số từng lớp học phần được hiển thị trong bảng phía dưới)*";

                    if (overallRate >= 85)
                    {
                        result.ActionableSuggestions.Add($"Tỷ lệ lấp đầy môn học rất cao ({overallRate}%). Cán bộ Phòng Đào Tạo có thể xem xét mở thêm 1 lớp song song hoặc tăng chỉ tiêu MaxCapacity để hỗ trợ sinh viên chưa kịp đăng ký.");
                    }
                    else if (overallRate < 50)
                    {
                        result.ActionableSuggestions.Add($"Tỷ lệ đăng ký môn này còn dưới 50% ({overallRate}%). Cần theo dõi thêm trong đợt đăng ký bổ sung trước khi xem xét gộp lớp.");
                    }

                    result.ActionableSuggestions.Add("Cán bộ có thể bấm nút 'In danh sách' tại mục 'Quản lý Đăng ký' để xuất bảng điểm danh danh sách sinh viên cụ thể theo từng lớp.");
                    result.ActionableSuggestions.Add("Có thể tùy chỉnh thông tin giảng viên, phòng học hoặc sức chứa tại trang 'Quản lý Lớp học phần'.");
                }
            }
            // -------------------------------------------------------------------------
            // NHÁNH 3: HỎI VỀ GIẢNG VIÊN (VD: "Thầy Hùng dạy bao nhiêu sinh viên", "Thầy Dũng")
            // -------------------------------------------------------------------------
            else if (matchedInstructor != null && matchedInstructor.Value.Sections.Any())
            {
                result.Category = "Tra Cứu Thông Tin Giảng Viên & Phân Công";
                var (instructorName, instSections) = matchedInstructor.Value;
                int totalStudents = instSections.Sum(s => s.CurrentEnrollment);
                int totalCapacity = instSections.Sum(s => s.MaxCapacity);

                foreach (var s in instSections)
                {
                    result.Sections.Add(CreateSectionItem(s));
                }

                var courseNames = string.Join(", ", instSections.Select(s => s.Course?.CourseName).Where(n => !string.IsNullOrEmpty(n)).Distinct());
                result.AnswerSummary = $"Thông tin giảng dạy của giảng viên **{instructorName}**:\n\n" +
                    $"- **Tổng số lớp học phần đảm nhiệm:** **{instSections.Count} lớp**.\n" +
                    $"- **Tổng sĩ số sinh viên đang theo học:** **{totalStudents} sinh viên** (trên tổng sức chứa {totalCapacity} chỗ).\n" +
                    $"- **Các môn giảng dạy:** {courseNames}.\n\n" +
                    "Chi tiết các lớp học phần được phân công hiển thị trong bảng bên dưới:";

                result.ActionableSuggestions.Add("Cán bộ có thể xuất danh sách sinh viên theo từng lớp của giảng viên để gửi danh sách điểm danh cho thầy/cô.");
                result.ActionableSuggestions.Add("Có thể điều chỉnh lịch học hoặc phân công giảng viên tại mục 'Quản lý Lớp học phần'.");
            }
            else if (isInstructorInquiry && matchedInstructor == null)
            {
                result.Category = "Tra Cứu Thông Tin Giảng Viên & Phân Công";
                var activeInstructors = sectionsPool
                    .Where(s => !string.IsNullOrEmpty(s.Instructor))
                    .GroupBy(s => s.Instructor)
                    .OrderBy(g => g.Key)
                    .ToList();

                var sb = new StringBuilder();
                sb.AppendLine($"Hệ thống đã tra cứu CSDL SQL Server nhưng **không tìm thấy giảng viên** nào khớp với nội dung câu hỏi: *\"{qRaw}\"*.\n");
                sb.AppendLine("👨‍🏫 **Danh sách các giảng viên hiện đang có phân công giảng dạy trong hệ thống CSDL nhà trường:**");
                foreach (var g in activeInstructors)
                {
                    var coursesTaught = string.Join(", ", g.Select(s => s.Course?.CourseName).Where(n => !string.IsNullOrEmpty(n)).Distinct());
                    sb.AppendLine($"- **{g.Key}**: phụ trách môn *{coursesTaught}*");
                }

                result.AnswerSummary = sb.ToString();
                result.ActionableSuggestions.Add("Kiểm tra lại họ tên hoặc học hàm/học vị của giảng viên tại phân hệ Quản lý Lớp học phần.");
            }
            // -------------------------------------------------------------------------
            // NHÁNH 4: HỎI VỀ SINH VIÊN CỤ THỂ (VD: "SV21001", "Nguyễn Văn Tuấn")
            // -------------------------------------------------------------------------
            else if (matchedStudent != null)
            {
                result.Category = "Tra Cứu Hồ Sơ & Học Vụ Sinh Viên";
                var registeredSections = (await _enrollmentRepository.GetRegisteredSectionsByStudentAsync(matchedStudent.StudentId, semester)).ToList();
                int totalCredits = registeredSections.Sum(r => r.Course?.Credits ?? 0);

                result.Students.Add(new AdminAiStudentItem
                {
                    StudentId = matchedStudent.StudentId,
                    StudentCode = matchedStudent.StudentCode,
                    FullName = matchedStudent.FullName,
                    Major = matchedStudent.Major,
                    RegisteredCredits = totalCredits,
                    RegisteredCoursesCount = registeredSections.Count,
                    Status = totalCredits >= 14 ? "Tiến độ tốt" : (totalCredits > 0 ? "Đang đăng ký" : "Chưa đăng ký môn")
                });

                foreach (var s in registeredSections)
                {
                    result.Sections.Add(CreateSectionItem(s));
                }

                var courseNames = registeredSections.Any()
                    ? string.Join("\n", registeredSections.Select(s => $"- **{s.SectionCode}**: {s.Course?.CourseName} ({s.Course?.Credits}TC) - GV {s.Instructor}"))
                    : "Chưa đăng ký học phần nào trong học kỳ này.";

                result.AnswerSummary = $"Hồ sơ học vụ của sinh viên **{matchedStudent.FullName} (MSSV: {matchedStudent.StudentCode})**:\n\n" +
                    $"- **Chuyên ngành:** {matchedStudent.Major} | Khóa: {matchedStudent.AcademicYear} | Email: {matchedStudent.Email}.\n" +
                    $"- **Tổng số tín chỉ đã đăng ký kỳ {semester}:** **{totalCredits} / {matchedStudent.MaxCreditsPerSemester} tín chỉ**.\n" +
                    $"- **Số lớp học phần đã ghi danh:** **{registeredSections.Count} lớp**.\n\n" +
                    $"Danh sách các lớp học phần sinh viên đang theo học:\n{courseNames}";

                result.ActionableSuggestions.Add("Cán bộ có thể truy cập 'Quản lý Đăng ký & Rút môn SV' để xem chi tiết hoặc thực hiện rút môn cứu xét cho sinh viên.");
                result.ActionableSuggestions.Add("Sinh viên có thể xuất phiếu xác nhận đăng ký học phần dạng file Excel hoặc PDF kèm mã biên nhận.");
            }
            // -------------------------------------------------------------------------
            // NHÁNH 5: CẢNH BÁO LỚP QUÁ TẢI / SẮP ĐẦY (>75%)
            // -------------------------------------------------------------------------
            else if (isOverloadIntent)
            {
                var highFillSections = allSections
                    .Where(s => s.MaxCapacity > 0 && (double)s.CurrentEnrollment / s.MaxCapacity >= 0.75)
                    .OrderByDescending(s => (double)s.CurrentEnrollment / s.MaxCapacity)
                    .ToList();

                result.Category = "Cảnh Báo Quá Tải Sĩ Số Lớp Học Phần";
                result.AnswerSummary = $"Hệ thống rà soát và phát hiện **{highFillSections.Count} lớp học phần** có tỷ lệ lấp đầy cao (từ 75% trở lên đến 100% sĩ số).\n\n" +
                    "Nhu cầu sinh viên đối với các học phần này rất lớn, Phòng Đào Tạo cần chủ động xem xét phương án mở thêm lớp học phần song song hoặc chuyển sang phòng học có sức chứa lớn hơn.";

                foreach (var s in highFillSections)
                {
                    result.Sections.Add(CreateSectionItem(s));
                }

                result.ActionableSuggestions.Add("Đề xuất mở thêm 1 lớp song song cho các môn có lớp đạt 100% sĩ số để đáp ứng nhu cầu sinh viên.");
                result.ActionableSuggestions.Add("Chuyển các lớp quá tải sang phòng Lab hoặc Giảng đường lớn hơn tại trang Quản lý Lớp học phần.");
            }
            // -------------------------------------------------------------------------
            // NHÁNH 6: CẢNH BÁO LỚP ÍT SINH VIÊN / NGUY CƠ HỦY LỚP (<50%)
            // -------------------------------------------------------------------------
            else if (isLowEnrollmentIntent)
            {
                var lowFillSections = allSections
                    .Where(s => s.MaxCapacity > 0 && (double)s.CurrentEnrollment / s.MaxCapacity < 0.5)
                    .OrderBy(s => (double)s.CurrentEnrollment / s.MaxCapacity)
                    .ToList();

                result.Category = "Cảnh Báo Nguy Cơ Hủy Lớp Do Sĩ Số Thấp";
                result.AnswerSummary = $"Phát hiện **{lowFillSections.Count} lớp học phần** có tỷ lệ đăng ký dưới 50% chỉ tiêu thiết kế trong kỳ {semester}.\n\n" +
                    "Theo quy chế đào tạo tín chỉ, các lớp có sĩ số quá thấp cần được theo dõi sát sao trước khi kết thúc đợt đăng ký tín chỉ trực tuyến để có phương án xử lý.";

                foreach (var s in lowFillSections)
                {
                    result.Sections.Add(CreateSectionItem(s));
                }

                result.ActionableSuggestions.Add("Gửi thông báo nhắc nhở sinh viên các khóa liên quan đăng ký bổ sung.");
                result.ActionableSuggestions.Add("Cân nhắc gộp lớp hoặc chuyển sinh viên sang khung giờ học phần khác nếu sĩ số dưới 10 SV.");
            }
            // -------------------------------------------------------------------------
            // NHÁNH 7: TỔNG QUAN HỌC KỲ / BÁO CÁO TOÀN TRƯỜNG
            // -------------------------------------------------------------------------
            else if (isSchoolOverviewIntent)
            {
                int totalCapacity = allSections.Sum(s => s.MaxCapacity);
                int totalEnrolled = allSections.Sum(s => s.CurrentEnrollment);
                double overallRate = totalCapacity > 0 ? Math.Round((double)totalEnrolled / totalCapacity * 100, 1) : 0;

                result.Category = "Báo Cáo Tổng Quan Tình Hình Học Vụ";
                result.AnswerSummary = $"**Báo cáo thống kê toàn diện tình hình đào tạo học kỳ {semester}:**\n\n" +
                    $"- **Tổng số sinh viên chính quy trong CSDL:** **{allStudents.Count} sinh viên**.\n" +
                    $"- **Tổng số môn học đang quản lý:** **{allCourses.Count} môn học**.\n" +
                    $"- **Tổng số lớp học phần đang mở:** **{allSections.Count} lớp**.\n" +
                    $"- **Tổng sức chứa đào tạo toàn trường:** **{totalCapacity} chỗ ngồi**.\n" +
                    $"- **Tổng số lượt sinh viên đã ghi danh thành công:** **{totalEnrolled} lượt**.\n" +
                    $"- **Tỷ lệ lấp đầy bình quân toàn trường:** **{overallRate}%**.";

                foreach (var s in allSections.OrderByDescending(s => (double)s.CurrentEnrollment / Math.Max(1, s.MaxCapacity)).Take(6))
                {
                    result.Sections.Add(CreateSectionItem(s));
                }

                result.ActionableSuggestions.Add("Cán bộ có thể bấm vào 'Quản lý Đăng ký' để xem danh sách sinh viên chi tiết theo từng lớp.");
                result.ActionableSuggestions.Add("Có thể theo dõi các lớp học phần quá tải hoặc lớp ít sinh viên bằng các nút tác vụ nhanh bên trên.");
            }
            // -------------------------------------------------------------------------
            // NHÁNH 8: QUY CHẾ HỌC VỤ (RÚT MÔN, TIÊN QUYẾT, TÍN CHỈ, TRÙNG LỊCH)
            // -------------------------------------------------------------------------
            else if (isDropCourseIntent)
            {
                result.Category = "Quy Chế Rút Môn & Hủy Học Phần";
                result.AnswerSummary = "Theo quy chế đào tạo theo hệ thống tín chỉ hiện hành của trường:\n\n" +
                    "- **Trong đợt đăng ký tín chỉ trực tuyến:** Sinh viên có quyền tự do hủy học phần hoặc đổi sang lớp học phần khác trực tiếp trên website mà không bị phạt hay ghi điểm F.\n" +
                    "- **Sau khi kết thúc thời hạn đăng ký trực tuyến:** Cổng đăng ký của sinh viên sẽ tự động khóa. Để rút học phần, sinh viên phải làm đơn cứu xét/rút môn có lý do chính đáng và nộp về **Phòng Đào Tạo**.\n" +
                    "- **Thẩm quyền của Cán bộ Đào tạo:** Cán bộ có thể truy cập phân hệ *Quản lý Đăng ký & Hủy / Rút Môn Học Vụ*, tìm tên sinh viên và bấm nút **'Rút tên SV'**. Khi đó hệ thống sẽ tự động gỡ sinh viên và **hoàn trả 1 chỉ tiêu sĩ số** cho lớp học phần đó.";

                result.ActionableSuggestions.Add("Truy cập menu 'Phòng Đào Tạo' -> 'Quản lý Đăng ký & Rút môn SV' để xử lý hồ sơ rút môn cho sinh viên.");
                result.ActionableSuggestions.Add("Sau khi rút môn, tổng số tín chỉ của sinh viên không được thấp hơn hạn mức tối thiểu (10 tín chỉ).");
            }
            else if (isPrerequisiteIntent)
            {
                result.Category = "Quy Chế Môn Học Tiên Quyết";
                result.AnswerSummary = "Quy định về học phần tiên quyết trong hệ thống đào tạo tín chỉ:\n\n" +
                    "- **Khái niệm:** Học phần tiên quyết là học phần mà sinh viên bắt buộc phải hoàn thành và đạt điểm từ **5.0/10 trở lên** trước khi được phép ghi danh vào học phần tiếp theo.\n" +
                    "- **Cơ chế kiểm soát tự động:** Hệ thống kiểm tra tức thời ma trận tiên quyết khi sinh viên thêm môn vào giỏ hoặc bấm đăng ký. Nếu chưa đạt môn tiên quyết, hệ thống lập tức hiển thị cảnh báo từ chối ghi danh.\n" +
                    "- **Các cặp môn tiên quyết tiêu biểu trong trường:**\n" +
                    "  + *Nhập môn lập trình (TIN101)* là tiên quyết cho *Lập trình Web (WEB301)* & *Cấu trúc dữ liệu & Giải thuật (TIN201)*.\n" +
                    "  + *Lập trình Hướng đối tượng (TIN102)* là tiên quyết cho *Kỹ thuật Phần mềm (KTPM302)* & *Lập trình Web (WEB301)*.";

                result.ActionableSuggestions.Add("Cán bộ có thể xem cấu hình tiên quyết chi tiết tại trang Quản lý Môn Học.");
                result.ActionableSuggestions.Add("Sinh viên chưa qua môn tiên quyết có thể đăng ký học lại môn đó trong các kỳ phụ hoặc học kỳ chính tiếp theo.");
            }
            else if (isCreditLimitIntent)
            {
                result.Category = "Quy Định Hạn Mức Tín Chỉ";
                result.AnswerSummary = "Quy định số lượng tín chỉ đăng ký trong mỗi học kỳ chính:\n\n" +
                    "- **Hạn mức tối thiểu:** **10 tín chỉ/học kỳ** (áp dụng cho sinh viên xếp loại học lực bình thường để đảm bảo tiến độ đào tạo theo khung thời gian chuẩn).\n" +
                    "- **Hạn mức tối đa tiêu chuẩn:** **24 tín chỉ/học kỳ** (cho phép sinh viên khá giỏi học vượt, đăng ký thêm học phần chuyên ngành).\n" +
                    "- **Trường hợp cảnh báo học vụ:** Sinh viên có điểm trung bình tích lũy dưới 2.0 hoặc bị cảnh báo học vụ sẽ bị giới hạn tối đa chỉ được đăng ký từ **10 - 14 tín chỉ**.\n" +
                    "- **Sinh viên năm cuối:** Sinh viên làm đồ án tốt nghiệp hoặc trong kỳ cuối có thể được xét miễn giảm số tín chỉ tối thiểu theo đơn.";

                result.ActionableSuggestions.Add("Kiểm tra danh sách sinh viên vượt quá hoặc dưới chuẩn hạn mức tín chỉ tại phân hệ Tra cứu Hồ sơ.");
            }
            else if (isScheduleConflictIntent)
            {
                result.Category = "Quy Chế Xung Đột & Trùng Lịch Học";
                result.AnswerSummary = "Quy định và thuật toán giải quyết xung đột lịch học:\n\n" +
                    "- **Nguyên tắc bất khả kháng:** Sinh viên không được phép đăng ký hai lớp học phần có tiết học trùng nhau tại cùng một thứ trong tuần.\n" +
                    "- **Cơ chế ma trận tiết học:** Một ngày được chia làm Ca sáng (Tiết 1 - 5) và Ca chiều (Tiết 6 - 10). Nếu khoảng `[StartPeriod, EndPeriod]` của hai lớp bị giao nhau trong cùng `DayOfWeek`, hệ thống sẽ từ chối lưu vào giỏ.\n" +
                    "- **Thời khóa biểu trực quan:** Sau khi đăng ký thành công, sinh viên có thể xem lưới TKB tuần và xuất file Excel/PDF tại mục 'Thời khóa biểu'.";

                result.ActionableSuggestions.Add("Sinh viên nên chọn lớp học phần có khung giờ khác hoặc đổi sang ca học song song nếu bị trùng lịch.");
            }
            // -------------------------------------------------------------------------
            // NHÁNH 9: XUẤT BÁO CÁO & IN ẤN HỌC VỤ (CHỈ KHI HỎI RÕ RÀNG VỀ XUẤT/IN BÁO CÁO)
            // -------------------------------------------------------------------------
            else if (isExportReportIntent)
            {
                result.Category = "Tiện Ích Xuất Báo Cáo Học Vụ";
                result.AnswerSummary = "Hệ thống hỗ trợ xuất báo cáo chuẩn xác thời gian thực từ CSDL SQL Server:\n\n" +
                    "1. **Báo cáo Lịch học cá nhân của Sinh viên:** Hỗ trợ xuất file Excel (.xlsx) và file PDF phiếu đăng ký học phần kèm mã biên nhận điện tử.\n" +
                    "2. **Báo cáo Danh sách Lớp Học Phần cho Giảng viên:** Cán bộ Phòng Đào Tạo có thể bấm nút **'In danh sách'** tại trang *Quản lý Đăng ký* để xuất bảng điểm danh danh sách sinh viên theo từng lớp học phần (MSSV, Họ tên, Ngành, Email, Thời điểm đăng ký).\n" +
                    "3. Dữ liệu báo cáo được kết xuất trực tiếp theo chuẩn ClosedXML và QuestPDF không dùng dữ liệu giả lập.";

                result.ActionableSuggestions.Add("Bấm vào nút 'In danh sách' tại mục Quản lý Đăng ký để in hoặc tải file báo cáo lớp học phần.");
            }
            // -------------------------------------------------------------------------
            // NHÁNH 10: PHÒNG HỌC & CƠ SỞ VẬT CHẤT
            // -------------------------------------------------------------------------
            else if (isFacilityIntent)
            {
                var matchedSections = allSections.Where(s =>
                    !string.IsNullOrEmpty(s.Room) && (qNorm.Contains(RemoveDiacritics(s.Room)) || s.Room.ToLowerInvariant().Contains(qLower))
                ).ToList();

                if (!matchedSections.Any()) matchedSections = allSections.Take(6).ToList();

                result.Category = "Tra Cứu Phòng Học & Cơ Sở Vật Chất";
                result.AnswerSummary = $"Thông tin bố trí phòng học trong kỳ {semester}:\n\n" +
                    $"- Đang có **{matchedSections.Count} lớp học phần** được xếp lịch giảng dạy tại các phòng này.\n" +
                    "Chi tiết lịch học và sĩ số các lớp tại từng phòng:";

                foreach (var s in matchedSections)
                {
                    result.Sections.Add(CreateSectionItem(s));
                }

                result.ActionableSuggestions.Add("Cán bộ có thể điều chỉnh phòng học trực tiếp tại mục Quản lý Lớp học phần.");
            }
            // -------------------------------------------------------------------------
            // NHÁNH 11: HỌC PHÍ & THANH TOÁN VIETQR
            // -------------------------------------------------------------------------
            else if (isTuitionIntent)
            {
                result.Category = "Quy Định Mức Thu Học Phí & Thanh Toán";
                result.AnswerSummary = "Thông tin về học phí và thanh toán học kỳ " + semester + " tại Trường Đại học Kinh tế Huế:\n\n" +
                    "- **Mức thu chuẩn:** **450.000 VNĐ / tín chỉ**.\n" +
                    "- **Ví dụ cụ thể:**\n" +
                    "  + Môn học 3 tín chỉ: **1.350.000 VNĐ**\n" +
                    "  + Môn học 4 tín chỉ: **1.800.000 VNĐ**\n" +
                    "  + Kỳ học tiêu chuẩn (14 tín chỉ): **6.300.000 VNĐ**\n\n" +
                    "- **Thông tin tài khoản thu học phí nhà trường:**\n" +
                    "  + Số tài khoản: **101705201967**\n" +
                    "  + Ngân hàng: **MB Bank (Ngân hàng TMCP Quân Đội)**\n" +
                    "  + Đơn vị thụ hưởng: **TRƯỜNG ĐẠI HỌC KINH TẾ HUẾ**\n" +
                    "- **Cơ chế thanh toán VietQR Napas247:** Sinh viên có thể vào mục 'Học phí' quét mã VietQR tự động điền đúng số tiền và cú pháp `[MSSV] HP 2026_HK1`, gạch nợ tức thì 24/7.\n" +
                    "- **Thời hạn nộp học phí:** Trong vòng 15 ngày kể từ ngày gửi phiếu đăng ký học phần.";

                result.ActionableSuggestions.Add("Cán bộ có thể theo dõi danh sách sinh viên đã nộp và chưa nộp học phí tại menu 'Quản lý Học phí'.");
                result.ActionableSuggestions.Add("Sinh viên có thể tra cứu và gạch nợ trực tiếp bằng quét mã VietQR.");
            }
            // -------------------------------------------------------------------------
            // NHÁNH 12: KẾ HOẠCH & THỜI GIAN MỞ CỔNG ĐĂNG KÝ HỌC PHẦN
            // -------------------------------------------------------------------------
            else if (isRegistrationPeriodIntent)
            {
                result.Category = "Kế Hoạch & Thời Gian Mở Cổng Đăng Ký";
                result.AnswerSummary = "Kế hoạch tổ chức đăng ký học phần học kỳ **" + semester + "** (Năm học 2026 - 2027):\n\n" +
                    "1. **Đợt đăng ký chính thức:** Từ **15/01/2026 đến hết 28/01/2026** (Cổng mở 24/24 cho toàn thể sinh viên).\n" +
                    "2. **Đợt đăng ký bổ sung & Rút môn trực tuyến:** Từ **05/02/2026 đến 12/02/2026**.\n" +
                    "3. **Thời hạn nộp học phí:** Đến hết ngày **28/02/2026**.\n" +
                    "4. **Ngày bắt đầu học kỳ mới:** Ngày **01/03/2026**.";

                result.ActionableSuggestions.Add("Sinh viên cần chủ động hoàn tất đăng ký đúng thời hạn để tránh tình trạng lớp học phần bị hết chỗ.");
                result.ActionableSuggestions.Add("Cán bộ có thể điều chỉnh thời gian mở/đóng từng lớp học phần tại trang 'Quản lý Lớp học phần'.");
            }
            // -------------------------------------------------------------------------
            // NHÁNH 13: HƯỚNG DẪN CÁC BƯỚC ĐĂNG KÝ HỌC PHẦN
            // -------------------------------------------------------------------------
            else if (isRegistrationGuideIntent)
            {
                result.Category = "Hướng Dẫn Quy Trình Đăng Ký Học Phần";
                result.AnswerSummary = "Quy trình 5 bước đăng ký học phần trên Cổng thông tin trực tuyến:\n\n" +
                    "1. **Bước 1 - Đăng nhập tài khoản:** Bấm nút **'Đăng nhập'** góc phải, dùng mã sinh viên (VD: `sv21001`) và mật khẩu.\n" +
                    "2. **Bước 2 - Tìm kiếm & Lựa chọn môn học:** Vào menu **'Tra cứu học phần'** hoặc nhờ **'Cố vấn AI'** tư vấn môn phù hợp.\n" +
                    "3. **Bước 3 - Thêm vào giỏ đăng ký:** Chọn lớp học phần phù hợp và bấm **'Thêm vào giỏ'** (Hệ thống tự động kiểm tra tiên quyết và trùng lịch).\n" +
                    "4. **Bước 4 - Xác nhận đăng ký:** Vào **'Giỏ đăng ký'**, kiểm tra tổng số tín chỉ (từ 10 - 24 TC) và bấm **'Gửi phiếu đăng ký'**.\n" +
                    "5. **Bước 5 - Xem thời khóa biểu & Nộp học phí:** Kiểm tra lịch học tại mục **'Thời khóa biểu'** và quét mã VietQR để hoàn tất học phí.";

                result.ActionableSuggestions.Add("Tài khoản thử nghiệm demo: Sinh viên `sv21001` (mk: `123456`), Quản trị `admin` (mk: `admin123`).");
            }
            // -------------------------------------------------------------------------
            // NHÁNH 14: THANG ĐIỂM, GPA, HỌC LỰC, HỌC LẠI & CẢI THIỆN
            // -------------------------------------------------------------------------
            else if (isGradingIntent)
            {
                result.Category = "Quy Chế Thang Điểm & Học Vụ";
                result.AnswerSummary = "Quy chế đánh giá kết quả học tập & thang điểm tín chỉ:\n\n" +
                    "- **Thang điểm 10 quy đổi sang Thang điểm 4 & Điểm chữ:**\n" +
                    "  + Điểm A (8.5 - 10.0): **4.0** (Xuất sắc)\n" +
                    "  + Điểm B+ (8.0 - 8.4): **3.5** (Giỏi)\n" +
                    "  + Điểm B (7.0 - 7.9): **3.0** (Khá)\n" +
                    "  + Điểm C+ (6.5 - 6.9): **2.5** (Trung bình khá)\n" +
                    "  + Điểm C (5.5 - 6.4): **2.0** (Trung bình)\n" +
                    "  + Điểm D+ (5.0 - 5.4): **1.5** (Trung bình yếu)\n" +
                    "  + Điểm D (4.0 - 4.9): **1.0** (Đạt)\n" +
                    "  + Điểm F (< 4.0): **0.0** (Không đạt - Bắt buộc học lại)\n\n" +
                    "- **Học lại:** Áp dụng khi bị điểm F (điểm < 4.0). Bắt buộc học lại môn đó hoặc môn tương đương.\n" +
                    "- **Học cải thiện:** Sinh viên có điểm D, D+ được phép đăng ký học lại để cải thiện điểm. Điểm cao hơn sẽ được lấy tính GPA.\n" +
                    "- **Cảnh báo học vụ:** Sinh viên có ĐTB tích lũy < 1.2 (năm 1), < 1.4 (năm 2), < 1.6 (năm 3), < 1.8 (năm 4) sẽ bị cảnh báo học vụ. Bị cảnh báo 2 lần liên tiếp có thể bị buộc thôi học.";

                result.ActionableSuggestions.Add("Sinh viên có thể tra cứu lịch sử môn đã học và điểm số tại trang Cố vấn AI sau khi đăng nhập.");
            }
            // -------------------------------------------------------------------------
            // NHÁNH 15: THÔNG TIN TRƯỜNG & CÁC KHOA / CHUYÊN NGÀNH
            // -------------------------------------------------------------------------
            else if (isUniversityInfoIntent)
            {
                result.Category = "Thông Tin Trường & Các Ngành Đào Tạo";
                result.AnswerSummary = "Thông tin chung về **Trường Đại học Kinh tế - Đại học Huế (HCE)**:\n\n" +
                    "- **Địa chỉ:** Số 99 Hồ Đắc Di, Phường An Cựu, TP. Huế, Tỉnh Thừa Thiên Huế.\n" +
                    "- **Website:** https://hce.edu.vn\n" +
                    "- **Các khoa đào tạo chuyên môn:**\n" +
                    "  + Khoa Hệ thống Thông tin Kinh tế & Thương mại điện tử\n" +
                    "  + Khoa Quản trị Kinh doanh\n" +
                    "  + Khoa Kinh tế & Phát triển\n" +
                    "  + Khoa Kế toán - Kiểm toán\n" +
                    "  + Khoa Tài chính - Ngân hàng\n" +
                    "- **Các ngành đào tạo tiêu biểu:** Hệ thống thông tin quản lý, Thương mại điện tử, Công nghệ thông tin, Quản trị kinh doanh, Kinh tế số, Kế toán, Tài chính ngân hàng...";

                result.ActionableSuggestions.Add("Hệ thống Đăng ký Tín chỉ phục vụ trực tuyến cho toàn thể sinh viên các khoa viện của nhà trường.");
            }
            // -------------------------------------------------------------------------
            // NHÁNH 16: THỦ TỤC SINH VIÊN (XÁC NHẬN, NGHĨA VỤ QUÂN SỰ, BẢO LƯU)
            // -------------------------------------------------------------------------
            else if (isStudentAffairsIntent)
            {
                result.Category = "Thủ Tục Hành Chính Sinh Viên";
                result.AnswerSummary = "Hướng dẫn thực hiện các thủ tục hành chính sinh viên:\n\n" +
                    "1. **Giấy xác nhận sinh viên (tạm hoãn nghĩa vụ quân sự, vay vốn NHCSXH, làm vé xe buýt):**\n" +
                    "   - Sinh viên nộp yêu cầu tại **Bộ phận Một Cửa (Phòng Đào Tạo)** hoặc đăng ký trên Cổng Dịch vụ công sinh viên.\n" +
                    "   - Thời gian cấp: Trong vòng 24 - 48 giờ làm việc.\n" +
                    "2. **Cấp lại thẻ sinh viên / Bảng điểm tạm thời:** Liên hệ trực tiếp Văn phòng Khoa hoặc Phòng Đào Tạo.\n" +
                    "3. **Bảo lưu kết quả học tập:** Sinh viên làm đơn (kèm minh chứng lý do ốm đau, hoàn cảnh gia đình hoặc thi hành nghĩa vụ quân sự) và nộp trước khi học kỳ bắt đầu ít nhất 2 tuần.";

                result.ActionableSuggestions.Add("Liên hệ Bộ phận Một cửa - Phòng Đào Tạo (Tầng 1 Nhà Hiệu Bộ) để được hỗ trợ thủ tục trực tiếp.");
            }
            // -------------------------------------------------------------------------
            // NHÁNH 17: HỌC BỔNG KHUYẾN KHÍCH HỌC TẬP
            // -------------------------------------------------------------------------
            else if (isScholarshipIntent)
            {
                result.Category = "Chính Sách Học Bổng Khuyến Khích";
                result.AnswerSummary = "Chính sách xét cấp học bổng khuyến khích học tập tại Trường Đại học Kinh tế Huế:\n\n" +
                    "- **Điều kiện xét:**\n" +
                    "  + Đăng ký tối thiểu 14 tín chỉ trong học kỳ chính.\n" +
                    "  + Không có học phần nào bị điểm F (rớt môn) trong kỳ xét tuyển.\n" +
                    "  + Điểm rèn luyện từ loại Tốt trở lên (>= 80 điểm).\n" +
                    "- **Các mức học bổng:**\n" +
                    "  + **Xuất sắc:** GPA >= 3.6 & Điểm rèn luyện Xuất sắc (>= 90 điểm).\n" +
                    "  + **Giỏi:** GPA từ 3.2 - 3.59 & Điểm rèn luyện Tốt (>= 80 điểm).\n" +
                    "  + **Khá:** GPA từ 2.5 - 3.19 & Điểm rèn luyện Khá trở lên.\n" +
                    "- Học bổng được chuyển khoản trực tiếp vào tài khoản ngân hàng của sinh viên.";

                result.ActionableSuggestions.Add("Sinh viên cần duy trì tiến độ học tập và tích cực tham gia hoạt động phong trào để đạt học bổng.");
            }
            // -------------------------------------------------------------------------
            // NHÁNH 18: CHÀO HỎI & HƯỚNG DẪN HỆ THỐNG
            // -------------------------------------------------------------------------
            else if (isGreetingIntent)
            {
                result.Category = "Trợ Lý AI Đào Tạo Sẵn Sàng Phục Vụ";
                result.AnswerSummary = "Xin chào Quý Thầy/Cô và các bạn!\n\n" +
                    "Tôi là **Trợ lý AI Quản Trị Đào Tạo (Admin Copilot)**. Tôi có thể hỗ trợ và giải đáp trực tiếp từ cơ sở dữ liệu các nội dung sau:\n\n" +
                    "1. **Thống kê sĩ số môn học & lớp học phần:** Hỏi môn học có bao nhiêu sinh viên, số lớp đang mở, phòng học, giảng viên phụ trách.\n" +
                    "2. **Cảnh báo học vụ:** Phát hiện lớp học phần quá tải (>75%) hoặc lớp sĩ số thấp (<50%) có nguy cơ hủy lớp.\n" +
                    "3. **Tra cứu Giảng viên & Sinh viên:** Phân công giảng dạy của thầy cô, tiến độ đăng ký tín chỉ của sinh viên theo MSSV.\n" +
                    "4. **Giải đáp Quy chế:** Quy định rút môn, hạn mức 10-24 tín chỉ, điều kiện tiên quyết, giải quyết trùng lịch.\n" +
                    "5. **Xuất báo cáo:** Hướng dẫn in danh sách điểm danh lớp học phần và xuất file Excel/PDF.\n\n" +
                    "Bạn có thể gõ câu hỏi bất kỳ ở thanh tìm kiếm bên trên!";

                result.ActionableSuggestions.Add("Thử hỏi: 'Lập trình ứng dụng Web có bao nhiêu sinh viên?'");
                result.ActionableSuggestions.Add("Thử hỏi: 'Lớp nào do TS. Hà Ngọc Long dạy?'");
                result.ActionableSuggestions.Add("Thử hỏi: 'Những lớp nào đang quá tải sĩ số?'");
            }
            // -------------------------------------------------------------------------
            // NHÁNH 12: MẶC ĐỊNH / TỰ ĐỘNG PHÂN TÍCH TỪ KHÓA MỜ THÔNG MINH
            // -------------------------------------------------------------------------
            else
            {
                // Tách từ khóa loại trừ stop-words tiếng Việt
                var stopWords = new HashSet<string> { "co", "la", "cac", "nhung", "cho", "cua", "trong", "va", "ve", "duoc", "khong", "thi", "de", "voi", "o", "tai", "nhu", "nao", "gi", "sao", "mot", "hai", "ba" };
                var words = qNorm.Split(new[] { ' ', ',', '.', '?', '!', ':', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length >= 2 && !stopWords.Contains(w))
                    .ToList();

                var matched = allSections.Where(s =>
                    words.Any(w =>
                        (s.Course != null && RemoveDiacritics(s.Course.CourseName).Contains(w)) ||
                        (s.Course != null && s.Course.CourseCode.ToLowerInvariant().Contains(w)) ||
                        (!string.IsNullOrEmpty(s.Instructor) && RemoveDiacritics(s.Instructor).Contains(w)) ||
                        s.SectionCode.ToLowerInvariant().Contains(w) ||
                        (!string.IsNullOrEmpty(s.Room) && s.Room.ToLowerInvariant().Contains(w))
                    )
                ).ToList();

                result.Category = "Phân Tích Dữ Liệu Học Vụ Trực Tiếp";

                if (matched.Any())
                {
                    result.AnswerSummary = $"Đã phân tích câu hỏi của bạn và tìm thấy **{matched.Count} lớp học phần** liên quan trực tiếp trong học kỳ {semester}:\n\n" +
                        "Dưới đây là thông tin chi tiết về sĩ số, giảng viên và thời khóa biểu của từng lớp:";

                    foreach (var s in matched)
                    {
                        result.Sections.Add(CreateSectionItem(s));
                    }

                    result.ActionableSuggestions.Add("Cán bộ có thể bấm nút 'In danh sách' tại mục Quản lý Đăng ký để in hoặc tải file báo cáo lớp học phần.");
                    result.ActionableSuggestions.Add("Có thể xem thêm thông tin chi tiết tại mục Quản lý Lớp học phần.");
                }
                else
                {
                    int totalCapacity = allSections.Sum(s => s.MaxCapacity);
                    int totalEnrolled = allSections.Sum(s => s.CurrentEnrollment);
                    double overallRate = totalCapacity > 0 ? Math.Round((double)totalEnrolled / totalCapacity * 100, 1) : 0;

                    result.AnswerSummary = $"**Báo cáo tổng hợp số liệu học vụ kỳ {semester}:**\n\n" +
                        $"- Hệ thống đang vận hành **{allSections.Count} lớp học phần** cho **{allCourses.Count} môn học**.\n" +
                        $"- Tổng sức chứa đào tạo: **{totalCapacity} chỗ ngồi**.\n" +
                        $"- Tổng số lượt sinh viên đã ghi danh thành công: **{totalEnrolled} lượt** (Tỷ lệ lấp đầy toàn trường: **{overallRate}%**).\n" +
                        $"- Tổng số sinh viên chính quy: **{allStudents.Count} sinh viên**.\n\n" +
                        $"*Gợi ý:* Bạn có thể hỏi cụ thể tên môn học (như *Lập trình ứng dụng Web*, *Cơ sở dữ liệu*), tên giảng viên (*thầy Hùng*, *thầy Tuấn*), hoặc mã sinh viên (*SV21001*) để có kết quả chính xác nhất.";

                    foreach (var s in allSections.OrderByDescending(s => (double)s.CurrentEnrollment / Math.Max(1, s.MaxCapacity)).Take(6))
                    {
                        result.Sections.Add(CreateSectionItem(s));
                    }

                    result.ActionableSuggestions.Add("Thử hỏi: 'Lập trình ứng dụng Web có bao nhiêu sinh viên?'");
                    result.ActionableSuggestions.Add("Thử hỏi: 'Lớp nào đang sắp đầy chỗ?'");
                }
            }
        }

        private static Course? FindMatchingCourse(string qNorm, List<Course> allCourses)
        {
            // 1. Kiểm tra mã môn chính xác (VD: "web301", "csdl201")
            foreach (var c in allCourses)
            {
                var code = c.CourseCode.ToLowerInvariant();
                if (Regex.IsMatch(qNorm, $@"\b{Regex.Escape(code)}\b") || qNorm.Contains(code))
                {
                    return c;
                }
            }

            // 2. Kiểm tra tên môn không dấu đầy đủ
            foreach (var c in allCourses.OrderByDescending(c => c.CourseName.Length))
            {
                var cNorm = RemoveDiacritics(c.CourseName);
                if (qNorm.Contains(cNorm))
                {
                    return c;
                }
            }

            // 3. Tra cứu theo từ khóa viết tắt & biệt danh phổ biến trong trường
            var aliases = new Dictionary<string, string[]>
            {
                ["WEB301"] = new[] { "lap trinh ung dung web", "lap trinh web", "ung dung web", "ltw", "web301", "web" },
                ["WEB302"] = new[] { "web nang cao", "microservices", "web302" },
                ["CSDL201"] = new[] { "co so du lieu", "csdl201", "csdl", "database", "du lieu", "sql" },
                ["CSDL202"] = new[] { "csdl phan tan", "nosql", "csdl202", "mongodb", "phan tan" },
                ["TIN101"] = new[] { "nhap mon lap trinh", "nmlt", "tin101", "nhap mon", "c#" },
                ["TIN102"] = new[] { "lap trinh huong doi tuong", "huong doi tuong", "oop", "tin102" },
                ["TIN201"] = new[] { "cau truc du lieu", "giai thuat", "ctdl", "tin201", "thuat toan" },
                ["KTPM302"] = new[] { "ky thuat phan mem", "ktpm302", "ktpm", "phan mem", "scrum", "agile" },
                ["ATTT401"] = new[] { "an toan thong tin", "attt401", "attt", "bao mat", "an toan mang" },
                ["ATTT402"] = new[] { "an ninh mang", "attt402", "phong chong tan cong" },
                ["AI401"] = new[] { "tri tue nhan tao", "ai401", "hoc may", "machine learning", "tri tue" },
                ["TOEIC"] = new[] { "tieng anh", "anh van", "toeic", "ngoai ngu" }
            };

            foreach (var kv in aliases)
            {
                var matchedCourse = allCourses.FirstOrDefault(c => c.CourseCode.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
                if (matchedCourse != null)
                {
                    foreach (var alias in kv.Value)
                    {
                        if (alias == "web")
                        {
                            // Đối với từ ngắn như "web", chỉ match khi đi kèm các từ ngữ chỉ môn hoặc lập trình
                            if (qNorm.Contains("mon web") || qNorm.Contains("lop web") || qNorm.Contains("lap trinh web") || qNorm.Contains("hoc web"))
                            {
                                return matchedCourse;
                            }
                        }
                        else if (qNorm.Contains(alias))
                        {
                            return matchedCourse;
                        }
                    }
                }
            }

            return null;
        }

        private static Section? FindMatchingSection(string qNorm, List<Section> allSections)
        {
            var qClean = qNorm.Replace(".", "_").Replace("-", "_").Replace(" ", "_");
            foreach (var s in allSections)
            {
                var sCodeNorm = s.SectionCode.ToLowerInvariant();
                if (qNorm.Contains(sCodeNorm) || qClean.Contains(sCodeNorm))
                {
                    return s;
                }
            }
            return null;
        }

        private static string CleanAcademicTitle(string instructorName)
        {
            if (string.IsNullOrWhiteSpace(instructorName)) return string.Empty;
            var name = instructorName.Trim();
            string[] titles = new[]
            {
                "pgs.ts.", "pgs. ts.", "gs.ts.", "gs. ts.", "pgs.", "gs.",
                "ts.", "ths.", "cn.", "pgs.ts", "gs.ts", "pgs", "gs", "ts", "ths",
                "tiến sĩ", "tien si", "thạc sĩ", "thac si", "giáo sư", "giao su", "phó giáo sư", "pho giao su",
                "thầy", "thay", "cô", "co", "giảng viên", "giang vien", "gv", "gv."
            };
            bool changed = true;
            while (changed)
            {
                changed = false;
                var lower = name.Trim().ToLowerInvariant();
                var norm = RemoveDiacritics(lower);
                foreach (var t in titles)
                {
                    if (norm.StartsWith(t + " "))
                    {
                        int len = t.Length + 1;
                        name = name.Trim().Substring(Math.Min(name.Trim().Length, len)).Trim();
                        changed = true;
                        break;
                    }
                    else if (norm == t)
                    {
                        name = string.Empty;
                        changed = true;
                        break;
                    }
                }
            }
            return name.Trim();
        }

        private static (string InstructorName, List<Section> Sections)? FindMatchingInstructor(string qNorm, List<Section> allSections)
        {
            var instructors = allSections
                .Where(s => !string.IsNullOrEmpty(s.Instructor))
                .Select(s => s.Instructor)
                .Distinct()
                .ToList();

            var matches = new List<(int Score, string Name, List<Section> Sections)>();

            bool hasInstructorKeywords =
                qNorm.Contains("thay") || qNorm.Contains("co") ||
                qNorm.Contains("giang vien") || qNorm.Contains("gv") ||
                qNorm.Contains("day") || qNorm.Contains("phu trach") ||
                qNorm.Contains("lop cua") || qNorm.Contains("mon cua");

            foreach (var inst in instructors)
            {
                string rawNorm = RemoveDiacritics(inst);
                string cleanName = CleanAcademicTitle(inst);
                string cleanNorm = RemoveDiacritics(cleanName);

                int score = 0;

                // 1. Khớp nguyên họ tên đầy đủ đã lược bỏ học vị (ví dụ: "ha ngoc long", "ngo bao chau", "le hoang son")
                if (!string.IsNullOrEmpty(cleanNorm) && qNorm.Contains(cleanNorm))
                {
                    score = 100;
                }
                // 2. Khớp nguyên chuỗi gốc cả học vị (ví dụ: "ts ha ngoc long", "ts. ha ngoc long")
                else if (qNorm.Contains(rawNorm))
                {
                    score = 100;
                }
                else
                {
                    var words = cleanNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var lastName = words.LastOrDefault(); // "long", "truc", "son", "tuan", "tung", "chau", "cuong", "dung", "hanh"
                    var middleAndLast = words.Length >= 2 ? $"{words[words.Length - 2]} {lastName}" : string.Empty; // "ngoc long", "thanh truc", "hoang son"

                    // 3. Khớp tên đệm + tên chính (ví dụ: "ngoc long", "thanh truc")
                    if (!string.IsNullOrEmpty(middleAndLast) && qNorm.Contains(middleAndLast))
                    {
                        score = 80;
                    }
                    // 4. Khớp tên chính kèm kính ngữ / chức danh
                    else if (!string.IsNullOrEmpty(lastName) && lastName.Length >= 2)
                    {
                        if (qNorm.Contains("thay " + lastName) ||
                            qNorm.Contains("co " + lastName) ||
                            qNorm.Contains("gv " + lastName) ||
                            qNorm.Contains("giang vien " + lastName) ||
                            qNorm.Contains("lop cua " + lastName) ||
                            qNorm.Contains("mon cua " + lastName) ||
                            qNorm.Contains(lastName + " day") ||
                            qNorm.Contains(lastName + " phu trach") ||
                            qNorm.Contains(lastName + " giang day"))
                        {
                            score = 70;
                        }
                        else if (hasInstructorKeywords && Regex.IsMatch(qNorm, $@"\b{Regex.Escape(lastName)}\b"))
                        {
                            score = 50;
                        }
                    }
                }

                if (score > 0)
                {
                    var matchedSecs = allSections.Where(s => s.Instructor.Equals(inst, StringComparison.OrdinalIgnoreCase)).ToList();
                    matches.Add((score, inst, matchedSecs));
                }
            }

            if (matches.Any())
            {
                var best = matches.OrderByDescending(m => m.Score).First();
                return (best.Name, best.Sections);
            }

            return null;
        }

        private static Student? FindMatchingStudent(string qNorm, List<Student> allStudents)
        {
            foreach (var st in allStudents)
            {
                var code = st.StudentCode.ToLowerInvariant();
                if (Regex.IsMatch(qNorm, $@"\b{Regex.Escape(code)}\b") || qNorm.Contains(code))
                {
                    return st;
                }
            }

            foreach (var st in allStudents)
            {
                var nameNorm = RemoveDiacritics(st.FullName);
                if (qNorm.Contains(nameNorm))
                {
                    return st;
                }
            }

            return null;
        }

        private static string FormatSchedules(Section s)
        {
            if (s.Schedules != null && s.Schedules.Any())
            {
                return string.Join("; ", s.Schedules.Select(sch => $"{sch.DayOfWeekName} (Tiết {sch.StartPeriod}-{sch.EndPeriod}, {FormatRoom(sch.Room)})"));
            }
            return !string.IsNullOrEmpty(s.Room) ? FormatRoom(s.Room) : "Chưa xếp phòng";
        }

        private static string FormatRoom(string? room)
        {
            if (string.IsNullOrWhiteSpace(room)) return "Chưa xếp phòng";
            room = room.Trim();
            if (room.StartsWith("Phòng", StringComparison.OrdinalIgnoreCase) ||
                room.StartsWith("Lab", StringComparison.OrdinalIgnoreCase) ||
                room.StartsWith("Hội trường", StringComparison.OrdinalIgnoreCase))
            {
                return room;
            }
            return $"Phòng {room}";
        }

        private static AdminAiSectionItem CreateSectionItem(Section s)
        {
            double rate = s.MaxCapacity > 0 ? Math.Round((double)s.CurrentEnrollment / s.MaxCapacity * 100, 1) : 0;
            string badge = rate >= 100
                ? "badge bg-danger"
                : (rate >= 80 ? "badge bg-warning text-dark" : (rate < 50 ? "badge bg-secondary" : "badge bg-primary"));

            string note = rate >= 100
                ? "ĐÃ HẾT CHỖ (100%)"
                : (rate >= 80
                    ? $"Sắp đầy ({rate}%) - Còn trống {s.MaxCapacity - s.CurrentEnrollment} chỗ"
                    : (rate < 50
                        ? $"Sĩ số thấp ({s.CurrentEnrollment}/{s.MaxCapacity} SV - {rate}%)"
                        : $"Đang mở ghi danh ({s.CurrentEnrollment}/{s.MaxCapacity} SV - {rate}%)"));

            return new AdminAiSectionItem
            {
                SectionId = s.SectionId,
                SectionCode = s.SectionCode,
                CourseName = s.Course?.CourseName ?? "Chưa rõ",
                Instructor = s.Instructor,
                CurrentEnrollment = s.CurrentEnrollment,
                MaxCapacity = s.MaxCapacity,
                StatusBadge = badge,
                Note = $"{FormatSchedules(s)} &bull; {note}"
            };
        }

        private static void PopulateRelatedSectionsForAi(
            AdminAiQueryResult result,
            string query,
            List<Section> allSections,
            List<Course> allCourses)
        {
            if (result.Sections.Any()) return;

            string qNorm = RemoveDiacritics(query);
            var matchedCourse = FindMatchingCourse(qNorm, allCourses);
            if (matchedCourse != null)
            {
                var secs = allSections.Where(s => s.CourseId == matchedCourse.CourseId).ToList();
                foreach (var s in secs)
                {
                    result.Sections.Add(CreateSectionItem(s));
                }
                return;
            }

            var matchedSection = FindMatchingSection(qNorm, allSections);
            if (matchedSection != null)
            {
                result.Sections.Add(CreateSectionItem(matchedSection));
                return;
            }

            var matchedInst = FindMatchingInstructor(qNorm, allSections);
            if (matchedInst != null)
            {
                foreach (var s in matchedInst.Value.Sections)
                {
                    result.Sections.Add(CreateSectionItem(s));
                }
            }
        }

        private static string RemoveDiacritics(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var normalizedString = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();

            foreach (var c in normalizedString)
            {
                var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
                if (unicodeCategory != UnicodeCategory.NonSpacingMark)
                {
                    sb.Append(c);
                }
            }

            return sb.ToString()
                .Normalize(NormalizationForm.FormC)
                .ToLowerInvariant()
                .Replace('đ', 'd')
                .Replace('Đ', 'd')
                .Trim();
        }

        private static string ExtractJson(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                return text.Substring(start, end - start + 1);
            }
            return text;
        }
    }
}
