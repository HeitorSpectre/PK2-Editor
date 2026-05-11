using System.Drawing;
using System.Windows.Forms;

namespace PK2Editor;

internal sealed class ProgressForm : Form
{
    private readonly ProgressBar _bar;
    private readonly Label _label;
    private readonly Button _cancel;
    public bool IsCancelled { get; private set; }

    public ProgressForm(string title, int max)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Width = 480;
        Height = 140;

        _label = new Label { Dock = DockStyle.Top, Height = 28, Padding = new Padding(8, 8, 0, 0) };
        _bar = new ProgressBar { Dock = DockStyle.Top, Height = 24, Maximum = Math.Max(1, max) };
        _cancel = new Button { Text = "Cancel", Dock = DockStyle.Bottom, Height = 32 };
        _cancel.Click += (_, _) => IsCancelled = true;

        Controls.Add(_bar);
        Controls.Add(_label);
        Controls.Add(_cancel);
    }

    public void SetProgress(int value, string message)
    {
        if (value > _bar.Maximum) value = _bar.Maximum;
        _bar.Value = value;
        _label.Text = message;
    }
}
