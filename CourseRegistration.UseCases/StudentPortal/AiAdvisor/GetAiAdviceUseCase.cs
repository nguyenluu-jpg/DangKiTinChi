using System.Text.Json;
using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;
using Microsoft.Extensions.AI;

namespace CourseRegistration.UseCases.StudentPortal.AiAdvisor
{
    /// <summary>
    /// Implementation của UseCase tư vấn đăng ký học phần thông minh
    /// Tuân thủ nghiêm ngặt các quy tắc:
    /// 1. Inject IChatClient của Microsoft.Extensions.AI
    /// 2. Xử lý lỗi ngoại lệ / hết hạn mức / Ollama chưa bật -> App vẫn chạy, hiển thị thông báo
    /// 3. AI CHỈ GỢI Ý: Không tự ý ghi vào CSDL, người dùng phải xác nhận
    /// </summary>
    public class GetAiAdviceUseCase : IGetAiAdviceUseCase
    {
        private readonly IChatClient? _chatClient;
        private readonly IStudentRepository _studentRepository;
        private readonly ISectionRepository _sectionRepository;
        private readonly IEnrollmentRepository _enrollmentRepository;
        private readonly ICourseRepository _courseRepository;

        public GetAiAdviceUseCase(
            IStudentRepository studentRepository,
            ISectionRepository sectionRepository,
            IEnrollmentRepository enrollmentRepository,
            ICourseRepository courseRepository,
            IChatClient? chatClient = null)
        {
            _studentRepository = studentRepository;
            _sectionRepository = sectionRepository;
            _enrollmentRepository = enrollmentRepository;
            _courseRepository = courseRepository;
            _chatClient = chatClient;
        }

        public async Task<AiCourseAdvice> ExecuteAsync(int studentId, string semester, string? studentGoalPrompt = null)
        {
            var advice = new AiCourseAdvice
            {
                StudentId = studentId,
                Semester = semester
            };

            // 1. Lấy thông tin sinh viên
            var student = await _studentRepository.GetStudentByIdAsync(studentId);
            if (student == null)
            {
                advice.WarningMessage = "Không tìm thấy thông tin sinh viên trong hệ thống.";
                advice.IsAiGenerated = false;
                return advice;
            }

            advice.StudentCode = student.StudentCode;
            advice.StudentName = student.FullName;

            // 2. Lấy môn đã hoàn thành & kiểm tra tiên quyết
            var completedCourses = (await _studentRepository.GetCompletedCoursesByStudentIdAsync(studentId)).ToList();
            var passedCourseIds = completedCourses.Where(c => c.Grade >= 5.0).Select(c => c.CourseId).ToHashSet();

            // 3. Lấy môn đã đăng ký trong kỳ hiện tại
            var registeredSections = (await _enrollmentRepository.GetRegisteredSectionsByStudentAsync(studentId, semester)).ToList();
            var registeredCourseIds = registeredSections.Select(r => r.CourseId).ToHashSet();
            int currentCredits = registeredSections.Sum(r => r.Course?.Credits ?? 0);
            int remainingCreditsAllowed = Math.Max(0, student.MaxCreditsPerSemester - currentCredits);

            // 4. Lấy danh sách lớp mở trong kỳ
            var allSections = (await _sectionRepository.GetSectionsBySemesterAsync(semester)).ToList();
            var allPrerequisites = (await _courseRepository.GetAllPrerequisitesAsync()).ToList();

            // 5. Lọc danh sách lớp học phần hợp lệ (đủ điều kiện tiên quyết, chưa học qua, chưa đăng ký kỳ này, còn chỗ)
            var candidateSections = new List<Section>();
            foreach (var sec in allSections)
            {
                if (sec.Course == null) continue;
                if (passedCourseIds.Contains(sec.CourseId)) continue; // Đã qua môn rồi thì không học lại
                if (registeredCourseIds.Contains(sec.CourseId)) continue; // Đã đăng ký trong kỳ rồi

                // Kiểm tra tiên quyết
                var prereqCourseIds = allPrerequisites.Where(p => p.CourseId == sec.CourseId).Select(p => p.PrerequisiteCourseId).ToList();
                bool prereqSatisfied = prereqCourseIds.All(pid => passedCourseIds.Contains(pid));
                if (!prereqSatisfied) continue;

                // Kiểm tra còn chỗ
                if (sec.CurrentEnrollment >= sec.MaxCapacity) continue;

                candidateSections.Add(sec);
            }

            // 6. Chuẩn bị gọi AI (hoặc fallback thông minh)
            bool aiSuccess = false;

            if (_chatClient != null && candidateSections.Any())
            {
                try
                {
                    // Xây dựng System Prompt & Context chi tiết
                    var completedSummary = completedCourses.Any()
                        ? string.Join(", ", completedCourses.Select(c => $"{c.Course?.CourseCode} ({c.Course?.CourseName} - Điểm: {c.Grade})"))
                        : "Chưa hoàn thành môn nào";

                    var candidateSummary = string.Join("\n", candidateSections.Select(s =>
                        $"- Lớp: {s.SectionCode} | Môn: {s.Course?.CourseCode} - {s.Course?.CourseName} ({s.Course?.Credits} TC) | GV: {s.Instructor} | Còn trống: {s.MaxCapacity - s.CurrentEnrollment} chỗ | Lịch: {FormatSchedule(s)}"));

                    var prompt = $@"Bạn là Cố vấn Học tập AI thông minh của Hệ Thống Đăng Ký Học Phần Đại học.
Hãy phân tích hồ sơ sinh viên sau và đưa ra gợi ý đăng ký học phần tối ưu nhất cho học kỳ {semester}:

[THÔNG TIN SINH VIÊN]
- Mã SV: {student.StudentCode}
- Họ tên: {student.FullName}
- Ngành: {student.Major} ({student.AcademicYear})
- Số tín chỉ tối đa được phép đăng ký thêm: {remainingCreditsAllowed} tín chỉ
- Các môn đã hoàn thành trong quá khứ: {completedSummary}
- Nhu cầu / Mục tiêu của sinh viên: {(string.IsNullOrWhiteSpace(studentGoalPrompt) ? "Tối ưu hóa tiến độ ra trường đúng hạn, cân bằng lịch học" : studentGoalPrompt)}

[DANH SÁCH LỚP HỌC PHẦN HỢP LỆ ĐỦ ĐIỀU KIỆN TIÊN QUYẾT TRONG KỲ]
{candidateSummary}

[YÊU CẦU ĐẦU RA]
Bạn PHẢI trả về ĐÚNG định dạng JSON theo cấu trúc sau (không kèm markdown ngoài khối ```json):
{{
  ""adviceMessage"": ""Lời khuyên chi tiết, ân cần và phân tích chiến lược học tập cho sinh viên bằng tiếng Việt"",
  ""recommendedSectionCodes"": [""MÃ_LỚP_1"", ""MÃ_LỚP_2""]
}}
Lưu ý:
1. Tổng số tín chỉ của các lớp được chọn KHÔNG ĐƯỢC VƯỢT QUÁ {remainingCreditsAllowed} TC.
2. Không chọn 2 lớp trùng lịch học.
3. Ưu tiên các môn cơ sở ngành cốt lõi phù hợp với {student.Major}.";

                    var messages = new List<ChatMessage>
                    {
                        new ChatMessage(ChatRole.System, "Bạn là Cố vấn học tập AI chuyên nghiệp. Trả về định dạng JSON hợp lệ."),
                        new ChatMessage(ChatRole.User, prompt)
                    };

                    var options = new ChatOptions
                    {
                        Temperature = 0.3f
                    };

                    var response = await _chatClient.GetResponseAsync(messages, options);
                    var responseText = response.Text ?? string.Empty;

                    // Parse JSON từ phản hồi
                    var jsonContent = ExtractJson(responseText);
                    if (!string.IsNullOrEmpty(jsonContent))
                    {
                        using var doc = JsonDocument.Parse(jsonContent);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("adviceMessage", out var msgElement))
                        {
                            advice.AiMessage = msgElement.GetString() ?? string.Empty;
                        }

                        if (root.TryGetProperty("recommendedSectionCodes", out var codesElement) && codesElement.ValueKind == JsonValueKind.Array)
                        {
                            var recommendedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var item in codesElement.EnumerateArray())
                            {
                                var code = item.GetString();
                                if (!string.IsNullOrEmpty(code)) recommendedCodes.Add(code);
                            }

                            int accumulatedCredits = 0;
                            foreach (var sec in candidateSections.Where(s => recommendedCodes.Contains(s.SectionCode)))
                            {
                                int credits = sec.Course?.Credits ?? 0;
                                if (accumulatedCredits + credits <= remainingCreditsAllowed)
                                {
                                    advice.RecommendedSections.Add(new AiRecommendedSection
                                    {
                                        SectionId = sec.SectionId,
                                        SectionCode = sec.SectionCode,
                                        CourseId = sec.CourseId,
                                        CourseCode = sec.Course?.CourseCode ?? string.Empty,
                                        CourseName = sec.Course?.CourseName ?? string.Empty,
                                        Credits = credits,
                                        Instructor = sec.Instructor,
                                        Room = sec.Room,
                                        ScheduleSummary = FormatSchedule(sec),
                                        RecommendationReason = $"AI đề xuất phù hợp với tiến độ chuyên ngành {student.Major} và đã đạt chuẩn tiên quyết.",
                                        IsSelected = true
                                    });
                                    accumulatedCredits += credits;
                                }
                            }
                        }

                        if (advice.RecommendedSections.Any())
                        {
                            advice.IsAiGenerated = true;
                            advice.ModelUsed = "Mô hình Trí tuệ Nhân tạo AI (IChatClient)";
                            aiSuccess = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Xử lý khi API lỗi, mạng ngắt, hoặc hết hạn mức (Quota Exceeded):
                    // Ứng dụng VẪN CHẠY BÌNH THƯỜNG, không bị sập hay gián đoạn.
                    advice.WarningMessage = $"Không thể kết nối đến API AI ({ex.Message.Split('\n')[0]}). Hệ thống đã tự động kích hoạt Cố vấn Quy tắc Học thuật Dự phòng để đảm bảo tính năng không bị gián đoạn.";
                    aiSuccess = false;
                }
            }

            // 7. FALLBACK QUY TẮC THÔNG MINH (KHI AI LỖI / CHƯA BẬT OLLAMA / CHƯA CÓ API KEY)
            if (!aiSuccess)
            {
                advice.IsAiGenerated = false;
                advice.ModelUsed = "Hệ thống Cố vấn Quy tắc Thông minh Dự phòng (Smart Rule-Based Advisor)";
                if (string.IsNullOrEmpty(advice.WarningMessage))
                {
                    advice.WarningMessage = "Đang chạy chế độ Cố vấn Quy tắc Tự động (Để kết nối LLM, bạn có thể khởi động Ollama local 'ollama run llama3' hoặc cấu hình OpenAI API Key trong User Secrets).";
                }

                // Chọn lọc các lớp tối ưu: Ưu tiên các môn mở rộng và cân bằng tín chỉ
                int accumulatedCredits = 0;
                var selectedSections = new List<Section>();

                foreach (var sec in candidateSections)
                {
                    int credits = sec.Course?.Credits ?? 0;
                    if (accumulatedCredits + credits > remainingCreditsAllowed) continue;

                    // Kiểm tra trùng lịch với các môn đã chọn
                    bool conflict = false;
                    foreach (var already in selectedSections)
                    {
                        if (HasScheduleConflict(sec, already))
                        {
                            conflict = true;
                            break;
                        }
                    }
                    if (conflict) continue;

                    selectedSections.Add(sec);
                    accumulatedCredits += credits;

                    advice.RecommendedSections.Add(new AiRecommendedSection
                    {
                        SectionId = sec.SectionId,
                        SectionCode = sec.SectionCode,
                        CourseId = sec.CourseId,
                        CourseCode = sec.Course?.CourseCode ?? string.Empty,
                        CourseName = sec.Course?.CourseName ?? string.Empty,
                        Credits = credits,
                        Instructor = sec.Instructor,
                        Room = sec.Room,
                        ScheduleSummary = FormatSchedule(sec),
                        RecommendationReason = $"Đạt chuẩn tiên quyết, lớp còn trống {sec.MaxCapacity - sec.CurrentEnrollment} chỗ và không xung đột lịch học.",
                        IsSelected = true
                    });

                    // Đề xuất khoảng 3-4 môn (9 - 14 tín chỉ) là tối ưu cho kỳ
                    if (accumulatedCredits >= 12) break;
                }

                advice.AiMessage = $"Chào bạn {student.FullName}! Dựa trên tiến độ học tập K21 ngành {student.Major}, hệ thống gợi ý bạn nên đăng ký {advice.RecommendedSections.Count} học phần ({advice.TotalCredits} tín chỉ) trong kỳ {semester}. Các môn này đều đã được kiểm tra nghiêm ngặt về điều kiện tiên quyết, sĩ số còn chỗ và tuyệt đối không trùng lịch học.";
            }

            return advice;
        }

        private static string FormatSchedule(Section section)
        {
            if (section.Schedules == null || !section.Schedules.Any()) return "Chưa xếp lịch";
            return string.Join("; ", section.Schedules.Select(s => $"Thứ {s.DayOfWeek} (Tiết {s.StartPeriod}-{s.EndPeriod}, {s.Room})"));
        }

        private static bool HasScheduleConflict(Section s1, Section s2)
        {
            if (s1.Schedules == null || s2.Schedules == null) return false;
            foreach (var sch1 in s1.Schedules)
            {
                foreach (var sch2 in s2.Schedules)
                {
                    if (sch1.DayOfWeek == sch2.DayOfWeek)
                    {
                        if (sch1.StartPeriod <= sch2.EndPeriod && sch2.StartPeriod <= sch1.EndPeriod)
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
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
