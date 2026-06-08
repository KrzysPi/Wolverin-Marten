namespace Test_Wolverin_Marten.Infrastructure;

/// <summary>
/// Symuluje przejściowe awarie w handlerach — używany w testach integracyjnych
/// do weryfikacji polityki retry Wolverine'a.
/// W produkcji rejestrowany z failuresBeforeSuccess = 0 (zawsze przechodzi).
/// W testach nadpisywany przez ConfigureTestServices z N awariami.
/// </summary>
public sealed class FailureSimulator(int failuresBeforeSuccess = 0)
{
    private int _remaining = failuresBeforeSuccess;

    /// <summary>Rzuca TransientException, jeśli zaplanowane są jeszcze awarie.</summary>
    public void ThrowIfShouldFail()
    {
        if (Interlocked.Decrement(ref _remaining) >= 0)
            throw new TransientException("Simulated transient failure — Wolverine will retry automatically.");
    }

    public void Reset(int failures) => Interlocked.Exchange(ref _remaining, failures);
}

/// <summary>
/// Wyjątek przejściowy — Wolverine ma skonfigurowaną politykę RetryWithCooldown
/// dla tego właśnie typu, więc każda wiadomość, której handler rzuci ten wyjątek,
/// zostanie ponowiona z opóźnieniem zamiast od razu trafić do dead letters.
/// </summary>
public sealed class TransientException(string message) : Exception(message);
