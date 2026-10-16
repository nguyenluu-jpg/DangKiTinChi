using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;
using Microsoft.EntityFrameworkCore;

namespace CourseRegistration.DataStore.SqlServer
{
    public class UserAccountRepository : IUserAccountRepository
    {
        private readonly IDbContextFactory<CourseRegistrationContext> _contextFactory;

        public UserAccountRepository(IDbContextFactory<CourseRegistrationContext> contextFactory)
        {
            _contextFactory = contextFactory;
        }

        public async Task<UserAccount?> GetByUsernameAsync(string username)
        {
            using var context = _contextFactory.CreateDbContext();
            return await context.UserAccounts
                .AsNoTracking()
                .Include(u => u.Student)
                .FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower());
        }

        public async Task<UserAccount?> AuthenticateAsync(string username, string password)
        {
            using var context = _contextFactory.CreateDbContext();
            return await context.UserAccounts
                .AsNoTracking()
                .Include(u => u.Student)
                .FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower() && u.Password == password);
        }

        public async Task<UserAccount?> GetByIdAsync(int accountId)
        {
            using var context = _contextFactory.CreateDbContext();
            return await context.UserAccounts
                .AsNoTracking()
                .Include(u => u.Student)
                .FirstOrDefaultAsync(u => u.AccountId == accountId);
        }

        public async Task<IEnumerable<UserAccount>> GetAllAccountsAsync()
        {
            using var context = _contextFactory.CreateDbContext();
            return await context.UserAccounts
                .AsNoTracking()
                .Include(u => u.Student)
                .OrderBy(u => u.Username)
                .ToListAsync();
        }
    }
}
