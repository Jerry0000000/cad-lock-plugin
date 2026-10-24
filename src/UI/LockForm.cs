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
            BatchFiles,
            UnlockDrawing,
            BatchUnlock
        }

        /// <summary>内容区固定宽度，窗体高度由 AutoSize 根据实际内容自适应，避免按钮被遮挡</summary>
        private const int ContentWidth = 548;

        public ActionType UserAction { get; private set; } = ActionType.None;
        public CadLockOptions Options { get; private set; } = new CadLockOptions();

        private TextBox txtSplit = null!;
        private TextBox txtRepeat = null!;

        private CheckBox chkSelect = null!;
        private CheckBox chkPlot = null!;
        private CheckBox chkCopy = null!;
        private CheckBox chkDelete = null!;
        private CheckBox chkReplace = null!;

        private Label lblTip = null!;

        private ToolTip toolTip = null!;

        public LockForm()
        {
            InitializeComponent();
        }

        private static Font UiFont(float size, FontStyle style = FontStyle.Regular)
        {
            return new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Point);
        }

        private void InitializeComponent()
        {
            SuspendLayout();

            Text = "Lock 图纸加密保护 —— 专业版";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(240, 242, 246);
            Font = UiFont(9F);
            AutoScaleMode = AutoScaleMode.Font;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            toolTip = new ToolTip
            {
                AutoPopDelay = 10000,
                InitialDelay = 350,
                ReshowDelay = 150,
                ShowAlways = true
            };

            // 根容器：纵向流式布局 + 自适应高度，彻底避免控件互相遮挡
            var root = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Location = Point.Empty,
                BackColor = Color.FromArgb(240, 242, 246),
                Padding = new Padding(14, 12, 14, 14)
            };

            root.Controls.Add(BuildHeader());

            // 1. 顶部加密参数配置组
            root.Controls.Add(BuildParamsGroup());

            // 2. 安全防护级别复选区
            root.Controls.Add(BuildSecurityGroup());

            // 3. 文件输出方式 (核心新增：是否替换原文件)
            root.Controls.Add(BuildOutputGroup());

            // 4. 加密操作按钮区
            root.Controls.Add(BuildLockButtonsGroup());

            // 5. 解锁 / 还原按钮区
            root.Controls.Add(BuildUnlockButtonsGroup());

            // 6. 温馨警示提示
            root.Controls.Add(BuildTipLabel());

            Controls.Add(root);

            ResumeLayout(true);
        }

        private Control BuildHeader()
        {
            var header = new Panel
            {
                Size = new Size(ContentWidth, 66),
                BackColor = Color.FromArgb(31, 56, 94),
                Margin = new Padding(0, 0, 0, 12)
            };

            header.Controls.Add(new Label
            {
                Text = "CAD 图纸加密保护",
                ForeColor = Color.White,
                Font = UiFont(14F, FontStyle.Bold),
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(16, 9)
            });

            header.Controls.Add(new Label
            {
                Text = "防炸开 · 防复制 · 防打印 · 防删除 · 批量处理 · 一键解锁还原",
                ForeColor = Color.FromArgb(176, 195, 222),
                Font = UiFont(8.5F),
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(18, 41)
            });

            return header;
        }

        private Control BuildParamsGroup()
        {
            var grp = new GroupBox
            {
                Text = "  加密参数设置  ",
                Size = new Size(ContentWidth, 74),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(60, 70, 85),
                Margin = new Padding(0, 0, 0, 10)
            };

            var tlp = new TableLayoutPanel
            {
                ColumnCount = 4,
                RowCount = 1,
                Dock = DockStyle.Fill,
                Padding = new Padding(12, 2, 12, 8),
                BackColor = Color.Transparent
            };
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var lblSplit = new Label
            {
                Text = "分块数量:",
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0)
            };

            txtSplit = new TextBox
            {
                Text = "100",
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 9, 10, 9)
            };

            var lblRepeat = new Label
            {
                Text = "加密次数:",
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(14, 0, 0, 0)
            };

            txtRepeat = new TextBox
            {
                Text = "10",
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 9, 10, 9)
            };

            string splitTip = "把图形分散打包成多份不可炸开图块，数值越大越难逆向，默认 100。";
            string repeatTip = "循环嵌套加密次数，一般 10~20 即可；超过 200 次普通 CAD 打开会非常卡。";
            toolTip.SetToolTip(txtSplit, splitTip);
            toolTip.SetToolTip(lblSplit, splitTip);
            toolTip.SetToolTip(txtRepeat, repeatTip);
            toolTip.SetToolTip(lblRepeat, repeatTip);

            tlp.Controls.Add(lblSplit, 0, 0);
            tlp.Controls.Add(txtSplit, 1, 0);
            tlp.Controls.Add(lblRepeat, 2, 0);
            tlp.Controls.Add(txtRepeat, 3, 0);

            grp.Controls.Add(tlp);
            return grp;
        }

        private Control BuildSecurityGroup()
        {
            var grp = new GroupBox
            {
                Text = "  安全保护选项（鼠标悬停可查看说明）  ",
                Size = new Size(ContentWidth, 76),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(60, 70, 85),
                Margin = new Padding(0, 0, 0, 10)
            };

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoScroll = false,
                Padding = new Padding(10, 4, 10, 6),
                BackColor = Color.Transparent
            };

            var chkExplode = new CheckBox
            {
                Text = "不可炸开 (核心)",
                Checked = true,
                Enabled = false, // 核心必选项，不可取消
                AutoSize = true,
                Font = UiFont(9F, FontStyle.Bold),
                Margin = new Padding(4, 6, 18, 6)
            };
            toolTip.SetToolTip(chkExplode, "加密的基本原则：图元被包装为禁止分解(Explodable=false)的块，此项必须开启。");

            chkSelect = new CheckBox
            {
                Text = "不可选中 (推荐)",
                Checked = true,
                AutoSize = true,
                Margin = new Padding(4, 6, 18, 6)
            };
            toolTip.SetToolTip(chkSelect,
                "加密块放入锁定图层：锁定图层上的对象不可选中/编辑/删除，这是 AutoCAD 原生行为，\n" +
                "锁状态随图纸保存，没装本插件的电脑上同样选不中。\n" +
                "注意：图层锁定不屏蔽对象捕捉；被手动解锁时本插件会自动锁回。");

            chkPlot = new CheckBox
            {
                Text = "不可打印",
                AutoSize = true,
                Margin = new Padding(4, 6, 18, 6)
            };
            toolTip.SetToolTip(chkPlot, "屏幕上正常显示，打印(PLOT)或导出PDF时加密内容输出为空白。");

            chkCopy = new CheckBox
            {
                Text = "不可复制",
                AutoSize = true,
                Margin = new Padding(4, 6, 18, 6)
            };
            toolTip.SetToolTip(chkCopy, "禁止通过剪贴板(Ctrl+C)、跨图纸拖放或写块(WBLOCK)把加密图元提取出去。");

            chkDelete = new CheckBox
            {
                Text = "不可删除",
                AutoSize = true,
                Margin = new Padding(4, 6, 18, 6)
            };
            toolTip.SetToolTip(chkDelete, "对加密图元启用删除保护，执行 ERASE 命令后底层自动撤销删除。");

            flow.Controls.Add(chkExplode);
            flow.Controls.Add(chkSelect);
            flow.Controls.Add(chkPlot);
            flow.Controls.Add(chkCopy);
            flow.Controls.Add(chkDelete);

            grp.Controls.Add(flow);
            return grp;
        }

        private Control BuildOutputGroup()
        {
            var grp = new GroupBox
            {
                Text = "  文件输出方式  ",
                Size = new Size(ContentWidth, 86),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(60, 70, 85),
                Margin = new Padding(0, 0, 0, 10)
            };

            chkReplace = new CheckBox
            {
                Text = "直接替换原文件（不再生成 _lock / _unlock 后缀的副本）",
                AutoSize = true,
                Font = UiFont(9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(178, 60, 40),
                Location = new Point(16, 24)
            };
            toolTip.SetToolTip(chkReplace,
                "勾选后：批量加密/解锁直接覆盖所选 DWG 文件，不产生新文件。\n" +
                "不勾选：加密另存为 文件名_lock.dwg，解锁另存为 文件名_unlock.dwg。\n" +
                "提示：替换为覆盖操作，建议先备份重要图纸。");

            var lblOutTip = new Label
            {
                Text = "仅对「批量加密 / 批量解锁」生效；支持 DWG、Excel、Word。",
                AutoSize = true,
                ForeColor = Color.FromArgb(120, 128, 140),
                Font = UiFont(8.5F),
                Location = new Point(16, 52)
            };

            grp.Controls.Add(chkReplace);
            grp.Controls.Add(lblOutTip);
            return grp;
        }

        private Control BuildLockButtonsGroup()
        {
            var grp = new GroupBox
            {
                Text = "  加密操作  ",
                Size = new Size(ContentWidth, 86),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(60, 70, 85),
                Margin = new Padding(0, 0, 0, 10)
            };

            var tlp = new TableLayoutPanel
            {
                ColumnCount = 3,
                RowCount = 1,
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 4, 8, 8),
                BackColor = Color.Transparent
            };
            for (int i = 0; i < 3; i++)
            {
                tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
            }
            tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            tlp.Controls.Add(CreateButton("① 选择对象加密", Color.FromArgb(0, 122, 204),
                "点击后返回 CAD 界面框选局部图元加密，完成后需手动保存图纸生效。",
                (s, e) => ExecuteAction(ActionType.SelectObjects)), 0, 0);

            tlp.Controls.Add(CreateButton("② 全图自动加密", Color.FromArgb(40, 167, 69),
                "无需手动选择，直接对当前空间全部图元自动加密，完成后需手动保存生效。",
                (s, e) => ExecuteAction(ActionType.EntireDrawing)), 1, 0);

            tlp.Controls.Add(CreateButton("③ 批量加密 (图纸/文档)", Color.FromArgb(90, 100, 116),
                "可多选 DWG 图纸与 Excel/Word 文档批量处理：\n" +
                "DWG 加密图形；Excel 清除数字签名、公式转为固定值并保护工作表；Word 清除签名并设为只读保护，\n" +
                "处理后文字与数字均不可编辑。勾选「替换原文件」时直接覆盖，否则另存为 文件名_lock。",
                (s, e) => ExecuteAction(ActionType.BatchFiles)), 2, 0);

            grp.Controls.Add(tlp);
            return grp;
        }

        private Control BuildUnlockButtonsGroup()
        {
            var grp = new GroupBox
            {
                Text = "  解锁 / 还原  ",
                Size = new Size(ContentWidth, 86),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(60, 70, 85),
                Margin = new Padding(0, 0, 0, 10)
            };

            var tlp = new TableLayoutPanel
            {
                ColumnCount = 2,
                RowCount = 1,
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 4, 8, 8),
                BackColor = Color.Transparent
            };
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            tlp.Controls.Add(CreateButton("④ 解锁当前图纸", Color.FromArgb(214, 138, 42),
                "把当前图纸中的加密块还原成普通图元，并移除防护钩子，完成后请手动保存。",
                (s, e) => ExecuteAction(ActionType.UnlockDrawing)), 0, 0);

            tlp.Controls.Add(CreateButton("⑤ 批量文件解锁", Color.FromArgb(122, 96, 168),
                "批量还原 DWG 图纸与 Excel/Word 文档（解除保护/恢复可编辑）；\n" +
                "勾选「替换原文件」时直接覆盖，否则另存为 文件名_unlock。",
                (s, e) => ExecuteAction(ActionType.BatchUnlock)), 1, 0);

            grp.Controls.Add(tlp);
            return grp;
        }

        private Control BuildTipLabel()
        {
            lblTip = new Label
            {
                Text = "【重要提醒】\n" +
                       "1. 加密为破坏性封装，勾选「替换原文件」前请务必备份图纸。\n" +
                       "2. 加密后个别文字/图形暂时不显示属 CAD 刷新机制，保存并重新打开即可正常显示。\n" +
                       "3. 加密次数不宜过大，否则原生 CAD 打开会非常卡顿。\n" +
                       "4. 同一张图纸请勿重复加密；如需重做，先用「解锁」还原。\n" +
                       "5. 加密图层被手动解锁后会被自动恢复锁定；重新打开图纸也会自动恢复全部防护。\n" +
                       "6. Excel/Word 文档会清除数字签名；Excel 公式转为固定值，文档加保护后文字与数字不可编辑。",
                AutoSize = false,
                Size = new Size(ContentWidth - 8, 96),
                ForeColor = Color.FromArgb(150, 78, 0),
                BackColor = Color.FromArgb(255, 247, 230),
                Font = UiFont(8.5F),
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(12, 10, 12, 10),
                Margin = new Padding(0, 2, 0, 0)
            };
            return lblTip;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // 自动换行文本在不同 DPI 下高度不同，这里按实际字体测量，避免提示文字被截断
            if (lblTip != null)
            {
                int textWidth = Math.Max(100, lblTip.Width - lblTip.Padding.Horizontal - 4);
                Size measured = TextRenderer.MeasureText(
                    lblTip.Text,
                    lblTip.Font,
                    new Size(textWidth, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);

                lblTip.Height = measured.Height + lblTip.Padding.Vertical + 4;
                PerformLayout();
            }
        }

        private Button CreateButton(string text, Color backColor, string tip, EventHandler onClick)
        {
            var btn = new Button
            {
                Text = text,
                Dock = DockStyle.Fill,
                Margin = new Padding(6, 4, 6, 4),
                BackColor = backColor,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = UiFont(9.5F, FontStyle.Bold),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = ControlPaint.Light(backColor, 0.25F);
            btn.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(backColor, 0.05F);
            btn.Click += onClick;
            toolTip.SetToolTip(btn, tip);
            return btn;
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
                DisableSelect = chkSelect.Checked,
                DisablePlot = chkPlot.Checked,
                DisableCopy = chkCopy.Checked,
                DisableDelete = chkDelete.Checked,
                ReplaceOriginalFile = chkReplace.Checked
            };

            UserAction = action;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
