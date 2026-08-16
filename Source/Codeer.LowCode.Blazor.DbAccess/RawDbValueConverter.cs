using Codeer.LowCode.Blazor.SystemSettings;
using Dapper;
using NpgsqlTypes;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Codeer.LowCode.Blazor.DbAccess
{
    //未解決のDB固有型(NetTypeFullNameがRawDbValueの列)の値変換。DBプロバイダ固有の型変換はここに集約する
    public static class RawDbValueConverter
    {
        //DapperはOracle固有型を知らないため、TypeHandlerでOracleDbTypeを明示してbindする
        class OracleIntervalYMTypeHandler : SqlMapper.TypeHandler<OracleIntervalYM>
        {
            public override void SetValue(IDbDataParameter parameter, OracleIntervalYM value)
            {
                if (parameter is OracleParameter oracleParameter) oracleParameter.OracleDbType = OracleDbType.IntervalYM;
                parameter.Value = value;
            }
            public override OracleIntervalYM Parse(object value) => (OracleIntervalYM)value;
        }

        //Dapperの既定ではbyte[]はDbType.BinaryでbindされOracleはRAW扱い(約32KB上限)になるため、
        //大きいバイナリ(BLOB列のファイル実体等)はOracleDbType.Blobを明示する。
        //2000バイト以下はRAW列にも入れられるよう既定のままにする
        class ByteArrayTypeHandler : SqlMapper.TypeHandler<byte[]>
        {
            public override void SetValue(IDbDataParameter parameter, byte[]? value)
            {
                if (parameter is OracleParameter oracleParameter && value?.Length > 2000) oracleParameter.OracleDbType = OracleDbType.Blob;
                else parameter.DbType = DbType.Binary;
                parameter.Value = (object?)value ?? DBNull.Value;
            }
            public override byte[] Parse(object value) => (byte[])value;
        }

        //PostgreSQLのinterval用ラッパ。TimeSpanのままだとDapperがDbType.Time(=time型)でbindするため型を分ける
        public class NpgsqlIntervalValue
        {
            public TimeSpan Value { get; }
            public NpgsqlIntervalValue(TimeSpan value) => Value = value;
        }

        class NpgsqlIntervalTypeHandler : SqlMapper.TypeHandler<NpgsqlIntervalValue>
        {
            public override void SetValue(IDbDataParameter parameter, NpgsqlIntervalValue? value)
            {
                if (parameter is Npgsql.NpgsqlParameter npgsqlParameter) npgsqlParameter.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Interval;
                parameter.Value = (object?)value?.Value ?? DBNull.Value;
            }
            public override NpgsqlIntervalValue Parse(object value) => new((TimeSpan)value);
        }

        //月成分を持つintervalはTimeSpanで表現できないため、Npgsqlネイティブ型(months/days/time)のままbindする
        class NpgsqlNativeIntervalTypeHandler : SqlMapper.TypeHandler<NpgsqlInterval>
        {
            public override void SetValue(IDbDataParameter parameter, NpgsqlInterval value)
            {
                if (parameter is Npgsql.NpgsqlParameter npgsqlParameter) npgsqlParameter.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Interval;
                parameter.Value = value;
            }
            public override NpgsqlInterval Parse(object value) => (NpgsqlInterval)value;
        }

        static RawDbValueConverter()
        {
            SqlMapper.AddTypeHandler(new OracleIntervalYMTypeHandler());
            SqlMapper.RemoveTypeMap(typeof(byte[]));
            SqlMapper.AddTypeHandler(new ByteArrayTypeHandler());
            SqlMapper.AddTypeHandler(new NpgsqlIntervalTypeHandler());
            SqlMapper.AddTypeHandler(new NpgsqlNativeIntervalTypeHandler());
        }

        //TypeHandlerの登録(staticコンストラクタ)を確実に走らせるための呼び出し口
        internal static void Initialize() { }

        internal static object? ConvertFieldValueToDbValue(DataSourceType? dataSourceType, string rawDbTypeName, object? value)
        {
            if (dataSourceType == DataSourceType.Oracle) return ConvertOracle(rawDbTypeName, value);
            if (dataSourceType == DataSourceType.PostgreSQL) return ConvertPostgreSQL(rawDbTypeName, value);
            return value;
        }

        static object? ConvertPostgreSQL(string rawDbTypeName, object? value)
        {
            switch (rawDbTypeName.ToLowerInvariant())
            {
                case "interval":
                    //Dapper既定ではTimeSpanがDbType.Time(=time型)でbindされ型不一致になるため、NpgsqlDbType.Intervalを明示する
                    if (value == null) return new NpgsqlIntervalValue(TimeSpan.Zero); //NOT NULL列の既定値
                    if (value is TimeSpan t) return new NpgsqlIntervalValue(t);
                    return ParseInterval(value.ToString() ?? "0:0:0");
            }
            return value;
        }

        //"1 year 2 mons 3 days 04:05:06" 形式(FormatIntervalと対)。各要素は省略可、単数複数どちらも受ける
        static readonly Regex IntervalPattern = new(
            @"^\s*(?:(?<y>[+-]?\d+)\s*years?\s*)?(?:(?<mo>[+-]?\d+)\s*(?:mons?|months?)\s*)?(?:(?<d>[+-]?\d+)\s*days?\s*)?(?<t>-?\d{1,2}:\d{1,2}(?::\d{1,2}(?:\.\d+)?)?)?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        //intervalの文字列解釈。従来のTimeSpan書式("1.02:03:04"等)はそのまま、
        //月成分を含む書式は NpgsqlInterval(months/days/time) にする(TimeSpanは月を表現できないため)
        static object ParseInterval(string text)
        {
            if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var timeSpan)) return new NpgsqlIntervalValue(timeSpan);

            var m = IntervalPattern.Match(text);
            var hasAny = m.Success &&
                         (m.Groups["y"].Success || m.Groups["mo"].Success || m.Groups["d"].Success || m.Groups["t"].Success);
            //解釈できない文字列は従来どおりの例外(FormatException)にする
            if (!hasAny) return new NpgsqlIntervalValue(TimeSpan.Parse(text, CultureInfo.InvariantCulture));

            var months = (m.Groups["y"].Success ? int.Parse(m.Groups["y"].Value) * 12 : 0)
                       + (m.Groups["mo"].Success ? int.Parse(m.Groups["mo"].Value) : 0);
            var days = m.Groups["d"].Success ? int.Parse(m.Groups["d"].Value) : 0;
            var time = m.Groups["t"].Success
                ? TimeSpan.Parse(m.Groups["t"].Value, CultureInfo.InvariantCulture)
                : TimeSpan.Zero;

            if (months == 0) return new NpgsqlIntervalValue(TimeSpan.FromDays(days) + time);
            return new NpgsqlInterval(months, days, time.Ticks / 10);
        }

        //月成分を持つintervalの文字列表記。PostgreSQLのtext表記に合わせる(ParseIntervalで往復可能)
        internal static string FormatInterval(NpgsqlInterval value)
        {
            var time = TimeSpan.FromTicks(value.Time * 10);
            //時刻部が24時間以上のときは日数へ寄せて hh:mm:ss に収める(ParseIntervalの時刻書式と対)
            var days = value.Days + time.Days;
            time -= TimeSpan.FromDays(time.Days);

            var years = value.Months / 12;
            var months = value.Months % 12;
            var parts = new List<string>();
            if (years != 0) parts.Add($"{years} {(Math.Abs(years) == 1 ? "year" : "years")}");
            if (months != 0) parts.Add($"{months} {(Math.Abs(months) == 1 ? "mon" : "mons")}");
            if (days != 0) parts.Add($"{days} {(Math.Abs(days) == 1 ? "day" : "days")}");
            if (time != TimeSpan.Zero || parts.Count == 0) parts.Add(time.ToString());
            return string.Join(" ", parts);
        }

        //読込値のField向け変換。月成分がなければ従来どおりTimeSpan(TextFieldでは"d.hh:mm:ss"表記)、
        //あればTimeSpanで表現できないため FormatInterval の文字列にする
        static object NpgsqlIntervalToFieldValue(NpgsqlInterval value)
            => value.Months == 0
                ? TimeSpan.FromDays(value.Days) + TimeSpan.FromTicks(value.Time * 10)
                : (object)FormatInterval(value);

        //全DB共通の行マテリアライズ (DbAccessor.QueryAsync が使用)。
        //PostgreSQLのinterval列は月成分を持つ値をNpgsql既定のTimeSpan変換(GetValue)で読むと例外になるため、
        //データ型名が interval の列だけ型を明示(NpgsqlInterval)して読む(他DBに interval という型名は無く発火しない)。
        //それ以外の列は Dapper dynamic(QueryAsync<object>)と同じ GetValue 読みなので、全DBで従来と等価
        internal static async Task<List<IDictionary<string, object>>> ReadRowsAsync(DbDataReader reader)
        {
            //列メタデータは行ループの外で1回だけ解決する(行×列ごとの呼び出しにしない)
            var names = new string[reader.FieldCount];
            var isInterval = new bool[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                names[i] = reader.GetName(i);
                isInterval[i] = reader.GetDataTypeName(i) == "interval";
            }

            var rows = new List<IDictionary<string, object>>();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object>(names.Length);
                for (var i = 0; i < names.Length; i++)
                {
                    object value;
                    if (isInterval[i])
                    {
                        value = reader.IsDBNull(i) ? null! : NpgsqlIntervalToFieldValue(reader.GetFieldValue<NpgsqlInterval>(i));
                    }
                    else
                    {
                        //Dapper dynamic と同じく GetValue 一発 + DBNull 判定(IsDBNull+GetValue の2回呼びにしない)
                        var raw = reader.GetValue(i);
                        value = raw is DBNull ? null! : raw;
                    }
                    //重複列名は最初の列が勝つ仕様
                    //QueryField の自前SQLで別名を付けない同名列 "SELECT a.id, b.id ..." 等で差が出ないように)
                    row.TryAdd(names[i], value);
                }
                rows.Add(row);
            }
            return rows;
        }

        static object? ConvertOracle(string rawDbTypeName, object? value)
        {
            var baseType = rawDbTypeName.Split('(')[0].Trim().ToUpperInvariant();
            switch (baseType)
            {
                case "INTERVAL YEAR":
                case "INTERVAL YEAR TO MONTH":
                    //読込時にODP.NETが総月数(long)を返すのに合わせ、書込も総月数として解釈する
                    var totalMonths = value == null ? 0L : Convert.ToInt64(value);
                    return new OracleIntervalYM((int)(totalMonths / 12), (int)(totalMonths % 12));
            }
            return value;
        }
    }
}
