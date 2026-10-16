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
using CourseRegistration.Web.Components;

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

            // Đăng ký Account UseCases (Sprint 2 - Đăng nhập & Phân quyền)
            builder.Services.AddTransient<ILoginUseCase, LoginUseCase>();

            // Đăng ký Student Portal UseCases
            builder.Services.AddTransient<ISearchSectionUseCase, SearchSectionUseCase>();
            builder.Services.AddTransient<IViewSectionUseCase, ViewSectionUseCase>();
            builder.Services.AddTransient<IViewRegistrationCartUseCase, ViewRegistrationCartUseCase>();
            builder.Services.AddTransient<IAddSectionToCartUseCase, AddSectionToCartUseCase>();
            builder.Services.AddTransient<IRemoveSectionFromCartUseCase, RemoveSectionFromCartUseCase>();
            builder.Services.AddTransient<IPlaceEnrollmentUseCase, PlaceEnrollmentUseCase>();
            builder.Services.AddTransient<IViewEnrollmentUseCase, ViewEnrollmentUseCase>();
            builder.Services.AddTransient<IViewScheduleUseCase, ViewScheduleUseCase>();

            // 5. Đăng ký Admin Portal UseCases
            builder.Services.AddTransient<IViewCoursesUseCase, ViewCoursesUseCase>();
            builder.Services.AddTransient<IAddCourseUseCase, AddCourseUseCase>();
            builder.Services.AddTransient<IViewSectionsUseCase, ViewSectionsUseCase>();
            builder.Services.AddTransient<IAddSectionUseCase, AddSectionUseCase>();
            builder.Services.AddTransient<IViewEnrollmentListUseCase, ViewEnrollmentListUseCase>();

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
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();
            app.UseAntiforgery();

            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.Run();
        }
    }
}
