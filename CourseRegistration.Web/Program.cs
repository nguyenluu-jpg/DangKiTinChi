using CourseRegistration.CoreBusiness.Services;
using CourseRegistration.CoreBusiness.Services.Interfaces;
using CourseRegistration.DataStore.SqlServer;
using Microsoft.EntityFrameworkCore;
using CourseRegistration.StateStore.DI;
using CourseRegistration.UseCases.Account;
using CourseRegistration.UseCases.AdminPortal.ManageCoursesScreen;
using CourseRegistration.UseCases.AdminPortal.ManageCoursesScreen.Interfaces;
using CourseRegistration.UseCases.AdminPortal.ManageSectionsScreen;
using CourseRegistration.UseCases.AdminPortal.ManageSectionsScreen.Interfaces;
using CourseRegistration.UseCases.AdminPortal.ViewEnrollmentListScreen;
using CourseRegistration.UseCases.AdminPortal.ManageTuitionScreen;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;
using CourseRegistration.UseCases.PluginInterfaces.StateStore;
using CourseRegistration.UseCases.PluginInterfaces.UI;
using CourseRegistration.UseCases.StudentPortal.RegistrationCartScreen;
using CourseRegistration.UseCases.StudentPortal.RegistrationCartScreen.Interfaces;
using CourseRegistration.UseCases.StudentPortal.SearchSectionScreen;
using CourseRegistration.UseCases.StudentPortal.SearchSectionScreen.Interfaces;
using CourseRegistration.UseCases.StudentPortal.ViewEnrollmentScreen;
using CourseRegistration.UseCases.StudentPortal.ViewEnrollmentScreen.Interfaces;
using CourseRegistration.UseCases.StudentPortal.ViewScheduleScreen;
using CourseRegistration.UseCases.StudentPortal.ViewScheduleScreen.Interfaces;
using CourseRegistration.UseCases.StudentPortal.ViewSectionScreen;
using CourseRegistration.UseCases.StudentPortal.ViewSectionScreen.Interfaces;
using CourseRegistration.UseCases.StudentPortal.Payment;
using CourseRegistration.Web.Components;
using CourseRegistration.Web.Services;

namespace CourseRegistration.Web
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add Razor Components & Interactive Server Components
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            // Đăng ký CoreBusiness Services 
            builder.Services.AddTransient<IEnrollmentValidationService, EnrollmentValidationService>();
            builder.Services.AddScoped<IRegistrationPeriodService, RegistrationPeriodService>();

            // Đăng ký DataStore Plugins (EF Core - Kiến trúc chuẩn eShop)
            var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
            builder.Services.AddDbContextFactory<CourseRegistrationContext>(options =>
            {
                options.UseSqlServer(connectionString);
            });
            builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<CourseRegistrationContext>>().CreateDbContext());

            builder.Services.AddScoped<ICourseRepository, CourseRepository>();
            builder.Services.AddScoped<ISectionRepository, SectionRepository>();
            builder.Services.AddScoped<IStudentRepository, StudentRepository>();
            builder.Services.AddScoped<IEnrollmentRepository, EnrollmentRepository>();
            builder.Services.AddScoped<IUserAccountRepository, UserAccountRepository>();

            // Đăng ký StateStore Plugins (Scoped: Giỏ đăng ký - K1.4)
            builder.Services.AddScoped<IRegistrationCart, RegistrationCart>();
            builder.Services.AddScoped<IRegistrationCartStateStore, RegistrationCartStateStore>();
            builder.Services.AddScoped<ICurrentUserStateStore, CurrentUserStateStore>();
            builder.Services.AddScoped<ILoginModalService, LoginModalService>();
            builder.Services.AddScoped<IChangePasswordModalService, ChangePasswordModalService>();
            builder.Services.AddScoped<AiChatSessionState>();

            // Đăng ký Account UseCases (Sprint 2 - Đăng nhập & Phân quyền)
            builder.Services.AddTransient<ILoginUseCase, LoginUseCase>();
            builder.Services.AddTransient<IChangePasswordUseCase, ChangePasswordUseCase>();

            // Đăng ký Student Portal UseCases
            builder.Services.AddTransient<ISearchSectionUseCase, SearchSectionUseCase>();
            builder.Services.AddTransient<IViewSectionUseCase, ViewSectionUseCase>();
            builder.Services.AddTransient<IViewRegistrationCartUseCase, ViewRegistrationCartUseCase>();
            builder.Services.AddTransient<IAddSectionToCartUseCase, AddSectionToCartUseCase>();
            builder.Services.AddTransient<IRemoveSectionFromCartUseCase, RemoveSectionFromCartUseCase>();
            builder.Services.AddTransient<IPlaceEnrollmentUseCase, PlaceEnrollmentUseCase>();
            builder.Services.AddTransient<IViewEnrollmentUseCase, ViewEnrollmentUseCase>();
            builder.Services.AddTransient<IViewScheduleUseCase, ViewScheduleUseCase>();
            builder.Services.AddTransient<IProcessTuitionPaymentUseCase, ProcessTuitionPaymentUseCase>();

            //Đăng ký Admin Portal UseCases
            builder.Services.AddTransient<IViewCoursesUseCase, ViewCoursesUseCase>();
            builder.Services.AddTransient<IAddCourseUseCase, AddCourseUseCase>();
            builder.Services.AddTransient<IViewSectionsUseCase, ViewSectionsUseCase>();
            builder.Services.AddTransient<IAddSectionUseCase, AddSectionUseCase>();
            builder.Services.AddTransient<IViewEnrollmentListUseCase, ViewEnrollmentListUseCase>();
            builder.Services.AddTransient<IManageTuitionUseCase, ManageTuitionUseCase>();

            //Đăng ký AI Advisor UseCase (Tính năng nâng cao theo yêu cầu đồ án)
            builder.Services.AddTransient<CourseRegistration.UseCases.StudentPortal.AiAdvisor.IGetAiAdviceUseCase, CourseRegistration.UseCases.StudentPortal.AiAdvisor.GetAiAdviceUseCase>();
            builder.Services.AddTransient<CourseRegistration.UseCases.AdminPortal.AiAdminAssistant.Interfaces.IAdminAiAssistantUseCase, CourseRegistration.UseCases.AdminPortal.AiAdminAssistant.AdminAiAssistantUseCase>();

            //Đăng ký Dịch vụ xuất báo cáo Excel & PDF (ClosedXML + QuestPDF)
            builder.Services.AddSingleton<IReportExportService, ReportExportService>();

            // Cấu hình AI IChatClient (Microsoft.Extensions.AI + Ollama / OpenAI / Gemini)
            // - API Key được đọc từ User Secrets hoặc Biến Môi Trường (an toàn tuyệt đối, không commit Git)
            // - Hỗ trợ chuyển đổi linh hoạt: "Ollama" (local miễn phí), "OpenAI" (gpt-4o-mini), "Gemini" (gemini-3.8-flash)
            // - AI:TimeoutSeconds (mặc định 45): thời gian chờ LLM trước khi tự động fallback sang động cơ tra cứu SQL Server
            var aiSection = builder.Configuration.GetSection("AI");
            var aiProvider = (aiSection["Provider"] ?? "Ollama").Trim();

            // Timeout dùng chung cho GetAiAdviceUseCase & AdminAiAssistantUseCase (không hard-code trong UseCase)
            int aiTimeoutSeconds = aiSection.GetValue<int?>("TimeoutSeconds") ?? CourseRegistration.UseCases.StudentPortal.AiAdvisor.AiAdvisorSettings.DefaultTimeoutSeconds;
            builder.Services.AddSingleton(new CourseRegistration.UseCases.StudentPortal.AiAdvisor.AiAdvisorSettings { TimeoutSeconds = aiTimeoutSeconds });

            string defaultEndpoint = "http://localhost:11434/v1";
            string defaultModel = "llama3";

            if (aiProvider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                defaultEndpoint = "https://api.openai.com/v1";
                defaultModel = "gpt-4o-mini";
            }
            else if (aiProvider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
            {
                // Endpoint tương thích chuẩn OpenAI của Google Gemini API
                defaultEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai/";
                // Lưu ý: gemini-1.5-flash đã bị Google ngừng hỗ trợ (không còn trong danh sách model của Gemini API,
                // xem https://ai.google.dev/gemini-api/docs/deprecations). Tài liệu hiện tại khuyến nghị dự án mới dùng
                // gemini-3.8-flash hoặc gemini-3.5-flash-lite. Có thể ghi đè bằng AI:ModelId.
                defaultModel = "gemini-3.8-flash";
            }

            var aiEndpoint = !string.IsNullOrWhiteSpace(aiSection["Endpoint"]) ? aiSection["Endpoint"] : defaultEndpoint;
            var aiModel = !string.IsNullOrWhiteSpace(aiSection["ModelId"]) ? aiSection["ModelId"] : defaultModel;

            var aiApiKey = builder.Configuration["AI:ApiKey"]
                           ?? Environment.GetEnvironmentVariable("AI__ApiKey")
                           ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                           ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");

            if (string.IsNullOrWhiteSpace(aiApiKey))
            {
                if (aiProvider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
                {
                    aiApiKey = "ollama-local-key";
                }
                else
                {
                    // Thiếu API key cho provider cloud: LLM chắc chắn sẽ lỗi 401 và hệ thống sẽ chạy ở chế độ fallback.
                    Console.WriteLine($"[AI Warning] Provider '{aiProvider}' cần API key nhưng chưa cấu hình (AI:ApiKey / biến môi trường AI__ApiKey, OPENAI_API_KEY, GEMINI_API_KEY). " +
                                      "Cố vấn AI sẽ chạy ở chế độ fallback tra cứu SQL Server cho tới khi cấu hình key.");
                    aiApiKey = "missing-key-fallback";
                }
            }

            try
            {
                var openAiOptions = new OpenAI.OpenAIClientOptions();
                if (!string.IsNullOrWhiteSpace(aiEndpoint) && Uri.TryCreate(aiEndpoint, UriKind.Absolute, out var endpointUri))
                {
                    openAiOptions.Endpoint = endpointUri;
                }
                var openAiClient = new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential(aiApiKey), openAiOptions);
                Microsoft.Extensions.AI.IChatClient chatClient = Microsoft.Extensions.AI.OpenAIClientExtensions.AsIChatClient(openAiClient.GetChatClient(aiModel));
                builder.Services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(chatClient);
            }
            catch (Exception ex)
            {
                // Ghi log cảnh báo an toàn mà không làm crash ứng dụng
                Console.WriteLine($"[AI Warning] Khởi tạo IChatClient Provider '{aiProvider}': {ex.Message}");
            }

            var app = builder.Build();

            // Tự động khởi tạo Database và truyền/nạp dữ liệu mẫu vào SQL Server (kiểu eShop)
            using (var scope = app.Services.CreateScope())
            {
                var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CourseRegistrationContext>>();
                using var context = contextFactory.CreateDbContext();
                DbInitializer.Initialize(context);
            }

            // Configure HTTP Request pipeline
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
                app.UseHttpsRedirection();
            }


            app.UseStaticFiles();
            app.UseAntiforgery();

            // Minimal APIs xuất báo cáo (Excel / PDF) từ dữ liệu thật
            app.MapGet("/api/reports/section-students/excel", async (int sectionId, ISectionRepository secRepo, IEnrollmentRepository enrRepo, IReportExportService exportService) =>
            {
                var section = await secRepo.GetSectionByIdAsync(sectionId);
                if (section == null) return Results.NotFound("Không tìm thấy lớp học phần.");
                var enrollments = (await enrRepo.GetEnrollmentsBySectionIdAsync(sectionId)).ToList();
                var bytes = exportService.GenerateSectionStudentsExcel(section, enrollments);
                return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"DanhSach_Lop_{section.SectionCode}.xlsx");
            });

            app.MapGet("/api/reports/section-students/pdf", async (int sectionId, ISectionRepository secRepo, IEnrollmentRepository enrRepo, IReportExportService exportService) =>
            {
                var section = await secRepo.GetSectionByIdAsync(sectionId);
                if (section == null) return Results.NotFound("Không tìm thấy lớp học phần.");
                var enrollments = (await enrRepo.GetEnrollmentsBySectionIdAsync(sectionId)).ToList();
                var bytes = exportService.GenerateSectionStudentsPdf(section, enrollments);
                return Results.File(bytes, "application/pdf", $"DanhSach_Lop_{section.SectionCode}.pdf");
            });

            app.MapGet("/api/reports/student-enrollment/pdf", async (int enrollmentId, string? studentCode, IEnrollmentRepository enrRepo, IStudentRepository stuRepo, ISectionRepository secRepo, IReportExportService exportService) =>
            {
                var enrollment = await enrRepo.GetEnrollmentByIdAsync(enrollmentId);
                if (enrollment == null) return Results.NotFound("Không tìm thấy phiếu đăng ký.");
                var student = await stuRepo.GetStudentByIdAsync(enrollment.StudentId);
                if (student == null) return Results.NotFound("Không tìm thấy sinh viên.");
                if (!string.IsNullOrWhiteSpace(studentCode) && !string.Equals(student.StudentCode, studentCode, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Problem(statusCode: 403, detail: "Từ chối truy cập: Mã sinh viên không khớp với phiếu đăng ký.");
                }
                var sections = (await enrRepo.GetRegisteredSectionsByStudentAsync(student.StudentId, enrollment.Semester)).ToList();
                var bytes = exportService.GenerateStudentEnrollmentPdf(student, enrollment, sections);
                return Results.File(bytes, "application/pdf", $"PhieuDangKy_{student.StudentCode}_{enrollment.Semester}.pdf");
            });

            app.MapGet("/api/reports/student-enrollment/excel", async (int enrollmentId, string? studentCode, IEnrollmentRepository enrRepo, IStudentRepository stuRepo, ISectionRepository secRepo, IReportExportService exportService) =>
            {
                var enrollment = await enrRepo.GetEnrollmentByIdAsync(enrollmentId);
                if (enrollment == null) return Results.NotFound("Không tìm thấy phiếu đăng ký.");
                var student = await stuRepo.GetStudentByIdAsync(enrollment.StudentId);
                if (student == null) return Results.NotFound("Không tìm thấy sinh viên.");
                if (!string.IsNullOrWhiteSpace(studentCode) && !string.Equals(student.StudentCode, studentCode, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Problem(statusCode: 403, detail: "Từ chối truy cập: Mã sinh viên không khớp với phiếu đăng ký.");
                }
                var sections = (await enrRepo.GetRegisteredSectionsByStudentAsync(student.StudentId, enrollment.Semester)).ToList();
                var bytes = exportService.GenerateStudentEnrollmentExcel(student, enrollment, sections);
                return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"ThoiKhoaBieu_{student.StudentCode}_{enrollment.Semester}.xlsx");
            });

            app.MapRazorComponents<CourseRegistration.Web.Components.App>()
                .AddInteractiveServerRenderMode();

            app.Run();
        }
    }
}
