using Codeer.LowCode.Blazor;
using Codeer.LowCode.Blazor.DataIO.Db;
using Codeer.LowCode.Blazor.Repository.Design;
using Codeer.LowCode.Blazor.SystemSettings;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using System.Data;
using System.Data.Common;
using System.Text;

namespace Codeer.LowCode.Blazor.DbAccess
{
    public class DbAccessor : IDbAccessor, IDisposable
    {
        //DB固有型変換のTypeHandler登録(RawDbValueConverterのstaticコンストラクタ)を確実に走らせる
        static DbAccessor() => RawDbValueConverter.Initialize();

        bool _transactionMode;

        class ConnectionOwner
        {
            internal bool NoNeedDispose { get; }
            internal DbConnection Connection { get; }
            internal ConnectionOwner(DbConnection connection, bool noNeedDispose)
            {
                Connection = connection;
                NoNeedDispose = noNeedDispose;
            }
        }

        static DbTableDefinitionCache _dbTableDefinitionCache = new();
        public virtual DbTableDefinitionCache? DbTableDefinitionCache => _dbTableDefinitionCache;
        public static void ClearTableDefinitionCache() => _dbTableDefinitionCache = new();

        //SQLデバッグログ。設定すると実行するSQLとパラメータ(および出力パラメータの実行結果)をダンプする。
        //パッケージ参照ではソースにステップインできないため、その代替のデバッグ手段。null(既定)なら無効でオーバーヘッドなし。
        //例: DbAccessor.SqlLog = Console.WriteLine;  ロガーへ流す場合: DbAccessor.SqlLog = s => logger.LogDebug(s);
        public static Action<string>? SqlLog { get; set; }

        //この接続で実行する SQL 1 文のタイムアウト(秒)。0 (既定) ならドライバの既定。全ての Dapper 呼び出しに commandTimeout として渡す
        public virtual int CommandTimeoutSeconds { get; set; }
        int? CommandTimeout => CommandTimeoutSeconds > 0 ? CommandTimeoutSeconds : null;

        readonly Dictionary<string, ConnectionOwner> _connections = new();
        readonly Dictionary<string, DbTransaction> _transactions = new();
        readonly Dictionary<string, IDbContextTransaction> _dbContextTransactions = new();
        readonly DataSource[] _dataSources;
        readonly Dictionary<string, DbContext> _dbContexts = new();

        public DbAccessor(DataSource[] dataSources) => _dataSources = dataSources;

        public DbAccessor(DataSource[] dataSources, Dictionary<string, DbContext> dbContext)
        {
            _dataSources = dataSources;
            _dbContexts = dbContext;
        }

        public virtual DataSource? GetDataSource(string dataSourceName)
            => _dataSources.FirstOrDefault(e => e.Name == dataSourceName);

        public virtual void StartTransaction()
            => _transactionMode = true;

        public virtual void StartDataAccess(string dataSourceName)
            => GetConnection(dataSourceName);

        public virtual async Task CommitAsync()
        {
            foreach (var e in _transactions)
            {
                await e.Value.CommitAsync();
                await e.Value.DisposeAsync();
            }
            _transactions.Clear();
            foreach (var e in _dbContextTransactions)
            {
                await e.Value.CommitAsync();
                await e.Value.DisposeAsync();
            }
            _dbContextTransactions.Clear();
        }

        public virtual async ValueTask DisposeAsync() => await ClearAsync();

        public virtual async ValueTask ClearAsync()
        {
            foreach (var e in _transactions) await e.Value.DisposeAsync();
            _transactions.Clear();

            foreach (var e in _connections)
            {
                if (e.Value.NoNeedDispose) continue;
                await e.Value.Connection.DisposeAsync();
                if (e.Value.Connection is SqliteConnection sqliteConn) SqliteConnection.ClearPool(sqliteConn);
            }
            _connections.Clear();

            foreach (var e in _dbContextTransactions) await e.Value.DisposeAsync();
            _dbContextTransactions.Clear();
        }

        public virtual void Dispose() => Clear();

        public virtual void Clear()
        {
            foreach (var e in _transactions) e.Value.Dispose();
            _transactions.Clear();

            foreach (var e in _connections)
            {
                if (e.Value.NoNeedDispose) continue;
                e.Value.Connection.Dispose();
                if (e.Value.Connection is SqliteConnection sqliteConn) SqliteConnection.ClearPool(sqliteConn);
            }
            _connections.Clear();

            foreach (var e in _dbContextTransactions) e.Value.Dispose();
            _dbContextTransactions.Clear();
        }

        public virtual DbConnection GetConnection(string dataSourceName)
        {
            if (_connections.TryGetValue(dataSourceName, out var ret)) return ret.Connection;

            var dataSource = _dataSources.FirstOrDefault(e => e.Name == dataSourceName);
            if (dataSource == null)
            {
                throw LowCodeException.Create($"{dataSourceName} not found in ({string.Join(", ", _dataSources.Select(e => e.Name))})");
            }

            if (_dbContexts.TryGetValue(dataSourceName, out var dbContext))
            {
                var conn = dbContext.Database.GetDbConnection();
                conn.Open();
                if (_transactionMode)
                {
                    _dbContextTransactions[dataSourceName] = dbContext!.Database.BeginTransaction();
                }
                _connections.Add(dataSourceName, new ConnectionOwner(conn, true));
                return conn;
            }
            else
            {
                DbConnection conn;
                switch (dataSource.DataSourceType)
                {
                    case DataSourceType.SQLServer:
                        conn = new SqlConnection(dataSource.ConnectionString);
                        break;
                    case DataSourceType.PostgreSQL:
                        conn = new NpgsqlConnection(dataSource.ConnectionString);
                        break;
                    case DataSourceType.Oracle:
                        conn = new OracleConnection(dataSource.ConnectionString);
                        break;
                    case DataSourceType.SQLite:
                        conn = new SqliteConnection(dataSource.ConnectionString);
                        break;
                    case DataSourceType.MySQL:
                        conn = new MySqlConnection(dataSource.ConnectionString);
                        break;
                    default: throw LowCodeException.Create("Invalid data source");
                }

                conn.Open();
                if (_transactionMode)
                {
                    _transactions[dataSourceName] = conn.BeginTransaction();
                }
                _connections.Add(dataSourceName, new ConnectionOwner(conn, false));
                return conn;
            }
        }

        public virtual IDbTransaction? GetTransaction(string dataSourceName)
        {
            GetConnection(dataSourceName);
            if (_transactions.TryGetValue(dataSourceName, out var transaction)) return transaction;
            if (_dbContextTransactions.TryGetValue(dataSourceName, out var efTransaction)) return efTransaction.GetDbTransaction();
            return null;
        }

        public virtual async Task<int> ExecuteAsync(string dataSourceName, string query, Dictionary<string, object?> args)
        {
            var conn = GetConnection(dataSourceName);
            if (SqlLog != null) DumpSql("Execute", dataSourceName, query, args.Select(e => $"{e.Key} = {FormatSqlLogValue(e.Value)}"));
            return await conn.ExecuteAsync(query, CreateParameter(args), GetTransaction(dataSourceName), commandTimeout: CommandTimeout);
        }

        public virtual async Task<string> InsertAsync(string dataSourceName, string query, Dictionary<string, object?> args)
        {
            var conn = GetConnection(dataSourceName);
            var ps = CreateParameter(args);
            if (SqlLog != null) DumpSql("Insert", dataSourceName, query, args.Select(e => $"{e.Key} = {FormatSqlLogValue(e.Value)}"));
            var ret = (await conn.ExecuteScalarAsync<string>(query, ps, GetTransaction(dataSourceName), commandTimeout: CommandTimeout)) ?? string.Empty;
            foreach (var e in args)
            {
                args[e.Key] = ps.Get<object>(e.Key);
            }
            if (SqlLog != null) DumpSql("Insert result", dataSourceName, null, new[] { $"NewId = {FormatSqlLogValue(ret)}" });
            return ret;
        }

        public virtual async Task<List<IDictionary<string, object>>> QueryAsync(string dataSourceName, string query, Dictionary<string, ParamAndRawDbTypeName> args)
        {
            var conn = GetConnection(dataSourceName);
            //実際にbindされる値(ToParameterでのDbString変換後)をダンプする
            if (SqlLog != null)
            {
                DumpSql("Query", dataSourceName, query, args.Select(e =>
                    $"{e.Key} = {FormatSqlLogValue(e.Value.ToParameter())}{(string.IsNullOrEmpty(e.Value.RawDbTypeName) ? "" : $" [{e.Value.RawDbTypeName}]")}"));
            }
            //パラメータbindは従来どおりDapper(TypeHandler込み)、行のマテリアライズは全DB共通の自前実装。
            //Dapper dynamic(QueryAsync<object>)と同じGetValue読みだが、PostgreSQLのinterval列(月成分あり)だけ
            //型を明示して読む必要があるため(DB種別で経路を分けず)ここに一本化している
            await using var reader = await conn.ExecuteReaderAsync(query, CreateParameter(args), GetTransaction(dataSourceName), commandTimeout: CommandTimeout);
            return await RawDbValueConverter.ReadRowsAsync(reader);
        }

        //未解決のDB固有型の値を実際にbindできる値へ変換する。独自の型を扱う場合はここをoverrideする
        public virtual object? ConvertFieldValueToDbValue(string dataSourceName, string rawDbTypeName, object? value)
            => RawDbValueConverter.ConvertFieldValueToDbValue(GetDataSource(dataSourceName)?.DataSourceType, rawDbTypeName, value);

        //Output/InputOutput/ReturnValue の文字列パラメータに指定する慣例的な最大サイズ
        const int TextParameterMaxSize = 4000;

        //ExecuteSqlField の SQL / ストアドプロシージャ実行。他の CRUD と同じ Dapper 経路
        //(登録済み TypeHandler・DateOnly/TimeOnly 変換が効く)に統一する。ログ・リトライ等を挟みたい場合はここをoverrideする
        public virtual async Task<DbSqlCommandResult> ExecuteSqlCommandAsync(string dataSourceName, DbSqlCommand command)
        {
            var conn = GetConnection(dataSourceName);
            var transaction = GetTransaction(dataSourceName);
            var commandType = command.CommandType == ExecuteSqlCommandType.StoredProcedure ?
                                CommandType.StoredProcedure :
                                CommandType.Text;

            var ps = new DynamicParameters();
            foreach (var p in command.Parameters)
            {
                var val = p.Value;
                if (val is DateOnly dateOnly) val = new DateTime(dateOnly.Year, dateOnly.Month, dateOnly.Day);
                if (val is TimeOnly timeOnly) val = new TimeSpan(timeOnly.Hour, timeOnly.Minute, timeOnly.Second);

                //値があり方向が Input なら Dapper の推論(TypeHandler 込み)に任せる。
                //null や出力系は値から型を推論できないため、NetType から DbType を明示する(未解決型 NetType=null は推論に任せる)
                var dbType = (val == null || p.Direction != ParameterDirection.Input) ? ToDbType(p.NetType) : null;
                //出力系の文字列パラメータは受け取りサイズの指定が必要
                var size = (p.Direction != ParameterDirection.Input && dbType == DbType.String) ? TextParameterMaxSize : (int?)null;
                ps.Add(p.Name, val, dbType, p.Direction, size);
            }

            if (SqlLog != null)
            {
                DumpSql($"ExecuteSqlCommand {command.MethodType}{(command.CommandType == ExecuteSqlCommandType.StoredProcedure ? " StoredProcedure" : "")}",
                    dataSourceName, command.CommandText, command.Parameters.Select(p =>
                        $"{p.Name} = {FormatSqlLogValue(p.Value)} ({p.Direction}{(p.NetType == null ? "" : ", " + p.NetType.Name)})"));
            }

            object? ret;
            if (command.MethodType == ExecuteSqlMethodType.Scalar)
            {
                ret = await conn.ExecuteScalarAsync<object>(command.CommandText, ps, transaction, commandTimeout: CommandTimeout, commandType: commandType);
            }
            else if (command.MethodType == ExecuteSqlMethodType.Reader)
            {
                try
                {
                    //コマンドを実行するだけで結果は読み捨てる(失敗時は例外)。Reader を開いたままにすると
                    //同一接続・同一トランザクションの後続コマンドがブロックされるため、スコープ内で確実に閉じる
                    using var reader = await conn.ExecuteReaderAsync(command.CommandText, ps, transaction, commandTimeout: CommandTimeout, commandType: commandType);
                }
                catch (Exception ex)
                {
                    throw LowCodeException.Create(ex.Message);
                }
                ret = null;
            }
            else
            {
                ret = await conn.ExecuteAsync(command.CommandText, ps, transaction, commandTimeout: CommandTimeout, commandType: commandType);
            }

            //実行後のパラメータ値(Output/InputOutput/ReturnValue の書き戻し用)。同名は先勝ち
            var result = new DbSqlCommandResult { ReturnValue = ret };
            foreach (var p in command.Parameters)
            {
                result.ParameterValues.TryAdd(p.Name, ps.Get<object?>(p.Name));
            }

            if (SqlLog != null)
            {
                DumpSql("ExecuteSqlCommand result", dataSourceName, null,
                    new[] { $"ReturnValue = {FormatSqlLogValue(ret)}" }.Concat(
                        command.Parameters.Where(p => p.Direction != ParameterDirection.Input)
                            .Select(p => $"{p.Name} = {FormatSqlLogValue(result.ParameterValues.GetValueOrDefault(p.Name))}")));
            }

            return result;
        }

        //ExecuteSql のパラメータ型(DB列型から解決済みの .NET 型)を Dapper に渡す DbType へ変換する
        static DbType? ToDbType(Type? netType)
        {
            if (netType == null) return null;
            if (netType == typeof(string)) return DbType.String;
            if (netType == typeof(int)) return DbType.Int32;
            if (netType == typeof(long)) return DbType.Int64;
            if (netType == typeof(short)) return DbType.Int16;
            if (netType == typeof(byte)) return DbType.Byte;
            if (netType == typeof(bool)) return DbType.Boolean;
            if (netType == typeof(decimal)) return DbType.Decimal;
            if (netType == typeof(double)) return DbType.Double;
            if (netType == typeof(float)) return DbType.Single;
            if (netType == typeof(DateTime)) return DbType.DateTime;
            if (netType == typeof(DateTimeOffset)) return DbType.DateTimeOffset;
            if (netType == typeof(TimeSpan)) return DbType.Time;
            if (netType == typeof(Guid)) return DbType.Guid;
            if (netType == typeof(byte[])) return DbType.Binary;
            if (netType == typeof(DateOnly)) return DbType.Date;
            if (netType == typeof(TimeOnly)) return DbType.Time;
            return null;
        }

        //SqlLog が設定されているとき、1回の実行を1メッセージにまとめてダンプする(sql=null は結果ダンプ等のSQL行なし)
        static void DumpSql(string operation, string dataSourceName, string? sql, IEnumerable<string> parameterLines)
        {
            var log = SqlLog;
            if (log == null) return;

            const string Separator = "--------------------------------------------------";
            var sb = new StringBuilder();
            sb.Append("[SQL] ").Append(operation).Append(" (").Append(dataSourceName).Append(')').AppendLine();
            //SQL部を罫線で挟み、後続のパラメータがSQLの続きに見えないようにする
            if (!string.IsNullOrEmpty(sql))
            {
                sb.AppendLine(Separator);
                sb.AppendLine(sql.TrimEnd());
                sb.AppendLine(Separator);
            }
            foreach (var line in parameterLines) sb.AppendLine(line);
            log(sb.ToString().TrimEnd());
        }

        static string FormatSqlLogValue(object? value) => value switch
        {
            null => "null",
            DBNull => "DBNull",
            string s => $"\"{s}\"",
            DbString ds => $"\"{ds.Value}\" (DbString, IsAnsi={ds.IsAnsi}, IsFixedLength={ds.IsFixedLength})",
            byte[] b => $"byte[{b.Length}]",
            _ => $"{value} ({value.GetType().Name})"
        };

        static DynamicParameters CreateParameter(Dictionary<string, ParamAndRawDbTypeName> args)
            => CreateParameter(args.ToDictionary(e => e.Key, e => e.Value.ToParameter()));

        static DynamicParameters CreateParameter(Dictionary<string, object?> args)
        {
            var dst = new DynamicParameters();
            foreach (var e in args)
            {
                var val = e.Value;
                if (val is DateOnly dateOnly) val = new DateTime(dateOnly.Year, dateOnly.Month, dateOnly.Day);
                if (val is TimeOnly timeOnly) val = new TimeSpan(timeOnly.Hour, timeOnly.Minute, timeOnly.Second);
                dst.Add(e.Key, val);
            }
            return dst;
        }
    }
}
