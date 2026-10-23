using System;
using System.Drawing;
using System.Windows.Forms;

namespace CadLockPlugin.UI
{
    public class LockForm : Form
    {
        public enum ActionType
        {
            None,
            SelectObjects,
            EntireDrawing,
            BatchFiles
        }

        public ActionType UserAction { get; private set; } = ActionType.None;
        public CadLockOptions Options { get; private set; } = new CadLockOptions();

        private TextBox txtSplit = null!;
        private TextBox txtRepeat = null!;

        private CheckBox chkExplode = null!;
        private CheckBox chkSnap = null!;
        private CheckBox chkPlot = null!;
        private CheckBox chkCopy = null!;
        private CheckBox chkDelete = null!;

        private Button btnSelectLock = null!;
        private Button btnEntireLock = null!;
        private Button btnBatchLock = null!;

        private ToolTip toolTip = null!;

        public LockForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "Lock图纸加密保护 —— 专业版";
            this.Size = new Size(480, 470);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(245, 246, 248);
            this.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            toolTip = new ToolTip
            {
                AutoPopDelay = 8000,
                InitialDelay = 400,
                ReshowDelay = 200,
                ShowAlways = true
            };

            // 1. 顶部加密参数配置组
            var grpParams = new GroupBox
            {
                Text = "加密参数设置",
                Location = new Point(18, 12),
                Size = new Size(428, 78),
                BackColor = Color.White
            };

            var lblSplit = new Label
            {
                Text = "分块数量:",
                Location = new Point(20, 30),
                AutoSize = true
            };
            txtSplit = new TextBox
            {
                Text = "100",
                Location = new Point(88, 27),
                Size = new Size(90, 23)
            };
            toolTip.SetToolTip(txtSplit, "将选择加密的图形分散组合成不可炸开图形，数值越大加密越复杂！(默认: 100)");
            toolTip.SetToolTip(lblSplit, "将选择加密的图形分散组合成不可炸开图形，数值越大加密越复杂！(默认: 100)");

            var lblRepeat = new Label
            {
                Text = "加密次数:",
                Location = new Point(225, 30),
                AutoSize = true
            };
            txtRepeat = new TextBox
            {
                Text = "10",
                Location = new Point(295, 27),
                Size = new Size(90, 23)
            };
            toolTip.SetToolTip(txtRepeat, "循环嵌套加密次数，超过200次普通CAD可能无法打开，需用看图软件查看！(默认: 10)");
            toolTip.SetToolTip(lblRepeat, "循环嵌套加密次数，超过200次普通CAD可能无法打开，需用看图软件查看！(默认: 10)");

            grpParams.Controls.Add(lblSplit);
            grpParams.Controls.Add(txtSplit);
            grpParams.Controls.Add(lblRepeat);
            grpParams.Controls.Add(txtRepeat);
            this.Controls.Add(grpParams);

            // 2. 安全防护级别复选区
            var grpSecurity = new GroupBox
            {
                Text = "安全保护选项 (鼠标悬停可查看说明)",
                Location = new Point(18, 100),
                Size = new Size(428, 115),
                BackColor = Color.White
            };

            chkExplode = new CheckBox
            {
                Text = "不可炸开 (核心)",
                Checked = true,
                Enabled = false, // 核心必选项，不可取消
                Location = new Point(20, 26),
                Size = new Size(130, 22),
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold)
            };
            toolTip.SetToolTip(chkExplode, "加密的基本原则，将图元包装为禁止分解(Explodable=false)的匿名块，此项必须开启。");

            chkSnap = new CheckBox
            {
                Text = "不可捕捉",
                Location = new Point(155, 26),
                Size = new Size(120, 22)
            };
            toolTip.SetToolTip(chkSnap, "选不中，也无法捕捉点，仅供图纸查看与打印，无法精确测量拾取。");

            chkPlot = new CheckBox
            {
                Text = "不可打印",
                Location = new Point(290, 26),
                Size = new Size(120, 22)
            };
            toolTip.SetToolTip(chkPlot, "屏幕上正常显示，但打印(PLOT)或导出PDF时加密内容输出为空白。");

            chkCopy = new CheckBox
            {
                Text = "不可复制",
                Location = new Point(20, 62),
                Size = new Size(130, 22)
            };
            toolTip.SetToolTip(chkCopy, "禁止通过剪贴板(Ctrl+C)、跨图纸拖放或写块(WBLOCK)将加密图元提取出去。");

            chkDelete = new CheckBox
            {
                Text = "不可删除",
                Location = new Point(155, 62),
                Size = new Size(120, 22)
            };
            toolTip.SetToolTip(chkDelete, "对加密图元启用删除保护，执行 ERASE 命令时底层自动撤销删除。");

            grpSecurity.Controls.Add(chkExplode);
            grpSecurity.Controls.Add(chkSnap);
            grpSecurity.Controls.Add(chkPlot);
            grpSecurity.Controls.Add(chkCopy);
            grpSecurity.Controls.Add(chkDelete);
            this.Controls.Add(grpSecurity);

            // 3. 核心执行大按钮区 (3 个一键操作按钮)
            btnSelectLock = new Button
            {
                Text = "① 选择对象加密",
                Location = new Point(18, 230),
                Size = new Size(136, 42),
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            toolTip.SetToolTip(btnSelectLock, "点击后返回CAD界面手动框选局部图元进行加密，需要手动保存图纸生效。");
            btnSelectLock.Click += (s, e) => ExecuteAction(ActionType.SelectObjects);

            btnEntireLock = new Button
            {
                Text = "② 全图自动加密",
                Location = new Point(164, 230),
                Size = new Size(136, 42),
                BackColor = Color.FromArgb(40, 167, 69),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            toolTip.SetToolTip(btnEntireLock, "无需手动选择，直接对当前图纸当前模型空间全图进行自动加密，需手动保存生效。");
            btnEntireLock.Click += (s, e) => ExecuteAction(ActionType.EntireDrawing);

            btnBatchLock = new Button
            {
                Text = "③ 批量图纸加密",
                Location = new Point(310, 230),
                Size = new Size(136, 42),
                BackColor = Color.FromArgb(108, 117, 125),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            toolTip.SetToolTip(btnBatchLock, "无需逐个打开图纸，后台自动批量读取多个DWG文件加密并另存为 文件名_lock.dwg。");
            btnBatchLock.Click += (s, e) => ExecuteAction(ActionType.BatchFiles);

            this.Controls.Add(btnSelectLock);
            this.Controls.Add(btnEntireLock);
            this.Controls.Add(btnBatchLock);

            // 4. 温馨警示提示框
            var pnlTip = new Panel
            {
                Location = new Point(18, 288),
                Size = new Size(428, 125),
                BackColor = Color.FromArgb(254, 243, 226),
                BorderStyle = BorderStyle.FixedSingle
            };

            var lblTip = new Label
            {
                Text = "【重要温馨提醒】\n" +
                       "1. 加密操作为破坏性封装不可逆，加密前必须自行备份原图纸！\n" +
                       "2. 加密后部分文字或图形若暂时不显示，属于CAD刷新机制，保存图纸后重新打开即可正常查看。\n" +
                       "3. 循环加密次数若超过100次，原生CAD打开可能极度卡顿，此时建议使用快速看图软件观看。\n" +
                       "4. 请勿对同一张图纸进行二次重复加密。",
                ForeColor = Color.FromArgb(160, 80, 0),
                Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular),
                Location = new Point(12, 10),
                Size = new Size(402, 105)
            };
            pnlTip.Controls.Add(lblTip);
            this.Controls.Add(pnlTip);
        }

        private void ExecuteAction(ActionType action)
        {
            // 解析分块数量
            if (!int.TryParse(txtSplit.Text.Trim(), out int splitVal) || splitVal < 1)
            {
                splitVal = 100;
            }

            // 解析循环嵌套次数
            if (!int.TryParse(txtRepeat.Text.Trim(), out int repeatVal) || repeatVal < 1)
            {
                repeatVal = 10;
            }

            Options = new CadLockOptions
            {
                SplitCount = splitVal,
                RepeatCount = repeatVal,
                DisableSnap = chkSnap.Checked,
                DisablePlot = chkPlot.Checked,
                DisableCopy = chkCopy.Checked,
                DisableDelete = chkDelete.Checked
            };

            UserAction = action;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }
}
