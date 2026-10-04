namespace EliteSharp.Game;

/// <summary>
/// The Equip Ship screen. The original lists the equipment for sale and asks
/// for the number of the item to buy; here, the list has a highlight that moves
/// up and down (the cursor keys, the D-pad or the left stick), and typing an
/// item's number moves the highlight to it. Return (or A) buys the highlighted
/// item. A laser then needs a view to go on, chosen from a highlighted list in
/// the same way, where Escape (or B) changes your mind. Nothing is paid for
/// until the item is fitted, and the screen stays up for buying more.
/// </summary>
public sealed partial class EliteGame
{
    /// <summary>The text row of the first item (below the title).</summary>
    private const int EquipFirstRow = 3;

    /// <summary>The text row of the first view when choosing where a laser goes (as in qv).</summary>
    private const int LaserViewFirstRow = 16;

    /// <summary>The highlighted item of equipment.</summary>
    private int _equipSelected;

    /// <summary>Whether an item number is being typed (so another digit adds to it).</summary>
    private bool _equipTyping;

    /// <summary>The highlighted view when choosing where a laser goes.</summary>
    private int _laserView;

    /// <summary>Show the Equip Ship screen, and buy equipment until a function key moves to another screen.</summary>
    private void ShowEquipScreen()
    {
        _equipSelected = 0;
        _equipTyping = false;
        DrawEquipScreen();
        RunListScreen(MoveInEquipScreen, PressInEquipScreen);
    }

    /// <summary>Draw the whole screen: the title, the equipment for sale and our cash.</summary>
    private void DrawEquipScreen()
    {
        ShowTradingScreen(32);
        _cursorX = 12;
        PrintTextSpace("market.equip");
        PrintTitle("market.ship");

        _fuelPrice = ((70 - _fuel) << 1) & 0xFFFF;
        for (int item = 0; item < EquipmentForSale(); item++)
        {
            DrawEquipRow(item);
        }

        DrawEquipSummary();
    }

    /// <summary>
    /// Draw an item's row (EQL1): highlighted if it's the selected item, or
    /// in yellow (without the highlight) once it's chosen and a laser's view is
    /// being picked.
    /// </summary>
    private void DrawEquipRow(int item, bool chosen = false)
    {
        StartListRow(EquipFirstRow + item, item == _equipSelected && !chosen);
        _colour = chosen ? Yellow : Cyan;
        PrintNumber3(item + 1);
        PrintSpace();
        PrintText(EquipmentKeys[item]);
        _cursorX = 25;
        PrintNumber16(EquipmentPrice(item), 6, true);
        _colour = Cyan;
    }

    /// <summary>Draw the bottom three rows: an optional message, and our cash.</summary>
    private void DrawEquipSummary(Action? message = null)
    {
        ClearBottomRows();
        message?.Invoke();
        _cursorX = 1;
        _cursorY = 23;
        _textCase = 0x80;
        PrintMoneyLine("market.cash", _cash);
    }

    /// <summary>Move the highlight up or down.</summary>
    private void MoveInEquipScreen(int x, int y)
    {
        if (y != 0)
        {
            _equipTyping = false;
            SelectEquipment(Math.Clamp(_equipSelected + y, 0, EquipmentForSale() - 1));
        }
    }

    /// <summary>Move the highlight to an item.</summary>
    private void SelectEquipment(int item)
    {
        if (item == _equipSelected)
        {
            return;
        }

        int previous = _equipSelected;
        _equipSelected = item;
        DrawEquipRow(previous);
        DrawEquipRow(item);
        DrawEquipSummary();
    }

    /// <summary>Act on a key press (other than a direction) on the Equip Ship screen.</summary>
    private bool PressInEquipScreen(int key)
    {
        switch (key)
        {
            case 0x0D:
                BuySelectedEquipment();
                break;
            case >= '0' and <= '9':
                TypeEquipmentDigit(key - '0');
                break;
        }

        return false;
    }

    /// <summary>
    /// Add a typed digit to the item number, moving the highlight to that item.
    /// If there's no such item, the digit starts a new number instead, and if
    /// there's no item with that number either, we beep.
    /// </summary>
    private void TypeEquipmentDigit(int digit)
    {
        int number = (_equipSelected + 1) * 10 + digit;
        if (!_equipTyping || number > EquipmentForSale())
        {
            number = digit;
        }

        if (number < 1 || number > EquipmentForSale())
        {
            Beep();
            return;
        }

        _equipTyping = true;
        SelectEquipment(number - 1);
    }

    /// <summary>
    /// Buy the highlighted item, if it isn't already fitted and we can afford
    /// it, asking which view to put a laser on.
    /// </summary>
    private void BuySelectedEquipment()
    {
        _equipTyping = false;
        int item = _equipSelected;
        if (EquipmentAlreadyPresent(item) is { } present)
        {
            // pres
            DrawEquipSummary(() =>
            {
                PrintTextSpace(present);
                PrintText("equipment.present");
            });
            Beep();
            return;
        }

        int price = EquipmentPrice(item);
        if (_cash < (uint)price)
        {
            DrawEquipSummary(() => PrintTextQuestion("market.cash"));
            Beep();
            return;
        }

        int view = 0;
        if (IsLaser(item))
        {
            view = ChooseLaserView(item);
            if (view < 0)
            {
                DrawEquipScreen();
                return;
            }
        }

        SpendCash(price);
        FitEquipment(item, view);
        DrawEquipScreen();
        Beep();
    }

    /// <summary>
    /// qv: ask which view to fit a laser to, returning the view number, or -1
    /// if we change our mind.
    /// </summary>
    private int ChooseLaserView(int item)
    {
        if (_techLevel >= 8)
        {
            // The list of equipment reaches down to the views, so clear it
            ClearScreen(32);
        }
        else
        {
            DrawEquipRow(item, chosen: true);
        }

        _laserView = 0;
        for (int view = 0; view < 4; view++)
        {
            DrawLaserViewRow(view);
        }

        DrawEquipSummary(() => PrintTextQuestion("views.view"));

        int chosenView = -1;
        RunListScreen(MoveLaserView, key =>
        {
            switch (key)
            {
                case 0x0D:
                    chosenView = _laserView;
                    return true;
                case 0x1B or 'N':
                    return true;
                case >= '0' and <= '3':
                    chosenView = key - '0';
                    return true;
                default:
                    return false;
            }
        });

        return chosenView;
    }

    /// <summary>Move the highlight up or down the views.</summary>
    private void MoveLaserView(int x, int y)
    {
        int view = Math.Clamp(_laserView + y, 0, 3);
        if (view == _laserView)
        {
            return;
        }

        int previous = _laserView;
        _laserView = view;
        DrawLaserViewRow(previous);
        DrawLaserViewRow(view);
    }

    /// <summary>qv1: draw a view's row, highlighted if it's the selected view.</summary>
    private void DrawLaserViewRow(int view)
    {
        StartListRow(LaserViewFirstRow + view, view == _laserView);
        _cursorX = 12;
        PrintTokenSpace(view + '0');
        PrintText(ViewKeys[view]);
    }
}
