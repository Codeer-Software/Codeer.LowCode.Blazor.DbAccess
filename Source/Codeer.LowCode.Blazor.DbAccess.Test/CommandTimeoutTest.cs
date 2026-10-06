using Codeer.LowCode.Blazor.DataIO.Db;
using Codeer.LowCode.Blazor.SystemSettings;

namespace Codeer.LowCode.Blazor.DbAccess.Test
{
    /// <summary>CommandTimeoutSeconds: IDbAccessor 経由の設定が DbAccessor に届き、全コマンドに付くこと。</summary>
    public class CommandTimeoutTest
    {
        [Test]
        public void インターフェース経由で設定した値が実装に届く()
        {
            IDbAccessor accessor = new DbAccessor(Array.Empty<DataSource>());
            Assert.That(accessor.CommandTimeoutSeconds, Is.EqualTo(0), "既定はドライバ既定 (0)");
            accessor.CommandTimeoutSeconds = 7;
            Assert.That(((DbAccessor)accessor).CommandTimeoutSeconds, Is.EqualTo(7), "インターフェースの既定実装ではなく DbAccessor の実装が選ばれている");
        }

        [Test]
        public async Task SQLiteではタイムアウトを設定してもクエリは普通に動く()
        {
            var file = Path.Combine(Path.GetTempPath(), $"dbaccess_timeout_{Guid.NewGuid():N}.db");
            try
            {
                await using var db = new DbAccessor(new[] { new DataSource { Name = "Main", DataSourceType = DataSourceType.SQLite, ConnectionString = $"Data Source={file}" } });
                db.CommandTimeoutSeconds = 1;
                await db.ExecuteAsync("Main", "CREATE TABLE T (Id INTEGER PRIMARY KEY, Name TEXT)", new());
                await db.ExecuteAsync("Main", "INSERT INTO T VALUES (1, 'a')", new());
                var rows = await db.QueryAsync("Main", "SELECT Name FROM T", new Dictionary<string, ParamAndRawDbTypeName>());
                Assert.That(rows.Single()["Name"], Is.EqualTo("a"));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(file)) File.Delete(file);
            }
        }

        //実 SQL Server で、WAITFOR が CommandTimeoutSeconds で打ち切られることを見る。接続文字列は環境変数 DBACCESS_MSSQL_CONNECTION
        [Test, Explicit("実 SQL Server が要る。DBACCESS_MSSQL_CONNECTION を設定して明示的に実行する")]
        public async Task SQLServerでは長いコマンドがタイムアウトで止まる()
        {
            var connection = Environment.GetEnvironmentVariable("DBACCESS_MSSQL_CONNECTION");
            if (string.IsNullOrEmpty(connection)) Assert.Ignore("DBACCESS_MSSQL_CONNECTION が未設定");
            await using var db = new DbAccessor(new[] { new DataSource { Name = "Main", DataSourceType = DataSourceType.SQLServer, ConnectionString = connection } });
            db.CommandTimeoutSeconds = 1;
            var started = DateTime.UtcNow;
            Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(async () => await db.QueryAsync("Main", "WAITFOR DELAY '00:00:05'; SELECT 1 AS N", new Dictionary<string, ParamAndRawDbTypeName>()));
            Assert.That(DateTime.UtcNow - started, Is.LessThan(TimeSpan.FromSeconds(4)));
        }
    }
}
