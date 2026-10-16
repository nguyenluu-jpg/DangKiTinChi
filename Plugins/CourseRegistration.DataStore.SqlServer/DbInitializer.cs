using CourseRegistration.CoreBusiness.Models;
using Microsoft.EntityFrameworkCore;

namespace CourseRegistration.DataStore.SqlServer
{
    public static class DbInitializer
    {
        public static void Initialize(CourseRegistrationContext context)
        {
            // Tự động tạo cơ sở dữ liệu và bảng nếu chưa tồn tại (như eShop)
            context.Database.EnsureCreated();

            // Tự động tạo bảng UserAccounts trong SQL Server nếu chưa có
            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='UserAccounts' and xtype='U')
                BEGIN
                    CREATE TABLE [dbo].[UserAccounts] (
                        [AccountId] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                        [Username] NVARCHAR(50) NOT NULL UNIQUE,
                        [Password] NVARCHAR(100) NOT NULL,
                        [FullName] NVARCHAR(100) NOT NULL,
                        [Role] NVARCHAR(20) NOT NULL,
                        [StudentId] INT NULL,
                        CONSTRAINT [FK_UserAccounts_Students] FOREIGN KEY ([StudentId]) REFERENCES [dbo].[Students]([StudentId]) ON DELETE SET NULL
                    );
                END
            ");

            // Nạp dữ liệu tài khoản người dùng mẫu nếu chưa có
            if (!context.UserAccounts.Any())
            {
                var accStudent1 = context.Students.FirstOrDefault(s => s.StudentCode == "SV21001");
                var accStudent2 = context.Students.FirstOrDefault(s => s.StudentCode == "SV22002");

                var defaultAccounts = new List<UserAccount>
                {
                    new UserAccount
                    {
                        Username = "admin",
                        Password = "admin123",
                        FullName = "Cán bộ Quản trị Đào tạo",
                        Role = "Admin",
                        StudentId = null
                    },
                    new UserAccount
                    {
                        Username = "sv21001",
                        Password = "123456",
                        FullName = accStudent1?.FullName ?? "Nguyễn Văn Tuấn",
                        Role = "Student",
                        StudentId = accStudent1?.StudentId ?? 1
                    },
                    new UserAccount
                    {
                        Username = "sv22002",
                        Password = "123456",
                        FullName = accStudent2?.FullName ?? "Trần Thị Mai",
                        Role = "Student",
                        StudentId = accStudent2?.StudentId ?? 2
                    }
                };
                context.UserAccounts.AddRange(defaultAccounts);
                context.SaveChanges();
            }

            // Nếu đã có dữ liệu môn học thì không cần khởi tạo lại
            if (context.Courses.Any())
            {
                return;
            }

            // 1. Dữ liệu môn học (Courses)
            var courses = new List<Course>
            {
                new Course { CourseCode = "TIN101", CourseName = "Cơ sở lập trình", Credits = 3, Description = "Kiến thức cơ bản về lập trình C# và tư duy thuật toán" },
                new Course { CourseCode = "TIN102", CourseName = "Lập trình Hướng đối tượng", Credits = 3, Description = "Lập trình đối tượng, 4 tính chất OOP và Design Patterns cơ bản" },
                new Course { CourseCode = "CSDL201", CourseName = "Cơ sở dữ liệu", Credits = 3, Description = "Mô hình quan hệ, chuẩn hóa 3NF, SQL và Indexing" },
                new Course { CourseCode = "TIN201", CourseName = "Cấu trúc Dữ liệu & Giải thuật", Credits = 3, Description = "Danh sách liên kết, cây nhị phân, thuật toán tìm kiếm và sắp xếp" },
                new Course { CourseCode = "WEB301", CourseName = "Lập trình Ứng dụng Web", Credits = 4, Description = "Xây dựng Web App hiện đại với Blazor Server, Clean Architecture và EF Core" },
                new Course { CourseCode = "KTPM302", CourseName = "Công nghệ Phần mềm & Scrum", Credits = 3, Description = "Quy trình phát triển phần mềm Agile/Scrum, thiết kế UML và kiểm thử" }
            };
            context.Courses.AddRange(courses);
            context.SaveChanges();

            // 2. Môn tiên quyết (Prerequisites)
            var cTin101 = courses.First(c => c.CourseCode == "TIN101");
            var cTin102 = courses.First(c => c.CourseCode == "TIN102");
            var cTin201 = courses.First(c => c.CourseCode == "TIN201");
            var cWeb301 = courses.First(c => c.CourseCode == "WEB301");
            var cKtpm302 = courses.First(c => c.CourseCode == "KTPM302");
            var cCsdl201 = courses.First(c => c.CourseCode == "CSDL201");

            var prerequisites = new List<Prerequisite>
            {
                new Prerequisite { CourseId = cTin102.CourseId, PrerequisiteCourseId = cTin101.CourseId },
                new Prerequisite { CourseId = cTin201.CourseId, PrerequisiteCourseId = cTin101.CourseId },
                new Prerequisite { CourseId = cWeb301.CourseId, PrerequisiteCourseId = cTin102.CourseId },
                new Prerequisite { CourseId = cKtpm302.CourseId, PrerequisiteCourseId = cTin102.CourseId }
            };
            context.Prerequisites.AddRange(prerequisites);
            context.SaveChanges();

            // 3. Sinh viên mẫu (Students)
            var students = new List<Student>
            {
                new Student
                {
                    StudentCode = "SV21001",
                    FullName = "Nguyễn Văn Tuấn",
                    Email = "tuan.nv@student.edu.vn",
                    PhoneNumber = "0901234567",
                    AcademicYear = "K21",
                    Major = "Công nghệ Thông tin",
                    MaxCreditsPerSemester = 24
                },
                new Student
                {
                    StudentCode = "SV22002",
                    FullName = "Trần Thị Mai",
                    Email = "mai.tt@student.edu.vn",
                    PhoneNumber = "0912345678",
                    AcademicYear = "K22",
                    Major = "Hệ thống Thông tin",
                    MaxCreditsPerSemester = 20
                }
            };
            context.Students.AddRange(students);
            context.SaveChanges();

            // 4. Môn học đã hoàn thành của sinh viên (CompletedCourses)
            var s1 = students.First(s => s.StudentCode == "SV21001");
            var s2 = students.First(s => s.StudentCode == "SV22002");

            var completedCourses = new List<CompletedCourse>
            {
                // SV 1 đã học xong TIN101 và TIN102 -> Đủ điều kiện đăng ký WEB301, KTPM302
                new CompletedCourse { StudentId = s1.StudentId, CourseId = cTin101.CourseId, Semester = "2023_HK1", Grade = 8.5 },
                new CompletedCourse { StudentId = s1.StudentId, CourseId = cTin102.CourseId, Semester = "2023_HK2", Grade = 7.5 },

                // SV 2 mới chỉ học TIN101 (chưa học TIN102) -> Test vi phạm tiên quyết khi đăng ký WEB301
                new CompletedCourse { StudentId = s2.StudentId, CourseId = cTin101.CourseId, Semester = "2024_HK1", Grade = 9.0 }
            };
            context.CompletedCourses.AddRange(completedCourses);
            context.SaveChanges();

            // 5. Lớp học phần (Sections)
            var sections = new List<Section>
            {
                new Section
                {
                    SectionCode = "WEB301_01",
                    CourseId = cWeb301.CourseId,
                    Instructor = "TS. Nguyễn Văn A",
                    MaxCapacity = 50,
                    CurrentEnrollment = 25,
                    Semester = "2025_HK1",
                    Room = "Lab 402"
                },
                new Section
                {
                    SectionCode = "WEB301_02",
                    CourseId = cWeb301.CourseId,
                    Instructor = "ThS. Trần Thị B",
                    MaxCapacity = 50,
                    CurrentEnrollment = 48,
                    Semester = "2025_HK1",
                    Room = "Lab 403"
                },
                new Section
                {
                    SectionCode = "TIN201_01",
                    CourseId = cTin201.CourseId,
                    Instructor = "PGS. Lê Hoàng C",
                    MaxCapacity = 60,
                    CurrentEnrollment = 30,
                    Semester = "2025_HK1",
                    Room = "Phòng A101"
                },
                new Section
                {
                    SectionCode = "TIN201_02",
                    CourseId = cTin201.CourseId,
                    Instructor = "ThS. Đỗ Minh D",
                    MaxCapacity = 60,
                    CurrentEnrollment = 20,
                    Semester = "2025_HK1",
                    Room = "Phòng A102"
                },
                new Section
                {
                    SectionCode = "CSDL201_01",
                    CourseId = cCsdl201.CourseId,
                    Instructor = "TS. Phạm Thanh E",
                    MaxCapacity = 60,
                    CurrentEnrollment = 45,
                    Semester = "2025_HK1",
                    Room = "Phòng B204"
                },
                new Section
                {
                    SectionCode = "KTPM302_01",
                    CourseId = cKtpm302.CourseId,
                    Instructor = "TS. Ngô Bảo F",
                    MaxCapacity = 50,
                    CurrentEnrollment = 50, // Lớp đã đầy chỗ (50/50) để kiểm tra quy tắc sĩ số
                    Semester = "2025_HK1",
                    Room = "Phòng C301"
                },
                new Section
                {
                    SectionCode = "TIN102_01",
                    CourseId = cTin102.CourseId,
                    Instructor = "ThS. Vũ Mạnh G",
                    MaxCapacity = 60,
                    CurrentEnrollment = 35,
                    Semester = "2025_HK1",
                    Room = "Phòng A205"
                }
            };
            context.Sections.AddRange(sections);
            context.SaveChanges();

            // 6. Lịch học của lớp (SectionSchedules)
            var secWeb01 = sections.First(s => s.SectionCode == "WEB301_01");
            var secWeb02 = sections.First(s => s.SectionCode == "WEB301_02");
            var secAlgo01 = sections.First(s => s.SectionCode == "TIN201_01");
            var secAlgo02 = sections.First(s => s.SectionCode == "TIN201_02");
            var secDb01 = sections.First(s => s.SectionCode == "CSDL201_01");
            var secKtpm01 = sections.First(s => s.SectionCode == "KTPM302_01");
            var secOop01 = sections.First(s => s.SectionCode == "TIN102_01");

            var schedules = new List<SectionSchedule>
            {
                new SectionSchedule { SectionId = secWeb01.SectionId, DayOfWeek = 2, StartPeriod = 1, EndPeriod = 4, Room = "Lab 402" },
                new SectionSchedule { SectionId = secWeb02.SectionId, DayOfWeek = 4, StartPeriod = 7, EndPeriod = 10, Room = "Lab 403" },
                new SectionSchedule { SectionId = secAlgo01.SectionId, DayOfWeek = 2, StartPeriod = 1, EndPeriod = 3, Room = "Phòng A101" }, // Trùng thứ 2 tiết 1-3 với WEB301_01
                new SectionSchedule { SectionId = secAlgo02.SectionId, DayOfWeek = 6, StartPeriod = 2, EndPeriod = 4, Room = "Phòng A102" },
                new SectionSchedule { SectionId = secDb01.SectionId, DayOfWeek = 3, StartPeriod = 7, EndPeriod = 9, Room = "Phòng B204" },
                new SectionSchedule { SectionId = secKtpm01.SectionId, DayOfWeek = 5, StartPeriod = 2, EndPeriod = 4, Room = "Phòng C301" },
                new SectionSchedule { SectionId = secOop01.SectionId, DayOfWeek = 7, StartPeriod = 1, EndPeriod = 3, Room = "Phòng A205" }
            };
            context.SectionSchedules.AddRange(schedules);
            context.SaveChanges();
        }
    }
}
