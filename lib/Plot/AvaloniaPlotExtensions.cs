using Avalonia;
using Avalonia.Input;
using ScottPlot.Interactivity;
using System;
using AvaKey = Avalonia.Input.Key;
using AvaCursor = Avalonia.Input.Cursor;
using ScottPlot;
internal static class AvaloniaPlotExtensions
{
    internal static Pixel ToPixel(this PointerEventArgs e, Visual visual)
    {
        float x = (float)e.GetPosition(visual).X;
        float y = (float)e.GetPosition(visual).Y;
        return new Pixel(x, y);
    }

    internal static void ProcessMouseDown(this UserInputProcessor processor, Pixel pixel, PointerUpdateKind kind)
    {
        IUserAction action = kind switch
        {
            PointerUpdateKind.LeftButtonPressed => new ScottPlot.Interactivity.UserActions.LeftMouseDown(pixel),
            PointerUpdateKind.MiddleButtonPressed => new ScottPlot.Interactivity.UserActions.MiddleMouseDown(pixel),
            PointerUpdateKind.RightButtonPressed => new ScottPlot.Interactivity.UserActions.RightMouseDown(pixel),
            _ => new ScottPlot.Interactivity.UserActions.Unknown("mouse down", kind.ToString()),
        };

        processor.Process(action);
    }

    internal static void ProcessMouseUp(this UserInputProcessor processor, Pixel pixel, PointerUpdateKind kind)
    {
        ScottPlot.Interactivity.IUserAction action = kind switch
        {
            PointerUpdateKind.LeftButtonReleased => new ScottPlot.Interactivity.UserActions.LeftMouseUp(pixel),
            PointerUpdateKind.MiddleButtonReleased => new ScottPlot.Interactivity.UserActions.MiddleMouseUp(pixel),
            PointerUpdateKind.RightButtonReleased => new ScottPlot.Interactivity.UserActions.RightMouseUp(pixel),
            _ => new ScottPlot.Interactivity.UserActions.Unknown("mouse up", kind.ToString()),
        };

        processor.Process(action);
    }

    internal static void ProcessMouseMove(this UserInputProcessor processor, Pixel pixel)
    {
        processor.Process(new ScottPlot.Interactivity.UserActions.MouseMove(pixel));
    }

    internal static void ProcessMouseWheel(this UserInputProcessor processor, Pixel pixel, double delta)
    {
        IUserAction action = delta > 0
            ? new ScottPlot.Interactivity.UserActions.MouseWheelUp(pixel)
            : new ScottPlot.Interactivity.UserActions.MouseWheelDown(pixel);

        processor.Process(action);
    }

    internal static void ProcessKeyDown(this UserInputProcessor processor, KeyEventArgs e)
    {
        ScottPlot.Interactivity.Key key = GetKey(e.Key);
        IUserAction action = new ScottPlot.Interactivity.UserActions.KeyDown(key);
        processor.Process(action);
    }

    internal static void ProcessKeyUp(this UserInputProcessor processor, KeyEventArgs e)
    {
        ScottPlot.Interactivity.Key key = GetKey(e.Key);
        IUserAction action = new ScottPlot.Interactivity.UserActions.KeyUp(key);
        processor.Process(action);
    }

    public static ScottPlot.Interactivity.Key GetKey(AvaKey avaKey)
    {
        return avaKey switch
        {
            AvaKey.LeftAlt => StandardKeys.Alt,
            AvaKey.RightAlt => StandardKeys.Alt,
            AvaKey.LeftShift => StandardKeys.Shift,
            AvaKey.RightShift => StandardKeys.Shift,
            AvaKey.LeftCtrl => StandardKeys.Control,
            AvaKey.RightCtrl => StandardKeys.Control,
            _ => new ScottPlot.Interactivity.Key(avaKey.ToString()),
        };
    }

    public static AvaCursor GetCursor(this ScottPlot.Cursor cursor)
    {
        return cursor switch
        {
            ScottPlot.Cursor.Arrow => new(StandardCursorType.Arrow),
            ScottPlot.Cursor.No => new(StandardCursorType.No),
            ScottPlot.Cursor.Wait => new(StandardCursorType.Wait),
            ScottPlot.Cursor.Hand => new(StandardCursorType.Hand),
            ScottPlot.Cursor.Cross => new(StandardCursorType.Cross),
            ScottPlot.Cursor.SizeAll => new(StandardCursorType.SizeAll),
            ScottPlot.Cursor.SizeNorthSouth => new(StandardCursorType.SizeNorthSouth),
            ScottPlot.Cursor.SizeWestEast => new(StandardCursorType.SizeWestEast),
            _ => throw new NotImplementedException(cursor.ToString()),
        };
    }
}