using Codeer.LowCode.Blazor.SystemSettings;
using Microsoft.Data.Sqlite;
using System.Data;
using Codeer.LowCode.Blazor.DbAccess;

namespace Test.DBLogic
{
    /// <summary>
    /// null パラメータが DB に送られるとき、DBNull.Value として正しくバインドされることを確認。
    ///
    /// 検証経路:
    ///   - DbAccessor.ExecuteAsync (Dapper 経由) — QueryField の WHERE 等で間接的に通る経路
    ///   - DbAccessor.QueryAsync   (Dapper 経由) — QueryField の SELECT で通る経路
    ///   - 生 ADO.NET (DbCommand.CreateParameter) — ExecuteSqlField の経路
    ///
    /// 現状の実装では:
    ///   - DbAccessor の CreateParameter が `dst.Add(name, null)` のまま渡しているので
    ///     Dapper が DbType を推論できず Npgsql/SQLite 等のドライバが SqliteException を投げる
    ///   - ExecuteSqlFieldDataIO が `para.Value = null` のまま設定するので InvalidOperationException
    /// </summary>
    public class DbAccessorNullParameterTest
    {
        // SQLite :memory: は接続単位なので、テストごとに一意 DataSource 名にして DbAccessor 生存中に接続を維持する
        DataSource MakeDataSource(string name) => new DataSource
        {
            Name = name,
            DataSourceType = DataSourceType.SQLite,
            ConnectionString = "Data Source=:memory:"
        };

        async Task CreateTableAsync(DbAccessor db, string ds)
        {
            await db.ExecuteAsync(ds,
                "CREATE TABLE T (Id INTEGER PRIMARY KEY, Memo TEXT)",
                new Dictionary<string, object?>());
        }

        [Test]
        public async Task DbAccessor_ExecuteAsync_nullパラメータがDBNullとしてバインドされる()
        {
            // Desc: Dapper 経由 (DbAccessor.ExecuteAsync) で null パラメータを渡したとき、
            //   INSERT が成功し DB 上 NULL として保存される。失敗する場合は SqliteException 等が投げられる。
            using var db = new DbAccessor(new[] { MakeDataSource("NullExec") });
            db.StartDataAccess("NullExec");
            await CreateTableAsync(db, "NullExec");

            await db.ExecuteAsync("NullExec",
                "INSERT INTO T (Id, Memo) VALUES (@id, @memo)",
                new Dictionary<string, object?> { ["id"] = 1, ["memo"] = null });

            var rows = await db.QueryAsync("NullExec", "SELECT Id FROM T WHERE Memo IS NULL", new());
            rows.Count.Is(1);
        }

        [Test]
        public async Task DbAccessor_QueryAsync_nullパラメータがDBNullとしてバインドされる()
        {
            // Desc: Dapper 経由 (DbAccessor.QueryAsync) で null パラメータを WHERE に渡せる。
            //   COALESCE で null を扱うクエリで動作確認 (パラメータ未設定だと Sqlite が例外)。
            using var db = new DbAccessor(new[] { MakeDataSource("NullQuery") });
            db.StartDataAccess("NullQuery");
            await CreateTableAsync(db, "NullQuery");

            await db.ExecuteAsync("NullQuery",
                "INSERT INTO T (Id, Memo) VALUES (@id, @memo)",
                new Dictionary<string, object?> { ["id"] = 1, ["memo"] = "x" });

            // @memo に null を渡し COALESCE で 'fallback' に置換 → Memo = 'x' とは一致しない → 0件
            var rows = await db.QueryAsync("NullQuery",
                "SELECT Id FROM T WHERE Memo = COALESCE(@memo, 'fallback')",
                new Dictionary<string, Codeer.LowCode.Blazor.DataIO.Db.ParamAndRawDbTypeName>
                {
                    ["memo"] = new() { Value = null, RawDbTypeName = "text" }
                });
            rows.Count.Is(0);
        }

        [Test]
        public async Task 生DbCommand_nullパラメータはDBNullで渡す必要がある()
        {
            // Desc: ExecuteSqlField 経路 (= conn.CreateCommand → cmd.CreateParameter → para.Value = ...) で
            //   `para.Value = null` のまま実行すると IDbDataParameter は「パラメータ未設定」とみなし、
            //   SQLite では SqliteException で失敗する。ExecuteSqlFieldDataIO では `?? DBNull.Value` で
            //   明示的に DBNull に変換する必要があり、修正後のロジックを再現したテスト。
            using var db = new DbAccessor(new[] { MakeDataSource("NullRaw") });
            db.StartDataAccess("NullRaw");
            await CreateTableAsync(db, "NullRaw");

            var conn = db.GetConnection("NullRaw");
            using (var insert = conn.CreateCommand())
            {
                insert.CommandText = "INSERT INTO T (Id, Memo) VALUES (@id, @memo)";

                var pId = insert.CreateParameter();
                pId.ParameterName = "@id";
                pId.Value = 1;
                insert.Parameters.Add(pId);

                var pMemo = insert.CreateParameter();
                pMemo.ParameterName = "@memo";
                // ExecuteSqlFieldDataIO の修正後と同等の処理: null は DBNull.Value で渡す。
                object? typedValue = null;
                pMemo.Value = typedValue ?? (object)DBNull.Value;
                insert.Parameters.Add(pMemo);

                await insert.ExecuteNonQueryAsync();
            }

            var rows = await db.QueryAsync("NullRaw", "SELECT Id FROM T WHERE Memo IS NULL", new());
            rows.Count.Is(1);
        }
    }
}
