namespace EliteSharp.Game;

/// <summary>
/// The Buy Cargo and Sell Cargo screens. The original asks about each item in
/// turn; here, every item in the market is listed, with a highlight that moves
/// up and down the list (the cursor keys, the D-pad or the left stick). Left
/// and right, or typing a number, set how much of the highlighted item goes in
/// the basket, and the quantity column shows what is left once the basket is
/// taken out. Nothing changes hands until Return (or A) buys or sells the
/// whole basket; Escape or "N" (or B) empties it, and "Y" (or the Y button)
/// puts as much of the highlighted item in it as possible. Leaving the screen
/// with something in the basket takes two presses of the function key (the
/// first just beeps), so the basket isn't left behind by mistake.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>The text row of the first item (the rows above it hold the headers).</summary>
    private const int TradeFirstRow = 4;

    /// <summary>How much of each item is in the basket.</summary>
    private readonly int[] _basket = new int[17];

    /// <summary>The price of each item in this market.</summary>
    private readonly int[] _tradePrices = new int[17];

    /// <summary>Whether this is the Sell Cargo screen (rather than Buy Cargo).</summary>
    private bool _tradeSelling;

    /// <summary>The highlighted item.</summary>
    private int _tradeSelected;

    /// <summary>Whether a number is being typed for the highlighted item (so digits add to it).</summary>
    private bool _tradeTyping;

    /// <summary>Show the Buy Cargo or Sell Cargo screen, and trade until another screen is chosen.</summary>
    private void ShowTradeScreen(bool selling)
    {
        ShowTradingScreen(selling ? 4 : 2);
        PrintMarketHeaders(selling ? "market.sell_headers" : "market.headers");

        _tradeSelling = selling;
        _tradeSelected = 0;
        _tradeTyping = false;
        Array.Clear(_basket);
        for (int item = 0; item < 17; item++)
        {
            _tradePrices[item] = CalculateItemPrice(item);
        }

        DrawTradeScreen();
        RunTradeScreen();
    }

    /// <summary>Trade until a function key moves to another screen.</summary>
    private void RunTradeScreen() =>
        RunListScreen(MoveInTradeScreen, PressInTradeScreen, confirmLeaving: () => _basket.Any(quantity => quantity != 0));

    /// <summary>Act on a key press (other than a direction) on the trading screens.</summary>
    private bool PressInTradeScreen(int key)
    {
        switch (key)
        {
            case 0x0D:
                CompleteTrade();
                break;
            case 0x1B or 'N':
                EmptyBasket();
                break;
            case 'Y':
                _tradeTyping = false;
                SetBasket(MaxTradeQuantity(_tradeSelected));
                break;
            case 0x7F:
                _tradeTyping = true;
                SetBasket(_basket[_tradeSelected] / 10);
                break;
            case >= '0' and <= '9':
                TypeTradeDigit(key - '0');
                break;
        }

        return false;
    }

    /// <summary>Move the highlight up or down, or take one from or add one to the basket.</summary>
    private void MoveInTradeScreen(int x, int y)
    {
        _tradeTyping = false;
        if (y != 0)
        {
            int selected = Math.Clamp(_tradeSelected + y, 0, 16);
            if (selected != _tradeSelected)
            {
                int previous = _tradeSelected;
                _tradeSelected = selected;
                DrawTradeRow(previous);
                DrawTradeRow(selected);
                DrawTradeSummary();
            }

            return;
        }

        int quantity = _basket[_tradeSelected] + x;
        if (quantity >= 0 && quantity <= MaxTradeQuantity(_tradeSelected))
        {
            SetBasket(quantity);
        }
    }

    /// <summary>Add a typed digit to the quantity of the highlighted item, beeping if there can't be that many.</summary>
    private void TypeTradeDigit(int digit)
    {
        int quantity = _tradeTyping ? _basket[_tradeSelected] * 10 + digit : digit;
        if (quantity > MaxTradeQuantity(_tradeSelected))
        {
            Beep();
            return;
        }

        _tradeTyping = true;
        SetBasket(quantity);
    }

    /// <summary>Put a quantity of the highlighted item in the basket.</summary>
    private void SetBasket(int quantity)
    {
        _basket[_tradeSelected] = quantity;
        DrawTradeRow(_tradeSelected);
        DrawTradeSummary();
    }

    /// <summary>Take everything out of the basket.</summary>
    private void EmptyBasket()
    {
        _tradeTyping = false;
        Array.Clear(_basket);
        DrawTradeScreen();
    }

    /// <summary>
    /// The most of an item that can go in the basket: everything we have, when
    /// selling, or when buying, as much as the market has that fits in the hold
    /// and that we can afford, along with the rest of the basket.
    /// </summary>
    private int MaxTradeQuantity(int item)
    {
        if (_tradeSelling)
        {
            return _cargo[item];
        }

        int quantity = 0;
        while (quantity < _marketAvailability[item] && BasketFits(item, quantity + 1))
        {
            quantity++;
        }

        return quantity;
    }

    /// <summary>Whether we could buy the basket with the given quantity of an item in it.</summary>
    private bool BasketFits(int item, int quantity)
    {
        // tnpr: items measured in tonnes share the hold with the rest of the
        // basket's tonnes
        int otherTonnes = 0;
        if (item <= 12)
        {
            for (int other = 0; other <= 12; other++)
            {
                otherTonnes += other == item ? 0 : _basket[other];
            }
        }

        _itemNumber = item;
        return !HasRoomInHold(quantity + otherTonnes) && BasketValue(item, quantity) <= _cash;
    }

    /// <summary>The value of the basket (in Cr * 10).</summary>
    private int BasketValue() => BasketValue(_tradeSelected, _basket[_tradeSelected]);

    /// <summary>The value of the basket (in Cr * 10) with the given quantity of an item in it.</summary>
    private int BasketValue(int item, int quantity)
    {
        int value = 0;
        for (int x = 0; x < 17; x++)
        {
            value += CalculateCost(x == item ? quantity : _basket[x], _tradePrices[x]);
        }

        return value;
    }

    /// <summary>Buy or sell everything in the basket.</summary>
    private void CompleteTrade()
    {
        _tradeTyping = false;
        if (_basket.All(quantity => quantity == 0))
        {
            return;
        }

        int value = BasketValue();
        if (_tradeSelling)
        {
            AddCash(value);
        }
        else if (!SpendCash(value))
        {
            Beep();
            return;
        }

        for (int item = 0; item < 17; item++)
        {
            int quantity = _basket[item];
            if (_tradeSelling)
            {
                _cargo[item] = (_cargo[item] - quantity) & 0xFF;
            }
            else
            {
                _cargo[item] = (_cargo[item] + quantity) & 0xFF;
                _marketAvailability[item] = (_marketAvailability[item] - quantity) & 0xFF;
            }
        }

        Array.Clear(_basket);
        DrawTradeScreen();
        Beep();
    }

    /// <summary>How much of an item is left to buy (or, when selling, in the hold) once the basket is taken out.</summary>
    private int TradeQuantityLeft(int item) => (_tradeSelling ? _cargo[item] : _marketAvailability[item]) - _basket[item];

    /// <summary>Draw every item and the summary at the bottom.</summary>
    private void DrawTradeScreen()
    {
        for (int item = 0; item < 17; item++)
        {
            DrawTradeRow(item);
        }

        DrawTradeSummary();
    }

    /// <summary>
    /// Draw an item's row: highlighted if it's the selected item,
    /// and in yellow if any of it is in the basket.
    /// </summary>
    private void DrawTradeRow(int item)
    {
        StartListRow(TradeFirstRow + item, item == _tradeSelected);
        _colour = _basket[item] != 0 ? Yellow : Cyan;
        PrintMarketItem(item, () => TradeQuantityLeft(item));
        _colour = Cyan;
    }

    /// <summary>
    /// Draw the bottom three rows: how much of the selected item is in the
    /// basket, what the basket costs (or fetches), and our cash.
    /// </summary>
    private void DrawTradeSummary()
    {
        ClearBottomRows();
        int item = _tradeSelected;
        _scratch[1] = _trading.Commodities[item].FactorAndUnit;
        PrintTextSpace(_tradeSelling ? "market.sell" : "market.buy");
        PrintText(CommodityKeys[item]);
        _cursorX = 22;
        _colour = Magenta;
        PrintNumber16(_basket[item], 5, false);
        PrintUnits();
        _colour = Cyan;

        PrintNewline();
        PrintMoneyLine("market.total", BasketValue());
        PrintNewline();
        PrintMoneyLine("market.cash", _cash);
    }
}
