using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ClassroomLive.Extension
{
    internal sealed class LanguageSelectionDialog : Form
    {
        private readonly Label title = new Label();
        private readonly Label body = new Label();
        private readonly ComboBox languages = new ComboBox();
        private readonly Button confirm = new Button();
        private readonly Action changed;

        internal LanguageSelectionDialog(Action changed)
        {
            this.changed = changed;
            Text = "Classroom Live";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(410, 205);
            Padding = new Padding(24);
            Font = SystemFonts.MessageBoxFont;

            title.SetBounds(24, 24, 362, 28);
            title.Font = new Font(Font, FontStyle.Bold);
            body.SetBounds(24, 58, 362, 35);
            languages.SetBounds(24, 105, 362, 28);
            languages.DropDownStyle = ComboBoxStyle.DropDownList;
            languages.DisplayMember = "Name";
            languages.Items.AddRange(ExtensionLocalization.Options.Cast<object>().ToArray());
            confirm.SetBounds(286, 157, 100, 32);
            confirm.DialogResult = DialogResult.OK;
            AcceptButton = confirm;
            Controls.AddRange(new Control[] { title, body, languages, confirm });

            var selected = ExtensionLocalization.Options.ToList().FindIndex(option => option.Code == ExtensionLocalization.Code);
            languages.SelectedIndex = Math.Max(0, selected);
            languages.SelectedIndexChanged += OnLanguageChanged;
            ApplyText();
        }

        internal string SelectedCode
        {
            get { return ((LocaleOption)languages.SelectedItem).Code; }
        }

        private void OnLanguageChanged(object sender, EventArgs e)
        {
            ExtensionLocalization.Apply(SelectedCode);
            ApplyText();
            changed();
        }

        private void ApplyText()
        {
            title.Text = ExtensionLocalization.T("language.prompt.title");
            body.Text = ExtensionLocalization.T("language.prompt.body");
            confirm.Text = ExtensionLocalization.T("language.confirm");
        }
    }
}
