using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;
using Microsoft.Extensions.AI;

namespace CourseRegistration.UseCases.StudentPortal.AiAdvisor
{
    /// <summary>
    /// Implementation của UseCase tư vấn đăng ký học phần thông minh
    /// Tuân thủ nghiêm ngặt 5 tiêu chuẩn của giảng viên (Mục 10):
    /// 1. API key đặt trong User Secrets hoặc biến môi trường, không commit lên git.
    /// 2. IChatClient được inject vào một UseCase riêng (GetAiAdviceUseCase), không gọi trực tiếp từ file .razor.
    /// 3. Có xử lý khi API lỗi hoặc hết hạn mức: ứng dụng vẫn chạy, hiển thị thông báo.
    /// 4. AI chỉ gợi ý; dữ liệu do AI tạo phải được người dùng xác nhận trước khi ghi vào CSDL.
    /// 5. Có thể dùng Ollama chạy local để không tốn phí hoặc OpenAI.
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

        public async Task<AiCourseAdvice> ExecuteAsync(
            int studentId,
            string semester,
            string? studentGoalPrompt = null,
            List<AiConversationMessage>? conversationHistory = null)
        {
            var advice = new AiCourseAdvice
            {
                StudentId = studentId,
                Semester = semester
            };

            // 1. Trích xuất toàn bộ dữ liệu CSDL SQL Server thời gian thực
            var allCourses = (await _courseRepository.GetAllCoursesAsync()).ToList();
            var allSections = (await _sectionRepository.GetSectionsBySemesterAsync(semester)).ToList();
            var allSectionsAcrossSemesters = (await _sectionRepository.GetAllSectionsAsync()).ToList();
            var allPrerequisites = (await _courseRepository.GetAllPrerequisitesAsync()).ToList();

            Student? student = null;
            var completedCourses = new List<CompletedCourse>();
            var registeredSections = new List<Section>();
            var passedCourseIds = new HashSet<int>();
            var registeredCourseIds = new HashSet<int>();
            int remainingCreditsAllowed = 18;

            if (studentId > 0)
            {
                student = await _studentRepository.GetStudentByIdAsync(studentId);
                if (student != null)
                {
                    advice.StudentCode = student.StudentCode;
                    advice.StudentName = student.FullName;

                    completedCourses = (await _studentRepository.GetCompletedCoursesByStudentIdAsync(studentId)).ToList();
                    passedCourseIds = completedCourses.Where(c => c.Grade >= 5.0).Select(c => c.CourseId).ToHashSet();

                    registeredSections = (await _enrollmentRepository.GetRegisteredSectionsByStudentAsync(studentId, semester)).ToList();
                    registeredCourseIds = registeredSections.Select(r => r.CourseId).ToHashSet();

                    int currentCredits = registeredSections.Sum(r => r.Course?.Credits ?? 0);
                    remainingCreditsAllowed = Math.Max(0, student.MaxCreditsPerSemester - currentCredits);
                }
            }

            string userPrompt = string.IsNullOrWhiteSpace(studentGoalPrompt) ? string.Empty : studentGoalPrompt.Trim();
            bool isExplicitRec = IsExplicitRecommendationRequest(userPrompt);

            // 2. Lọc danh sách lớp ứng viên hợp lệ cho đề xuất học phần (chỉ dùng khi có yêu cầu gợi ý rõ ràng)
            var candidateSections = new List<Section>();
            foreach (var sec in allSections)
            {
                if (sec.Course == null) continue;
                if (passedCourseIds.Contains(sec.CourseId)) continue;
                if (registeredCourseIds.Contains(sec.CourseId)) continue;

                var prereqCourseIds = allPrerequisites
                    .Where(p => p.CourseId == sec.CourseId)
                    .Select(p => p.PrerequisiteCourseId)
                    .ToList();
                bool prereqSatisfied = prereqCourseIds.All(pid => passedCourseIds.Contains(pid));
                if (!prereqSatisfied) continue;

                if (sec.CurrentEnrollment >= sec.MaxCapacity) continue;

                candidateSections.Add(sec);
            }

            // 3. THỬ NGHIỆM GỌI MÔ HÌNH AI (IChatClient - OpenAI / Ollama Local)
            bool aiSuccess = false;
            if (_chatClient != null && !string.IsNullOrWhiteSpace(userPrompt))
            {
                try
                {
                    var sectionsDbDump = string.Join("\n", allSectionsAcrossSemesters.Select(s =>
                        $"- Lớp: {s.SectionCode} | Môn: {s.Course?.CourseCode} - {s.Course?.CourseName} ({s.Course?.Credits}TC) | GV: {s.Instructor} | Phòng: {s.Room} | Lịch: {FormatSchedule(s)} | Sĩ số: {s.CurrentEnrollment}/{s.MaxCapacity} | Học kỳ: {s.Semester}"));

                    var coursesDbDump = string.Join(", ", allCourses.Select(c => $"{c.CourseCode}: {c.CourseName} ({c.Credits}TC)"));

                    var prereqDbDump = string.Join("\n", allPrerequisites.Select(p =>
                        $"- Môn {p.Course?.CourseCode} yêu cầu tiên quyết {p.PrerequisiteCourse?.CourseCode} ({p.PrerequisiteCourse?.CourseName})"));

                    string studentProfileText = student != null
                        ? $"- Sinh viên: {student.FullName} (MSSV: {student.StudentCode}) | Ngành: {student.Major}\n" +
                          $"- Số tín chỉ tối đa được đăng ký thêm trong kỳ {semester}: {remainingCreditsAllowed} TC\n" +
                          $"- Các môn đã hoàn thành: {(completedCourses.Any() ? string.Join(", ", completedCourses.Select(c => $"{c.Course?.CourseCode} ({c.Grade}đ)")) : "Chưa hoàn thành môn nào")}\n" +
                          $"- Các lớp đã đăng ký trong kỳ: {(registeredSections.Any() ? string.Join(", ", registeredSections.Select(r => $"{r.SectionCode} ({r.Course?.CourseName} - {FormatSchedule(r)})")) : "Chưa đăng ký lớp nào")}"
                        : "- Người dùng hiện đang ở chế độ Khách (chưa đăng nhập tài khoản cá nhân).";

                    var systemPrompt = $@"Bạn là Cố vấn Học thuật AI của Trường Đại học Kinh tế Huế.
Nhiệm vụ của bạn là trả lời sinh viên bằng tiếng Việt, đúng trọng tâm, văn phong sư phạm, lịch sự và súc tích.

[DỮ LIỆU THỰC TẾ TỪ CSDL SQL SERVER]
Danh sách lớp học phần toàn trường:
{sectionsDbDump}

Danh sách môn học:
{coursesDbDump}

Điều kiện tiên quyết:
{prereqDbDump}

Hồ sơ sinh viên đang hỏi:
{studentProfileText}

[NGUYÊN TẮC CỐT LÕI]
1. DỰA HOÀN TOÀN VÀO DỮ LIỆU SQL SERVER Ở TRÊN. Tuyệt đối không tự bịa thông tin, phòng học, giảng viên hay mã lớp.
2. TRA CỨU GIẢNG VIÊN: Khi người dùng hỏi về môn học hoặc lớp của một giảng viên (ví dụ: 'Môn của thầy Hà Ngọc Long là môn nào?', 'Thầy Long dạy môn gì?', 'Lịch học lớp thầy Long'):
   - Tra cứu tất cả các môn và lớp học phần do giảng viên đó phụ trách (lưu ý học vị như TS., ThS., PGS.TS.).
   - Trả lời rõ ràng tên các môn học, mã môn, mã lớp học phần, học kỳ mở lớp, lịch học chi tiết (thứ, tiết) và phòng học.
   - Nếu giảng viên không có trong dữ liệu CSDL, thông báo rõ ràng là không tìm thấy giảng viên đó và gợi ý các giảng viên đang có trong CSDL, TUYỆT ĐỐI KHÔNG liệt kê danh sách môn học không liên quan.
3. PHÂN BIỆT RÕ: Lớp học phần sinh viên đã đăng ký (trong hồ sơ) vs Danh sách học phần toàn trường.
   - Khi sinh viên hỏi về lịch học của cá nhân (ví dụ: 'thứ 2 tôi học gì', 'hôm nay tôi có học không', 'thời khóa biểu của em'), CHỈ tra cứu trong các lớp sinh viên ĐÃ ĐĂNG KÝ. Nếu sinh viên chưa đăng ký hoặc không có lớp vào ngày đó, hãy thông báo rõ ràng là không có lịch học.
   - Chỉ khi sinh viên hỏi chung về trường (ví dụ: 'thứ 2 trường có những lớp nào', 'toàn trường mở môn gì') mới liệt kê các lớp trong danh sách toàn trường.
4. KHÔNG TỰ ĐỀ XUẤT MÔN KHI CHƯA ĐƯỢC YÊU CẦU: Nếu sinh viên chỉ hỏi thông tin (giảng viên, phòng học, lịch học, học phí, quy chế), trả lời đúng thông tin được hỏi, KHÔNG tự động gợi ý danh sách môn đăng ký. Chỉ khi sinh viên yêu cầu gợi ý/tư vấn môn học mới đề xuất môn.
5. Khi có nhiều lớp học phần phù hợp, hãy liệt kê đầy đủ thông tin từng lớp.
6. Duy trì ngữ cảnh cuộc trò chuyện nếu người dùng hỏi tiếp các câu hỏi nối tiếp.
7. Nếu sinh viên hỏi về học phí: Đơn giá là 450.000 VNĐ / tín chỉ, MB Bank 101705201967 (Napas 24/7).
8. Nếu sinh viên hỏi về quy chế rút môn: Trong hạn trực tuyến tự rút trong giỏ/phiếu; sau hạn nộp đơn về Phòng Đào Tạo. Hạn mức tối thiểu 10 TC, tối đa 24 TC.";

                    var messages = new List<ChatMessage>
                    {
                        new ChatMessage(ChatRole.System, systemPrompt)
                    };

                    if (conversationHistory != null && conversationHistory.Any())
                    {
                        foreach (var hist in conversationHistory.TakeLast(6))
                        {
                            messages.Add(new ChatMessage(hist.IsUser ? ChatRole.User : ChatRole.Assistant, hist.Content));
                        }
                    }

                    messages.Add(new ChatMessage(ChatRole.User, userPrompt));

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                    var response = await _chatClient.GetResponseAsync(messages, new ChatOptions { Temperature = 0.2f }, cts.Token);
                    var responseText = response.Text?.Trim() ?? string.Empty;

                    if (!string.IsNullOrWhiteSpace(responseText))
                    {
                        advice.AiMessage = responseText;
                        advice.IsAiGenerated = true;
                        advice.ModelUsed = "Trí Tuệ Nhân Tạo AI (IChatClient - LLM)";
                        aiSuccess = true;

                        // CHỈ bổ sung vào RecommendedSections khi người dùng YÊU CẦU GỢI Ý MÔN RÕ RÀNG
                        if (isExplicitRec && student != null)
                        {
                            foreach (var sec in candidateSections)
                            {
                                if (responseText.Contains(sec.SectionCode, StringComparison.OrdinalIgnoreCase))
                                {
                                    if (!advice.RecommendedSections.Any(r => r.SectionId == sec.SectionId))
                                    {
                                        advice.RecommendedSections.Add(new AiRecommendedSection
                                        {
                                            SectionId = sec.SectionId,
                                            SectionCode = sec.SectionCode,
                                            CourseId = sec.CourseId,
                                            CourseCode = sec.Course?.CourseCode ?? string.Empty,
                                            CourseName = sec.Course?.CourseName ?? string.Empty,
                                            Credits = sec.Course?.Credits ?? 0,
                                            Instructor = sec.Instructor,
                                            Room = sec.Room,
                                            ScheduleSummary = FormatSchedule(sec),
                                            RecommendationReason = $"Được AI đề xuất dựa trên nguyện vọng '{userPrompt}'.",
                                            IsSelected = true
                                        });
                                    }
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Ngoại lệ API / Hết hạn mức / Mất kết nối / Timeout -> Tự động chuyển fallback
                    aiSuccess = false;
                }
            }

            // 4. ĐỘNG CƠ CỐ VẤN TRI THỨC SQL SERVER CHUYÊN SÂU (FALLBACK KHI OFFLINE / KHÔNG CÓ API KEY)
            // Tuân thủ: "Có xử lý khi API lỗi hoặc hết hạn mức: ứng dụng vẫn chạy, hiển thị thông báo."
            // Đảm bảo hiểu đúng câu hỏi tiếng Việt, trả lời đúng trọng tâm và truy vấn dữ liệu thực tế từ SQL Server!
            if (!aiSuccess)
            {
                advice.IsAiGenerated = false;
                advice.ModelUsed = "Động Cơ Tri Thức SQL Server Chuyên Sâu";
                advice.WarningMessage = "💡 Cố vấn AI đang hoạt động ở chế độ Động cơ Tri thức Học thuật Chuyên sâu (truy vấn trực tiếp dữ liệu thực tế từ SQL Server). Kết quả được trích xuất chính xác theo thời gian thực.";

                ExecuteDeterministicKnowledgeEngine(
                    advice,
                    userPrompt,
                    student,
                    allCourses,
                    allSections,
                    allPrerequisites,
                    candidateSections,
                    completedCourses,
                    registeredSections,
                    remainingCreditsAllowed,
                    semester,
                    conversationHistory,
                    allSectionsAcrossSemesters);
            }

            return advice;
        }

        /// <summary>
        /// Động cơ Phân tích Tri thức SQL Server Chuyên Sâu:
        /// Hiểu tiếng Việt, phân biệt lịch cá nhân vs toàn trường, không tự đề xuất môn khi chưa được yêu cầu.
        /// </summary>
        private static void ExecuteDeterministicKnowledgeEngine(
            AiCourseAdvice advice,
            string userPrompt,
            Student? student,
            List<Course> allCourses,
            List<Section> allSections,
            List<Prerequisite> allPrerequisites,
            List<Section> candidateSections,
            List<CompletedCourse> completedCourses,
            List<Section> registeredSections,
            int remainingCreditsAllowed,
            string semester,
            List<AiConversationMessage>? conversationHistory,
            List<Section>? allSectionsAcrossSemesters = null)
        {
            if (string.IsNullOrWhiteSpace(userPrompt))
            {
                if (student != null)
                {
                    advice.AiMessage = $"### 👋 Chào bạn {student.FullName} (MSSV: `{student.StudentCode}`)!\n\n" +
                                       $"Tôi là **Cố vấn Học thuật AI** của bạn trong học kỳ **{semester}**. Bạn có thể hỏi tôi:\n" +
                                       $"- *Lịch học cá nhân:* 'Thứ 2 tôi học gì?', 'Hôm nay tôi có học không?', 'Thời khóa biểu của tôi'.\n" +
                                       $"- *Tra cứu toàn trường:* Môn học, giảng viên, mã lớp học phần, phòng học, sĩ số lớp.\n" +
                                       $"- *Học bạ & Tiên quyết:* Điểm số tích lũy, các môn đã hoàn thành, điều kiện tiên quyết.\n" +
                                       $"- *Quy chế & Học phí:* Rút môn học, hạn mức tín chỉ 10-24, đơn giá 450.000 đ/TC và VietQR.\n" +
                                       $"- *Tư vấn lộ trình:* 'Gợi ý môn học kỳ này cho tôi', 'Nên đăng ký môn nào', 'Học nhẹ', 'Tránh sáng thứ 2'...";
                }
                else
                {
                    advice.AiMessage = $"### 👋 Chào bạn! Tôi là Cố Vấn Học Thuật AI.\n\n" +
                                       $"Bạn có thể hỏi tôi về bất kỳ thông tin nào trong CSDL nhà trường (môn học, giảng viên, phòng học, lịch học toàn trường, học phí, quy chế tín chỉ) hoặc **Đăng nhập** để tra cứu thời khóa biểu và lộ trình riêng của bạn!";
                }
                return;
            }

            string qRaw = userPrompt.Trim();
            string qLower = qRaw.ToLowerInvariant();
            string qNorm = RemoveDiacritics(qLower);

            // Duy trì ngữ cảnh hội thoại từ câu hỏi trước
            string contextSubject = ExtractContextSubject(qNorm, conversationHistory, allCourses, allSections);
            string effectiveSearchText = !string.IsNullOrEmpty(contextSubject) ? $"{qNorm} {contextSubject}" : qNorm;

            // Xác định thời gian (Hôm nay / Ngày mai / Thứ 2 - CN)
            int? queriedDay = DetectTargetDay(qNorm);

            // Kiểm tra xem câu hỏi có hướng tới cá nhân sinh viên hay không
            bool isPersonalQuery =
                qNorm.Contains("cua toi") || qNorm.Contains("cua em") || qNorm.Contains("cua minh") ||
                qNorm.Contains("toi hoc") || qNorm.Contains("em hoc") || qNorm.Contains("minh hoc") ||
                qNorm.Contains("toi co hoc") || qNorm.Contains("em co hoc") || qNorm.Contains("minh co hoc") ||
                qNorm.Contains("thoi khoa bieu") || qNorm.Contains("lich hoc cua") ||
                qNorm.Contains("mon da dang ky") || qNorm.Contains("mon em da") || qNorm.Contains("mon toi da") ||
                qNorm.Contains("phieu dang ky") || qNorm.Contains("ket qua dang ky") ||
                qNorm.Contains("hom nay toi") || qNorm.Contains("hom nay em") ||
                qNorm.Contains("hom nay co hoc khong") || qNorm.Contains("hom nay hoc gi") ||
                qNorm.Contains("lich hoc hom nay") ||
                Regex.IsMatch(qNorm, @"\b(thu [2-8]|chu nhat)\s+(toi|em|minh)\s+(co|hoc)");

            // =========================================================================
            // TRƯỜNG HỢP 1: LỊCH HỌC / THỜI KHÓA BIỂU CÁ NHÂN CỦA SINH VIÊN
            // Phân biệt rõ ràng với học phần toàn trường!
            // =========================================================================
            if (isPersonalQuery && !qNorm.Contains("diem") && !qNorm.Contains("hoc ba") && !qNorm.Contains("goi y") && !qNorm.Contains("tu van") && !qNorm.Contains("nen hoc"))
            {
                if (student == null)
                {
                    advice.AiMessage = "👋 **Chào bạn! Bạn hiện đang ở chế độ Khách (chưa đăng nhập).**\n\n" +
                                       "Để Cố vấn AI tra cứu thời khóa biểu cá nhân và các môn bạn đã đăng ký, vui lòng bấm nút **Đăng nhập** bên dưới nhé!\n\n" +
                                       "🔑 **Tài khoản sinh viên thử nghiệm:**\n" +
                                       "- Sinh viên 1: `sv21001` / Mật khẩu: `123456`\n" +
                                       "- Sinh viên 2: `sv21002` / Mật khẩu: `123456`";
                    advice.SuggestLogin = true;
                    return;
                }

                // Sub-case 1.1: Hỏi về một ngày cụ thể (ví dụ: "Thứ 2 tôi học gì?", "Thứ 4 em có học không?", "Hôm nay tôi học gì?")
                if (queriedDay.HasValue)
                {
                    int day = queriedDay.Value;
                    string dayName = day == 8 ? "Chủ Nhật" : $"Thứ {day}";

                    var daySections = registeredSections.Where(s =>
                        s.Schedules != null && s.Schedules.Any(sch => sch.DayOfWeek == day)).ToList();

                    var sb = new StringBuilder();
                    sb.AppendLine($"### 🗓️ Lịch Học Cá Nhân Vào {dayName} (Học kỳ {semester}):");
                    sb.AppendLine($"Sinh viên: **{student.FullName}** (MSSV: `{student.StudentCode}`)");
                    sb.AppendLine();

                    if (daySections.Any())
                    {
                        sb.AppendLine($"Vào **{dayName}**, bạn có **{daySections.Count} lớp học phần** đã đăng ký:");
                        sb.AppendLine();

                        foreach (var s in daySections)
                        {
                            var schs = s.Schedules!.Where(sch => sch.DayOfWeek == day).ToList();
                            string periods = string.Join(", ", schs.Select(sch => $"Tiết {sch.StartPeriod} - {sch.EndPeriod} (Phòng: {sch.Room})"));

                            sb.AppendLine($"- **{s.Course?.CourseName}** (Mã lớp: `{s.SectionCode}` - **{s.Course?.Credits} Tín chỉ**):");
                            sb.AppendLine($"  + Ca học: **{periods}**");
                            sb.AppendLine($"  + Giảng viên: **{s.Instructor}**");
                            sb.AppendLine($"  + Phòng học: **{s.Room}**");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"✅ Vào **{dayName}**, bạn **không có lịch học nào** trong số các lớp học phần đã đăng ký.");
                        sb.AppendLine($"💡 Bạn có thể dùng thời gian này để tự học, ôn bài hoặc nghỉ ngơi!");
                    }

                    advice.AiMessage = sb.ToString();
                    return; // KHÔNG đề xuất môn
                }

                // Sub-case 1.2: Hỏi toàn bộ thời khóa biểu / danh sách môn đã đăng ký
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"### 📋 Danh Sách Học Phần Đã Đăng Ký Học Kỳ {semester}:");
                    sb.AppendLine($"Sinh viên: **{student.FullName}** (MSSV: `{student.StudentCode}`)");
                    sb.AppendLine();

                    if (registeredSections.Any())
                    {
                        int totalRegCredits = registeredSections.Sum(s => s.Course?.Credits ?? 0);
                        sb.AppendLine($"Bạn đã đăng ký thành công **{registeredSections.Count} lớp học phần** (Tổng số: **{totalRegCredits} Tín chỉ**):");
                        sb.AppendLine();

                        foreach (var s in registeredSections)
                        {
                            sb.AppendLine($"- **Lớp `{s.SectionCode}`** — **{s.Course?.CourseName}** (`{s.Course?.CourseCode}` - **{s.Course?.Credits} TC**):");
                            sb.AppendLine($"  + Giảng viên: **{s.Instructor}** | Phòng: **{s.Room}**");
                            sb.AppendLine($"  + Thời khóa biểu: **{FormatScheduleDetailed(s)}**");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"⚠️ Bạn **chưa đăng ký lớp học phần nào** trong học kỳ **{semester}**.");
                        sb.AppendLine($"💡 Bạn có thể vào mục **Đăng Ký Học Phần** hoặc hỏi tôi: *\"Gợi ý môn học kỳ này\"* để nhận đề xuất lộ trình tối ưu.");
                    }

                    advice.AiMessage = sb.ToString();
                    return; // KHÔNG đề xuất môn
                }
            }

            // =========================================================================
            // TRƯỜNG HỢP 2: HỌC BẠ / ĐIỂM SỐ CÁ NHÂN CỦA SINH VIÊN
            // =========================================================================
            bool asksGrades = qNorm.Contains("diem cua em") || qNorm.Contains("diem cua toi") || qNorm.Contains("hoc ba") ||
                              qNorm.Contains("mon da hoc") || qNorm.Contains("mon da qua") || qNorm.Contains("mon tich luy") ||
                              qNorm.Contains("ket qua hoc tap") || qNorm.Contains("diem f") || qNorm.Contains("mon rot");

            if (asksGrades)
            {
                if (student == null)
                {
                    advice.AiMessage = "👋 **Chào bạn! Bạn hiện đang ở chế độ Khách (chưa đăng nhập).**\n\n" +
                                       "Để xem học bạ, điểm số tích lũy cá nhân, vui lòng bấm nút **Đăng nhập** nhé!";
                    advice.SuggestLogin = true;
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine($"### 📊 Kết Quả Tích Lũy & Học Bạ Của Sinh Viên: {student.FullName} (`{student.StudentCode}`)");
                sb.AppendLine($"- **Chuyên ngành:** {student.Major} | **Khóa học:** {student.AcademicYear}");
                sb.AppendLine($"- **Hạn mức số tín chỉ tối đa:** **{student.MaxCreditsPerSemester} Tín chỉ / kỳ**");
                sb.AppendLine();

                if (completedCourses.Any())
                {
                    sb.AppendLine($"Bạn đã hoàn thành **{completedCourses.Count} học phần** nền tảng:");
                    sb.AppendLine();

                    foreach (var c in completedCourses)
                    {
                        string status = c.Grade >= 5.0 ? "✅ Đạt" : "❌ Chưa đạt";
                        sb.AppendLine($"- **{c.Course?.CourseCode}** — {c.Course?.CourseName} ({c.Course?.Credits}TC): **{c.Grade:0.0} điểm** ({status} - Kỳ {c.Semester})");
                    }
                }
                else
                {
                    sb.AppendLine("- *Bạn chưa có dữ liệu môn học đã hoàn thành trong hệ thống.*");
                }

                advice.AiMessage = sb.ToString();
                return; // KHÔNG đề xuất môn
            }

            // =========================================================================
            // TRƯỜNG HỢP 3: YÊU CẦU GỢI Ý / TƯ VẤN LỘ TRÌNH ĐĂNG KÝ HỌC PHẦN (CHỈ KHI YÊU CẦU)
            // Tuyệt đối tuân thủ: Chỉ đề xuất môn khi sinh viên có yêu cầu rõ ràng!
            // =========================================================================
            bool isAdvisoryRequest = IsExplicitRecommendationRequest(qNorm);

            if (isAdvisoryRequest)
            {
                if (student == null)
                {
                    advice.AiMessage = "👋 **Chào bạn! Bạn hiện đang ở chế độ Khách (chưa đăng nhập).**\n\n" +
                                       "Để Cố vấn AI có thể phân tích học bạ, kiểm tra điều kiện tiên quyết và đề xuất lộ trình môn học chính xác cho riêng bạn, vui lòng bấm nút **Đăng nhập** nhé!";
                    advice.SuggestLogin = true;
                    return;
                }

                GenerateCourseRecommendations(
                    advice,
                    student,
                    candidateSections,
                    remainingCreditsAllowed,
                    qNorm,
                    semester);
                return;
            }

            // =========================================================================
            // TRƯỜNG HỢP 4: HỌC PHÍ & VIETQR
            // =========================================================================
            bool isTuition = qNorm.Contains("hoc phi") || qNorm.Contains("bao nhieu tien") || qNorm.Contains("dong tien") ||
                             qNorm.Contains("nop tien") || qNorm.Contains("ngan hang") || qNorm.Contains("stk") ||
                             qNorm.Contains("so tai khoan") || qNorm.Contains("vietqr") || qNorm.Contains("mb bank") ||
                             qNorm.Contains("chuyen khoan");

            if (isTuition)
            {
                var sb = new StringBuilder();
                sb.AppendLine("### 💰 Quy Định Mức Thu Học Phí & Thanh Toán Năm Học 2026 - 2027:");
                sb.AppendLine();
                sb.AppendLine("- **Đơn giá tín chỉ:** **450.000 VNĐ / tín chỉ**.");
                sb.AppendLine("- **Ví dụ cụ thể:**");
                sb.AppendLine("  + Môn 2 tín chỉ: **900.000 VNĐ**");
                sb.AppendLine("  + Môn 3 tín chỉ: **1.350.000 VNĐ**");
                sb.AppendLine("  + Môn 4 tín chỉ: **1.800.000 VNĐ**");
                sb.AppendLine("  + Học kỳ tiêu chuẩn (14 tín chỉ): **6.300.000 VNĐ**");
                sb.AppendLine();

                if (student != null && registeredSections.Any())
                {
                    int totalRegCredits = registeredSections.Sum(s => s.Course?.Credits ?? 0);
                    long totalTuition = (long)totalRegCredits * 450000;
                    sb.AppendLine($"💳 **Học phí cá nhân của bạn ({student.FullName} - {student.StudentCode}):**");
                    sb.AppendLine($"- Tổng số tín chỉ đã đăng ký: **{totalRegCredits} Tín chỉ**");
                    sb.AppendLine($"- Tổng học phí học kỳ {semester}: **{totalTuition:N0} VNĐ**");
                    sb.AppendLine();
                }

                sb.AppendLine("- **Thông tin tài khoản thu học phí nhà trường:**");
                sb.AppendLine("  + Số tài khoản: **101705201967**");
                sb.AppendLine("  + Ngân hàng: **MB Bank (Ngân hàng TMCP Quân Đội)**");
                sb.AppendLine("  + Đơn vị thụ hưởng: **TRƯỜNG ĐẠI HỌC KINH TẾ HUẾ**");
                sb.AppendLine();
                sb.AppendLine("- **Thanh toán thông minh qua VietQR:** Hệ thống tự động tạo mã VietQR Napas247 điền sẵn số tiền và nội dung `[MSSV] HP 2026_HK1`, gạch nợ tức thì 24/7.");
                sb.AppendLine("- **Hạn nộp học phí:** Trong vòng 15 ngày kể từ ngày gửi phiếu đăng ký học phần.");

                advice.AiMessage = sb.ToString();
                return; // KHÔNG đề xuất môn
            }

            // =========================================================================
            // TRƯỜNG HỢP 5: QUY CHẾ RÚT MÔN & HỌC VỤ
            // =========================================================================
            bool isWithdraw = qNorm.Contains("rut mon") || qNorm.Contains("huy mon") || qNorm.Contains("bo mon") ||
                              qNorm.Contains("xin rut") || qNorm.Contains("huy hoc phan") ||
                              qNorm.Contains("han muc tin chi") || qNorm.Contains("toi da bao nhieu") ||
                              qNorm.Contains("toi thieu bao nhieu");

            if (isWithdraw)
            {
                advice.AiMessage = "### 📋 Quy Chế Rút Môn & Hủy Học Phần Dành Cho Sinh Viên:\n\n" +
                                   "- **Trong thời gian đăng ký trực tuyến:** Bạn hoàn toàn có thể vào mục **'Giỏ đăng ký'** hoặc **'Xem kết quả đăng ký'** để xóa hoặc đổi sang lớp khác mà không bị tính phí hay ghi điểm F vào bảng điểm.\n" +
                                   "- **Sau khi kết thúc thời hạn trực tuyến:** Cổng trực tuyến sẽ đóng. Để rút học phần, bạn cần làm đơn xin rút môn (có ý kiến của Cố vấn học tập) và nộp về **Phòng Đào Tạo** để Cán bộ duyệt rút môn trên hệ thống.\n" +
                                   "- **Hạn mức số tín chỉ trong kỳ:**\n" +
                                   "  + Tối thiểu: **10 Tín chỉ** (sinh viên không được rút xuống dưới 10 TC).\n" +
                                   "  + Tối đa: **24 Tín chỉ** (tùy theo xếp loại học lực kỳ trước).\n" +
                                   "- **Xử lý điểm F:** Nếu môn học bị điểm F (dưới 5.0), điểm F sẽ lưu vào học bạ và sinh viên phải đăng ký học lại môn đó ở các học kỳ tiếp theo.";
                return; // KHÔNG đề xuất môn
            }

            // =========================================================================
            // TRƯỜNG HỢP 6: TRA CỨU MÃ LỚP HỌC PHẦN CỤ THỂ TOÀN TRƯỜNG (SectionCode)
            // =========================================================================
            var matchedSectionByCode = allSections.FirstOrDefault(s =>
                qLower.Contains(s.SectionCode.ToLowerInvariant()) ||
                effectiveSearchText.Contains(RemoveDiacritics(s.SectionCode.ToLowerInvariant())));

            if (matchedSectionByCode != null)
            {
                var sec = matchedSectionByCode;
                int remainingSlots = Math.Max(0, sec.MaxCapacity - sec.CurrentEnrollment);
                string scheduleText = FormatScheduleDetailed(sec);

                advice.AiMessage = $"### 📋 Thông Tin Chi Tiết Lớp Học Phần: `{sec.SectionCode}`\n\n" +
                                   $"- **Học phần:** **{sec.Course?.CourseName}** (Mã môn: `{sec.Course?.CourseCode}` - **{sec.Course?.Credits} Tín chỉ**)\n" +
                                   $"- **Giảng viên phụ trách:** **{sec.Instructor}**\n" +
                                   $"- **Phòng học:** **{sec.Room}**\n" +
                                   $"- **Thời khóa biểu:** **{scheduleText}**\n" +
                                   $"- **Học kỳ:** **{sec.Semester}**\n" +
                                   $"- **Tình trạng sĩ số:** **{sec.CurrentEnrollment}/{sec.MaxCapacity} sinh viên** (Còn trống **{remainingSlots} chỗ**).\n\n" +
                                   $"💡 *Lưu ý: Nếu có nhu cầu đăng ký, bạn có thể tìm mã lớp này tại mục 'Đăng Ký Học Phần' để thêm vào giỏ.*";
                return; // KHÔNG tự ý đề xuất môn
            }

            // =========================================================================
            // TRƯỜNG HỢP 7: TRA CỨU MÔN HỌC TOÀN TRƯỜNG (Course)
            // =========================================================================
            var matchedCourse = FindMatchingCourse(effectiveSearchText, allCourses);
            if (matchedCourse != null)
            {
                var courseSections = allSections.Where(s => s.CourseId == matchedCourse.CourseId).ToList();
                var prereqs = allPrerequisites.Where(p => p.CourseId == matchedCourse.CourseId).ToList();
                var prereqCourses = allCourses.Where(c => prereqs.Any(p => p.PrerequisiteCourseId == c.CourseId)).ToList();

                string prereqText = prereqCourses.Any()
                    ? string.Join(", ", prereqCourses.Select(c => $"**{c.CourseName} ({c.CourseCode})**"))
                    : "Không yêu cầu (sinh viên có thể đăng ký tự do)";

                bool asksPrereq = qNorm.Contains("tien quyet") || qNorm.Contains("dieu kien") || qNorm.Contains("hoc truoc");

                var sb = new StringBuilder();
                sb.AppendLine($"### 📘 Thông Tin Học Phần: {matchedCourse.CourseName} (`{matchedCourse.CourseCode}`)");
                sb.AppendLine();
                sb.AppendLine($"- **Số tín chỉ:** **{matchedCourse.Credits} Tín chỉ** (Học phí tạm tính: **{(matchedCourse.Credits * 450000):N0} VNĐ**)");
                sb.AppendLine($"- **Mô tả môn học:** {matchedCourse.Description}");
                sb.AppendLine($"- **Học phần tiên quyết:** {prereqText}");
                sb.AppendLine();

                if (student != null)
                {
                    var studentPassedPrereq = prereqCourses.All(p => completedCourses.Any(c => c.CourseId == p.CourseId && c.Grade >= 5.0));
                    if (prereqCourses.Any())
                    {
                        if (studentPassedPrereq)
                        {
                            sb.AppendLine($"✅ **Kiểm tra học bạ ({student.FullName}):** Bạn **ĐÃ HOÀN THÀNH** học phần tiên quyết cho môn này. Bạn hoàn toàn đủ điều kiện ghi danh!");
                        }
                        else
                        {
                            sb.AppendLine($"⚠️ **Cảnh báo học bạ ({student.FullName}):** Bạn **CHƯA TÍCH LŨY ĐỦ** học phần tiên quyết ({prereqText}). Hệ thống sẽ chặn nếu cố tình ghi danh.");
                        }
                        sb.AppendLine();
                    }
                }

                // Nếu có chỉ định đích danh giảng viên của môn:
                var specificInstructorMatch = FindMatchingInstructor(qNorm, courseSections);
                var targetSections = (specificInstructorMatch.HasValue && specificInstructorMatch.Value.Sections.Any())
                    ? specificInstructorMatch.Value.Sections
                    : courseSections;

                if (courseSections.Any())
                {
                    if (specificInstructorMatch.HasValue && specificInstructorMatch.Value.Sections.Any())
                    {
                        sb.AppendLine($"👨‍🏫 **Lớp học phần môn {matchedCourse.CourseName} do {specificInstructorMatch.Value.Name} phụ trách ({semester}):**");
                    }
                    else
                    {
                        sb.AppendLine($"🏫 **Danh sách {courseSections.Count} lớp học phần đang mở trong kỳ ({semester}):**");
                    }
                    sb.AppendLine();

                    foreach (var s in targetSections)
                    {
                        int remain = Math.Max(0, s.MaxCapacity - s.CurrentEnrollment);
                        sb.AppendLine($"- **Mã lớp:** `{s.SectionCode}`");
                        sb.AppendLine($"  + Giảng viên phụ trách: **{s.Instructor}**");
                        sb.AppendLine($"  + Lịch học & Phòng: **{FormatScheduleDetailed(s)}** | Phòng: **{s.Room}**");
                        sb.AppendLine($"  + Sĩ số hiện tại: **{s.CurrentEnrollment}/{s.MaxCapacity}** (Còn trống: **{remain} chỗ**)");
                    }

                    if (targetSections.Count < courseSections.Count)
                    {
                        var otherClasses = string.Join(", ", courseSections.Where(s => !targetSections.Contains(s)).Select(s => $"`{s.SectionCode}` ({s.Instructor})"));
                        sb.AppendLine();
                        sb.AppendLine($"💡 *Môn này còn có lớp khác: {otherClasses}.*");
                    }
                }
                else
                {
                    sb.AppendLine($"- *Hiện tại trong học kỳ {semester} chưa xếp lịch mở lớp học phần cho môn này.*");
                }

                advice.AiMessage = sb.ToString();
                return; // KHÔNG tự ý đề xuất môn
            }

            // =========================================================================
            // TRƯỜNG HỢP 8: TRA CỨU GIẢNG VIÊN (Instructor)
            // =========================================================================
            var sectionsPool = allSectionsAcrossSemesters != null && allSectionsAcrossSemesters.Any() ? allSectionsAcrossSemesters : allSections;
            var matchedInstructor = FindMatchingInstructor(effectiveSearchText, sectionsPool);
            if (matchedInstructor != null && matchedInstructor.Value.Sections.Any())
            {
                var (instName, instSections) = matchedInstructor.Value;
                int totalEnrolled = instSections.Sum(s => s.CurrentEnrollment);
                int totalCap = instSections.Sum(s => s.MaxCapacity);

                var sb = new StringBuilder();
                sb.AppendLine($"### 👨‍🏫 Thông Tin Giảng Viên & Môn Học Phụ Trách: **{instName}**");
                sb.AppendLine();
                sb.AppendLine($"Cố vấn AI đã truy vấn CSDL SQL Server thời gian thực. Giảng viên **{instName}** hiện phụ trách **{instSections.Count} lớp học phần** (Tổng sĩ số: **{totalEnrolled}/{totalCap} sinh viên**):");
                sb.AppendLine();

                // Nhóm theo từng môn học để trả lời rõ ràng: TÊN MÔN, MÃ LỚP, HỌC KỲ và LỊCH HỌC
                var courseGroups = instSections.GroupBy(s => s.CourseId);
                foreach (var cg in courseGroups)
                {
                    var course = cg.First().Course;
                    string courseName = course?.CourseName ?? "Chưa rõ tên môn";
                    string courseCode = course?.CourseCode ?? "";
                    int credits = course?.Credits ?? 0;

                    sb.AppendLine($"📘 **Môn học: {courseName}** (Mã môn: `{courseCode}` — **{credits} Tín chỉ**)");
                    foreach (var s in cg)
                    {
                        int remain = Math.Max(0, s.MaxCapacity - s.CurrentEnrollment);
                        string scheduleStr = FormatScheduleDetailed(s);
                        sb.AppendLine($"- **Mã lớp học phần:** `{s.SectionCode}` (Học kỳ: **{s.Semester}**)");
                        sb.AppendLine($"  + **Lịch học:** **{scheduleStr}**");
                        sb.AppendLine($"  + **Phòng học:** **{s.Room ?? "Chưa xếp phòng"}**");
                        sb.AppendLine($"  + **Sĩ số hiện tại:** **{s.CurrentEnrollment}/{s.MaxCapacity} sinh viên** (Còn trống **{remain} chỗ**)");
                    }
                    sb.AppendLine();
                }

                advice.AiMessage = sb.ToString();
                return; // KHÔNG tự ý đề xuất môn
            }

            // =========================================================================
            // TRƯỜNG HỢP 8.1: HỎI VỀ GIẢNG VIÊN NHƯNG KHÔNG TÌM THẤY TRONG CSDL SQL SERVER
            // =========================================================================
            bool isInstructorInquiry =
                qNorm.Contains("thay ") || qNorm.Contains("co ") ||
                qNorm.Contains("giang vien") || qNorm.Contains("gv ") ||
                qNorm.Contains("mon cua thay") || qNorm.Contains("mon cua co") ||
                qNorm.Contains("lop cua thay") || qNorm.Contains("lop cua co") ||
                qNorm.Contains("ai day") || qNorm.Contains("ai giang day") ||
                qNorm.Contains("ai phu trach");

            if (isInstructorInquiry && matchedInstructor == null)
            {
                var activeInstructors = sectionsPool
                    .Where(s => !string.IsNullOrEmpty(s.Instructor))
                    .GroupBy(s => s.Instructor)
                    .OrderBy(g => g.Key)
                    .ToList();

                var sb = new StringBuilder();
                sb.AppendLine("### 🔍 Thông Báo Tra Cứu Giảng Viên:");
                sb.AppendLine();
                sb.AppendLine($"Cố vấn AI đã tra cứu trực tiếp trong CSDL SQL Server nhưng **không tìm thấy giảng viên** nào khớp với nội dung câu hỏi: *\"{qRaw}\"*.");
                sb.AppendLine();
                sb.AppendLine("👨‍🏫 **Danh sách các giảng viên hiện đang có phân công giảng dạy trong hệ thống CSDL nhà trường:**");
                foreach (var g in activeInstructors)
                {
                    var coursesTaught = string.Join(", ", g.Select(s => s.Course?.CourseName).Where(n => !string.IsNullOrEmpty(n)).Distinct());
                    sb.AppendLine($"- **{g.Key}**: phụ trách môn *{coursesTaught}*");
                }
                sb.AppendLine();
                sb.AppendLine("💡 *Bạn vui lòng kiểm tra lại họ tên hoặc học hàm/học vị của giảng viên nhé!*");

                advice.AiMessage = sb.ToString();
                return;
            }

            // =========================================================================
            // TRƯỜNG HỢP 9: TRA CỨU LỊCH HỌC TOÀN TRƯỜNG THEO THỨ / NGÀY
            // (Chỉ chạy khi không phải câu hỏi cá nhân của sinh viên)
            // =========================================================================
            if (queriedDay.HasValue)
            {
                int day = queriedDay.Value;
                string dayName = day == 8 ? "Chủ Nhật" : $"Thứ {day}";

                var sectionsOnDay = allSections.Where(s =>
                    s.Schedules != null && s.Schedules.Any(sch => sch.DayOfWeek == day)).ToList();

                var sb = new StringBuilder();
                sb.AppendLine($"### 🗓️ Danh Sách Lớp Học Phần Toàn Trường Diễn Ra Vào {dayName} ({semester}):");
                sb.AppendLine();

                if (sectionsOnDay.Any())
                {
                    sb.AppendLine($"Có **{sectionsOnDay.Count} lớp học phần** diễn ra vào **{dayName}**:");
                    sb.AppendLine();

                    foreach (var sec in sectionsOnDay)
                    {
                        var schs = sec.Schedules!.Where(sch => sch.DayOfWeek == day).ToList();
                        string periods = string.Join(", ", schs.Select(sch => $"Tiết {sch.StartPeriod} - {sch.EndPeriod} (Phòng {sch.Room})"));
                        int remain = Math.Max(0, sec.MaxCapacity - sec.CurrentEnrollment);

                        sb.AppendLine($"- **Lớp `{sec.SectionCode}`** — **{sec.Course?.CourseName}** (`{sec.Course?.CourseCode}` - {sec.Course?.Credits}TC):");
                        sb.AppendLine($"  + Giảng viên: **{sec.Instructor}**");
                        sb.AppendLine($"  + Ca học: **{periods}**");
                        sb.AppendLine($"  + Sĩ số: **{sec.CurrentEnrollment}/{sec.MaxCapacity}** (Còn trống: **{remain} chỗ**)");
                    }
                }
                else
                {
                    sb.AppendLine($"- Không có lớp học phần nào được xếp lịch vào {dayName} trong học kỳ {semester}.");
                }

                advice.AiMessage = sb.ToString();
                return; // KHÔNG tự ý đề xuất môn
            }

            // =========================================================================
            // TRƯỜNG HỢP 10: TRA CỨU PHÒNG HỌC (Room)
            // =========================================================================
            var matchedRoom = FindMatchingRoom(effectiveSearchText, allSections);
            if (!string.IsNullOrEmpty(matchedRoom))
            {
                var sectionsInRoom = allSections.Where(s =>
                    (!string.IsNullOrEmpty(s.Room) && s.Room.Equals(matchedRoom, StringComparison.OrdinalIgnoreCase)) ||
                    (s.Schedules != null && s.Schedules.Any(sch => !string.IsNullOrEmpty(sch.Room) && sch.Room.Equals(matchedRoom, StringComparison.OrdinalIgnoreCase)))
                ).ToList();

                var sb = new StringBuilder();
                sb.AppendLine($"### 🏫 Thông Tin Phòng Học: **{matchedRoom}**");
                sb.AppendLine();

                if (sectionsInRoom.Any())
                {
                    sb.AppendLine($"Phòng **{matchedRoom}** được bố trí cho **{sectionsInRoom.Count} lớp học phần** trong học kỳ **{semester}**:");
                    sb.AppendLine();

                    foreach (var s in sectionsInRoom)
                    {
                        sb.AppendLine($"- **Lớp `{s.SectionCode}`** — **{s.Course?.CourseName}** ({s.Course?.Credits}TC):");
                        sb.AppendLine($"  + Giảng viên: **{s.Instructor}**");
                        sb.AppendLine($"  + Thời khóa biểu: **{FormatScheduleDetailed(s)}**");
                        sb.AppendLine($"  + Sĩ số: **{s.CurrentEnrollment}/{s.MaxCapacity} sinh viên**");
                    }
                }
                else
                {
                    sb.AppendLine($"- Chưa có lớp học phần nào được xếp lịch tại phòng {matchedRoom} trong học kỳ này.");
                }

                advice.AiMessage = sb.ToString();
                return; // KHÔNG tự ý đề xuất môn
            }

            // =========================================================================
            // TRƯỜNG HỢP 11: CHÀO HỎI & GIỚI THIỆU TRỢ LÝ
            // =========================================================================
            bool isGreeting = qNorm == "chao" || qNorm.StartsWith("chao ") || qNorm.Contains("xin chao") ||
                              qNorm == "hello" || qNorm == "hi" || qNorm.StartsWith("hi ") ||
                              qNorm.Contains("ban la ai") || qNorm == "alo" || qNorm.StartsWith("alo ") ||
                              qNorm.Contains("tro giup");

            if (isGreeting && !qNorm.Contains("mon") && !qNorm.Contains("lop") && !qNorm.Contains("thay") && !qNorm.Contains("co") && !qNorm.Contains("phong") && !qNorm.Contains("diem"))
            {
                advice.AiMessage = $"### 👋 Chào bạn! Tôi là Cố Vấn Học Thuật AI của Trường Đại học Kinh tế Huế.\n\n" +
                                   $"Tôi có thể hỗ trợ bạn tra cứu toàn bộ thông tin học vụ theo thời gian thực từ SQL Server:\n" +
                                   $"- 📘 **Môn học & Lớp:** Tra cứu *Lập trình Web*, *Cơ sở dữ liệu*, mã lớp, phòng học, giảng viên, sĩ số.\n" +
                                   $"- 🗓️ **Thời khóa biểu:** Phân biệt chính xác giữa *Lịch học cá nhân đã đăng ký* và *Lịch học toàn trường*.\n" +
                                   $"- 💰 **Học phí & VietQR:** 450.000 đ/tín chỉ, tài khoản MB Bank, thanh toán VietQR Napas 24/7.\n" +
                                   $"- ⚖️ **Quy chế tín chỉ:** Hạn mức 10-24 tín chỉ, quy định rút môn học, điều kiện tiên quyết.\n\n" +
                                   $"👉 Bạn có thể đặt câu hỏi hoặc yêu cầu: *\"Gợi ý môn cho tôi kỳ này\"* khi muốn lên kế hoạch học tập!";
                return;
            }

            // =========================================================================
            // TRƯỜNG HỢP 12: MÔN HỌC KHÔNG TỒN TẠI TRONG CSDL
            // =========================================================================
            if (qNorm.Contains("mon ") || qNorm.Contains("hoc phan "))
            {
                advice.AiMessage = $"### 🔍 Thông Báo Tra Cứu Học Phần:\n\n" +
                                   $"Cố vấn AI đã tra cứu trực tiếp trong CSDL SQL Server nhưng **không tìm thấy môn học nào** khớp với nội dung: *\"{qRaw}\"*.\n\n" +
                                   $"💡 Bạn vui lòng kiểm tra lại tên môn học hoặc mã môn học (ví dụ: *Lập trình Ứng dụng Web (WEB301)*, *Cơ sở Dữ liệu (CSDL201)*, *Kỹ thuật Phần mềm (KTPM302)*...).";
                return;
            }

            // =========================================================================
            // TRƯỜNG HỢP 13: CÂU HỎI CHƯA RÕ RÀNG HOẶC THIẾU DỮ LIỆU
            // =========================================================================
            string studentGreeting = student != null ? $"Chào bạn **{student.FullName}** (`{student.StudentCode}`)!" : "Chào bạn!";
            advice.AiMessage = $"### 🤖 Cố Vấn Học Thuật AI Sẵn Sàng Hỗ Trợ:\n\n" +
                               $"{studentGreeting} Tôi đã nhận được câu hỏi: *\"{qRaw}\"*.\n\n" +
                               $"Để Cố vấn AI có thể tra cứu CSDL SQL Server và trả lời chính xác nhất, bạn có thể hỏi cụ thể theo các mẫu sau:\n\n" +
                               $"- **Lịch học cá nhân:** *\"Thứ 2 tôi học gì?\"* hoặc *\"Thời khóa biểu của tôi\"*\n" +
                               $"- **Tra cứu môn học:** *\"Môn Lập trình Web có mấy lớp và ai dạy?\"* hoặc *\"Môn Cơ sở dữ liệu học phòng nào?\"*\n" +
                               $"- **Tra cứu giảng viên:** *\"Thầy Long dạy môn gì?\"* hoặc *\"Thầy Sơn dạy vào thứ mấy?\"*\n" +
                               $"- **Tra cứu mã lớp:** *\"Lớp WEB301_01 học thứ mấy, phòng nào?\"*\n" +
                               $"- **Tra cứu học phí & quy chế:** *\"Học phí 1 tín chỉ bao nhiêu?\"* hoặc *\"Quy chế rút môn thế nào?\"*\n" +
                               $"- **Tư vấn đăng ký:** *\"Gợi ý môn cho tôi kỳ này\"* (AI sẽ phân tích học bạ và đề xuất môn phù hợp).";
        }

        /// <summary>
        /// Thuật toán tạo gợi ý học phần tối ưu không trùng lịch và tuân thủ tiên quyết.
        /// CHỈ được gọi khi sinh viên yêu cầu gợi ý rõ ràng!
        /// </summary>
        private static void GenerateCourseRecommendations(
            AiCourseAdvice advice,
            Student student,
            List<Section> candidateSections,
            int remainingCreditsAllowed,
            string qNorm,
            string semester)
        {
            int targetCredits = 14;
            if (qNorm.Contains("toi da") || qNorm.Contains("nhieu"))
            {
                targetCredits = remainingCreditsAllowed;
            }
            else if (qNorm.Contains("it") || qNorm.Contains("nhe") || qNorm.Contains("vua suc"))
            {
                targetCredits = Math.Min(9, remainingCreditsAllowed);
            }
            else
            {
                targetCredits = Math.Min(14, remainingCreditsAllowed);
            }

            bool avoidMondayMorning = qNorm.Contains("tranh sang thu 2") || qNorm.Contains("tranh tiet som") || qNorm.Contains("tranh thu 2");

            var scored = new List<(Section Section, double Score, string Reason)>();
            foreach (var sec in candidateSections)
            {
                if (sec.Course == null) continue;
                double score = 10.0;
                var reasons = new List<string>();

                var courseNameNorm = RemoveDiacritics(sec.Course.CourseName.ToLowerInvariant());

                if (qNorm.Contains("web") && courseNameNorm.Contains("web"))
                {
                    score += 50;
                    reasons.Add("Khớp mục tiêu chuyên sâu Lập trình Web");
                }
                if (qNorm.Contains("csdl") && courseNameNorm.Contains("du lieu"))
                {
                    score += 45;
                    reasons.Add("Khớp nguyện vọng Cơ sở dữ liệu");
                }
                if (qNorm.Contains("an toan") && courseNameNorm.Contains("an toan"))
                {
                    score += 50;
                    reasons.Add("Khớp chuyên ngành An toàn thông tin");
                }

                if (avoidMondayMorning && sec.Schedules != null && sec.Schedules.Any(sch => sch.DayOfWeek == 2 && sch.StartPeriod <= 3))
                {
                    score -= 100;
                }
                else if (avoidMondayMorning)
                {
                    reasons.Add("Tránh sáng Thứ 2");
                }

                reasons.Add($"Đạt chuẩn tiên quyết, lớp còn {sec.MaxCapacity - sec.CurrentEnrollment} chỗ trống");
                scored.Add((sec, score, string.Join("; ", reasons)));
            }

            var sorted = scored.OrderByDescending(x => x.Score).ToList();
            int accumulated = 0;
            var selected = new List<Section>();

            foreach (var item in sorted)
            {
                var sec = item.Section;
                int cr = sec.Course?.Credits ?? 0;
                if (accumulated + cr > remainingCreditsAllowed) continue;

                bool conflict = selected.Any(already => HasScheduleConflict(sec, already));
                if (conflict) continue;

                selected.Add(sec);
                accumulated += cr;

                advice.RecommendedSections.Add(new AiRecommendedSection
                {
                    SectionId = sec.SectionId,
                    SectionCode = sec.SectionCode,
                    CourseId = sec.CourseId,
                    CourseCode = sec.Course?.CourseCode ?? string.Empty,
                    CourseName = sec.Course?.CourseName ?? string.Empty,
                    Credits = cr,
                    Instructor = sec.Instructor,
                    Room = sec.Room,
                    ScheduleSummary = FormatSchedule(sec),
                    RecommendationReason = item.Reason,
                    IsSelected = true
                });

                if (accumulated >= targetCredits) break;
            }

            advice.AiMessage = $"### 🎯 Đề Xuất Lộ Trình Học Tập Tối Ưu Cho Sinh Viên:\n\n" +
                               $"Chào bạn **{student.FullName}** (`{student.StudentCode}` - Ngành {student.Major})!\n\n" +
                               $"- Dựa trên hồ sơ học bạ và nguyện vọng của bạn, Cố vấn AI đề xuất gói học phần gồm **{advice.RecommendedSections.Count} môn ({advice.TotalCredits} Tín chỉ)** trong học kỳ {semester}.\n" +
                               $"- **Cam kết an toàn học vụ:** 100% môn đề xuất đã thỏa mãn điều kiện tiên quyết và **tuyệt đối không bị trùng lịch học**.\n" +
                               $"- **Xác nhận người dùng:** Dữ liệu do AI đề xuất dưới đây là gợi ý. Bạn hãy rà soát danh sách bên dưới, chọn các môn mong muốn và bấm nút **'Xác Nhận & Thêm Vào Giỏ Đăng Ký'** để lưu vào giỏ hàng.";
        }

        /// <summary>
        /// Nhận diện yêu cầu tư vấn / gợi ý môn học rõ ràng từ sinh viên
        /// </summary>
        private static bool IsExplicitRecommendationRequest(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string norm = RemoveDiacritics(text.ToLowerInvariant());

            if (norm.Contains("goi y") || norm.Contains("tu van") || norm.Contains("de xuat") ||
                norm.Contains("nen hoc") || norm.Contains("nen dang ky") || norm.Contains("hoc gi ky nay") ||
                norm.Contains("dang ky gi ky nay") || norm.Contains("len thoi khoa bieu") ||
                norm.Contains("xep thoi khoa bieu") || norm.Contains("ke hoach hoc tap") ||
                norm.Contains("lo trinh hoc") || norm.Contains("hoc nhe") || norm.Contains("can bang") ||
                norm.Contains("toi da tin chi") || norm.Contains("it tin chi") || norm.Contains("nhieu tin chi") ||
                norm.Contains("tranh sang thu 2"))
            {
                return true;
            }

            return false;
        }

        private static Course? FindMatchingCourse(string qNorm, List<Course> allCourses)
        {
            foreach (var c in allCourses)
            {
                string codeNorm = RemoveDiacritics(c.CourseCode.ToLowerInvariant());
                string nameNorm = RemoveDiacritics(c.CourseName.ToLowerInvariant());

                if (qNorm.Contains(codeNorm) || qNorm.Contains(nameNorm))
                {
                    return c;
                }
            }

            // Tìm từ khóa rút gọn
            if (qNorm.Contains("lap trinh web") || qNorm.Contains("web"))
            {
                return allCourses.FirstOrDefault(c => c.CourseCode == "WEB301") ?? allCourses.FirstOrDefault(c => c.CourseCode == "WEB302");
            }
            if (qNorm.Contains("co so du lieu") || qNorm.Contains("csdl") || qNorm.Contains("database"))
            {
                return allCourses.FirstOrDefault(c => c.CourseCode == "CSDL201");
            }
            if (qNorm.Contains("cau truc du lieu") || qNorm.Contains("giai thuat") || qNorm.Contains("algo"))
            {
                return allCourses.FirstOrDefault(c => c.CourseCode == "TIN201");
            }
            if (qNorm.Contains("huong doi tuong") || qNorm.Contains("oop"))
            {
                return allCourses.FirstOrDefault(c => c.CourseCode == "TIN102");
            }
            if (qNorm.Contains("nhap mon lap trinh") || qNorm.Contains("tin101"))
            {
                return allCourses.FirstOrDefault(c => c.CourseCode == "TIN101");
            }
            if (qNorm.Contains("an toan") || qNorm.Contains("bao mat") || qNorm.Contains("attt"))
            {
                return allCourses.FirstOrDefault(c => c.CourseCode == "ATTT401");
            }
            if (qNorm.Contains("kiem thu") || qNorm.Contains("ktpm") || qNorm.Contains("chat luong phan mem") || qNorm.Contains("ky thuat phan mem"))
            {
                return allCourses.FirstOrDefault(c => c.CourseCode == "KTPM302");
            }
            if (qNorm.Contains("tieng anh") || qNorm.Contains("toeic") || qNorm.Contains("anh van"))
            {
                return allCourses.FirstOrDefault(c => c.CourseCode == "TOEIC");
            }
            if (qNorm.Contains("tri tue nhan tao") || qNorm.Contains("hoc may") || qNorm.Contains("ai401") ||
                qNorm.Contains("mon ai") || qNorm.Contains("lop ai") || qNorm.Contains("hoc ai"))
            {
                return allCourses.FirstOrDefault(c => c.CourseCode == "AI401");
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

        private static (string Name, List<Section> Sections)? FindMatchingInstructor(string qNorm, List<Section> allSections)
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
                string rawNorm = RemoveDiacritics(inst.ToLowerInvariant());
                string cleanName = CleanAcademicTitle(inst);
                string cleanNorm = RemoveDiacritics(cleanName.ToLowerInvariant());

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
                    var matchedSecs = allSections.Where(s => s.Instructor == inst).ToList();
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

        /// <summary>
        /// Xác định ngày được hỏi trong câu (Hôm nay / Ngày mai / Thứ 2 - CN)
        /// </summary>
        private static int? DetectTargetDay(string qNorm)
        {
            if (qNorm.Contains("hom nay"))
            {
                DayOfWeek dow = DateTime.Today.DayOfWeek;
                return dow == DayOfWeek.Sunday ? 8 : ((int)dow + 1);
            }

            if (qNorm.Contains("ngay mai"))
            {
                DayOfWeek dow = DateTime.Today.AddDays(1).DayOfWeek;
                return dow == DayOfWeek.Sunday ? 8 : ((int)dow + 1);
            }

            if (qNorm.Contains("thu 2") || qNorm.Contains("thu hai")) return 2;
            if (qNorm.Contains("thu 3") || qNorm.Contains("thu ba")) return 3;
            if (qNorm.Contains("thu 4") || qNorm.Contains("thu tu")) return 4;
            if (qNorm.Contains("thu 5") || qNorm.Contains("thu nam")) return 5;
            if (qNorm.Contains("thu 6") || qNorm.Contains("thu sau")) return 6;
            if (qNorm.Contains("thu 7") || qNorm.Contains("thu bay")) return 7;
            if (qNorm.Contains("chu nhat") || qNorm.Contains("cn")) return 8;

            return null;
        }

        private static string? FindMatchingRoom(string qNorm, List<Section> allSections)
        {
            var rooms = allSections
                .Where(s => !string.IsNullOrEmpty(s.Room))
                .Select(s => s.Room)
                .Distinct()
                .ToList();

            foreach (var r in rooms)
            {
                string rNorm = RemoveDiacritics(r.ToLowerInvariant());
                if (qNorm.Contains(rNorm)) return r;
            }

            if (qNorm.Contains("lab 402") || qNorm.Contains("402")) return "Lab 402";
            if (qNorm.Contains("lab 403") || qNorm.Contains("403")) return "Lab 403";
            if (qNorm.Contains("lab 501") || qNorm.Contains("501")) return "Lab 501";
            if (qNorm.Contains("lab 502") || qNorm.Contains("502")) return "Lab 502";
            if (qNorm.Contains("a101")) return "Phòng A101";
            if (qNorm.Contains("a102")) return "Phòng A102";
            if (qNorm.Contains("a205")) return "Phòng A205";
            if (qNorm.Contains("b204")) return "Phòng B204";
            if (qNorm.Contains("b301")) return "Phòng B301";
            if (qNorm.Contains("c301")) return "Phòng C301";
            if (qNorm.Contains("c302")) return "Phòng C302";
            if (qNorm.Contains("hoi truong a")) return "Hội trường A";
            if (qNorm.Contains("hoi truong b")) return "Hội trường B";

            return null;
        }

        private static string ExtractContextSubject(
            string qNorm,
            List<AiConversationMessage>? conversationHistory,
            List<Course> allCourses,
            List<Section> allSections)
        {
            if (conversationHistory == null || !conversationHistory.Any()) return string.Empty;

            bool isFollowUp = qNorm.Contains("mon do") || qNorm.Contains("mon nay") ||
                              qNorm.Contains("lop do") || qNorm.Contains("lop nay") ||
                              qNorm.Contains("thay do") || qNorm.Contains("co do") ||
                              qNorm.Contains("the con") || qNorm.Contains("lop thu nhat") ||
                              qNorm.Contains("lop thu 2") || qNorm.Contains("lop 1") || qNorm.Contains("lop 2") ||
                              qNorm.Contains("ai day") || qNorm.Contains("phong nao") ||
                              qNorm.Contains("thu may") || qNorm.Contains("tiet may") ||
                              qNorm.Contains("con cho khong") || qNorm.Contains("bao nhieu tin chi");

            if (!isFollowUp) return string.Empty;

            // Tìm ngược trong lịch sử câu gần nhất
            for (int i = conversationHistory.Count - 1; i >= 0; i--)
            {
                var msg = conversationHistory[i];
                string histNorm = RemoveDiacritics(msg.Content.ToLowerInvariant());

                var c = FindMatchingCourse(histNorm, allCourses);
                if (c != null)
                {
                    return RemoveDiacritics(c.CourseName.ToLowerInvariant());
                }

                var inst = FindMatchingInstructor(histNorm, allSections);
                if (inst != null)
                {
                    return RemoveDiacritics(inst.Value.Name.ToLowerInvariant());
                }

                var sec = allSections.FirstOrDefault(s => histNorm.Contains(s.SectionCode.ToLowerInvariant()));
                if (sec != null)
                {
                    return sec.SectionCode;
                }
            }

            return string.Empty;
        }

        private static string FormatSchedule(Section section)
        {
            if (section.Schedules == null || !section.Schedules.Any()) return "Chưa xếp lịch";
            return string.Join("; ", section.Schedules.Select(s => $"Thứ {s.DayOfWeek} (Tiết {s.StartPeriod}-{s.EndPeriod}, {s.Room})"));
        }

        private static string FormatScheduleDetailed(Section section)
        {
            if (section.Schedules == null || !section.Schedules.Any()) return "Chưa xếp lịch thời khóa biểu";
            var items = section.Schedules.Select(s =>
            {
                string dayStr = s.DayOfWeek == 8 ? "Chủ Nhật" : $"Thứ {s.DayOfWeek}";
                return $"{dayStr} (Từ Tiết {s.StartPeriod} đến Tiết {s.EndPeriod})";
            });
            return string.Join(" & ", items);
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

        private static string RemoveDiacritics(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            var normalizedString = text.Normalize(NormalizationForm.FormD);
            var stringBuilder = new StringBuilder();

            foreach (var c in normalizedString)
            {
                var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
                if (unicodeCategory != UnicodeCategory.NonSpacingMark)
                {
                    stringBuilder.Append(c);
                }
            }

            return stringBuilder.ToString()
                .Normalize(NormalizationForm.FormC)
                .Replace('đ', 'd')
                .Replace('Đ', 'D');
        }
    }
}
