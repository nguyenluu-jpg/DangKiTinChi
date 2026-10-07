using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;

namespace CourseRegistration.DataStore.HardCoded
{
    public class SectionRepository : ISectionRepository
    {
        private readonly ICourseRepository _courseRepository;
        private static readonly List<Section> _sections = new List<Section>();
        private static readonly List<SectionSchedule> _schedules = new List<SectionSchedule>();
        private static readonly object _lock = new object();

        public SectionRepository(ICourseRepository courseRepository)
        {
            _courseRepository = courseRepository;
            InitializeMockData();
        }

        private void InitializeMockData()
        {
            lock (_lock)
            {
                if (_sections.Any()) return;

                var courses = _courseRepository.GetAllCoursesAsync().Result.ToList();

                var cWeb = courses.FirstOrDefault(c => c.CourseCode == "WEB301");
                var cAlgo = courses.FirstOrDefault(c => c.CourseCode == "TIN201");
                var cDb = courses.FirstOrDefault(c => c.CourseCode == "CSDL201");
                var cSe = courses.FirstOrDefault(c => c.CourseCode == "KTPM302");
                var cOop = courses.FirstOrDefault(c => c.CourseCode == "TIN102");

                _sections.AddRange(new List<Section>
                {
                    // Lớp 1: Lập trình Web - Nhóm 1 (Thứ 2, tiết 1-4)
                    new Section
                    {
                        SectionId = 1,
                        SectionCode = "WEB301_01",
                        CourseId = cWeb?.CourseId ?? 5,
                        Course = cWeb,
                        Instructor = "TS. Nguyễn Văn A",
                        MaxCapacity = 50,
                        CurrentEnrollment = 25,
                        Semester = "2025_HK1",
                        Room = "Lab 402"
                    },
                    // Lớp 2: Lập trình Web - Nhóm 2 (Thứ 4, tiết 7-10)
                    new Section
                    {
                        SectionId = 2,
                        SectionCode = "WEB301_02",
                        CourseId = cWeb?.CourseId ?? 5,
                        Course = cWeb,
                        Instructor = "ThS. Trần Thị B",
                        MaxCapacity = 50,
                        CurrentEnrollment = 48,
                        Semester = "2025_HK1",
                        Room = "Lab 403"
                    },
                    // Lớp 3: Cấu trúc Dữ liệu & GT (Thứ 2, tiết 1-3) -> TRÙNG LỊCH VỚI LỚP 1 (Thứ 2 tiết 1-4) ĐỂ DEMO VI PHẠM!
                    new Section
                    {
                        SectionId = 3,
                        SectionCode = "TIN201_01",
                        CourseId = cAlgo?.CourseId ?? 4,
                        Course = cAlgo,
                        Instructor = "PGS. Lê Hoàng C",
                        MaxCapacity = 60,
                        CurrentEnrollment = 30,
                        Semester = "2025_HK1",
                        Room = "Phòng A101"
                    },
                    // Lớp 4: Cấu trúc Dữ liệu & GT (Thứ 6, tiết 2-4) -> Không trùng lịch
                    new Section
                    {
                        SectionId = 4,
                        SectionCode = "TIN201_02",
                        CourseId = cAlgo?.CourseId ?? 4,
                        Course = cAlgo,
                        Instructor = "ThS. Đỗ Minh D",
                        MaxCapacity = 60,
                        CurrentEnrollment = 20,
                        Semester = "2025_HK1",
                        Room = "Phòng A102"
                    },
                    // Lớp 5: Cơ sở Dữ liệu (Thứ 3, tiết 7-9)
                    new Section
                    {
                        SectionId = 5,
                        SectionCode = "CSDL201_01",
                        CourseId = cDb?.CourseId ?? 3,
                        Course = cDb,
                        Instructor = "TS. Phạm Thanh E",
                        MaxCapacity = 60,
                        CurrentEnrollment = 45,
                        Semester = "2025_HK1",
                        Room = "Phòng B204"
                    },
                    // Lớp 6: Công nghệ Phần mềm -> ĐÃ ĐẦY CHỖ (50/50) ĐỂ DEMO VI PHẠM LUẬT 2!
                    new Section
                    {
                        SectionId = 6,
                        SectionCode = "KTPM302_01",
                        CourseId = cSe?.CourseId ?? 6,
                        Course = cSe,
                        Instructor = "TS. Ngô Bảo F",
                        MaxCapacity = 50,
                        CurrentEnrollment = 50, // Hết chỗ
                        Semester = "2025_HK1",
                        Room = "Phòng C301"
                    },
                    // Lớp 7: Lập trình OOP (Thứ 7, tiết 1-3)
                    new Section
                    {
                        SectionId = 7,
                        SectionCode = "TIN102_01",
                        CourseId = cOop?.CourseId ?? 2,
                        Course = cOop,
                        Instructor = "ThS. Vũ Mạnh G",
                        MaxCapacity = 60,
                        CurrentEnrollment = 35,
                        Semester = "2025_HK1",
                        Room = "Phòng A205"
                    }
                });

                // Gán lịch học
                _schedules.AddRange(new List<SectionSchedule>
                {
                    new SectionSchedule { ScheduleId = 1, SectionId = 1, DayOfWeek = 2, StartPeriod = 1, EndPeriod = 4, Room = "Lab 402" },
                    new SectionSchedule { ScheduleId = 2, SectionId = 2, DayOfWeek = 4, StartPeriod = 7, EndPeriod = 10, Room = "Lab 403" },
                    new SectionSchedule { ScheduleId = 3, SectionId = 3, DayOfWeek = 2, StartPeriod = 1, EndPeriod = 3, Room = "Phòng A101" }, // Trùng thứ 2 tiết 1-3
                    new SectionSchedule { ScheduleId = 4, SectionId = 4, DayOfWeek = 6, StartPeriod = 2, EndPeriod = 4, Room = "Phòng A102" },
                    new SectionSchedule { ScheduleId = 5, SectionId = 5, DayOfWeek = 3, StartPeriod = 7, EndPeriod = 9, Room = "Phòng B204" },
                    new SectionSchedule { ScheduleId = 6, SectionId = 6, DayOfWeek = 5, StartPeriod = 2, EndPeriod = 4, Room = "Phòng C301" },
                    new SectionSchedule { ScheduleId = 7, SectionId = 7, DayOfWeek = 7, StartPeriod = 1, EndPeriod = 3, Room = "Phòng A205" }
                });

                foreach (var section in _sections)
                {
                    section.Schedules = _schedules.Where(s => s.SectionId == section.SectionId).ToList();
                }
            }
        }

        public Task<IEnumerable<Section>> GetAllSectionsAsync()
        {
            return Task.FromResult<IEnumerable<Section>>(_sections.ToList());
        }

        public Task<IEnumerable<Section>> GetSectionsBySemesterAsync(string semester)
        {
            var list = _sections.Where(s => s.Semester.Equals(semester, StringComparison.OrdinalIgnoreCase)).ToList();
            return Task.FromResult<IEnumerable<Section>>(list);
        }

        public Task<IEnumerable<Section>> SearchSectionsAsync(string keyword, string semester)
        {
            var query = _sections.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(semester))
            {
                query = query.Where(s => s.Semester.Equals(semester, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                query = query.Where(s =>
                    s.SectionCode.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    (s.Course != null && s.Course.CourseName.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                    (s.Course != null && s.Course.CourseCode.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                    s.Instructor.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            }

            return Task.FromResult<IEnumerable<Section>>(query.ToList());
        }

        public Task<Section?> GetSectionByIdAsync(int sectionId)
        {
            var section = _sections.FirstOrDefault(s => s.SectionId == sectionId);
            return Task.FromResult(section);
        }

        public Task AddSectionAsync(Section section)
        {
            lock (_lock)
            {
                section.SectionId = _sections.Any() ? _sections.Max(s => s.SectionId) + 1 : 1;
                _sections.Add(section);
            }
            return Task.CompletedTask;
        }

        public Task UpdateSectionAsync(Section section)
        {
            lock (_lock)
            {
                var existing = _sections.FirstOrDefault(s => s.SectionId == section.SectionId);
                if (existing != null)
                {
                    existing.SectionCode = section.SectionCode;
                    existing.Instructor = section.Instructor;
                    existing.MaxCapacity = section.MaxCapacity;
                    existing.Room = section.Room;
                    existing.Semester = section.Semester;
                }
            }
            return Task.CompletedTask;
        }

        public Task DeleteSectionAsync(int sectionId)
        {
            lock (_lock)
            {
                var existing = _sections.FirstOrDefault(s => s.SectionId == sectionId);
                if (existing != null)
                {
                    _sections.Remove(existing);
                }
            }
            return Task.CompletedTask;
        }

        public Task AddScheduleAsync(SectionSchedule schedule)
        {
            lock (_lock)
            {
                schedule.ScheduleId = _schedules.Any() ? _schedules.Max(s => s.ScheduleId) + 1 : 1;
                _schedules.Add(schedule);
                var sec = _sections.FirstOrDefault(s => s.SectionId == schedule.SectionId);
                if (sec != null && !sec.Schedules.Contains(schedule))
                {
                    sec.Schedules.Add(schedule);
                }
            }
            return Task.CompletedTask;
        }

        public Task DeleteScheduleAsync(int scheduleId)
        {
            lock (_lock)
            {
                var sched = _schedules.FirstOrDefault(s => s.ScheduleId == scheduleId);
                if (sched != null)
                {
                    _schedules.Remove(sched);
                    var sec = _sections.FirstOrDefault(s => s.SectionId == sched.SectionId);
                    sec?.Schedules.Remove(sched);
                }
            }
            return Task.CompletedTask;
        }

        public Task<IEnumerable<SectionSchedule>> GetSchedulesBySectionIdAsync(int sectionId)
        {
            var list = _schedules.Where(s => s.SectionId == sectionId).ToList();
            return Task.FromResult<IEnumerable<SectionSchedule>>(list);
        }

        public Task<bool> IncrementEnrollmentCountAsync(int sectionId)
        {
            lock (_lock)
            {
                var section = _sections.FirstOrDefault(s => s.SectionId == sectionId);
                if (section == null) return Task.FromResult(false);

                // Luật 2: Sĩ số không vượt giới hạn
                if (section.CurrentEnrollment >= section.MaxCapacity)
                {
                    return Task.FromResult(false);
                }

                section.CurrentEnrollment++;
                return Task.FromResult(true);
            }
        }

        public Task DecrementEnrollmentCountAsync(int sectionId)
        {
            lock (_lock)
            {
                var section = _sections.FirstOrDefault(s => s.SectionId == sectionId);
                if (section != null && section.CurrentEnrollment > 0)
                {
                    section.CurrentEnrollment--;
                }
            }
            return Task.CompletedTask;
        }
    }
}
