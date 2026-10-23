using System;
using System.Drawing;
using System.Windows.Forms;

namespace CadLockPlugin.UI
{
    public class LockForm : Form
    {
        public enum LockMode
        {
            SelectObjects,
            EntireDrawing,
            BatchFiles
        }

        public LockMode SelectedMode { get; private set; } = LockMode.EntireDrawing;
        public CadLockOptions Options { get; private set; } = new CadLockOptions();

        private RadioButton rbSelect = null!;
        private RadioButton rbEntire = null!;
        private RadioButton rbBatch = null!;

        private NumericUpDown numSplit = null!;
        private NumericUpDown numRepeat = null!;

        private CheckBox chkSnap = null!;
        private CheckBox chkPlot = null!;
        private CheckBox chkCopy = null!;
        private CheckBox chkDelete = null!;

        private Button btnOk = null!;
        private Button btnCancel = null!;

        public LockForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "CAD 图纸加密保护设置 (LOCK)";
            this.Size = new Size(460, 480);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            // 1. 加密模式分组
            var grpMode = new GroupBox
            {
                Text = "加密模式",
                Location = new Point(20, 15),
                Size = new Size(405, 95)
            };

            rbSelect = new RadioButton
            {
                Text = "① 选择对象加密 (手动框选局部图形)",
                Location = new Point(20, 25),
                Size = new Size(350, 20)
            };
            rbEntire = new RadioButton
            {
                Text = "② 全图自动加密 (推荐，加密当前活动图纸全图)",
                Location = new Point(20, 47),
                Size = new Size(350, 20),
                Checked = true
            };
            rbBatch = new RadioButton
            {
                Text = "③ 批量图纸加密 (无需打开，后台自动批量保存 _lock.dwg)",
                Location = new Point(20, 69),
                Size = new Size(380, 20)
            };

            grpMode.Controls.Add(rbSelect);
            grpMode.Controls.Add(rbEntire);
            grpMode.Controls.Add(rbBatch);
            this.Controls.Add(grpMode);

            // 2. 加密参数分组
            var grpParams = new GroupBox
            {
                Text = "加密强度参数",
                Location = new Point(20, 120),
                Size = new Size(405, 80)
            };

            var lblSplit = new Label
            {
                Text = "分块数量:",
                Location = new Point(20, 30),
                AutoSize = true
            };
            numSplit = new NumericUpDown
            {
                Location = new Point(90, 27),
                Size = new Size(70, 23),
                Minimum = 1,
                Maximum = 100,
                Value = 10
            };

            var lblRepeat = new Label
            {
                Text = "加密次数:",
                Location = new Point(210, 30),
                AutoSize = true
            };
            numRepeat = new NumericUpDown
            {
                Location = new Point(280, 27),
                Size = new Size(70, 23),
                Minimum = 1,
                Maximum = 300,
                Value = 10
            };

            var lblParamTip = new Label
            {
                Text = "提示：嵌套超过100次后普通CAD可能卡顿崩溃，需用快速看图软件查看。",
                ForeColor = Color.DimGray,
                Location = new Point(20, 55),
                AutoSize = true
            };

            grpParams.Controls.Add(lblSplit);
            grpParams.Controls.Add(numSplit);
            grpParams.Controls.Add(lblRepeat);
            grpParams.Controls.Add(numRepeat);
            grpParams.Controls.Add(lblParamTip);
            this.Controls.Add(grpParams);

            // 3. 安全与权限防护
            var grpSecurity = new GroupBox
            {
                Text = "高级安全防护限制",
                Location = new Point(20, 210),
                Size = new Size(405, 110)
            };

            chkSnap = new CheckBox
            {
                Text = "不可捕捉 (图元不可测量与吸附点)",
                Location = new Point(20, 25),
                Size = new Size(350, 22)
            };
            chkPlot = new CheckBox
            {
                Text = "不可打印 (屏幕可见，但执行 PLOT 打印输出空白)",
                Location = new Point(20, 48),
                Size = new Size(350, 22)
            };
            chkCopy = new CheckBox
            {
                Text = "不可复制 (禁止跨图纸复制、写块与剪贴板)",
                Location = new Point(20, 71),
                Size = new Size(350, 22)
            };
            chkDelete = new CheckBox
            {
                Text = "不可删除 (锁定图形，阻止执行 ERASE 删除)",
                Location = new Point(210, 25),
                Size = new Size(180, 22)
            };

            grpSecurity.Controls.Add(chkSnap);
            grpSecurity.Controls.Add(chkPlot);
            grpSecurity.Controls.Add(chkCopy);
            grpSecurity.Controls.Add(chkDelete);
            this.Controls.Add(grpSecurity);

            // 4. 警示提示
            var lblWarn = new Label
            {
                Text = "【警告】加密操作不可逆，执行加密前请务必自行备份原图纸！",
                ForeColor = Color.Red,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                Location = new Point(20, 335),
                AutoSize = true
            };
            this.Controls.Add(lblWarn);

            // 5. 底部按钮
            btnOk = new Button
            {
                Text = "开始加密",
                DialogResult = DialogResult.OK,
                Location = new Point(230, 380),
                Size = new Size(95, 32),
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnOk.Click += BtnOk_Click;

            btnCancel = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Location = new Point(335, 380),
                Size = new Size(90, 32)
            };

            this.Controls.Add(btnOk);
            this.Controls.Add(btnCancel);
            this.AcceptButton = btnOk;
            this.CancelButton = btnCancel;
        }

        private void BtnOk_Click(object? sender, EventArgs e)
        {
            if (rbSelect.Checked) SelectedMode = LockMode.SelectObjects;
            else if (rbBatch.Checked) SelectedMode = LockMode.BatchFiles;
            else SelectedMode = LockMode.EntireDrawing;

            Options = new CadLockOptions
            {
                SplitCount = (int)numSplit.Value,
                RepeatCount = (int)numRepeat.Value,
                DisableSnap = chkSnap.Checked,
                DisablePlot = chkPlot.Checked,
                DisableCopy = chkCopy.Checked,
                DisableDelete = chkDelete.Checked
            };
        }
    }
}
