using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace Codeer.LowCode.Blazor.DbAccess
{
    /// <summary>
    /// DB の例外の分類。DB ごとに違う例外の型とコードを、アプリが利用者に返す文言を決めるための意味に揃える。
    /// </summary>
    public static class DbErrors
    {
        /// <summary>一意制約 (UNIQUE / PRIMARY KEY) 違反か。内側の例外も見る。</summary>
        public static bool IsUniqueViolation(Exception ex) => ex switch
        {
            PostgresException e => e.SqlState == PostgresErrorCodes.UniqueViolation,
            SqlException e => e.Number is 2627 or 2601,
            SqliteException e => e.SqliteExtendedErrorCode is 2067 or 1555,
            MySqlException e => e.ErrorCode == MySqlErrorCode.DuplicateKeyEntry,
            OracleException e => e.Number == 1,
            _ => ex.InnerException != null && IsUniqueViolation(ex.InnerException),
        };
    }
}
