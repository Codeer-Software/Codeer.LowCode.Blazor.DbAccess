using Codeer.LowCode.Blazor.DataIO.Db;
using Codeer.LowCode.Blazor.DbAccess;
using Codeer.LowCode.Blazor.Repository.Design;
using Codeer.LowCode.Blazor.SystemSettings;
using System.Data;

namespace Test.DBLogic
{
    /// <summary>
    /// DbAccessor.SqlLog (SQLデバッグログ) の検証。
    /// 設定時に各実行経路(Execute/Insert/Query/ExecuteSqlCommand)で SQL とパラメータがダンプされ、
    /// 未設定(既定)では何も出力されないこと。
    /// </summary>
    public class SqlLogTest
    {
        readonly List<string> _logs = new();

        [TearDown]
        public void TearDown() => DbAccessor.SqlLog = null;

        DataSource MakeDataSource(string name) => new DataSource
        {
            Name = name,
            DataSourceType = DataSourceType.SQLite,
            ConnectionString = "Data Source=:memory:"
        };

        DbAccessor MakeDbWithLog(string ds)
        {
            _logs.Clear();
            DbAccessor.SqlLog = _logs.Add;
            var db = new DbAccessor(new[] { MakeDataSource(ds) });
            db.StartDataAccess(ds);
            return db;
        }

        [Test]
        public async Task Execute_Query_Insert_のSQLとパラメータがダンプされる()
        {
            // Desc: ExecuteAsync/QueryAsync/InsertAsync それぞれの実行で、操作名・SQL・パラメータ名=値が
            //   1メッセージにまとまって SqlLog へ流れる。Insert は実行後に NewId もダンプされる。
            const string Ds = "LogBasic";
            using var db = MakeDbWithLog(Ds);
            await db.ExecuteAsync(Ds, "CREATE TABLE T (Id INTEGER PRIMARY KEY, Memo TEXT)", new());

            await db.ExecuteAsync(Ds,
                "INSERT INTO T (Id, Memo) VALUES (@id, @memo)",
                new Dictionary<string, object?> { ["id"] = 1, ["memo"] = "abc" });
            var execLog = _logs.Last();
            execLog.Contains($"[SQL] Execute ({Ds})").IsTrue();
            execLog.Contains("INSERT INTO T (Id, Memo) VALUES (@id, @memo)").IsTrue();
            execLog.Contains("id = 1 (Int32)").IsTrue();
            execLog.Contains("memo = \"abc\"").IsTrue();
            //SQL部は罫線で挟まれ、パラメータがSQLの続きに見えない
            execLog.Split("--------------------------------------------------").Length.Is(3);

            await db.QueryAsync(Ds, "SELECT * FROM T WHERE Id = @id",
                new Dictionary<string, ParamAndRawDbTypeName> { ["id"] = new() { Value = 1 } });
            var queryLog = _logs.Last();
            queryLog.Contains($"[SQL] Query ({Ds})").IsTrue();
            queryLog.Contains("SELECT * FROM T WHERE Id = @id").IsTrue();
            queryLog.Contains("id = 1 (Int32)").IsTrue();

            await db.InsertAsync(Ds,
                "INSERT INTO T (Id, Memo) VALUES (@id, @memo); SELECT last_insert_rowid();",
                new Dictionary<string, object?> { ["id"] = 2, ["memo"] = null });
            //Insert は「実行前のSQL+パラメータ」と「実行後の NewId」の2メッセージ
            var insertLog = _logs[^2];
            insertLog.Contains($"[SQL] Insert ({Ds})").IsTrue();
            insertLog.Contains("memo = null").IsTrue();
            var insertResultLog = _logs[^1];
            insertResultLog.Contains($"[SQL] Insert result ({Ds})").IsTrue();
            insertResultLog.Contains("NewId = \"2\"").IsTrue();
        }

        [Test]
        public async Task ExecuteSqlCommand_のパラメータ方向と実行結果がダンプされる()
        {
            // Desc: ExecuteSqlCommandAsync は「実行前: SQL+パラメータ(方向・型付き)」と
            //   「実行後: ReturnValue(+出力系パラメータ値)」の2メッセージがダンプされる。
            const string Ds = "LogCmd";
            using var db = MakeDbWithLog(Ds);
            await db.ExecuteAsync(Ds, "CREATE TABLE T (Id INTEGER PRIMARY KEY, Memo TEXT)", new());

            await db.ExecuteSqlCommandAsync(Ds, new DbSqlCommand
            {
                CommandText = "INSERT INTO T (Id, Memo) VALUES (@id, @memo)",
                MethodType = ExecuteSqlMethodType.NonQuery,
                Parameters = new List<DbSqlCommandParameter>
                {
                    new() { Name = "id", Value = 1L, NetType = typeof(long), Direction = ParameterDirection.Input },
                    new() { Name = "memo", Value = "abc", NetType = typeof(string), Direction = ParameterDirection.Input },
                },
            });

            var commandLog = _logs[^2];
            commandLog.Contains($"[SQL] ExecuteSqlCommand NonQuery ({Ds})").IsTrue();
            commandLog.Contains("INSERT INTO T (Id, Memo) VALUES (@id, @memo)").IsTrue();
            commandLog.Contains("id = 1 (Int64) (Input, Int64)").IsTrue();
            commandLog.Contains("memo = \"abc\" (Input, String)").IsTrue();

            var resultLog = _logs[^1];
            resultLog.Contains($"[SQL] ExecuteSqlCommand result ({Ds})").IsTrue();
            resultLog.Contains("ReturnValue = 1 (Int32)").IsTrue();
        }

        [Test]
        public async Task SqlLog未設定なら何もダンプされない()
        {
            // Desc: 既定(SqlLog=null)では一切ログが流れない(=既存アプリの挙動は変わらない)。
            const string Ds = "LogOff";
            _logs.Clear();
            DbAccessor.SqlLog = null;
            using var db = new DbAccessor(new[] { MakeDataSource(Ds) });
            db.StartDataAccess(Ds);

            await db.ExecuteAsync(Ds, "CREATE TABLE T (Id INTEGER PRIMARY KEY)", new());
            await db.QueryAsync(Ds, "SELECT * FROM T", new());

            _logs.Count.Is(0);
        }
    }
}
