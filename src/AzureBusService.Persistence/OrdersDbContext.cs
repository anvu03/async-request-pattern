using Microsoft.EntityFrameworkCore;

namespace AzureBusService.Persistence;

public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("orders");

        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("Orders", "orders", table =>
            {
                table.HasCheckConstraint("CK_Orders_Status", "[Status] IN (0, 1, 2, 3, 4)");
                table.HasCheckConstraint("CK_Orders_Currency", "[Currency] COLLATE Latin1_General_100_BIN2 LIKE '[A-Z][A-Z][A-Z]'");
                table.HasCheckConstraint("CK_Orders_Total", "[Total] >= 0");
                table.HasCheckConstraint("CK_Orders_RequestFingerprint", "DATALENGTH([RequestFingerprint]) = 32");
                table.HasCheckConstraint("CK_Orders_OwnerSubject", "LEN([OwnerSubject]) > 0");
                table.HasCheckConstraint("CK_Orders_IdempotencyKey", "LEN([IdempotencyKey]) > 0");
                table.HasCheckConstraint("CK_Orders_CreatedUtc", "DATEPART(TZOFFSET, [CreatedUtc]) = 0");
                table.HasCheckConstraint("CK_Orders_UpdatedUtc", "DATEPART(TZOFFSET, [UpdatedUtc]) = 0");
            });
            entity.HasKey(order => order.Id).HasName("PK_Orders");
            entity.Property(order => order.Id).HasColumnName("Id").HasColumnType("uniqueidentifier").ValueGeneratedNever();
            entity.Property(order => order.CustomerId).HasColumnName("CustomerId").HasColumnType("uniqueidentifier");
            entity.Property(order => order.Status).HasColumnName("Status").HasColumnType("int").HasDefaultValue(OrderStatus.Pending);
            entity.Property(order => order.Currency).HasColumnName("Currency").HasColumnType("char(3)").HasMaxLength(3).IsFixedLength().IsUnicode(false);
            entity.Property(order => order.Total).HasColumnName("Total").HasColumnType("decimal(19,4)");
            entity.Property(order => order.OwnerSubject).HasColumnName("OwnerSubject").HasColumnType("nvarchar(200)").HasMaxLength(200).UseCollation("Latin1_General_100_BIN2");
            entity.Property(order => order.IdempotencyKey).HasColumnName("IdempotencyKey").HasColumnType("nvarchar(128)").HasMaxLength(128);
            entity.Property(order => order.RequestFingerprint).HasColumnName("RequestFingerprint").HasColumnType("binary(32)").HasMaxLength(32).IsFixedLength();
            entity.Property(order => order.CreatedUtc).HasColumnName("CreatedUtc").HasColumnType("datetimeoffset(7)").HasDefaultValueSql("SYSUTCDATETIME()");
            entity.Property(order => order.UpdatedUtc).HasColumnName("UpdatedUtc").HasColumnType("datetimeoffset(7)").HasDefaultValueSql("SYSUTCDATETIME()");
            entity.Property(order => order.FailureCode).HasColumnName("FailureCode").HasColumnType("nvarchar(64)").HasMaxLength(64);
            entity.Property(order => order.FailureMessage).HasColumnName("FailureMessage").HasColumnType("nvarchar(512)").HasMaxLength(512);
            entity.Property(order => order.RowVersion).HasColumnName("RowVersion").HasColumnType("rowversion").IsRowVersion();
            entity.HasIndex(order => new { order.OwnerSubject, order.IdempotencyKey }).IsUnique().HasDatabaseName("UX_Orders_OwnerSubject_IdempotencyKey");
            entity.HasIndex(order => new { order.OwnerSubject, order.CreatedUtc }).HasDatabaseName("IX_Orders_OwnerSubject_CreatedUtc");
            entity.HasIndex(order => new { order.Status, order.UpdatedUtc }).HasDatabaseName("IX_Orders_Status_UpdatedUtc");
        });

        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.ToTable("OrderItems", "orders", table =>
            {
                table.HasCheckConstraint("CK_OrderItems_Quantity", "[Quantity] > 0");
                table.HasCheckConstraint("CK_OrderItems_UnitPrice", "[UnitPrice] > 0");
            });
            entity.HasKey(item => item.Id).HasName("PK_OrderItems");
            entity.Property(item => item.Id).HasColumnName("Id").HasColumnType("uniqueidentifier").ValueGeneratedNever();
            entity.Property(item => item.OrderId).HasColumnName("OrderId").HasColumnType("uniqueidentifier");
            entity.Property(item => item.ProductId).HasColumnName("ProductId").HasColumnType("uniqueidentifier");
            entity.Property(item => item.Quantity).HasColumnName("Quantity").HasColumnType("int");
            entity.Property(item => item.UnitPrice).HasColumnName("UnitPrice").HasColumnType("decimal(19,4)");
            entity.HasOne(item => item.Order).WithMany(order => order.Items).HasForeignKey(item => item.OrderId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("FK_OrderItems_Orders_OrderId");
            entity.HasIndex(item => item.OrderId).HasDatabaseName("IX_OrderItems_OrderId");
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("OutboxMessages", "orders", table =>
            {
                table.HasCheckConstraint("CK_OutboxMessages_SchemaVersion", "[SchemaVersion] > 0");
                table.HasCheckConstraint("CK_OutboxMessages_PayloadJson", "ISJSON([PayloadJson]) = 1");
                table.HasCheckConstraint("CK_OutboxMessages_AttemptCount", "[AttemptCount] >= 0");
                table.HasCheckConstraint("CK_OutboxMessages_Lease", "([LeaseOwner] IS NULL AND [LeaseExpiresUtc] IS NULL) OR ([LeaseOwner] IS NOT NULL AND [LeaseExpiresUtc] IS NOT NULL)");
                table.HasCheckConstraint("CK_OutboxMessages_TerminalState", "[PublishedUtc] IS NULL OR [QuarantinedUtc] IS NULL");
                table.HasCheckConstraint("CK_OutboxMessages_CreatedUtc", "DATEPART(TZOFFSET, [CreatedUtc]) = 0");
                table.HasCheckConstraint("CK_OutboxMessages_NextAttemptUtc", "DATEPART(TZOFFSET, [NextAttemptUtc]) = 0");
                table.HasCheckConstraint("CK_OutboxMessages_LastAttemptUtc", "[LastAttemptUtc] IS NULL OR DATEPART(TZOFFSET, [LastAttemptUtc]) = 0");
                table.HasCheckConstraint("CK_OutboxMessages_PublishedUtc", "[PublishedUtc] IS NULL OR DATEPART(TZOFFSET, [PublishedUtc]) = 0");
                table.HasCheckConstraint("CK_OutboxMessages_LeaseExpiresUtc", "[LeaseExpiresUtc] IS NULL OR DATEPART(TZOFFSET, [LeaseExpiresUtc]) = 0");
                table.HasCheckConstraint("CK_OutboxMessages_QuarantinedUtc", "[QuarantinedUtc] IS NULL OR DATEPART(TZOFFSET, [QuarantinedUtc]) = 0");
            });
            entity.HasKey(message => message.Id).HasName("PK_OutboxMessages");
            entity.Property(message => message.Id).HasColumnName("Id").HasColumnType("uniqueidentifier").ValueGeneratedNever();
            entity.Property(message => message.OrderId).HasColumnName("OrderId").HasColumnType("uniqueidentifier");
            entity.Property(message => message.ContractName).HasColumnName("ContractName").HasColumnType("nvarchar(128)").HasMaxLength(128);
            entity.Property(message => message.SchemaVersion).HasColumnName("SchemaVersion").HasColumnType("int");
            entity.Property(message => message.PayloadJson).HasColumnName("PayloadJson").HasColumnType("nvarchar(max)");
            entity.Property(message => message.CreatedUtc).HasColumnName("CreatedUtc").HasColumnType("datetimeoffset(7)").HasDefaultValueSql("SYSUTCDATETIME()");
            entity.Property(message => message.NextAttemptUtc).HasColumnName("NextAttemptUtc").HasColumnType("datetimeoffset(7)").HasDefaultValueSql("SYSUTCDATETIME()");
            entity.Property(message => message.LastAttemptUtc).HasColumnName("LastAttemptUtc").HasColumnType("datetimeoffset(7)");
            entity.Property(message => message.PublishedUtc).HasColumnName("PublishedUtc").HasColumnType("datetimeoffset(7)");
            entity.Property(message => message.LeaseOwner).HasColumnName("LeaseOwner").HasColumnType("nvarchar(200)").HasMaxLength(200);
            entity.Property(message => message.LeaseExpiresUtc).HasColumnName("LeaseExpiresUtc").HasColumnType("datetimeoffset(7)");
            entity.Property(message => message.AttemptCount).HasColumnName("AttemptCount").HasColumnType("int").HasDefaultValue(0);
            entity.Property(message => message.LastFailureCode).HasColumnName("LastFailureCode").HasColumnType("nvarchar(64)").HasMaxLength(64);
            entity.Property(message => message.LastFailureMessage).HasColumnName("LastFailureMessage").HasColumnType("nvarchar(512)").HasMaxLength(512);
            entity.Property(message => message.QuarantinedUtc).HasColumnName("QuarantinedUtc").HasColumnType("datetimeoffset(7)");
            entity.Property(message => message.RowVersion).HasColumnName("RowVersion").HasColumnType("rowversion").IsRowVersion();
            entity.HasOne(message => message.Order).WithMany().HasForeignKey(message => message.OrderId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("FK_OutboxMessages_Orders_OrderId");
            entity.HasIndex(message => message.OrderId).HasDatabaseName("IX_OutboxMessages_OrderId");
            entity.HasIndex(message => new { message.NextAttemptUtc, message.LeaseExpiresUtc, message.CreatedUtc }).HasFilter("[PublishedUtc] IS NULL AND [QuarantinedUtc] IS NULL").HasDatabaseName("IX_OutboxMessages_Dispatch");
            entity.HasIndex(message => message.PublishedUtc).HasFilter("[PublishedUtc] IS NOT NULL").HasDatabaseName("IX_OutboxMessages_PublishedUtc");
        });

        modelBuilder.Entity<InboxMessage>(entity =>
        {
            entity.ToTable("InboxMessages", "orders", table =>
            {
                table.HasCheckConstraint("CK_InboxMessages_SchemaVersion", "[SchemaVersion] > 0");
                table.HasCheckConstraint("CK_InboxMessages_ReceivedUtc", "DATEPART(TZOFFSET, [ReceivedUtc]) = 0");
                table.HasCheckConstraint("CK_InboxMessages_ProcessedUtc", "DATEPART(TZOFFSET, [ProcessedUtc]) = 0");
            });
            entity.HasKey(message => message.MessageId).HasName("PK_InboxMessages");
            entity.Property(message => message.MessageId).HasColumnName("MessageId").HasColumnType("uniqueidentifier").ValueGeneratedNever();
            entity.Property(message => message.ContractName).HasColumnName("ContractName").HasColumnType("nvarchar(128)").HasMaxLength(128);
            entity.Property(message => message.SchemaVersion).HasColumnName("SchemaVersion").HasColumnType("int");
            entity.Property(message => message.ReceivedUtc).HasColumnName("ReceivedUtc").HasColumnType("datetimeoffset(7)").HasDefaultValueSql("SYSUTCDATETIME()");
            entity.Property(message => message.ProcessedUtc).HasColumnName("ProcessedUtc").HasColumnType("datetimeoffset(7)");
            entity.HasIndex(message => message.ProcessedUtc).HasDatabaseName("IX_InboxMessages_ProcessedUtc");
        });
    }
}
