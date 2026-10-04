using EliteSharp.Rendering;

namespace EliteSharp.Tests.Rendering;

/// <summary>
/// Timed text in the HUD (such as the view's name), which is shown until its
/// time is up and then removed, while the rest of the text stays.
/// </summary>
public sealed class HudTimedTextTests
{
    private const long End = 1000;

    /// <summary>Print a timed A and a normal B in the given HUD.</summary>
    private static void PrintText(Hud hud)
    {
        using (hud.TimedText(End))
        {
            hud.Print(1, 1, 'A', Ink.Cyan);
        }

        hud.Print(2, 1, 'B', Ink.Cyan);
    }

    [Fact]
    public void TimedTextIsShownUntilItsTimeIsUp()
    {
        var exchange = new FrameExchange();
        var hud = new Hud(exchange);
        PrintText(hud);
        hud.Present(End - 1);
        Assert.Equal(2, exchange.TakeLatest()!.SpaceQuads.Count);
    }

    [Fact]
    public void TimedTextIsRemovedOnceItsTimeIsUp()
    {
        var exchange = new FrameExchange();
        var hud = new Hud(exchange);
        PrintText(hud);
        hud.Present(End);
        Assert.Single(exchange.TakeLatest()!.SpaceQuads);

        // It stays gone, even at an earlier time
        hud.Present(End - 1);
        Assert.Single(exchange.TakeLatest()!.SpaceQuads);
    }

    [Fact]
    public void TextPrintedOverTimedTextStays()
    {
        var exchange = new FrameExchange();
        var hud = new Hud(exchange);
        using (hud.TimedText(End))
        {
            hud.Print(1, 1, 'A', Ink.Cyan);
        }

        hud.Print(1, 1, 'C', Ink.Cyan);
        hud.Present(End);
        Assert.Single(exchange.TakeLatest()!.SpaceQuads);
    }
}
