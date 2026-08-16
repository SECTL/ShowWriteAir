using ShowWrite.Models;
using ShowWrite.Services;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace ShowWrite
{
    public partial class SettingsWindow : Window
    {
        private readonly AppConfig _config;
        private readonly List<string> _cameras;
        private readonly LanguageManager _languageManager;

        public SettingsWindow(AppConfig config, List<string> cameras)
        {
            InitializeComponent();
            _config = config ?? new AppConfig();
            _cameras = cameras ?? new List<string>();
            _languageManager = LanguageManager.Instance;
            Loaded += OnLoaded;
            _languageManager.LanguageChanged += UpdateLanguage;
        }

        private void UpdateLanguage()
        {
            // 更新窗口标题
            Title = _languageManager.GetTranslation("Settings");

            // 更新菜单项
            GeneralMenuText.Text = _languageManager.GetTranslation("GeneralSettings");
            AdvancedMenuText.Text = _languageManager.GetTranslation("AdvancedSettings");
            StartupMenuText.Text = _languageManager.GetTranslation("StartupSettings");
            AboutMenuText.Text = _languageManager.GetTranslation("About");

            // 更新语言下拉框选项
            UpdateLanguageComboBox();

            // 更新文件关联状态文本
            UpdateFileAssociationStatus();
        }

        private void UpdateLanguageComboBox()
        {
            foreach (ComboBoxItem item in LanguageComboBox.Items)
            {
                if (item.Tag?.ToString() == "0")
                    item.Content = "简体中文";
                else if (item.Tag?.ToString() == "1")
                    item.Content = "繁體中文";
                else if (item.Tag?.ToString() == "2")
                    item.Content = "文言文";
                else if (item.Tag?.ToString() == "3")
                    item.Content = "English";
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 设置版本信息
            VersionText.Text = Assembly.GetExecutingAssembly().GetName().Version.ToString();

            // 初始化摄像头列表
            CameraComboBox.ItemsSource = _cameras;

            // 从配置文件加载当前设置
            LoadConfig();

            // 更新文件关联状态显示
            UpdateFileAssociationStatus();
        }

        private void MenuItem_Selected(object sender, RoutedEventArgs e)
        {
            if (sender is ListBoxItem item)
            {
                string tag = item.Tag?.ToString() ?? "";

                // 清除"关于"按钮的选中状态
                AboutMenuItem.Tag = null;

                // 根据选择显示对应的面板（添加空值检查）
                if (GeneralPanel != null)
                    GeneralPanel.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
                if (AdvancedPanel != null)
                    AdvancedPanel.Visibility = tag == "Advanced" ? Visibility.Visible : Visibility.Collapsed;
                if (RUN != null)
                    RUN.Visibility = tag == "Startup" ? Visibility.Visible : Visibility.Collapsed;
                if (AboutPanel != null)
                    AboutPanel.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void AboutMenuItem_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // 清除 ListBox 的选中状态
            MenuList.UnselectAll();

            // 设置"关于"按钮为选中状态
            AboutMenuItem.Tag = "Selected";

            if (GeneralPanel != null)
                GeneralPanel.Visibility = Visibility.Collapsed;
            if (AdvancedPanel != null)
                AdvancedPanel.Visibility = Visibility.Collapsed;
            if (RUN != null)
                RUN.Visibility = Visibility.Collapsed;
            if (AboutPanel != null)
                AboutPanel.Visibility = Visibility.Visible;
        }

        private void LoadConfig()
        {
            // 语言设置
            foreach (ComboBoxItem item in LanguageComboBox.Items)
            {
                if (item.Tag?.ToString() == ((int)_languageManager.CurrentLanguage).ToString())
                {
                    item.IsSelected = true;
                    break;
                }
            }

            // 界面主题设置
            foreach (ComboBoxItem item in ThemeComboBox.Items)
            {
                if (item.Tag?.ToString() == _config.Theme)
                {
                    item.IsSelected = true;
                    break;
                }
            }

            // 启动设置
            StartMaximizedCheckBox.IsChecked = _config.StartMaximized;
            AutoStartCameraCheckBox.IsChecked = _config.AutoStartCamera;

            // 设置选中的摄像头
            if (_config.CameraIndex >= 0 && _config.CameraIndex < _cameras.Count)
            {
                CameraComboBox.SelectedIndex = _config.CameraIndex;
            }

            // 默认工具设置
            PenWidthSlider.Value = _config.DefaultPenWidth;
            CanvasModeCheckBox.IsChecked = _config.EnableCanvasMode;

            // 设置画笔颜色
            foreach (ComboBoxItem item in PenColorComboBox.Items)
            {
                if (item.Tag?.ToString() == _config.DefaultPenColor)
                {
                    item.IsSelected = true;
                    break;
                }
            }

            // 高级设置
            EnableHardwareAccel.IsChecked = _config.EnableHardwareAcceleration;
            EnableFrameProcessing.IsChecked = _config.EnableFrameProcessing;

            // 帧率限制
            if (_config.FrameRateLimit >= 0 && _config.FrameRateLimit < FrameRateComboBox.Items.Count)
            {
                FrameRateComboBox.SelectedIndex = _config.FrameRateLimit;
            }

            // 开发者模式设置
            DeveloperModeCheckBox.IsChecked = _config.DeveloperMode;

            // 启动图设置
            StartupImageUrlTextBox.Text = _config.StartupImageUrl ?? "";
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            // 保存设置到配置对象
            
            // 语言设置
            if (LanguageComboBox.SelectedItem is ComboBoxItem languageItem)
            {
                if (int.TryParse(languageItem.Tag?.ToString(), out int languageValue))
                {
                    _languageManager.CurrentLanguage = (LanguageType)languageValue;
                }
            }

            // 界面主题
            if (ThemeComboBox.SelectedItem is ComboBoxItem themeItem)
            {
                _config.Theme = themeItem.Tag?.ToString() ?? "Light";
            }

            // 启动设置
            _config.StartMaximized = StartMaximizedCheckBox.IsChecked ?? true;
            _config.AutoStartCamera = AutoStartCameraCheckBox.IsChecked ?? true;
            _config.CameraIndex = CameraComboBox.SelectedIndex;
            _config.DefaultPenWidth = PenWidthSlider.Value;
            _config.EnableCanvasMode = CanvasModeCheckBox.IsChecked ?? false;

            // 获取选中的画笔颜色
            if (PenColorComboBox.SelectedItem is ComboBoxItem colorItem)
            {
                _config.DefaultPenColor = colorItem.Tag?.ToString() ?? "#FF0000FF";
            }

            // 高级设置
            _config.EnableHardwareAcceleration = EnableHardwareAccel.IsChecked ?? true;
            _config.EnableFrameProcessing = EnableFrameProcessing.IsChecked ?? false;
            _config.FrameRateLimit = FrameRateComboBox.SelectedIndex;

            // 开发者模式设置
            _config.DeveloperMode = DeveloperModeCheckBox.IsChecked ?? false;

            // 启动图设置
            _config.StartupImageUrl = StartupImageUrlTextBox.Text?.Trim() ?? "";

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void VisitWebsiteButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 打开GitHub发布页
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://github.com/wwcrdrvf6u/ShowWrite/",
                    UseShellExecute = true // 必须设置为true才能打开URL
                });
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // 处理可能的异常（如默认浏览器未设置）
                System.Windows.MessageBox.Show($"无法打开链接: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 更新文件关联状态显示
        /// </summary>
        private void UpdateFileAssociationStatus()
        {
            if (FileAssociationStatusText == null)
                return;

            bool registered = FileAssociationService.IsRegistered();
            FileAssociationStatusText.Text = registered
                ? _languageManager.GetTranslation("AssociationRegistered")
                : _languageManager.GetTranslation("AssociationNotRegistered");
            FileAssociationStatusText.Foreground = registered
                ? System.Windows.Media.Brushes.Green
                : System.Windows.Media.Brushes.Gray;

            if (RegisterAssociationBtn != null)
                RegisterAssociationBtn.IsEnabled = !registered;
            if (UnregisterAssociationBtn != null)
                UnregisterAssociationBtn.IsEnabled = registered;
        }

        /// <summary>
        /// 注册文件关联
        /// </summary>
        private void RegisterAssociation_Click(object sender, RoutedEventArgs e)
        {
            bool ok = FileAssociationService.RegisterAssociations();
            if (ok)
            {
                System.Windows.MessageBox.Show(
                    _languageManager.GetTranslation("AssociationRegisterSuccess"),
                    _languageManager.GetTranslation("FileAssociationSettings"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                System.Windows.MessageBox.Show(
                    _languageManager.GetTranslation("AssociationRegisterFail"),
                    _languageManager.GetTranslation("FileAssociationSettings"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            UpdateFileAssociationStatus();
        }

        /// <summary>
        /// 取消文件关联
        /// </summary>
        private void UnregisterAssociation_Click(object sender, RoutedEventArgs e)
        {
            FileAssociationService.UnregisterAssociations();
            System.Windows.MessageBox.Show(
                _languageManager.GetTranslation("AssociationUnregisterSuccess"),
                _languageManager.GetTranslation("FileAssociationSettings"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            UpdateFileAssociationStatus();
        }
    }
}
