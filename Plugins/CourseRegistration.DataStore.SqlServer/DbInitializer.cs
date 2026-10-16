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

            // Nâng cấp dữ liệu học kỳ sang Năm học 2026 - 2027 (2026_HK1)
            try
            {
                context.Database.ExecuteSqlRaw(@"
                    IF EXISTS (SELECT * FROM sysobjects WHERE name='Sections' and xtype='U')
                    BEGIN
                        UPDATE [dbo].[Sections] SET [Semester] = '2026_HK1' WHERE [Semester] = '2025_HK1';
                    END
                    IF EXISTS (SELECT * FROM sysobjects WHERE name='Enrollments' and xtype='U')
                    BEGIN
                        UPDATE [dbo].[Enrollments] SET [Semester] = '2026_HK1' WHERE [Semester] = '2025_HK1';
                    END
                ");
            }
            catch { }

            // Tự động chuyển đổi SV22002 -> SV21002 và sv22002 -> sv21002 nếu có dữ liệu cũ
            try
            {
                context.Database.ExecuteSqlRaw(@"
                    IF EXISTS (SELECT * FROM [dbo].[Students] WHERE [StudentCode] = 'SV22002')
                    BEGIN
                        UPDATE [dbo].[Students] SET [StudentCode] = 'SV21002', [AcademicYear] = 'K21' WHERE [StudentCode] = 'SV22002';
                    END
                    IF EXISTS (SELECT * FROM [dbo].[UserAccounts] WHERE [Username] = 'sv22002')
                    BEGIN
                        UPDATE [dbo].[UserAccounts] SET [Username] = 'sv21002' WHERE [Username] = 'sv22002';
                    END
                ");
            }
            catch { }

            // Đảm bảo tên cột trong bảng CompletedCourses là SemesterCompleted
            try
            {
                context.Database.ExecuteSqlRaw(@"
                    IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.CompletedCourses') AND name = 'Semester')
                       AND NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.CompletedCourses') AND name = 'SemesterCompleted')
                    BEGIN
                        EXEC sp_rename 'dbo.CompletedCourses.Semester', 'SemesterCompleted', 'COLUMN';
                    END
                ");
            }
            catch { }

            // 1. Đảm bảo danh sách 7 sinh viên tuần tự (SV21001 đến SV21007)
            var targetStudents = new List<Student>
            {
                new Student { StudentCode = "SV21001", FullName = "Nguyễn Văn Tuấn", AcademicYear = "K21", Major = "Công nghệ Thông tin", MaxCreditsPerSemester = 24, Email = "tuan.nv@student.edu.vn", PhoneNumber = "0901234567" },
                new Student { StudentCode = "SV21002", FullName = "Trần Thị Mai", AcademicYear = "K21", Major = "Hệ thống Thông tin", MaxCreditsPerSemester = 20, Email = "mai.tt@student.edu.vn", PhoneNumber = "0912345678" },
                new Student { StudentCode = "SV21003", FullName = "Lê Hoàng Nam", AcademicYear = "K21", Major = "Kỹ thuật Phần mềm", MaxCreditsPerSemester = 24, Email = "nam.lh@student.edu.vn", PhoneNumber = "0923456789" },
                new Student { StudentCode = "SV21004", FullName = "Phạm Minh Anh", AcademicYear = "K21", Major = "Công nghệ Thông tin", MaxCreditsPerSemester = 20, Email = "anh.pm@student.edu.vn", PhoneNumber = "0934567890" },
                new Student { StudentCode = "SV21005", FullName = "Hoàng Đức Huy", AcademicYear = "K21", Major = "Khoa học Máy tính", MaxCreditsPerSemester = 24, Email = "huy.hd@student.edu.vn", PhoneNumber = "0945678901" },
                new Student { StudentCode = "SV21006", FullName = "Vũ Thảo Linh", AcademicYear = "K21", Major = "Hệ thống Thông tin", MaxCreditsPerSemester = 22, Email = "linh.vt@student.edu.vn", PhoneNumber = "0956789012" },
                new Student { StudentCode = "SV21007", FullName = "Đặng Quốc Bảo", AcademicYear = "K21", Major = "An toàn Thông tin", MaxCreditsPerSemester = 24, Email = "bao.dq@student.edu.vn", PhoneNumber = "0967890123" }
            };

            foreach (var st in targetStudents)
            {
                if (!context.Students.Any(s => s.StudentCode == st.StudentCode))
                {
                    context.Students.Add(st);
                }
            }
            context.SaveChanges();

            // 2. Đảm bảo tài khoản người dùng Admin và 7 Sinh viên (sv21001 đến sv21007)
            if (!context.UserAccounts.Any(a => a.Username == "admin"))
            {
                context.UserAccounts.Add(new UserAccount
                {
                    Username = "admin",
                    Password = "admin123",
                    FullName = "Cán bộ Quản trị Đào tạo",
                    Role = "Admin",
                    StudentId = null
                });
            }

            var dbStudents = context.Students.ToList();
            foreach (var st in dbStudents)
            {
                var username = st.StudentCode.ToLower();
                var acc = context.UserAccounts.FirstOrDefault(a => a.Username == username);
                if (acc == null)
                {
                    context.UserAccounts.Add(new UserAccount
                    {
                        Username = username,
                        Password = "123456",
                        FullName = st.FullName,
                        Role = "Student",
                        StudentId = st.StudentId
                    });
                }
                else
                {
                    acc.StudentId = st.StudentId;
                    acc.FullName = st.FullName;
                    acc.Password = "123456";
                }
            }
            context.SaveChanges();

            // 3. Đảm bảo dữ liệu môn học cơ sở nếu chưa có
            if (!context.Courses.Any())
            {
                var initCourses = new List<Course>
                {
                    new Course { CourseCode = "TIN101", CourseName = "Cơ sở lập trình", Credits = 3, Description = "Kiến thức cơ bản về lập trình C# và tư duy thuật toán" },
                    new Course { CourseCode = "TIN102", CourseName = "Lập trình Hướng đối tượng", Credits = 3, Description = "Lập trình đối tượng, 4 tính chất OOP và Design Patterns cơ bản" },
                    new Course { CourseCode = "CSDL201", CourseName = "Cơ sở dữ liệu", Credits = 3, Description = "Mô hình quan hệ, chuẩn hóa 3NF, SQL và Indexing" },
                    new Course { CourseCode = "TIN201", CourseName = "Cấu trúc Dữ liệu & Giải thuật", Credits = 3, Description = "Danh sách liên kết, cây nhị phân, thuật toán tìm kiếm và sắp xếp" },
                    new Course { CourseCode = "WEB301", CourseName = "Lập trình Ứng dụng Web", Credits = 4, Description = "Xây dựng Web App hiện đại với Blazor Server, Clean Architecture và EF Core" },
                    new Course { CourseCode = "KTPM302", CourseName = "Công nghệ Phần mềm & Scrum", Credits = 3, Description = "Quy trình phát triển phần mềm Agile/Scrum, thiết kế UML và kiểm thử" }
                };
                context.Courses.AddRange(initCourses);
                context.SaveChanges();

                var cTin101 = initCourses.First(c => c.CourseCode == "TIN101");
                var cTin102 = initCourses.First(c => c.CourseCode == "TIN102");
                var cTin201 = initCourses.First(c => c.CourseCode == "TIN201");
                var cWeb301 = initCourses.First(c => c.CourseCode == "WEB301");
                var cKtpm302 = initCourses.First(c => c.CourseCode == "KTPM302");

                var initPrereqs = new List<Prerequisite>
                {
                    new Prerequisite { CourseId = cTin102.CourseId, PrerequisiteCourseId = cTin101.CourseId },
                    new Prerequisite { CourseId = cTin201.CourseId, PrerequisiteCourseId = cTin101.CourseId },
                    new Prerequisite { CourseId = cWeb301.CourseId, PrerequisiteCourseId = cTin102.CourseId },
                    new Prerequisite { CourseId = cKtpm302.CourseId, PrerequisiteCourseId = cTin102.CourseId }
                };
                context.Prerequisites.AddRange(initPrereqs);
                context.SaveChanges();
            }

            // 4. Đảm bảo môn học đã hoàn thành (CompletedCourses) cho việc kiểm thử Luật 1
            var dbTin101 = context.Courses.FirstOrDefault(c => c.CourseCode == "TIN101");
            var dbTin102 = context.Courses.FirstOrDefault(c => c.CourseCode == "TIN102");

            if (dbTin101 != null && dbTin102 != null)
            {
                var s1 = dbStudents.FirstOrDefault(s => s.StudentCode == "SV21001");
                var s2 = dbStudents.FirstOrDefault(s => s.StudentCode == "SV21002");

                // SV21001: Đã đạt TIN101 và TIN102
                if (s1 != null)
                {
                    if (!context.CompletedCourses.Any(c => c.StudentId == s1.StudentId && c.CourseId == dbTin101.CourseId))
                    {
                        context.CompletedCourses.Add(new CompletedCourse { StudentId = s1.StudentId, CourseId = dbTin101.CourseId, Semester = "2024_HK1", Grade = 8.5 });
                    }
                    if (!context.CompletedCourses.Any(c => c.StudentId == s1.StudentId && c.CourseId == dbTin102.CourseId))
                    {
                        context.CompletedCourses.Add(new CompletedCourse { StudentId = s1.StudentId, CourseId = dbTin102.CourseId, Semester = "2024_HK2", Grade = 7.5 });
                    }
                }

                // SV21002: Chỉ đạt TIN101, CHƯA ĐẠT TIN102 -> Dùng DEMO kiểm tra Chặn Tiên Quyết
                if (s2 != null)
                {
                    if (!context.CompletedCourses.Any(c => c.StudentId == s2.StudentId && c.CourseId == dbTin101.CourseId))
                    {
                        context.CompletedCourses.Add(new CompletedCourse { StudentId = s2.StudentId, CourseId = dbTin101.CourseId, Semester = "2025_HK1", Grade = 9.0 });
                    }
                    // Đảm bảo SV21002 KHÔNG có TIN102
                    var tin102Passed = context.CompletedCourses.FirstOrDefault(c => c.StudentId == s2.StudentId && c.CourseId == dbTin102.CourseId);
                    if (tin102Passed != null)
                    {
                        context.CompletedCourses.Remove(tin102Passed);
                    }
                }
                context.SaveChanges();
            }

            // 5. Lớp học phần (Sections) nếu chưa có
            if (!context.Sections.Any())
            {
                var dbCourses = context.Courses.ToList();
                var cWeb301 = dbCourses.FirstOrDefault(c => c.CourseCode == "WEB301");
                var cTin201 = dbCourses.FirstOrDefault(c => c.CourseCode == "TIN201");
                var cCsdl201 = dbCourses.FirstOrDefault(c => c.CourseCode == "CSDL201");
                var cKtpm302 = dbCourses.FirstOrDefault(c => c.CourseCode == "KTPM302");
                var cTin102 = dbCourses.FirstOrDefault(c => c.CourseCode == "TIN102");

                if (cWeb301 != null && cTin201 != null && cCsdl201 != null && cKtpm302 != null && cTin102 != null)
                {
                    var sections = new List<Section>
                    {
                        new Section
                        {
                            SectionCode = "WEB301_01",
                            CourseId = cWeb301.CourseId,
                            Instructor = "TS. Nguyễn Văn A",
                            MaxCapacity = 50,
                            CurrentEnrollment = 25,
                            Semester = "2026_HK1",
                            Room = "Lab 402"
                        },
                        new Section
                        {
                            SectionCode = "WEB301_02",
                            CourseId = cWeb301.CourseId,
                            Instructor = "ThS. Trần Thị B",
                            MaxCapacity = 50,
                            CurrentEnrollment = 48,
                            Semester = "2026_HK1",
                            Room = "Lab 403"
                        },
                        new Section
                        {
                            SectionCode = "TIN201_01",
                            CourseId = cTin201.CourseId,
                            Instructor = "PGS. Lê Hoàng C",
                            MaxCapacity = 60,
                            CurrentEnrollment = 30,
                            Semester = "2026_HK1",
                            Room = "Phòng A101"
                        },
                        new Section
                        {
                            SectionCode = "TIN201_02",
                            CourseId = cTin201.CourseId,
                            Instructor = "ThS. Đỗ Minh D",
                            MaxCapacity = 60,
                            CurrentEnrollment = 20,
                            Semester = "2026_HK1",
                            Room = "Phòng A102"
                        },
                        new Section
                        {
                            SectionCode = "CSDL201_01",
                            CourseId = cCsdl201.CourseId,
                            Instructor = "TS. Phạm Thanh E",
                            MaxCapacity = 60,
                            CurrentEnrollment = 45,
                            Semester = "2026_HK1",
                            Room = "Phòng B204"
                        },
                        new Section
                        {
                            SectionCode = "KTPM302_01",
                            CourseId = cKtpm302.CourseId,
                            Instructor = "TS. Ngô Bảo F",
                            MaxCapacity = 50,
                            CurrentEnrollment = 50, // Lớp đã đầy chỗ (50/50) để kiểm tra quy tắc sĩ số
                            Semester = "2026_HK1",
                            Room = "Phòng C301"
                        },
                        new Section
                        {
                            SectionCode = "TIN102_01",
                            CourseId = cTin102.CourseId,
                            Instructor = "ThS. Vũ Mạnh G",
                            MaxCapacity = 60,
                            CurrentEnrollment = 35,
                            Semester = "2026_HK1",
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
    }
}
