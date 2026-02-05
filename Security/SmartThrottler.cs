namespace DiscordFS.Security;

/// <summary>
/// Sistema de throttling inteligente com jitter aleatório para evitar detecção de automação.
/// </summary>
public class SmartThrottler
{
    private readonly double _minDelaySeconds;
    private readonly double _maxDelaySeconds;
    private readonly double _backoffMultiplier;
    private double _currentMultiplier = 1.0;
    private DateTime _lastRequest = DateTime.MinValue;
    private int _consecutiveErrors = 0;
    private readonly object _lock = new();

    public SmartThrottler(
        double minDelaySeconds = 1.5,
        double maxDelaySeconds = 4.2,
        double backoffMultiplier = 1.5)
    {
        _minDelaySeconds = minDelaySeconds;
        _maxDelaySeconds = maxDelaySeconds;
        _backoffMultiplier = backoffMultiplier;
    }

    /// <summary>
    /// Calcula o próximo delay com jitter aleatório.
    /// </summary>
    public TimeSpan GetNextDelay()
    {
        lock (_lock)
        {
            // Jitter gaussiano para parecer mais "humano"
            var baseDelay = _minDelaySeconds + (Random.Shared.NextDouble() * (_maxDelaySeconds - _minDelaySeconds));
            
            // Aplicar multiplicador de backoff se houve erros recentes
            var actualDelay = baseDelay * _currentMultiplier;
            
            // Adicionar micro-jitter (±200ms)
            actualDelay += (Random.Shared.NextDouble() - 0.5) * 0.4;
            
            // Garantir mínimo
            actualDelay = Math.Max(actualDelay, 0.5);
            
            return TimeSpan.FromSeconds(actualDelay);
        }
    }

    /// <summary>
    /// Aguarda o delay calculado antes da próxima requisição.
    /// </summary>
    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        var delay = GetNextDelay();
        Console.WriteLine($"[Throttle] Aguardando {delay.TotalSeconds:F2}s...");
        await Task.Delay(delay, cancellationToken);
        
        lock (_lock)
        {
            _lastRequest = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Registra um erro e aumenta o backoff.
    /// </summary>
    public void RegisterError(int? httpStatusCode = null)
    {
        lock (_lock)
        {
            _consecutiveErrors++;
            
            if (httpStatusCode == 429) // Too Many Requests
            {
                // Backoff agressivo para rate limit
                _currentMultiplier = Math.Min(_currentMultiplier * 3.0, 20.0);
                Console.WriteLine($"[Throttle] Rate limit detectado! Multiplicador: {_currentMultiplier:F1}x");
            }
            else
            {
                // Backoff gradual para outros erros
                _currentMultiplier = Math.Min(_currentMultiplier * _backoffMultiplier, 10.0);
            }
        }
    }

    /// <summary>
    /// Registra sucesso e gradualmente reduz o backoff.
    /// </summary>
    public void RegisterSuccess()
    {
        lock (_lock)
        {
            _consecutiveErrors = 0;
            
            // Reduzir multiplicador gradualmente
            if (_currentMultiplier > 1.0)
            {
                _currentMultiplier = Math.Max(_currentMultiplier * 0.9, 1.0);
            }
        }
    }

    /// <summary>
    /// Pausa longa para recuperação de rate limit.
    /// </summary>
    public async Task RateLimitPauseAsync(int seconds = 60, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[Throttle] Rate limit! Pausando por {seconds}s...");
        await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
        
        lock (_lock)
        {
            // Após pausa, reduzir multiplicador pela metade
            _currentMultiplier = Math.Max(_currentMultiplier / 2.0, 2.0);
        }
    }

    public double CurrentMultiplier => _currentMultiplier;
    public int ConsecutiveErrors => _consecutiveErrors;
}
