using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace FfprExport.Gui;

/// <summary>
/// Single-window localized WinForms GUI for scanning Steam FF1-FF6 Pixel Remaster installations,
/// running ffpr-export.exe build-db sequentially, streaming UTF-8 logs and language-independent
/// @@progress updates, and displaying structured verification results from *.build.json.
/// </summary>
internal sealed class MainForm : Form
{
    private const int MaxLogCharacters = 500_000;

    private enum UiProgressState
    {
        Ready,
        MissingCli,
        Exporting,
        Cancelling,
        Cancelled,
        Finished,
    }

    private sealed record LanguageOption(AppLanguage Language)
    {
        public override string ToString() => AppLocalization.GetNativeDisplayName(Language);
    }

    private readonly bool _skipInitialLoad;
    private readonly bool _hasExternalLanguageOverride;
    private readonly GuiStartupLogger? _startupLogger;
    private readonly ProcessIntegrityInfo _integrityInfo;
    private readonly AgpRootResolution _agpRoot;
    private readonly string? _settingsPathOverride;
    private readonly string _settingsPath;
    private readonly List<GuiGameEntry> _games = [];
    private readonly List<GuiGameExportResult> _results = [];
    private readonly List<Font> _ownedFonts = [];
    private readonly List<string> _startupWarnings = [];

    private readonly Font _uiFont;
    private readonly Font _boldButtonFont;
    private readonly Font _logFont;
    private readonly Font _verdictFont;

    private Label _languageLabel = null!;
    private ComboBox _languageComboBox = null!;
    private Panel _rootNoticePanel = null!;
    private Label _rootNoticeLabel = null!;
    private StatusStrip _statusStrip = null!;
    private ToolStripStatusLabel _statusDataDirLabel = null!;
    private ToolStripStatusLabel _statusIntegrityLabel = null!;

    private GroupBox _gamesGroup = null!;
    private CheckBox _selectAllCheckBox = null!;
    private Label _selectionSummaryLabel = null!;
    private Button _rescanButton = null!;
    private Button _manualAddButton = null!;
    private ListView _gamesListView = null!;

    private GroupBox _outputGroup = null!;
    private Label _outputRootLabel = null!;
    private TextBox _outputRootTextBox = null!;
    private Button _browseOutputButton = null!;
    private Label _outputPreviewLabel = null!;

    private Button _startButton = null!;
    private Button _cancelButton = null!;
    private Label _progressLabel = null!;
    private ProgressBar _progressBar = null!;
    private TextBox _logTextBox = null!;

    private GroupBox _completionGroup = null!;
    private ListView _resultsListView = null!;
    private Label _detailDbPathLabel = null!;
    private Label _detailFileHashLabel = null!;
    private Label _detailContentHashLabel = null!;
    private Label _detailVerdictTitleLabel = null!;
    private TextBox _detailDbPathBox = null!;
    private TextBox _detailFileHashBox = null!;
    private TextBox _detailContentHashBox = null!;
    private Label _detailVerdictLabel = null!;
    private Button _openOutputFolderButton = null!;
    private Button _copyHashesButton = null!;
    private Label _copyFeedbackLabel = null!;

    private GuiSettings _settings = new();
    private bool _initialStateLoaded;
    private bool _suppressItemChecked;
    private bool _suppressOutputWarningClear;
    private bool _suppressLanguageChanged;
    private bool _userChangedLanguage;
    private bool _isExporting;
    private bool _closeAfterCancel;
    private bool _isWaitingForResults;
    private UiProgressState _progressState = UiProgressState.Ready;
    private int _activeGameOneBased;
    private int _activeTotalGames;
    private GuiGameEntry? _activeGame;
    private ProgressStepUpdate? _activeStep;
    private int _lastCopiedHashCount;
    private string? _lastScanWarning;
    private string? _outputRootFallbackWarning;
    private CancellationTokenSource? _exportCts;

    public MainForm(
        bool skipInitialLoad = false,
        string? settingsPathOverride = null,
        GuiStartupLogger? startupLogger = null,
        AgpRootResolution? agpRoot = null,
        ProcessIntegrityInfo? integrityInfo = null,
        bool hasExternalLanguageOverride = false)
    {
        _skipInitialLoad = skipInitialLoad;
        _hasExternalLanguageOverride = hasExternalLanguageOverride;
        _startupLogger = startupLogger;
        _integrityInfo = integrityInfo ?? agpRoot?.Integrity ?? ProcessIntegrity.Detect();
        _agpRoot = agpRoot ?? AgpPaths.ResolveAgpHome(integrityOverride: _integrityInfo);
        _settingsPathOverride = settingsPathOverride;
        _settingsPath = ResolveSettingsPathSafely(settingsPathOverride, _agpRoot);

        _uiFont = CreateFontSafe(L.Get("Gui_FontRoleUi"), GuiFontFallbackPolicy.UiFontCandidates, 9.5f, FontStyle.Regular);
        _boldButtonFont = CreateFontSafe(L.Get("Gui_FontRolePrimaryButton"), GuiFontFallbackPolicy.UiFontCandidates, 10.5f, FontStyle.Bold);
        _logFont = CreateFontSafe(L.Get("Gui_FontRoleLog"), GuiFontFallbackPolicy.LogFontCandidates, 9f, FontStyle.Regular);
        _verdictFont = CreateFontSafe(L.Get("Gui_FontRoleVerdict"), GuiFontFallbackPolicy.UiFontCandidates, 9.5f, FontStyle.Bold);

        InitializeLayout();
        ApplyRootNoticeAndStatusBar(recordStartupWarning: true);
        SyncLanguageSelector();
        FlushStartupWarningsToUi();
    }

    internal void RunSelfTestInitialization()
    {
        CreateControl();
        EnsureNativeHandlesCreated(this);
        if (!_skipInitialLoad)
        {
            EnsureInitialStateLoaded();
        }

        PerformLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _exportCts?.Dispose();
            foreach (var font in _ownedFonts)
            {
                try
                {
                    font.Dispose();
                }
                catch
                {
                    // Ignore font disposal errors during teardown.
                }
            }

            _ownedFonts.Clear();
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

        try
        {
            EnsureInitialStateLoaded();
        }
        catch (Exception ex)
        {
            LogStartupDiagnosticWarning(L.Format("Gui_StartupLoadException", ex.Message), ex, "MainForm.OnLoad");
        }
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
                L.Get("Gui_ConfirmCloseMessage"),
                L.Get("Gui_ConfirmCloseTitle"),
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
        Text = L.Format("Gui_WindowTitle", version);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = _uiFont;
        MinimumSize = new Size(720, 680);
        ClientSize = new Size(860, 780);
        StartPosition = FormStartPosition.CenterScreen;

        var rootLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 6,
        };
        rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 28f));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 37f));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 35f));

        rootLayout.Controls.Add(BuildHeaderBar(), 0, 0);
        rootLayout.Controls.Add(BuildRootNoticeBanner(), 0, 1);
        rootLayout.Controls.Add(BuildGamesGroup(), 0, 2);
        rootLayout.Controls.Add(BuildOutputGroup(), 0, 3);
        rootLayout.Controls.Add(BuildExportAndLogPanel(), 0, 4);
        rootLayout.Controls.Add(BuildCompletionGroup(), 0, 5);

        _statusStrip = BuildStatusStrip();

        // Add Fill control before Bottom control so WinForms docks the StatusStrip along the bottom edge.
        Controls.Add(rootLayout);
        Controls.Add(_statusStrip);
        SizeChanged += (_, _) => UpdateRootNoticeWrapWidth();
        ResumeLayout(performLayout: true);
    }

    private FlowLayoutPanel BuildHeaderBar()
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 4),
            Padding = new Padding(0),
        };

        _languageComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 132,
            Margin = new Padding(0),
        };
        foreach (var lang in AppLocalization.SupportedLanguages)
        {
            _languageComboBox.Items.Add(new LanguageOption(lang));
        }

        _languageComboBox.SelectedIndexChanged += OnLanguageSelectionChanged;

        _languageLabel = new Label
        {
            Text = L.Get("Lang_Label"),
            AutoSize = true,
            Margin = new Padding(0, 4, 6, 0),
        };

        // FlowDirection.RightToLeft places the first added control on the far right.
        bar.Controls.Add(_languageComboBox);
        bar.Controls.Add(_languageLabel);
        return bar;
    }

    private Panel BuildRootNoticeBanner()
    {
        _rootNoticePanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.FromArgb(255, 248, 225),
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(10, 7, 10, 7),
            Margin = new Padding(0, 0, 0, 8),
            Visible = false,
        };

        _rootNoticeLabel = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(120, 53, 15),
            MaximumSize = new Size(800, 0),
            Text = string.Empty,
        };

        _rootNoticePanel.Controls.Add(_rootNoticeLabel);
        return _rootNoticePanel;
    }

    private StatusStrip BuildStatusStrip()
    {
        var strip = new StatusStrip
        {
            SizingGrip = false,
            Dock = DockStyle.Bottom,
        };

        _statusDataDirLabel = new ToolStripStatusLabel
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = L.Get("Gui_StatusDataDirNotReady"),
        };

        _statusIntegrityLabel = new ToolStripStatusLabel
        {
            BorderSides = ToolStripStatusLabelBorderSides.Left,
            Padding = new Padding(8, 0, 4, 0),
            Text = L.Format("Gui_StatusIntegrity", _integrityInfo.DisplayLabel),
        };

        strip.Items.Add(_statusDataDirLabel);
        strip.Items.Add(_statusIntegrityLabel);
        return strip;
    }

    private void ApplyRootNoticeAndStatusBar(bool recordStartupWarning)
    {
        var (dataDirText, integrityText) = GuiStartupLogger.FormatStatusBarSummary(_agpRoot, _integrityInfo);
        _statusDataDirLabel.Text = dataDirText;
        _statusDataDirLabel.ToolTipText = dataDirText;
        _statusIntegrityLabel.Text = integrityText;
        if (_integrityInfo.IsLowOrUntrusted)
        {
            _statusIntegrityLabel.ForeColor = Color.FromArgb(180, 83, 9);
        }

        var bannerText = AgpPaths.FormatUiNoticeBanner(_agpRoot, _integrityInfo);
        if (!string.IsNullOrWhiteSpace(bannerText))
        {
            _rootNoticeLabel.Text = bannerText;
            _rootNoticePanel.Visible = true;
            UpdateRootNoticeWrapWidth();
            if (recordStartupWarning)
            {
                RecordStartupWarning(bannerText);
            }
        }
        else
        {
            _rootNoticePanel.Visible = false;
        }
    }

    private void UpdateRootNoticeWrapWidth()
    {
        if (_rootNoticePanel is null || _rootNoticeLabel is null || !_rootNoticePanel.Visible)
        {
            return;
        }

        var available = Math.Max(360, ClientSize.Width - 52);
        if (_rootNoticeLabel.MaximumSize.Width != available)
        {
            _rootNoticeLabel.MaximumSize = new Size(available, 0);
        }
    }

    private GroupBox BuildGamesGroup()
    {
        _gamesGroup = new GroupBox
        {
            Text = L.Get("Gui_GamesGroupTitle"),
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
            Text = L.Get("Gui_SelectAll"),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 3, 12, 0),
        };
        _selectAllCheckBox.Click += OnSelectAllClicked;

        _selectionSummaryLabel = new Label
        {
            Text = L.Get("Gui_ScanningSteam"),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 4, 8, 0),
        };

        _rescanButton = new Button
        {
            Text = L.Get("Gui_Rescan"),
            AutoSize = true,
            MinimumSize = new Size(82, 28),
            Margin = new Padding(0, 0, 6, 0),
        };
        _rescanButton.Click += OnRescanClicked;

        _manualAddButton = new Button
        {
            Text = L.Get("Gui_AddGameFolder"),
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
        _gamesListView.Columns.Add(L.Get("Gui_ColGame"), 220);
        _gamesListView.Columns.Add(L.Get("Gui_ColBuildId"), 105);
        _gamesListView.Columns.Add(L.Get("Gui_ColDirectory"), 460);
        _gamesListView.ItemChecked += OnGameItemChecked;
        _gamesListView.SizeChanged += (_, _) => AdjustLastColumnWidth(_gamesListView, minLastColumnWidth: 260);

        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(_gamesListView, 0, 1);
        _gamesGroup.Controls.Add(layout);
        return _gamesGroup;
    }

    private GroupBox BuildOutputGroup()
    {
        _outputGroup = new GroupBox
        {
            Text = L.Get("Gui_OutputGroupTitle"),
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

        _outputRootLabel = new Label
        {
            Text = L.Get("Gui_OutputRootLabel"),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 5, 6, 0),
        };

        _outputRootTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 8, 2),
        };
        _outputRootTextBox.TextChanged += (_, _) =>
        {
            if (!_suppressOutputWarningClear)
            {
                _outputRootFallbackWarning = null;
            }

            UpdateOutputPreview();
        };

        _browseOutputButton = new Button
        {
            Text = L.Get("Gui_Browse"),
            AutoSize = true,
            MinimumSize = new Size(80, 28),
            Margin = new Padding(0),
        };
        _browseOutputButton.Click += OnBrowseOutputClicked;

        _outputPreviewLabel = new Label
        {
            Text = L.Get("Gui_OutputPreviewPrefix"),
            AutoSize = true,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 6, 0, 0),
        };

        layout.Controls.Add(_outputRootLabel, 0, 0);
        layout.Controls.Add(_outputRootTextBox, 1, 0);
        layout.Controls.Add(_browseOutputButton, 2, 0);
        layout.Controls.Add(_outputPreviewLabel, 0, 1);
        layout.SetColumnSpan(_outputPreviewLabel, 3);

        _outputGroup.Controls.Add(layout);
        return _outputGroup;
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
            Text = L.Get("Gui_StartExport"),
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
            Text = L.Get("Gui_Cancel"),
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
            Text = L.Get("Gui_Ready"),
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
        _completionGroup = new GroupBox
        {
            Text = L.Get("Gui_CompletionGroupTitle"),
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
        _resultsListView.Columns.Add(L.Get("Gui_ColGame"), 165);
        _resultsListView.Columns.Add(L.Get("Gui_ColBuildId"), 95);
        _resultsListView.Columns.Add(L.Get("Gui_ColVerdict"), 175);
        _resultsListView.Columns.Add(L.Get("Gui_ColDbPath"), 340);
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
            Text = L.Get("Gui_VerdictPlaceholderIdle"),
            AutoSize = true,
            Dock = DockStyle.Fill,
            Font = _verdictFont,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 2, 0, 2),
        };

        _detailDbPathLabel = AddDetailRow(detailPanel, 0, L.Get("Gui_DetailDbPath"), _detailDbPathBox);
        _detailFileHashLabel = AddDetailRow(detailPanel, 1, L.Get("Gui_DetailFileHash"), _detailFileHashBox);
        _detailContentHashLabel = AddDetailRow(detailPanel, 2, L.Get("Gui_DetailContentHash"), _detailContentHashBox);
        _detailVerdictTitleLabel = AddDetailRow(detailPanel, 3, L.Get("Gui_DetailVerdict"), _detailVerdictLabel);

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
            Text = L.Get("Gui_OpenOutputFolder"),
            AutoSize = true,
            MinimumSize = new Size(124, 28),
            Margin = new Padding(0, 0, 8, 0),
        };
        _openOutputFolderButton.Click += OnOpenOutputFolderClicked;

        _copyHashesButton = new Button
        {
            Text = L.Get("Gui_CopyHashes"),
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

        _completionGroup.Controls.Add(layout);
        return _completionGroup;
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

    private static Label AddDetailRow(TableLayoutPanel panel, int rowIndex, string labelText, Control valueControl)
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
        return label;
    }

    private void SyncLanguageSelector()
    {
        _suppressLanguageChanged = true;
        try
        {
            var current = AppLocalization.CurrentLanguage;
            for (var i = 0; i < _languageComboBox.Items.Count; i++)
            {
                if (_languageComboBox.Items[i] is LanguageOption option && option.Language == current)
                {
                    _languageComboBox.SelectedIndex = i;
                    break;
                }
            }
        }
        finally
        {
            _suppressLanguageChanged = false;
        }
    }

    private void OnLanguageSelectionChanged(object? sender, EventArgs e)
    {
        if (_suppressLanguageChanged || _isExporting || _languageComboBox.SelectedItem is not LanguageOption option)
        {
            return;
        }

        if (option.Language == AppLocalization.CurrentLanguage && _userChangedLanguage)
        {
            return;
        }

        AppLocalization.SetProcessLanguage(option.Language);
        _userChangedLanguage = true;
        _settings.Language = AppLocalization.ToCode(option.Language);
        TrySaveSettings();
        ApplyLocalization();
    }

    private void ApplyLocalization()
    {
        SuspendLayout();
        try
        {
            Text = L.Format("Gui_WindowTitle", ReadDisplayVersion());
            _languageLabel.Text = L.Get("Lang_Label");
            SyncLanguageSelector();

            ApplyRootNoticeAndStatusBar(recordStartupWarning: false);

            _gamesGroup.Text = L.Get("Gui_GamesGroupTitle");
            _selectAllCheckBox.Text = L.Get("Gui_SelectAll");
            _rescanButton.Text = L.Get("Gui_Rescan");
            _manualAddButton.Text = L.Get("Gui_AddGameFolder");
            if (_gamesListView.Columns.Count >= 3)
            {
                _gamesListView.Columns[0].Text = L.Get("Gui_ColGame");
                _gamesListView.Columns[1].Text = L.Get("Gui_ColBuildId");
                _gamesListView.Columns[2].Text = L.Get("Gui_ColDirectory");
            }

            foreach (ListViewItem item in _gamesListView.Items)
            {
                if (item.Tag is GuiGameEntry game && item.SubItems.Count >= 2)
                {
                    item.SubItems[1].Text = game.BuildIdDisplay;
                }
            }

            _outputGroup.Text = L.Get("Gui_OutputGroupTitle");
            _outputRootLabel.Text = L.Get("Gui_OutputRootLabel");
            _browseOutputButton.Text = L.Get("Gui_Browse");

            _startButton.Text = L.Get("Gui_StartExport");
            _cancelButton.Text = L.Get("Gui_Cancel");
            RefreshProgressLabelText();

            _completionGroup.Text = L.Get("Gui_CompletionGroupTitle");
            if (_resultsListView.Columns.Count >= 4)
            {
                _resultsListView.Columns[0].Text = L.Get("Gui_ColGame");
                _resultsListView.Columns[1].Text = L.Get("Gui_ColBuildId");
                _resultsListView.Columns[2].Text = L.Get("Gui_ColVerdict");
                _resultsListView.Columns[3].Text = L.Get("Gui_ColDbPath");
            }

            _detailDbPathLabel.Text = L.Get("Gui_DetailDbPath");
            _detailFileHashLabel.Text = L.Get("Gui_DetailFileHash");
            _detailContentHashLabel.Text = L.Get("Gui_DetailContentHash");
            _detailVerdictTitleLabel.Text = L.Get("Gui_DetailVerdict");
            _openOutputFolderButton.Text = L.Get("Gui_OpenOutputFolder");
            _copyHashesButton.Text = L.Get("Gui_CopyHashes");

            RefreshLocalizedResults();

            if (_lastCopiedHashCount > 0)
            {
                _copyFeedbackLabel.Text = L.Format("Gui_CopiedHashesFeedback", _lastCopiedHashCount);
            }

            if (_initialStateLoaded)
            {
                SyncSelectionUi();
            }
            else
            {
                _selectionSummaryLabel.Text = L.Get("Gui_ScanningSteam");
                UpdateOutputPreview();
            }
        }
        finally
        {
            ResumeLayout(performLayout: true);
        }
    }

    private void RefreshProgressLabelText()
    {
        switch (_progressState)
        {
            case UiProgressState.Ready:
                _progressLabel.Text = L.Get("Gui_Ready");
                break;
            case UiProgressState.MissingCli:
                var lookup = CliCommandHelper.FindCliExecutableSafe(AppContext.BaseDirectory, Environment.ProcessPath);
                _progressLabel.Text = lookup.ResolvedPath is null && !string.IsNullOrWhiteSpace(lookup.WarningMessage)
                    ? L.Format("Common_WarningPrefix", lookup.WarningMessage)
                    : L.Get("Gui_Ready");
                break;
            case UiProgressState.Exporting:
                if (_activeGame is not null)
                {
                    _progressLabel.Text = CliCommandHelper.FormatGameProgressHeader(
                        _activeGameOneBased,
                        _activeTotalGames,
                        _activeGame,
                        _activeStep);
                }

                break;
            case UiProgressState.Cancelling:
                _progressLabel.Text = L.Get("Gui_CancellingProgress");
                break;
            case UiProgressState.Cancelled:
                _progressLabel.Text = L.Get("Gui_ExportCancelledProgress");
                break;
            case UiProgressState.Finished:
                var passed = _results.Count(r => r.Verdict is KnownBuildVerdictKind.Matched or KnownBuildVerdictKind.NewGameVersion);
                var mismatch = _results.Count(r => r.Verdict == KnownBuildVerdictKind.HashMismatch);
                var failed = _results.Count - passed - mismatch;
                _progressLabel.Text = L.Format("Gui_BatchFinishedSummary", _results.Count, passed, mismatch, failed);
                break;
        }
    }

    private void RefreshLocalizedResults()
    {
        if (_results.Count == 0)
        {
            _detailVerdictLabel.Text = _isWaitingForResults
                ? L.Get("Gui_VerdictPlaceholderWaiting")
                : L.Get("Gui_VerdictPlaceholderIdle");
            return;
        }

        var selectedIndex = _resultsListView.SelectedIndices.Count > 0
            ? _resultsListView.SelectedIndices[0]
            : -1;

        for (var i = 0; i < _results.Count; i++)
        {
            var updated = BuildReportReader.ReevaluateForCurrentLanguage(_results[i]);
            _results[i] = updated;
            if (i < _resultsListView.Items.Count)
            {
                var row = _resultsListView.Items[i];
                row.Tag = updated;
                row.ForeColor = GetVerdictColor(updated.Verdict);
                if (row.SubItems.Count >= 4)
                {
                    row.SubItems[1].Text = updated.EffectiveBuildId;
                    row.SubItems[2].Text = updated.VerdictDisplay;
                    row.SubItems[3].Text = updated.SqlitePath;
                }
            }
        }

        if (selectedIndex >= 0 && selectedIndex < _results.Count)
        {
            DisplayResultDetail(_results[selectedIndex]);
        }
    }

    private void EnsureInitialStateLoaded()
    {
        if (_initialStateLoaded)
        {
            return;
        }

        _initialStateLoaded = true;
        LoadInitialState();
    }

    private void LoadInitialState()
    {
        if (!string.IsNullOrWhiteSpace(_startupLogger?.InitializationWarning))
        {
            AppendLogLine(L.Format("Common_InfoPrefix", _startupLogger.InitializationWarning));
        }

        try
        {
            var legacySettingsPath = string.IsNullOrWhiteSpace(_settingsPathOverride)
                ? GuiSettingsStore.GetLegacySettingsPathForMigration(_agpRoot)
                : null;
            var settingsResult = GuiSettingsStore.LoadWithMigration(_settingsPath, legacySettingsPath);
            _settings = settingsResult.Settings;

            if (!_hasExternalLanguageOverride
                && AppLocalization.TryParseLanguage(_settings.Language, out var savedLang)
                && savedLang != AppLocalization.CurrentLanguage)
            {
                AppLocalization.SetProcessLanguage(savedLang);
                ApplyLocalization();
            }

            if (!string.IsNullOrWhiteSpace(settingsResult.InfoMessage))
            {
                _startupLogger?.WriteInfo(settingsResult.InfoMessage);
                AppendLogLine(L.Format("Common_InfoPrefix", settingsResult.InfoMessage));
            }

            if (!string.IsNullOrWhiteSpace(settingsResult.WarningMessage))
            {
                LogStartupDiagnosticWarning(settingsResult.WarningMessage, settingsResult.Error, "LoadSettings");
            }
        }
        catch (Exception ex)
        {
            _settings = new GuiSettings();
            LogStartupDiagnosticWarning(L.Format("Gui_LoadSettingsFailed", _settingsPath, ex.Message), ex, "LoadSettings");
        }

        _outputRootFallbackWarning = null;
        try
        {
            _suppressOutputWarningClear = true;
            if (!string.IsNullOrWhiteSpace(_settings.OutputRoot))
            {
                _outputRootTextBox.Text = _settings.OutputRoot.Trim();
            }
            else
            {
                var resolution = GuiOutputPlanner.ResolveDefaultOutputRoot(_agpRoot);
                if (resolution.UsedFallback && !string.IsNullOrWhiteSpace(resolution.WarningMessage))
                {
                    _outputRootFallbackWarning = resolution.WarningMessage;
                    if (!_rootNoticePanel.Visible)
                    {
                        LogStartupDiagnosticWarning(resolution.WarningMessage, error: null, "ResolveDefaultOutputRoot");
                    }
                }

                _outputRootTextBox.Text = resolution.OutputRoot;
            }
        }
        catch (Exception ex)
        {
            _outputRootFallbackWarning = L.Format("Gui_DefaultOutputRootFailed", ex.Message);
            _outputRootTextBox.Text = string.Empty;
            LogStartupDiagnosticWarning(_outputRootFallbackWarning, ex, "ResolveDefaultOutputRoot");
        }
        finally
        {
            _suppressOutputWarningClear = false;
        }

        ScanSteamGames(preserveManualEntries: false);

        var cliLookup = CliCommandHelper.FindCliExecutableSafe(
            AppContext.BaseDirectory,
            Environment.ProcessPath);
        if (cliLookup.ResolvedPath is null && !string.IsNullOrWhiteSpace(cliLookup.WarningMessage))
        {
            LogStartupDiagnosticWarning(cliLookup.WarningMessage, cliLookup.Error, "FindCliExecutable");
            _progressState = UiProgressState.MissingCli;
            _progressLabel.Text = L.Format("Common_WarningPrefix", cliLookup.WarningMessage);
        }
    }

    private void ScanSteamGames(bool preserveManualEntries)
    {
        var previousChecked = CaptureCheckedStates();
        var manualEntries = preserveManualEntries
            ? _games.Where(g => g.IsManual).ToList()
            : [];

        var scanResult = GuiGameScanner.DiscoverSteamGamesSafe();
        _lastScanWarning = scanResult.WarningMessage;
        if (!string.IsNullOrWhiteSpace(scanResult.WarningMessage))
        {
            LogStartupDiagnosticWarning(scanResult.WarningMessage, scanResult.Error, "ScanSteamGames");
        }

        var discovered = scanResult.Games;

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
            Description = L.Get("Gui_ManualGameDialogDesc"),
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
                L.Format("Gui_ManualGameDialogInvalidHint", ex.Message),
                L.Get("Gui_ManualGameDialogInvalidTitle"),
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
            Description = L.Get("Gui_BrowseOutputDialogDesc"),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };

        var currentRoot = GuiGameScanner.NormalizeUserPath(_outputRootTextBox.Text);
        if (TryDirectoryExists(currentRoot))
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

        var cliLookup = CliCommandHelper.FindCliExecutableSafe(
            AppContext.BaseDirectory,
            Environment.ProcessPath);
        if (cliLookup.ResolvedPath is null)
        {
            var missingText = cliLookup.Error is not null && !string.IsNullOrWhiteSpace(cliLookup.WarningMessage)
                ? cliLookup.WarningMessage
                : CliCommandHelper.FormatMissingCliMessage(cliLookup.ExpectedPath);
            MessageBox.Show(
                this,
                missingText,
                L.Get("Gui_MissingCliDialogTitle"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        var cliPath = cliLookup.ResolvedPath;

        var selectedGames = GetCheckedGames();
        if (selectedGames.Count == 0)
        {
            MessageBox.Show(
                this,
                L.Get("Gui_NoGameSelectedMessage"),
                L.Get("Gui_NoGameSelectedTitle"),
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
                L.Format("Gui_InvalidOutputMessage", ex.Message),
                L.Get("Gui_InvalidOutputTitle"),
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
                L.Format("Gui_ConfirmOverwriteMessage", pathLines),
                L.Get("Gui_ConfirmOverwriteTitle"),
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
        _progressState = UiProgressState.Cancelling;
        _progressLabel.Text = L.Get("Gui_CancellingProgress");
        AppendLogLine(L.Get("Gui_CancellingLog"));
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
        _lastCopiedHashCount = 0;
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

                _progressState = UiProgressState.Exporting;
                _activeGameOneBased = oneBased;
                _activeTotalGames = total;
                _activeGame = item.Game;
                _activeStep = null;
                _progressLabel.Text = CliCommandHelper.FormatGameProgressHeader(oneBased, total, item.Game);
                _progressBar.Value = CliCommandHelper.CalculateProgressPermille(currentGameIndex, total);

                AppendLogLine("============================================================");
                AppendLogLine(L.Format("Gui_BatchItemStartLog", oneBased, total, item.Game.DisplayLabel, item.Game.BuildIdDisplay));
                AppendLogLine(L.Format("Gui_BatchItemGameDirLog", item.Game.GameDirectory));
                AppendLogLine(L.Format("Gui_BatchItemOutputDirLog", item.OutputDirectory));

                if (item.DirectoryState == OutputDirectoryState.ForeignNonEmptyDirectory)
                {
                    try
                    {
                        AppendLogLine(L.Format("Gui_BatchClearingForeignLog", item.OutputDirectory));
                        GuiOutputPlanner.ClearConfirmedForeignDirectory(item.OutputDirectory, protectedGameDirs);
                    }
                    catch (Exception ex)
                    {
                        AppendLogLine(L.Format("Gui_BatchClearForeignErrorLog", item.OutputDirectory, ex.Message));
                        var clearFail = BuildReportReader.EvaluateOutcome(
                            item.Game,
                            item.OutputDirectory,
                            exitCode: 2,
                            cancelled: false,
                            report: null,
                            readError: L.Format("Gui_BatchClearForeignReadError", ex.Message));
                        AddExportResult(clearFail);
                        _progressBar.Value = CliCommandHelper.CalculateProgressPermille(oneBased, total);
                        continue;
                    }
                }
                else if (item.UseForceFlag)
                {
                    AppendLogLine(L.Get("Gui_BatchForceHintLog"));
                }

                var cliArgs = CliCommandHelper.BuildBuildDbArguments(
                    item.Game.GameDirectory,
                    item.OutputDirectory,
                    force: item.UseForceFlag,
                    language: AppLocalization.CurrentLanguageCode,
                    progressJson: true);
                var agpTempDir = _agpRoot.Succeeded ? AgpPaths.GetTempDirectory(_agpRoot) : null;
                var startInfo = CliCommandHelper.CreateStartInfo(
                    cliPath,
                    cliArgs,
                    agpHome: _agpRoot.Succeeded ? _agpRoot.AgpHome : null,
                    tempDirectory: agpTempDir);

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
                    AppendLogLine(L.Format("Gui_BatchItemCancelledLog", item.Game.GameCode));
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
                    AppendLogLine(L.Format("Gui_BatchLaunchCliErrorLog", Path.GetFileName(cliPath), ex.Message));
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
        if (CliCommandHelper.TryParseProgressStep(line, out var parsedStep))
        {
            PostToUi(() =>
            {
                _progressState = UiProgressState.Exporting;
                _activeGameOneBased = currentIndexOneBased;
                _activeTotalGames = totalGames;
                _activeGame = game;
                _activeStep = parsedStep;
                _progressLabel.Text = CliCommandHelper.FormatGameProgressHeader(
                    currentIndexOneBased,
                    totalGames,
                    game,
                    parsedStep);
                _progressBar.Value = CliCommandHelper.CalculateProgressPermille(
                    completedGames,
                    totalGames,
                    parsedStep);
            });
            return;
        }

        if (CliCommandHelper.IsProgressProtocolLine(line))
        {
            return;
        }

        PostToUi(() => AppendLogLine(line));
    }

    private void AddExportResult(GuiGameExportResult result)
    {
        _isWaitingForResults = false;
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
            _progressState = UiProgressState.Cancelled;
            _progressLabel.Text = L.Get("Gui_ExportCancelledProgress");
            AppendLogLine(L.Get("Gui_ExportCancelledLog"));
            return;
        }

        var passed = _results.Count(r => r.Verdict is KnownBuildVerdictKind.Matched or KnownBuildVerdictKind.NewGameVersion);
        var mismatch = _results.Count(r => r.Verdict == KnownBuildVerdictKind.HashMismatch);
        var failed = _results.Count - passed - mismatch;

        _progressBar.Value = 1000;
        _progressState = UiProgressState.Finished;
        _progressLabel.Text = L.Format("Gui_BatchFinishedSummary", _results.Count, passed, mismatch, failed);
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
        _detailFileHashBox.Text = result.Report?.FileSha256 ?? L.Get("Common_NoneParen");
        _detailContentHashBox.Text = result.Report?.ContentSha256 ?? L.Get("Common_NoneParen");
        _detailVerdictLabel.Text = result.VerdictDetailText;
        _detailVerdictLabel.ForeColor = GetVerdictColor(result.Verdict);
    }

    private void ClearResultDetail()
    {
        _isWaitingForResults = true;
        _detailDbPathBox.Text = string.Empty;
        _detailFileHashBox.Text = string.Empty;
        _detailContentHashBox.Text = string.Empty;
        _detailVerdictLabel.Text = L.Get("Gui_VerdictPlaceholderWaiting");
        _detailVerdictLabel.ForeColor = SystemColors.GrayText;
    }

    private void OnOpenOutputFolderClicked(object? sender, EventArgs e)
    {
        var selectedDir = _resultsListView.SelectedItems.Count > 0
            && _resultsListView.SelectedItems[0].Tag is GuiGameExportResult selected
            ? selected.OutputDirectory
            : null;
        var recentDirs = _results.Select(r => r.OutputDirectory).ToArray();
        var targetDir = GuiOutputPlanner.ResolveFolderToOpen(
            selectedDir,
            recentDirs,
            _outputRootTextBox.Text,
            TryDirectoryExists);

        if (string.IsNullOrWhiteSpace(targetDir))
        {
            MessageBox.Show(
                this,
                L.Get("Gui_OpenFolderNotCreatedMessage"),
                L.Get("Gui_OpenFolderNotCreatedTitle"),
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
                L.Format("Gui_OpenFolderFailedMessage", targetDir, ex.Message),
                L.Get("Gui_OpenFolderFailedTitle"),
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
                L.Get("Gui_CopyHashesEmptyMessage"),
                L.Get("Gui_CopyHashes"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            Clipboard.SetText(text);
            var count = _results.Count(r => r.Report is not null);
            _lastCopiedHashCount = count;
            _copyFeedbackLabel.Text = L.Format("Gui_CopiedHashesFeedback", count);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                L.Format("Gui_CopyHashesFailedMessage", ex.Message),
                L.Get("Gui_CopyHashesFailedTitle"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void SetInputsLocked(bool locked)
    {
        _languageComboBox.Enabled = !locked;
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
            if (!string.IsNullOrWhiteSpace(_lastScanWarning))
            {
                _selectionSummaryLabel.Text = _lastScanWarning;
                _selectionSummaryLabel.ForeColor = Color.FromArgb(196, 43, 28);
            }
            else
            {
                _selectionSummaryLabel.Text = L.Get("Gui_NoGamesFoundHint");
                _selectionSummaryLabel.ForeColor = SystemColors.GrayText;
            }
        }
        else
        {
            _selectAllCheckBox.Enabled = !_isExporting;
            _selectAllCheckBox.CheckState = checkedCount == 0
                ? CheckState.Unchecked
                : checkedCount == total
                    ? CheckState.Checked
                    : CheckState.Indeterminate;
            _selectionSummaryLabel.ForeColor = SystemColors.GrayText;
            _selectionSummaryLabel.Text = L.Format("Gui_SelectionSummary", checkedCount, total);
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
        var preview = GuiOutputPlanner.FormatOutputPreview(
            _outputRootTextBox.Text,
            GetCheckedGames());
        if (!string.IsNullOrWhiteSpace(_outputRootFallbackWarning))
        {
            _outputPreviewLabel.Text = L.Format("Gui_OutputPreviewWithHint", preview, _outputRootFallbackWarning);
            _outputPreviewLabel.ForeColor = Color.FromArgb(180, 83, 9);
        }
        else
        {
            _outputPreviewLabel.Text = preview;
            _outputPreviewLabel.ForeColor = SystemColors.GrayText;
        }
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
                Language = _userChangedLanguage ? AppLocalization.CurrentLanguageCode : _settings.Language,
                CheckedGames = new Dictionary<string, bool>(mergedChecked, StringComparer.Ordinal),
            };
            GuiSettingsStore.Save(_settingsPath, toSave);
            _settings = toSave;
        }
        catch
        {
            // Never block closing or export if the settings directory is read-only.
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

    private string ResolveSettingsPathSafely(string? settingsPathOverride, AgpRootResolution agpRoot)
    {
        if (!string.IsNullOrWhiteSpace(settingsPathOverride))
        {
            return settingsPathOverride.Trim();
        }

        try
        {
            return GuiSettingsStore.GetDefaultSettingsPath(agpRoot);
        }
        catch (Exception ex)
        {
            RecordStartupWarning(L.Format("Gui_DefaultSettingsPathFailed", ex.Message));
            _startupLogger?.WriteException("ResolveSettingsPathSafely", ex);
            return string.Empty;
        }
    }

    private Font CreateFontSafe(string roleLabel, IReadOnlyList<string> preferredFamilies, float emSize, FontStyle style)
    {
        var selection = GuiFontFallbackPolicy.ResolveFont(
            roleLabel,
            preferredFamilies,
            tryCreateFromFamily: familyName => TryCreateNamedFont(familyName, emSize, style),
            tryCreateFromMessageBoxFont: () => TryCreateFallbackFromMessageBoxFont(emSize, style),
            getUltimateFallback: () => (Control.DefaultFont, Owned: false, Name: Control.DefaultFont.Name));

        if (selection.OwnedByCaller)
        {
            _ownedFonts.Add(selection.Font);
        }

        if (!string.IsNullOrWhiteSpace(selection.WarningMessage))
        {
            RecordStartupWarning(selection.WarningMessage);
        }

        return selection.Font;
    }

    private static Font? TryCreateNamedFont(string familyName, float emSize, FontStyle style)
    {
        using var family = new FontFamily(familyName);
        if (!family.IsStyleAvailable(style))
        {
            return null;
        }

        return new Font(family.Name, emSize, style, GraphicsUnit.Point);
    }

    private static (Font Font, bool Owned, string Name)? TryCreateFallbackFromMessageBoxFont(float emSize, FontStyle style)
    {
        var messageBoxFont = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        if (messageBoxFont is null)
        {
            return null;
        }

        try
        {
            if (messageBoxFont.FontFamily.IsStyleAvailable(style))
            {
                var styled = new Font(messageBoxFont.FontFamily, emSize, style, GraphicsUnit.Point);
                messageBoxFont.Dispose();
                return (styled, Owned: true, Name: styled.Name);
            }

            if (style != FontStyle.Regular && messageBoxFont.FontFamily.IsStyleAvailable(FontStyle.Regular))
            {
                var regular = new Font(messageBoxFont.FontFamily, emSize, FontStyle.Regular, GraphicsUnit.Point);
                messageBoxFont.Dispose();
                return (regular, Owned: true, Name: regular.Name);
            }
        }
        catch
        {
            // Fall through and return the raw SystemFonts.MessageBoxFont instance.
        }

        return (messageBoxFont, Owned: true, Name: messageBoxFont.Name);
    }

    private void RecordStartupWarning(string warning)
    {
        _startupWarnings.Add(warning);
        _startupLogger?.WriteWarning(warning);
    }

    private void FlushStartupWarningsToUi()
    {
        if (_startupWarnings.Count == 0)
        {
            return;
        }

        foreach (var warning in _startupWarnings)
        {
            AppendLogLine(L.Format("Common_WarningPrefix", warning));
        }
    }

    private void LogStartupDiagnosticWarning(string warning, Exception? error, string context)
    {
        AppendLogLine(L.Format("Common_WarningPrefix", warning));
        if (error is not null)
        {
            _startupLogger?.WriteException(context, error);
        }
        else
        {
            _startupLogger?.WriteWarning($"[{context}] {warning}");
        }
    }

    private static void EnsureNativeHandlesCreated(Control control)
    {
        control.CreateControl();
        if (!control.IsHandleCreated)
        {
            _ = control.Handle;
        }

        foreach (Control child in control.Controls)
        {
            EnsureNativeHandlesCreated(child);
        }
    }

    private static bool TryDirectoryExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return Directory.Exists(PathSupport.ToIoPath(path));
        }
        catch
        {
            return false;
        }
    }

    private static void AdjustLastColumnWidth(ListView listView, int minLastColumnWidth)
    {
        if (listView.IsDisposed || listView.Columns.Count < 2)
        {
            return;
        }

        try
        {
            var fixedWidth = 0;
            for (var i = 0; i < listView.Columns.Count - 1; i++)
            {
                fixedWidth += listView.Columns[i].Width;
            }

            var available = listView.ClientSize.Width - fixedWidth - 4;
            listView.Columns[^1].Width = Math.Max(minLastColumnWidth, available);
        }
        catch (ObjectDisposedException)
        {
            // Control disposed during resize/teardown.
        }
        catch (InvalidOperationException)
        {
            // Column sizing unavailable before native handle initialization.
        }
    }

    private static string ReadDisplayVersion()
    {
        try
        {
            var assembly = typeof(MainForm).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                return informational.Split('+')[0];
            }

            return assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        }
        catch
        {
            return "0.1.0";
        }
    }
}
