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

            // Tự động nâng cấp bảng Enrollments thêm các cột quản lý Học phí & Thanh toán VietQR
            try
            {
                context.Database.ExecuteSqlRaw(@"
                    IF EXISTS (SELECT * FROM sysobjects WHERE name='Enrollments' and xtype='U')
                    BEGIN
                        IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Enrollments') AND name = 'PaymentStatus')
                        BEGIN
                            ALTER TABLE [dbo].[Enrollments] ADD [PaymentStatus] NVARCHAR(30) NOT NULL CONSTRAINT DF_Enrollments_PaymentStatus DEFAULT('Unpaid');
                        END
                        IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Enrollments') AND name = 'PaymentDate')
                        BEGIN
                            ALTER TABLE [dbo].[Enrollments] ADD [PaymentDate] DATETIME2 NULL;
                        END
                        IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Enrollments') AND name = 'PaymentMethod')
                        BEGIN
                            ALTER TABLE [dbo].[Enrollments] ADD [PaymentMethod] NVARCHAR(100) NULL;
                        END
                        IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Enrollments') AND name = 'TransactionReference')
                        BEGIN
                            ALTER TABLE [dbo].[Enrollments] ADD [TransactionReference] NVARCHAR(100) NULL;
                        END
                    END
                ");
            }
            catch { }

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

            // Tự động phân bổ trạng thái học phí mẫu cho sinh viên (Có nộp và Chưa nộp)
            try
            {
                context.Database.ExecuteSqlRaw(@"
                    IF EXISTS (SELECT * FROM sysobjects WHERE name='Enrollments' and xtype='U')
                    BEGIN
                        -- Sinh viên ĐÃ nộp học phí (Paid: SV21003, SV21004, SV21007, SV21010, SV21013, SV21016, SV21019)
                        UPDATE dbo.Enrollments 
                        SET PaymentStatus = 'Paid',
                            PaymentDate = DATEADD(day, -4, GETDATE()),
                            PaymentMethod = N'VietQR Napas247',
                            TransactionReference = 'VQR202610' + RIGHT('000' + CAST(EnrollmentId AS NVARCHAR(10)), 3)
                        WHERE StudentId IN (SELECT StudentId FROM dbo.Students WHERE StudentCode IN ('SV21003', 'SV21004', 'SV21007', 'SV21010', 'SV21013', 'SV21016', 'SV21019'))
                          AND Semester = '2026_HK1';

                        -- Sinh viên CHƯA nộp học phí (Unpaid: SV21001, SV21002, SV21005, SV21006, SV21008, SV21009, SV21011...)
                        UPDATE dbo.Enrollments 
                        SET PaymentStatus = 'Unpaid',
                            PaymentDate = NULL,
                            PaymentMethod = NULL,
                            TransactionReference = NULL
                        WHERE StudentId IN (SELECT StudentId FROM dbo.Students WHERE StudentCode IN ('SV21001', 'SV21002', 'SV21005', 'SV21006', 'SV21008', 'SV21009', 'SV21011', 'SV21012', 'SV21014', 'SV21015', 'SV21017', 'SV21018', 'SV21020'))
                          AND Semester = '2026_HK1';
                    END
                ");
            }
            catch { }

            // Tự động cập nhật tên Giảng viên chính thức (không dùng A B C)
            try
            {
                context.Database.ExecuteSqlRaw(@"
                    IF EXISTS (SELECT * FROM sysobjects WHERE name='Sections' and xtype='U')
                    BEGIN
                        UPDATE [dbo].[Sections] SET [Instructor] = N'TS. Hà Ngọc Long' WHERE [SectionCode] IN ('WEB301_01', 'WEB302_01') OR [Instructor] LIKE N'%Nguyễn Văn Hùng%' OR [Instructor] LIKE N'%Nguyễn Văn A%';
                        UPDATE [dbo].[Sections] SET [Instructor] = N'ThS. Trần Thị Thanh Trúc' WHERE [SectionCode] = 'WEB301_02' OR [Instructor] LIKE N'%Trần Thị B%';
                        UPDATE [dbo].[Sections] SET [Instructor] = N'PGS.TS. Lê Hoàng Sơn' WHERE [SectionCode] = 'TIN201_01' OR [Instructor] LIKE N'%Lê Hoàng C%';
                        UPDATE [dbo].[Sections] SET [Instructor] = N'ThS. Đỗ Minh Tuấn' WHERE [SectionCode] = 'TIN201_02' OR [Instructor] LIKE N'%Đỗ Minh D%';
                        UPDATE [dbo].[Sections] SET [Instructor] = N'TS. Phạm Thanh Tùng' WHERE [SectionCode] = 'CSDL201_01' OR [Instructor] LIKE N'%Phạm Thanh E%';
                        UPDATE [dbo].[Sections] SET [Instructor] = N'TS. Ngô Bảo Châu' WHERE [SectionCode] = 'KTPM302_01' OR [Instructor] LIKE N'%Ngô Bảo F%';
                        UPDATE [dbo].[Sections] SET [Instructor] = N'ThS. Vũ Mạnh Cường' WHERE [SectionCode] = 'TIN102_01' OR [Instructor] LIKE N'%Vũ Mạnh G%';
                        UPDATE [dbo].[Sections] SET [Instructor] = N'PGS.TS. Ngô Văn Dũng' WHERE [SectionCode] = 'ATTT401_01' OR [Instructor] LIKE N'%Ngô Văn K%';
                        UPDATE [dbo].[Sections] SET [Instructor] = N'ThS. Lý Thị Mỹ Hạnh' WHERE [SectionCode] = 'TOEIC_01' OR [Instructor] LIKE N'%Lý Thị N%';
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

            // 1. Đảm bảo danh sách 20 sinh viên chính quy đầy đủ họ và tên tiếng Việt
            var targetStudents = new List<Student>
            {
                new Student { StudentCode = "SV21001", FullName = "Nguyễn Văn Tuấn", AcademicYear = "K21", Major = "Công nghệ Thông tin", MaxCreditsPerSemester = 24, Email = "tuan.nv@student.edu.vn", PhoneNumber = "0901234567" },
                new Student { StudentCode = "SV21002", FullName = "Trần Thị Mai", AcademicYear = "K21", Major = "Hệ thống Thông tin", MaxCreditsPerSemester = 20, Email = "mai.tt@student.edu.vn", PhoneNumber = "0912345678" },
                new Student { StudentCode = "SV21003", FullName = "Lê Hoàng Nam", AcademicYear = "K21", Major = "Kỹ thuật Phần mềm", MaxCreditsPerSemester = 24, Email = "nam.lh@student.edu.vn", PhoneNumber = "0923456789" },
                new Student { StudentCode = "SV21004", FullName = "Phạm Minh Anh", AcademicYear = "K21", Major = "Công nghệ Thông tin", MaxCreditsPerSemester = 20, Email = "anh.pm@student.edu.vn", PhoneNumber = "0934567890" },
                new Student { StudentCode = "SV21005", FullName = "Hoàng Đức Huy", AcademicYear = "K21", Major = "Khoa học Máy tính", MaxCreditsPerSemester = 24, Email = "huy.hd@student.edu.vn", PhoneNumber = "0945678901" },
                new Student { StudentCode = "SV21006", FullName = "Vũ Thảo Linh", AcademicYear = "K21", Major = "Hệ thống Thông tin", MaxCreditsPerSemester = 22, Email = "linh.vt@student.edu.vn", PhoneNumber = "0956789012" },
                new Student { StudentCode = "SV21007", FullName = "Đặng Quốc Bảo", AcademicYear = "K21", Major = "An toàn Thông tin", MaxCreditsPerSemester = 24, Email = "bao.dq@student.edu.vn", PhoneNumber = "0967890123" },
                new Student { StudentCode = "SV21008", FullName = "Bùi Thanh Hằng", AcademicYear = "K21", Major = "Thương mại Điện tử", MaxCreditsPerSemester = 22, Email = "hang.bt@student.edu.vn", PhoneNumber = "0978901234" },
                new Student { StudentCode = "SV21009", FullName = "Đỗ Anh Khoa", AcademicYear = "K21", Major = "Khoa học Dữ liệu", MaxCreditsPerSemester = 24, Email = "khoa.da@student.edu.vn", PhoneNumber = "0989012345" },
                new Student { StudentCode = "SV21010", FullName = "Ngô Phương Thảo", AcademicYear = "K21", Major = "Kỹ thuật Phần mềm", MaxCreditsPerSemester = 22, Email = "thao.np@student.edu.vn", PhoneNumber = "0990123456" },
                new Student { StudentCode = "SV21011", FullName = "Phan Minh Trí", AcademicYear = "K21", Major = "Mạng Máy Tính", MaxCreditsPerSemester = 24, Email = "tri.pm@student.edu.vn", PhoneNumber = "0902345678" },
                new Student { StudentCode = "SV21012", FullName = "Dương Thu Trang", AcademicYear = "K21", Major = "Trí tuệ Nhân tạo", MaxCreditsPerSemester = 22, Email = "trang.dt@student.edu.vn", PhoneNumber = "0913456789" },
                new Student { StudentCode = "SV21013", FullName = "Trịnh Gia Huy", AcademicYear = "K21", Major = "Công nghệ Thông tin", MaxCreditsPerSemester = 24, Email = "huy.tg@student.edu.vn", PhoneNumber = "0924567890" },
                new Student { StudentCode = "SV21014", FullName = "Mai Khánh Huyền", AcademicYear = "K21", Major = "Hệ thống Thông tin", MaxCreditsPerSemester = 20, Email = "huyen.mk@student.edu.vn", PhoneNumber = "0935678901" },
                new Student { StudentCode = "SV21015", FullName = "Hồ Hải Đăng", AcademicYear = "K21", Major = "Khoa học Máy tính", MaxCreditsPerSemester = 24, Email = "dang.hh@student.edu.vn", PhoneNumber = "0946789012" },
                new Student { StudentCode = "SV21016", FullName = "Lương Bảo Ngọc", AcademicYear = "K21", Major = "An toàn Thông tin", MaxCreditsPerSemester = 24, Email = "ngoc.lb@student.edu.vn", PhoneNumber = "0957890123" },
                new Student { StudentCode = "SV21017", FullName = "Tạ Quang Dũng", AcademicYear = "K21", Major = "Kỹ thuật Phần mềm", MaxCreditsPerSemester = 22, Email = "dung.tq@student.edu.vn", PhoneNumber = "0968901234" },
                new Student { StudentCode = "SV21018", FullName = "Nguyễn Ngọc Bích", AcademicYear = "K21", Major = "Khoa học Dữ liệu", MaxCreditsPerSemester = 24, Email = "bich.nn@student.edu.vn", PhoneNumber = "0979012345" },
                new Student { StudentCode = "SV21019", FullName = "Võ Hoàng Long", AcademicYear = "K21", Major = "Mạng Máy Tính", MaxCreditsPerSemester = 24, Email = "long.vh@student.edu.vn", PhoneNumber = "0980123456" },
                new Student { StudentCode = "SV21020", FullName = "Đinh Thúy Nga", AcademicYear = "K21", Major = "Công nghệ Thông tin", MaxCreditsPerSemester = 22, Email = "nga.dt@student.edu.vn", PhoneNumber = "0991234567" }
            };

            foreach (var st in targetStudents)
            {
                var existing = context.Students.FirstOrDefault(s => s.StudentCode == st.StudentCode);
                if (existing == null)
                {
                    context.Students.Add(st);
                }
                else
                {
                    existing.FullName = st.FullName;
                    existing.Major = st.Major;
                    existing.AcademicYear = st.AcademicYear;
                    existing.MaxCreditsPerSemester = st.MaxCreditsPerSemester;
                    existing.Email = st.Email;
                    existing.PhoneNumber = st.PhoneNumber;
                }
            }
            context.SaveChanges();

            // 2. Đảm bảo tài khoản người dùng Admin và Sinh viên
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
                }
            }
            context.SaveChanges();

            // 3. Đảm bảo dữ liệu môn học cơ sở
            var initCourses = new List<Course>
            {
                new Course { CourseCode = "TIN101", CourseName = "Nhập môn Lập trình", Credits = 3, Description = "Kiến thức cơ bản về lập trình C# và tư duy thuật toán" },
                new Course { CourseCode = "TIN102", CourseName = "Lập trình Hướng đối tượng", Credits = 3, Description = "Lập trình đối tượng, 4 tính chất OOP và Design Patterns cơ bản" },
                new Course { CourseCode = "CSDL201", CourseName = "Cơ sở Dữ liệu", Credits = 3, Description = "Mô hình quan hệ, chuẩn hóa 3NF, SQL và Indexing" },
                new Course { CourseCode = "TIN201", CourseName = "Cấu trúc Dữ liệu & Giải thuật", Credits = 3, Description = "Danh sách liên kết, cây nhị phân, thuật toán tìm kiếm và sắp xếp" },
                new Course { CourseCode = "WEB301", CourseName = "Lập trình Ứng dụng Web", Credits = 4, Description = "Xây dựng Web App hiện đại với Blazor Server, Clean Architecture và EF Core" },
                new Course { CourseCode = "KTPM302", CourseName = "Kỹ thuật Phần mềm", Credits = 3, Description = "Quy trình phát triển phần mềm Agile/Scrum, thiết kế UML và kiểm thử" },
                new Course { CourseCode = "ATTT401", CourseName = "An toàn Thông tin", Credits = 3, Description = "Mật mã học, bảo mật hệ thống thông tin, xác thực và an toàn mạng ứng dụng" },
                new Course { CourseCode = "TOEIC", CourseName = "Tiếng Anh chuyên ngành CNTT", Credits = 2, Description = "Thuật ngữ tiếng Anh chuyên ngành công nghệ thông tin và giao tiếp công sở" },
                // Môn học mở thêm cho Học kỳ 2 (2026_HK2)
                new Course { CourseCode = "WEB302", CourseName = "Phát triển Web Nâng cao & Microservices", Credits = 4, Description = "Kiến trúc Microservices, Docker, CI/CD, SignalR thời gian thực và Web API bảo mật" },
                new Course { CourseCode = "AI401", CourseName = "Trí tuệ Nhân tạo & Học máy", Credits = 3, Description = "Thuật toán Machine Learning, Deep Learning, Mạng nơ-ron và ứng dụng AI" },
                new Course { CourseCode = "CSDL202", CourseName = "Cơ sở Dữ liệu Phân tán & NoSQL", Credits = 3, Description = "Mô hình CSDL phân tán, MongoDB, Redis caching, tính nhất quán dữ liệu và Big Data" },
                new Course { CourseCode = "ATTT402", CourseName = "An ninh Mạng & Phòng chống Tấn công", Credits = 3, Description = "Kỹ thuật phân tích mã độc, bảo mật hạ tầng mạng, phòng chống tấn công DDoS và thâm nhập" }
            };

            foreach (var c in initCourses)
            {
                if (!context.Courses.Any(dbC => dbC.CourseCode == c.CourseCode))
                {
                    context.Courses.Add(c);
                }
            }
            context.SaveChanges();

            var dbCourses = context.Courses.ToList();
            var cTin101 = dbCourses.First(c => c.CourseCode == "TIN101");
            var cTin102 = dbCourses.First(c => c.CourseCode == "TIN102");
            var cTin201 = dbCourses.First(c => c.CourseCode == "TIN201");
            var cWeb301 = dbCourses.First(c => c.CourseCode == "WEB301");
            var cKtpm302 = dbCourses.First(c => c.CourseCode == "KTPM302");
            var cCsdl201 = dbCourses.First(c => c.CourseCode == "CSDL201");
            var cAttt401 = dbCourses.First(c => c.CourseCode == "ATTT401");
            var cToeic = dbCourses.First(c => c.CourseCode == "TOEIC");
            var cWeb302 = dbCourses.First(c => c.CourseCode == "WEB302");
            var cAi401 = dbCourses.First(c => c.CourseCode == "AI401");
            var cCsdl202 = dbCourses.First(c => c.CourseCode == "CSDL202");
            var cAttt402 = dbCourses.First(c => c.CourseCode == "ATTT402");

            // Điều kiện tiên quyết (gồm cả HK1 và HK2)
            var prereqList = new List<Prerequisite>
            {
                new Prerequisite { CourseId = cTin102.CourseId, PrerequisiteCourseId = cTin101.CourseId },
                new Prerequisite { CourseId = cTin201.CourseId, PrerequisiteCourseId = cTin101.CourseId },
                new Prerequisite { CourseId = cWeb301.CourseId, PrerequisiteCourseId = cTin102.CourseId },
                new Prerequisite { CourseId = cKtpm302.CourseId, PrerequisiteCourseId = cTin102.CourseId },
                new Prerequisite { CourseId = cWeb302.CourseId, PrerequisiteCourseId = cWeb301.CourseId },
                new Prerequisite { CourseId = cAi401.CourseId, PrerequisiteCourseId = cTin201.CourseId },
                new Prerequisite { CourseId = cCsdl202.CourseId, PrerequisiteCourseId = cCsdl201.CourseId },
                new Prerequisite { CourseId = cAttt402.CourseId, PrerequisiteCourseId = cAttt401.CourseId }
            };

            foreach (var p in prereqList)
            {
                if (!context.Prerequisites.Any(pr => pr.CourseId == p.CourseId && pr.PrerequisiteCourseId == p.PrerequisiteCourseId))
                {
                    context.Prerequisites.Add(p);
                }
            }
            context.SaveChanges();

            // 4. Môn học đã hoàn thành (CompletedCourses) cho việc kiểm thử Luật 1
            var s1 = dbStudents.FirstOrDefault(s => s.StudentCode == "SV21001");
            var s2 = dbStudents.FirstOrDefault(s => s.StudentCode == "SV21002");
            var s3 = dbStudents.FirstOrDefault(s => s.StudentCode == "SV21003");

            if (s1 != null)
            {
                if (!context.CompletedCourses.Any(c => c.StudentId == s1.StudentId && c.CourseId == cTin101.CourseId))
                    context.CompletedCourses.Add(new CompletedCourse { StudentId = s1.StudentId, CourseId = cTin101.CourseId, Semester = "2024_HK1", Grade = 8.5 });
                if (!context.CompletedCourses.Any(c => c.StudentId == s1.StudentId && c.CourseId == cTin102.CourseId))
                    context.CompletedCourses.Add(new CompletedCourse { StudentId = s1.StudentId, CourseId = cTin102.CourseId, Semester = "2024_HK2", Grade = 7.5 });
            }

            // SV21002: Chỉ đạt TIN101, CHƯA ĐẠT TIN102 -> Dùng DEMO kiểm tra Chặn Tiên Quyết
            if (s2 != null)
            {
                if (!context.CompletedCourses.Any(c => c.StudentId == s2.StudentId && c.CourseId == cTin101.CourseId))
                    context.CompletedCourses.Add(new CompletedCourse { StudentId = s2.StudentId, CourseId = cTin101.CourseId, Semester = "2025_HK1", Grade = 6.5 });
                var tin102Passed = context.CompletedCourses.FirstOrDefault(c => c.StudentId == s2.StudentId && c.CourseId == cTin102.CourseId);
                if (tin102Passed != null)
                {
                    context.CompletedCourses.Remove(tin102Passed);
                }
            }

            if (s3 != null)
            {
                if (!context.CompletedCourses.Any(c => c.StudentId == s3.StudentId && c.CourseId == cTin101.CourseId))
                    context.CompletedCourses.Add(new CompletedCourse { StudentId = s3.StudentId, CourseId = cTin101.CourseId, Semester = "2024_HK1", Grade = 7.5 });
                if (!context.CompletedCourses.Any(c => c.StudentId == s3.StudentId && c.CourseId == cTin102.CourseId))
                    context.CompletedCourses.Add(new CompletedCourse { StudentId = s3.StudentId, CourseId = cTin102.CourseId, Semester = "2024_HK2", Grade = 8.0 });
                if (!context.CompletedCourses.Any(c => c.StudentId == s3.StudentId && c.CourseId == cCsdl201.CourseId))
                    context.CompletedCourses.Add(new CompletedCourse { StudentId = s3.StudentId, CourseId = cCsdl201.CourseId, Semester = "2025_HK1", Grade = 8.5 });
            }
            context.SaveChanges();

            // 5. Lớp học phần (Sections: gồm cả 2 Học kỳ 2026_HK1 và 2026_HK2)
            var targetSections = new List<Section>
            {
                // Học kỳ 1 (2026_HK1)
                new Section { SectionCode = "WEB301_01", CourseId = cWeb301.CourseId, Instructor = "TS. Hà Ngọc Long", MaxCapacity = 50, CurrentEnrollment = 0, Semester = "2026_HK1", Room = "Lab 402" },
                new Section { SectionCode = "WEB301_02", CourseId = cWeb301.CourseId, Instructor = "ThS. Trần Thị Thanh Trúc", MaxCapacity = 50, CurrentEnrollment = 0, Semester = "2026_HK1", Room = "Lab 403" },
                new Section { SectionCode = "TIN201_01", CourseId = cTin201.CourseId, Instructor = "PGS.TS. Lê Hoàng Sơn", MaxCapacity = 60, CurrentEnrollment = 0, Semester = "2026_HK1", Room = "Phòng A101" },
                new Section { SectionCode = "TIN201_02", CourseId = cTin201.CourseId, Instructor = "ThS. Đỗ Minh Tuấn", MaxCapacity = 60, CurrentEnrollment = 0, Semester = "2026_HK1", Room = "Phòng A102" },
                new Section { SectionCode = "CSDL201_01", CourseId = cCsdl201.CourseId, Instructor = "TS. Phạm Thanh Tùng", MaxCapacity = 60, CurrentEnrollment = 0, Semester = "2026_HK1", Room = "Phòng B204" },
                new Section { SectionCode = "KTPM302_01", CourseId = cKtpm302.CourseId, Instructor = "TS. Ngô Bảo Châu", MaxCapacity = 10, CurrentEnrollment = 10, Semester = "2026_HK1", Room = "Phòng C301" }, // Đủ sĩ số (10/10) để demo chặn hết chỗ
                new Section { SectionCode = "TIN102_01", CourseId = cTin102.CourseId, Instructor = "ThS. Vũ Mạnh Cường", MaxCapacity = 60, CurrentEnrollment = 0, Semester = "2026_HK1", Room = "Phòng A205" },
                new Section { SectionCode = "ATTT401_01", CourseId = cAttt401.CourseId, Instructor = "PGS.TS. Ngô Văn Dũng", MaxCapacity = 40, CurrentEnrollment = 0, Semester = "2026_HK1", Room = "Lab 501" },
                new Section { SectionCode = "TOEIC_01", CourseId = cToeic.CourseId, Instructor = "ThS. Lý Thị Mỹ Hạnh", MaxCapacity = 80, CurrentEnrollment = 0, Semester = "2026_HK1", Room = "Hội trường A" },

                // Học kỳ 2 (2026_HK2)
                new Section { SectionCode = "WEB302_01", CourseId = cWeb302.CourseId, Instructor = "TS. Hà Ngọc Long", MaxCapacity = 50, CurrentEnrollment = 0, Semester = "2026_HK2", Room = "Lab 402" },
                new Section { SectionCode = "AI401_01", CourseId = cAi401.CourseId, Instructor = "PGS.TS. Lê Hoàng Sơn", MaxCapacity = 60, CurrentEnrollment = 0, Semester = "2026_HK2", Room = "Lab 502" },
                new Section { SectionCode = "CSDL202_01", CourseId = cCsdl202.CourseId, Instructor = "TS. Phạm Thanh Tùng", MaxCapacity = 60, CurrentEnrollment = 0, Semester = "2026_HK2", Room = "Phòng B301" },
                new Section { SectionCode = "ATTT402_01", CourseId = cAttt402.CourseId, Instructor = "PGS.TS. Ngô Văn Dũng", MaxCapacity = 40, CurrentEnrollment = 0, Semester = "2026_HK2", Room = "Lab 501" },
                new Section { SectionCode = "KTPM302_02", CourseId = cKtpm302.CourseId, Instructor = "TS. Ngô Bảo Châu", MaxCapacity = 60, CurrentEnrollment = 0, Semester = "2026_HK2", Room = "Phòng C302" },
                new Section { SectionCode = "TOEIC_02", CourseId = cToeic.CourseId, Instructor = "ThS. Lý Thị Mỹ Hạnh", MaxCapacity = 80, CurrentEnrollment = 0, Semester = "2026_HK2", Room = "Hội trường B" }
            };

            foreach (var sec in targetSections)
            {
                var existing = context.Sections.FirstOrDefault(s => s.SectionCode == sec.SectionCode);
                if (existing == null)
                {
                    context.Sections.Add(sec);
                }
                else
                {
                    existing.Instructor = sec.Instructor;
                    existing.MaxCapacity = sec.MaxCapacity;
                    existing.Room = sec.Room;
                    existing.Semester = sec.Semester;
                }
            }
            context.SaveChanges();

            // 6. Lịch học của lớp (SectionSchedules)
            var dbSections = context.Sections.ToList();
            var secWeb01 = dbSections.First(s => s.SectionCode == "WEB301_01");
            var secWeb02 = dbSections.First(s => s.SectionCode == "WEB301_02");
            var secAlgo01 = dbSections.First(s => s.SectionCode == "TIN201_01");
            var secAlgo02 = dbSections.First(s => s.SectionCode == "TIN201_02");
            var secDb01 = dbSections.First(s => s.SectionCode == "CSDL201_01");
            var secKtpm01 = dbSections.First(s => s.SectionCode == "KTPM302_01");
            var secOop01 = dbSections.First(s => s.SectionCode == "TIN102_01");
            var secAttt01 = dbSections.First(s => s.SectionCode == "ATTT401_01");
            var secToeic01 = dbSections.First(s => s.SectionCode == "TOEIC_01");

            var secWeb302_01 = dbSections.FirstOrDefault(s => s.SectionCode == "WEB302_01");
            var secAi401_01 = dbSections.FirstOrDefault(s => s.SectionCode == "AI401_01");
            var secDb202_01 = dbSections.FirstOrDefault(s => s.SectionCode == "CSDL202_01");
            var secAttt402_01 = dbSections.FirstOrDefault(s => s.SectionCode == "ATTT402_01");
            var secKtpm302_02 = dbSections.FirstOrDefault(s => s.SectionCode == "KTPM302_02");
            var secToeic02 = dbSections.FirstOrDefault(s => s.SectionCode == "TOEIC_02");

            var targetSchedules = new List<SectionSchedule>
            {
                // Lịch học HK1
                new SectionSchedule { SectionId = secWeb01.SectionId, DayOfWeek = 2, StartPeriod = 1, EndPeriod = 4, Room = "Lab 402" },
                new SectionSchedule { SectionId = secWeb02.SectionId, DayOfWeek = 4, StartPeriod = 7, EndPeriod = 10, Room = "Lab 403" },
                new SectionSchedule { SectionId = secAlgo01.SectionId, DayOfWeek = 2, StartPeriod = 1, EndPeriod = 3, Room = "Phòng A101" }, // Trùng thứ 2 tiết 1-3 với WEB301_01
                new SectionSchedule { SectionId = secAlgo02.SectionId, DayOfWeek = 6, StartPeriod = 2, EndPeriod = 4, Room = "Phòng A102" },
                new SectionSchedule { SectionId = secDb01.SectionId, DayOfWeek = 3, StartPeriod = 7, EndPeriod = 9, Room = "Phòng B204" },
                new SectionSchedule { SectionId = secKtpm01.SectionId, DayOfWeek = 5, StartPeriod = 1, EndPeriod = 3, Room = "Phòng C301" },
                new SectionSchedule { SectionId = secOop01.SectionId, DayOfWeek = 7, StartPeriod = 1, EndPeriod = 3, Room = "Phòng A205" },
                new SectionSchedule { SectionId = secAttt01.SectionId, DayOfWeek = 6, StartPeriod = 7, EndPeriod = 9, Room = "Lab 501" },
                new SectionSchedule { SectionId = secToeic01.SectionId, DayOfWeek = 7, StartPeriod = 1, EndPeriod = 3, Room = "Hội trường A" }
            };

            // Lịch học HK2
            if (secWeb302_01 != null) targetSchedules.Add(new SectionSchedule { SectionId = secWeb302_01.SectionId, DayOfWeek = 3, StartPeriod = 1, EndPeriod = 4, Room = "Lab 402" });
            if (secAi401_01 != null) targetSchedules.Add(new SectionSchedule { SectionId = secAi401_01.SectionId, DayOfWeek = 4, StartPeriod = 1, EndPeriod = 3, Room = "Lab 502" });
            if (secDb202_01 != null) targetSchedules.Add(new SectionSchedule { SectionId = secDb202_01.SectionId, DayOfWeek = 5, StartPeriod = 7, EndPeriod = 9, Room = "Phòng B301" });
            if (secAttt402_01 != null) targetSchedules.Add(new SectionSchedule { SectionId = secAttt402_01.SectionId, DayOfWeek = 6, StartPeriod = 1, EndPeriod = 3, Room = "Lab 501" });
            if (secKtpm302_02 != null) targetSchedules.Add(new SectionSchedule { SectionId = secKtpm302_02.SectionId, DayOfWeek = 2, StartPeriod = 7, EndPeriod = 9, Room = "Phòng C302" });
            if (secToeic02 != null) targetSchedules.Add(new SectionSchedule { SectionId = secToeic02.SectionId, DayOfWeek = 7, StartPeriod = 7, EndPeriod = 9, Room = "Hội trường B" });

            foreach (var sch in targetSchedules)
            {
                if (!context.SectionSchedules.Any(s => s.SectionId == sch.SectionId && s.DayOfWeek == sch.DayOfWeek && s.StartPeriod == sch.StartPeriod))
                {
                    context.SectionSchedules.Add(sch);
                }
            }
            context.SaveChanges();

            // 7. BỔ SUNG DỮ LIỆU ĐĂNG KÝ HỌC PHẦN (ENROLLMENTS & ENROLLMENTLINES) CHO MỌI LỚP
            // Đảm bảo mỗi lớp đều có danh sách sinh viên đăng ký thực tế trong CSDL
            if (context.EnrollmentLines.Count() < 10)
            {
                // Danh sách phân bổ môn đăng ký cho từng sinh viên:
                // Map: StudentCode -> danh sách SectionCode
                var studentEnrollmentPlans = new Dictionary<string, List<string>>
                {
                    ["SV21001"] = new List<string> { "WEB301_01", "CSDL201_01", "ATTT401_01", "TOEIC_01" },
                    ["SV21002"] = new List<string> { "CSDL201_01", "TIN102_01", "TOEIC_01" },
                    ["SV21003"] = new List<string> { "WEB301_01", "TIN201_02", "KTPM302_01", "TOEIC_01" },
                    ["SV21004"] = new List<string> { "TIN201_01", "CSDL201_01", "TIN102_01", "TOEIC_01" },
                    ["SV21005"] = new List<string> { "WEB301_01", "TIN201_02", "KTPM302_01", "TOEIC_01" },
                    ["SV21006"] = new List<string> { "TIN201_01", "CSDL201_01", "TIN102_01", "TOEIC_01" },
                    ["SV21007"] = new List<string> { "WEB301_01", "CSDL201_01", "ATTT401_01", "KTPM302_01" },
                    ["SV21008"] = new List<string> { "WEB301_01", "TIN201_01", "KTPM302_01", "TOEIC_01" },
                    ["SV21009"] = new List<string> { "WEB301_01", "TIN201_02", "KTPM302_01", "TOEIC_01" },
                    ["SV21010"] = new List<string> { "WEB301_01", "TIN201_01", "ATTT401_01", "KTPM302_01" },
                    ["SV21011"] = new List<string> { "WEB301_02", "TIN201_02", "KTPM302_01" },
                    ["SV21012"] = new List<string> { "WEB301_02", "TIN201_01", "KTPM302_01" },
                    ["SV21013"] = new List<string> { "WEB301_01", "TIN201_02", "KTPM302_01" },
                    ["SV21014"] = new List<string> { "WEB301_02", "CSDL201_01", "TOEIC_01" },
                    ["SV21015"] = new List<string> { "WEB301_02", "TIN201_02", "CSDL201_01" },
                    ["SV21016"] = new List<string> { "WEB301_02", "CSDL201_01", "ATTT401_01" },
                    ["SV21017"] = new List<string> { "WEB301_02", "TIN102_01", "TOEIC_01" },
                    ["SV21018"] = new List<string> { "TIN201_01", "TIN102_01", "CSDL201_01" },
                    ["SV21019"] = new List<string> { "TIN201_01", "TIN102_01", "TOEIC_01" },
                    ["SV21020"] = new List<string> { "CSDL201_01", "TIN102_01", "TOEIC_01" }
                };

                var paidStudentCodes = new HashSet<string> { "SV21003", "SV21004", "SV21007", "SV21010", "SV21013", "SV21016", "SV21019" };

                int index = 1;
                foreach (var entry in studentEnrollmentPlans)
                {
                    var student = dbStudents.FirstOrDefault(s => s.StudentCode == entry.Key);
                    if (student == null) continue;

                    var isPaid = paidStudentCodes.Contains(entry.Key);

                    // Kiểm tra sinh viên đã có phiếu đăng ký Confirmed trong kỳ 2026_HK1 chưa
                    var existingEnrollment = context.Enrollments
                        .Include(e => e.EnrollmentLines)
                        .FirstOrDefault(e => e.StudentId == student.StudentId && e.Semester == "2026_HK1" && e.Status == "Confirmed");

                    if (existingEnrollment == null)
                    {
                        var newEnrollment = new Enrollment
                        {
                            StudentId = student.StudentId,
                            DateRegistered = DateTime.Now.AddDays(-15).AddHours(index),
                            Semester = "2026_HK1",
                            Status = "Confirmed",
                            UniqueId = $"ENR-2026-{index:D4}",
                            PaymentStatus = isPaid ? "Paid" : "Unpaid",
                            PaymentDate = isPaid ? DateTime.Now.AddDays(-5).AddHours(index) : null,
                            PaymentMethod = isPaid ? "VietQR Napas247" : null,
                            TransactionReference = isPaid ? $"VQR202610{index:D4}" : null
                        };
                        context.Enrollments.Add(newEnrollment);
                        context.SaveChanges();

                        foreach (var secCode in entry.Value)
                        {
                            var sec = dbSections.FirstOrDefault(s => s.SectionCode == secCode);
                            if (sec != null)
                            {
                                context.EnrollmentLines.Add(new EnrollmentLine
                                {
                                    EnrollmentId = newEnrollment.EnrollmentId,
                                    SectionId = sec.SectionId
                                });
                            }
                        }
                    }
                    else
                    {
                        // Cập nhật trạng thái học phí nếu chưa đồng bộ
                        if (isPaid && existingEnrollment.PaymentStatus != "Paid")
                        {
                            existingEnrollment.PaymentStatus = "Paid";
                            existingEnrollment.PaymentDate = DateTime.Now.AddDays(-5).AddHours(index);
                            existingEnrollment.PaymentMethod = "VietQR Napas247";
                            existingEnrollment.TransactionReference = $"VQR202610{index:D4}";
                        }

                        // Nếu đã có phiếu nhưng chưa có lines, bổ sung lines
                        foreach (var secCode in entry.Value)
                        {
                            var sec = dbSections.FirstOrDefault(s => s.SectionCode == secCode);
                            if (sec != null && !existingEnrollment.EnrollmentLines.Any(l => l.SectionId == sec.SectionId))
                            {
                                context.EnrollmentLines.Add(new EnrollmentLine
                                {
                                    EnrollmentId = existingEnrollment.EnrollmentId,
                                    SectionId = sec.SectionId
                                });
                            }
                        }
                    }
                    index++;
                }
                context.SaveChanges();
            }

            // Đồng bộ sĩ số CurrentEnrollment của từng lớp khớp chuẩn 100% với số lượng sinh viên đã đăng ký
            try
            {
                context.Database.ExecuteSqlRaw(@"
                    UPDATE s
                    SET s.CurrentEnrollment = ISNULL((
                        SELECT COUNT(DISTINCT e.StudentId) 
                        FROM dbo.EnrollmentLines el 
                        INNER JOIN dbo.Enrollments e ON el.EnrollmentId = e.EnrollmentId 
                        WHERE el.SectionId = s.SectionId AND e.Status = 'Confirmed'
                    ), 0)
                    FROM dbo.Sections s;
                ");
            }
            catch { }
        }
    }
}
