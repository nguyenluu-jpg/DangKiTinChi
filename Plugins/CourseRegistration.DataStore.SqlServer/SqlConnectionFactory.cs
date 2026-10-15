using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace CourseRegistration.DataStore.SqlServer
{
    public interface ISqlConnectionFactory
    {
        IDbConnection CreateConnection();
        string ConnectionString { get; }
    }

    public class SqlConnectionFactory : ISqlConnectionFactory
    {
        private readonly string _connectionString;

        public SqlConnectionFactory(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Không tìm thấy chuỗi kết nối 'DefaultConnection' trong cấu hình.");
        }

        public string ConnectionString => _connectionString;

        public IDbConnection CreateConnection()
        {
            var connection = new SqlConnection(_connectionString);
            return connection;
        }
    }
}
