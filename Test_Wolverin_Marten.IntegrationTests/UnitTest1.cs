using System.Net;
using System.Net.Http.Json;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Test_Wolverin_Marten.Contracts;
using Test_Wolverin_Marten.Domain;
using Test_Wolverin_Marten.Infrastructure;

namespace Test_Wolverin_Marten.IntegrationTests;

public class OrdersEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private const string ConnectionString =
        "Host=localhost;Port=5432;Database=test_wolverin_marten;Username=postgres;Password=postgres";

    public OrdersEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(_ =>
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Marten", ConnectionString);
        });
    }

    // ════════════════════════════════════════════════════════════════════════
    // TEST 1: happy path — wiadomość opublikowana, handler przetwarza,
    //         dokument pojawia się w bazie (eventual consistency)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task Post_orders_persists_order_document()
    {
        await EnsureDatabaseExistsAsync();
        using var client = _factory.CreateClient();

        var orderId = Guid.NewGuid();
        var command = new CreateOrder(orderId, "Integration Test Customer", 120.50m);

        var response = await client.PostAsJsonAsync("/orders", command);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // PublishAsync jest asynchroniczne: HTTP wraca natychmiast, handler działa w tle.
        // Polling z timeoutem 10s — w praktyce <200ms lokalnie.
        var saved = await WaitForDocumentAsync<Order>(orderId);

        Assert.NotNull(saved);
        Assert.Equal(command.CustomerName, saved!.CustomerName);
        Assert.Equal(command.TotalAmount, saved.TotalAmount);
        Assert.Equal(OrderStatus.Pending, saved.Status);
    }

    // ════════════════════════════════════════════════════════════════════════
    // TEST 2: retry — ConfirmOrderHandler rzuca TransientException 2×,
    //         Wolverine ponawia automatycznie, za 3. razem sukces.
    //         Weryfikujemy, że wiadomość NIGDY nie ginie.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task Confirm_order_retries_on_transient_failure_and_eventually_succeeds()
    {
        await EnsureDatabaseExistsAsync();

        // Konfigurujemy nową fabrykę z FailureSimulator ustawionym na 2 awarie.
        // ConfigureTestServices nadpisuje singleton zarejestrowany w Program.cs (0 awarii).
        using var factoryWithFailures = _factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(services =>
                services.AddSingleton(new FailureSimulator(failuresBeforeSuccess: 2))));

        using var client = factoryWithFailures.CreateClient();

        // 1. Tworzymy zamówienie — order musi istnieć zanim go potwierdzimy.
        var orderId = Guid.NewGuid();
        await client.PostAsJsonAsync("/orders", new CreateOrder(orderId, "Retry Customer", 99m));

        var created = await WaitForDocumentAsync<Order>(orderId, timeout: TimeSpan.FromSeconds(10));
        Assert.NotNull(created);

        // 2. Potwierdzamy zamówienie.
        //    ConfirmOrderHandler rzuci TransientException 2×:
        //      próba 1 → fail → Wolverine czeka 100ms → próba 2 → fail → 500ms → próba 3 → sukces
        var confirmResponse = await client.PostAsJsonAsync($"/orders/{orderId}/confirm", new { });
        Assert.Equal(HttpStatusCode.Accepted, confirmResponse.StatusCode);

        // 3. Polling aż status zmieni się na Confirmed (max 20s — retry + cooldown = ~700ms).
        var confirmed = await WaitForDocumentAsync<Order>(
            orderId,
            predicate: o => o.Status == OrderStatus.Confirmed,
            timeout: TimeSpan.FromSeconds(20));

        Assert.NotNull(confirmed);
        Assert.Equal(OrderStatus.Confirmed, confirmed!.Status);
    }

    // ════════════════════════════════════════════════════════════════════════
    // HELPERS
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Odpytuje Marten w pętli co 200ms aż dokument istnieje i spełnia predykat
    /// (lub mija timeout). Odwzorowuje "eventual consistency" durable queue.
    /// </summary>
    private static async Task<T?> WaitForDocumentAsync<T>(
        object id,
        Func<T, bool>? predicate = null,
        TimeSpan? timeout = null) where T : class
    {
        using var store = DocumentStore.For(opts => opts.Connection(ConnectionString));

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            await using var session = store.QuerySession();
            var doc = await session.LoadAsync<T>(id);
            if (doc is not null && (predicate is null || predicate(doc)))
                return doc;

            await Task.Delay(200);
        }

        return null;
    }

    private static async Task EnsureDatabaseExistsAsync()
    {
        var targetBuilder = new NpgsqlConnectionStringBuilder(ConnectionString);
        var adminBuilder = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = "postgres" };

        await using var connection = new NpgsqlConnection(adminBuilder.ConnectionString);
        await connection.OpenAsync();

        await using var existsCmd = new NpgsqlCommand(
            "select 1 from pg_database where datname = @db;", connection);
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
