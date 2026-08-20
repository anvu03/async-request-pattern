SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;

DECLARE @LockResult int;
EXEC @LockResult = sys.sp_getapplock
    @Resource = N'AzureBusService.DatabaseMigrations',
    @LockMode = N'Exclusive',
    @LockOwner = N'Transaction',
    @LockTimeout = 60000;

IF @LockResult < 0
    THROW 51000, 'Could not acquire the database migration lock.', 1;

IF SCHEMA_ID(N'infra') IS NULL
    EXEC(N'CREATE SCHEMA [infra] AUTHORIZATION [dbo];');

IF OBJECT_ID(N'[infra].[SchemaVersions]', N'U') IS NULL
BEGIN
    CREATE TABLE [infra].[SchemaVersions]
    (
        [Version] int NOT NULL,
        [ScriptName] nvarchar(128) NOT NULL,
        [AppliedUtc] datetimeoffset(7) NOT NULL CONSTRAINT [DF_SchemaVersions_AppliedUtc] DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [PK_SchemaVersions] PRIMARY KEY CLUSTERED ([Version]),
        CONSTRAINT [CK_SchemaVersions_Version] CHECK ([Version] > 0),
        CONSTRAINT [CK_SchemaVersions_AppliedUtc] CHECK (DATEPART(TZOFFSET, [AppliedUtc]) = 0)
    );
END;

IF NOT EXISTS (SELECT 1 FROM [infra].[SchemaVersions] WHERE [Version] = 1)
BEGIN
    IF SCHEMA_ID(N'orders') IS NULL
        EXEC(N'CREATE SCHEMA [orders] AUTHORIZATION [dbo];');

    CREATE TABLE [orders].[Orders]
    (
        [Id] uniqueidentifier NOT NULL,
        [CustomerId] uniqueidentifier NOT NULL,
        [Status] int NOT NULL CONSTRAINT [DF_Orders_Status] DEFAULT (0),
        [Currency] char(3) NOT NULL,
        [Total] decimal(19,4) NOT NULL,
        [OwnerSubject] nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [IdempotencyKey] nvarchar(128) NOT NULL,
        [RequestFingerprint] binary(32) NOT NULL,
        [CreatedUtc] datetimeoffset(7) NOT NULL CONSTRAINT [DF_Orders_CreatedUtc] DEFAULT (SYSUTCDATETIME()),
        [UpdatedUtc] datetimeoffset(7) NOT NULL CONSTRAINT [DF_Orders_UpdatedUtc] DEFAULT (SYSUTCDATETIME()),
        [FailureCode] nvarchar(64) NULL,
        [FailureMessage] nvarchar(512) NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_Orders] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [CK_Orders_Status] CHECK ([Status] IN (0, 1, 2, 3, 4)),
        CONSTRAINT [CK_Orders_Currency] CHECK ([Currency] COLLATE Latin1_General_100_BIN2 LIKE '[A-Z][A-Z][A-Z]'),
        CONSTRAINT [CK_Orders_Total] CHECK ([Total] >= 0),
        CONSTRAINT [CK_Orders_RequestFingerprint] CHECK (DATALENGTH([RequestFingerprint]) = 32),
        CONSTRAINT [CK_Orders_OwnerSubject] CHECK (LEN([OwnerSubject]) > 0),
        CONSTRAINT [CK_Orders_IdempotencyKey] CHECK (LEN([IdempotencyKey]) > 0),
        CONSTRAINT [CK_Orders_CreatedUtc] CHECK (DATEPART(TZOFFSET, [CreatedUtc]) = 0),
        CONSTRAINT [CK_Orders_UpdatedUtc] CHECK (DATEPART(TZOFFSET, [UpdatedUtc]) = 0)
    );

    CREATE UNIQUE INDEX [UX_Orders_OwnerSubject_IdempotencyKey]
        ON [orders].[Orders] ([OwnerSubject], [IdempotencyKey]);
    CREATE INDEX [IX_Orders_OwnerSubject_CreatedUtc]
        ON [orders].[Orders] ([OwnerSubject], [CreatedUtc]);
    CREATE INDEX [IX_Orders_Status_UpdatedUtc]
        ON [orders].[Orders] ([Status], [UpdatedUtc]);

    CREATE TABLE [orders].[OrderItems]
    (
        [Id] uniqueidentifier NOT NULL,
        [OrderId] uniqueidentifier NOT NULL,
        [ProductId] uniqueidentifier NOT NULL,
        [Quantity] int NOT NULL,
        [UnitPrice] decimal(19,4) NOT NULL,
        CONSTRAINT [PK_OrderItems] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_OrderItems_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [orders].[Orders] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [CK_OrderItems_Quantity] CHECK ([Quantity] > 0),
        CONSTRAINT [CK_OrderItems_UnitPrice] CHECK ([UnitPrice] > 0)
    );

    CREATE INDEX [IX_OrderItems_OrderId] ON [orders].[OrderItems] ([OrderId]);

    CREATE TABLE [orders].[OutboxMessages]
    (
        [Id] uniqueidentifier NOT NULL,
        [OrderId] uniqueidentifier NOT NULL,
        [ContractName] nvarchar(128) NOT NULL,
        [SchemaVersion] int NOT NULL,
        [PayloadJson] nvarchar(max) NOT NULL,
        [CreatedUtc] datetimeoffset(7) NOT NULL CONSTRAINT [DF_OutboxMessages_CreatedUtc] DEFAULT (SYSUTCDATETIME()),
        [NextAttemptUtc] datetimeoffset(7) NOT NULL CONSTRAINT [DF_OutboxMessages_NextAttemptUtc] DEFAULT (SYSUTCDATETIME()),
        [LastAttemptUtc] datetimeoffset(7) NULL,
        [PublishedUtc] datetimeoffset(7) NULL,
        [LeaseOwner] nvarchar(200) NULL,
        [LeaseExpiresUtc] datetimeoffset(7) NULL,
        [AttemptCount] int NOT NULL CONSTRAINT [DF_OutboxMessages_AttemptCount] DEFAULT (0),
        [LastFailureCode] nvarchar(64) NULL,
        [LastFailureMessage] nvarchar(512) NULL,
        [QuarantinedUtc] datetimeoffset(7) NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_OutboxMessages] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_OutboxMessages_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [orders].[Orders] ([Id]),
        CONSTRAINT [CK_OutboxMessages_SchemaVersion] CHECK ([SchemaVersion] > 0),
        CONSTRAINT [CK_OutboxMessages_PayloadJson] CHECK (ISJSON([PayloadJson]) = 1),
        CONSTRAINT [CK_OutboxMessages_AttemptCount] CHECK ([AttemptCount] >= 0),
        CONSTRAINT [CK_OutboxMessages_Lease] CHECK (([LeaseOwner] IS NULL AND [LeaseExpiresUtc] IS NULL) OR ([LeaseOwner] IS NOT NULL AND [LeaseExpiresUtc] IS NOT NULL)),
        CONSTRAINT [CK_OutboxMessages_TerminalState] CHECK ([PublishedUtc] IS NULL OR [QuarantinedUtc] IS NULL),
        CONSTRAINT [CK_OutboxMessages_CreatedUtc] CHECK (DATEPART(TZOFFSET, [CreatedUtc]) = 0),
        CONSTRAINT [CK_OutboxMessages_NextAttemptUtc] CHECK (DATEPART(TZOFFSET, [NextAttemptUtc]) = 0),
        CONSTRAINT [CK_OutboxMessages_LastAttemptUtc] CHECK ([LastAttemptUtc] IS NULL OR DATEPART(TZOFFSET, [LastAttemptUtc]) = 0),
        CONSTRAINT [CK_OutboxMessages_PublishedUtc] CHECK ([PublishedUtc] IS NULL OR DATEPART(TZOFFSET, [PublishedUtc]) = 0),
        CONSTRAINT [CK_OutboxMessages_LeaseExpiresUtc] CHECK ([LeaseExpiresUtc] IS NULL OR DATEPART(TZOFFSET, [LeaseExpiresUtc]) = 0),
        CONSTRAINT [CK_OutboxMessages_QuarantinedUtc] CHECK ([QuarantinedUtc] IS NULL OR DATEPART(TZOFFSET, [QuarantinedUtc]) = 0)
    );

    CREATE INDEX [IX_OutboxMessages_Dispatch]
        ON [orders].[OutboxMessages] ([NextAttemptUtc], [LeaseExpiresUtc], [CreatedUtc])
        WHERE [PublishedUtc] IS NULL AND [QuarantinedUtc] IS NULL;
    CREATE INDEX [IX_OutboxMessages_OrderId]
        ON [orders].[OutboxMessages] ([OrderId]);
    CREATE INDEX [IX_OutboxMessages_PublishedUtc]
        ON [orders].[OutboxMessages] ([PublishedUtc])
        WHERE [PublishedUtc] IS NOT NULL;

    CREATE TABLE [orders].[InboxMessages]
    (
        [MessageId] uniqueidentifier NOT NULL,
        [ContractName] nvarchar(128) NOT NULL,
        [SchemaVersion] int NOT NULL,
        [ReceivedUtc] datetimeoffset(7) NOT NULL CONSTRAINT [DF_InboxMessages_ReceivedUtc] DEFAULT (SYSUTCDATETIME()),
        [ProcessedUtc] datetimeoffset(7) NOT NULL,
        CONSTRAINT [PK_InboxMessages] PRIMARY KEY CLUSTERED ([MessageId]),
        CONSTRAINT [CK_InboxMessages_SchemaVersion] CHECK ([SchemaVersion] > 0),
        CONSTRAINT [CK_InboxMessages_ReceivedUtc] CHECK (DATEPART(TZOFFSET, [ReceivedUtc]) = 0),
        CONSTRAINT [CK_InboxMessages_ProcessedUtc] CHECK (DATEPART(TZOFFSET, [ProcessedUtc]) = 0)
    );

    CREATE INDEX [IX_InboxMessages_ProcessedUtc]
        ON [orders].[InboxMessages] ([ProcessedUtc]);

    INSERT INTO [infra].[SchemaVersions] ([Version], [ScriptName])
    VALUES (1, N'001_initial_schema.sql');
END;

COMMIT TRANSACTION;
