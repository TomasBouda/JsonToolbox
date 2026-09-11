using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using JsonToolbox.App.ViewModels;

namespace JsonToolbox.App.Controls;

/// <summary>
/// Lets the document tabs be dragged into a different order, and closed with the middle
/// button.
/// </summary>
/// <remarks>
/// <para>
/// The tabs move as the pointer moves rather than at the drop: once the pointer has passed the
/// middle of a neighbour, the dragged tab takes its place. That is what every browser does,
/// and it means the order on screen is always the order that would result from letting go.
/// </para>
/// <para>
/// A drag begins only after the pointer has travelled a little, so that a plain click still
/// reaches the tab's button and activates it. Once it has begun, the pointer is captured here,
/// which cancels the button's click: rearranging the tabs is not a request to switch to one.
/// </para>
/// </remarks>
internal sealed class TabDragging
{
    /// <summary>How far the pointer has to move before a press becomes a drag.</summary>
    private const double DragThreshold = 4;

    private readonly ItemsControl _tabs;
    private DocumentSession? _pressed;
    private Point _origin;
    private bool _dragging;

    private TabDragging(ItemsControl tabs)
    {
        _tabs = tabs;

        // Tunnelling handlers see the press before the tab's button does, so the session under
        // the pointer can be noted without taking anything away from the button.
        tabs.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        tabs.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        tabs.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
        tabs.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost);
    }

    /// <summary>Starts watching the given strip of tabs.</summary>
    public static void Attach(ItemsControl tabs) => _ = new TabDragging(tabs);

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (SessionAt(e) is not { } session)
        {
            return;
        }

        PointerPointProperties properties = e.GetCurrentPoint(_tabs).Properties;

        if (properties.IsMiddleButtonPressed)
        {
            session.CloseCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (properties.IsLeftButtonPressed)
        {
            _pressed = session;
            _origin = e.GetPosition(_tabs);
            _dragging = false;
        }
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } session || _tabs.DataContext is not MainWindowViewModel window)
        {
            return;
        }

        Point position = e.GetPosition(_tabs);

        if (!_dragging)
        {
            if (Math.Abs(position.X - _origin.X) < DragThreshold)
            {
                return;
            }

            _dragging = true;
            e.Pointer.Capture(_tabs);
        }

        window.MoveDocument(session, TargetIndex(session, position.X));
        e.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging)
        {
            e.Pointer.Capture(null);
            e.Handled = true;
        }

        _pressed = null;
        _dragging = false;
    }

    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _pressed = null;
        _dragging = false;
    }

    /// <summary>
    /// Where the dragged tab belongs for a pointer at the given position: past the middle of
    /// the tabs it has been dragged over, and where it is otherwise.
    /// </summary>
    private int TargetIndex(DocumentSession session, double x)
    {
        int from = _tabs.Items.IndexOf(session);
        int target = from;

        for (int i = 0; i < _tabs.ItemCount; i++)
        {
            if (i == from || _tabs.ContainerFromIndex(i) is not { } container)
            {
                continue;
            }

            double middle = container.TranslatePoint(new Point(container.Bounds.Width / 2, 0), _tabs)?.X
                ?? double.NaN;

            if (i < from && x < middle)
            {
                target = Math.Min(target, i);
            }
            else if (i > from && x > middle)
            {
                target = Math.Max(target, i);
            }
        }

        return target;
    }

    /// <summary>The session whose tab is under the pointer, if any.</summary>
    private DocumentSession? SessionAt(PointerEventArgs e)
    {
        for (StyledElement? element = e.Source as StyledElement; element is not null && element != _tabs; element = element.Parent)
        {
            if (element is Button { Classes: var classes } && classes.Contains("tab") && element.DataContext is DocumentSession session)
            {
                return session;
            }
        }

        return null;
    }
}
