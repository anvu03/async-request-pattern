using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace AzureBusService.Persistence;

public static class DatabaseSchema
{
    public const int CurrentVersion = 1;

    public static async Task EnsureCurrentAsync(
        IDbContextFactory<OrdersDbContext> dbContextFactory,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandType = CommandType.Text;
        command.CommandText = """
            IF OBJECT_ID(N'[infra].[SchemaVersions]', N'U') IS NULL
                SELECT CAST(0 AS int);
            ELSE
                SELECT COALESCE(MAX([Version]), 0) FROM [infra].[SchemaVersions];
            """;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        var version = Convert.ToInt32(result, CultureInfo.InvariantCulture);
        if (version != CurrentVersion)
        {
            throw new InvalidOperationException(
                $"OrdersDb schema version {version} does not match required version {CurrentVersion}. Run scripts/migrate.sh.");
        }
    }
}
