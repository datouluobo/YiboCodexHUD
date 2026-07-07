namespace YiboCodexHUD.Core.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
