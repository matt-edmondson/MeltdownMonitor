using MeltdownMonitor.Core.Persistence;

namespace MeltdownMonitor.App;

/// <summary>
/// Tiny dialog with four label buttons and an optional notes field.
/// The user opens it from the tray menu to record how they're feeling.
/// </summary>
public sealed class AnnotationDialog : Form
{
	public AnnotationLabel SelectedLabel { get; private set; }
	public string? Notes { get; private set; }

	public AnnotationDialog()
	{
		Text = "How are you feeling?";
		FormBorderStyle = FormBorderStyle.FixedDialog;
		MaximizeBox = false;
		MinimizeBox = false;
		StartPosition = FormStartPosition.CenterScreen;
		Size = new Size(360, 200);
		TopMost = true;

		var layout = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			Padding = new Padding(16),
			FlowDirection = FlowDirection.TopDown,
		};

		var buttonRow = new FlowLayoutPanel
		{
			AutoSize = true,
			FlowDirection = FlowDirection.LeftToRight,
		};

		foreach (AnnotationLabel label in Enum.GetValues<AnnotationLabel>())
		{
			var captured = label;
			var btn = new Button
			{
				Text = label.ToString(),
				Width = 80,
			};
			btn.Click += (_, _) =>
			{
				SelectedLabel = captured;
				DialogResult = DialogResult.OK;
				Close();
			};
			buttonRow.Controls.Add(btn);
		}

		var notesBox = new TextBox
		{
			PlaceholderText = "Optional notes…",
			Width = 320,
		};
		notesBox.TextChanged += (_, _) => Notes = string.IsNullOrWhiteSpace(notesBox.Text) ? null : notesBox.Text;

		layout.Controls.Add(new Label { Text = "Select one:", AutoSize = true });
		layout.Controls.Add(buttonRow);
		layout.Controls.Add(notesBox);

		Controls.Add(layout);
	}
}
