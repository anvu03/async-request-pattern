-- LOCAL DEVELOPMENT ONLY. Production identities and credentials are provisioned separately.
:setvar APP_LOGIN "orders_app_local"
:setvar APP_PASSWORD "LocalOnly_OrdersDb_2026!"

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() = N'master'
    THROW 51000, 'Run app_login.sql against the application database, not master.', 1;

DECLARE @AppLogin sysname = N'$(APP_LOGIN)';
DECLARE @AppPassword nvarchar(128) = N'$(APP_PASSWORD)';
DECLARE @Sql nvarchar(max);

IF NOT EXISTS (SELECT 1 FROM [master].[sys].[server_principals] WHERE [name] = @AppLogin)
BEGIN
    SET @Sql = N'CREATE LOGIN ' + QUOTENAME(@AppLogin)
        + N' WITH PASSWORD = ' + QUOTENAME(@AppPassword, '''')
        + N', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF;';
    EXEC sys.sp_executesql @Sql;
END;

IF (SELECT [type] FROM [master].[sys].[server_principals] WHERE [name] = @AppLogin) <> 'S'
    THROW 51000, 'Existing server principal is not a SQL login.', 1;

BEGIN TRANSACTION;

IF NOT EXISTS (SELECT 1 FROM [sys].[database_principals] WHERE [name] = @AppLogin)
BEGIN
    SET @Sql = N'CREATE USER ' + QUOTENAME(@AppLogin)
        + N' FOR LOGIN ' + QUOTENAME(@AppLogin)
        + N' WITH DEFAULT_SCHEMA = [orders];';
    EXEC sys.sp_executesql @Sql;
END
ELSE IF (SELECT [sid] FROM [sys].[database_principals] WHERE [name] = @AppLogin) <> SUSER_SID(@AppLogin)
BEGIN
    THROW 51000, 'Existing database user is not mapped to the expected login.', 1;
END;

IF DATABASE_PRINCIPAL_ID(N'OrdersAppRole') IS NULL
    CREATE ROLE [OrdersAppRole] AUTHORIZATION [dbo];

GRANT SELECT, INSERT, UPDATE ON OBJECT::[orders].[Orders] TO [OrdersAppRole];
GRANT SELECT, INSERT ON OBJECT::[orders].[OrderItems] TO [OrdersAppRole];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::[orders].[OutboxMessages] TO [OrdersAppRole];
GRANT SELECT, INSERT, DELETE ON OBJECT::[orders].[InboxMessages] TO [OrdersAppRole];
GRANT SELECT ON OBJECT::[infra].[SchemaVersions] TO [OrdersAppRole];

IF NOT EXISTS
(
    SELECT 1
    FROM [sys].[database_role_members] AS [members]
    WHERE [members].[role_principal_id] = DATABASE_PRINCIPAL_ID(N'OrdersAppRole')
      AND [members].[member_principal_id] = DATABASE_PRINCIPAL_ID(@AppLogin)
)
BEGIN
    SET @Sql = N'ALTER ROLE [OrdersAppRole] ADD MEMBER ' + QUOTENAME(@AppLogin) + N';';
    EXEC sys.sp_executesql @Sql;
END;

COMMIT TRANSACTION;
