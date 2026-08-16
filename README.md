# Codeer.LowCode.Blazor.DbAccess

Multi-database access layer for [Codeer.LowCode.Blazor](https://github.com/Codeer-Software/Codeer.LowCode.Blazor.Manual) server applications.

Provides the standard `IDbAccessor` implementation (`DbAccessor`) used by the server side of a
Codeer.LowCode.Blazor application. It supports **SQL Server / PostgreSQL / Oracle / SQLite / MySQL**
with a single API, using [Dapper](https://github.com/DapperLib/Dapper) for parameter binding and a
common row-materialization path.

- NuGet: `Codeer.LowCode.Blazor.DbAccess`
- License: MIT (this project). See the note below about bundled database providers.

## Usage

Reference the package from your server project and register `DbAccessor` as the `IDbAccessor`
(exactly as the application templates do):

```csharp
using Codeer.LowCode.Blazor.DbAccess;

IDbAccessor accessor = new DbAccessor(dataSources);
```

Every member of `DbAccessor` is `virtual`, so you can subclass it to insert logging, retries, or
provider-specific value binding without forking the code:

```csharp
public class MyDbAccessor : DbAccessor
{
    public MyDbAccessor(DataSource[] dataSources) : base(dataSources) { }

    public override async Task<List<IDictionary<string, object>>> QueryAsync(
        string dataSourceName, string query, Dictionary<string, ParamAndRawDbTypeName> args)
    {
        // ... custom logging / retry ...
        return await base.QueryAsync(dataSourceName, query, args);
    }
}
```

If you prefer not to take a dependency on this package, copy the source from this repository into
your own project and modify it as needed.

## SQL debug logging

Since the package source cannot be stepped into while debugging, `DbAccessor` provides a debug log
mode instead. Assign a sink to the static `DbAccessor.SqlLog` property and every execution
(`QueryAsync` / `ExecuteAsync` / `InsertAsync` / `ExecuteSqlCommandAsync`) dumps the SQL and its
parameters — including output parameter values after execution. It is `null` by default (disabled,
no overhead).

```csharp
// e.g. in Program.cs, for development only
DbAccessor.SqlLog = Console.WriteLine;
// or route to your logger
DbAccessor.SqlLog = message => logger.LogDebug(message);
```

Example output:

```
[SQL] Query (Main)
--------------------------------------------------
SELECT * FROM t WHERE id = @id
--------------------------------------------------
id = 123 (Int32)

[SQL] ExecuteSqlCommand NonQuery (Main)
--------------------------------------------------
BEGIN PROC_PARAM_TEST(:num_in, :num_out); END;
--------------------------------------------------
num_in = 111 (Decimal) (Input, Decimal)
num_out = null (Output, Decimal)

[SQL] ExecuteSqlCommand result (Main)
ReturnValue = -1 (Int32)
num_out = 1110 (OracleDecimal)
```

Note: parameter values may contain sensitive data — enable this only in development environments.

## Database provider licenses

This package declares (but does not redistribute) the following database providers. Each is restored
by NuGet on the consumer side:

| Provider | License |
|---|---|
| Dapper | Apache-2.0 |
| Microsoft.Data.SqlClient | MIT |
| Microsoft.Data.Sqlite | MIT |
| Npgsql | PostgreSQL License |
| MySqlConnector | MIT |
| **Oracle.ManagedDataAccess.Core** | **Oracle Free Use Terms and Conditions (FUTC)** — free to use, but not open source |
| Microsoft.EntityFrameworkCore(.Relational) | MIT |

All are free for commercial use. Only the Oracle provider is under a proprietary (though free) license
rather than an OSI-approved open-source license; review the Oracle FUTC if that matters to you.
