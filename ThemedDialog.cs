using System.Drawing;
using System.Windows.Forms;

namespace ProcessMuterByChannel
{
    /// <summary>
    /// Окно подтверждения в стиле OBS с кнопками ОК и Отмена. Стандартный MessageBox не темизируется,
    /// поэтому окно нарисовано теми же контролами, что и настройки. Фокус по умолчанию стоит на «Отмена»:
    /// случайный Enter не должен подтверждать удаление
    /// </summary>
    internal sealed class ThemedDialog : Form
    {
        private readonly ThemedButton _ok;
        private readonly ThemedButton _cancel;

        private ThemedDialog(string title, string message, string okText, string cancelText, ButtonTone okTone)
        {
            AutoScaleMode = AutoScaleMode.None;
            Text = title;
            ShowIcon = false;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = ObsTheme.BaseBg;
            ForeColor = ObsTheme.Text;
            Font = Ui.Font();

            int pad = Ui.Px(20);
            int textWidth = Ui.Px(380);

            var heading = new Label
            {
                Text = title,
                AutoSize = false,
                Font = Ui.Font(FontStyle.Bold, 1.1f),
                ForeColor = ObsTheme.Text,
                BackColor = ObsTheme.BaseBg,
                Bounds = new Rectangle(pad, pad, textWidth, Ui.Px(28)),
                TextAlign = ContentAlignment.MiddleLeft
            };

            var body = new Label
            {
                Text = message,
                AutoSize = false,
                ForeColor = ObsTheme.TextMuted,
                BackColor = ObsTheme.BaseBg
            };
            var size = TextRenderer.MeasureText(message, body.Font, new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            body.Bounds = new Rectangle(pad, pad + Ui.Px(36), textWidth, size.Height + Ui.Px(4));

            int buttonsTop = body.Bottom + Ui.Px(22);
            int buttonW = Ui.Px(110), buttonH = Ui.Px(32), gap = Ui.Px(10);
            _cancel = new ThemedButton { Text = cancelText, Bounds = new Rectangle(pad + textWidth - buttonW, buttonsTop, buttonW, buttonH) };
            _ok = new ThemedButton { Text = okText, Tone = okTone, Bounds = new Rectangle(_cancel.Left - gap - buttonW, buttonsTop, buttonW, buttonH) };
            _ok.Click += (s, e) => DialogResult = DialogResult.OK;
            _cancel.Click += (s, e) => DialogResult = DialogResult.Cancel;

            Controls.Add(heading);
            Controls.Add(body);
            Controls.Add(_ok);
            Controls.Add(_cancel);

            ClientSize = new Size(pad * 2 + textWidth, buttonsTop + buttonH + pad);
            ActiveControl = _cancel;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Ui.ApplyWindowChrome(Handle);
        }

        // Esc закрывает окно как «Отмена», Enter нажимает ту кнопку, на которой стоит фокус
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                return true;
            }

            if (keyData == Keys.Enter)
            {
                DialogResult = _ok.Focused ? DialogResult.OK : DialogResult.Cancel;
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>true, если пользователь нажал ОК</summary>
        public static bool Confirm(IWin32Window owner, string title, string message, ButtonTone okTone = ButtonTone.Normal)
        {
            using var dialog = new ThemedDialog(title, message, Loc.Ok, Loc.Cancel, okTone);
            return dialog.ShowDialog(owner) == DialogResult.OK;
        }
    }
}
