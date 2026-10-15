using CourseRegistration.CoreBusiness.Services;
using CourseRegistration.CoreBusiness.Services.Interfaces;
using CourseRegistration.DataStore.SqlServer;
using CourseRegistration.StateStore.DI;
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

            // Đăng ký DataStore Plugins (Sprint 2: SQL Server thật qua Dapper)
            builder.Services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
            builder.Services.AddScoped<ICourseRepository, CourseRepository>();
            builder.Services.AddScoped<ISectionRepository, SectionRepository>();
            builder.Services.AddScoped<IStudentRepository, StudentRepository>();
            builder.Services.AddScoped<IEnrollmentRepository, EnrollmentRepository>();

            // Đăng ký StateStore Plugins (Scoped: Giỏ đăng ký - K1.4)
            builder.Services.AddScoped<IRegistrationCart, RegistrationCart>();
            builder.Services.AddScoped<IRegistrationCartStateStore, RegistrationCartStateStore>();
            builder.Services.AddScoped<ICurrentUserStateStore, CurrentUserStateStore>();

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
