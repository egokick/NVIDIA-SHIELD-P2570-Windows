namespace ShieldController;

internal sealed class SetupScrollPanel : Panel
{
    internal void ScrollPageDown()
    {
        var message = Message.Create(Handle, 0x0115 /* WM_VSCROLL */, 3 /* SB_PAGEDOWN */, 0);
        WndProc(ref message);
    }
    internal void ScrollToBottom()
    {
        // Exercise this control's normal scrollbar message path, without
        // showing a window or injecting input into any other application.
        var message = Message.Create(Handle, 0x0115 /* WM_VSCROLL */, 7 /* SB_BOTTOM */, 0);
        WndProc(ref message);
    }
}
