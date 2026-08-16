using Codeer.LowCode.Blazor.SystemSettings;
using Dapper;
using Codeer.LowCode.Blazor.DbAccess;
using System.Text.Json;

namespace Test.DBLogic
{
    /// <summary>
    /// DbAccessor.QueryAsync の行マテリアライズ統一 (Dapper dynamic → RawDbValueConverter.ReadRowsAsync) の
    /// デグレ検出テスト。読込は全モジュール・全画面が通る生命線のため、ここが割れたら即検出できるようにする。
    ///
    /// 中核は「等価性テスト」: 同じクエリを
    ///   新経路 = DbAccessor.QueryAsync (ReadRowsAsync)
    ///   旧経路 = Dapper の dynamic (QueryAsync&lt;object&gt;、統一前の実装)
    /// の両方で実行し、行数・列名・値・値の実行時型が完全一致することを実 DB (SQLite / PostgreSQL / SQL Server、
    /// Oracle は Explicit) で検証する。将来 ReadRowsAsync に手を入れて挙動が変われば、ここが割れる。
    ///
    /// 意図的な差分は PostgreSQL interval のみ (旧経路は月成分で例外 → 新経路は読める)。
    /// これも「旧経路が例外のまま / 新経路が読める」の両面を固定する (Npgsql 側の挙動が変わったら気づける)。
    /// </summary>
    public class QueryAsyncMaterializationTest
    {
        const string Lite = "Lite";
        const string Pg = "Pg";
        const string Ss = "Ss";

        // 実DBの接続文字列はテストプロジェクト直下の TestConnections.json (gitignore 対象) から取る。
        // 公開リポジトリのため平文はコミットしない。書式は TestConnections.sample.json 参照。
        // ファイルや項目が無い環境ではそのDBのテストは Ignore でスキップされる
        static string PgConn => RequireConnection("PostgreSQL");
        static string SsConn => RequireConnection("SQLServer");

        static readonly Lazy<Dictionary<string, string>?> _testConnections = new(() =>
        {
            var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestConnections.json"));
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
        });

        static string RequireConnection(string name)
        {
            var value = _testConnections.Value?.GetValueOrDefault(name);
            if (string.IsNullOrEmpty(value)) Assert.Ignore($"TestConnections.json の {name} が未設定のためスキップ (実DB接続文字列)");
            return value!;
        }

        string _sqliteFile = null!;
        readonly List<DbAccessor> _accessors = new();

        [SetUp]
        public void SetUp() => _sqliteFile = Path.Combine(Path.GetTempPath(), $"querymat_{Guid.NewGuid():N}.db");

        [TearDown]
        public void TearDown()
        {
            foreach (var a in _accessors) a.Dispose();
            _accessors.Clear();
            try { if (File.Exists(_sqliteFile)) File.Delete(_sqliteFile); } catch { /* プール解放遅延は無視 */ }
        }

        DbAccessor Sqlite()
        {
            var db = new DbAccessor(new[] { new DataSource { Name = Lite, DataSourceType = DataSourceType.SQLite, ConnectionString = $"Data Source={_sqliteFile};" } });
            _accessors.Add(db);
            return db;
        }

        DbAccessor Postgres()
        {
            var db = new DbAccessor(new[] { new DataSource { Name = Pg, DataSourceType = DataSourceType.PostgreSQL, ConnectionString = PgConn } });
            _accessors.Add(db);
            return db;
        }

        DbAccessor SqlServer()
        {
            var db = new DbAccessor(new[] { new DataSource { Name = Ss, DataSourceType = DataSourceType.SQLServer, ConnectionString = SsConn } });
            _accessors.Add(db);
            return db;
        }

        // ---- 等価性の中核ヘルパ ----

        //同じクエリを新経路(QueryAsync)と旧経路(Dapper dynamic)で読み、行数・列名(集合)・値・実行時型の完全一致を検証する
        static async Task AssertSameAsDapperDynamic(DbAccessor db, string ds, string sql)
        {
            var actual = await db.QueryAsync(ds, sql, new());

            var conn = db.GetConnection(ds);
            var expected = (await conn.QueryAsync<object>(sql, null, db.GetTransaction(ds)))
                .Select(e => (IDictionary<string, object>)e).ToList();

            actual.Count.Is(expected.Count, $"行数が旧実装と不一致: {sql}");
            for (var i = 0; i < actual.Count; i++)
            {
                //列名: 集合として一致 (列挙順は Dictionary 実装依存のため固定しない。消費側は名前引きのみ)
                actual[i].Keys.OrderBy(e => e, StringComparer.Ordinal)
                    .SequenceEqual(expected[i].Keys.OrderBy(e => e, StringComparer.Ordinal))
                    .IsTrue($"列名が旧実装と不一致: [{string.Join(",", actual[i].Keys)}] vs [{string.Join(",", expected[i].Keys)}]");

                foreach (var key in expected[i].Keys)
                {
                    AssertValueEquals(expected[i][key], actual[i][key], $"{sql} 行{i} 列{key}");
                }
            }
        }

        static void AssertValueEquals(object? expected, object? actual, string position)
        {
            if (expected == null)
            {
                actual.IsNull($"旧実装は null なのに値が入っている: {position}");
                return;
            }
            actual.IsNotNull($"旧実装は値があるのに null: {position}");
            actual!.GetType().Is(expected.GetType(), $"実行時型が旧実装と不一致: {position}");
            if (expected is byte[] expectedBytes)
            {
                ((byte[])actual).SequenceEqual(expectedBytes).IsTrue($"バイナリ値が旧実装と不一致: {position}");
                return;
            }
            actual.Is(expected, $"値が旧実装と不一致: {position}");
        }

        // ===================================================================================
        // 等価性: SQLite (テーブル読み・式・NULL・多行)
        // ===================================================================================

        [Test]
        public async Task SQLite_旧実装と完全一致_型網羅の式()
        {
            // Desc: SQLite の代表型 (INTEGER=long / REAL=double / TEXT=string / BLOB=byte[] / NULL) を含む式クエリで、
            //   新経路の結果が旧実装(Dapper dynamic)と行数・列名・値・型まで完全一致する。
            var db = Sqlite();
            await AssertSameAsDapperDynamic(db, Lite,
                "SELECT 1 AS c_int, 1.5 AS c_real, 'あいうx' AS c_text, x'0102FF' AS c_blob, NULL AS c_null");
        }

        [Test]
        public async Task SQLite_旧実装と完全一致_テーブル読みとNULL混在()
        {
            // Desc: 実テーブル読み (NULL 混在・複数行) でも旧実装と完全一致する。
            var db = Sqlite();
            await db.ExecuteAsync(Lite, "CREATE TABLE m (id INTEGER PRIMARY KEY, name TEXT, price REAL, data BLOB)", new());
            await db.ExecuteAsync(Lite, "INSERT INTO m (name, price, data) VALUES ('a', 1.5, x'01'), (NULL, NULL, NULL), ('c', 3.5, x'0203')", new());

            await AssertSameAsDapperDynamic(db, Lite, "SELECT * FROM m ORDER BY id");
        }

        [Test]
        public async Task SQLite_旧実装と完全一致_500行で欠落しない()
        {
            // Desc: 多行 (500行) でも行の欠落・値ズレがなく旧実装と一致する (ページング読みの土台)。
            var db = Sqlite();
            await AssertSameAsDapperDynamic(db, Lite,
                "WITH RECURSIVE cnt(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM cnt WHERE x<500) SELECT x, x*2 AS y, 'r' || x AS t FROM cnt");
        }

        [Test]
        public async Task SQLite_旧実装と完全一致_0行()
        {
            // Desc: 結果 0 行は空リスト (旧実装と同じ)。
            var db = Sqlite();
            await db.ExecuteAsync(Lite, "CREATE TABLE e (id INTEGER)", new());
            await AssertSameAsDapperDynamic(db, Lite, "SELECT * FROM e");
        }

        [Test]
        public async Task SQLite_重複列名は旧実装と同じく最初の列で引ける()
        {
            // Desc: 別名を付けない同名列 ("SELECT a.id, b.id ..." 相当)。旧実装(DapperRow)の名前引きは
            //   最初の列が勝つため、新経路も同じ値を返す (Dictionary は重複キーを持てないので
            //   キー数は減るが、消費側の名前引き結果は同一)。
            var db = Sqlite();
            var rows = await db.QueryAsync(Lite, "SELECT 1 AS a, 2 AS a", new());

            var conn = db.GetConnection(Lite);
            var oldRow = (IDictionary<string, object>)(await conn.QueryAsync<object>("SELECT 1 AS a, 2 AS a")).Single();

            rows.Single()["a"].Is(oldRow["a"], "重複列名の名前引き結果が旧実装と不一致");
            Convert.ToInt64(rows.Single()["a"]).Is(1L);
        }

        [Test]
        public async Task SQLite_トランザクション内の読みが自分の書き込みを見える()
        {
            // Desc: QueryAsync が GetTransaction を渡し続けていること (トランザクション中の read-your-writes)。
            //   Tx を渡し忘れる退行をすると SQLite ではロック、他 DB では別スナップショットになる。
            var db = Sqlite();
            await db.ExecuteAsync(Lite, "CREATE TABLE tx (id INTEGER)", new());

            var writer = Sqlite();
            writer.StartTransaction();
            await writer.ExecuteAsync(Lite, "INSERT INTO tx (id) VALUES (1)", new());

            var rows = await writer.QueryAsync(Lite, "SELECT COUNT(*) AS n FROM tx", new());
            Convert.ToInt32(rows.Single()["n"]).Is(1);

            await writer.DisposeAsync(); //ロールバック
        }

        // ===================================================================================
        // 等価性: PostgreSQL (実DB)
        // ===================================================================================

        [Test]
        public async Task PostgreSQL_旧実装と完全一致_型網羅の式()
        {
            // Desc: PostgreSQL の代表型 (int4/int8/numeric/float8/text/varchar/bool/date/timestamp/timestamptz/
            //   uuid/bytea/jsonb/NULL) で旧実装と完全一致する。
            var db = Postgres();
            await AssertSameAsDapperDynamic(db, Pg,
                "SELECT 1::int4 AS c_int4, 2::int8 AS c_int8, 3.50::numeric(10,2) AS c_num, 4.5::float8 AS c_f8, " +
                "'あx'::text AS c_text, 'y'::varchar(10) AS c_vc, true AS c_bool, " +
                "DATE '2024-01-02' AS c_date, TIMESTAMP '2024-01-02 03:04:05' AS c_ts, " +
                "TIMESTAMPTZ '2024-01-02 03:04:05+00' AS c_tstz, " +
                "'11111111-2222-3333-4444-555555555555'::uuid AS c_uuid, " +
                "'\\x0102'::bytea AS c_bytea, '{\"a\":1}'::jsonb AS c_jsonb, NULL::text AS c_null");
        }

        [Test]
        public async Task PostgreSQL_旧実装と完全一致_複数行()
        {
            // Desc: generate_series による複数行でも旧実装と一致する。
            var db = Postgres();
            await AssertSameAsDapperDynamic(db, Pg,
                "SELECT g AS id, 'row' || g AS name, (g * 1.5)::numeric(10,2) AS amount FROM generate_series(1, 20) AS g ORDER BY g");
        }

        [Test]
        public async Task PostgreSQL_interval月成分_旧経路は例外のまま_新経路は読める()
        {
            // Desc: 統一の動機となった「意図的な差分」の両面を固定する。
            //   旧経路(Dapper dynamic=GetValue)は月成分を持つ interval で例外になる (これが直ればこの回避策は不要になる
            //   ので、Npgsql 更新で挙動が変わったらこのテストで気づく)。新経路は月あり=PG text表記 / 月なし=TimeSpan で読める。
            var db = Postgres();

            var conn = db.GetConnection(Pg);
            Assert.CatchAsync<Exception>(async () =>
                {
                    (await conn.QueryAsync<object>("SELECT INTERVAL '1 year' AS iv")).ToList();
                },
                "旧経路(GetValue)が月成分intervalを読めるようになっている。回避策(ReadRowsAsyncのinterval特別扱い)の要否を再検討すること");

            var rows = await db.QueryAsync(Pg, "SELECT INTERVAL '1 year 2 mons 3 days 04:05:06' AS iv1, INTERVAL '02:00:00' AS iv2, NULL::interval AS iv3", new());
            rows.Single()["iv1"].Is("1 year 2 mons 3 days 04:05:06");
            rows.Single()["iv2"].Is(TimeSpan.FromHours(2));
            rows.Single()["iv3"].IsNull();
        }

        // ===================================================================================
        // 等価性: SQL Server (実DB)
        // ===================================================================================

        [Test]
        public async Task SQLServer_旧実装と完全一致_型網羅の式()
        {
            // Desc: SQL Server の代表型 (int/bigint/decimal/float/nvarchar/bit/datetime2/datetimeoffset/
            //   uniqueidentifier/varbinary/NULL) で旧実装と完全一致する。
            var db = SqlServer();
            await AssertSameAsDapperDynamic(db, Ss,
                "SELECT CAST(1 AS int) AS c_int, CAST(2 AS bigint) AS c_big, CAST(3.50 AS decimal(10,2)) AS c_dec, " +
                "CAST(4.5 AS float) AS c_f, N'あx' AS c_nv, CAST(1 AS bit) AS c_bit, " +
                "CAST('2024-01-02T03:04:05' AS datetime2) AS c_dt2, " +
                "CAST('2024-01-02T03:04:05+09:00' AS datetimeoffset) AS c_dto, " +
                "CAST('11111111-2222-3333-4444-555555555555' AS uniqueidentifier) AS c_guid, " +
                "CAST(0x0102 AS varbinary(10)) AS c_vb, CAST(NULL AS nvarchar(10)) AS c_null");
        }

        [Test]
        public async Task SQLServer_旧実装と完全一致_複数行()
        {
            // Desc: VALUES 句による複数行でも旧実装と一致する。
            var db = SqlServer();
            await AssertSameAsDapperDynamic(db, Ss,
                "SELECT v.id, v.name FROM (VALUES (1, N'a'), (2, N'b'), (3, NULL)) AS v(id, name) ORDER BY v.id");
        }

        // ===================================================================================
        // 等価性: Oracle (実機が要るため Explicit。他のOracle結合テストと同じ扱い)
        // ===================================================================================

        [Test]
        [Explicit("Oracle実機(SeleniumTestDataOracle)が必要")]
        public async Task Oracle_旧実装と完全一致_型網羅の式()
        {
            // Desc: Oracle の代表型 (NUMBER/VARCHAR2/DATE/TIMESTAMP/RAW/NULL) で旧実装と完全一致する。
            var db = new DbAccessor(new[] { new DataSource
            {
                Name = "Ora",
                DataSourceType = DataSourceType.Oracle,
                ConnectionString = RequireConnection("Oracle"),
            } });
            _accessors.Add(db);

            await AssertSameAsDapperDynamic(db, "Ora",
                "SELECT 1 AS c_num, 'x' AS c_text, DATE '2024-01-02' AS c_date, " +
                "TIMESTAMP '2024-01-02 03:04:05' AS c_ts, HEXTORAW('0102') AS c_raw, CAST(NULL AS VARCHAR2(10)) AS c_null FROM DUAL");
        }
    }
}
