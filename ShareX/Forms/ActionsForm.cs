#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib;
using ShareX.Properties;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace ShareX
{
    public partial class ActionsForm : Form
    {
        public ExternalProgram FileAction { get; private set; }

        private Label lblHdrPolicy;
        private ComboBox cbHdrPolicy;

        public ActionsForm() : this(new ExternalProgram())
        {
        }

        public ActionsForm(ExternalProgram fileAction)
        {
            InitializeComponent();
            InitializeHdrPolicyControls();
            ShareXResources.ApplyTheme(this, true);

            FileAction = fileAction;
            txtName.Text = fileAction.Name ?? "";
            txtPath.Text = fileAction.Path ?? "";
            txtArguments.Text = fileAction.Args ?? "";
            CodeMenu.Create<CodeMenuEntryActions>(txtArguments);
            txtOutputExtension.Text = fileAction.OutputExtension ?? "";
            txtExtensions.Text = fileAction.Extensions ?? "";
            cbHiddenWindow.Checked = fileAction.HiddenWindow;
            cbDeleteInputFile.Checked = fileAction.DeleteInputFile;
            cbHdrPolicy.SelectedIndex = Math.Clamp(
                (int)fileAction.HdrPolicy,
                0,
                cbHdrPolicy.Items.Count - 1);
        }

        private void InitializeHdrPolicyControls()
        {
            const int additionalHeight = 38;
            int controlTop = cbDeleteInputFile.Bottom + 10;

            lblHdrPolicy = new Label
            {
                AutoSize = true,
                Left = lblOutputExtension.Left,
                Top = controlTop + 4,
                Text = "HDR files:"
            };
            cbHdrPolicy = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Left = txtOutputExtension.Left,
                Top = controlTop,
                Width = txtOutputExtension.Width
            };
            cbHdrPolicy.Items.AddRange(new object[]
            {
                "Convert to SDR before running (default)",
                "Allow HDR without validation",
                "Require verifiable HDR output"
            });

            btnOK.Top += additionalHeight;
            btnCancel.Top += additionalHeight;
            ClientSize = new Size(ClientSize.Width, ClientSize.Height + additionalHeight);
            Controls.Add(lblHdrPolicy);
            Controls.Add(cbHdrPolicy);
        }

        private void btnPathBrowse_Click(object sender, EventArgs e)
        {
            FileHelpers.BrowseFile(txtPath, "", true);
        }

        private void txtOutputExtension_TextChanged(object sender, EventArgs e)
        {
            cbDeleteInputFile.Enabled = txtOutputExtension.TextLength > 0;
        }

        private void btnOK_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(txtName.Text))
            {
                MessageBox.Show(Resources.ActionsForm_btnOK_Click_Name_can_t_be_empty_, "ShareX", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrEmpty(txtPath.Text))
            {
                MessageBox.Show(Resources.ActionsForm_btnOK_Click_File_path_can_t_be_empty_, "ShareX", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            FileAction.Name = txtName.Text;
            FileAction.Path = txtPath.Text;
            FileAction.Args = txtArguments.Text;
            FileAction.Extensions = txtExtensions.Text;
            FileAction.OutputExtension = txtOutputExtension.Text;
            FileAction.HiddenWindow = cbHiddenWindow.Checked;
            FileAction.DeleteInputFile = cbDeleteInputFile.Checked;
            FileAction.HdrPolicy = (ExternalProgramHdrPolicy)Math.Max(0, cbHdrPolicy.SelectedIndex);

            DialogResult = DialogResult.OK;
            Close();
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }
}
