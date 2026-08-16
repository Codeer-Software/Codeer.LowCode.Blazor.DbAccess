using Codeer.LowCode.Blazor.SystemSettings;
using NpgsqlTypes;
using Oracle.ManagedDataAccess.Types;
using Codeer.LowCode.Blazor.DbAccess;

namespace Test.DBLogic
{
    /// <summary>
    /// Codeer.LowCode.Blazor.DbAccess の RawDbValueConverter(未解決DB固有型の既定解決)を
    /// DbAccessor.ConvertFieldValueToDbValue (public virtual) 経由で検証する。
    /// DataSource 設定のリストだけで構築でき、DB接続は不要。
    ///
    /// 既定解決の契約:
    ///   - Oracle INTERVAL YEAR [TO MONTH]: 総月数(long解釈) → OracleIntervalYM。null は NOT NULL 列既定値 (0,0)
    ///   - PostgreSQL interval: TimeSpan → NpgsqlIntervalValue ラップ(bind時に NpgsqlDbType.Interval を明示するため)。
    ///     月成分を含む文字列("1 year 2 mons ...")は TimeSpan で表現できないため NpgsqlInterval にする。
    ///     null は NOT NULL 列既定値 TimeSpan.Zero
    ///   - 上記以外の型名・DataSourceType・存在しないデータソースは素通し
    /// </summary>
    public class RawDbValueConverterTest
    {
        DbAccessor _db = null!;

        [SetUp]
        public void SetUp()
            => _db = new DbAccessor(
            [
                new DataSource { Name = "Ora", DataSourceType = DataSourceType.Oracle },
                new DataSource { Name = "Pg", DataSourceType = DataSourceType.PostgreSQL },
                new DataSource { Name = "Ms", DataSourceType = DataSourceType.SQLServer },
            ]);

        [TearDown]
        public void TearDown() => _db.Dispose();

        // ===== Oracle: INTERVAL YEAR TO MONTH =====

        [TestCase("INTERVAL YEAR TO MONTH")]
        [TestCase("INTERVAL YEAR(2) TO MONTH")] // 精度付きでも基底型名で解決される
        public void Oracle_IntervalYearToMonth_総月数がOracleIntervalYMに変換される(string rawTypeName)
        {
            var ret = _db.ConvertFieldValueToDbValue("Ora", rawTypeName, 14m);
            var interval = (OracleIntervalYM)ret!;
            interval.Years.Is(1);
            interval.Months.Is(2);
        }

        [Test]
        public void Oracle_IntervalYearToMonth_nullはNOTNULL列既定値のゼロ間隔()
        {
            var interval = (OracleIntervalYM)_db.ConvertFieldValueToDbValue("Ora", "INTERVAL YEAR TO MONTH", null)!;
            interval.Years.Is(0);
            interval.Months.Is(0);
        }

        [Test]
        public void Oracle_IntervalYearToMonth_負の総月数()
        {
            var interval = (OracleIntervalYM)_db.ConvertFieldValueToDbValue("Ora", "INTERVAL YEAR TO MONTH", -14m)!;
            interval.Years.Is(-1);
            interval.Months.Is(-2);
        }

        [Test]
        public void Oracle_IntervalYearToMonth_ConvertToInt64可能な文字列も変換できる()
        {
            var interval = (OracleIntervalYM)_db.ConvertFieldValueToDbValue("Ora", "INTERVAL YEAR TO MONTH", "25")!;
            interval.Years.Is(2);
            interval.Months.Is(1);
        }

        [Test]
        public void Oracle_対象外の型名は素通し()
        {
            _db.ConvertFieldValueToDbValue("Ora", "NUMBER", 5m).Is(5m);
            _db.ConvertFieldValueToDbValue("Ora", "NUMBER", null).IsNull();
        }

        // ===== PostgreSQL: interval =====

        [Test]
        public void Postgres_Interval_TimeSpanは内部型にラップされる()
        {
            var src = new TimeSpan(1, 30, 0);
            var ret = _db.ConvertFieldValueToDbValue("Pg", "interval", src);
            ((RawDbValueConverter.NpgsqlIntervalValue)ret!).Value.Is(src);
        }

        [Test]
        public void Postgres_Interval_文字列はTimeSpanにパースされる()
        {
            var ret = _db.ConvertFieldValueToDbValue("Pg", "interval", "1:30:00");
            ((RawDbValueConverter.NpgsqlIntervalValue)ret!).Value.Is(new TimeSpan(1, 30, 0));
        }

        [Test]
        public void Postgres_Interval_nullはNOTNULL列既定値のゼロ間隔()
        {
            var ret = _db.ConvertFieldValueToDbValue("Pg", "interval", null);
            ((RawDbValueConverter.NpgsqlIntervalValue)ret!).Value.Is(TimeSpan.Zero);
        }

        [Test]
        public void Postgres_型名は大文字小文字を区別しない()
        {
            var src = new TimeSpan(0, 5, 0);
            var ret = _db.ConvertFieldValueToDbValue("Pg", "INTERVAL", src);
            ((RawDbValueConverter.NpgsqlIntervalValue)ret!).Value.Is(src);
        }

        [Test]
        public void Postgres_Interval_月成分を含む文字列はNpgsqlIntervalになる()
        {
            var interval = (NpgsqlInterval)_db.ConvertFieldValueToDbValue("Pg", "interval", "1 year 2 mons 3 days 04:05:06")!;
            interval.Months.Is(14);
            interval.Days.Is(3);
            interval.Time.Is((4 * 3600 + 5 * 60 + 6) * 1_000_000L); //マイクロ秒
        }

        [Test]
        public void Postgres_Interval_月だけの文字列()
        {
            var interval = (NpgsqlInterval)_db.ConvertFieldValueToDbValue("Pg", "interval", "2 mons")!;
            interval.Months.Is(2);
            interval.Days.Is(0);
            interval.Time.Is(0L);
        }

        [Test]
        public void Postgres_Interval_負の月成分()
        {
            //PostgreSQLのtext表記("-1 years -2 mons")をそのまま受ける
            var interval = (NpgsqlInterval)_db.ConvertFieldValueToDbValue("Pg", "interval", "-1 years -2 mons")!;
            interval.Months.Is(-14);
        }

        [Test]
        public void Postgres_Interval_月成分がなければ従来どおりTimeSpanラップ()
        {
            var ret = _db.ConvertFieldValueToDbValue("Pg", "interval", "3 days 04:05:06");
            ((RawDbValueConverter.NpgsqlIntervalValue)ret!).Value.Is(new TimeSpan(3, 4, 5, 6));
        }

        [Test]
        public void Postgres_Interval_解釈できない文字列は従来どおりFormatException()
            => Assert.Throws<FormatException>(() => _db.ConvertFieldValueToDbValue("Pg", "interval", "abc"));

        [Test]
        public void Postgres_対象外の型名は素通し()
        {
            _db.ConvertFieldValueToDbValue("Pg", "inet", "192.168.0.1").Is("192.168.0.1");
            _db.ConvertFieldValueToDbValue("Pg", "text", null).IsNull();
        }

        // ===== その他のDataSourceType / 存在しないデータソース =====

        [Test]
        public void SQLServer等その他のDataSourceTypeは素通し()
        {
            var src = new TimeSpan(1, 30, 0);
            _db.ConvertFieldValueToDbValue("Ms", "interval", src).Is(src);
            _db.ConvertFieldValueToDbValue("Ms", "INTERVAL YEAR TO MONTH", 14m).Is(14m);
        }

        [Test]
        public void 存在しないデータソース名は素通し()
            => _db.ConvertFieldValueToDbValue("NoSuch", "interval", 14m).Is(14m);
    }
}
