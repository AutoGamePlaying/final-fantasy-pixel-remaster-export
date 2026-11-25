using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace FfprExport.Gui;

/// <summary>
/// Single-window Chinese WinForms GUI for scanning Steam FF1-FF6 Pixel Remaster installations,
/// running ffpr-export.exe build-db sequentially, streaming UTF-8 logs/progress, and displaying
/// structured verification results from *.build.json.
/// </summary>
internal sealed class MainForm : Form
{
    private const int MaxLogCharacters = 500_000;

    private readonly bool _skipInitialLoad;
    private readonly string _settingsPath;
    private readonly List<GuiGameEntry> _games = [];
    private readonly List<GuiGameExportResult> _results = [];

    private readonly Font _uiFont;
    private readonly Font _boldButtonFont;
    private readonly Font _logFont;
    private readonly Font _verdictFont;

    private CheckBox _selectAllCheckBox = null!;
    private Label _selectionSummaryLabel = null!;
    private Button _rescanButton = null!;
    private Button _manualAddButton = null!;
    private ListView _gamesListView = null!;

    private TextBox _outputRootTextBox = null!;
    private Button _browseOutputButton = null!;
    private Label _outputPreviewLabel = null!;

    private Button _startButton = null!;
    private Button _cancelButton = null!;
    private Label _progressLabel = null!;
    private ProgressBar _progressBar = null!;
    private TextBox _logTextBox = null!;

    private ListView _resultsListView = null!;
    private TextBox _detailDbPathBox = null!;
    private TextBox _detailFileHashBox = null!;
    private TextBox _detailContentHashBox = null!;
    private Label _detailVerdictLabel = null!;
    private Button _openOutputFolderButton = null!;
    private Button _copyHashesButton = null!;
    private Label _copyFeedbackLabel = null!;

    private GuiSettings _settings = new();
    private bool _suppressItemChecked;
    private bool _isExporting;
    private bool _closeAfterCancel;
    private CancellationTokenSource? _exportCts;

    public MainForm(bool skipInitialLoad = false, string? settingsPathOverride = null)
    {
        _skipInitialLoad = skipInitialLoad;
        _settingsPath = string.IsNullOrWhiteSpace(settingsPathOverride)
            ? GuiSettingsStore.GetDefaultSettingsPath()
            : settingsPathOverride;

        _uiFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        _boldButtonFont = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold, GraphicsUnit.Point);
        _logFont = new Font("Consolas", 9f, FontStyle.Regular, GraphicsUnit.Point);
        _verdictFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point);

        InitializeLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _exportCts?.Dispose();
            _uiFont.Dispose();
            _boldButtonFont.Dispose();
            _logFont.Dispose();
            _verdictFont.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (_skipInitialLoad)
        {
            return;
        }

        LoadInitialState();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_isExporting)
        {
            if (_closeAfterCancel)
            {
                e.Cancel = true;
                return;
            }

            var answer = MessageBox.Show(
                this,
                "当前正在导出游戏数据，确定要取消导出并关闭窗口吗？",
                "确认关闭",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            e.Cancel = true;
            if (answer == DialogResult.Yes)
            {
                _closeAfterCancel = true;
                RequestCancelExport();
            }

            return;
        }

        TrySaveSettings();
        base.OnFormClosing(e);
    }

    private void InitializeLayout()
    {
        SuspendLayout();

        var version = ReadDisplayVersion();
        Text = $"FFPR 导出工具 v{version} — FINAL FANTASY I–VI Pixel Remaster";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = _uiFont;
        MinimumSize = new Size(720, 680);
        ClientSize = new Size(860, 760);
        StartPosition = FormStartPosition.CenterScreen;

        var rootLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 4,
        };
        rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 28f));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 37f));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 35f));

        rootLayout.Controls.Add(BuildGamesGroup(), 0, 0);
        rootLayout.Controls.Add(BuildOutputGroup(), 0, 1);
        rootLayout.Controls.Add(BuildExportAndLogPanel(), 0, 2);
        rootLayout.Controls.Add(BuildCompletionGroup(), 0, 3);

        Controls.Add(rootLayout);
        ResumeLayout(performLayout: true);
    }

    private GroupBox BuildGamesGroup()
    {
        var group = new GroupBox
        {
            Text = "1. 游戏（自动扫描 Steam 库，默认全选）",
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            Margin = new Padding(0, 0, 0, 8),
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 6),
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _selectAllCheckBox = new CheckBox
        {
            Text = "全选",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 3, 12, 0),
        };
        _selectAllCheckBox.Click += OnSelectAllClicked;

        _selectionSummaryLabel = new Label
        {
            Text = "正在扫描 Steam 游戏库……",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 4, 8, 0),
        };

        _rescanButton = new Button
        {
            Text = "重新扫描",
            AutoSize = true,
            MinimumSize = new Size(82, 28),
            Margin = new Padding(0, 0, 6, 0),
        };
        _rescanButton.Click += OnRescanClicked;

        _manualAddButton = new Button
        {
            Text = "手动选择游戏目录…",
            AutoSize = true,
            MinimumSize = new Size(140, 28),
            Margin = new Padding(0),
        };
        _manualAddButton.Click += OnManualAddClicked;

        toolbar.Controls.Add(_selectAllCheckBox, 0, 0);
        toolbar.Controls.Add(_selectionSummaryLabel, 1, 0);
        toolbar.Controls.Add(_rescanButton, 2, 0);
        toolbar.Controls.Add(_manualAddButton, 3, 0);

        _gamesListView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            MinimumSize = new Size(0, 96),
        };
        _gamesListView.Columns.Add("游戏", 220);
        _gamesListView.Columns.Add("构建号", 105);
        _gamesListView.Columns.Add("目录", 460);
        _gamesListView.ItemChecked += OnGameItemChecked;
        _gamesListView.SizeChanged += (_, _) => AdjustLastColumnWidth(_gamesListView, minLastColumnWidth: 260);

        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(_gamesListView, 0, 1);
        group.Controls.Add(layout);
        return group;
    }

    private GroupBox BuildOutputGroup()
    {
        var group = new GroupBox
        {
            Text = "2. 输出位置",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(8),
            Margin = new Padding(0, 0, 0, 8),
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 2,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var rootLabel = new Label
        {
            Text = "输出根目录：",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 5, 6, 0),
        };

        _outputRootTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 8, 2),
        };
        _outputRootTextBox.TextChanged += (_, _) => UpdateOutputPreview();

        _browseOutputButton = new Button
        {
            Text = "浏览…",
            AutoSize = true,
            MinimumSize = new Size(80, 28),
            Margin = new Padding(0),
        };
        _browseOutputButton.Click += OnBrowseOutputClicked;

        _outputPreviewLabel = new Label
        {
            Text = "子目录预览：",
            AutoSize = true,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 6, 0, 0),
        };

        layout.Controls.Add(rootLabel, 0, 0);
        layout.Controls.Add(_outputRootTextBox, 1, 0);
        layout.Controls.Add(_browseOutputButton, 2, 0);
        layout.Controls.Add(_outputPreviewLabel, 0, 1);
        layout.SetColumnSpan(_outputPreviewLabel, 3);

        group.Controls.Add(layout);
        return group;
    }

    private Panel BuildExportAndLogPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = new Padding(0, 0, 0, 8),
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var buttonRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 6),
        };
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _startButton = new Button
        {
            Text = "开始导出",
            Font = _boldButtonFont,
            Dock = DockStyle.Fill,
            Height = 40,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0, 120, 212),
            ForeColor = Color.White,
            Margin = new Padding(0, 0, 8, 0),
        };
        _startButton.FlatAppearance.BorderSize = 0;
        _startButton.Click += OnStartExportClicked;

        _cancelButton = new Button
        {
            Text = "取消",
            Width = 100,
            Height = 40,
            Enabled = false,
            Margin = new Padding(0),
        };
        _cancelButton.Click += OnCancelClicked;

        buttonRow.Controls.Add(_startButton, 0, 0);
        buttonRow.Controls.Add(_cancelButton, 1, 0);

        _progressLabel = new Label
        {
            Text = "准备就绪",
            AutoSize = true,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 4),
        };

        _progressBar = new ProgressBar
        {
            Dock = DockStyle.Fill,
            Minimum = 0,
            Maximum = 1000,
            Value = 0,
            Height = 18,
            Style = ProgressBarStyle.Continuous,
            Margin = new Padding(0, 0, 0, 6),
        };

        _logTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = _logFont,
            BackColor = SystemColors.Window,
            MinimumSize = new Size(0, 110),
        };

        panel.Controls.Add(buttonRow, 0, 0);
        panel.Controls.Add(_progressLabel, 0, 1);
        panel.Controls.Add(_progressBar, 0, 2);
        panel.Controls.Add(_logTextBox, 0, 3);
        return panel;
    }

    private GroupBox BuildCompletionGroup()
    {
        var group = new GroupBox
        {
            Text = "3. 完成结果",
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            Margin = new Padding(0),
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _resultsListView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            MinimumSize = new Size(0, 88),
        };
        _resultsListView.Columns.Add("游戏", 165);
        _resultsListView.Columns.Add("构建号", 95);
        _resultsListView.Columns.Add("结论", 175);
        _resultsListView.Columns.Add("库文件路径", 340);
        _resultsListView.SelectedIndexChanged += OnResultSelectionChanged;
        _resultsListView.SizeChanged += (_, _) => AdjustLastColumnWidth(_resultsListView, minLastColumnWidth: 220);

        var detailPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 4,
            Margin = new Padding(0, 6, 0, 6),
        };
        detailPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        detailPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        for (var row = 0; row < 4; row++)
        {
            detailPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        _detailDbPathBox = CreateReadOnlyDetailBox();
        _detailFileHashBox = CreateReadOnlyDetailBox();
        _detailContentHashBox = CreateReadOnlyDetailBox();
        _detailVerdictLabel = new Label
        {
            Text = "（导出完成后在此显示比对结论）",
            AutoSize = true,
            Dock = DockStyle.Fill,
            Font = _verdictFont,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 2, 0, 2),
        };

        AddDetailRow(detailPanel, 0, "数据库路径：", _detailDbPathBox);
        AddDetailRow(detailPanel, 1, "文件哈希：", _detailFileHashBox);
        AddDetailRow(detailPanel, 2, "内容哈希：", _detailContentHashBox);
        AddDetailRow(detailPanel, 3, "比对结论：", _detailVerdictLabel);

        var actionRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0),
        };
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        _openOutputFolderButton = new Button
        {
            Text = "打开输出文件夹",
            AutoSize = true,
            MinimumSize = new Size(124, 28),
            Margin = new Padding(0, 0, 8, 0),
        };
        _openOutputFolderButton.Click += OnOpenOutputFolderClicked;

        _copyHashesButton = new Button
        {
            Text = "复制哈希",
            AutoSize = true,
            MinimumSize = new Size(96, 28),
            Enabled = false,
            Margin = new Padding(0, 0, 10, 0),
        };
        _copyHashesButton.Click += OnCopyHashesClicked;

        _copyFeedbackLabel = new Label
        {
            Text = string.Empty,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = Color.FromArgb(16, 124, 16),
            Margin = new Padding(0, 5, 0, 0),
        };

        actionRow.Controls.Add(_openOutputFolderButton, 0, 0);
        actionRow.Controls.Add(_copyHashesButton, 1, 0);
        actionRow.Controls.Add(_copyFeedbackLabel, 2, 0);

        layout.Controls.Add(_resultsListView, 0, 0);
        layout.Controls.Add(detailPanel, 0, 1);
        layout.Controls.Add(actionRow, 0, 2);

        group.Controls.Add(layout);
        return group;
    }

    private TextBox CreateReadOnlyDetailBox()
    {
        return new TextBox
        {
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Control,
            Dock = DockStyle.Fill,
            Font = _logFont,
            Margin = new Padding(0, 3, 0, 2),
        };
    }

    private static void AddDetailRow(TableLayoutPanel panel, int rowIndex, string labelText, Control valueControl)
    {
        var label = new Label
        {
            Text = labelText,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 2, 6, 2),
        };
        panel.Controls.Add(label, 0, rowIndex);
        panel.Controls.Add(valueControl, 1, rowIndex);
    }

    private void LoadInitialState()
    {
        _settings = GuiSettingsStore.Load(_settingsPath);
        var defaultRoot = GuiOutputPlanner.GetDefaultOutputRoot();
        _outputRootTextBox.Text = string.IsNullOrWhiteSpace(_settings.OutputRoot)
            ? defaultRoot
            : _settings.OutputRoot.Trim();

        ScanSteamGames(preserveManualEntries: false);

        var (cliPath, expectedCliPath) = CliCommandHelper.FindCliExecutable(
            AppContext.BaseDirectory,
            Environment.ProcessPath);
        if (cliPath is null)
        {
            AppendLogLine($"警告：未找到同目录命令行程序 {CliCommandHelper.CliExecutableFileName}（预期位置：{expectedCliPath}）");
        }
    }

    private void ScanSteamGames(bool preserveManualEntries)
    {
        var previousChecked = CaptureCheckedStates();
        var manualEntries = preserveManualEntries
            ? _games.Where(g => g.IsManual).ToList()
            : [];

        IReadOnlyList<GuiGameEntry> discovered;
        try
        {
            discovered = GuiGameScanner.DiscoverSteamGames();
        }
        catch (Exception ex)
        {
            AppendLogLine($"提示：扫描 Steam 游戏库时出现异常：{ex.Message}");
            discovered = [];
        }

        _games.Clear();
        var seenKeys = new HashSet<string>(GuiGameScanner.PathComparer);
        foreach (var game in discovered)
        {
            var key = GuiGameScanner.NormalizePathKey(game.GameDirectory);
            if (seenKeys.Add(key))
            {
                _games.Add(game);
            }
        }

        foreach (var manual in manualEntries)
        {
            var key = GuiGameScanner.NormalizePathKey(manual.GameDirectory);
            if (seenKeys.Add(key))
            {
                _games.Add(manual);
            }
        }

        RebuildGamesListView(previousChecked);
    }

    private void RebuildGamesListView(IReadOnlyDictionary<string, bool> runtimeCheckedStates)
    {
        _suppressItemChecked = true;
        try
        {
            _gamesListView.BeginUpdate();
            _gamesListView.Items.Clear();

            foreach (var game in _games)
            {
                var key = GuiGameScanner.NormalizePathKey(game.GameDirectory);
                var isChecked = runtimeCheckedStates.TryGetValue(key, out var remembered)
                    ? remembered
                    : GuiSettingsStore.ResolveInitialCheckedState(_settings, game.GameDirectory);

                var item = new ListViewItem(game.DisplayLabel)
                {
                    Checked = isChecked,
                    Tag = game,
                };
                item.SubItems.Add(game.BuildIdDisplay);
                item.SubItems.Add(game.GameDirectory);
                _gamesListView.Items.Add(item);
            }

            AdjustLastColumnWidth(_gamesListView, minLastColumnWidth: 260);
        }
        finally
        {
            _gamesListView.EndUpdate();
            _suppressItemChecked = false;
        }

        SyncSelectionUi();
    }

    private void OnSelectAllClicked(object? sender, EventArgs e)
    {
        if (_isExporting || _gamesListView.Items.Count == 0)
        {
            return;
        }

        var targetChecked = _selectAllCheckBox.Checked;
        _suppressItemChecked = true;
        try
        {
            foreach (ListViewItem item in _gamesListView.Items)
            {
                item.Checked = targetChecked;
            }
        }
        finally
        {
            _suppressItemChecked = false;
        }

        SyncSelectionUi();
    }

    private void OnGameItemChecked(object? sender, ItemCheckedEventArgs e)
    {
        if (_suppressItemChecked)
        {
            return;
        }

        SyncSelectionUi();
    }

    private void OnRescanClicked(object? sender, EventArgs e)
    {
        if (_isExporting)
        {
            return;
        }

        ScanSteamGames(preserveManualEntries: true);
    }

    private void OnManualAddClicked(object? sender, EventArgs e)
    {
        if (_isExporting)
        {
            return;
        }

        using var dialog = new FolderBrowserDialog
        {
            Description = "请选择 FINAL FANTASY I–VI Pixel Remaster 游戏安装根目录（含 *_Data 文件夹）",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        GuiGameEntry inspected;
        try
        {
            inspected = GuiGameScanner.InspectManualGameDirectory(dialog.SelectedPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"{ex.Message}\n\n请选择 FINAL FANTASY I–VI Pixel Remaster 的游戏安装根目录（包含对应 *_Data 文件夹）。",
                "不支持的游戏目录",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var newKey = GuiGameScanner.NormalizePathKey(inspected.GameDirectory);
        for (var i = 0; i < _gamesListView.Items.Count; i++)
        {
            if (_gamesListView.Items[i].Tag is GuiGameEntry existing
                && GuiGameScanner.PathComparer.Equals(
                    GuiGameScanner.NormalizePathKey(existing.GameDirectory),
                    newKey))
            {
                _gamesListView.Items[i].Checked = true;
                _gamesListView.Items[i].Selected = true;
                _gamesListView.EnsureVisible(i);
                SyncSelectionUi();
                return;
            }
        }

        _games.Add(inspected);
        _suppressItemChecked = true;
        try
        {
            var item = new ListViewItem(inspected.DisplayLabel)
            {
                Checked = true,
                Tag = inspected,
            };
            item.SubItems.Add(inspected.BuildIdDisplay);
            item.SubItems.Add(inspected.GameDirectory);
            _gamesListView.Items.Add(item);
            item.Selected = true;
            item.EnsureVisible();
        }
        finally
        {
            _suppressItemChecked = false;
        }

        SyncSelectionUi();
    }

    private void OnBrowseOutputClicked(object? sender, EventArgs e)
    {
        if (_isExporting)
        {
            return;
        }

        using var dialog = new FolderBrowserDialog
        {
            Description = "选择导出输出根目录（每个游戏会在其下创建 <ffN>-<构建号> 子目录）",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };

        var currentRoot = GuiGameScanner.NormalizeUserPath(_outputRootTextBox.Text);
        if (!string.IsNullOrWhiteSpace(currentRoot) && Directory.Exists(PathSupport.ToIoPath(currentRoot)))
        {
            dialog.SelectedPath = currentRoot;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            _outputRootTextBox.Text = dialog.SelectedPath;
        }
    }

    private async void OnStartExportClicked(object? sender, EventArgs e)
    {
        if (_isExporting)
        {
            return;
        }

        var (cliPath, expectedCliPath) = CliCommandHelper.FindCliExecutable(
            AppContext.BaseDirectory,
            Environment.ProcessPath);
        if (cliPath is null)
        {
            MessageBox.Show(
                this,
                CliCommandHelper.FormatMissingCliMessage(expectedCliPath),
                "缺少命令行程序",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        var selectedGames = GetCheckedGames();
        if (selectedGames.Count == 0)
        {
            MessageBox.Show(
                this,
                "请至少勾选一个要导出的游戏。",
                "未选择游戏",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        IReadOnlyList<PlannedGameExport> plan;
        try
        {
            plan = GuiOutputPlanner.CreateExportPlan(_outputRootTextBox.Text, selectedGames);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"输出位置检查未通过：{ex.Message}",
                "输出目录无效",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var foreignTargets = plan
            .Where(p => p.DirectoryState == OutputDirectoryState.ForeignNonEmptyDirectory)
            .ToArray();
        if (foreignTargets.Length > 0)
        {
            var pathLines = string.Join(Environment.NewLine, foreignTargets.Select(p => $"• {p.OutputDirectory}"));
            var confirm = MessageBox.Show(
                this,
                "以下输出目录已存在且非空，但不是由 ffpr-export 创建的建库目录：\n\n"
                + pathLines
                + "\n\n继续将清空上述目录中的现有文件并写入新的导出结果。是否确认覆盖？",
                "确认覆盖非空目录",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes)
            {
                return;
            }
        }

        TrySaveSettings();
        await RunExportBatchAsync(cliPath, plan);
    }

    private void OnCancelClicked(object? sender, EventArgs e)
    {
        RequestCancelExport();
    }

    private void RequestCancelExport()
    {
        if (!_isExporting || _exportCts is null || _exportCts.IsCancellationRequested)
        {
            return;
        }

        _cancelButton.Enabled = false;
        _progressLabel.Text = "正在取消导出并结束子进程……";
        AppendLogLine("==> 收到取消请求，正在终止导出子进程树……");
        _exportCts.Cancel();
    }

    private async Task RunExportBatchAsync(string cliPath, IReadOnlyList<PlannedGameExport> plan)
    {
        _isExporting = true;
        _closeAfterCancel = false;
        _exportCts?.Dispose();
        _exportCts = new CancellationTokenSource();
        var token = _exportCts.Token;

        _results.Clear();
        _resultsListView.Items.Clear();
        ClearResultDetail();
        _logTextBox.Clear();
        _copyFeedbackLabel.Text = string.Empty;
        _progressBar.Value = 0;

        SetInputsLocked(locked: true);

        var protectedGameDirs = plan.Select(p => p.Game.GameDirectory).ToArray();
        var total = plan.Count;
        var cancelled = false;

        try
        {
            for (var i = 0; i < total; i++)
            {
                if (token.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                var item = plan[i];
                var oneBased = i + 1;
                var currentGameIndex = i;

                _progressLabel.Text = CliCommandHelper.FormatGameProgressHeader(oneBased, total, item.Game);
                _progressBar.Value = CliCommandHelper.CalculateProgressPermille(currentGameIndex, total);

                AppendLogLine("============================================================");
                AppendLogLine($"[{oneBased}/{total}] 开始导出：{item.Game.DisplayLabel}（构建号：{item.Game.BuildIdDisplay}）");
                AppendLogLine($"游戏目录：{item.Game.GameDirectory}");
                AppendLogLine($"输出目录：{item.OutputDirectory}");

                if (item.DirectoryState == OutputDirectoryState.ForeignNonEmptyDirectory)
                {
                    try
                    {
                        AppendLogLine($"正在清空已确认覆盖的目录：{item.OutputDirectory}");
                        GuiOutputPlanner.ClearConfirmedForeignDirectory(item.OutputDirectory, protectedGameDirs);
                    }
                    catch (Exception ex)
                    {
                        AppendLogLine($"错误：清理目录失败（{item.OutputDirectory}）：{ex.Message}");
                        var clearFail = BuildReportReader.EvaluateOutcome(
                            item.Game,
                            item.OutputDirectory,
                            exitCode: 2,
                            cancelled: false,
                            report: null,
                            readError: $"清理已有目录失败：{ex.Message}");
                        AddExportResult(clearFail);
                        _progressBar.Value = CliCommandHelper.CalculateProgressPermille(oneBased, total);
                        continue;
                    }
                }
                else if (item.UseForceFlag)
                {
                    AppendLogLine("提示：输出目录包含上次建库结果，将附加 --force 自动覆盖重建。");
                }

                var cliArgs = CliCommandHelper.BuildBuildDbArguments(
                    item.Game.GameDirectory,
                    item.OutputDirectory,
                    force: item.UseForceFlag);
                var startInfo = CliCommandHelper.CreateStartInfo(cliPath, cliArgs);

                int exitCode;
                try
                {
                    exitCode = await CliProcessRunner.RunAsync(
                        startInfo,
                        line => OnChildStdoutLine(oneBased, currentGameIndex, total, item.Game, line),
                        line => PostToUi(() => AppendLogLine($"[stderr] {line}")),
                        token);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    AppendLogLine($"[{item.Game.GameCode}] 已取消。");
                    var cancelResult = BuildReportReader.EvaluateOutcome(
                        item.Game,
                        item.OutputDirectory,
                        exitCode: -1,
                        cancelled: true,
                        report: null);
                    AddExportResult(cancelResult);
                    break;
                }
                catch (Exception ex)
                {
                    AppendLogLine($"错误：启动或运行 {Path.GetFileName(cliPath)} 失败：{ex.Message}");
                    var launchFail = BuildReportReader.EvaluateOutcome(
                        item.Game,
                        item.OutputDirectory,
                        exitCode: 2,
                        cancelled: false,
                        report: null,
                        readError: ex.Message);
                    AddExportResult(launchFail);
                    _progressBar.Value = CliCommandHelper.CalculateProgressPermille(oneBased, total);
                    continue;
                }

                BuildReportReader.TryReadReportFromDirectory(
                    item.OutputDirectory,
                    item.Game.Spec.Id,
                    out var report,
                    out var readError);

                var outcome = BuildReportReader.EvaluateOutcome(
                    item.Game,
                    item.OutputDirectory,
                    exitCode,
                    cancelled: false,
                    report,
                    readError);
                AddExportResult(outcome);
                _progressBar.Value = CliCommandHelper.CalculateProgressPermille(oneBased, total);
            }

            UpdateFinishedStatusSummary(cancelled);
        }
        finally
        {
            _isExporting = false;
            SetInputsLocked(locked: false);
            _exportCts?.Dispose();
            _exportCts = null;

            if (_closeAfterCancel)
            {
                _closeAfterCancel = false;
                BeginInvoke(new Action(Close));
            }
        }
    }

    private void OnChildStdoutLine(
        int currentIndexOneBased,
        int completedGames,
        int totalGames,
        GuiGameEntry game,
        string line)
    {
        var parsedStep = CliCommandHelper.TryParseProgressStep(line, out var step) ? step : null;
        PostToUi(() =>
        {
            AppendLogLine(line);
            if (parsedStep is not null)
            {
                _progressLabel.Text = CliCommandHelper.FormatGameProgressHeader(
                    currentIndexOneBased,
                    totalGames,
                    game,
                    parsedStep);
                _progressBar.Value = CliCommandHelper.CalculateProgressPermille(
                    completedGames,
                    totalGames,
                    parsedStep);
            }
        });
    }

    private void AddExportResult(GuiGameExportResult result)
    {
        _results.Add(result);

        var row = new ListViewItem(result.Game.DisplayLabel)
        {
            Tag = result,
            ForeColor = GetVerdictColor(result.Verdict),
        };
        row.SubItems.Add(result.EffectiveBuildId);
        row.SubItems.Add(result.VerdictDisplay);
        row.SubItems.Add(result.SqlitePath);
        _resultsListView.Items.Add(row);

        row.Selected = true;
        row.EnsureVisible();
        DisplayResultDetail(result);
        AdjustLastColumnWidth(_resultsListView, minLastColumnWidth: 220);

        _copyHashesButton.Enabled = _results.Any(r => r.Report is not null);
    }

    private void UpdateFinishedStatusSummary(bool cancelled)
    {
        if (cancelled)
        {
            _progressLabel.Text = "导出已取消";
            AppendLogLine("==> 导出任务已取消。");
            return;
        }

        var passed = _results.Count(r => r.Verdict is KnownBuildVerdictKind.Matched or KnownBuildVerdictKind.NewGameVersion);
        var mismatch = _results.Count(r => r.Verdict == KnownBuildVerdictKind.HashMismatch);
        var failed = _results.Count - passed - mismatch;

        _progressBar.Value = 1000;
        _progressLabel.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"全部导出结束（共 {_results.Count} 个：通过 {passed}，哈希不同 {mismatch}，失败 {failed}）");
        AppendLogLine($"==> {_progressLabel.Text}");
    }

    private void OnResultSelectionChanged(object? sender, EventArgs e)
    {
        if (_resultsListView.SelectedItems.Count > 0
            && _resultsListView.SelectedItems[0].Tag is GuiGameExportResult selected)
        {
            DisplayResultDetail(selected);
        }
    }

    private void DisplayResultDetail(GuiGameExportResult result)
    {
        _detailDbPathBox.Text = result.SqlitePath;
        _detailFileHashBox.Text = result.Report?.FileSha256 ?? "（无）";
        _detailContentHashBox.Text = result.Report?.ContentSha256 ?? "（无）";
        _detailVerdictLabel.Text = result.VerdictDetailText;
        _detailVerdictLabel.ForeColor = GetVerdictColor(result.Verdict);
    }

    private void ClearResultDetail()
    {
        _detailDbPathBox.Text = string.Empty;
        _detailFileHashBox.Text = string.Empty;
        _detailContentHashBox.Text = string.Empty;
        _detailVerdictLabel.Text = "（等待导出结果……）";
        _detailVerdictLabel.ForeColor = SystemColors.GrayText;
    }

    private void OnOpenOutputFolderClicked(object? sender, EventArgs e)
    {
        string? targetDir = null;
        if (_resultsListView.SelectedItems.Count > 0
            && _resultsListView.SelectedItems[0].Tag is GuiGameExportResult selected
            && Directory.Exists(PathSupport.ToIoPath(selected.OutputDirectory)))
        {
            targetDir = selected.OutputDirectory;
        }
        else
        {
            var root = GuiGameScanner.NormalizeUserPath(_outputRootTextBox.Text);
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(PathSupport.ToIoPath(root)))
            {
                targetDir = Path.GetFullPath(root);
            }
        }

        if (string.IsNullOrWhiteSpace(targetDir))
        {
            MessageBox.Show(
                this,
                "输出文件夹尚未创建，请先执行导出或确认输出根目录是否存在。",
                "无法打开文件夹",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = targetDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"打开输出文件夹失败（{targetDir}）：{ex.Message}",
                "打开失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void OnCopyHashesClicked(object? sender, EventArgs e)
    {
        var text = BuildReportReader.FormatClipboardHashText(_results);
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show(
                this,
                "当前没有可复制的哈希记录。",
                "复制哈希",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            Clipboard.SetText(text);
            var count = _results.Count(r => r.Report is not null);
            _copyFeedbackLabel.Text = string.Create(
                CultureInfo.InvariantCulture,
                $"已复制 {count} 个游戏的哈希到剪贴板");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"写入剪贴板失败：{ex.Message}",
                "复制失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void SetInputsLocked(bool locked)
    {
        _gamesListView.Enabled = !locked;
        _selectAllCheckBox.Enabled = !locked;
        _rescanButton.Enabled = !locked;
        _manualAddButton.Enabled = !locked;
        _outputRootTextBox.Enabled = !locked;
        _browseOutputButton.Enabled = !locked;
        _startButton.Enabled = !locked && GetCheckedGames().Count > 0;
        _startButton.BackColor = _startButton.Enabled
            ? Color.FromArgb(0, 120, 212)
            : Color.FromArgb(160, 160, 160);
        _cancelButton.Enabled = locked;
    }

    private void SyncSelectionUi()
    {
        var total = _gamesListView.Items.Count;
        var checkedCount = GetCheckedGames().Count;

        if (total == 0)
        {
            _selectAllCheckBox.CheckState = CheckState.Unchecked;
            _selectAllCheckBox.Enabled = !_isExporting && false;
            _selectionSummaryLabel.Text = "未自动找到 FF1–FF6，请点击“手动选择游戏目录…”添加";
        }
        else
        {
            _selectAllCheckBox.Enabled = !_isExporting;
            _selectAllCheckBox.CheckState = checkedCount == 0
                ? CheckState.Unchecked
                : checkedCount == total
                    ? CheckState.Checked
                    : CheckState.Indeterminate;
            _selectionSummaryLabel.Text = string.Create(
                CultureInfo.InvariantCulture,
                $"已选 {checkedCount} / {total} 个游戏");
        }

        if (!_isExporting)
        {
            _startButton.Enabled = checkedCount > 0;
            _startButton.BackColor = _startButton.Enabled
                ? Color.FromArgb(0, 120, 212)
                : Color.FromArgb(160, 160, 160);
        }

        UpdateOutputPreview();
    }

    private void UpdateOutputPreview()
    {
        _outputPreviewLabel.Text = GuiOutputPlanner.FormatOutputPreview(
            _outputRootTextBox.Text,
            GetCheckedGames());
    }

    private List<GuiGameEntry> GetCheckedGames()
    {
        var list = new List<GuiGameEntry>();
        foreach (ListViewItem item in _gamesListView.Items)
        {
            if (item.Checked && item.Tag is GuiGameEntry game)
            {
                list.Add(game);
            }
        }

        return list;
    }

    private Dictionary<string, bool> CaptureCheckedStates()
    {
        var states = new Dictionary<string, bool>(GuiGameScanner.PathComparer);
        foreach (ListViewItem item in _gamesListView.Items)
        {
            if (item.Tag is GuiGameEntry game)
            {
                var key = GuiGameScanner.NormalizePathKey(game.GameDirectory);
                states[key] = item.Checked;
            }
        }

        return states;
    }

    private void TrySaveSettings()
    {
        try
        {
            var mergedChecked = new Dictionary<string, bool>(_settings.CheckedGames, GuiGameScanner.PathComparer);
            foreach (var (key, isChecked) in CaptureCheckedStates())
            {
                mergedChecked[key] = isChecked;
            }

            var toSave = new GuiSettings
            {
                Format = 1,
                OutputRoot = GuiGameScanner.NormalizeUserPath(_outputRootTextBox.Text),
                CheckedGames = new Dictionary<string, bool>(mergedChecked, StringComparer.Ordinal),
            };
            GuiSettingsStore.Save(_settingsPath, toSave);
            _settings = toSave;
        }
        catch
        {
            // Never block closing or export if %APPDATA% is read-only.
        }
    }

    private void AppendLogLine(string line)
    {
        if (IsDisposed)
        {
            return;
        }

        if (_logTextBox.TextLength > MaxLogCharacters)
        {
            var keepStart = Math.Min(_logTextBox.TextLength / 2, _logTextBox.TextLength);
            _logTextBox.Text = _logTextBox.Text[keepStart..];
        }

        _logTextBox.AppendText(line + Environment.NewLine);
    }

    private void PostToUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(action);
            }
            catch (ObjectDisposedException)
            {
                // Window already disposed during shutdown.
            }
            catch (InvalidOperationException)
            {
                // Handle torn down during shutdown.
            }
        }
        else
        {
            action();
        }
    }

    private static Color GetVerdictColor(KnownBuildVerdictKind verdict)
    {
        return verdict switch
        {
            KnownBuildVerdictKind.Matched => Color.FromArgb(16, 124, 16),
            KnownBuildVerdictKind.NewGameVersion => Color.FromArgb(0, 90, 158),
            KnownBuildVerdictKind.Cancelled => Color.FromArgb(110, 110, 110),
            _ => Color.FromArgb(196, 43, 28),
        };
    }

    private static void AdjustLastColumnWidth(ListView listView, int minLastColumnWidth)
    {
        if (listView.Columns.Count < 2)
        {
            return;
        }

        var fixedWidth = 0;
        for (var i = 0; i < listView.Columns.Count - 1; i++)
        {
            fixedWidth += listView.Columns[i].Width;
        }

        var available = listView.ClientSize.Width - fixedWidth - 4;
        listView.Columns[^1].Width = Math.Max(minLastColumnWidth, available);
    }

    private static string ReadDisplayVersion()
    {
        var assembly = typeof(MainForm).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational.Split('+')[0];
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    }
}
