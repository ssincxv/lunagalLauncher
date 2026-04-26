using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using lunagalLauncher.Utils;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Serilog;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;

namespace lunagalLauncher.Views;

/// <summary>侧栏快捷位置（桌面、文档等）。</summary>
public sealed class ModernPickerPlaceItem
{
    public string Label { get; init; } = "";
    public string Glyph { get; init; } = "\uE8B7";
    public string TargetPath { get; init; } = "";
    public bool IsHeader { get; init; }
}

/// <summary>文件/文件夹/驱动器列表项。</summary>
public sealed class ModernPickerFileItem
{
    public string FullPath { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool IsDirectory { get; init; }
    public bool IsDrive { get; init; }
    public string Glyph { get; init; } = "\uE8B5";
    public string Subtitle { get; init; } = "";
    public string DetailRight { get; init; } = "";
    public string ModifiedText { get; init; } = "";
    public string TypeText { get; init; } = "";
    public string SizeText { get; init; } = "";
}

/// <summary>
/// 应用内 WinUI 文件选择窗口（可执行文件场景），不调用 Shell <c>IContextMenu</c>。
/// 纯代码构造 UI，避免独立 Window 的 XAML 编译问题。
/// </summary>
public sealed class ModernFilePickerWindow : Window
{
    internal const string ThisPcToken = "__THIS_PC__";

    private readonly TaskCompletionSource<OpenFilePickerResult> _completion;
    private bool _resultPosted;

    private string _currentPath = "";
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();

    private bool _showAllFiles;
    private bool _detailsView = true;

    private readonly ObservableCollection<ModernPickerFileItem> _items = new();
    private readonly MenuFlyout _itemContextMenu = new();
    private ModernPickerFileItem? _contextTarget;

    private readonly Grid _rootGrid;
    private TextBox _addressTextBox = null!;
    private StackPanel _breadcrumbPanel = null!;
    private TextBox _searchBox = null!;
    private TextBox _fileNameBox = null!;
    private ComboBox _filterCombo = null!;
    private InfoBar _statusInfoBar = null!;
    private GridView _fileGridView = null!;
    private ListView _fileListView = null!;
    private TextBlock _emptyFolderText = null!;
    private Button _backButton = null!;
    private Button _forwardButton = null!;
    private Button _upButton = null!;
    private Button _newFolderButton = null!;
    private FontIcon _viewToggleIcon = null!;
    private ListView _placesList = null!;

    public ModernFilePickerWindow(
        string title,
        string? initialDirectory,
        TaskCompletionSource<OpenFilePickerResult> completion)
    {
        _completion = completion;
        Title = title;

        try
        {
            if (AppWindow.Presenter is OverlappedPresenter op)
            {
                op.IsResizable = true;
                op.IsMaximizable = true;
                op.IsMinimizable = true;
                op.PreferredMinimumWidth = 680;
                op.PreferredMinimumHeight = 440;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "ModernFilePicker: 设置 Presenter 失败");
        }

        try
        {
            SystemBackdrop = null;
        }
        catch { }

        _rootGrid = new Grid
        {
            Background = SafeGetThemeBrush("ApplicationPageBackgroundThemeBrush",
                new SolidColorBrush(Microsoft.UI.Colors.White)),
        };
        _rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var toolbar = BuildToolbar();
        Grid.SetRow(toolbar, 0);
        _rootGrid.Children.Add(toolbar);

        var main = BuildMainArea();
        Grid.SetRow(main, 1);
        _rootGrid.Children.Add(main);

        var bottom = BuildBottomArea();
        Grid.SetRow(bottom, 2);
        _rootGrid.Children.Add(bottom);

        Content = _rootGrid;

        _filterCombo.SelectedIndex = 0;
        _showAllFiles = false;

        BuildPlacesList();
        BuildContextMenu();

        Closed += OnWindowClosed;

        _fileGridView.ItemsSource = _items;
        _fileListView.ItemsSource = _items;
        _fileGridView.ItemTemplate = CreateGridItemTemplate();
        _fileListView.ItemTemplate = CreateListItemTemplate();
        _fileGridView.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load(
            "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ItemsWrapGrid Orientation='Horizontal' HorizontalAlignment='Stretch'/></ItemsPanelTemplate>");
        _fileGridView.IsItemClickEnabled = true;
        _fileListView.IsItemClickEnabled = true;
        _fileGridView.SelectionMode = ListViewSelectionMode.Single;
        _fileListView.SelectionMode = ListViewSelectionMode.Single;
        _fileGridView.ItemClick += FileGridView_ItemClick;
        _fileListView.ItemClick += FileListView_ItemClick;
        _fileGridView.RightTapped += FileArea_RightTapped;
        _fileListView.RightTapped += FileArea_RightTapped;
        _fileGridView.SelectionChanged += (_, _) => SyncSelectionToFileNameBox();
        _fileListView.SelectionChanged += (_, _) => SyncSelectionToFileNameBox();

        _currentPath = ResolveInitialPath(initialDirectory);
        UpdateLocationChrome();
        SyncHistoryNavUi();
        LoadItems();
    }

    private static Brush SafeGetThemeBrush(string key, Brush fallback)
    {
        try
        {
            if (Application.Current?.Resources.TryGetValue(key, out var o) == true && o is Brush b)
                return b;
        }
        catch { }

        return fallback;
    }

    private Border BuildToolbar()
    {
        var bar = new Border
        {
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 243, 243, 243)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = SafeGetThemeBrush("DividerStrokeColorDefaultBrush",
                new SolidColorBrush(Microsoft.UI.Colors.Gray)),
        };

        var chrome = new Grid();
        chrome.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
        chrome.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
        chrome.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });

        var tabRow = new Grid { Padding = new Thickness(12, 6, 12, 0) };
        tabRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tabRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tabRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var activeTab = new Border
        {
            Width = 192,
            Height = 32,
            Padding = new Thickness(12, 0, 10, 0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            CornerRadius = new CornerRadius(7, 7, 0, 0),
        };
        activeTab.Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new FontIcon { Glyph = "\uE8A5", FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock
                {
                    Text = "选择应用程序",
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };
        tabRow.Children.Add(activeTab);

        var addTab = MakeChromeGlyphButton("\uE710", "新建标签页");
        addTab.Width = 34;
        addTab.Height = 30;
        Grid.SetColumn(addTab, 1);
        tabRow.Children.Add(addTab);
        Grid.SetRow(tabRow, 0);
        chrome.Children.Add(tabRow);

        var commandRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Padding = new Thickness(10, 6, 10, 5),
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
        };

        _backButton = MakeToolbarButton("BackButton", "\uE72B", "后退", BackButton_Click);
        _forwardButton = MakeToolbarButton("ForwardButton", "\uE72A", "前进", ForwardButton_Click);
        _upButton = MakeToolbarButton("UpButton", "\uE74A", "向上一级", UpButton_Click);
        _newFolderButton = MakeToolbarButton("NewFolderButton", "\uE8F4", "新建文件夹", NewFolderButton_Click);
        commandRow.Children.Add(MakeCommandButton(_newFolderButton, "新建"));
        commandRow.Children.Add(MakeCommandDivider());
        commandRow.Children.Add(MakeExplorerCommandButton("\uE8C6", "剪切", null, isEnabled: false));
        commandRow.Children.Add(MakeExplorerCommandButton("\uE8C8", "复制", null, isEnabled: false));
        commandRow.Children.Add(MakeExplorerCommandButton("\uE77F", "粘贴", null, isEnabled: false));
        commandRow.Children.Add(MakeCommandDivider());
        commandRow.Children.Add(MakeExplorerCommandButton("\uE8CB", "排序", null, isEnabled: false));
        commandRow.Children.Add(MakeExplorerCommandButton("\uE8A1", "查看", ViewToggleButton_Click));
        commandRow.Children.Add(MakeExplorerCommandButton("\uE712", "更多", null, isEnabled: false));
        Grid.SetRow(commandRow, 1);
        chrome.Children.Add(commandRow);

        var navRow = new Grid
        {
            ColumnSpacing = 8,
            Padding = new Thickness(12, 6, 12, 7),
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
        };
        navRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        navRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        navRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });

        var navButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        navButtons.Children.Add(_backButton);
        navButtons.Children.Add(_forwardButton);
        navButtons.Children.Add(_upButton);
        navButtons.Children.Add(MakeToolbarButton("RefreshButton", "\uE72C", "刷新", (_, _) => LoadItems()));
        Grid.SetColumn(navButtons, 0);
        navRow.Children.Add(navButtons);

        var addressHost = new Border
        {
            MinHeight = 32,
            Padding = new Thickness(8, 0, 8, 0),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 218, 218, 218)),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 252, 252, 252)),
        };
        var addressGrid = new Grid();
        _breadcrumbPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _addressTextBox = new TextBox
        {
            Name = "AddressTextBox",
            MinHeight = 30,
            Padding = new Thickness(8, 4, 8, 4),
            FontSize = 13,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            PlaceholderText = "路径或粘贴文件夹地址后按 Enter",
            Visibility = Visibility.Collapsed,
        };
        _addressTextBox.KeyDown += AddressTextBox_KeyDown;
        _addressTextBox.LostFocus += (_, _) =>
        {
            _addressTextBox.Visibility = Visibility.Collapsed;
            _breadcrumbPanel.Visibility = Visibility.Visible;
        };
        addressHost.Tapped += (_, _) =>
        {
            _breadcrumbPanel.Visibility = Visibility.Collapsed;
            _addressTextBox.Visibility = Visibility.Visible;
            _addressTextBox.Text = _currentPath == ThisPcToken ? "" : _currentPath;
            _addressTextBox.Focus(FocusState.Programmatic);
            _addressTextBox.SelectAll();
        };
        addressGrid.Children.Add(_breadcrumbPanel);
        addressGrid.Children.Add(_addressTextBox);
        addressHost.Child = addressGrid;
        Grid.SetColumn(addressHost, 1);
        navRow.Children.Add(addressHost);

        _searchBox = new TextBox
        {
            MinHeight = 32,
            FontSize = 13,
            Padding = new Thickness(10, 5, 10, 5),
            PlaceholderText = "在当前文件夹中搜索",
            IsReadOnly = true,
        };
        Grid.SetColumn(_searchBox, 2);
        navRow.Children.Add(_searchBox);

        Grid.SetRow(navRow, 2);
        chrome.Children.Add(navRow);

        bar.Child = chrome;
        return bar;
    }

    private static Border MakeCommandDivider()
    {
        return new Border
        {
            Width = 1,
            Height = 22,
            Margin = new Thickness(5, 5, 5, 5),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 226, 226, 226)),
        };
    }

    private static Button MakeChromeGlyphButton(string glyph, string tip)
    {
        var b = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Content = new FontIcon { FontSize = 14, Glyph = glyph },
        };
        ToolTipService.SetToolTip(b, tip);
        return b;
    }

    private Button MakeCommandButton(Button existingButton, string label)
    {
        existingButton.Width = double.NaN;
        existingButton.MinWidth = 78;
        existingButton.Height = 34;
        existingButton.Padding = new Thickness(10, 0, 12, 0);
        existingButton.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new FontIcon { FontSize = 15, Glyph = "\uE8F4" },
                new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        return existingButton;
    }

    private Button MakeExplorerCommandButton(string glyph, string label, RoutedEventHandler? onClick, bool isEnabled = true)
    {
        var icon = new FontIcon { FontSize = 15, Glyph = glyph };
        if (label == "查看")
            _viewToggleIcon = icon;

        var b = new Button
        {
            MinWidth = 72,
            Height = 34,
            Padding = new Thickness(10, 0, 10, 0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            IsEnabled = isEnabled,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    icon,
                    new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        if (onClick != null)
            b.Click += onClick;
        return b;
    }

    private static Button MakeToolbarButton(string name, string glyph, string tip, RoutedEventHandler onClick)
    {
        var b = new Button
        {
            Name = name,
            Width = 34,
            Height = 32,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Content = new FontIcon { FontSize = 14, Glyph = glyph },
        };
        ToolTipService.SetToolTip(b, tip);
        b.Click += onClick;
        return b;
    }

    private Grid BuildMainArea()
    {
        var grid = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.White) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(182) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var navBorder = new Border
        {
            Padding = new Thickness(0, 10, 0, 8),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 248, 248, 248)),
        };
        _placesList = new ListView
        {
            Name = "PlacesList",
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            Padding = new Thickness(6, 0, 6, 0),
        };
        _placesList.ItemContainerStyle = (Style)XamlReader.Load(
            @"<Style TargetType='ListViewItem' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
  <Setter Property='MinHeight' Value='32'/>
  <Setter Property='Padding' Value='8,4'/>
  <Setter Property='Margin' Value='0,1'/>
  <Setter Property='CornerRadius' Value='4'/>
</Style>");
        _placesList.ItemClick += PlacesList_ItemClick;
        _placesList.ItemTemplate = (DataTemplate)XamlReader.Load(
            @"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Grid MinHeight='28'>
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width='24'/>
      <ColumnDefinition Width='*'/>
    </Grid.ColumnDefinitions>
    <FontIcon Grid.Column='0' VerticalAlignment='Center' FontSize='15' Glyph='{Binding Glyph}'/>
    <TextBlock Grid.Column='1' VerticalAlignment='Center' FontSize='13' Text='{Binding Label}' TextTrimming='CharacterEllipsis'/>
  </Grid>
</DataTemplate>");
        navBorder.Child = _placesList;
        Grid.SetColumn(navBorder, 0);
        grid.Children.Add(navBorder);

        var navSeparator = new Border
        {
            Width = 1,
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 234, 234, 234)),
        };
        Grid.SetColumn(navSeparator, 1);
        grid.Children.Add(navSeparator);

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = BuildDetailsHeader();
        Grid.SetRow(header, 0);
        right.Children.Add(header);

        var fileHost = new Grid
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
        };
        _fileGridView = new GridView
        {
            Name = "FileGridView",
            Padding = new Thickness(16, 12, 16, 12),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
        };
        _fileListView = new ListView
        {
            Name = "FileListView",
            Padding = new Thickness(8, 0, 8, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Visible,
        };
        _fileListView.ItemContainerStyle = (Style)XamlReader.Load(
            @"<Style TargetType='ListViewItem' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
  <Setter Property='MinHeight' Value='34'/>
  <Setter Property='Padding' Value='8,2'/>
  <Setter Property='Margin' Value='0'/>
  <Setter Property='CornerRadius' Value='3'/>
  <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
</Style>");
        _emptyFolderText = new TextBlock
        {
            Text = "此文件夹为空。",
            FontSize = 13,
            Foreground = SafeGetThemeBrush("TextFillColorSecondaryBrush",
                new SolidColorBrush(Microsoft.UI.Colors.Gray)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        fileHost.Children.Add(_fileGridView);
        fileHost.Children.Add(_fileListView);
        fileHost.Children.Add(_emptyFolderText);
        Grid.SetRow(fileHost, 1);
        right.Children.Add(fileHost);

        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        return grid;
    }

    private static Border BuildDetailsHeader()
    {
        var outer = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 235, 235, 235)),
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
        };
        var header = new Grid
        {
            Padding = new Thickness(16, 0, 16, 0),
            ColumnSpacing = 12,
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });

        AddHeaderText(header, "名称", 0);
        AddHeaderText(header, "修改日期", 1);
        AddHeaderText(header, "类型", 2);
        AddHeaderText(header, "大小", 3, TextAlignment.Right);
        outer.Child = header;
        return outer;
    }

    private static void AddHeaderText(Grid grid, string text, int column, TextAlignment align = TextAlignment.Left)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 88, 88, 88)),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = align,
        };
        Grid.SetColumn(tb, column);
        grid.Children.Add(tb);
    }

    private static DataTemplate CreateGridItemTemplate()
    {
        return (DataTemplate)XamlReader.Load(
            @"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
  <Grid Width='104' Padding='4' Background='Transparent'>
    <Grid.RowDefinitions>
      <RowDefinition Height='Auto'/><RowDefinition Height='8'/><RowDefinition Height='Auto'/>
    </Grid.RowDefinitions>
    <FontIcon Grid.Row='0' HorizontalAlignment='Center' FontSize='40' Glyph='{Binding Glyph}'/>
    <TextBlock Grid.Row='2' HorizontalAlignment='Center' MaxLines='2' FontSize='12' Text='{Binding DisplayName}'
      TextAlignment='Center' TextTrimming='CharacterEllipsis' TextWrapping='WrapWholeWords'/>
  </Grid>
</DataTemplate>");
    }

    private static DataTemplate CreateListItemTemplate()
    {
        return (DataTemplate)XamlReader.Load(
            @"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
  <Grid ColumnSpacing='12' MinHeight='32'>
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/><ColumnDefinition Width='160'/><ColumnDefinition Width='112'/><ColumnDefinition Width='96'/>
    </Grid.ColumnDefinitions>
    <FontIcon Grid.Column='0' VerticalAlignment='Center' FontSize='17' Width='24' Glyph='{Binding Glyph}'/>
    <TextBlock Grid.Column='1' VerticalAlignment='Center' FontSize='13' Text='{Binding DisplayName}' TextTrimming='CharacterEllipsis'/>
    <TextBlock Grid.Column='2' VerticalAlignment='Center' FontSize='12' Foreground='{ThemeResource TextFillColorSecondaryBrush}' Text='{Binding ModifiedText}' TextTrimming='CharacterEllipsis'/>
    <TextBlock Grid.Column='3' VerticalAlignment='Center' FontSize='12' Foreground='{ThemeResource TextFillColorSecondaryBrush}' Text='{Binding TypeText}' TextTrimming='CharacterEllipsis'/>
    <TextBlock Grid.Column='4' VerticalAlignment='Center' FontSize='12' Foreground='{ThemeResource TextFillColorSecondaryBrush}' Text='{Binding SizeText}' TextAlignment='Right' TextTrimming='CharacterEllipsis'/>
  </Grid>
</DataTemplate>");
    }

    private Border BuildBottomArea()
    {
        var border = new Border
        {
            Padding = new Thickness(14, 8, 14, 10),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 248, 248, 248)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = SafeGetThemeBrush("DividerStrokeColorDefaultBrush",
                new SolidColorBrush(Microsoft.UI.Colors.Gray)),
        };

        var grid = new Grid
        {
            ColumnSpacing = 10,
            RowSpacing = 6,
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var fileNameLabel = new TextBlock
        {
            Text = "文件名",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetRow(fileNameLabel, 0);
        Grid.SetColumn(fileNameLabel, 0);
        grid.Children.Add(fileNameLabel);

        _fileNameBox = new TextBox
        {
            Name = "FileNameBox",
            MinHeight = 32,
            Margin = new Thickness(58, 0, 0, 0),
            Padding = new Thickness(8, 4, 8, 4),
            FontSize = 13,
            PlaceholderText = "选择文件或输入名称",
        };
        Grid.SetRow(_fileNameBox, 0);
        Grid.SetColumn(_fileNameBox, 0);
        grid.Children.Add(_fileNameBox);

        var fileTypeLabel = new TextBlock
        {
            Text = "文件类型",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetRow(fileTypeLabel, 0);
        Grid.SetColumn(fileTypeLabel, 1);
        grid.Children.Add(fileTypeLabel);

        _filterCombo = new ComboBox
        {
            Name = "FilterCombo",
            MinHeight = 32,
            Margin = new Thickness(64, 0, 0, 0),
            FontSize = 13,
        };
        _filterCombo.Items.Add(new ComboBoxItem { Content = "可执行文件 (*.exe;*.bat;*.cmd)", Tag = "exe" });
        _filterCombo.Items.Add(new ComboBoxItem { Content = "所有文件 (*.*)", Tag = "all" });
        _filterCombo.SelectionChanged += FilterCombo_SelectionChanged;
        Grid.SetRow(_filterCombo, 0);
        Grid.SetColumn(_filterCombo, 1);
        grid.Children.Add(_filterCombo);

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        var cancel = new Button { Content = "取消", MinWidth = 96, MinHeight = 36 };
        cancel.Click += CancelButton_Click;
        var open = new Button
        {
            Content = "打开",
            MinWidth = 96,
            MinHeight = 36,
        };
        if (Application.Current?.Resources.TryGetValue("AccentButtonStyle", out var acObj) == true && acObj is Style ac)
            open.Style = ac;
        open.Click += OpenButton_Click;
        btnRow.Children.Add(cancel);
        btnRow.Children.Add(open);
        Grid.SetRow(btnRow, 0);
        Grid.SetColumn(btnRow, 2);
        grid.Children.Add(btnRow);

        _statusInfoBar = new InfoBar
        {
            Name = "StatusInfoBar",
            IsOpen = false,
            IsClosable = true,
            Severity = InfoBarSeverity.Informational,
        };
        Grid.SetRow(_statusInfoBar, 1);
        Grid.SetColumnSpan(_statusInfoBar, 3);
        grid.Children.Add(_statusInfoBar);

        border.Child = grid;
        return border;
    }

    /// <summary>在主 UI 线程上显示选择器并等待结果（通过队列完成 TCS，避免 UI 死锁）。</summary>
    public static Task<OpenFilePickerResult> PresentAsync(string title, string? initialDirectory)
    {
        if (Application.Current is not App app || app.window is null)
            return Task.FromResult(new OpenFilePickerResult(null, OpenFilePickerCompletion.Unavailable, null));

        var tcs = new TaskCompletionSource<OpenFilePickerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dq = app.window.DispatcherQueue;

        void Open()
        {
            try
            {
                var w = new ModernFilePickerWindow(title, initialDirectory, tcs);
                PositionNearOwner(w, app.window);
                w.Activate();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ModernFilePicker: 无法创建窗口");
                tcs.TrySetResult(new OpenFilePickerResult(null, OpenFilePickerCompletion.Unavailable, null));
            }
        }

        if (dq.HasThreadAccess)
        {
            Open();
            return tcs.Task;
        }

        if (!dq.TryEnqueue(Open))
            tcs.TrySetResult(new OpenFilePickerResult(null, OpenFilePickerCompletion.Unavailable, null));

        return tcs.Task;
    }

    private static void PositionNearOwner(Window picker, Window owner)
    {
        try
        {
            var pw = picker.AppWindow;
            var ow = owner.AppWindow;
            const int w = 960;
            const int h = 640;
            pw.Resize(new SizeInt32(w, h));
            var pos = ow.Position;
            var size = ow.Size;
            int x = pos.X + Math.Max(0, (size.Width - w) / 2);
            int y = pos.Y + Math.Max(0, (size.Height - h) / 2);
            pw.Move(new PointInt32(x, y));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "ModernFilePicker: 居中窗口失败");
        }
    }

    private static string ResolveInitialPath(string? initialDirectory)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(initialDirectory))
            {
                var p = initialDirectory.Trim();
                if (Directory.Exists(p))
                    return Path.GetFullPath(p);
            }
        }
        catch { }

        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop))
                return Path.GetFullPath(desktop);
        }
        catch { }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private void BuildPlacesList()
    {
        var places = new List<ModernPickerPlaceItem>
        {
            new()
            {
                Label = "主文件夹",
                Glyph = "\uE80F",
                TargetPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            },
            new()
            {
                Label = "桌面",
                Glyph = "\uE8A5",
                TargetPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            },
            new()
            {
                Label = "文档",
                Glyph = "\uE8A5",
                TargetPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            },
            new()
            {
                Label = "下载",
                Glyph = "\uE896",
                TargetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            },
            new()
            {
                Label = "图片",
                Glyph = "\uEB9F",
                TargetPath = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            },
            new()
            {
                Label = "音乐",
                Glyph = "\uE8D6",
                TargetPath = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            },
            new()
            {
                Label = "视频",
                Glyph = "\uE8B2",
                TargetPath = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            },
            new()
            {
                Label = "此电脑",
                Glyph = "\uEDA2",
                TargetPath = ThisPcToken,
            },
        };

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady && drive.DriveType != DriveType.Fixed)
                    continue;
                var name = drive.Name.TrimEnd('\\');
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? $"本地磁盘 ({name.TrimEnd(':')})"
                    : $"{drive.VolumeLabel} ({name.TrimEnd(':')})";
                places.Add(new ModernPickerPlaceItem
                {
                    Label = label,
                    Glyph = "\uEDA2",
                    TargetPath = drive.RootDirectory.FullName,
                });
            }
            catch
            {
                // 忽略不可访问驱动器
            }
        }

        _placesList.ItemsSource = places;
    }

    private void BuildContextMenu()
    {
        _itemContextMenu.Items.Add(new MenuFlyoutItem { Text = "复制路径" });
        _itemContextMenu.Items.Add(new MenuFlyoutItem { Text = "复制文件名" });
        _itemContextMenu.Items.Add(new MenuFlyoutItem { Text = "打开所在文件夹" });
        _itemContextMenu.Items.Add(new MenuFlyoutSeparator());
        _itemContextMenu.Items.Add(new MenuFlyoutItem { Text = "刷新列表" });

        if (_itemContextMenu.Items[0] is MenuFlyoutItem m0)
            m0.Click += (_, _) => CopyPathFromContext();
        if (_itemContextMenu.Items[1] is MenuFlyoutItem m1)
            m1.Click += (_, _) => CopyNameFromContext();
        if (_itemContextMenu.Items[2] is MenuFlyoutItem m2)
            m2.Click += (_, _) => OpenContainingFromContext();
        if (_itemContextMenu.Items[4] is MenuFlyoutItem m4)
            m4.Click += (_, _) => LoadItems();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_resultPosted)
            return;
        _resultPosted = true;
        _completion.TrySetResult(new OpenFilePickerResult(null, OpenFilePickerCompletion.Cancelled, null));
    }

    private void PostResult(OpenFilePickerResult result)
    {
        if (_resultPosted)
            return;
        _resultPosted = true;

        var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (dq == null)
        {
            _completion.TrySetResult(result);
            try
            {
                Close();
            }
            catch { }

            return;
        }

        dq.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _completion.TrySetResult(result);
            try
            {
                Close();
            }
            catch { }
        });
    }

    private void ShowStatus(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        _statusInfoBar.Message = message;
        _statusInfoBar.Severity = severity;
        _statusInfoBar.IsOpen = true;
    }

    private void PlacesList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ModernPickerPlaceItem p)
            return;
        if (p.IsHeader)
            return;

        if (p.TargetPath == ThisPcToken)
        {
            NavigateTo(ThisPcToken, recordHistory: true);
            return;
        }

        try
        {
            if (Directory.Exists(p.TargetPath))
                NavigateTo(Path.GetFullPath(p.TargetPath), recordHistory: true);
            else
                ShowStatus($"无法访问：{p.Label}", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "ModernFilePicker: 快捷位置失败");
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_back.Count == 0)
            return;
        _forward.Push(_currentPath);
        var prev = _back.Pop();
        NavigateTo(prev, recordHistory: false);
        SyncHistoryNavUi();
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_forward.Count == 0)
            return;
        _back.Push(_currentPath);
        var next = _forward.Pop();
        NavigateTo(next, recordHistory: false);
        SyncHistoryNavUi();
    }

    private void UpButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPath == ThisPcToken)
            return;

        try
        {
            var root = Path.GetPathRoot(_currentPath);
            if (string.Equals(_currentPath.TrimEnd('\\', '/'), root?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                NavigateTo(ThisPcToken, recordHistory: true);
                return;
            }

            var parent = Directory.GetParent(_currentPath)?.FullName;
            if (!string.IsNullOrEmpty(parent))
                NavigateTo(parent, recordHistory: true);
            else
                NavigateTo(ThisPcToken, recordHistory: true);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Warning);
        }
    }

    private void NewFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPath == ThisPcToken || !Directory.Exists(_currentPath))
        {
            ShowStatus("请先在普通文件夹内再新建文件夹。", InfoBarSeverity.Warning);
            return;
        }

        _ = NewFolderCoreAsync();
    }

    private async Task NewFolderCoreAsync()
    {
        var dlg = new ContentDialog
        {
            Title = "新建文件夹",
            PrimaryButtonText = "创建",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = _rootGrid.XamlRoot,
        };
        var box = new TextBox
        {
            PlaceholderText = "文件夹名称",
            MinHeight = 36,
        };
        dlg.Content = box;
        var r = await dlg.ShowAsync();
        if (r != ContentDialogResult.Primary)
            return;

        var name = box.Text?.Trim();
        if (string.IsNullOrEmpty(name))
            return;

        try
        {
            var path = Path.Combine(_currentPath, name);
            Directory.CreateDirectory(path);
            LoadItems();
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void ViewToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _detailsView = !_detailsView;
        _fileGridView.Visibility = _detailsView ? Visibility.Collapsed : Visibility.Visible;
        _fileListView.Visibility = _detailsView ? Visibility.Visible : Visibility.Collapsed;
        _viewToggleIcon.Glyph = _detailsView ? "\uE8FD" : "\uE8A1";
        LoadItems();
    }

    private void AddressTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        e.Handled = true;
        var raw = _addressTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(raw))
            return;

        try
        {
            if (Directory.Exists(raw))
            {
                NavigateTo(Path.GetFullPath(raw), recordHistory: true);
                _addressTextBox.Visibility = Visibility.Collapsed;
                _breadcrumbPanel.Visibility = Visibility.Visible;
                return;
            }

            ShowStatus("路径不存在或不是文件夹。", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void FilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filterCombo.SelectedItem is not ComboBoxItem item)
            return;
        var tag = item.Tag as string;
        _showAllFiles = string.Equals(tag, "all", StringComparison.OrdinalIgnoreCase);
        LoadItems();
    }

    private void FileGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ModernPickerFileItem item)
            return;
        HandleItemActivate(item);
    }

    private void FileListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ModernPickerFileItem item)
            return;
        HandleItemActivate(item);
    }

    private void FileArea_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var fe = e.OriginalSource as FrameworkElement;
        while (fe != null && fe.DataContext is not ModernPickerFileItem)
            fe = fe.Parent as FrameworkElement;
        if (fe?.DataContext is not ModernPickerFileItem item)
            return;
        _contextTarget = item;
        e.Handled = true;
        _itemContextMenu.ShowAt(fe, e.GetPosition(fe));
    }

    private void HandleItemActivate(ModernPickerFileItem item)
    {
        if (item.IsDirectory || item.IsDrive)
        {
            NavigateTo(item.FullPath, recordHistory: true);
            return;
        }

        if (IsSelectableFile(item.FullPath))
            PostResult(new OpenFilePickerResult(item.FullPath, OpenFilePickerCompletion.Success, null));
    }

    private void SyncSelectionToFileNameBox()
    {
        ModernPickerFileItem? sel = null;
        if (_fileGridView.Visibility == Visibility.Visible && _fileGridView.SelectedItem is ModernPickerFileItem g)
            sel = g;
        else if (_fileListView.Visibility == Visibility.Visible && _fileListView.SelectedItem is ModernPickerFileItem l)
            sel = l;

        if (sel != null && !sel.IsDirectory && !sel.IsDrive)
            _fileNameBox.Text = sel.DisplayName;
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_fileGridView.Visibility == Visibility.Visible && _fileGridView.SelectedItem is ModernPickerFileItem gs)
            {
                if (!gs.IsDirectory && !gs.IsDrive && IsSelectableFile(gs.FullPath))
                {
                    PostResult(new OpenFilePickerResult(gs.FullPath, OpenFilePickerCompletion.Success, null));
                    return;
                }
            }

            if (_fileListView.Visibility == Visibility.Visible && _fileListView.SelectedItem is ModernPickerFileItem ls)
            {
                if (!ls.IsDirectory && !ls.IsDrive && IsSelectableFile(ls.FullPath))
                {
                    PostResult(new OpenFilePickerResult(ls.FullPath, OpenFilePickerCompletion.Success, null));
                    return;
                }
            }

            var name = _fileNameBox.Text?.Trim();
            if (string.IsNullOrEmpty(name) || _currentPath == ThisPcToken)
            {
                ShowStatus("请选择可执行文件或输入有效文件名。", InfoBarSeverity.Warning);
                return;
            }

            var combined = Path.Combine(_currentPath, name);
            if (File.Exists(combined) && IsSelectableFile(combined))
            {
                PostResult(new OpenFilePickerResult(Path.GetFullPath(combined), OpenFilePickerCompletion.Success, null));
                return;
            }

            ShowStatus("找不到匹配的可执行文件。", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        PostResult(new OpenFilePickerResult(null, OpenFilePickerCompletion.Cancelled, null));

    private bool IsSelectableFile(string path)
    {
        if (_showAllFiles)
            return File.Exists(path);
        var ext = Path.GetExtension(path);
        return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase);
    }

    private void NavigateTo(string path, bool recordHistory)
    {
        try
        {
            if (recordHistory && _currentPath != path)
            {
                if (!string.IsNullOrEmpty(_currentPath))
                    _back.Push(_currentPath);
                _forward.Clear();
            }

            _currentPath = path;
            UpdateLocationChrome();
            SyncHistoryNavUi();
            LoadItems();
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void UpdateLocationChrome()
    {
        _addressTextBox.Text = _currentPath == ThisPcToken ? "" : _currentPath;
        _breadcrumbPanel.Children.Clear();

        if (_currentPath == ThisPcToken)
        {
            AddBreadcrumbPart("此电脑", ThisPcToken, isLast: true);
            _searchBox.PlaceholderText = "在 此电脑 中搜索";
            return;
        }

        AddBreadcrumbPart("此电脑", ThisPcToken, isLast: false);

        try
        {
            var root = Path.GetPathRoot(_currentPath);
            if (!string.IsNullOrEmpty(root))
            {
                AddBreadcrumbPart(root.TrimEnd('\\'), root, IsSamePath(root, _currentPath));
                var relative = Path.GetRelativePath(root, _currentPath);
                if (!string.Equals(relative, ".", StringComparison.Ordinal) && !relative.StartsWith("..", StringComparison.Ordinal))
                {
                    var current = root;
                    foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    {
                        if (string.IsNullOrWhiteSpace(part))
                            continue;
                        current = Path.Combine(current, part);
                        AddBreadcrumbPart(part, current, IsSamePath(current, _currentPath));
                    }
                }
            }
            else
            {
                AddBreadcrumbPart(_currentPath, _currentPath, isLast: true);
            }
        }
        catch
        {
            AddBreadcrumbPart(_currentPath, _currentPath, isLast: true);
        }

        _searchBox.PlaceholderText = $"在 {GetCurrentFolderDisplayName()} 中搜索";
    }

    private void AddBreadcrumbPart(string label, string target, bool isLast)
    {
        var btn = new Button
        {
            MinWidth = 0,
            Height = 28,
            Padding = new Thickness(8, 0, 8, 0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(3),
            Content = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(label) ? target : label,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        if (!isLast)
            btn.Click += (_, _) => NavigateTo(target, recordHistory: true);
        _breadcrumbPanel.Children.Add(btn);

        if (!isLast)
        {
            _breadcrumbPanel.Children.Add(new FontIcon
            {
                Glyph = "\uE974",
                FontSize = 10,
                Width = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 96, 96, 96)),
            });
        }
    }

    private string GetCurrentFolderDisplayName()
    {
        if (_currentPath == ThisPcToken)
            return "此电脑";

        try
        {
            var name = Path.GetFileName(_currentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(name))
                return name;
            return _currentPath;
        }
        catch
        {
            return "当前文件夹";
        }
    }

    private static bool IsSamePath(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\', '/'),
                Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
    }

    private void SyncHistoryNavUi()
    {
        _backButton.IsEnabled = _back.Count > 0;
        _forwardButton.IsEnabled = _forward.Count > 0;
        _upButton.IsEnabled = _currentPath != ThisPcToken;
        _newFolderButton.IsEnabled = _currentPath != ThisPcToken && Directory.Exists(_currentPath);
    }

    private void LoadItems()
    {
        _items.Clear();
        _statusInfoBar.IsOpen = false;
        _emptyFolderText.Visibility = Visibility.Collapsed;

        try
        {
            if (_currentPath == ThisPcToken)
            {
                var drives = new List<ModernPickerFileItem>();
                foreach (var di in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (!di.IsReady && di.DriveType != DriveType.Fixed)
                            continue;
                        var name = di.Name.TrimEnd('\\');
                        var label = string.IsNullOrWhiteSpace(di.VolumeLabel)
                            ? $"本地磁盘 ({name.TrimEnd(':')})"
                            : $"{di.VolumeLabel} ({name.TrimEnd(':')})";
                        drives.Add(new ModernPickerFileItem
                        {
                            FullPath = di.RootDirectory.FullName,
                            DisplayName = label,
                            IsDirectory = true,
                            IsDrive = true,
                            Glyph = "\uEDA2",
                            Subtitle = di.DriveType.ToString(),
                            DetailRight = di.IsReady ? FormatBytes(di.AvailableFreeSpace) + " 可用" : "",
                            ModifiedText = "",
                            TypeText = "驱动器",
                            SizeText = di.IsReady ? FormatBytes(di.AvailableFreeSpace) + " 可用" : "",
                        });
                    }
                    catch
                    {
                        // 单个驱动器忽略
                    }
                }

                foreach (var x in drives.OrderBy(x => x.DisplayName))
                    _items.Add(x);
                UpdateEmptyFolderChrome();
                return;
            }

            if (!Directory.Exists(_currentPath))
            {
                ShowStatus("文件夹不存在。", InfoBarSeverity.Warning);
                UpdateEmptyFolderChrome();
                return;
            }

            var enumOpts = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System,
                RecurseSubdirectories = false,
            };

            var dirs = new List<string>();
            foreach (var d in Directory.EnumerateDirectories(_currentPath, "*", enumOpts))
                dirs.Add(d);

            dirs.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var d in dirs)
                _items.Add(CreateItemFromDirectory(d));

            var files = new List<string>();
            foreach (var f in Directory.EnumerateFiles(_currentPath, "*", enumOpts))
            {
                if (_showAllFiles || IsSelectableFile(f))
                    files.Add(f);
            }

            files.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
                _items.Add(CreateItemFromFile(f));
            UpdateEmptyFolderChrome();
        }
        catch (UnauthorizedAccessException)
        {
            ShowStatus("没有权限浏览此文件夹。", InfoBarSeverity.Warning);
            UpdateEmptyFolderChrome();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "ModernFilePicker: 枚举失败");
            ShowStatus(ex.Message, InfoBarSeverity.Error);
            UpdateEmptyFolderChrome();
        }
    }

    private void UpdateEmptyFolderChrome()
    {
        _emptyFolderText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private ModernPickerFileItem CreateItemFromDirectory(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name))
            name = path;
        DateTime? write = null;
        try
        {
            write = Directory.GetLastWriteTime(path);
        }
        catch { }

        return new ModernPickerFileItem
        {
            FullPath = path,
            DisplayName = name,
            IsDirectory = true,
            Glyph = "\uE8B7",
            Subtitle = "文件夹",
            DetailRight = write?.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? "",
            ModifiedText = write?.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? "",
            TypeText = "文件夹",
            SizeText = "",
        };
    }

    private ModernPickerFileItem CreateItemFromFile(string path)
    {
        var name = Path.GetFileName(path);
        DateTime? write = null;
        long len = 0;
        try
        {
            var fi = new FileInfo(path);
            write = fi.LastWriteTime;
            len = fi.Length;
        }
        catch { }

        return new ModernPickerFileItem
        {
            FullPath = path,
            DisplayName = name,
            IsDirectory = false,
            Glyph = "\uE8A5",
            Subtitle = Path.GetExtension(path).TrimStart('.').ToUpperInvariant() + " 文件",
            DetailRight = $"{FormatBytes(len)} · {write?.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? ""}",
            ModifiedText = write?.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? "",
            TypeText = Path.GetExtension(path).TrimStart('.').ToUpperInvariant() + " 文件",
            SizeText = FormatBytes(len),
        };
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        double d = bytes;
        string[] u = { "KB", "MB", "GB", "TB" };
        int i = 0;
        while (d >= 1024 && i < u.Length - 1)
        {
            d /= 1024;
            i++;
        }

        return $"{d:0.#} {u[i]}";
    }

    private void CopyPathFromContext()
    {
        var t = _contextTarget;
        if (t == null)
            return;
        try
        {
            var dp = new DataPackage();
            dp.SetText(t.FullPath);
            Clipboard.SetContent(dp);
            ShowStatus("已复制路径", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void CopyNameFromContext()
    {
        var t = _contextTarget;
        if (t == null)
            return;
        try
        {
            var dp = new DataPackage();
            dp.SetText(t.DisplayName);
            Clipboard.SetContent(dp);
            ShowStatus("已复制文件名", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OpenContainingFromContext()
    {
        var t = _contextTarget;
        if (t == null)
            return;
        try
        {
            if (t.IsDrive)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = t.FullPath,
                    UseShellExecute = true,
                });
                return;
            }

            if (t.IsDirectory)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{t.FullPath}\"",
                    UseShellExecute = true,
                });
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{t.FullPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }
}
