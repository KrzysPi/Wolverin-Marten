using System.Net;
using System.Net.Http.Json;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Test_Wolverin_Marten.Contracts;
using Test_Wolverin_Marten.Domain;

namespace Test_Wolverin_Marten.IntegrationTests;

public class OrdersEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private const string ConnectionString = "Host=localhost;Port=5432;Database=test_wolverin_marten;Username=postgres;Password=postgres";

    public OrdersEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(_ =>
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Marten", ConnectionString);
        });
    }

    [Fact]
    public async Task Post_orders_persists_order_document()
    {
        await EnsureDatabaseExistsAsync();

        using var client = _factory.CreateClient();

        var orderId = Guid.NewGuid();
        var command = new CreateOrder(orderId, "Integration Test Customer", 120.50m);

        var response = await client.PostAsJsonAsync("/orders", command);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var store = DocumentStore.For(ConnectionString);
        await using var query = store.QuerySession();
        var saved = await query.LoadAsync<Order>(orderId);

        Assert.NotNull(saved);
        Assert.Equal(command.CustomerName, saved!.CustomerName);
        Assert.Equal(command.TotalAmount, saved.TotalAmount);
    }

    private static async Task EnsureDatabaseExistsAsync()
    {
        var targetBuilder = new NpgsqlConnectionStringBuilder(ConnectionString);
        var adminBuilder = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Database = "postgres"
        };

        await using var connection = new NpgsqlConnection(adminBuilder.ConnectionString);
        await connection.OpenAsync();

        const string sql = "select 1 from pg_database where datname = @db;";
        await using var existsCmd = new NpgsqlCommand(sql, connection);
        existsCmd.Parameters.AddWithValue("db", targetBuilder.Database!);

        var exists = await existsCmd.ExecuteScalarAsync();
        if (exists is null)
        {
            var dbName = $"\"{targetBuilder.Database!.Replace("\"", "\"\"")}\"";
            await using var createCmd = new NpgsqlCommand($"create database {dbName};", connection);
            await createCmd.ExecuteNonQueryAsync();
        }
    }
}
