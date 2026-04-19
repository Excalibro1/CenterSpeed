namespace Source2Surf.Timer.Shared.Interfaces;

public interface ITimerHudFeed
{
    static readonly string Identity = typeof(ITimerHudFeed).FullName!;

    bool TryGetWidgetText(int slot, out string text);
}
