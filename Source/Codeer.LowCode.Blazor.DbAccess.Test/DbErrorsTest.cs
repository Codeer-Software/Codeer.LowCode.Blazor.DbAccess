using Codeer.LowCode.Blazor.DbAccess;
using Codeer.LowCode.Blazor.SystemSettings;

namespace Test.DBLogic
{
    /// <summary>
    /// DbErrors.IsUniqueViolation: 一意制約違反の例外を DB ごとの型とコードから判定する (ここでは SQLite で実際に起こす)。
    /// </summary>
    public class DbErrorsTest
    {
        static DataSource MakeDataSource(string name) => new DataSource
        {
            Name = name,
            DataSourceType = DataSourceType.SQLite,
            ConnectionString = "Data Source=:memory:"
        };

        static async Task<Exception> InsertTwiceAsync(string ds, string table)
        {
            using var db = new DbAccessor(new[] { MakeDataSource(ds) });
            db.StartDataAccess(ds);
            await db.ExecuteAsync(ds, table, new Dictionary<string, object?>());
            await db.ExecuteAsync(ds, "INSERT INTO T (Id, Code) VALUES (1, 'a')", new Dictionary<string, object?>());
            try
            {
                await db.ExecuteAsync(ds, "INSERT INTO T (Id, Code) VALUES (2, 'a')", new Dictionary<string, object?>());
            }
            catch (Exception ex)
            {
                return ex;
            }
            throw new Exception("二度目の INSERT が通ってしまった");
        }

        [Test]
        public async Task UNIQUE制約違反は一意違反と判定される()
        {
            var ex = await InsertTwiceAsync("Unique", "CREATE TABLE T (Id INTEGER PRIMARY KEY, Code TEXT UNIQUE)");
            DbErrors.IsUniqueViolation(ex).IsTrue();
        }

        [Test]
        public async Task 主キー違反は一意違反と判定される()
        {
            using var db = new DbAccessor(new[] { MakeDataSource("Pk") });
            db.StartDataAccess("Pk");
            await db.ExecuteAsync("Pk", "CREATE TABLE T (Id INTEGER PRIMARY KEY, Code TEXT)", new Dictionary<string, object?>());
            await db.ExecuteAsync("Pk", "INSERT INTO T (Id, Code) VALUES (1, 'a')", new Dictionary<string, object?>());
            Exception? caught = null;
            try
            {
                await db.ExecuteAsync("Pk", "INSERT INTO T (Id, Code) VALUES (1, 'b')", new Dictionary<string, object?>());
            }
            catch (Exception ex)
            {
                caught = ex;
            }
            caught.IsNotNull();
            DbErrors.IsUniqueViolation(caught!).IsTrue();
        }

        [Test]
        public async Task 包んだ例外の内側も見る()
        {
            var ex = await InsertTwiceAsync("Inner", "CREATE TABLE T (Id INTEGER PRIMARY KEY, Code TEXT UNIQUE)");
            DbErrors.IsUniqueViolation(new InvalidOperationException("wrapped", ex)).IsTrue();
        }

        [Test]
        public async Task 一意違反でない例外は判定されない()
        {
            using var db = new DbAccessor(new[] { MakeDataSource("NotNull") });
            db.StartDataAccess("NotNull");
            await db.ExecuteAsync("NotNull", "CREATE TABLE T (Id INTEGER PRIMARY KEY, Code TEXT NOT NULL)", new Dictionary<string, object?>());
            Exception? caught = null;
            try
            {
                await db.ExecuteAsync("NotNull", "INSERT INTO T (Id, Code) VALUES (1, NULL)", new Dictionary<string, object?>());
            }
            catch (Exception ex)
            {
                caught = ex;
            }
            caught.IsNotNull();
            DbErrors.IsUniqueViolation(caught!).IsFalse();
            DbErrors.IsUniqueViolation(new InvalidOperationException("plain")).IsFalse();
        }
    }
}
