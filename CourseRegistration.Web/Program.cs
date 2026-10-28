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

            // Cấu hình AI IChatClient (Microsoft.Extensions.AI + OpenAI/Ollama)
            // - API Key ưu tiên đọc từ User Secrets hoặc biến môi trường (không commit lên git)
            // - Hỗ trợ đầy đủ Ollama local (miễn phí) và OpenAI
            var aiSection = builder.Configuration.GetSection("AI");
            var aiEndpoint = aiSection["Endpoint"] ?? "http://localhost:11434/v1";
            var aiModel = aiSection["ModelId"] ?? "llama3";
            var aiApiKey = builder.Configuration["AI:ApiKey"]
                           ?? Environment.GetEnvironmentVariable("AI__ApiKey")
                           ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");

            if (string.IsNullOrWhiteSpace(aiApiKey))
            {
                aiApiKey = "ollama-local-key"; // Ollama local không cần phí/key
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
                Console.WriteLine($"[AI Warning] Khởi tạo IChatClient: {ex.Message}");
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

            app.MapGet("/api/reports/student-enrollment/pdf", async (int enrollmentId, IEnrollmentRepository enrRepo, IStudentRepository stuRepo, ISectionRepository secRepo, IReportExportService exportService) =>
            {
                var enrollment = await enrRepo.GetEnrollmentByIdAsync(enrollmentId);
                if (enrollment == null) return Results.NotFound("Không tìm thấy phiếu đăng ký.");
                var student = await stuRepo.GetStudentByIdAsync(enrollment.StudentId);
                if (student == null) return Results.NotFound("Không tìm thấy sinh viên.");
                var sections = (await enrRepo.GetRegisteredSectionsByStudentAsync(student.StudentId, enrollment.Semester)).ToList();
                var bytes = exportService.GenerateStudentEnrollmentPdf(student, enrollment, sections);
                return Results.File(bytes, "application/pdf", $"PhieuDangKy_{student.StudentCode}_{enrollment.Semester}.pdf");
            });

            app.MapGet("/api/reports/student-enrollment/excel", async (int enrollmentId, IEnrollmentRepository enrRepo, IStudentRepository stuRepo, ISectionRepository secRepo, IReportExportService exportService) =>
            {
                var enrollment = await enrRepo.GetEnrollmentByIdAsync(enrollmentId);
                if (enrollment == null) return Results.NotFound("Không tìm thấy phiếu đăng ký.");
                var student = await stuRepo.GetStudentByIdAsync(enrollment.StudentId);
                if (student == null) return Results.NotFound("Không tìm thấy sinh viên.");
                var sections = (await enrRepo.GetRegisteredSectionsByStudentAsync(student.StudentId, enrollment.Semester)).ToList();
                var bytes = exportService.GenerateStudentEnrollmentExcel(student, enrollment, sections);
                return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"ThoiKhoaBieu_{student.StudentCode}_{enrollment.Semester}.xlsx");
            });

            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.Run();
        }
    }
}
