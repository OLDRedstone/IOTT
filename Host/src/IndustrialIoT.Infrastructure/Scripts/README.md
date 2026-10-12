# Database initialization

Host uses MySQL 8.4 with Pomelo EF Core 8. On startup,
`DatabaseBootstrapper.InitializeAsync` creates all model tables in an empty
database using `EnsureCreatedAsync`. The connection must have schema creation
permissions for the initial startup. This does not upgrade an existing schema.

`001_*.sql`, `002_*.sql` and `003_*.sql` are historical SQL Server scripts. They
are not copied to build output and are never executed by the MySQL Host.

Configure `ConnectionStrings:Default` and `Database:ServerVersion` in Host
configuration. Put passwords in the ignored `appsettings.Local.json` or set
`ConnectionStrings__Default` through the environment. Environment variables
and command-line arguments override the local settings file.

Text uses `utf8mb4`; JSON payloads use `longtext`. Persisted `DateTimeOffset`
values are normalized to UTC `datetime(6)` and read as UTC. Original offsets
are not retained; precision is one microsecond.

For migration, initialize a new MySQL database, transfer configuration data,
and validate counts and foreign keys before switching the service connection.
Retain the original SQL Server database for rollback.

## Focused verification

Build the regression runner without touching the running Host output:

```powershell
dotnet build Host/tests/IndustrialIoT.Protocols.Tests/IndustrialIoT.Protocols.Tests.csproj --artifacts-path tmp/mysql-host-build
dotnet tmp/mysql-host-build/bin/IndustrialIoT.Protocols.Tests/debug/IndustrialIoT.Protocols.Tests.dll --mysql-model
```

Set `MYSQL_TEST_CONNECTION` to a MySQL connection with permission to create and
drop test databases, then run the same DLL with `--mysql`. This explicitly opts
into creating uniquely named `IOTTRegression_<guid>` databases and deleting
only those databases afterward. The configured database name is ignored.
`MYSQL_TEST_SERVER_VERSION` optionally overrides the default `8.4.0`.
The suite verifies initialization, Unicode, JSON, UTC and nullable timestamps,
microsecond precision, and atomic import rollback/commit.
