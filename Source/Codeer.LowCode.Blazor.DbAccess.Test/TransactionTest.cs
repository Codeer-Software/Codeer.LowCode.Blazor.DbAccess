using Codeer.LowCode.Blazor.DataIO.Db;
using Codeer.LowCode.Blazor.SystemSettings;
using Microsoft.Data.Sqlite;

namespace Codeer.LowCode.Blazor.DbAccess.Test
{
    /// <summary>StartTransaction: すでに開いている接続にも張る・Commit / Rollback の後は張り直せる。</summary>
    public class TransactionTest
    {
        string _file = string.Empty;

        [SetUp]
        public async Task SetUp()
        {
            _file = Path.Combine(Path.GetTempPath(), $"dbaccess_tran_{Guid.NewGuid():N}.db");
            await using var db = CreateAccessor();
            await db.ExecuteAsync("Main", "CREATE TABLE T (Id INTEGER PRIMARY KEY, Name TEXT)", new());
            await db.ExecuteAsync("Main", "INSERT INTO T VALUES (1, 'a')", new());
        }

        [TearDown]
        public void TearDown()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_file)) File.Delete(_file);
        }

        DbAccessor CreateAccessor()
            => new(new[] { new DataSource { Name = "Main", DataSourceType = DataSourceType.SQLite, ConnectionString = $"Data Source={_file}" } });

        async Task<string?> ReadNameAsync()
        {
            await using var db = CreateAccessor();
            var rows = await db.QueryAsync("Main", "SELECT Name FROM T WHERE Id = 1", new Dictionary<string, ParamAndRawDbTypeName>());
            return rows.Single()["Name"]?.ToString();
        }

        [Test]
        public async Task 接続を先に開いてからStartTransactionしてもRollbackで戻る()
        {
            IDbAccessor db = CreateAccessor();
            db.GetConnection("Main");
            db.StartTransaction();
            Assert.That(db.GetTransaction("Main"), Is.Not.Null, "開いている接続にもトランザクションが張られる");
            await db.ExecuteAsync("Main", "UPDATE T SET Name = 'b' WHERE Id = 1", new());
            await db.RollbackAsync();

            Assert.That(await ReadNameAsync(), Is.EqualTo("a"));
        }

        [Test]
        public async Task Commitの後にStartTransactionし直せば2回目もRollbackで戻る()
        {
            IDbAccessor db = CreateAccessor();
            db.StartTransaction();
            await db.ExecuteAsync("Main", "UPDATE T SET Name = 'b' WHERE Id = 1", new());
            await db.CommitAsync();
            Assert.That(db.GetTransaction("Main"), Is.Null, "Commit の後はトランザクションのモードを抜ける");

            db.StartTransaction();
            await db.ExecuteAsync("Main", "UPDATE T SET Name = 'c' WHERE Id = 1", new());
            await db.RollbackAsync();

            Assert.That(await ReadNameAsync(), Is.EqualTo("b"));
        }

        [Test]
        public async Task Rollbackの後はトランザクションのモードを抜ける()
        {
            await using var db = CreateAccessor();
            db.StartTransaction();
            await db.ExecuteAsync("Main", "UPDATE T SET Name = 'b' WHERE Id = 1", new());
            await ((IDbAccessor)db).RollbackAsync();

            Assert.That(db.GetTransaction("Main"), Is.Null);
            await db.ExecuteAsync("Main", "UPDATE T SET Name = 'c' WHERE Id = 1", new());
            Assert.That(await ReadNameAsync(), Is.EqualTo("c"), "自動コミットで反映される");
        }
    }
}
