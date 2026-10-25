using CourseRegistration.CoreBusiness.Models;
using Microsoft.EntityFrameworkCore;

namespace CourseRegistration.DataStore.SqlServer
{
    public class CourseRegistrationContext : DbContext
    {
        public CourseRegistrationContext(DbContextOptions<CourseRegistrationContext> options) : base(options)
        {
        }

        public DbSet<Course> Courses { get; set; } = null!;
        public DbSet<Section> Sections { get; set; } = null!;
        public DbSet<SectionSchedule> SectionSchedules { get; set; } = null!;
        public DbSet<Student> Students { get; set; } = null!;
        public DbSet<Enrollment> Enrollments { get; set; } = null!;
        public DbSet<EnrollmentLine> EnrollmentLines { get; set; } = null!;
        public DbSet<Prerequisite> Prerequisites { get; set; } = null!;
        public DbSet<CompletedCourse> CompletedCourses { get; set; } = null!;
        public DbSet<UserAccount> UserAccounts { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Cấu hình bảng Course
            modelBuilder.Entity<Course>(entity =>
            {
                entity.ToTable("Courses");
                entity.HasKey(c => c.CourseId);
                entity.Property(c => c.CourseCode).HasMaxLength(10).IsRequired();
                entity.Property(c => c.CourseName).HasMaxLength(200).IsRequired();
            });

            // Cấu hình bảng Prerequisite (Môn tiên quyết)
            modelBuilder.Entity<Prerequisite>(entity =>
            {
                entity.ToTable("Prerequisites");
                entity.HasKey(p => p.PrerequisiteId);
                entity.HasOne(p => p.Course)
                      .WithMany()
                      .HasForeignKey(p => p.CourseId)
                      .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(p => p.PrerequisiteCourse)
                      .WithMany()
                      .HasForeignKey(p => p.PrerequisiteCourseId)
                      .OnDelete(DeleteBehavior.Restrict);
            });

            // Cấu hình bảng Section (Lớp học phần)
            modelBuilder.Entity<Section>(entity =>
            {
                entity.ToTable("Sections");
                entity.HasKey(s => s.SectionId);
                entity.Property(s => s.SectionCode).HasMaxLength(20).IsRequired();
                entity.Property(s => s.Instructor).HasMaxLength(100).IsRequired();
                entity.HasOne(s => s.Course)
                      .WithMany()
                      .HasForeignKey(s => s.CourseId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // Cấu hình bảng SectionSchedule (Thời khóa biểu lớp)
            modelBuilder.Entity<SectionSchedule>(entity =>
            {
                entity.ToTable("SectionSchedules");
                entity.HasKey(sch => sch.ScheduleId);
                entity.HasOne(sch => sch.Section)
                      .WithMany(s => s.Schedules)
                      .HasForeignKey(sch => sch.SectionId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // Cấu hình bảng Student (Sinh viên)
            modelBuilder.Entity<Student>(entity =>
            {
                entity.ToTable("Students");
                entity.HasKey(s => s.StudentId);
                entity.Property(s => s.StudentCode).HasMaxLength(20).IsRequired();
                entity.Property(s => s.FullName).HasMaxLength(100).IsRequired();
            });

            // Cấu hình bảng CompletedCourse (Học phần đã tích lũy)
            modelBuilder.Entity<CompletedCourse>(entity =>
            {
                entity.ToTable("CompletedCourses");
                entity.HasKey(cc => cc.CompletedCourseId);
                entity.Property(cc => cc.Semester)
                      .HasColumnName("SemesterCompleted")
                      .HasMaxLength(20);
                entity.Property(cc => cc.Grade)
                      .HasConversion<decimal>()
                      .HasPrecision(4, 2);
                entity.HasOne(cc => cc.Student)
                      .WithMany()
                      .HasForeignKey(cc => cc.StudentId)
                      .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(cc => cc.Course)
                      .WithMany()
                      .HasForeignKey(cc => cc.CourseId)
                      .OnDelete(DeleteBehavior.Restrict);
            });

            // Cấu hình bảng Enrollment (Phiếu đăng ký)
            modelBuilder.Entity<Enrollment>(entity =>
            {
                entity.ToTable("Enrollments");
                entity.HasKey(e => e.EnrollmentId);
                entity.HasOne(e => e.Student)
                      .WithMany()
                      .HasForeignKey(e => e.StudentId)
                      .OnDelete(DeleteBehavior.Cascade);

                entity.Property(e => e.PaymentStatus).HasMaxLength(30).HasDefaultValue("Unpaid");
                entity.Property(e => e.PaymentMethod).HasMaxLength(100);
                entity.Property(e => e.TransactionReference).HasMaxLength(100);

                entity.HasMany(e => e.EnrollmentLines)
                      .WithOne()
                      .HasForeignKey(el => el.EnrollmentId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // Cấu hình bảng EnrollmentLine (Chi tiết đăng ký)
            modelBuilder.Entity<EnrollmentLine>(entity =>
            {
                entity.ToTable("EnrollmentLines");
                entity.HasKey(el => el.EnrollmentLineId);
                entity.HasOne(el => el.Section)
                      .WithMany()
                      .HasForeignKey(el => el.SectionId)
                      .OnDelete(DeleteBehavior.Restrict);
            });

            // Cấu hình bảng UserAccounts (Tài khoản người dùng & Phân quyền)
            modelBuilder.Entity<UserAccount>(entity =>
            {
                entity.ToTable("UserAccounts");
                entity.HasKey(u => u.AccountId);
                entity.Property(u => u.Username).HasMaxLength(50).IsRequired();
                entity.Property(u => u.Password).HasMaxLength(100).IsRequired();
                entity.Property(u => u.FullName).HasMaxLength(100).IsRequired();
                entity.Property(u => u.Role).HasMaxLength(20).IsRequired();
                entity.HasOne(u => u.Student)
                      .WithMany()
                      .HasForeignKey(u => u.StudentId)
                      .OnDelete(DeleteBehavior.SetNull);
            });
        }
    }
}
