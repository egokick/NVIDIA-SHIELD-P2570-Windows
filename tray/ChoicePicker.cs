namespace ShieldController;

// A standard button and native menu keep selection keyboard-accessible and
// render through the same WinForms path in both the UI and hidden previews.
internal sealed class ChoicePicker(string placeholder) : Button
{
    private object[] items = [];
    private int selectedIndex = -1;
    internal event EventHandler? SelectedIndexChanged;
    internal IReadOnlyList<object> Items => items;
    internal object? SelectedItem => selectedIndex >= 0 && selectedIndex < items.Length ? items[selectedIndex] : null;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int SelectedIndex
    {
        get => selectedIndex;
        set { if (value < -1 || value >= items.Length) throw new ArgumentOutOfRangeException(nameof(value)); bool changed = selectedIndex != value; selectedIndex = value; Text = (SelectedItem?.ToString() ?? placeholder) + "  ▾"; if (changed) SelectedIndexChanged?.Invoke(this, EventArgs.Empty); }
    }
    internal void SetItems(IEnumerable<object> values) { items = values.ToArray(); SelectedIndex = -1; }
    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        var menu = new ContextMenuStrip();
        for (int i = 0; i < items.Length; i++)
        {
            int index = i;
            var item = new ToolStripMenuItem(items[i].ToString()) { Checked = i == selectedIndex };
            item.Click += (_, _) => SelectedIndex = index; menu.Items.Add(item);
        }
        menu.Closed += (_, _) => menu.Dispose();
        menu.Show(this, new Point(0, Height));
    }
}
