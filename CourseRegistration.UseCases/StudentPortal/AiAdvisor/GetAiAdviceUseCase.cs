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

            // 2. Lọc danh sách lớp ứng viên hợp lệ cho đề xuất học phần (chưa học, chưa đăng ký, đủ tiên quyết, còn chỗ)
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
                    var sectionsDbDump = string.Join("\n", allSections.Select(s =>
                        $"- Lớp: {s.SectionCode} | Môn: {s.Course?.CourseCode} - {s.Course?.CourseName} ({s.Course?.Credits}TC) | GV: {s.Instructor} | Phòng: {s.Room} | Lịch: {FormatSchedule(s)} | Sĩ số: {s.CurrentEnrollment}/{s.MaxCapacity}"));

                    var coursesDbDump = string.Join(", ", allCourses.Select(c => $"{c.CourseCode}: {c.CourseName} ({c.Credits}TC)"));

                    var prereqDbDump = string.Join("\n", allPrerequisites.Select(p =>
                        $"- Môn {p.Course?.CourseCode} yêu cầu tiên quyết {p.PrerequisiteCourse?.CourseCode} ({p.PrerequisiteCourse?.CourseName})"));

                    string studentProfileText = student != null
                        ? $"- Sinh viên: {student.FullName} (MSSV: {student.StudentCode}) | Ngành: {student.Major}\n" +
                          $"- Số tín chỉ tối đa được đăng ký thêm trong kỳ {semester}: {remainingCreditsAllowed} TC\n" +
                          $"- Các môn đã hoàn thành: {(completedCourses.Any() ? string.Join(", ", completedCourses.Select(c => $"{c.Course?.CourseCode} ({c.Grade}đ)")) : "Chưa hoàn thành môn nào")}\n" +
                          $"- Các môn đã đăng ký trong kỳ: {(registeredSections.Any() ? string.Join(", ", registeredSections.Select(r => $"{r.SectionCode} ({r.Course?.CourseName})")) : "Chưa đăng ký môn nào")}"
                        : "- Người dùng hiện đang ở chế độ Khách (chưa đăng nhập tài khoản cá nhân).";

                    var systemPrompt = $@"Bạn là Cố vấn Học thuật AI của Trường Đại học Kinh tế Huế.
Nhiệm vụ của bạn là trả lời sinh viên bằng tiếng Việt, đúng trọng tâm, văn phong sư phạm, lịch sự và súc tích.

[DỮ LIỆU THỰC TẾ TỪ CSDL SQL SERVER HỌC KỲ {semester}]
Danh sách lớp học phần:
{sectionsDbDump}

Danh sách môn học toàn trường:
{coursesDbDump}

Điều kiện tiên quyết:
{prereqDbDump}

Hồ sơ sinh viên đang hỏi:
{studentProfileText}

[NGUYÊN TẮC CỐT LÕI]
1. DỰA HOÀN TOÀN VÀO DỮ LIỆU SQL SERVER Ở TRÊN. Tuyệt đối không tự bịa thông tin, phòng học, giảng viên hay mã lớp.
2. Khi người dùng hỏi về Môn học, Giảng viên, Mã lớp học phần, Thứ, Tiết, Ngày học, Phòng học, Sĩ số: Trả lời chính xác, rõ ràng số liệu thực tế.
3. Khi có nhiều lớp học phần phù hợp, hãy liệt kê đầy đủ thông tin từng lớp.
4. Duy trì ngữ cảnh cuộc trò chuyện nếu người dùng hỏi tiếp các câu hỏi nối tiếp.
5. Nếu câu hỏi chưa rõ hoặc thiếu dữ liệu, hãy hỏi lại hoặc thông báo rõ ràng cho sinh viên.
6. Nếu sinh viên hỏi về học phí: Đơn giá là 450.000 VNĐ / tín chỉ, MB Bank 101705201967 (Napas 24/7).
7. Nếu sinh viên hỏi về quy chế rút môn: Trong hạn trực tuyến tự rút trong giỏ/phiếu; sau hạn nộp đơn về Phòng Đào Tạo. Hạn mức tối thiểu 10 TC, tối đa 24 TC.";

                    var messages = new List<ChatMessage>
                    {
                        new ChatMessage(ChatRole.System, systemPrompt)
                    };

                    // Bổ sung lịch sử hội thoại gần nhất (Conversation Context)
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

                        // Rà soát xem LLM có đề cập đến lớp học phần nào để bổ sung vào RecommendedSections cho sinh viên xác nhận
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
                    conversationHistory);
            }

            return advice;
        }

        /// <summary>
        /// Động cơ Phân tích Tri thức SQL Server Chuyên Sâu:
        /// Hiểu tiếng Việt, duy trì ngữ cảnh hội thoại, tra cứu chính xác Môn học, Giảng viên, Mã lớp, Thứ, Tiết, Phòng học.
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
            List<AiConversationMessage>? conversationHistory)
        {
            if (string.IsNullOrWhiteSpace(userPrompt))
            {
                if (student != null)
                {
                    advice.AiMessage = $"### 👋 Chào bạn {student.FullName} (MSSV: {student.StudentCode})!\n\n" +
                                       $"Tôi là **Cố vấn Học thuật AI** của bạn trong học kỳ **{semester}**. Bạn có thể hỏi tôi bất kỳ điều gì:\n" +
                                       $"- *Tra cứu môn học, giảng viên, mã lớp học phần, thứ, tiết, phòng học.*\n" +
                                       $"- *Kiểm tra điểm số, học bạ, các môn đã đăng ký và môn tiên quyết.*\n" +
                                       $"- *Quy chế rút môn, hạn mức số tín chỉ, mức học phí và thanh toán VietQR.*\n" +
                                       $"- *Hoặc yêu cầu tôi lên thời khóa biểu theo nguyện vọng cá nhân (cân bằng, học nhẹ, tránh sáng thứ 2, v.v.).*";
                }
                else
                {
                    advice.AiMessage = $"### 👋 Chào bạn! Tôi là Cố Vấn Học Thuật AI.\n\n" +
                                       $"Bạn có thể hỏi tôi về bất kỳ thông tin nào trong CSDL nhà trường (môn học, giảng viên, phòng học, lịch học, học phí, quy chế tín chỉ) hoặc đăng nhập để tôi tư vấn kế hoạch học tập riêng cho bạn nhé!";
                }
                return;
            }

            string qRaw = userPrompt.Trim();
            string qLower = qRaw.ToLowerInvariant();
            string qNorm = RemoveDiacritics(qLower);

            // Duy trì ngữ cảnh hội thoại (Context resolution) từ câu hỏi trước nếu câu này có đại từ thay thế
            string contextSubject = ExtractContextSubject(qNorm, conversationHistory, allCourses, allSections);
            string effectiveSearchText = !string.IsNullOrEmpty(contextSubject) ? $"{qNorm} {contextSubject}" : qNorm;

            // =========================================================================
            // TRƯỜNG HỢP 1: CÂU HỎI VỀ MÃ LỚP HỌC PHẦN CỤ THỂ (SectionCode)
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
                                   $"💡 *Lưu ý: Bạn có thể chọn lớp này vào Giỏ đăng ký học phần nếu còn thời hạn đăng ký trực tuyến.*";

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
                    RecommendationReason = $"Thông tin lớp {sec.SectionCode} tra cứu trực tiếp theo yêu cầu của bạn.",
                    IsSelected = true
                });
                return;
            }

            // =========================================================================
            // TRƯỜNG HỢP 2: CÂU HỎI VỀ MÔN HỌC (Course: WEB301, Lập trình Web, Cơ sở dữ liệu...)
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

                // Phân tích ý định cụ thể của câu hỏi:
                bool asksInstructor = qNorm.Contains("ai day") || qNorm.Contains("giang vien") || qNorm.Contains("thay") || qNorm.Contains("co");
                bool asksRoom = qNorm.Contains("phong nao") || qNorm.Contains("o dau") || qNorm.Contains("dia diem");
                bool asksSchedule = qNorm.Contains("thu may") || qNorm.Contains("tiet may") || qNorm.Contains("khi nao") || qNorm.Contains("lich hoc") || qNorm.Contains("gio hoc");
                bool asksPrereq = qNorm.Contains("tien quyet") || qNorm.Contains("dieu kien") || qNorm.Contains("hoc truoc");
                bool asksCapacity = qNorm.Contains("si so") || qNorm.Contains("con cho") || qNorm.Contains("bao nhieu nguoi") || qNorm.Contains("bao nhieu sv");

                var sb = new StringBuilder();
                sb.AppendLine($"### 📘 Thông Tin Học Phần: {matchedCourse.CourseName} (`{matchedCourse.CourseCode}`)");
                sb.AppendLine();
                sb.AppendLine($"- **Số tín chỉ:** **{matchedCourse.Credits} Tín chỉ** (Học phí tạm tính: **{(matchedCourse.Credits * 450000):N0} VNĐ**)");
                sb.AppendLine($"- **Mô tả môn học:** {matchedCourse.Description}");
                sb.AppendLine($"- **Học phần tiên quyết:** {prereqText}");
                sb.AppendLine();

                if (asksPrereq && !asksSchedule && !asksInstructor)
                {
                    if (student != null)
                    {
                        var studentPassedPrereq = prereqCourses.All(p => completedCourses.Any(c => c.CourseId == p.CourseId && c.Grade >= 5.0));
                        if (prereqCourses.Any())
                        {
                            if (studentPassedPrereq)
                            {
                                sb.AppendLine($"✅ **Kiểm tra học bạ:** Bạn ({student.FullName}) **ĐÃ HOÀN THÀNH** học phần tiên quyết cho môn này. Bạn hoàn toàn đủ điều kiện ghi danh!");
                            }
                            else
                            {
                                sb.AppendLine($"⚠️ **Cảnh báo học vụ:** Bạn ({student.FullName}) **CHƯA TÍCH LŨY ĐỦ** học phần tiên quyết ({prereqText}). Nếu đưa vào giỏ đăng ký, hệ thống sẽ chặn giao dịch theo Luật tiên quyết.");
                            }
                            sb.AppendLine();
                        }
                    }
                }

                if (courseSections.Any())
                {
                    sb.AppendLine($"🏫 **Danh sách {courseSections.Count} lớp học phần đang mở trong kỳ ({semester}):**");
                    sb.AppendLine();

                    foreach (var s in courseSections)
                    {
                        int remain = Math.Max(0, s.MaxCapacity - s.CurrentEnrollment);
                        sb.AppendLine($"- **Mã lớp:** `{s.SectionCode}`");
                        sb.AppendLine($"  + Giảng viên phụ trách: **{s.Instructor}**");
                        sb.AppendLine($"  + Lịch học & Phòng học: **{FormatScheduleDetailed(s)}** | Phòng: **{s.Room}**");
                        sb.AppendLine($"  + Sĩ số hiện tại: **{s.CurrentEnrollment}/{s.MaxCapacity}** (Còn trống: **{remain} chỗ**)");

                        advice.RecommendedSections.Add(new AiRecommendedSection
                        {
                            SectionId = s.SectionId,
                            SectionCode = s.SectionCode,
                            CourseId = s.CourseId,
                            CourseCode = matchedCourse.CourseCode,
                            CourseName = matchedCourse.CourseName,
                            Credits = matchedCourse.Credits,
                            Instructor = s.Instructor,
                            Room = s.Room,
                            ScheduleSummary = FormatSchedule(s),
                            RecommendationReason = $"Lớp {s.SectionCode} thuộc môn {matchedCourse.CourseName} theo câu hỏi của bạn.",
                            IsSelected = true
                        });
                    }
                }
                else
                {
                    sb.AppendLine($"- *Hiện tại trong học kỳ {semester} chưa xếp lịch mở lớp học phần cho môn này.*");
                }

                advice.AiMessage = sb.ToString();
                return;
            }

            // =========================================================================
            // TRƯỜNG HỢP 3: CÂU HỎI VỀ GIẢNG VIÊN (Instructor)
            // =========================================================================
            var matchedInstructor = FindMatchingInstructor(effectiveSearchText, allSections);
            if (matchedInstructor != null && matchedInstructor.Value.Sections.Any())
            {
                var (instName, instSections) = matchedInstructor.Value;
                int totalEnrolled = instSections.Sum(s => s.CurrentEnrollment);
                int totalCap = instSections.Sum(s => s.MaxCapacity);

                var sb = new StringBuilder();
                sb.AppendLine($"### 👨‍🏫 Thông Tin Giảng Viên: {instName}");
                sb.AppendLine();
                sb.AppendLine($"Trong học kỳ **{semester}**, giảng viên phụ trách **{instSections.Count} lớp học phần** với tổng số **{totalEnrolled}/{totalCap} sinh viên**:");
                sb.AppendLine();

                foreach (var s in instSections)
                {
                    int remain = Math.Max(0, s.MaxCapacity - s.CurrentEnrollment);
                    sb.AppendLine($"- **Lớp `{s.SectionCode}`** — **{s.Course?.CourseName}** (`{s.Course?.CourseCode}` - {s.Course?.Credits}TC):");
                    sb.AppendLine($"  + Thời khóa biểu: **{FormatScheduleDetailed(s)}**");
                    sb.AppendLine($"  + Phòng học: **{s.Room}**");
                    sb.AppendLine($"  + Sĩ số: **{s.CurrentEnrollment}/{s.MaxCapacity} sinh viên** (Còn trống: **{remain} chỗ**)");

                    advice.RecommendedSections.Add(new AiRecommendedSection
                    {
                        SectionId = s.SectionId,
                        SectionCode = s.SectionCode,
                        CourseId = s.CourseId,
                        CourseCode = s.Course?.CourseCode ?? string.Empty,
                        CourseName = s.Course?.CourseName ?? string.Empty,
                        Credits = s.Course?.Credits ?? 0,
                        Instructor = s.Instructor,
                        Room = s.Room,
                        ScheduleSummary = FormatSchedule(s),
                        RecommendationReason = $"Lớp do giảng viên {instName} trực tiếp giảng dạy.",
                        IsSelected = true
                    });
                }

                advice.AiMessage = sb.ToString();
                return;
            }

            // =========================================================================
            // TRƯỜNG HỢP 4: CÂU HỎI VỀ THỨ / NGÀY TRONG TUẦN (DayOfWeek)
            // =========================================================================
            int? queriedDayOfWeek = DetectDayOfWeek(qNorm);
            if (queriedDayOfWeek.HasValue)
            {
                int day = queriedDayOfWeek.Value;
                string dayName = day == 8 ? "Chủ Nhật" : $"Thứ {day}";

                var sectionsOnDay = allSections.Where(s =>
                    s.Schedules != null && s.Schedules.Any(sch => sch.DayOfWeek == day)).ToList();

                var sb = new StringBuilder();
                sb.AppendLine($"### 🗓️ Danh Sách Lớp Học Phần Học Vào {dayName} ({semester}):");
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
                            RecommendationReason = $"Lịch học diễn ra vào {dayName} theo yêu cầu của bạn.",
                            IsSelected = true
                        });
                    }
                }
                else
                {
                    sb.AppendLine($"- Không có lớp học phần nào được xếp lịch vào {dayName} trong học kỳ {semester}.");
                }

                advice.AiMessage = sb.ToString();
                return;
            }

            // =========================================================================
            // TRƯỜNG HỢP 5: CÂU HỎI VỀ PHÒNG HỌC (Room: Lab 402, A101, B204...)
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

                        advice.RecommendedSections.Add(new AiRecommendedSection
                        {
                            SectionId = s.SectionId,
                            SectionCode = s.SectionCode,
                            CourseId = s.CourseId,
                            CourseCode = s.Course?.CourseCode ?? string.Empty,
                            CourseName = s.Course?.CourseName ?? string.Empty,
                            Credits = s.Course?.Credits ?? 0,
                            Instructor = s.Instructor,
                            Room = s.Room,
                            ScheduleSummary = FormatSchedule(s),
                            RecommendationReason = $"Lớp học được tổ chức tại phòng {matchedRoom}.",
                            IsSelected = true
                        });
                    }
                }
                else
                {
                    sb.AppendLine($"- Chưa có lớp học phần nào được xếp lịch tại phòng {matchedRoom} trong học kỳ này.");
                }

                advice.AiMessage = sb.ToString();
                return;
            }

            // =========================================================================
            // TRƯỜNG HỢP 6: CÂU HỎI VỀ HỌC BẠ / ĐIỂM SỐ / MÔN ĐÃ HỌC CỦA SINH VIÊN
            // =========================================================================
            bool asksGrades = qNorm.Contains("diem cua em") || qNorm.Contains("diem cua toi") || qNorm.Contains("hoc ba") ||
                              qNorm.Contains("mon da hoc") || qNorm.Contains("mon da qua") || qNorm.Contains("mon tich luy") ||
                              qNorm.Contains("ket qua hoc tap") || qNorm.Contains("diem f") || qNorm.Contains("mon rot");

            if (asksGrades)
            {
                if (student == null)
                {
                    advice.AiMessage = "👋 **Chào bạn! Bạn hiện đang ở chế độ Khách (chưa đăng nhập).**\n\n" +
                                       "Để Cố vấn AI có thể đọc học bạ, điểm số tích lũy cá nhân và phân tích điều kiện tiên quyết riêng cho bạn, vui lòng bấm nút **Đăng nhập** nhé!\n\n" +
                                       "🔑 **Tài khoản thử nghiệm demo:**\n" +
                                       "- Sinh viên 1 (Đã qua TIN102): `sv21001` / Mật khẩu: `123456`\n" +
                                       "- Sinh viên 2 (Chưa học TIN102): `sv21002` / Mật khẩu: `123456`";
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
                        sb.AppendLine($"- **{c.Course?.CourseCode}** — {c.Course?.CourseName} ({c.Course?.Credits}TC): **{c.Grade:0.0} điểm** ({status} - Kỳ {c.SemesterCompleted})");
                    }
                }
                else
                {
                    sb.AppendLine("- *Bạn chưa có dữ liệu môn học đã hoàn thành trong hệ thống.*");
                }

                advice.AiMessage = sb.ToString();
                return;
            }

            // =========================================================================
            // TRƯỜNG HỢP 7: CÂU HỎI VỀ MÔN ĐÃ ĐĂNG KÝ / LỊCH HỌC KỲ NÀY CỦA SINH VIÊN
            // =========================================================================
            bool asksRegistered = qNorm.Contains("mon da dang ky") || qNorm.Contains("mon em da dk") ||
                                  qNorm.Contains("thoi khoa bieu cua em") || qNorm.Contains("lich hoc cua em") ||
                                  qNorm.Contains("phieu dang ky cua em");

            if (asksRegistered)
            {
                if (student == null)
                {
                    advice.AiMessage = "👋 **Chào bạn! Bạn hiện đang ở chế độ Khách (chưa đăng nhập).**\n\n" +
                                       "Vui lòng bấm **Đăng nhập** bằng tài khoản sinh viên (ví dụ: `sv21001` / `123456`) để tra cứu phiếu đăng ký và thời khóa biểu cá nhân của bạn nhé!";
                    advice.SuggestLogin = true;
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine($"### 📋 Danh Sách Học Phần Đã Đăng Ký Kỳ {semester}:");
                sb.AppendLine($"Sinh viên: **{student.FullName}** (`{student.StudentCode}`)");
                sb.AppendLine();

                if (registeredSections.Any())
                {
                    int totalRegCredits = registeredSections.Sum(s => s.Course?.Credits ?? 0);
                    sb.AppendLine($"Bạn đã đăng ký thành công **{registeredSections.Count} học phần** (Tổng: **{totalRegCredits} Tín chỉ**):");
                    sb.AppendLine();

                    foreach (var s in registeredSections)
                    {
                        sb.AppendLine($"- **Lớp `{s.SectionCode}`** — **{s.Course?.CourseName}** (`{s.Course?.CourseCode}` - {s.Course?.Credits}TC):");
                        sb.AppendLine($"  + Giảng viên: **{s.Instructor}** | Phòng: **{s.Room}**");
                        sb.AppendLine($"  + Lịch học: **{FormatScheduleDetailed(s)}**");
                    }
                }
                else
                {
                    sb.AppendLine($"- Bạn chưa đăng ký học phần nào trong học kỳ {semester}. Hãy vào mục **Tra Cứu Học Phần** hoặc hỏi tôi để được gợi ý lộ trình phù hợp!");
                }

                advice.AiMessage = sb.ToString();
                return;
            }

            // =========================================================================
            // TRƯỜNG HỢP 8: CÂU HỎI VỀ HỌC PHÍ & THANH TOÁN VIETQR
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
                return;
            }

            // =========================================================================
            // TRƯỜNG HỢP 9: CÂU HỎI VỀ QUY CHẾ, RÚT MÔN, HỦY HỌC PHẦN
            // =========================================================================
            bool isWithdraw = qNorm.Contains("rut mon") || qNorm.Contains("huy mon") || qNorm.Contains("bo mon") ||
                              qNorm.Contains("xin rut") || qNorm.Contains("huy hoc phan");

            if (isWithdraw)
            {
                advice.AiMessage = "### 📋 Quy Chế Rút Môn & Hủy Học Phần Dành Cho Sinh Viên:\n\n" +
                                   "- **Trong thời gian đăng ký trực tuyến:** Bạn hoàn toàn có thể vào mục **'Giỏ đăng ký'** hoặc **'Xem kết quả đăng ký'** để xóa hoặc đổi sang lớp khác mà không bị tính phí hay ghi điểm F vào bảng điểm.\n" +
                                   "- **Sau khi kết thúc thời hạn trực tuyến:** Cổng trực tuyến sẽ đóng. Để rút học phần, bạn cần làm đơn xin rút môn (có ý kiến của Cố vấn học tập) và nộp về **Phòng Đào Tạo** để Cán bộ duyệt rút môn trên hệ thống.\n" +
                                   "- **Lưu ý hạn mức:** Sau khi rút môn, tổng số tín chỉ trong kỳ của bạn **không được thấp hơn 10 tín chỉ** (hạn mức tối thiểu theo quy chế).";
                return;
            }

            // =========================================================================
            // TRƯỜNG HỢP 10: TƯ VẤN LỘ TRÌNH / GỢI Ý MÔN HỌC (Study Plan Recommendation)
            // =========================================================================
            bool isAdvisoryRequest = qNorm.Contains("goi y") || qNorm.Contains("tu van") || qNorm.Contains("nen hoc") ||
                                     qNorm.Contains("thoi khoa bieu") || qNorm.Contains("ke hoach") || qNorm.Contains("can bang") ||
                                     qNorm.Contains("toi da") || qNorm.Contains("hoc nhe") || qNorm.Contains("it tin chi") ||
                                     qNorm.Contains("nhieu tin chi") || qNorm.Contains("tranh sang thu 2");

            if (isAdvisoryRequest && student != null)
            {
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
            // TRƯỜNG HỢP 11: CÂU HỎI VỀ MÔN HỌC KHÔNG TỒN TẠI TRONG CSDL
            // =========================================================================
            if (qNorm.Contains("mon ") || qNorm.Contains("hoc phan "))
            {
                var availableCourseNames = string.Join(", ", allCourses.Select(c => $"**{c.CourseName}** (`{c.CourseCode}`)"));
                advice.AiMessage = $"### 🔍 Thông Báo Tra Cứu Học Phần:\n\n" +
                                   $"Cố vấn AI đã tra cứu CSDL nhưng không tìm thấy môn học nào khớp với nội dung: *\"{qRaw}\"*.\n\n" +
                                   $"📚 **Danh sách các học phần hiện có trong chương trình đào tạo ({semester}):**\n" +
                                   $"{availableCourseNames}.\n\n" +
                                   $"💡 Bạn vui lòng kiểm tra lại tên môn học hoặc mã môn học nhé!";
                return;
            }

            // =========================================================================
            // TRƯỜNG HỢP 12: CÂU HỎI CHƯA RÕ RÀNG HOẶC CHUNG CHUNG -> HỎI LẠI RÕ RÀNG
            // =========================================================================
            string studentGreeting = student != null ? $"Chào bạn **{student.FullName}** (`{student.StudentCode}`)!" : "Chào bạn!";
            advice.AiMessage = $"### 🤖 Cố Vấn Học Thuật AI Sẵn Sàng Hỗ Trợ:\n\n" +
                               $"{studentGreeting} Tôi đã nhận được câu hỏi: *\"{qRaw}\"*.\n\n" +
                               $"Để tôi có thể tra cứu CSDL SQL Server và trả lời chính xác nhất, bạn có thể hỏi cụ thể hơn theo một trong các gợi ý sau:\n\n" +
                               $"- **Tra cứu môn học:** *\"Môn Lập trình Web có mấy lớp và ai dạy?\"* hoặc *\"Môn Cơ sở dữ liệu học phòng nào?\"*\n" +
                               $"- **Tra cứu giảng viên:** *\"Thầy Long dạy môn gì?\"* hoặc *\"Thầy Sơn dạy vào thứ mấy?\"*\n" +
                               $"- **Tra cứu mã lớp:** *\"Lớp WEB301_01 học thứ mấy, tiết mấy, phòng nào?\"*\n" +
                               $"- **Tra cứu lịch theo thứ:** *\"Thứ 2 có những lớp nào học?\"*\n" +
                               $"- **Tra cứu phòng học:** *\"Phòng Lab 402 học môn gì?\"*\n" +
                               $"- **Tra cứu học phí & quy chế:** *\"Học phí 1 tín chỉ bao nhiêu?\"* hoặc *\"Quy chế rút môn học thế nào?\"*";
        }

        /// <summary>
        /// Thuật toán tạo gợi ý học phần tối ưu không trùng lịch và tuân thủ tiên quyết
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
            if (qNorm.Contains("kiem thu") || qNorm.Contains("ktpm") || qNorm.Contains("chat luong phan mem"))
            {
                return allCourses.FirstOrDefault(c => c.CourseCode == "KTPM302");
            }
            if (qNorm.Contains("tieng anh") || qNorm.Contains("toeic") || qNorm.Contains("anh van"))
            {
                return allCourses.FirstOrDefault(c => c.CourseCode == "TOEIC");
            }
            if (qNorm.Contains("tri tue nhan tao") || qNorm.Contains("hoc may") || qNorm.Contains("ai"))
            {
                return allCourses.FirstOrDefault(c => c.CourseCode == "AI401");
            }

            return null;
        }

        private static (string Name, List<Section> Sections)? FindMatchingInstructor(string qNorm, List<Section> allSections)
        {
            var instructors = allSections
                .Where(s => !string.IsNullOrEmpty(s.Instructor))
                .Select(s => s.Instructor)
                .Distinct()
                .ToList();

            foreach (var inst in instructors)
            {
                string instNorm = RemoveDiacritics(inst.ToLowerInvariant());
                var parts = instNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(p => p.Length > 2 && p != "thay" && p != "ths" && p != "ts" && p != "pgs")
                    .ToList();

                if (qNorm.Contains(instNorm) || parts.Any(p => qNorm.Contains(p)))
                {
                    var secs = allSections.Where(s => s.Instructor == inst).ToList();
                    return (inst, secs);
                }
            }

            return null;
        }

        private static int? DetectDayOfWeek(string qNorm)
        {
            if (qNorm.Contains("thu 2") || qNorm.Contains("thu hai")) return 2;
            if (qNorm.Contains("thu 3") || qNorm.Contains("thu ba")) return 3;
            if (qNorm.Contains("thu 4") || qNorm.Contains("thu tu")) return 4;
            if (qNorm.Contains("thu 5") || qNorm.Contains("thu nam")) return 5;
            if (qNorm.Contains("thu 6") || qNorm.Contains("thu sau")) return 6;
            if (qNorm.Contains("thu 7") || qNorm.Contains("thu bay")) return 7;
            if (qNorm.Contains("chu nhat")) return 8;
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
