using System.Drawing;
using System.IO;
using System.Windows.Forms;
using PK2Editor.PK2;

namespace PK2Editor;

public sealed class MainForm : Form
{
    private readonly MenuStrip _menu;
    private readonly ToolStrip _toolbar;
    private readonly StatusStrip _status;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly ToolStripStatusLabel _hashStats;
    private readonly SplitContainer _splitMain;
    private readonly TreeView _tree;
    private readonly ListView _list;

    private readonly Panel _searchPanel;
    private readonly TextBox _searchBox;
    private readonly Button _searchClear;

    private readonly ToolStripButton _btnOpen;
    private readonly ToolStripButton _btnExtractSel;
    private readonly ToolStripButton _btnExtractAll;
    private readonly ToolStripButton _btnRebuild;

    private readonly HashDatabase _hashDb = new();
    private PK2Reader? _reader;
    private List<object> _currentView = new();

    private const string AppTitle = "PK2 Editor - CSI: 3 Dimensions of Murder (PS2)";
    private const int TreePanelMinWidth = 240;
    private const int TreePanelMaxWidth = 420;
    private const int RightPanelMinWidth = 620;

    public MainForm()
    {
        Text = AppTitle;
        StartPosition = FormStartPosition.CenterScreen;
        Width = 1280;
        Height = 720;
        MinimumSize = new Size(900, 520);

        _menu = BuildMenu();
        MainMenuStrip = _menu;

        _toolbar = BuildToolbar(out _btnOpen, out _btnExtractSel, out _btnExtractAll, out _btnRebuild);

        _searchPanel = BuildSearchPanel(out _searchBox, out _searchClear);

        _status = new StatusStrip();
        _statusLabel = new ToolStripStatusLabel("Ready. Open a GameData.pk2 file from the File menu.")
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _hashStats = new ToolStripStatusLabel("HashDB: 0");
        _status.Items.Add(_statusLabel);
        _status.Items.Add(_hashStats);

        _tree = new TreeView
        {
            Dock = DockStyle.Fill,
            HideSelection = false,
            Font = new Font("Segoe UI", 9F),
            ShowLines = true,
            ShowRootLines = true,
        };
        _tree.AfterSelect += (_, _) => RefreshList();
        _tree.NodeMouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
                _tree.SelectedNode = e.Node;
        };
        var treeContext = new ContextMenuStrip();
        treeContext.Items.Add("Reinsert files into this folder from folder...", null, (_, _) => ReinsertTreeFolderFromFolder());
        treeContext.Opening += (_, e) => e.Cancel = _tree.SelectedNode?.Tag is not PK2Folder;
        _tree.ContextMenuStrip = treeContext;

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false,
            MultiSelect = true,
            Font = new Font("Segoe UI", 9F),
        };
        _list.Columns.Add("Name", 360);
        _list.Columns.Add("Folder", 320);
        _list.Columns.Add("CRC32", 90);
        _list.Columns.Add("Offset", 110);
        _list.Columns.Add("Size (bytes)", 120);
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.MouseDoubleClick += (_, _) => OpenListItemOrExtract();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { OpenListItemOrExtract(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.A)
            {
                foreach (ListViewItem it in _list.Items) it.Selected = true;
                e.Handled = true;
            }
        };
        _list.ColumnClick += (_, e) => SortByColumn(e.Column);

        var listContext = new ContextMenuStrip();
        listContext.Items.Add("Extract selected file(s)...", null, (_, _) => ExtractSelected());
        listContext.Items.Add("Reinsert selected file...", null, (_, _) => ReinsertSingleSelectedFile());
        listContext.Items.Add("Reinsert selected file(s) from folder...", null, (_, _) => ReinsertSelectedFromFolder());
        listContext.Items.Add(new ToolStripSeparator());
        listContext.Items.Add("Copy path", null, (_, _) => CopySelectedPath());
        listContext.Items.Add("Copy CRC32", null, (_, _) => CopySelectedHash());
        _list.ContextMenuStrip = listContext;

        var leftHost = new Panel { Dock = DockStyle.Fill };
        leftHost.Controls.Add(_tree);

        var rightHost = new Panel { Dock = DockStyle.Fill };
        rightHost.Controls.Add(_list);
        rightHost.Controls.Add(_searchPanel);

        _splitMain = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 300,
            FixedPanel = FixedPanel.Panel1,
            SplitterWidth = 5,
        };
        _splitMain.Panel1.Controls.Add(leftHost);
        _splitMain.Panel2.Controls.Add(rightHost);

        // Order matters: docked controls fill in reverse-add order. Add the
        // central content first, then the top bars and bottom status.
        Controls.Add(_splitMain);
        Controls.Add(_toolbar);
        Controls.Add(_menu);
        Controls.Add(_status);

        Load += (_, _) => LoadHashDatabase();
        Shown += (_, _) => ConfigureSplitPanelLimits();
        Resize += (_, _) =>
        {
            AutoSizeTreePanel();
            AutoSizeListColumns();
        };
        FormClosing += (_, _) => _reader?.Dispose();
        UpdateButtons();
    }

    // ---- UI building -----------------------------------------------------

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip { Dock = DockStyle.Top };

        var file = new ToolStripMenuItem("&File");
        var miOpen = new ToolStripMenuItem("&Open GameData.pk2...", null, (_, _) => OpenArchive())
        { ShortcutKeys = Keys.Control | Keys.O };
        var miClose = new ToolStripMenuItem("&Close archive", null, (_, _) => CloseArchive());
        var miExtractSel = new ToolStripMenuItem("Extract &selected...", null, (_, _) => ExtractSelected())
        { ShortcutKeys = Keys.Control | Keys.E };
        var miExtractAll = new ToolStripMenuItem("Extract &all...", null, (_, _) => ExtractAll());
        var miRebuild = new ToolStripMenuItem("&Rebuild PK2 from folder...", null, (_, _) => RebuildFromFolder())
        { ShortcutKeys = Keys.Control | Keys.R };
        var miExit = new ToolStripMenuItem("E&xit", null, (_, _) => Close());
        file.DropDownItems.AddRange(new ToolStripItem[]
        {
            miOpen, miClose, new ToolStripSeparator(),
            miExtractSel, miExtractAll, miRebuild, new ToolStripSeparator(), miExit,
        });

        menu.Items.Add(file);
        return menu;
    }

    private ToolStrip BuildToolbar(
        out ToolStripButton open,
        out ToolStripButton extractSel,
        out ToolStripButton extractAll,
        out ToolStripButton rebuild)
    {
        var bar = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            ImageScalingSize = new Size(20, 20),
            Padding = new Padding(4, 2, 4, 2),
            RenderMode = ToolStripRenderMode.System,
        };

        open = new ToolStripButton("Open PK2")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
        };
        open.Click += (_, _) => OpenArchive();

        extractSel = new ToolStripButton("Extract selected")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Enabled = false,
        };
        extractSel.Click += (_, _) => ExtractSelected();

        extractAll = new ToolStripButton("Extract all")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Enabled = false,
        };
        extractAll.Click += (_, _) => ExtractAll();

        rebuild = new ToolStripButton("Rebuild PK2")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Enabled = false,
        };
        rebuild.Click += (_, _) => RebuildFromFolder();

        bar.Items.Add(open);
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(extractSel);
        bar.Items.Add(extractAll);
        bar.Items.Add(rebuild);
        return bar;
    }

    private Panel BuildSearchPanel(out TextBox box, out Button clear)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 44,
            Padding = new Padding(8, 8, 8, 8),
            BackColor = SystemColors.ControlLightLight,
        };

        var label = new Label
        {
            Text = "Search:",
            AutoSize = true,
            Location = new Point(8, 14),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
        };

        var searchBox = new TextBox
        {
            Location = new Point(80, 10),
            Width = 520,
            Font = new Font("Segoe UI", 10F),
            PlaceholderText = "type part of the name, folder, or CRC32...",
        };
        searchBox.TextChanged += (_, _) => RefreshList();
        searchBox.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Escape) { searchBox.Clear(); e.Handled = true; e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Down) { _list.Focus(); if (_list.Items.Count > 0) _list.Items[0].Selected = true; e.Handled = true; }
        };
        var clearBtn = new Button
        {
            Text = "Clear",
            Location = new Point(610, 8),
            Width = 70,
            Height = 26,
        };
        clearBtn.Click += (_, _) => searchBox.Clear();

        panel.Controls.Add(label);
        panel.Controls.Add(searchBox);
        panel.Controls.Add(clearBtn);

        // Ctrl+F focuses the search box from anywhere in the form.
        KeyPreview = true;
        KeyDown += (s, e) =>
        {
            if (e.Control && e.KeyCode == Keys.F)
            {
                searchBox.Focus();
                searchBox.SelectAll();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };

        box = searchBox;
        clear = clearBtn;
        return panel;
    }

    // ---- archive lifecycle ----------------------------------------------

    private void LoadHashDatabase()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Resources", "Files_CSI3.symmap");
            if (File.Exists(path)) _hashDb.LoadSymmap(path);
            _hashStats.Text = $"HashDB: {_hashDb.Count}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to load hash database: {ex.Message}",
                "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenArchive()
    {
        using var ofd = new OpenFileDialog
        {
            Filter = "PK2 files|*.pk2|All files|*.*",
            Title = "Open GameData.pk2",
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;

        CloseArchive();
        try
        {
            UseWaitCursor = true;
            Application.DoEvents();
            var r = new PK2Reader(ofd.FileName, _hashDb);
            r.Parse();
            _reader = r;
            RebuildTree();
            RefreshList();
            UpdateStatusCount();
            Text = $"{AppTitle} - {Path.GetFileName(ofd.FileName)}";
        }
        catch (Exception ex)
        {
            _reader?.Dispose();
            _reader = null;
            MessageBox.Show(this, $"Could not open the file:\n\n{ex.Message}",
                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            UpdateButtons();
        }
    }

    private void CloseArchive()
    {
        _reader?.Dispose();
        _reader = null;
        _tree.Nodes.Clear();
        _list.Items.Clear();
        _statusLabel.Text = "Ready.";
        Text = AppTitle;
        UpdateButtons();
    }

    private void RebuildTree()
    {
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        if (_reader != null)
        {
            var root = new TreeNode($"{Path.GetFileName(_reader.FilePath)}  [{_reader.Root.Files.Count}]")
            {
                Tag = _reader.Root,
            };
            BuildNode(root, _reader.Root, includeFiles: true);
            _tree.Nodes.Add(root);
            root.Expand();
            _tree.SelectedNode = root;
        }
        _tree.EndUpdate();
        AutoSizeTreePanel();

        static void BuildNode(TreeNode parent, PK2Folder folder, bool includeFiles)
        {
            foreach (var sub in folder.Subfolders.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                string label = $"{sub.Name.TrimEnd('\\', '/')}  [{sub.Files.Count}]";
                var n = new TreeNode(label) { Tag = sub };
                BuildNode(n, sub, includeFiles: false);
                parent.Nodes.Add(n);
            }

            if (includeFiles)
            {
                foreach (var file in folder.Files.OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase))
                {
                    parent.Nodes.Add(new TreeNode(file.DisplayName) { Tag = file });
                }
            }
        }
    }

    private void AutoSizeTreePanel()
    {
        if (_splitMain.IsDisposed || _tree.Nodes.Count == 0 || _splitMain.Width <= 0)
            return;

        ConfigureSplitPanelLimits();

        int contentWidth = MeasureTreeNodes(_tree.Nodes, 0) + 48;
        int desired = Math.Clamp(contentWidth, TreePanelMinWidth, TreePanelMaxWidth);
        int maxAllowed = Math.Max(TreePanelMinWidth, _splitMain.Width - RightPanelMinWidth - _splitMain.SplitterWidth);
        desired = Math.Min(desired, maxAllowed);

        if (desired > 0 && Math.Abs(_splitMain.SplitterDistance - desired) > 8)
            _splitMain.SplitterDistance = desired;
    }

    private void ConfigureSplitPanelLimits()
    {
        if (_splitMain.Width <= TreePanelMinWidth + RightPanelMinWidth + _splitMain.SplitterWidth)
            return;

        _splitMain.Panel1MinSize = TreePanelMinWidth;
        _splitMain.Panel2MinSize = RightPanelMinWidth;
    }

    private int MeasureTreeNodes(TreeNodeCollection nodes, int depth)
    {
        int max = 0;
        foreach (TreeNode node in nodes)
        {
            int textWidth = TextRenderer.MeasureText(node.Text, _tree.Font).Width;
            int nodeWidth = (depth * 20) + textWidth;
            max = Math.Max(max, nodeWidth);
            if (node.Nodes.Count > 0)
                max = Math.Max(max, MeasureTreeNodes(node.Nodes, depth + 1));
        }
        return max;
    }

    private void AutoSizeListColumns()
    {
        if (_list.Columns.Count < 5 || _list.ClientSize.Width <= 0)
            return;

        int available = Math.Max(620, _list.ClientSize.Width - 8);
        const int crcWidth = 92;
        const int offsetWidth = 108;
        const int sizeWidth = 118;

        int folderWidth = Math.Clamp((int)(available * 0.34), 220, 340);
        int nameWidth = Math.Max(240, available - folderWidth - crcWidth - offsetWidth - sizeWidth);

        _list.Columns[0].Width = nameWidth;
        _list.Columns[1].Width = folderWidth;
        _list.Columns[2].Width = crcWidth;
        _list.Columns[3].Width = offsetWidth;
        _list.Columns[4].Width = sizeWidth;
    }

    // ---- listing & search ------------------------------------------------

    private void RefreshList()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        _currentView.Clear();

        if (_reader != null)
        {
            string filter = _searchBox.Text.Trim();
            if (!string.IsNullOrEmpty(filter))
            {
                // Global search ignores the selected folder in the tree.
                var folders = EnumerateFolders(_reader.Root)
                    .Where(f => !string.IsNullOrEmpty(f.FullPath))
                    .Where(f =>
                        f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        f.FullPath.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase)
                    .Cast<object>();

                var files = _reader.AllFiles.Where(f =>
                    f.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    f.FolderPath.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    f.NameHash.ToString("X8").Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f.FolderPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Cast<object>();

                _currentView = folders.Concat(files).ToList();
            }
            else
            {
                _currentView = _tree.SelectedNode?.Tag switch
                {
                    PK2File file => new List<object> { file },
                    PK2Folder folder => folder.Subfolders
                        .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                        .Cast<object>()
                        .Concat(folder.Files
                            .OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase)
                            .Cast<object>())
                        .ToList(),
                    _ => _reader.Root.Subfolders
                        .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                        .Cast<object>()
                        .Concat(_reader.Root.Files
                            .OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase)
                            .Cast<object>())
                        .ToList(),
                };
            }

            foreach (var item in _currentView)
                _list.Items.Add(CreateListItem(item));
        }
        _list.EndUpdate();
        AutoSizeListColumns();
        UpdateStatusCount();
        UpdateButtons();
    }

    private void SortByColumn(int column)
    {
        if (_currentView.Count == 0) return;
        IEnumerable<object> sorted = column switch
        {
            0 => _currentView
                .OrderBy(i => i is PK2Folder ? 0 : 1)
                .ThenBy(DisplaySortName, StringComparer.OrdinalIgnoreCase),
            1 => _currentView
                .OrderBy(i => i is PK2Folder folder ? folder.FullPath : ((PK2File)i).FolderPath,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i is PK2Folder ? 0 : 1)
                .ThenBy(DisplaySortName, StringComparer.OrdinalIgnoreCase),
            2 => _currentView
                .OrderBy(i => i is PK2Folder ? 0u : ((PK2File)i).NameHash),
            3 => _currentView
                .OrderBy(i => i is PK2Folder ? 0u : ((PK2File)i).Offset),
            4 => _currentView
                .OrderBy(i => i is PK2Folder ? 0u : ((PK2File)i).Size),
            _ => _currentView,
        };
        _currentView = sorted.ToList();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var item in _currentView)
            _list.Items.Add(CreateListItem(item));
        _list.EndUpdate();
        AutoSizeListColumns();
    }

    private ListViewItem CreateListItem(object item)
    {
        if (item is PK2Folder folder)
        {
            return new ListViewItem(new[]
            {
                folder.Name.TrimEnd('\\', '/'),
                folder.FullPath,
                "",
                "",
                "Folder",
            })
            {
                Tag = folder,
                Font = new Font(_list.Font, FontStyle.Bold),
            };
        }

        var file = (PK2File)item;
        var listItem = new ListViewItem(new[]
        {
            file.DisplayName,
            file.FolderPath,
            $"{file.NameHash:X8}",
            $"0x{file.Offset:X8}",
            file.Size.ToString("N0"),
        })
        { Tag = file };
        if (!file.IsResolved) listItem.ForeColor = Color.DimGray;
        return listItem;
    }

    private static string DisplaySortName(object item)
    {
        return item switch
        {
            PK2Folder folder => folder.Name,
            PK2File file => file.DisplayName,
            _ => string.Empty,
        };
    }

    private void UpdateStatusCount()
    {
        if (_reader == null)
        {
            _statusLabel.Text = "Ready. Open a GameData.pk2 file from the File menu.";
            return;
        }
        int total = _reader.AllFiles.Count;
        int shown = _list.Items.Count;
        int resolved = _reader.AllFiles.Count(f => f.IsResolved);
        int sel = _list.SelectedItems.Count;
        string selPart = sel > 0 ? $" - {sel} selected" : "";
        _statusLabel.Text =
            $"{Path.GetFileName(_reader.FilePath)} - showing {shown} of {total} files " +
            $"({resolved} named){selPart}";
    }

    private void UpdateButtons()
    {
        bool hasArchive = _reader != null;
        bool hasSel = _list.SelectedItems.Cast<ListViewItem>().Any(i => i.Tag is PK2File);
        _btnExtractSel.Enabled = hasArchive && hasSel;
        _btnExtractAll.Enabled = hasArchive;
        _btnRebuild.Enabled = hasArchive;
        UpdateStatusCount();
    }

    private static IEnumerable<PK2File> CollectRecursive(PK2Folder folder)
    {
        foreach (var f in folder.Files) yield return f;
        foreach (var sub in folder.Subfolders)
            foreach (var f in CollectRecursive(sub)) yield return f;
    }

    private static IEnumerable<PK2Folder> EnumerateFolders(PK2Folder folder)
    {
        yield return folder;
        foreach (var sub in folder.Subfolders)
            foreach (var item in EnumerateFolders(sub))
                yield return item;
    }

    private void OpenListItemOrExtract()
    {
        if (_list.SelectedItems.Count == 1 && _list.SelectedItems[0].Tag is PK2Folder folder)
        {
            SelectTreeNode(folder);
            return;
        }

        ExtractSelected();
    }

    private void SelectTreeNode(PK2Folder folder)
    {
        foreach (TreeNode node in _tree.Nodes)
        {
            var found = FindTreeNode(node, folder);
            if (found == null) continue;

            _tree.SelectedNode = found;
            found.EnsureVisible();
            found.Expand();
            _tree.Focus();
            return;
        }
    }

    private static TreeNode? FindTreeNode(TreeNode node, PK2Folder folder)
    {
        if (ReferenceEquals(node.Tag, folder))
            return node;

        foreach (TreeNode child in node.Nodes)
        {
            var found = FindTreeNode(child, folder);
            if (found != null)
                return found;
        }

        return null;
    }

    // ---- extraction ------------------------------------------------------

    private void ExtractSelected()
    {
        if (_reader == null) return;
        if (_list.SelectedItems.Count == 0)
        {
            MessageBox.Show(this, "Select at least one file in the list.",
                "Nothing selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var selected = _list.SelectedItems.Cast<ListViewItem>()
            .Select(i => i.Tag)
            .OfType<PK2File>()
            .ToList();

        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Select one or more files. Folders can be opened with Enter or double-click.",
                "No files selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (selected.Count == 1)
        {
            var f = selected[0];
            using var sfd = new SaveFileDialog
            {
                FileName = f.DisplayName.TrimEnd('\\', '/'),
                Filter = "All files|*.*",
                Title = "Save extracted file",
            };
            if (sfd.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                File.WriteAllBytes(sfd.FileName, _reader.ExtractFile(f));
                _statusLabel.Text = $"Extracted: {sfd.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Extraction failed: {ex.Message}",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return;
        }

        using var fbd = new FolderBrowserDialog
        {
            Description = $"Destination folder for the {selected.Count} selected files",
        };
        if (fbd.ShowDialog(this) != DialogResult.OK) return;
        ExtractMany(selected, fbd.SelectedPath);
    }

    private void ExtractAll()
    {
        if (_reader == null) return;
        using var fbd = new FolderBrowserDialog { Description = "Destination folder for full extraction" };
        if (fbd.ShowDialog(this) != DialogResult.OK) return;
        ExtractMany(_reader.AllFiles, fbd.SelectedPath);
    }

    private void ExtractMany(List<PK2File> files, string destRoot)
    {
        if (_reader == null) return;
        try
        {
            using var progress = new ProgressForm("Extracting files...", files.Count);
            progress.Show(this);
            int i = 0;
            foreach (var f in files)
            {
                string targetDir = Path.Combine(destRoot, SanitizeFolderPath(f.FolderPath));
                Directory.CreateDirectory(targetDir);
                string targetFile = Path.Combine(targetDir, SanitizeFileName(f.DisplayName));
                File.WriteAllBytes(targetFile, _reader.ExtractFile(f));
                progress.SetProgress(++i, $"{i}/{files.Count}: {f.DisplayName}");
                if (i % 32 == 0) Application.DoEvents();
                if (progress.IsCancelled) break;
            }
            progress.Close();
            _statusLabel.Text = $"Extraction complete: {i} files in {destRoot}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Extraction failed: {ex.Message}",
                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RebuildFromFolder()
    {
        if (_reader == null) return;

        using var fbd = new FolderBrowserDialog
        {
            Description = "Select the folder that contains extracted and modified files",
        };
        if (fbd.ShowDialog(this) != DialogResult.OK) return;

        using var sfd = new SaveFileDialog
        {
            FileName = Path.GetFileNameWithoutExtension(_reader.FilePath) + "_modified.pk2",
            Filter = "PK2 files|*.pk2|All files|*.*",
            Title = "Save rebuilt PK2 as",
        };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;

        if (string.Equals(Path.GetFullPath(_reader.FilePath), Path.GetFullPath(sfd.FileName), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Choose a new output file. Rebuilding directly over the source PK2 is not allowed.",
                "Unsafe output path", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            var replacements = PK2Writer.BuildReplacementMap(_reader.AllFiles, fbd.SelectedPath);
            if (replacements.Count == 0)
            {
                MessageBox.Show(this, "No matching replacement files were found in the selected folder.",
                    "No matches", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            RebuildArchiveWithReplacements(replacements, sfd.FileName);
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "PK2 rebuild cancelled.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"PK2 rebuild failed: {ex.Message}",
                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ReinsertSingleSelectedFile()
    {
        if (_reader == null) return;
        var selected = GetSelectedFiles();
        if (selected.Count != 1)
        {
            MessageBox.Show(this, "Select exactly one file to reinsert from a single replacement file.",
                "Select one file", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var target = selected[0];
        using var ofd = new OpenFileDialog
        {
            FileName = target.DisplayName.TrimEnd('\\', '/'),
            Filter = "All files|*.*",
            Title = $"Select replacement for {target.DisplayName}",
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;

        using var sfd = CreateRebuildSaveDialog();
        if (sfd.ShowDialog(this) != DialogResult.OK) return;

        RebuildArchiveWithReplacements(
            new Dictionary<PK2File, string> { [target] = ofd.FileName },
            sfd.FileName);
    }

    private void ReinsertSelectedFromFolder()
    {
        if (_reader == null) return;
        var selected = GetSelectedFiles();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Select one or more files in the file list.",
                "Nothing selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var fbd = new FolderBrowserDialog
        {
            Description = "Select the folder that contains replacement files for the selected PK2 entries",
        };
        if (fbd.ShowDialog(this) != DialogResult.OK) return;

        var replacements = PK2Writer.BuildReplacementMap(selected, fbd.SelectedPath);
        if (replacements.Count == 0)
        {
            MessageBox.Show(this, "No matching replacement files were found in the selected folder.",
                "No matches", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var sfd = CreateRebuildSaveDialog();
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        RebuildArchiveWithReplacements(replacements, sfd.FileName);
    }

    private void ReinsertTreeFolderFromFolder()
    {
        if (_reader == null) return;
        if (_tree.SelectedNode?.Tag is not PK2Folder folder)
            return;

        var files = CollectRecursive(folder).ToList();
        if (files.Count == 0)
        {
            MessageBox.Show(this, "The selected folder does not contain files.",
                "Empty folder", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var fbd = new FolderBrowserDialog
        {
            Description = $"Select the folder that contains replacement files for {FolderDisplayName(folder)}",
        };
        if (fbd.ShowDialog(this) != DialogResult.OK) return;

        var replacements = PK2Writer.BuildReplacementMap(files, fbd.SelectedPath, folder.FullPath);
        if (replacements.Count == 0)
        {
            MessageBox.Show(this, "No matching replacement files were found for the selected PK2 folder.",
                "No matches", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var sfd = CreateRebuildSaveDialog();
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        RebuildArchiveWithReplacements(replacements, sfd.FileName);
    }

    private void RebuildArchiveWithReplacements(IReadOnlyDictionary<PK2File, string> replacements, string outputPath)
    {
        if (_reader == null) return;

        if (string.Equals(Path.GetFullPath(_reader.FilePath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Choose a new output file. Rebuilding directly over the source PK2 is not allowed.",
                "Unsafe output path", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            using var progress = new ProgressForm("Reinserting files...", replacements.Count);
            var reporter = new DelegateProgress<PK2RepackProgress>(p =>
            {
                progress.SetProgress(p.Completed, p.Message);
                if (p.Completed % 32 == 0)
                    Application.DoEvents();
            });
            progress.Show(this);
            var result = PK2Writer.RebuildWithReplacements(
                _reader,
                replacements,
                outputPath,
                reporter,
                () => progress.IsCancelled);
            progress.Close();

            _statusLabel.Text = $"Reinserted {result.ReplacedFiles} file(s) into rebuilt PK2";
            MessageBox.Show(this,
                $"PK2 rebuild complete.\n\nReinserted files: {result.ReplacedFiles}\nTotal files: {result.TotalFiles}\nOutput size: {result.OutputSize:N0} bytes",
                "Reinsertion complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "PK2 rebuild cancelled.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"PK2 rebuild failed: {ex.Message}",
                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private List<PK2File> GetSelectedFiles()
    {
        return _list.SelectedItems
            .Cast<ListViewItem>()
            .Select(i => i.Tag)
            .OfType<PK2File>()
            .ToList();
    }

    private SaveFileDialog CreateRebuildSaveDialog()
    {
        string baseName = _reader == null
            ? "GameData_modified.pk2"
            : Path.GetFileNameWithoutExtension(_reader.FilePath) + "_modified.pk2";
        return new SaveFileDialog
        {
            FileName = baseName,
            Filter = "PK2 files|*.pk2|All files|*.*",
            Title = "Save rebuilt PK2 as",
        };
    }

    private static string FolderDisplayName(PK2Folder folder)
    {
        return string.IsNullOrEmpty(folder.FullPath)
            ? "archive root"
            : folder.FullPath.TrimEnd('\\', '/');
    }

    // ---- misc actions ----------------------------------------------------

    private void CopySelectedPath()
    {
        if (_list.SelectedItems.Count == 0) return;
        Clipboard.SetText(_list.SelectedItems[0].Tag switch
        {
            PK2File file => file.FullPath,
            PK2Folder folder => folder.FullPath,
            _ => string.Empty,
        });
    }

    private void CopySelectedHash()
    {
        if (_list.SelectedItems.Count == 0) return;
        if (_list.SelectedItems[0].Tag is not PK2File f) return;
        Clipboard.SetText($"{f.NameHash:X8}");
    }

    private static string SanitizeFolderPath(string folderPath)
    {
        var parts = folderPath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Path.DirectorySeparatorChar, parts.Select(SanitizeFileName));
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.TrimEnd('\\', '/');
    }

    private sealed class DelegateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
