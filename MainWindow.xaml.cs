using AForge.Imaging.Filters;
using iNKORE.UI.WPF.Modern;
using Newtonsoft.Json;
using ShowWriteAir.Models;
using ShowWriteAir.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using WinBrush = System.Windows.Media.Brush;
using WinBrushes = System.Windows.Media.Brushes;
using WinButton = System.Windows.Controls.Button;
using WinComboBox = System.Windows.Controls.ComboBox;
using WinCursors = System.Windows.Input.Cursors;
using WinOrientation = System.Windows.Controls.Orientation;
using WinMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WinMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using WinPoint = System.Windows.Point;
using WinImage = System.Windows.Controls.Image;
using System.Windows.Controls.Primitives;
using ListBox = System.Windows.Controls.ListBox;
using QRCoder;
using System.Diagnostics;
using System.Threading.Tasks;

namespace ShowWriteAir
{
    public partial class MainWindow : Window
    {
        // 管理器实例
        private readonly VideoService _videoService = new();
        private DrawingManager _drawingManager;
        private CameraManager _cameraManager;
        private PanZoomManager _panZoomManager;
        private MemoryManager _memoryManager;
        private FrameProcessor _frameProcessor;
        private TouchManager _touchManager;
        private LogManager _logManager;
        private PhotoPopupManager _photoPopupManager;
        private Services.DeviceConnectionManager _deviceConnectionManager;
        private LanguageManager _languageManager;

        // 数据集合
        private readonly ObservableCollection<PhotoWithStrokes> _photos = new();
        private StrokeCollection _liveStrokes = new StrokeCollection();

        // 状态变量
        private bool _isLiveMode = true;
        private bool _isClosing = false;
        private AppConfig config = new AppConfig();

        // 视频帧接收状态
        private bool _isFirstFrameProcessed = false;

        // UI相关
        private SolidColorBrush _noCameraBackground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 40, 40));
        private Button _currentSelectedColorButton = null;
        private string _currentPenColor = "Black";

        // 配置文件路径
        private readonly string configPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        // 双击检测
        private DateTime _lastClickTime = DateTime.MinValue;
        private const int DoubleClickDelay = 300; // 毫秒

        // 画面调节参数
        private double _brightness = 0.0;
        private double _contrast = 0.0;
        private int _rotation = 0;
        private bool _mirrorHorizontal = false;
        private bool _mirrorVertical = false;

        // 梯形校正相关
        private bool _isPerspectiveCorrectionMode = false;
        private bool _isEnteringCorrectionMode = false; // 防止重复进入的保护机制
        private System.Drawing.Bitmap _originalCorrectionFrame = null;
        private int _draggingPointIndex = -1;
        private WinPoint[] _correctionPoints = new WinPoint[4];
        private bool _isCorrectionModeInitialized = false;

        // 启动图相关 - 由App.xaml.cs控制
        private bool _shouldShowSplash = false;

        // 文件关联打开标志 - 由App.xaml.cs控制
        private bool _isOpenedFromFile = false;

        // 主题相关
        private ResourceDictionary _currentTheme;

        // 清屏确认滑块相关
        private bool _isSliderDragging = false;
        private double _sliderStartX = 0;
        private double _sliderMaxDistance = 0;
        private bool _sliderReachedEnd = false;

        /// <summary>
        /// 主构造函数 - 由App.xaml.cs调用
        /// </summary>
        /// <param name="shouldShowSplash">是否显示启动图</param>
        /// <param name="isOpenedFromFile">是否通过文件关联打开</param>
        public MainWindow(bool shouldShowSplash = false, bool isOpenedFromFile = false)
        {
            _shouldShowSplash = shouldShowSplash;
            _isOpenedFromFile = isOpenedFromFile;

            // 如果App.xaml.cs要求显示启动图，这里才显示
            if (_shouldShowSplash)
            {
                ShowSplashScreen();
            }

            // 初始化日志系统
            Logger.Initialize(minLogLevel: LogLevel.Debug);
            Logger.Info("MainWindow", "主窗口初始化开始");
            _logManager = new LogManager();

            InitializeComponent();

            // 初始化管理器
            InitializeManagers();

            // 初始化UI和数据绑定
            InitializeUI();

            // 加载配置和启动
            LoadAndStart();
            // 在加载配置后再初始化颜色选择器，确保使用最新的默认颜色
            InitializePenColorSelector();

            this.Loaded += MainWindow_Loaded;
            this.SizeChanged += MainWindow_SizeChanged;
            this.ContentRendered += MainWindow_ContentRendered;

            Logger.Info("MainWindow", "主窗口初始化完成");

            // 如果显示了启动图，现在关闭它
            if (_shouldShowSplash)
            {
                CloseSplashScreen();
            }
        }

        /// <summary>
        /// 默认构造函数 - 保留供WPF设计器使用
        /// </summary>
        public MainWindow() : this(false, false)
        {
        }

        /// <summary>
        /// 显示启动图
        /// </summary>
        private void ShowSplashScreen()
        {
            try
            {
                Logger.Debug("MainWindow", "显示启动图");

                // 注意：这里我们不实际创建启动窗口
                // 启动图由App.xaml.cs控制
                Logger.Debug("MainWindow", "启动图由App.xaml.cs控制");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"显示启动图失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 关闭启动图
        /// </summary>
        private void CloseSplashScreen()
        {
            try
            {
                Logger.Debug("MainWindow", "关闭启动图");
                // 启动图由App.xaml.cs控制，这里只是记录
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"关闭启动图失败: {ex.Message}", ex);
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // 记录进程ID
                var process = System.Diagnostics.Process.GetCurrentProcess();
                Logger.Info("MainWindow", $"主窗口加载完成，进程ID: {process.Id}, 进程名: {process.ProcessName}");

                // 确保校正画布有正确的尺寸
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (CorrectionCanvas != null && FindName("VideoArea") != null)
                    {
                        var videoArea = (Grid)FindName("VideoArea");
                        CorrectionCanvas.Width = videoArea.ActualWidth;
                        CorrectionCanvas.Height = videoArea.ActualHeight;
                    }
                }), DispatcherPriority.Loaded);

                // 确保主窗口在前台
                this.Activate();
                this.Topmost = true;
                this.Topmost = false;
                this.Focus();

                // 检查是否有多余进程
                CheckForDuplicateProcesses();
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"主窗口加载事件失败: {ex.Message}", ex);
            }
        }

        private void MainWindow_ContentRendered(object sender, EventArgs e)
        {
            App.CloseSplash();
        }

        /// <summary>
        /// 检查重复进程
        /// </summary>
        private void CheckForDuplicateProcesses()
        {
            try
            {
                var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
                var processes = System.Diagnostics.Process.GetProcessesByName(currentProcess.ProcessName);

                if (processes.Length > 1)
                {
                    Logger.Warning("MainWindow", $"检测到多个进程: {processes.Length} 个同名进程");

                    foreach (var process in processes)
                    {
                        if (process.Id != currentProcess.Id)
                        {
                            Logger.Warning("MainWindow", $"发现其他进程: ID={process.Id}, 启动时间={process.StartTime}");
                        }
                    }
                }
                else
                {
                    Logger.Info("MainWindow", "进程检查正常: 只有一个进程运行");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"检查重复进程失败: {ex.Message}", ex);
            }
        }

        private void SwitchTheme(bool useDarkTheme)
        {
            // 清除现有资源
            this.Resources.MergedDictionaries.Clear();

            // 创建新的资源字典
            var resourceDictionary = new ResourceDictionary();

            // 根据主题加载对应的资源文件
            if (useDarkTheme)
            {
                resourceDictionary.MergedDictionaries.Add(
                    new ResourceDictionary() { Source = new Uri("themes/DarkTheme.xaml", UriKind.Relative) });
            }
            else
            {
                resourceDictionary.MergedDictionaries.Add(
                    new ResourceDictionary() { Source = new Uri("themes/LightTheme.xaml", UriKind.Relative) });
            }

            // 添加画笔设置按钮样式
            var penSettingsStyle = new Style(typeof(Button));
            penSettingsStyle.Setters.Add(new Setter(Button.WidthProperty, 32.0));
            penSettingsStyle.Setters.Add(new Setter(Button.HeightProperty, 32.0));
            penSettingsStyle.Setters.Add(new Setter(Button.MarginProperty, new Thickness(2)));
            penSettingsStyle.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(1)));
            penSettingsStyle.Setters.Add(new Setter(Button.BorderBrushProperty, new SolidColorBrush(System.Windows.Media.Color.FromRgb(85, 85, 85))));
            resourceDictionary.Add("PenSettingsButtonStyle", penSettingsStyle);

            // 应用新的资源字典
            this.Resources = resourceDictionary;
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 如果在校正模式下，重新初始化校正点位置
            if (_isPerspectiveCorrectionMode && _isCorrectionModeInitialized)
            {
                InitializeCorrectionPoints();
            }

            // 照片栏固定在右侧，不需要重新定位
        }

        private void InitializeManagers()
        {
            try
            {
                Logger.Info("MainWindow", "开始初始化管理器");

                if (config == null) config = new AppConfig();

                _drawingManager = new DrawingManager((InkCanvas)FindName("Ink"), (Grid)FindName("VideoArea"), this);

                var eraserOverlayCanvas = (System.Windows.Controls.Canvas)FindName("EraserOverlayCanvas");
                if (eraserOverlayCanvas != null)
                {
                    _drawingManager.InitializeEraserOverlay(eraserOverlayCanvas);
                }

                var overlayInkCanvas = (InkCanvas)FindName("OverlayInkCanvas");
                var zoomTransform = (ScaleTransform)FindName("ZoomTransform");
                var panTransform = (TranslateTransform)FindName("PanTransform");
                if (overlayInkCanvas != null && zoomTransform != null && panTransform != null)
                {
                    _drawingManager.SetOverlayInkCanvas(overlayInkCanvas, zoomTransform, panTransform);
                    Logger.Info("MainWindow", "OverlayInkCanvas 已设置到 DrawingManager");
                }

                _cameraManager = new CameraManager(_videoService, config);

                _memoryManager = new MemoryManager();

                _frameProcessor = new FrameProcessor(_cameraManager, _memoryManager);

                _panZoomManager = new PanZoomManager((ScaleTransform)FindName("ZoomTransform"), (TranslateTransform)FindName("PanTransform"), (Grid)FindName("VideoArea"), _drawingManager);

                _touchManager = new TouchManager(_drawingManager);

                InitializePhotoPopupManager();

                SubscribeToEvents();

                Logger.Info("MainWindow", "管理器初始化完成");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"初始化管理器失败: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// 初始化照片悬浮窗管理器
        /// </summary>
        private void InitializePhotoPopupManager()
        {
            try
            {
                _photoPopupManager = new PhotoPopupManager(
                    null,
                    PhotoList,
                    this,
                    _photos,
                    _drawingManager,
                    _cameraManager,
                    _memoryManager,
                    _frameProcessor,
                    _panZoomManager,
                    _logManager);

                // 订阅照片悬浮窗管理器事件
                _photoPopupManager.PhotoSelected += OnPhotoSelected;
                _photoPopupManager.BackToLiveRequested += OnBackToLiveRequested;
                _photoPopupManager.SaveImageRequested += OnSaveImageRequested;

                // 订阅照片集合变化：本地新增照片自动同步到已连接的手机客户端
                // 来自手机的照片（FromClient）不再同步回手机，以避免循环
                _photos.CollectionChanged += Photos_CollectionChanged;

                Logger.Info("MainWindow", "照片悬浮窗管理器初始化完成");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"初始化照片悬浮窗管理器失败: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// 照片选择事件处理（修复版）
        /// </summary>
        private void OnPhotoSelected(PhotoWithStrokes photo)
        {
            try
            {
                if (photo == null)
                {
                    Logger.Warning("MainWindow", "照片选择事件收到空照片对象");
                    return;
                }

                if (photo.Image == null)
                {
                    Logger.Warning("MainWindow", "照片对象的Image属性为空");
                    return;
                }

                Logger.Info("MainWindow", $"切换到照片查看模式，照片尺寸: {photo.Image.Width}x{photo.Image.Height}");

                // 1. 设置到非实时模式
                _isLiveMode = false;

                // 2. 显示选中的照片
                var videoImage = (WinImage)FindName("VideoImage");
                var videoArea = (Grid)FindName("VideoArea");
                if (videoImage != null)
                {
                    videoImage.Source = photo.Image;
                }
                if (videoArea != null)
                {
                    videoArea.Background = WinBrushes.Transparent;
                }

                // 3. 切换到照片对应的笔迹（按 origin 尺寸做坐标缩放，解决窗口尺寸变化导致的笔迹与照片错位）
                //    同时还原矢量/位图填充，并按同一 scaleX/scaleY 缩放以保持对齐
                if (photo.Strokes != null)
                {
                    _drawingManager.SwitchToPhotoStrokes(photo.Strokes, photo.OriginInkWidth, photo.OriginInkHeight,
                        photo.FillPaths, photo.FillImages);
                    Logger.Debug("MainWindow", $"已切换到照片笔迹，包含 {photo.Strokes.Count} 个笔迹 (origin {photo.OriginInkWidth}x{photo.OriginInkHeight}, 填充 {(photo.FillPaths?.Count ?? 0)}+{(photo.FillImages?.Count ?? 0)})");
                }
                else
                {
                    Logger.Warning("MainWindow", "照片没有关联的笔迹");
                    _drawingManager.SwitchToPhotoStrokes(new StrokeCollection(), 0, 0, photo.FillPaths, photo.FillImages);
                }

                // 4. 更新UI状态
                UpdateUIModeForPhotoView();

                // 5. 触发内存清理（异步执行，不阻塞UI）
                Task.Run(() => _memoryManager?.TriggerMemoryCleanup());

                Logger.Info("MainWindow", "已成功切换到照片查看模式");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"处理照片选择事件失败: {ex.Message}", ex);

                // 出错时尝试恢复实时模式
                try
                {
                    _isLiveMode = true;
                    if (_cameraManager != null && _cameraManager.IsCameraAvailable)
                    {
                        _cameraManager.RestartCamera();
                    }
                    var videoImage = (WinImage)FindName("VideoImage");
                    if (videoImage != null)
                    {
                        videoImage.Source = null;
                    }
                    if (CaptureBtn != null)
                    {
                        CaptureBtn.Visibility = Visibility.Visible;
                    }
                    if (ScanBtn != null)
                    {
                        ScanBtn.Visibility = Visibility.Visible;
                    }
                }
                catch (Exception innerEx)
                {
                    Logger.Error("MainWindow", $"恢复实时模式失败: {innerEx.Message}", innerEx);
                }
            }
        }

        /// <summary>
        /// 为照片查看模式更新UI状态
        /// </summary>
        private void UpdateUIModeForPhotoView()
        {
            try
            {
                // 1. 设置窗口标题显示照片模式
                this.Title = $"ShowWriteAir - 照片查看模式";

                // 2. 关闭可能的悬浮窗
                if (PenSettingsPopup.IsOpen)
                {
                    PenSettingsPopup.IsOpen = false;
                }

                // 3. 隐藏拍照和扫描按钮
                if (CaptureBtn != null)
                {
                    CaptureBtn.Visibility = Visibility.Collapsed;
                }
                if (ScanBtn != null)
                {
                    ScanBtn.Visibility = Visibility.Collapsed;
                }

                Logger.Debug("MainWindow", "UI状态已更新为照片查看模式");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"更新UI状态失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 返回实时模式请求处理（修复版）
        /// </summary>
        private void OnBackToLiveRequested()
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    // ---------------------------------------------------------
                    // [新增修复 3] 清除列表选中状态
                    // 这样下次点击同一张照片时，SelectionChanged 事件才能再次触发
                    if (PhotoList != null)
                    {
                        PhotoList.SelectedIndex = -1;
                    }
                    // ---------------------------------------------------------

                    // 1. 重置视频帧记录状态
                    _isFirstFrameProcessed = false;
                    Logger.ResetVideoFrameLogging();

                    // 2. 重新启动摄像头
                    if (_cameraManager != null && _cameraManager.IsCameraAvailable)
                    {
                        _cameraManager.RestartCamera();
                    }

                    // 3. 设置为实时模式
                    _isLiveMode = true;

                    // 4. 清空视频图像，让摄像头帧重新显示
                    var videoImage = (WinImage)FindName("VideoImage");
                    var videoArea = (Grid)FindName("VideoArea");
                    if (videoImage != null)
                    {
                        videoImage.Source = null;
                    }
                    if (videoArea != null)
                    {
                        videoArea.Background = _noCameraBackground;
                    }

                    // 5. 切换回实时笔迹
                    _drawingManager.SwitchToPhotoStrokes(_liveStrokes);

                    // 6. 更新UI状态
                    this.Title = "ShowWriteAir";

                    // 7. 显示拍照和扫描按钮
                    if (CaptureBtn != null)
                    {
                        CaptureBtn.Visibility = Visibility.Visible;
                    }
                    if (ScanBtn != null)
                    {
                        ScanBtn.Visibility = Visibility.Visible;
                    }

                    Logger.Info("MainWindow", "已返回实时模式");
                }
                catch (Exception ex)
                {
                    Logger.Error("MainWindow", $"处理返回实时模式请求失败: {ex.Message}", ex);
                }
            });
        }

        /// <summary>
        /// 保存图片请求处理
        /// </summary>
        private void OnSaveImageRequested()
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    // 调用原有的保存图片逻辑
                    SaveImage_Click(null, null);

                    Logger.Debug("MainWindow", "保存图片请求处理完成");
                }
                catch (Exception ex)
                {
                    Logger.Error("MainWindow", $"处理保存图片请求失败: {ex.Message}", ex);
                }
            });
        }

        /// <summary>
        /// 订阅管理器事件
        /// </summary>
        private void SubscribeToEvents()
        {
            try
            {
                Logger.Debug("MainWindow", "开始订阅事件");

                // 摄像头帧事件
                _cameraManager.OnNewFrameProcessed += OnCameraFrameReceived;

                // 绘制管理器事件
                _drawingManager.OnSDKTouchAreaChanged += OnSDKTouchAreaChanged;

                // 触控管理器事件
                _touchManager.OnTouchCountChanged += OnTouchCountChanged;
                _touchManager.OnTouchAreaChanged += OnTouchAreaChanged;
                _touchManager.OnTouchCenterChanged += OnTouchCenterChanged;

                Logger.Debug("MainWindow", "事件订阅完成");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"订阅事件失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 取消所有事件订阅
        /// </summary>
        private void UnsubscribeAllEvents()
        {
            try
            {
                Logger.Info("MainWindow", "开始取消所有事件订阅");

                // 取消摄像头管理器事件
                if (_cameraManager != null)
                {
                    _cameraManager.OnNewFrameProcessed -= OnCameraFrameReceived;
                }

                // 取消绘制管理器事件
                if (_drawingManager != null)
                {
                    _drawingManager.OnSDKTouchAreaChanged -= OnSDKTouchAreaChanged;
                }

                // 取消触控管理器事件
                if (_touchManager != null)
                {
                    _touchManager.OnTouchCountChanged -= OnTouchCountChanged;
                    _touchManager.OnTouchAreaChanged -= OnTouchAreaChanged;
                    _touchManager.OnTouchCenterChanged -= OnTouchCenterChanged;
                }

                // 取消照片悬浮窗管理器事件
                if (_photoPopupManager != null)
                {
                    _photoPopupManager.PhotoSelected -= OnPhotoSelected;
                    _photoPopupManager.BackToLiveRequested -= OnBackToLiveRequested;
                    _photoPopupManager.SaveImageRequested -= OnSaveImageRequested;
                }

                // 取消窗口事件
                this.Loaded -= MainWindow_Loaded;
                this.SizeChanged -= MainWindow_SizeChanged;

                Logger.Info("MainWindow", "所有事件订阅已取消");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"取消事件订阅失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 初始化UI和数据绑定
        /// </summary>
        private void InitializeUI()
        {
            try
            {
                Logger.Debug("MainWindow", "开始初始化UI");

                // 初始化语言管理器
                _languageManager = LanguageManager.Instance;
                _languageManager.LanguageChanged += UpdateLanguageUI;

                // 初始化实时模式笔迹
                _drawingManager.SwitchToPhotoStrokes(_liveStrokes);

                // 应用窗口设置
                WindowStyle = WindowStyle.None;
                WindowState = config.StartMaximized ? WindowState.Maximized : WindowState.Normal;

                // 应用绘制管理器配置
                _drawingManager.ApplyConfig(config);

                // 根据画板模式更新油漆桶按钮可见性
                UpdatePaintBucketButtonVisibility();

                // 初始化UI组件
                InitializePenSettingsPopup();
                InitializeTouchInfoPopup();

                

                if (PhotoList != null)
                {
                    PhotoList.SelectionChanged -= PhotoList_SelectionChanged; // 防止重复绑定
                    PhotoList.SelectionChanged += PhotoList_SelectionChanged;
                }
                // 开始触控跟踪
                _touchManager.StartTracking();

                // 更新语言UI
                UpdateLanguageUI();

                Logger.Debug("MainWindow", "UI初始化完成");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"初始化UI失败: {ex.Message}", ex);
            }
        }

        private void PhotoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 如果选中项是 PhotoWithStrokes 类型，则调用切换逻辑
            if (PhotoList.SelectedItem is PhotoWithStrokes photo)
            {
                // 调用现有的照片选择处理逻辑
                OnPhotoSelected(photo);

                // 确保照片栏保持展开
                var photoPanelBorder = FindName("PhotoPanelBorder") as Border;
                if (photoPanelBorder != null && photoPanelBorder.Visibility != Visibility.Visible)
                {
                    photoPanelBorder.Visibility = Visibility.Visible;
                    UpdatePhotoButtonState(true);
                }
            }
        }

        /// <summary>
        /// 加载配置和启动应用
        /// </summary>
        private void LoadAndStart()
        {
            try
            {
                Logger.Debug("MainWindow", "开始加载配置和启动");

                // 加载配置
                LoadConfig();

                // LoadConfig 加载真实配置后，重新应用画板模式等运行时设置
                // （InitializeUI 中的 ApplyConfig 用的是字段初始化的默认值）
                _drawingManager.ApplyConfig(config);
                UpdatePaintBucketButtonVisibility();

                // 应用主题
                ApplyTheme();

                // 检查摄像头可用性
                if (!_cameraManager.CheckCameraAvailability())
                {
                    ShowNoCameraBackground();
                }
                else if (config.AutoStartCamera && !_isOpenedFromFile)
                {
                    StartCameraWithFallback();

                    // 启动后应用摄像头配置
                    ApplyCameraConfigOnStartup();
                }
                else if (_isOpenedFromFile)
                {
                    ShowNoCameraBackground();
                    Logger.Info("MainWindow", "通过文件关联打开，不自动连接摄像头");
                }

                // 显示 TouchSDK 状态
                UpdateTouchSDKStatus();

                // 调试图层可见性
                TestLayerVisibility();

                Logger.Debug("MainWindow", "配置加载和启动完成");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"加载配置和启动失败: {ex.Message}", ex);
            }
        }

        #region 初始化方法

        /// <summary>
        /// 初始化画笔设置悬浮窗
        /// </summary>
        private void InitializePenSettingsPopup()
        {
            try
            {
                // 设置初始笔宽并保存原始宽度
                _panZoomManager.SetOriginalPenWidth(_drawingManager.UserPenWidth);
                PenWidthSlider.Value = _drawingManager.UserPenWidth;
                PenWidthValue.Text = _drawingManager.UserPenWidth.ToString("0");

                Logger.Debug("MainWindow", "画笔设置悬浮窗初始化完成");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"初始化画笔设置悬浮窗失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 初始化触控信息悬浮窗
        /// </summary>
        private void InitializeTouchInfoPopup()
        {
            try
            {
                // 设置悬浮窗初始位置在右上角
                TouchInfoPopup.HorizontalOffset = SystemParameters.PrimaryScreenWidth - 200;
                TouchInfoPopup.VerticalOffset = 50;

                // 根据开发者模式设置悬浮窗可见性
                TouchInfoPopup.IsOpen = config.DeveloperMode;

                Logger.Debug("MainWindow", $"触控信息悬浮窗初始化完成，开发者模式: {config.DeveloperMode}，悬浮窗显示: {TouchInfoPopup.IsOpen}");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"初始化触控信息悬浮窗失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 图层可见性测试方法
        /// </summary>
        private void TestLayerVisibility()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    Logger.Debug("MainWindow", "=== 图层可见性测试 ===");
                    var videoArea = (Grid)FindName("VideoArea");
                    Logger.Debug("MainWindow", $"VideoArea 子元素数量: {VisualTreeHelper.GetChildrenCount(videoArea)}");

                    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(videoArea); i++)
                    {
                        var child = VisualTreeHelper.GetChild(videoArea, i);
                        Logger.Debug("MainWindow", $"子元素 {i}: {child.GetType().Name}, 可见性: {((UIElement)child).Visibility}");
                    }

                    var videoImage = (WinImage)FindName("VideoImage");
                    var ink = (InkCanvas)FindName("Ink");

                    Logger.Debug("MainWindow", $"VideoImage 源: {videoImage?.Source}");
                    Logger.Debug("MainWindow", $"VideoImage 渲染尺寸: {videoImage?.RenderSize}");
                    Logger.Debug("MainWindow", $"InkCanvas 背景: {ink?.Background}");
                    Logger.Debug("MainWindow", $"InkCanvas 默认绘制属性: {ink?.DefaultDrawingAttributes.Color}, {ink?.DefaultDrawingAttributes.Width}");

                    Logger.Debug("MainWindow", "=== 图层可见性测试结束 ===");
                }
                catch (Exception ex)
                {
                    Logger.Error("MainWindow", $"图层可见性测试失败: {ex.Message}", ex);
                }
            }), DispatcherPriority.Loaded);
        }

        #endregion

        #region 事件处理方法


        /// <summary>
        /// 摄像头帧接收事件（修复版）
        /// </summary>
        private void OnCameraFrameReceived(System.Drawing.Bitmap frame)
        {
            // 如果不是实时模式或正在关闭，不处理帧
            if (_isClosing || !_isLiveMode || _isPerspectiveCorrectionMode)
            {
                _memoryManager?.DisposeFrame(frame, true);
                return;
            }

            Dispatcher.Invoke(() =>
            {
                if (_isLiveMode && !_isClosing && !_isPerspectiveCorrectionMode)
                {
                    try
                    {
                        // 记录第一次视频帧接收状态
                        if (!_isFirstFrameProcessed)
                        {
                            bool frameValid = frame != null && frame.Width > 0 && frame.Height > 0;
                            string frameInfo = frameValid ?
                                $"帧尺寸: {frame.Width}x{frame.Height}" :
                                "无效帧";

                            Logger.LogVideoFrameStatus("Camera", frameValid, frameInfo);
                            _isFirstFrameProcessed = true;
                        }

                        // 处理并显示帧
                        var bitmapImage = _frameProcessor.ProcessFrameToBitmapImage(frame);
                        var videoImage = (WinImage)FindName("VideoImage");
                        if (bitmapImage != null && videoImage != null)
                        {
                            videoImage.Source = bitmapImage;
                        }

                        // 更新内存管理
                        _memoryManager?.UpdateLastProcessedFrame(frame);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("MainWindow", $"视频帧处理错误: {ex.Message}", ex);
                    }
                    finally
                    {
                        // 释放当前帧
                        _memoryManager?.DisposeFrame(frame);
                    }
                }
                else
                {
                    _memoryManager?.DisposeFrame(frame, true);
                }
            });
        }

        /// <summary>
        /// TouchSDK 面积变化事件
        /// </summary>
        private void OnSDKTouchAreaChanged(double area)
        {
            if (_isClosing) return;

            Dispatcher.Invoke(() =>
            {
                _touchManager.UpdateSDKTouchArea(area);
                UpdateSDKTouchAreaDisplay();
            });
        }

        /// <summary>
        /// 触控点数变化事件
        /// </summary>
        private void OnTouchCountChanged(int count)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateTouchInfoDisplay();
            });
        }

        /// <summary>
        /// 触控面积变化事件
        /// </summary>
        private void OnTouchAreaChanged(double area)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateTouchInfoDisplay();
            });
        }

        /// <summary>
        /// 触控中心变化事件
        /// </summary>
        private void OnTouchCenterChanged(WinPoint center)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateTouchInfoDisplay();
            });
        }

        /// <summary>
        /// 更新触控信息显示
        /// </summary>
        private void UpdateTouchInfoDisplay()
        {
            try
            {
                if (TouchCountText != null)
                {
                    TouchCountText.Text = _touchManager.GetTouchSDKStatusText();
                }

                if (TouchAreaText != null)
                {
                    var area = _touchManager.TouchCount >= 3 ?
                        _touchManager.CalculatePolygonArea(_touchManager.GetCurrentTouchPoints()) : 0;
                    TouchAreaText.Text = $"面积: {area:F0} 像素²";
                }

                if (TouchCenterText != null)
                {
                    var center = _touchManager.CalculateTouchCenter();
                    TouchCenterText.Text = $"中心: ({center.X:F0}, {center.Y:F0})";
                }
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"更新触控信息显示失败: {ex.Message}", ex);
            }
        }

        #endregion

        #region UI事件处理

        #region 画笔设置悬浮窗交互逻辑

        /// <summary>
        /// 画笔按钮点击事件（修改版）
        /// </summary>
        private void PenBtn_Click(object sender, RoutedEventArgs e)
        {
            // 如果当前不是画笔模式，切换到画笔模式
            if (_drawingManager.CurrentMode != DrawingManager.ToolMode.Pen)
            {
                SetMode(DrawingManager.ToolMode.Pen);
                Logger.Debug("MainWindow", "切换到画笔模式");
            }
            else
            {
                // 如果已经是画笔模式，切换悬浮窗的显示状态
                PenSettingsPopup.IsOpen = !PenSettingsPopup.IsOpen;
                
                // 确保按钮保持选中状态（因为 ToggleButton 点击会自动切换状态）
                PenBtn.IsChecked = true;
                
                Logger.Debug("MainWindow", $"切换悬浮窗状态: {PenSettingsPopup.IsOpen}");
            }
        }

        /// <summary>
        /// VideoArea鼠标按下事件 - 添加悬浮窗自动隐藏逻辑
        /// </summary>
        private void VideoArea_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_isClosing || _isPerspectiveCorrectionMode) return;

            // 自动隐藏画笔设置悬浮窗
            if (PenSettingsPopup.IsOpen && _drawingManager.CurrentMode == DrawingManager.ToolMode.Pen)
            {
                PenSettingsPopup.IsOpen = false;
                Logger.Debug("MainWindow", "点击VideoArea，自动隐藏画笔设置悬浮窗");
            }

            // 形状功能已移除

            // 自动隐藏连接设备悬浮窗
            if (ConnectDevicePopup.IsOpen)
            {
                ConnectDevicePopup.IsOpen = false;
                Logger.Debug("MainWindow", "点击VideoArea，自动隐藏连接设备悬浮窗");
            }

            // 自动收起照片栏
            var photoPanelBorder = FindName("PhotoPanelBorder") as Border;
            if (photoPanelBorder != null && photoPanelBorder.Visibility == Visibility.Visible)
            {
                photoPanelBorder.Visibility = Visibility.Collapsed;
                UpdatePhotoButtonState(false);
                Logger.Debug("MainWindow", "点击VideoArea，自动收起照片栏");
            }

            // 调用原有的鼠标事件处理
            _panZoomManager.HandleMouseDown(e, _drawingManager.CurrentMode);
            _drawingManager.HandleMouseDown(e);
        }

        /// <summary>
        /// VideoArea鼠标左键按下事件（双击对焦）- 添加悬浮窗自动隐藏逻辑
        /// </summary>
        private void VideoArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_isClosing || _isPerspectiveCorrectionMode) return;

            // 自动隐藏画笔设置悬浮窗
            if (PenSettingsPopup.IsOpen && _drawingManager.CurrentMode == DrawingManager.ToolMode.Pen)
            {
                PenSettingsPopup.IsOpen = false;
                Logger.Debug("MainWindow", "点击VideoArea，自动隐藏画笔设置悬浮窗");
            }

            // 自动收起照片栏
            var photoPanelBorder2 = FindName("PhotoPanelBorder") as Border;
            if (photoPanelBorder2 != null && photoPanelBorder2.Visibility == Visibility.Visible)
            {
                photoPanelBorder2.Visibility = Visibility.Collapsed;
                UpdatePhotoButtonState(false);
                Logger.Debug("MainWindow", "点击VideoArea，自动收起照片栏");
            }

            // 原有的双击检测逻辑
            var currentTime = DateTime.Now;
            var timeSinceLastClick = (currentTime - _lastClickTime).TotalMilliseconds;

            if (timeSinceLastClick <= DoubleClickDelay)
            {
                // 双击事件 - 自动对焦
                if (_drawingManager.CurrentMode == DrawingManager.ToolMode.Move)
                {
                    try
                    {
                        if (_cameraManager.IsCameraAvailable)
                        {
                            _cameraManager.AutoFocus();
                            Logger.Info("MainWindow", "触发自动对焦");
                            MessageBox.Show("已触发自动对焦。", "对焦");
                        }
                        else
                        {
                            Logger.Warning("MainWindow", "没有可用的摄像头，无法进行自动对焦");
                            MessageBox.Show("没有可用的摄像头，无法进行自动对焦。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("MainWindow", $"自动对焦失败: {ex.Message}", ex);
                        MessageBox.Show("自动对焦失败: " + ex.Message, "错误");
                    }
                }
                _lastClickTime = DateTime.MinValue; // 重置
            }
            else
            {
                _lastClickTime = currentTime;
            }

            // 调用绘制管理器的鼠标按下处理
            _drawingManager.HandleMouseDown(e);
        }

        #endregion

        private void VideoArea_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isPerspectiveCorrectionMode) return;
            _panZoomManager.HandleMouseMove(e, _drawingManager.CurrentMode);
            _drawingManager.HandleMouseMove(e);
        }

        private void VideoArea_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPerspectiveCorrectionMode) return;
            _panZoomManager.HandleMouseUp(e, _drawingManager.CurrentMode);
            _drawingManager.HandleMouseUp(e);
        }

        private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_isPerspectiveCorrectionMode) return;
            _panZoomManager.HandleMouseWheel(e, _drawingManager.CurrentMode, VideoArea);
            _drawingManager.HandleMouseWheel(e);
        }

        // 触控事件
        protected override void OnTouchDown(TouchEventArgs e)
        {
            if (_isClosing || _isPerspectiveCorrectionMode) return;
            base.OnTouchDown(e);
            _touchManager.HandleTouchDown(e, VideoArea);
            _drawingManager.HandleTouchDown(e);
        }

        protected override void OnTouchMove(TouchEventArgs e)
        {
            if (_isClosing || _isPerspectiveCorrectionMode) return;
            base.OnTouchMove(e);
            _touchManager.HandleTouchMove(e, VideoArea);
            _drawingManager.HandleTouchMove(e);
        }

        protected override void OnTouchUp(TouchEventArgs e)
        {
            if (_isClosing || _isPerspectiveCorrectionMode) return;
            base.OnTouchUp(e);
            _touchManager.HandleTouchUp(e);
            _drawingManager.HandleTouchUp(e);
        }

        // 手势操作
        private void VideoArea_ManipulationStarting(object sender, ManipulationStartingEventArgs e)
        {
            if (_isPerspectiveCorrectionMode) return;
            _panZoomManager.HandleManipulationStarting(e, _drawingManager.CurrentMode);
        }

        private void VideoArea_ManipulationDelta(object sender, ManipulationDeltaEventArgs e)
        {
            if (_isPerspectiveCorrectionMode) return;
            _panZoomManager.HandleManipulationDelta(e, _drawingManager.CurrentMode, VideoArea);
        }

        #endregion

        #region 梯形校正功能模块

        // ==================== 梯形校正核心功能 ====================

        /// <summary>
        /// 安全的进入梯形校正模式（带防重入）
        /// </summary>
        private async void SafeEnterPerspectiveCorrectionMode()
        {
            // 防重入检查
            if (_isEnteringCorrectionMode || _isPerspectiveCorrectionMode)
            {
                Logger.Warning("MainWindow", "校正模式正在进入或已在其中，忽略请求");
                return;
            }

            _isEnteringCorrectionMode = true;

            try
            {
                await Task.Delay(100); // 给UI一个响应时间

                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        EnterPerspectiveCorrectionMode();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("MainWindow", $"安全进入校正模式失败: {ex.Message}", ex);
                        MessageBox.Show($"进入校正模式失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                });
            }
            finally
            {
                _isEnteringCorrectionMode = false;
            }
        }

        /// <summary>
        /// 进入梯形校正模式（修复版）
        /// </summary>
        private void EnterPerspectiveCorrectionMode()
        {
            if (_isClosing) return;

            try
            {
                // 检查是否已经在校正模式下
                if (_isPerspectiveCorrectionMode)
                {
                    Logger.Warning("MainWindow", "已在校正模式下，忽略重复请求");
                    return;
                }

                // 检查摄像头
                if (!_cameraManager.IsCameraAvailable)
                {
                    MessageBox.Show("没有可用的摄像头，无法使用梯形校正功能。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Logger.Info("MainWindow", "开始进入梯形校正模式");

                // 获取当前视频帧
                var frame = _cameraManager.GetCurrentFrame();
                if (frame == null)
                {
                    MessageBox.Show("无法获取摄像头画面。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                try
                {
                    // 保存当前帧用于校正
                    _originalCorrectionFrame?.Dispose();
                    _originalCorrectionFrame = (System.Drawing.Bitmap)frame.Clone();

                    // 暂停摄像头
                    _cameraManager.PauseCamera();

                    // 显示原始图像
                    var bitmapImage = _memoryManager.BitmapToBitmapImage(_originalCorrectionFrame);
                    var videoImage = (WinImage)FindName("VideoImage");
                    if (videoImage != null)
                    {
                        videoImage.Source = bitmapImage;
                    }

                    // 隐藏底部工具栏
                    BottomToolbar.Visibility = Visibility.Collapsed;

                    // 显示校正模式界面
                    PerspectiveCorrectionGrid.Visibility = Visibility.Visible;

                    // 初始化校正点位置
                    InitializeCorrectionPoints();

                    // 设置校正点事件
                    SetupCorrectionPointsEvents();

                    // 设置模式标志
                    _isPerspectiveCorrectionMode = true;
                    _isCorrectionModeInitialized = true;

                    var ink = (InkCanvas)FindName("Ink");
                    if (ink != null)
                    {
                        ink.IsEnabled = false;
                    }

                    // 更新校正UI
                    UpdateCorrectionUI();

                    Logger.Info("MainWindow", "已进入梯形校正模式");
                }
                catch (Exception ex)
                {
                    Logger.Error("MainWindow", $"初始化校正模式失败: {ex.Message}", ex);
                    MessageBox.Show($"初始化校正模式失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);

                    // 清理资源
                    if (_originalCorrectionFrame != null)
                    {
                        _originalCorrectionFrame.Dispose();
                        _originalCorrectionFrame = null;
                    }

                    // 恢复摄像头
                    _cameraManager.ResumeCamera();

                    // 重置状态
                    _isPerspectiveCorrectionMode = false;
                    _isCorrectionModeInitialized = false;
                }
                finally
                {
                    frame.Dispose();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"进入梯形校正模式失败: {ex.Message}", ex);
                MessageBox.Show($"进入梯形校正模式失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 退出梯形校正模式（修复版）
        /// </summary>
        private void ExitPerspectiveCorrectionMode(bool applyCorrection = false)
        {
            try
            {
                Logger.Info("MainWindow", $"退出梯形校正模式，应用校正: {applyCorrection}");

                // 检查是否在校正模式下
                if (!_isPerspectiveCorrectionMode) return;

                // 清理校正点事件
                RemoveCorrectionPointsEvents();

                // 释放鼠标捕获（防止鼠标卡死）
                ReleaseAllMouseCaptures();

                var ink = (InkCanvas)FindName("Ink");
                if (ink != null)
                {
                    ink.IsEnabled = true;
                }

                // 隐藏校正模式界面
                PerspectiveCorrectionGrid.Visibility = Visibility.Collapsed;

                // 显示底部工具栏
                BottomToolbar.Visibility = Visibility.Visible;

                // 重置模式标志
                _isPerspectiveCorrectionMode = false;
                _isCorrectionModeInitialized = false;

                // 重置拖动状态
                _draggingPointIndex = -1;

                // 释放背景帧
                if (_originalCorrectionFrame != null)
                {
                    _originalCorrectionFrame.Dispose();
                    _originalCorrectionFrame = null;
                }

                // 恢复摄像头
                if (_isLiveMode)
                {
                    _cameraManager.ResumeCamera();
                }

                // 强制垃圾回收
                GC.Collect();
                GC.WaitForPendingFinalizers();

                Logger.Info("MainWindow", "已退出梯形校正模式");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"退出梯形校正模式失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 强制退出校正模式
        /// </summary>
        private void ForceExitCorrectionMode()
        {
            try
            {
                Logger.Warning("MainWindow", "强制退出校正模式");

                // 释放所有鼠标捕获
                ReleaseAllMouseCaptures();

                // 移除校正点事件
                RemoveCorrectionPointsEvents();

                // 重置状态
                _isPerspectiveCorrectionMode = false;
                _isCorrectionModeInitialized = false;
                _draggingPointIndex = -1;

                // 释放背景帧
                if (_originalCorrectionFrame != null)
                {
                    _originalCorrectionFrame.Dispose();
                    _originalCorrectionFrame = null;
                }

                // 恢复UI状态
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        var ink = (InkCanvas)FindName("Ink");
                        if (ink != null)
                        {
                            ink.IsEnabled = true;
                        }
                        PerspectiveCorrectionGrid.Visibility = Visibility.Collapsed;
                        BottomToolbar.Visibility = Visibility.Visible;
                    }
                    catch { }
                });

                Logger.Info("MainWindow", "已强制退出校正模式");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"强制退出校正模式失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 释放所有鼠标捕获
        /// </summary>
        private void ReleaseAllMouseCaptures()
        {
            try
            {
                CorrectionCanvas.ReleaseMouseCapture();
                CorrectionPoint0.ReleaseMouseCapture();
                CorrectionPoint1.ReleaseMouseCapture();
                CorrectionPoint2.ReleaseMouseCapture();
                CorrectionPoint3.ReleaseMouseCapture();

                // 释放可能的其他鼠标捕获
                Mouse.OverrideCursor = null;
                Cursor = WinCursors.Arrow;

                Logger.Debug("MainWindow", "所有鼠标捕获已释放");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"释放鼠标捕获失败: {ex.Message}", ex);
            }
        }

        // ==================== 校正点管理 ====================

        /// <summary>
        /// 初始化校正点位置
        /// </summary>
        private void InitializeCorrectionPoints()
        {
            try
            {
                var videoArea = (Grid)FindName("VideoArea");
                double videoWidth = videoArea.ActualWidth;
                double videoHeight = videoArea.ActualHeight;

                // 如果视频区域尺寸为0，使用默认值
                if (videoWidth <= 0 || videoHeight <= 0)
                {
                    videoWidth = 800;
                    videoHeight = 600;
                }

                // 设置四个点的初始位置（一个矩形，占视频区域的70%）
                double marginX = videoWidth * 0.15;
                double marginY = videoHeight * 0.15;

                _correctionPoints[0] = new WinPoint(marginX, marginY);
                _correctionPoints[1] = new WinPoint(videoWidth - marginX, marginY);
                _correctionPoints[2] = new WinPoint(videoWidth - marginX, videoHeight - marginY);
                _correctionPoints[3] = new WinPoint(marginX, videoHeight - marginY);

                // 设置校正画布的尺寸
                CorrectionCanvas.Width = videoWidth;
                CorrectionCanvas.Height = videoHeight;

                // 更新UI
                UpdateCorrectionUI();

                Logger.Debug("MainWindow", $"初始化校正点完成: 画布尺寸={videoWidth}x{videoHeight}");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"初始化校正点失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 更新校正UI
        /// </summary>
        private void UpdateCorrectionUI()
        {
            try
            {
                // 更新校正点的位置
                Canvas.SetLeft(CorrectionPoint0, _correctionPoints[0].X - 10);
                Canvas.SetTop(CorrectionPoint0, _correctionPoints[0].Y - 10);
                Canvas.SetLeft(CorrectionLabel0, _correctionPoints[0].X + 10);
                Canvas.SetTop(CorrectionLabel0, _correctionPoints[0].Y + 5);

                Canvas.SetLeft(CorrectionPoint1, _correctionPoints[1].X - 10);
                Canvas.SetTop(CorrectionPoint1, _correctionPoints[1].Y - 10);
                Canvas.SetLeft(CorrectionLabel1, _correctionPoints[1].X + 10);
                Canvas.SetTop(CorrectionLabel1, _correctionPoints[1].Y + 5);

                Canvas.SetLeft(CorrectionPoint2, _correctionPoints[2].X - 10);
                Canvas.SetTop(CorrectionPoint2, _correctionPoints[2].Y - 10);
                Canvas.SetLeft(CorrectionLabel2, _correctionPoints[2].X + 10);
                Canvas.SetTop(CorrectionLabel2, _correctionPoints[2].Y + 5);

                Canvas.SetLeft(CorrectionPoint3, _correctionPoints[3].X - 10);
                Canvas.SetTop(CorrectionPoint3, _correctionPoints[3].Y - 10);
                Canvas.SetLeft(CorrectionLabel3, _correctionPoints[3].X + 10);
                Canvas.SetTop(CorrectionLabel3, _correctionPoints[3].Y + 5);

                // 更新多边形
                CorrectionPolygon.Points = new PointCollection(_correctionPoints);
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"更新校正UI失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 设置校正点事件
        /// </summary>
        private void SetupCorrectionPointsEvents()
        {
            try
            {
                // 为每个校正点添加鼠标事件
                CorrectionPoint0.MouseLeftButtonDown += CorrectionPoint_MouseDown;
                CorrectionPoint1.MouseLeftButtonDown += CorrectionPoint_MouseDown;
                CorrectionPoint2.MouseLeftButtonDown += CorrectionPoint_MouseDown;
                CorrectionPoint3.MouseLeftButtonDown += CorrectionPoint_MouseDown;

                // 为校正点添加拖动事件
                CorrectionCanvas.MouseLeftButtonDown += CorrectionCanvas_MouseDown;
                CorrectionCanvas.MouseMove += CorrectionCanvas_MouseMove;
                CorrectionCanvas.MouseLeftButtonUp += CorrectionCanvas_MouseUp;

                Logger.Debug("MainWindow", "校正点事件已绑定");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"设置校正点事件失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 移除校正点事件
        /// </summary>
        private void RemoveCorrectionPointsEvents()
        {
            try
            {
                CorrectionPoint0.MouseLeftButtonDown -= CorrectionPoint_MouseDown;
                CorrectionPoint1.MouseLeftButtonDown -= CorrectionPoint_MouseDown;
                CorrectionPoint2.MouseLeftButtonDown -= CorrectionPoint_MouseDown;
                CorrectionPoint3.MouseLeftButtonDown -= CorrectionPoint_MouseDown;

                CorrectionCanvas.MouseLeftButtonDown -= CorrectionCanvas_MouseDown;
                CorrectionCanvas.MouseMove -= CorrectionCanvas_MouseMove;
                CorrectionCanvas.MouseLeftButtonUp -= CorrectionCanvas_MouseUp;

                Logger.Debug("MainWindow", "校正点事件已移除");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"移除校正点事件失败: {ex.Message}", ex);
            }
        }

        // ==================== 校正点拖动事件 ====================

        /// <summary>
        /// 校正点鼠标按下事件（修复版）
        /// </summary>
        private void CorrectionPoint_MouseDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (!_isPerspectiveCorrectionMode || _isClosing) return;

                var point = sender as Ellipse;
                if (point == null) return;

                // 确定是哪个点
                if (point == CorrectionPoint0) _draggingPointIndex = 0;
                else if (point == CorrectionPoint1) _draggingPointIndex = 1;
                else if (point == CorrectionPoint2) _draggingPointIndex = 2;
                else if (point == CorrectionPoint3) _draggingPointIndex = 3;
                else _draggingPointIndex = -1;

                if (_draggingPointIndex >= 0)
                {
                    // 检查是否已有鼠标捕获
                    if (Mouse.Captured != null && Mouse.Captured != point)
                    {
                        Mouse.Captured.ReleaseMouseCapture();
                    }

                    // 捕获鼠标
                    if (point.CaptureMouse())
                    {
                        e.Handled = true;
                    }
                    else
                    {
                        Logger.Warning("MainWindow", "鼠标捕获失败");
                        _draggingPointIndex = -1;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"校正点鼠标按下失败: {ex.Message}", ex);
                _draggingPointIndex = -1;
            }
        }

        /// <summary>
        /// 校正画布鼠标按下事件
        /// </summary>
        private void CorrectionCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isPerspectiveCorrectionMode) return;

            // 获取点击位置
            var position = e.GetPosition(CorrectionCanvas);

            // 检查是否点击了校正点（10像素范围内的点击都算）
            for (int i = 0; i < 4; i++)
            {
                var point = _correctionPoints[i];
                var distance = Math.Sqrt(Math.Pow(position.X - point.X, 2) + Math.Pow(position.Y - point.Y, 2));

                if (distance <= 10) // 点击半径10像素内的点
                {
                    _draggingPointIndex = i;

                    // 设置鼠标捕获
                    CorrectionCanvas.CaptureMouse();

                    e.Handled = true;
                    return;
                }
            }

            _draggingPointIndex = -1;
        }

        /// <summary>
        /// 校正画布鼠标移动事件（修复版）
        /// </summary>
        private void CorrectionCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            try
            {
                if (!_isPerspectiveCorrectionMode || _draggingPointIndex == -1 || _isClosing) return;

                var position = e.GetPosition(CorrectionCanvas);

                // 限制点在画布范围内（留出10像素边距）
                position.X = Math.Max(10, Math.Min(CorrectionCanvas.ActualWidth - 10, position.X));
                position.Y = Math.Max(10, Math.Min(CorrectionCanvas.ActualHeight - 10, position.Y));

                // 更新校正点位置
                _correctionPoints[_draggingPointIndex] = position;

                // 更新UI显示
                UpdateCorrectionUI();

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"校正点鼠标移动失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 校正画布鼠标释放事件（修复版）
        /// </summary>
        private void CorrectionCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (!_isPerspectiveCorrectionMode || _isClosing) return;

                // 释放鼠标捕获
                if (_draggingPointIndex >= 0)
                {
                    // 释放校正点的鼠标捕获
                    if (_draggingPointIndex == 0) CorrectionPoint0.ReleaseMouseCapture();
                    else if (_draggingPointIndex == 1) CorrectionPoint1.ReleaseMouseCapture();
                    else if (_draggingPointIndex == 2) CorrectionPoint2.ReleaseMouseCapture();
                    else if (_draggingPointIndex == 3) CorrectionPoint3.ReleaseMouseCapture();

                    // 释放画布的鼠标捕获
                    CorrectionCanvas.ReleaseMouseCapture();

                    _draggingPointIndex = -1;
                    e.Handled = true;
                }

                // 确保鼠标状态正常
                Mouse.OverrideCursor = null;
                Cursor = WinCursors.Arrow;
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"校正点鼠标释放失败: {ex.Message}", ex);
            }
            finally
            {
                _draggingPointIndex = -1;
            }
        }

        // ==================== 校正按钮事件 ====================

        /// <summary>
        /// 应用校正按钮点击事件
        /// </summary>
        private void ApplyCorrectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isPerspectiveCorrectionMode || _originalCorrectionFrame == null) return;

            try
            {
                Logger.Info("MainWindow", "开始应用梯形校正");

                // 1. 获取原始图像尺寸
                double imageWidth = _originalCorrectionFrame.Width;
                double imageHeight = _originalCorrectionFrame.Height;

                if (imageWidth <= 0 || imageHeight <= 0)
                {
                    MessageBox.Show("图像尺寸无效，无法应用校正。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 2. 获取图像显示区域
                Rect imageRect = GetImageRectInVideoArea();

                // 3. 将UI坐标转换为图像坐标
                List<AForge.IntPoint> points = new List<AForge.IntPoint>();
                for (int i = 0; i < 4; i++)
                {
                    double x = (_correctionPoints[i].X - imageRect.X) * imageWidth / imageRect.Width;
                    double y = (_correctionPoints[i].Y - imageRect.Y) * imageHeight / imageRect.Height;

                    // 限制在图像范围内
                    x = Math.Max(0, Math.Min(imageWidth - 1, x));
                    y = Math.Max(0, Math.Min(imageHeight - 1, y));

                    points.Add(new AForge.IntPoint((int)x, (int)y));
                }

                // 4. 创建透视校正过滤器
                var filter = new QuadrilateralTransformation(points, (int)imageWidth, (int)imageHeight);

                // 5. 应用到摄像头管理器
                _cameraManager.SetPerspectiveCorrectionFilter(filter);

                // 6. 保存校正配置
                SaveCorrectionConfig(points, (int)imageWidth, (int)imageHeight);

                // 7. 退出校正模式
                ExitPerspectiveCorrectionMode(true);

                MessageBox.Show("梯形校正已成功应用！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);

                Logger.Info("MainWindow", "梯形校正应用完成");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"应用梯形校正失败: {ex.Message}", ex);
                MessageBox.Show($"应用校正失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 重置校正按钮点击事件
        /// </summary>
        private void ResetCorrectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isPerspectiveCorrectionMode) return;

            Logger.Info("MainWindow", "重置梯形校正点");
            InitializeCorrectionPoints();
        }

        /// <summary>
        /// 取消校正按钮点击事件
        /// </summary>
        private void CancelCorrectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isPerspectiveCorrectionMode) return;

            Logger.Info("MainWindow", "取消梯形校正");
            ExitPerspectiveCorrectionMode(false);
        }

        /// <summary>
        /// 打开梯形校正菜单项点击事件
        /// </summary>
        private void OpenPerspectiveCorrection_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing || _isEnteringCorrectionMode) return;

            // 隐藏更多菜单
            MoreMenuPopup.IsOpen = false;

            // 使用安全方法进入梯形校正模式
            SafeEnterPerspectiveCorrectionMode();
        }

        // ==================== 辅助方法 ====================

        /// <summary>
        /// 获取图像在VideoArea中的显示区域
        /// </summary>
        private Rect GetImageRectInVideoArea()
        {
            var videoImage = (WinImage)FindName("VideoImage");
            var videoArea = (Grid)FindName("VideoArea");
            if (videoImage.Source == null || videoArea == null)
            {
                // 如果没有图像源，返回校正画布的尺寸
                return new Rect(0, 0,
                    CorrectionCanvas?.ActualWidth ?? 800,
                    CorrectionCanvas?.ActualHeight ?? 600);
            }

            try
            {
                double imageWidth = videoImage.Source.Width;
                double imageHeight = videoImage.Source.Height;

                if (imageWidth <= 0 || imageHeight <= 0)
                {
                    return new Rect(0, 0,
                        CorrectionCanvas?.ActualWidth ?? 800,
                        CorrectionCanvas?.ActualHeight ?? 600);
                }

                double aspectRatio = imageWidth / imageHeight;
                double areaWidth = videoArea.ActualWidth;
                double areaHeight = videoArea.ActualHeight;
                double areaAspectRatio = areaWidth / areaHeight;

                double width, height;
                if (aspectRatio > areaAspectRatio)
                {
                    // 宽度受限
                    width = areaWidth;
                    height = areaWidth / aspectRatio;
                }
                else
                {
                    // 高度受限
                    height = areaHeight;
                    width = areaHeight * aspectRatio;
                }

                // 计算居中位置
                double x = (areaWidth - width) / 2;
                double y = (areaHeight - height) / 2;

                return new Rect(x, y, width, height);
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"获取图像显示区域失败: {ex.Message}", ex);
                return new Rect(0, 0,
                    CorrectionCanvas?.ActualWidth ?? 800,
                    CorrectionCanvas?.ActualHeight ?? 600);
            }
        }

        /// <summary>
        /// 保存校正配置
        /// </summary>
        private void SaveCorrectionConfig(List<AForge.IntPoint> points, int sourceWidth, int sourceHeight)
        {
            try
            {
                var cameraIndex = _cameraManager.CurrentCameraIndex;
                var cameraName = _cameraManager.GetCurrentCameraName();

                // 创建或获取相机配置
                if (!config.CameraConfigs.ContainsKey(cameraIndex))
                {
                    config.CameraConfigs[cameraIndex] = new CameraConfig
                    {
                        CameraIndex = cameraIndex,
                        CameraName = cameraName
                    };
                }

                // 更新校正配置
                config.CameraConfigs[cameraIndex].SetCorrectionPoints(points);
                config.CameraConfigs[cameraIndex].SourceWidth = sourceWidth;
                config.CameraConfigs[cameraIndex].SourceHeight = sourceHeight;
                config.CameraConfigs[cameraIndex].OriginalCameraWidth = _originalCorrectionFrame?.Width ?? 0;
                config.CameraConfigs[cameraIndex].OriginalCameraHeight = _originalCorrectionFrame?.Height ?? 0;
                config.CameraConfigs[cameraIndex].HasCorrection = true;

                // 保存配置
                SaveConfig();

                Logger.Info("MainWindow", $"摄像头 {cameraIndex} ({cameraName}) 的校正配置已保存");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"保存校正配置失败: {ex.Message}", ex);
            }
        }

        #endregion

        #region 核心功能方法

        /// <summary>
        /// 拍照功能（修复版）
        /// </summary>
        private void Capture_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing) return;

            if (!_cameraManager.IsCameraAvailable)
            {
                MessageBox.Show("没有可用的摄像头，无法拍照。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Logger.Info("MainWindow", "开始拍照");

            var frame = _cameraManager.GetCurrentFrame();
            if (frame != null)
            {
                try
                {
                    StrokeCollection currentStrokes = new StrokeCollection(_drawingManager.GetStrokes());

                    var bitmapImage = _frameProcessor.ProcessFrameToBitmapImage(frame);
                    if (bitmapImage != null)
                    {
                        var (filePath, fileName) = SavePhotoToDisk(bitmapImage, currentStrokes);
                        
                        if (!string.IsNullOrEmpty(filePath))
                        {
                            _photoPopupManager.AddPhoto(bitmapImage, currentStrokes, filePath);
                            
                            var photoPanelBorder = FindName("PhotoPanelBorder") as Border;
                            if (photoPanelBorder != null)
                            {
                                photoPanelBorder.Visibility = Visibility.Visible;
                                UpdatePhotoButtonState(true);
                                Logger.Debug("MainWindow", "拍照成功，自动展开照片栏");
                            }
                            
                            ShowPhotoTip();
                            _memoryManager.TriggerMemoryCleanup();
                            Logger.Info("MainWindow", $"拍照成功，已保存到: {filePath}");
                        }
                    }
                }
                finally
                {
                    frame.Dispose();
                }
            }
            else
            {
                Logger.Warning("MainWindow", "无法获取摄像头画面进行拍照");
                MessageBox.Show("无法获取摄像头画面。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private (string filePath, string fileName) SavePhotoToDisk(BitmapSource bitmap, StrokeCollection strokes, string prefix = "Photo")
        {
            try
            {
                var dateFolder = DateTime.Now.ToString("yyyy-MM-dd");
                var saveDir = System.IO.Path.Combine(@"D:\EasiCameraPhoto", dateFolder);
                
                if (!System.IO.Directory.Exists(saveDir))
                {
                    System.IO.Directory.CreateDirectory(saveDir);
                }

                var fileName = $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                var filePath = System.IO.Path.Combine(saveDir, fileName);

                SaveImageWithInk(bitmap, strokes, filePath);
                
                return (filePath, fileName);
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"保存照片到硬盘失败: {ex.Message}", ex);
                return (null, null);
            }
        }

        /// <summary>
        /// 扫码功能
        /// </summary>
        private void ScanQRCode_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing) return;

            if (!_cameraManager.IsCameraAvailable)
            {
                MessageBox.Show("没有可用的摄像头，无法使用扫码功能。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Logger.Info("MainWindow", "开始扫码");

            var frame = _cameraManager.GetCurrentFrame();
            if (frame == null) return;

            try
            {
                var result = _frameProcessor.DecodeBarcodeFromBitmap(frame);
                if (result != null)
                {
                    System.Windows.Clipboard.SetText(result.Text ?? string.Empty);
                    Logger.Info("MainWindow", $"扫码成功: {result.BarcodeFormat} - {result.Text}");
                    MessageBox.Show($"识别到：{result.BarcodeFormat}\n{result.Text}\n(已复制到剪贴板)", "扫一扫");
                }
                else
                {
                    Logger.Info("MainWindow", "未检测到二维码/条码");
                    MessageBox.Show("未检测到二维码/条码。", "扫一扫");
                }
            }
            finally
            {
                frame.Dispose();
            }
        }

        #endregion

        #region 颜色选择器相关方法

        /// <summary>
        /// 选择指定的颜色按钮
        /// </summary>
        private void SelectColorButton(string colorName)
        {
            try
            {
                // 隐藏所有对钩
                HideAllCheckIcons();

                // 根据颜色名称找到对应的按钮并显示对钩
                switch (colorName)
                {
                    case "Black":
                        if (CheckIcon_Black != null) CheckIcon_Black.Visibility = Visibility.Visible;
                        _currentSelectedColorButton = GetColorButtonByTag("Black");
                        break;
                    case "Red":
                        if (CheckIcon_Red != null) CheckIcon_Red.Visibility = Visibility.Visible;
                        _currentSelectedColorButton = GetColorButtonByTag("Red");
                        break;
                    case "Green":
                        if (CheckIcon_Green != null) CheckIcon_Green.Visibility = Visibility.Visible;
                        _currentSelectedColorButton = GetColorButtonByTag("Green");
                        break;
                    case "Blue":
                        if (CheckIcon_Blue != null) CheckIcon_Blue.Visibility = Visibility.Visible;
                        _currentSelectedColorButton = GetColorButtonByTag("Blue");
                        break;
                    case "Yellow":
                        if (CheckIcon_Yellow != null) CheckIcon_Yellow.Visibility = Visibility.Visible;
                        _currentSelectedColorButton = GetColorButtonByTag("Yellow");
                        break;
                    case "White":
                        if (CheckIcon_White != null) CheckIcon_White.Visibility = Visibility.Visible;
                        _currentSelectedColorButton = GetColorButtonByTag("White");
                        break;
                    case "Orange":
                        if (CheckIcon_Orange != null) CheckIcon_Orange.Visibility = Visibility.Visible;
                        _currentSelectedColorButton = GetColorButtonByTag("Orange");
                        break;
                    case "Purple":
                        if (CheckIcon_Purple != null) CheckIcon_Purple.Visibility = Visibility.Visible;
                        _currentSelectedColorButton = GetColorButtonByTag("Purple");
                        break;
                    case "Cyan":
                        if (CheckIcon_Cyan != null) CheckIcon_Cyan.Visibility = Visibility.Visible;
                        _currentSelectedColorButton = GetColorButtonByTag("Cyan");
                        break;
                    case "Magenta":
                        if (CheckIcon_Magenta != null) CheckIcon_Magenta.Visibility = Visibility.Visible;
                        _currentSelectedColorButton = GetColorButtonByTag("Magenta");
                        break;
                    case "Brown":
                        if (CheckIcon_Brown != null) CheckIcon_Brown.Visibility = Visibility.Visible;
                        _currentSelectedColorButton = GetColorButtonByTag("Brown");
                        break;
                    case "Pink":
                        if (CheckIcon_Pink != null) CheckIcon_Pink.Visibility = Visibility.Visible;
                        _currentSelectedColorButton = GetColorButtonByTag("Pink");
                        break;
                    case "Gray":
                        if (CheckIcon_Gray != null) CheckIcon_Gray.Visibility = Visibility.Visible;
                        _currentSelectedColorButton = GetColorButtonByTag("Gray");
                        break;
                    case "DarkRed":
                        if (CheckIcon_DarkRed != null) CheckIcon_DarkRed.Visibility = Visibility.Visible;
                        _currentSelectedColorButton = GetColorButtonByTag("DarkRed");
                        break;
                    case "DarkGreen":
                        if (CheckIcon_DarkGreen != null) CheckIcon_DarkGreen.Visibility = Visibility.Visible;
                        _currentSelectedColorButton = GetColorButtonByTag("DarkGreen");
                        break;
                    case "DarkBlue":
                        if (CheckIcon_DarkBlue != null) CheckIcon_DarkBlue.Visibility = Visibility.Visible;
                        _currentSelectedColorButton = GetColorButtonByTag("DarkBlue");
                        break;
                    case "Gold":
                        if (CheckIcon_Gold != null) CheckIcon_Gold.Visibility = Visibility.Visible;
                        _currentSelectedColorButton = GetColorButtonByTag("Gold");
                        break;
                    case "Silver":
                        if (CheckIcon_Silver != null) CheckIcon_Silver.Visibility = Visibility.Visible;
                        _currentSelectedColorButton = GetColorButtonByTag("Silver");
                        break;
                    case "Lime":
                        if (CheckIcon_Lime != null) CheckIcon_Lime.Visibility = Visibility.Visible;
                        _currentSelectedColorButton = GetColorButtonByTag("Lime");
                        break;
                    case "Teal":
                        if (CheckIcon_Teal != null) CheckIcon_Teal.Visibility = Visibility.Visible;
                        _currentSelectedColorButton = GetColorButtonByTag("Teal");
                        break;
                    default:
                        if (CheckIcon_Black != null) CheckIcon_Black.Visibility = Visibility.Visible;
                        _currentSelectedColorButton = GetColorButtonByTag("Black");
                        break;
                }

                // 更新当前画笔颜色
                _currentPenColor = colorName;
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"选择颜色按钮失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 隐藏所有对钩图标
        /// </summary>
        private void HideAllCheckIcons()
        {
            if (CheckIcon_Black != null) CheckIcon_Black.Visibility = Visibility.Collapsed;
            if (CheckIcon_Red != null) CheckIcon_Red.Visibility = Visibility.Collapsed;
            if (CheckIcon_Green != null) CheckIcon_Green.Visibility = Visibility.Collapsed;
            if (CheckIcon_Blue != null) CheckIcon_Blue.Visibility = Visibility.Collapsed;
            if (CheckIcon_Yellow != null) CheckIcon_Yellow.Visibility = Visibility.Collapsed;
            if (CheckIcon_White != null) CheckIcon_White.Visibility = Visibility.Collapsed;
            if (CheckIcon_Orange != null) CheckIcon_Orange.Visibility = Visibility.Collapsed;
            if (CheckIcon_Purple != null) CheckIcon_Purple.Visibility = Visibility.Collapsed;
            if (CheckIcon_Cyan != null) CheckIcon_Cyan.Visibility = Visibility.Collapsed;
            if (CheckIcon_Magenta != null) CheckIcon_Magenta.Visibility = Visibility.Collapsed;
            if (CheckIcon_Brown != null) CheckIcon_Brown.Visibility = Visibility.Collapsed;
            if (CheckIcon_Pink != null) CheckIcon_Pink.Visibility = Visibility.Collapsed;
            if (CheckIcon_Gray != null) CheckIcon_Gray.Visibility = Visibility.Collapsed;
            if (CheckIcon_DarkRed != null) CheckIcon_DarkRed.Visibility = Visibility.Collapsed;
            if (CheckIcon_DarkGreen != null) CheckIcon_DarkGreen.Visibility = Visibility.Collapsed;
            if (CheckIcon_DarkBlue != null) CheckIcon_DarkBlue.Visibility = Visibility.Collapsed;
            if (CheckIcon_Gold != null) CheckIcon_Gold.Visibility = Visibility.Collapsed;
            if (CheckIcon_Silver != null) CheckIcon_Silver.Visibility = Visibility.Collapsed;
            if (CheckIcon_Lime != null) CheckIcon_Lime.Visibility = Visibility.Collapsed;
            if (CheckIcon_Teal != null) CheckIcon_Teal.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 根据Tag获取颜色按钮
        /// </summary>
        private Button GetColorButtonByTag(string tag)
        {
            if (PenSettingsPopup == null || PenSettingsPopup.Child == null) return null;

            var border = PenSettingsPopup.Child as Border;
            if (border == null) return null;

            var stackPanel = border.Child as StackPanel;
            if (stackPanel == null) return null;

            var grid = stackPanel.Children.OfType<Grid>().FirstOrDefault();
            if (grid == null) return null;

            // 在Grid中查找具有指定Tag的按钮
            foreach (UIElement child in grid.Children)
            {
                if (child is Button button && button.Tag?.ToString() == tag)
                {
                    return button;
                }
            }

            return null;
        }

        /// <summary>
        /// 根据颜色名称获取颜色
        /// </summary>
        private System.Windows.Media.Color GetColorFromName(string colorName)
        {
            switch (colorName)
            {
                case "Black": return System.Windows.Media.Colors.Black;
                case "Red": return System.Windows.Media.Colors.Red;
                case "Green": return System.Windows.Media.Colors.Green;
                case "Blue": return System.Windows.Media.Colors.Blue;
                case "Yellow": return System.Windows.Media.Colors.Yellow;
                case "White": return System.Windows.Media.Colors.White;
                case "Orange": return System.Windows.Media.Colors.Orange;
                case "Purple": return System.Windows.Media.Colors.Purple;
                case "Cyan": return System.Windows.Media.Colors.Cyan;
                case "Magenta": return System.Windows.Media.Colors.Magenta;
                case "Brown": return System.Windows.Media.Colors.Brown;
                case "Pink": return System.Windows.Media.Colors.Pink;
                case "Gray": return System.Windows.Media.Colors.Gray;
                case "DarkRed": return System.Windows.Media.Colors.DarkRed;
                case "DarkGreen": return System.Windows.Media.Colors.DarkGreen;
                case "DarkBlue": return System.Windows.Media.Colors.DarkBlue;
                case "Gold": return System.Windows.Media.Colors.Gold;
                case "Silver": return System.Windows.Media.Colors.Silver;
                case "Lime": return System.Windows.Media.Colors.Lime;
                case "Teal": return System.Windows.Media.Colors.Teal;
                default: return System.Windows.Media.Colors.Black;
            }
        }

        /// <summary>
        /// 初始化画笔颜色选择器
        /// </summary>
        private void InitializePenColorSelector()
        {
            try
            {
                var defaultColorHex = config?.DefaultPenColor ?? "#FF000000";
                var colorName = GetColorNameFromHex(defaultColorHex);

                SelectColorButton(colorName);
                _currentPenColor = colorName;

                // 将配置中的默认颜色应用到画笔
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(defaultColorHex);
                _drawingManager.SetPenColor(color);

                Logger.Debug("MainWindow", $"画笔颜色选择器初始化完成，颜色: {defaultColorHex}");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"初始化画笔颜色选择器失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 根据颜色十六进制值获取颜色名称
        /// </summary>
        private string GetColorNameFromHex(string colorHex)
        {
            try
            {
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex);
                var colorNameMap = new Dictionary<string, System.Windows.Media.Color>
                {
                    { "Black", System.Windows.Media.Colors.Black },
                    { "Red", System.Windows.Media.Colors.Red },
                    { "Green", System.Windows.Media.Colors.Green },
                    { "Blue", System.Windows.Media.Colors.Blue },
                    { "Yellow", System.Windows.Media.Colors.Yellow },
                    { "White", System.Windows.Media.Colors.White },
                    { "Orange", System.Windows.Media.Colors.Orange },
                    { "Purple", System.Windows.Media.Colors.Purple },
                    { "Cyan", System.Windows.Media.Colors.Cyan },
                    { "Magenta", System.Windows.Media.Colors.Magenta },
                    { "Brown", System.Windows.Media.Colors.Brown },
                    { "Pink", System.Windows.Media.Colors.Pink },
                    { "Gray", System.Windows.Media.Colors.Gray },
                    { "DarkRed", System.Windows.Media.Colors.DarkRed },
                    { "DarkGreen", System.Windows.Media.Colors.DarkGreen },
                    { "DarkBlue", System.Windows.Media.Colors.DarkBlue },
                    { "Gold", System.Windows.Media.Colors.Gold },
                    { "Silver", System.Windows.Media.Colors.Silver },
                    { "Lime", System.Windows.Media.Colors.Lime },
                    { "Teal", System.Windows.Media.Colors.Teal }
                };

                foreach (var pair in colorNameMap)
                {
                    if (pair.Value.A == color.A && pair.Value.R == color.R && pair.Value.G == color.G && pair.Value.B == color.B)
                    {
                        return pair.Key;
                    }
                }
            }
            catch
            {
            }
            return "Black";
        }

        #endregion

        #region 悬浮窗事件处理

        /// <summary>
        /// 画笔设置悬浮窗打开事件
        /// </summary>
        private void PenSettingsPopup_Opened(object sender, EventArgs e)
        {
            try
            {
                // 确保画笔按钮保持选中状态
                if (_drawingManager.CurrentMode == DrawingManager.ToolMode.Pen)
                {
                    PenBtn.IsChecked = true;
                }

                // 更新颜色选择器的选中状态
                if (!string.IsNullOrEmpty(_currentPenColor))
                {
                    SelectColorButton(_currentPenColor);
                }

                Logger.Debug("MainWindow", "画笔设置悬浮窗已打开");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"画笔设置悬浮窗打开事件失败: {ex.Message}", ex);
            }
        }

        private void PenSettingsPopup_Closed(object sender, EventArgs e)
        {
            try
            {
                // 连带关闭"更多颜色"悬浮窗：MoreColorsPopup 的 PlacementTarget 位于本窗内部，
                // 若仅关闭本窗而留下它，会形成一个 PlacementTarget 已隐藏的悬空 Popup，
                // 导致画笔选色窗无法再次打开。
                if (MoreColorsPopup != null && MoreColorsPopup.IsOpen)
                {
                    MoreColorsPopup.IsOpen = false;
                }

                // 确保画笔按钮保持选中状态
                if (_drawingManager.CurrentMode == DrawingManager.ToolMode.Pen)
                {
                    PenBtn.IsChecked = true;
                }

                Logger.Debug("MainWindow", "画笔设置悬浮窗已关闭");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"画笔设置悬浮窗关闭事件失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 更多菜单弹出窗口关闭事件
        /// </summary>
        private void MoreMenuPopup_Closed(object sender, EventArgs e)
        {
            Logger.Debug("MainWindow", "更多菜单弹出窗口已关闭");
        }

        #endregion

        #region 其他事件处理方法



        /// <summary>
        /// 文档扫描功能
        /// </summary>
        private void ScanDocument_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing) return;

            if (!_cameraManager.IsCameraAvailable)
            {
                MessageBox.Show("没有可用的摄像头，无法使用文档扫描功能。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Logger.Info("MainWindow", "开始文档扫描");

            var frame = _cameraManager.GetCurrentFrame();
            if (frame == null) return;

            System.Drawing.Bitmap processed = null;
            try
            {
                processed = _frameProcessor.ProcessDocumentScan(frame);
                if (processed != null)
                {
                    var bitmapImage = _memoryManager.BitmapToBitmapImage(processed);
                    var strokes = new StrokeCollection(_drawingManager.GetStrokes());
                    
                    var (filePath, fileName) = SavePhotoToDisk(bitmapImage, strokes, "Scan");
                    if (!string.IsNullOrEmpty(filePath))
                    {
                        _photoPopupManager.AddPhoto(bitmapImage, strokes, filePath);
                        ShowPhotoTip();
                        _memoryManager.TriggerMemoryCleanup();
                        Logger.Info("MainWindow", $"文档扫描完成，已保存到: {filePath}");
                    }
                }
            }
            finally
            {
                frame.Dispose();
                processed?.Dispose();
            }
        }

        /// <summary>
        /// 保存图片功能
        /// </summary>
        private void SaveImage_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing) return;

            var currentPhoto = _photoPopupManager.CurrentPhoto;
            if (currentPhoto == null)
            {
                MessageBox.Show("请先拍照或选择一张图片。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Logger.Info("MainWindow", "开始保存图片");

            // 使用WPF的SaveFileDialog
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PNG 图片|*.png|JPEG 图片|*.jpg",
                FileName = $"Capture_{DateTime.Now:yyyyMMdd_HHmmss}.png"
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    // 保存包含批注的图片
                    SaveImageWithInk(currentPhoto.Image, currentPhoto.Strokes, dlg.FileName);
                    Logger.Info("MainWindow", $"图片保存成功: {dlg.FileName}");
                    MessageBox.Show("保存成功！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    Logger.Error("MainWindow", $"保存图片失败: {ex.Message}", ex);
                    MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private bool _isSaveSelectionMode = false;

        private void SavePhoto_Click(object sender, RoutedEventArgs e)
        {
            if (_isSaveSelectionMode)
            {
                SaveSelectedPhotos();
            }
            else
            {
                EnterSaveSelectionMode();
            }
        }

        private void EnterSaveSelectionMode()
        {
            _isSaveSelectionMode = true;

            var defaultButtonsPanel = FindName("DefaultButtonsPanel") as StackPanel;
            var selectModeButtonsPanel = FindName("SelectModeButtonsPanel") as StackPanel;
            var photoList = FindName("PhotoList") as ListBox;

            if (defaultButtonsPanel != null)
            {
                defaultButtonsPanel.Visibility = Visibility.Collapsed;
            }

            if (selectModeButtonsPanel != null)
            {
                selectModeButtonsPanel.Visibility = Visibility.Visible;
            }

            if (photoList != null)
            {
                photoList.Tag = "Visible";
                Logger.Debug("MainWindow", $"进入保存选择模式，photoList.Tag = {photoList.Tag}");
                
                // 强制刷新列表项
                var items = photoList.Items;
                var collectionView = System.Windows.Data.CollectionViewSource.GetDefaultView(items);
                collectionView.Refresh();
            }

            Logger.Debug("MainWindow", "进入保存选择模式");
        }

        private void SaveSelectedPhotos()
        {
            var selectedPhotos = _photoPopupManager.GetPhotos().Where(p => p.IsSelected).ToList();

            if (selectedPhotos.Count == 0)
            {
                MessageBox.Show("请先选择要保存的照片。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PNG 图片|*.png|JPEG 图片|*.jpg",
                FileName = $"Photos_{DateTime.Now:yyyyMMdd_HHmmss}.png"
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    foreach (var photo in selectedPhotos)
                    {
                        string fileName = dlg.FileName.Replace(".png", $"_{photo.Index}.png").Replace(".jpg", $"_{photo.Index}.jpg");
                        SaveImageWithInk(photo.Image, photo.Strokes, fileName);
                    }

                    Logger.Info("MainWindow", $"保存了 {selectedPhotos.Count} 张照片");
                    MessageBox.Show($"保存成功！共保存 {selectedPhotos.Count} 张照片。", "成功", MessageBoxButton.OK, MessageBoxImage.Information);

                    ExitSaveSelectionMode();
                }
                catch (Exception ex)
                {
                    Logger.Error("MainWindow", $"保存照片失败: {ex.Message}", ex);
                    MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void ExitSaveSelectionMode()
        {
            _isSaveSelectionMode = false;

            var defaultButtonsPanel = FindName("DefaultButtonsPanel") as StackPanel;
            var selectModeButtonsPanel = FindName("SelectModeButtonsPanel") as StackPanel;
            var photoList = FindName("PhotoList") as ListBox;

            if (defaultButtonsPanel != null)
            {
                defaultButtonsPanel.Visibility = Visibility.Visible;
            }

            if (selectModeButtonsPanel != null)
            {
                selectModeButtonsPanel.Visibility = Visibility.Collapsed;
            }

            if (photoList != null)
            {
                photoList.Tag = "Collapsed";
                photoList.SelectedIndex = -1;
                Logger.Debug("MainWindow", $"退出保存选择模式，photoList.Tag = {photoList.Tag}");

                var photos = _photoPopupManager.GetPhotos();
                foreach (var photo in photos)
                {
                    photo.IsSelected = false;
                }
            }

            _photoPopupManager.BackToLive();

            Logger.Debug("MainWindow", "退出保存选择模式");
        }

        private void InvertSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isSaveSelectionMode)
            {
                return;
            }

            var photos = _photoPopupManager.GetPhotos();
            foreach (var photo in photos)
            {
                photo.IsSelected = !photo.IsSelected;
            }
        }

        private void ImportPhoto_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing) return;

            Logger.Info("MainWindow", "开始导入图片");

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.svg|SVG 矢量图|*.svg|PNG 图片|*.png|JPEG 图片|*.jpg;*.jpeg|BMP 图片|*.bmp|GIF 图片|*.gif",
                Title = "选择要导入的图片",
                Multiselect = true
            };

            if (dlg.ShowDialog() == true)
            {
                ImportPhotosFromFiles(dlg.FileNames);
            }
        }

        /// <summary>
        /// 从文件路径数组导入图片（支持命令行参数调用）
        /// </summary>
        /// <param name="filePaths">图片文件路径数组</param>
        public void ImportPhotosFromFiles(string[] filePaths)
        {
            if (_isClosing || filePaths == null || filePaths.Length == 0)
                return;

            Logger.Info("MainWindow", $"开始导入 {filePaths.Length} 个图片文件");

            // 特殊情况：仅导入一份 SVG 笔迹，且当前正选中一张照片时，
            // 将该笔迹按比例校准后追加到当前照片（而不是创建新照片）
            if (filePaths.Length == 1
                && System.IO.Path.GetExtension(filePaths[0])?.Equals(".svg", StringComparison.OrdinalIgnoreCase) == true
                && _photoPopupManager?.CurrentPhoto != null
                && _photoPopupManager.CurrentPhoto.Image != null)
            {
                string svgPath = filePaths[0];
                if (System.IO.File.Exists(svgPath))
                {
                    if (ImportSvgToCurrentPhoto(svgPath))
                    {
                        Logger.Info("MainWindow", $"SVG 笔迹已校准并追加到当前选中照片: {svgPath}");
                        return;
                    }
                    // 校准追加失败时回退到常规导入流程
                    Logger.Warning("MainWindow", "SVG 笔迹追加到当前照片失败，回退到常规导入流程");
                }
            }

            int successCount = 0;
            int failCount = 0;

            foreach (var filePath in filePaths)
            {
                // 检查文件是否存在
                if (!System.IO.File.Exists(filePath))
                {
                    failCount++;
                    Logger.Error("MainWindow", $"文件不存在: {filePath}", null);
                    continue;
                }

                // 检查文件扩展名是否为图片
                string extension = System.IO.Path.GetExtension(filePath).ToLower();
                string[] supportedExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".svg" };
                if (!supportedExtensions.Contains(extension))
                {
                    failCount++;
                    Logger.Error("MainWindow", $"不支持的文件格式: {filePath}", null);
                    continue;
                }

                try
                {
                    // SVG 文件：解析为可编辑的笔迹
                    if (extension == ".svg")
                    {
                        if (ImportSvgFile(filePath))
                        {
                            successCount++;
                            Logger.Info("MainWindow", $"SVG 导入成功: {filePath}");
                        }
                        else
                        {
                            failCount++;
                        }
                        continue;
                    }

                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(filePath);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();

                    _photoPopupManager.AddPhoto(bitmap, null, filePath);
                    successCount++;
                    Logger.Info("MainWindow", $"图片导入成功: {filePath}");
                }
                catch (Exception ex)
                {
                    failCount++;
                    Logger.Error("MainWindow", $"导入图片失败: {filePath}, {ex.Message}", ex);
                }
            }

            // 显示结果（仅在失败时显示）
            if (failCount > 0)
            {
                MessageBox.Show($"成功导入 {successCount} 张图片，失败 {failCount} 张", "导入结果", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (successCount > 0)
            {
                Logger.Info("MainWindow", $"所有图片导入成功，共 {successCount} 张");
            }

            // 如果通过文件关联打开，默认展示第一张图片并打开照片栏
            if (_isOpenedFromFile && successCount > 0)
            {
                Dispatcher.Invoke(() =>
                {
                    // 打开照片栏
                    var photoPanelBorder = FindName("PhotoPanelBorder") as Border;
                    if (photoPanelBorder != null && photoPanelBorder.Visibility != Visibility.Visible)
                    {
                        photoPanelBorder.Visibility = Visibility.Visible;
                        UpdatePhotoButtonState(true);
                        Logger.Info("MainWindow", "文件关联打开，自动展开照片栏");
                    }

                    // 获取第一张照片并设置选中状态
                    var photos = _photoPopupManager.GetPhotos();
                    if (photos != null && photos.Count > 0)
                    {
                        var firstPhoto = photos[0];
                        // 设置照片为选中状态（显示阴影遮罩和"再次点击 返回实时"文字）
                        firstPhoto.IsSelected = true;
                        // 同时让 ListBox 选中该项，触发选中视觉效果
                        var photoList = FindName("PhotoList") as ListBox;
                        if (photoList != null)
                        {
                            photoList.SelectedItem = firstPhoto;
                        }
                        // 显示图片
                        DisplayPhotoWithoutSelection(firstPhoto);
                        Logger.Info("MainWindow", $"文件关联打开，自动选中并显示第一张照片: {firstPhoto.FilePath}");
                    }
                });
            }
        }

        /// <summary>
        /// 判断文件路径是否为受支持的图片格式
        /// </summary>
        private static bool IsSupportedImageFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return false;

            string extension = System.IO.Path.GetExtension(filePath).ToLower();
            string[] supportedExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".svg" };
            return supportedExtensions.Contains(extension);
        }

        /// <summary>
        /// 解析 SVG 文件，提取宽高、笔迹和填充图片（不创建新照片）
        /// </summary>
        private bool TryParseSvgFile(string filePath, out int width, out int height, out StrokeCollection strokes, out List<(BitmapSource bitmap, double x, double y, double w, double h)> fillBitmaps, out List<(string pathData, System.Windows.Media.Color color)> fillPaths)
        {
            width = 1920;
            height = 1080;
            strokes = new StrokeCollection();
            fillBitmaps = new List<(BitmapSource, double, double, double, double)>();
            fillPaths = new List<(string, System.Windows.Media.Color)>();

            try
            {
                var doc = System.Xml.Linq.XDocument.Load(filePath);
                var root = doc.Root;
                if (root == null)
                {
                    Logger.Error("MainWindow", $"SVG 文件为空: {filePath}", null);
                    return false;
                }

                // 从 <svg> 元素读取宽高
                width = ParseSvgLength(root.Attribute("width")?.Value, 0);
                height = ParseSvgLength(root.Attribute("height")?.Value, 0);

                // 若未指定宽高，则使用 viewBox
                if (width <= 0 || height <= 0)
                {
                    var viewBox = root.Attribute("viewBox")?.Value;
                    if (!string.IsNullOrEmpty(viewBox))
                    {
                        var parts = viewBox.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 4)
                        {
                            if (width <= 0) width = ParseSvgLength(parts[2], 0);
                            if (height <= 0) height = ParseSvgLength(parts[3], 0);
                        }
                    }
                }
                if (width <= 0) width = 1920;
                if (height <= 0) height = 1080;

                // 解析所有 <path> 元素为笔迹
                var pathElements = root.Descendants().Where(e => e.Name.LocalName == "path").ToList();
                Logger.Info("MainWindow", $"SVG 包含 {pathElements.Count} 个 path 元素");

                // 解析所有 <image> 元素（油漆桶填充图片等位图内容）
                var imageElements = root.Descendants().Where(e => e.Name.LocalName == "image").ToList();
                Logger.Info("MainWindow", $"SVG 包含 {imageElements.Count} 个 image 元素");

                foreach (var imgElem in imageElements)
                {
                    try
                    {
                        var invariant = System.Globalization.CultureInfo.InvariantCulture;
                        double ix = 0, iy = 0, iw = 0, ih = 0;
                        double.TryParse(imgElem.Attribute("x")?.Value, System.Globalization.NumberStyles.Float, invariant, out ix);
                        double.TryParse(imgElem.Attribute("y")?.Value, System.Globalization.NumberStyles.Float, invariant, out iy);
                        double.TryParse(imgElem.Attribute("width")?.Value, System.Globalization.NumberStyles.Float, invariant, out iw);
                        double.TryParse(imgElem.Attribute("height")?.Value, System.Globalization.NumberStyles.Float, invariant, out ih);

                        // 支持 href 和 xlink:href
                        var href = imgElem.Attribute("href")?.Value
                                   ?? imgElem.Attribute(System.Xml.Linq.XNamespace.Get("http://www.w3.org/1999/xlink") + "href")?.Value;
                        if (string.IsNullOrEmpty(href)) continue;

                        BitmapSource bmp = null;
                        if (href.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                        {
                            // data URI: data:image/png;base64,XXXX
                            var commaIdx = href.IndexOf(',');
                            if (commaIdx >= 0 && commaIdx < href.Length - 1)
                            {
                                var base64 = href.Substring(commaIdx + 1);
                                var bytes = Convert.FromBase64String(base64);
                                var img = new BitmapImage();
                                img.BeginInit();
                                img.StreamSource = new MemoryStream(bytes);
                                img.CacheOption = BitmapCacheOption.OnLoad;
                                img.EndInit();
                                img.Freeze();
                                bmp = img;
                            }
                        }

                        if (bmp == null) continue;
                        if (iw <= 0) iw = bmp.PixelWidth;
                        if (ih <= 0) ih = bmp.PixelHeight;
                        fillBitmaps.Add((bmp, ix, iy, iw, ih));
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning("MainWindow", $"解析 SVG image 元素失败: {ex.Message}");
                    }
                }

                foreach (var pathElem in pathElements)
                {
                    var d = pathElem.Attribute("d")?.Value;
                    if (string.IsNullOrEmpty(d)) continue;

                    var invariant = System.Globalization.CultureInfo.InvariantCulture;

                    // 识别矢量填充路径（由 ExportStrokesAsSvg 导出的油漆桶填充）
                    var swFillAttr = pathElem.Attribute("data-sw-fill")?.Value;
                    var fillAttrValue = pathElem.Attribute("fill")?.Value;
                    var strokeAttrValue = pathElem.Attribute("stroke")?.Value;
                    bool hasNoStroke = string.IsNullOrEmpty(strokeAttrValue) || strokeAttrValue == "none";
                    bool hasFill = !string.IsNullOrEmpty(fillAttrValue) && fillAttrValue != "none";
                    bool isFillPath = swFillAttr == "1" || (hasFill && hasNoStroke);

                    if (isFillPath)
                    {
                        try
                        {
                            var fillColor = ParseSvgColor(fillAttrValue ?? "#000000", Colors.Black);
                            // 保存矢量 pathData（不再光栅化为位图，保持矢量）
                            fillPaths.Add((d, fillColor));
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning("MainWindow", $"保存矢量填充路径失败: {ex.Message}");
                        }
                        continue;
                    }

                    // 解析颜色/不透明度/线宽：优先 stroke 属性，回退到 fill（兼容旧格式或外部填充式 SVG）
                    System.Windows.Media.Color strokeColor = Colors.Black;
                    double opacity = 1.0;
                    double strokeWidth = 1.5;

                    var strokeAttr = pathElem.Attribute("stroke")?.Value;
                    bool hasStroke = !string.IsNullOrEmpty(strokeAttr) && strokeAttr != "none";
                    if (hasStroke)
                    {
                        strokeColor = ParseSvgColor(strokeAttr, strokeColor);
                    }

                    var strokeWidthAttr = pathElem.Attribute("stroke-width")?.Value;
                    if (!string.IsNullOrEmpty(strokeWidthAttr) && double.TryParse(strokeWidthAttr, System.Globalization.NumberStyles.Float, invariant, out double sw))
                    {
                        strokeWidth = sw;
                    }

                    var strokeOpacityAttr = pathElem.Attribute("stroke-opacity")?.Value;
                    if (!string.IsNullOrEmpty(strokeOpacityAttr) && double.TryParse(strokeOpacityAttr, System.Globalization.NumberStyles.Float, invariant, out double so))
                    {
                        opacity = so;
                    }

                    // 若无 stroke 属性，回退到 fill 属性
                    if (!hasStroke)
                    {
                        var fillAttr = pathElem.Attribute("fill")?.Value;
                        if (!string.IsNullOrEmpty(fillAttr) && fillAttr != "none")
                        {
                            strokeColor = ParseSvgColor(fillAttr, strokeColor);
                        }
                        var fillOpacityAttr = pathElem.Attribute("fill-opacity")?.Value;
                        if (!string.IsNullOrEmpty(fillOpacityAttr) && double.TryParse(fillOpacityAttr, System.Globalization.NumberStyles.Float, invariant, out double fo))
                        {
                            opacity = fo;
                        }
                    }

                    // 解析 style 内联属性
                    var styleAttr = pathElem.Attribute("style")?.Value;
                    if (!string.IsNullOrEmpty(styleAttr))
                    {
                        if (hasStroke)
                        {
                            var strokeMatch = System.Text.RegularExpressions.Regex.Match(styleAttr, @"stroke\s*:\s*([^;]+)");
                            if (strokeMatch.Success && strokeMatch.Groups[1].Value.Trim() != "none")
                            {
                                strokeColor = ParseSvgColor(strokeMatch.Groups[1].Value.Trim(), strokeColor);
                            }
                            var swMatch = System.Text.RegularExpressions.Regex.Match(styleAttr, @"stroke-width\s*:\s*([^;]+)");
                            if (swMatch.Success && double.TryParse(swMatch.Groups[1].Value.Trim(), System.Globalization.NumberStyles.Float, invariant, out double sw2))
                            {
                                strokeWidth = sw2;
                            }
                            var soMatch = System.Text.RegularExpressions.Regex.Match(styleAttr, @"stroke-opacity\s*:\s*([^;]+)");
                            if (soMatch.Success && double.TryParse(soMatch.Groups[1].Value.Trim(), System.Globalization.NumberStyles.Float, invariant, out double so2))
                            {
                                opacity = so2;
                            }
                        }
                        else
                        {
                            var fillMatch = System.Text.RegularExpressions.Regex.Match(styleAttr, @"fill\s*:\s*([^;]+)");
                            if (fillMatch.Success)
                            {
                                strokeColor = ParseSvgColor(fillMatch.Groups[1].Value.Trim(), strokeColor);
                            }
                            var opacityMatch = System.Text.RegularExpressions.Regex.Match(styleAttr, @"fill-opacity\s*:\s*([^;]+)");
                            if (opacityMatch.Success && double.TryParse(opacityMatch.Groups[1].Value.Trim(), System.Globalization.NumberStyles.Float, invariant, out double op2))
                            {
                                opacity = op2;
                            }
                        }
                    }

                    var pathStrokes = CreateStrokesFromSvgPath(d, strokeColor, opacity, strokeWidth);
                    foreach (Stroke s in pathStrokes)
                    {
                        strokes.Add(s);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"解析 SVG 失败: {filePath}, {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// 导入 SVG 矢量图文件，将其中路径解析为可编辑的笔迹（作为新照片加入照片栏）
        /// </summary>
        private bool ImportSvgFile(string filePath)
        {
            if (!TryParseSvgFile(filePath, out int width, out int height, out var strokes, out var fillBitmaps, out var fillPaths))
                return false;

            // 创建透明背景的占位图
            var placeholder = CreateTransparentPlaceholder(width, height);

            // 仅将位图填充图片渲染到占位图（笔迹作为矢量加载到 InkCanvas，不渲染为位图，避免出现"位图+矢量"双图）
            if (fillBitmaps.Count > 0)
            {
                placeholder = RenderFillImagesToBitmap(width, height, fillBitmaps);
            }

            // 在 AddPhoto 之前预先构造好新照片的矢量填充快照。
            // 原因：AddPhoto 内部会调用 _drawingManager.GetFillPathsSnapshot() 自动快照当前 InkCanvas 上的填充，
            // 但此时 InkCanvas 上还没添加本次 SVG 的填充（AddFillPath 在下方才调用），
            // 自动快照会捕获到错误状态（空或上一张照片的填充）。这里直接从 SVG 解析结果构造正确的快照。
            var newFillPaths = new List<DrawingManager.FillPathRecord>();
            if (fillPaths != null && fillPaths.Count > 0)
            {
                foreach (var fp in fillPaths)
                {
                    try
                    {
                        var geometry = System.Windows.Media.PathGeometry.CreateFromGeometry(
                            System.Windows.Media.Geometry.Parse(fp.pathData));
                        if (geometry == null) continue;
                        if (geometry.CanFreeze) geometry.Freeze();
                        newFillPaths.Add(new DrawingManager.FillPathRecord { Geometry = geometry, Color = fp.color });
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning("MainWindow", $"构造矢量填充快照失败: {ex.Message}");
                    }
                }
            }

            _photoPopupManager.AddPhoto(placeholder, strokes, filePath);

            // 覆盖 AddPhoto 内部捕获的（可能错误的）填充快照，确保新照片持有的是本次 SVG 的填充
            var newPhoto = _photoPopupManager?.CurrentPhoto;
            if (newPhoto != null)
            {
                newPhoto.FillPaths = newFillPaths;
                // 位图填充已直接渲染进 placeholder.Image，无需单独保存 FillImages 快照
                newPhoto.FillImages = null;
            }

            // 矢量填充 Path 也加载到 InkCanvas（位于笔画下方，保持矢量）
            if (fillPaths != null && fillPaths.Count > 0)
            {
                foreach (var fp in fillPaths)
                {
                    _drawingManager?.AddFillPath(fp.pathData, fp.color, 1.0, 1.0);
                }
            }
            return true;
        }

        /// <summary>
        /// 将 SVG 笔迹校准后追加到当前选中照片（不创建新照片）。
        /// 校准：将笔迹从 SVG 坐标空间 (svgWidth × svgHeight) 缩放到当前照片像素空间 (photoW × photoH)。
        /// </summary>
        private bool ImportSvgToCurrentPhoto(string filePath)
        {
            var currentPhoto = _photoPopupManager?.CurrentPhoto;
            if (currentPhoto == null || currentPhoto.Image == null)
            {
                Logger.Warning("MainWindow", "无法导入笔迹到当前照片：当前没有选中照片");
                return false;
            }

            if (!TryParseSvgFile(filePath, out int svgWidth, out int svgHeight, out var strokes, out var fillBitmaps, out var fillPaths))
                return false;

            if ((strokes == null || strokes.Count == 0) && (fillBitmaps == null || fillBitmaps.Count == 0) && (fillPaths == null || fillPaths.Count == 0))
            {
                Logger.Warning("MainWindow", $"SVG 文件中没有可导入的笔迹或填充: {filePath}");
                return false;
            }

            int photoW = currentPhoto.Image.PixelWidth;
            int photoH = currentPhoto.Image.PixelHeight;
            if (photoW <= 0) photoW = svgWidth;
            if (photoH <= 0) photoH = svgHeight;

            // 校准：将 SVG 坐标（照片像素空间）反向缩放到目标 InkCanvas DIP 空间
            // 导出端把 InkCanvas DIP 缩放到照片像素空间，导入时需做反向缩放以恢复 InkCanvas DIP
            var ink = (InkCanvas)FindName("Ink");
            double inkW = ink?.ActualWidth ?? 0;
            double inkH = ink?.ActualHeight ?? 0;
            double scaleX = (inkW > 0 && svgWidth > 0) ? inkW / svgWidth : 1.0;
            double scaleY = (inkH > 0 && svgHeight > 0) ? inkH / svgHeight : 1.0;
            if (double.IsNaN(scaleX) || double.IsInfinity(scaleX) || scaleX <= 0) scaleX = 1.0;
            if (double.IsNaN(scaleY) || double.IsInfinity(scaleY) || scaleY <= 0) scaleY = 1.0;

            bool needScale = Math.Abs(scaleX - 1.0) > 1e-6 || Math.Abs(scaleY - 1.0) > 1e-6;

            // 追加填充图片（校准位置与尺寸，位于笔画下方）
            if (fillBitmaps != null && fillBitmaps.Count > 0)
            {
                foreach (var fb in fillBitmaps)
                {
                    double fx = fb.x * scaleX;
                    double fy = fb.y * scaleY;
                    double fw = fb.w * scaleX;
                    double fh = fb.h * scaleY;
                    _drawingManager?.AddFillImage(fb.bitmap, fx, fy, fw, fh);
                }
            }

            // 追加矢量填充 Path（位于笔画下方，按 SVG -> InkCanvas 缩放）
            if (fillPaths != null && fillPaths.Count > 0)
            {
                foreach (var fp in fillPaths)
                {
                    _drawingManager?.AddFillPath(fp.pathData, fp.color, scaleX, scaleY);
                }
            }

            // 追加笔迹（校准后）
            if (strokes != null && strokes.Count > 0)
            {
                if (needScale)
                {
                    strokes = ScaleStrokes(strokes, scaleX, scaleY);
                }
                _drawingManager?.AddStrokes(strokes);
            }

            // 保存快照到当前照片以持久化
            currentPhoto.Strokes = _drawingManager?.GetStrokes() ?? new StrokeCollection();
            // 同步快照矢量/位图填充，使切换照片或回看时填充不再丢失
            currentPhoto.FillPaths = _drawingManager?.GetFillPathsSnapshot() ?? new List<DrawingManager.FillPathRecord>();
            currentPhoto.FillImages = _drawingManager?.GetFillImagesSnapshot() ?? new List<DrawingManager.FillImageRecord>();
            // 同步更新 InkCanvas 尺寸，便于后续切换/回看时按比例缩放对齐
            var inkSize3 = _drawingManager?.GetInkCanvasSize() ?? new System.Windows.Size(0, 0);
            currentPhoto.OriginInkWidth = inkSize3.Width;
            currentPhoto.OriginInkHeight = inkSize3.Height;

            Logger.Info("MainWindow", $"已将笔迹/填充校准后追加到当前照片 (SVG {svgWidth}x{svgHeight} -> 照片 {photoW}x{photoH}, 笔迹 {strokes?.Count ?? 0} 条, 位图填充 {fillBitmaps?.Count ?? 0} 张, 矢量填充 {fillPaths?.Count ?? 0} 条): {filePath}");
            return true;
        }

        /// <summary>
        /// 按比例缩放笔迹坐标及线宽（用于将笔迹从一个坐标系映射到另一个坐标系）
        /// </summary>
        private static StrokeCollection ScaleStrokes(StrokeCollection strokes, double scaleX, double scaleY)
        {
            var result = new StrokeCollection();
            // 线宽按面积守恒缩放，保持视觉比例
            double widthScale = Math.Sqrt(scaleX * scaleY);
            if (double.IsNaN(widthScale) || double.IsInfinity(widthScale) || widthScale <= 0)
                widthScale = 1.0;

            foreach (Stroke stroke in strokes)
            {
                var srcPoints = stroke.StylusPoints;
                if (srcPoints == null || srcPoints.Count == 0) continue;

                var newPoints = new StylusPointCollection();
                for (int i = 0; i < srcPoints.Count; i++)
                {
                    var p = srcPoints[i];
                    newPoints.Add(new StylusPoint(p.X * scaleX, p.Y * scaleY, p.PressureFactor));
                }

                var newStroke = new Stroke(newPoints);
                var attrs = stroke.DrawingAttributes.Clone();
                attrs.Width = Math.Max(0.1, attrs.Width * widthScale);
                attrs.Height = Math.Max(0.1, attrs.Height * widthScale);
                newStroke.DrawingAttributes = attrs;
                result.Add(newStroke);
            }
            return result;
        }

        /// <summary>
        /// 将 SVG path data 转换为可编辑的 StrokeCollection
        /// 直接解析 path data，不使用 Geometry.Parse，避免 WPF 几何解析导致的点丢失/简化
        /// </summary>
        private StrokeCollection CreateStrokesFromSvgPath(string pathData, System.Windows.Media.Color color, double opacity, double strokeWidth)
        {
            var result = new StrokeCollection();
            try
            {
                var invariant = System.Globalization.CultureInfo.InvariantCulture;

                // 用正则提取命令字母和数字
                var tokenRegex = new System.Text.RegularExpressions.Regex(
                    @"[MLHVZCQSTAcmlhvzcqsta]|[-+]?\d*\.?\d+(?:[eE][-+]?\d+)?");
                var tokens = tokenRegex.Matches(pathData)
                    .Cast<System.Text.RegularExpressions.Match>()
                    .Select(m => m.Value)
                    .ToList();

                if (tokens.Count == 0) return result;

                bool isHighlighter = opacity < 0.7;
                double w = Math.Max(0.1, strokeWidth);

                var currentPoints = new List<WinPoint>();
                char command = 'M';
                double curX = 0, curY = 0;
                double subpathStartX = 0, subpathStartY = 0;
                bool isFirstCommand = true;
                int i = 0;

                // 从 tokens[i] 解析数字（使用不变文化，避免逗号小数点问题）
                double Num(int idx)
                {
                    if (idx < tokens.Count && double.TryParse(tokens[idx], System.Globalization.NumberStyles.Float, invariant, out double v))
                        return v;
                    return 0;
                }

                void FlushPoints()
                {
                    if (currentPoints.Count >= 2)
                    {
                        var stylusPoints = new StylusPointCollection(currentPoints);
                        var stroke = new Stroke(stylusPoints);
                        stroke.DrawingAttributes = new DrawingAttributes
                        {
                            Color = color,
                            Width = w,
                            Height = w,
                            StylusTip = StylusTip.Ellipse,
                            IsHighlighter = isHighlighter,
                            FitToCurve = true,
                            IgnorePressure = true
                        };
                        result.Add(stroke);
                    }
                    currentPoints.Clear();
                }

                while (i < tokens.Count)
                {
                    // 判断当前 token 是否为命令字母
                    if (tokens[i].Length == 1 && char.IsLetter(tokens[i][0]))
                    {
                        command = tokens[i][0];
                        i++;
                    }

                    bool isRelative = char.IsLower(command) && !isFirstCommand;
                    char cmd = char.ToUpper(command);

                    switch (cmd)
                    {
                        case 'M':
                            // 开始新子路径
                            if (currentPoints.Count > 0)
                                FlushPoints();

                            curX = Num(i);
                            curY = Num(i + 1);
                            i += 2;
                            // 第一个 M/m 始终视为绝对坐标
                            if (isRelative && !isFirstCommand)
                            {
                                curX += subpathStartX;
                                curY += subpathStartY;
                            }
                            subpathStartX = curX;
                            subpathStartY = curY;
                            currentPoints.Add(new WinPoint(curX, curY));
                            // M 后续隐式为 L
                            command = char.IsLower(command) ? 'l' : 'L';
                            break;

                        case 'L':
                            {
                                double x = Num(i);
                                double y = Num(i + 1);
                                i += 2;
                                if (isRelative) { curX += x; curY += y; }
                                else { curX = x; curY = y; }
                                currentPoints.Add(new WinPoint(curX, curY));
                                break;
                            }

                        case 'H':
                            {
                                double x = Num(i);
                                i += 1;
                                if (isRelative) curX += x;
                                else curX = x;
                                currentPoints.Add(new WinPoint(curX, curY));
                                break;
                            }

                        case 'V':
                            {
                                double y = Num(i);
                                i += 1;
                                if (isRelative) curY += y;
                                else curY = y;
                                currentPoints.Add(new WinPoint(curX, curY));
                                break;
                            }

                        case 'Z':
                            if (currentPoints.Count > 0)
                            {
                                currentPoints.Add(new WinPoint(subpathStartX, subpathStartY));
                                curX = subpathStartX;
                                curY = subpathStartY;
                            }
                            FlushPoints();
                            break;

                        // 曲线命令：跳过参数（本应用导出的 path 仅含 M/L，此处仅为兼容外部 SVG）
                        case 'C':
                            i += 6;
                            break;
                        case 'S':
                            i += 4;
                            break;
                        case 'Q':
                            i += 4;
                            break;
                        case 'T':
                            i += 2;
                            break;
                        case 'A':
                            i += 7;
                            break;

                        default:
                            i++;
                            break;
                    }

                    isFirstCommand = false;
                }

                FlushPoints();
            }
            catch (Exception ex)
            {
                Logger.Warning("MainWindow", $"解析 SVG path 失败: {pathData}, 错误: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// 解析 SVG 颜色字符串（#RRGGBB、#RGB、#AARRGGBB、rgb(r,g,b)、命名颜色）
        /// </summary>
        private static System.Windows.Media.Color ParseSvgColor(string colorStr, System.Windows.Media.Color defaultValue)
        {
            if (string.IsNullOrEmpty(colorStr)) return defaultValue;
            colorStr = colorStr.Trim();
            try
            {
                if (colorStr.StartsWith("#"))
                {
                    string hex = colorStr.Substring(1);
                    if (hex.Length == 6)
                    {
                        byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                        byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                        byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                        return System.Windows.Media.Color.FromRgb(r, g, b);
                    }
                    else if (hex.Length == 3)
                    {
                        byte r = Convert.ToByte(new string(hex[0], 2), 16);
                        byte g = Convert.ToByte(new string(hex[1], 2), 16);
                        byte b = Convert.ToByte(new string(hex[2], 2), 16);
                        return System.Windows.Media.Color.FromRgb(r, g, b);
                    }
                    else if (hex.Length == 8)
                    {
                        byte a = Convert.ToByte(hex.Substring(0, 2), 16);
                        byte r = Convert.ToByte(hex.Substring(2, 2), 16);
                        byte g = Convert.ToByte(hex.Substring(4, 2), 16);
                        byte b = Convert.ToByte(hex.Substring(6, 2), 16);
                        return System.Windows.Media.Color.FromArgb(a, r, g, b);
                    }
                }
                else if (colorStr.StartsWith("rgb("))
                {
                    var inner = colorStr.Substring(4).TrimEnd(')');
                    var parts = inner.Split(',');
                    if (parts.Length >= 3)
                    {
                        byte r = byte.Parse(parts[0].Trim());
                        byte g = byte.Parse(parts[1].Trim());
                        byte b = byte.Parse(parts[2].Trim());
                        return System.Windows.Media.Color.FromRgb(r, g, b);
                    }
                }
                else if (colorStr.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                         colorStr.Equals("transparent", StringComparison.OrdinalIgnoreCase))
                {
                    return Colors.Transparent;
                }
                else
                {
                    // 命名颜色
                    var colorObj = System.Windows.Media.ColorConverter.ConvertFromString(colorStr);
                    if (colorObj is System.Windows.Media.Color c) return c;
                }
            }
            catch
            {
                // 解析失败时使用默认值
            }
            return defaultValue;
        }

        /// <summary>
        /// 解析 SVG 长度值（去除 px/pt/mm/cm/in 等单位）
        /// </summary>
        private static int ParseSvgLength(string value, int defaultValue)
        {
            if (string.IsNullOrEmpty(value)) return defaultValue;
            value = value.Trim();
            value = System.Text.RegularExpressions.Regex.Replace(value, @"[^\d.]", "");
            if (double.TryParse(value, out double result))
            {
                return (int)Math.Round(result);
            }
            return defaultValue;
        }

        /// <summary>
        /// 创建一个透明背景的占位图（用于承载导入的 SVG 笔迹）
        /// </summary>
        private static BitmapSource CreateTransparentPlaceholder(int width, int height)
        {
            if (width <= 0) width = 1920;
            if (height <= 0) height = 1080;

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                // 不绘制背景 - 保持透明，只承载笔迹
                context.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0, 0, width, height));
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>
        /// 仅将 SVG 中的填充图片渲染到位图（笔迹不渲染，保留为矢量加载到 InkCanvas）
        /// </summary>
        private static BitmapSource RenderFillImagesToBitmap(int width, int height,
            List<(BitmapSource bitmap, double x, double y, double w, double h)> fillBitmaps)
        {
            if (width <= 0) width = 1920;
            if (height <= 0) height = 1080;

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                // 透明背景
                context.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0, 0, width, height));

                // 绘制填充图片
                if (fillBitmaps != null)
                {
                    foreach (var item in fillBitmaps)
                    {
                        try
                        {
                            var rect = new Rect(item.x, item.y, item.w, item.h);
                            context.DrawImage(item.bitmap, rect);
                        }
                        catch
                        {
                            // 忽略单张图片绘制失败
                        }
                    }
                }
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>
        /// 拖拽进入窗口时设置拖放效果
        /// </summary>
        private void Window_DragEnter(object sender, System.Windows.DragEventArgs e)
        {
            SetDragDropEffect(e);
        }

        /// <summary>
        /// 拖拽在窗口内移动时维持拖放效果
        /// </summary>
        private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            SetDragDropEffect(e);
        }

        /// <summary>
        /// 判断拖放数据是否可被识别为图片来源
        /// </summary>
        /// <remarks>
        /// 支持本地文件（FileDrop）、位图（Bitmap）、FileContents（浏览器拖拽的图片字节）、
        /// URL（UniformResourceLocator/Text）、HTML。
        /// 注意：DragEnter/DragOver 阶段不可调用 GetData 读取内容（外部进程拖入会返回 null），
        /// 仅用 GetDataPresent 判断格式是否存在。
        /// </remarks>
        private static bool IsImageDropSupported(System.Windows.IDataObject data)
        {
            if (data == null) return false;
            return data.GetDataPresent(System.Windows.DataFormats.FileDrop)
                || data.GetDataPresent(System.Windows.DataFormats.Bitmap)
                || data.GetDataPresent("FileContents")
                || data.GetDataPresent("FileGroupDescriptor")
                || data.GetDataPresent("UniformResourceLocator")
                || data.GetDataPresent("UniformResourceLocatorW")
                || data.GetDataPresent(System.Windows.DataFormats.Html)
                || data.GetDataPresent(System.Windows.DataFormats.Text)
                || data.GetDataPresent(System.Windows.DataFormats.UnicodeText);
        }

        /// <summary>
        /// 根据拖放数据类型设置拖放效果
        /// </summary>
        private void SetDragDropEffect(System.Windows.DragEventArgs e)
        {
            e.Effects = IsImageDropSupported(e.Data)
                ? System.Windows.DragDropEffects.Copy
                : System.Windows.DragDropEffects.None;
            e.Handled = true;
        }

        /// <summary>
        /// 释放拖拽的数据，导入其中的图片并展开照片栏
        /// </summary>
        private void Window_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (_isClosing) return;

            if (!IsImageDropSupported(e.Data))
            {
                e.Effects = System.Windows.DragDropEffects.None;
                e.Handled = true;
                return;
            }

            // 拖放导入不触发文件关联的自动展示行为
            bool wasOpenedFromFile = _isOpenedFromFile;
            _isOpenedFromFile = false;
            try
            {
                bool imported = false;

                // 1) 本地文件
                if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
                {
                    var files = e.Data.GetData(System.Windows.DataFormats.FileDrop) as string[];
                    var imageFiles = files?.Where(IsSupportedImageFile).ToArray();
                    if (imageFiles != null && imageFiles.Length > 0)
                    {
                        Logger.Info("MainWindow", $"通过拖放导入 {imageFiles.Length} 个图片文件");
                        ImportPhotosFromFiles(imageFiles);
                        imported = true;
                    }
                }

                // 2) FileContents（浏览器拖拽时通常包含真实图片字节流）
                if (!imported && e.Data.GetDataPresent("FileContents"))
                {
                    try
                    {
                        var bitmapSource = TryReadImageFromData(e.Data, "FileContents");
                        if (bitmapSource != null)
                        {
                            _photoPopupManager.AddPhoto(bitmapSource, null, null);
                            Logger.Info("MainWindow", "通过拖放导入 FileContents 图片");
                            imported = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("MainWindow", $"从 FileContents 导入失败: {ex.Message}", ex);
                    }
                }

                // 3) 位图（从某些应用/网页直接拖出的位图数据）
                if (!imported && e.Data.GetDataPresent(System.Windows.DataFormats.Bitmap))
                {
                    try
                    {
                        var bmp = e.Data.GetData(System.Windows.DataFormats.Bitmap);
                        BitmapSource bitmapSource = ConvertToBitmapSource(bmp);
                        if (bitmapSource != null)
                        {
                            if (!bitmapSource.IsFrozen && bitmapSource.CanFreeze) bitmapSource.Freeze();
                            _photoPopupManager.AddPhoto(bitmapSource, null, null);
                            Logger.Info("MainWindow", "通过拖放导入位图图片");
                            imported = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("MainWindow", $"从位图导入失败: {ex.Message}", ex);
                    }
                }

                // 4) URL（网页图片链接）
                if (!imported)
                {
                    string url = TryGetUrlFromData(e.Data);
                    if (!string.IsNullOrEmpty(url))
                    {
                        Logger.Info("MainWindow", $"通过拖放从 URL 导入图片: {url}");
                        ImportPhotoFromUrl(url);
                        imported = true;
                    }
                }

                if (imported)
                {
                    ExpandPhotoPanel();
                    e.Effects = System.Windows.DragDropEffects.Copy;
                }
                else
                {
                    e.Effects = System.Windows.DragDropEffects.None;
                }
            }
            finally
            {
                _isOpenedFromFile = wasOpenedFromFile;
            }
            e.Handled = true;
        }

        /// <summary>
        /// 从指定数据格式中尝试读取为图片字节并解码为 BitmapSource。
        /// 优先用 WPF 解码，失败时回退到 Magick.NET（支持更多格式，如 WebP）。
        /// </summary>
        private static BitmapSource TryReadImageFromData(System.Windows.IDataObject data, string format)
        {
            byte[] bytes = ReadBytesFromData(data, format);
            if (bytes == null || bytes.Length == 0) return null;

            // 先尝试 WPF 内置解码
            BitmapSource source = TryDecodeWithWpf(bytes);
            if (source != null) return source;

            // 回退到 Magick.NET
            return TryDecodeWithMagick(bytes);
        }

        /// <summary>
        /// 从 IDataObject 指定格式读取字节（支持 Stream 和字节数组）
        /// </summary>
        private static byte[] ReadBytesFromData(System.Windows.IDataObject data, string format)
        {
            try
            {
                var raw = data.GetData(format);
                if (raw is byte[] arr) return arr;
                if (raw is System.IO.Stream stream)
                {
                    using (var ms = new System.IO.MemoryStream())
                    {
                        stream.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"读取 {format} 数据失败: {ex.Message}", ex);
            }
            return null;
        }

        /// <summary>
        /// 使用 WPF BitmapImage 解码图片字节
        /// </summary>
        private static BitmapSource TryDecodeWithWpf(byte[] bytes)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = new System.IO.MemoryStream(bytes);
                bitmap.EndInit();
                if (bitmap.CanFreeze) bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex)
            {
                Logger.Debug("MainWindow", $"WPF 解码失败，将尝试 Magick: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 使用 Magick.NET 解码图片字节（支持 WebP 等更多格式）。
        /// 通过 PNG 字节流中转，避免依赖 Magick.NET 的 WPF 扩展包。
        /// </summary>
        private static BitmapSource TryDecodeWithMagick(byte[] bytes)
        {
            try
            {
                using var magickImage = new ImageMagick.MagickImage(bytes);
                // 写出为 PNG 字节，再用 WPF 解码
                using var ms = new System.IO.MemoryStream();
                magickImage.Write(ms, ImageMagick.MagickFormat.Png);
                byte[] pngBytes = ms.ToArray();

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = new System.IO.MemoryStream(pngBytes);
                bitmap.EndInit();
                if (bitmap.CanFreeze) bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"Magick 解码失败: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// 将拖放数据中的位图对象转换为 WPF BitmapSource
        /// </summary>
        private static BitmapSource ConvertToBitmapSource(object data)
        {
            if (data is BitmapSource bs) return bs;
            if (data is System.Drawing.Bitmap drawingBitmap)
            {
                var hbitmap = drawingBitmap.GetHbitmap();
                try
                {
                    var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        hbitmap, IntPtr.Zero, Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
                finally
                {
                    // 释放 GDI 资源
                    DeleteObject(hbitmap);
                }
            }
            return null;
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        /// <summary>
        /// 从拖放数据中尝试提取图片 URL。
        /// 优先从 HTML 中提取 &lt;img src="..."&gt;（网页拖拽时这是真实的图片地址），
        /// UniformResourceLocator 通常返回页面 URL 而非图片 URL，仅在看起来像图片时使用。
        /// </summary>
        private static string TryGetUrlFromData(System.Windows.IDataObject data)
        {
            // 1) 从 HTML 中提取 <img src="...">，这是网页拖拽时最可靠的图片地址来源
            if (data.GetDataPresent(System.Windows.DataFormats.Html))
            {
                string html = data.GetData(System.Windows.DataFormats.Html) as string;
                string imgSrc = ExtractImageUrlFromHtml(html);
                if (!string.IsNullOrEmpty(imgSrc)) return imgSrc;
            }

            // 2) UniformResourceLocator（仅当看起来像图片 URL 时使用）
            foreach (string format in new[] { "UniformResourceLocatorW", "UniformResourceLocator" })
            {
                if (data.GetDataPresent(format))
                {
                    string url = ReadUrlFromData(data, format);
                    if (IsLikelyImageUrl(url)) return url;
                }
            }

            // 3) 纯文本 URL（仅当看起来像图片 URL 时使用）
            foreach (string format in new[] { System.Windows.DataFormats.UnicodeText, System.Windows.DataFormats.Text })
            {
                if (data.GetDataPresent(format))
                {
                    string text = data.GetData(format) as string;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        string trimmed = text.Trim();
                        if (Uri.TryCreate(trimmed, UriKind.Absolute, out _) && IsLikelyImageUrl(trimmed))
                            return trimmed;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// 从 IDataObject 中按指定格式读取 URL 字符串（处理 MemoryStream 与字符串两种返回形式）
        /// </summary>
        private static string ReadUrlFromData(System.Windows.IDataObject data, string format)
        {
            try
            {
                var raw = data.GetData(format);
                if (raw is string s) return s.Trim('\0', ' ', '\r', '\n');
                if (raw is System.IO.Stream stream)
                {
                    using (var reader = new System.IO.StreamReader(stream, System.Text.Encoding.Unicode, true))
                    {
                        return reader.ReadToEnd().Trim('\0', ' ', '\r', '\n');
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 粗略判断 URL 是否指向图片
        /// </summary>
        private static bool IsLikelyImageUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            string lower = url.ToLowerInvariant();
            string[] exts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tiff", ".ico" };
            return exts.Any(ext => lower.Contains(ext));
        }

        /// <summary>
        /// 从 HTML 中提取第一张图片的 src 地址
        /// </summary>
        private static string ExtractImageUrlFromHtml(string html)
        {
            if (string.IsNullOrEmpty(html)) return null;
            try
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    html, "<img[^>]+src\\s*=\\s*[\"'](?<src>[^\"']+)[\"']",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups["src"].Value;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 异步从 URL 下载图片并导入。
        /// 校验响应 Content-Type 是否为图片，避免把 HTML 误当作图片解码。
        /// WPF 解码失败时回退到 Magick.NET。
        /// </summary>
        private async void ImportPhotoFromUrl(string url)
        {
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    Logger.Warning("MainWindow", $"无效的图片 URL: {url}");
                    ShowImportFailure($"无效的图片地址：{url}");
                    return;
                }

                using var client = new System.Net.Http.HttpClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                // 模拟浏览器避免部分网站拒绝下载
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ShowWriteAir");
                client.DefaultRequestHeaders.Add("Referer", $"{uri.Scheme}://{uri.Host}/");

                // 使用 GetAsync 以便检查响应头中的 Content-Type
                using var response = await client.GetAsync(uri, System.Net.Http.HttpCompletionOption.ResponseContentRead).ConfigureAwait(true);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Warning("MainWindow", $"下载图片失败，HTTP 状态: {response.StatusCode}, URL: {url}");
                    ShowImportFailure($"下载失败，HTTP 状态：{response.StatusCode}");
                    return;
                }

                string contentType = response.Content?.Headers?.ContentType?.MediaType?.ToLowerInvariant() ?? string.Empty;
                byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(true);
                if (bytes == null || bytes.Length == 0)
                {
                    Logger.Warning("MainWindow", $"下载的图片为空: {url}");
                    ShowImportFailure("下载到的数据为空");
                    return;
                }

                // 如果 Content-Type 表明是 HTML/文本，说明 URL 并非图片地址
                if (contentType.Contains("text/html") || contentType.Contains("text/plain") || contentType.Contains("application/json"))
                {
                    Logger.Warning("MainWindow", $"URL 返回非图片内容（{contentType}）: {url}");
                    ShowImportFailure($"地址返回的不是图片（{contentType}），请确认拖拽的是图片本身");
                    return;
                }

                // 先用 WPF 解码，失败则用 Magick.NET
                BitmapSource bitmap = TryDecodeWithWpf(bytes);
                if (bitmap == null)
                {
                    bitmap = TryDecodeWithMagick(bytes);
                }

                if (bitmap == null)
                {
                    Logger.Error("MainWindow", $"无法解码图片，Content-Type={contentType}, 字节数={bytes.Length}, URL={url}", null);
                    ShowImportFailure("无法识别该图片格式");
                    return;
                }

                _photoPopupManager.AddPhoto(bitmap, null, url);
                Logger.Info("MainWindow", $"从 URL 导入图片成功: {url}");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"从 URL 导入图片失败: {url}, {ex.Message}", ex);
                ShowImportFailure($"从网页导入图片失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 在 UI 线程上显示导入失败提示
        /// </summary>
        private void ShowImportFailure(string message)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                MessageBox.Show(message, "导入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }));
        }

        /// <summary>
        /// 展开照片栏（若已展开则不做处理）
        /// </summary>
        private void ExpandPhotoPanel()
        {
            try
            {
                var photoPanelBorder = FindName("PhotoPanelBorder") as Border;
                if (photoPanelBorder != null && photoPanelBorder.Visibility != Visibility.Visible)
                {
                    photoPanelBorder.Visibility = Visibility.Visible;
                    UpdatePhotoButtonState(true);
                    Logger.Info("MainWindow", "拖放导入后自动展开照片栏");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"展开照片栏失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 显示照片但不触发选中状态（用于文件关联打开）
        /// </summary>
        /// <param name="photo">要显示的照片</param>
        private void DisplayPhotoWithoutSelection(PhotoWithStrokes photo)
        {
            if (photo == null || photo.Image == null)
                return;

            // 设置到非实时模式
            _isLiveMode = false;

            // 显示照片
            var videoImage = (WinImage)FindName("VideoImage");
            var videoArea = (Grid)FindName("VideoArea");
            if (videoImage != null)
            {
                videoImage.Source = photo.Image;
            }
            if (videoArea != null)
            {
                videoArea.Background = WinBrushes.Transparent;
            }

            // 切换到照片对应的笔迹（按 origin 尺寸做坐标缩放，解决窗口尺寸变化导致的笔迹与照片错位）
            // 同时还原矢量/位图填充，并按同一 scaleX/scaleY 缩放以保持对齐
            if (photo.Strokes != null)
            {
                _drawingManager.SwitchToPhotoStrokes(photo.Strokes, photo.OriginInkWidth, photo.OriginInkHeight,
                    photo.FillPaths, photo.FillImages);
            }
            else
            {
                _drawingManager.SwitchToPhotoStrokes(new StrokeCollection(), 0, 0, photo.FillPaths, photo.FillImages);
            }

            // 更新UI状态
            UpdateUIModeForPhotoView();

            Logger.Debug("MainWindow", $"已显示照片（无选中状态）: {photo.FilePath}");
        }

        /// <summary>
        /// 保存图片时包含批注
        /// </summary>
        private void SaveImageWithInk(BitmapSource originalImage, StrokeCollection strokes, string filePath)
        {
            if (strokes == null || strokes.Count == 0)
            {
                // 如果没有批注，直接保存原图
                _frameProcessor.SaveBitmapSourceToFile(originalImage, filePath);
                return;
            }

            // 创建包含批注的视觉对象
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                // 绘制原始图片
                context.DrawImage(originalImage, new Rect(0, 0, originalImage.PixelWidth, originalImage.PixelHeight));
                // 绘制批注
                foreach (var stroke in strokes)
                {
                    var geometry = stroke.GetGeometry(stroke.DrawingAttributes);
                    var brush = new SolidColorBrush(stroke.DrawingAttributes.Color);
                    context.DrawGeometry(brush, null, geometry);
                }
            }

            // 渲染为位图
            var renderBitmap = new RenderTargetBitmap(
                originalImage.PixelWidth,
                originalImage.PixelHeight,
                originalImage.DpiX,
                originalImage.DpiY,
                PixelFormats.Pbgra32);
            renderBitmap.Render(visual);

            // 保存到文件
            _frameProcessor.SaveBitmapSourceToFile(renderBitmap, filePath);
        }

        /// <summary>
        /// 导出笔迹（不包含背景图像）
        /// 支持 SVG 矢量图 和 PNG 透明背景
        /// </summary>
        private void ExportStrokes_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing) return;

            var currentPhoto = _photoPopupManager.CurrentPhoto;
            if (currentPhoto == null || currentPhoto.Strokes == null || currentPhoto.Strokes.Count == 0)
            {
                MessageBox.Show("当前照片没有可导出的笔迹。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Logger.Info("MainWindow", "开始导出笔迹");

            // 使用 SaveFileDialog，可用格式包含 SVG 矢量图 和 PNG 透明背景
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "SVG 矢量图|*.svg|PNG 透明背景|*.png",
                FileName = $"Strokes_{DateTime.Now:yyyyMMdd_HHmmss}.svg"
            };

            if (dlg.ShowDialog() != true) return;

            try
            {
                int width = currentPhoto.Image?.PixelWidth ?? 1920;
                int height = currentPhoto.Image?.PixelHeight ?? 1080;

                string ext = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant();
                if (ext == ".svg")
                {
                    var fillImages = _drawingManager?.GetFillImages() ?? new List<WinImage>();
                    // 计算 InkCanvas DIP 坐标空间 -> 照片像素空间的缩放比例
                    // 笔迹/填充坐标存于 InkCanvas DIP 空间，SVG viewBox 是照片像素空间，需统一缩放
                    var ink = (InkCanvas)FindName("Ink");
                    double inkW = ink?.ActualWidth ?? 0;
                    double inkH = ink?.ActualHeight ?? 0;
                    double coordScaleX = (inkW > 0) ? width / inkW : 1.0;
                    double coordScaleY = (inkH > 0) ? height / inkH : 1.0;
                    if (double.IsNaN(coordScaleX) || double.IsInfinity(coordScaleX) || coordScaleX <= 0) coordScaleX = 1.0;
                    if (double.IsNaN(coordScaleY) || double.IsInfinity(coordScaleY) || coordScaleY <= 0) coordScaleY = 1.0;
                    ExportStrokesAsSvg(currentPhoto.Strokes, dlg.FileName, width, height, fillImages, coordScaleX, coordScaleY);
                }
                else // png
                {
                    ExportStrokesAsPng(currentPhoto.Strokes, dlg.FileName, width, height);
                }

                Logger.Info("MainWindow", $"笔迹导出成功: {dlg.FileName}");
                MessageBox.Show("导出成功！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"导出笔迹失败: {ex.Message}", ex);
                MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 将笔迹导出为 SVG 矢量图（不包含背景图像）
        /// 使用笔迹中心线(StylusPoints) + stroke/stroke-width，保证导出后可正确还原为可编辑笔迹
        /// 油漆桶填充图片以 base64 PNG 形式嵌入为 SVG &lt;image&gt; 元素
        /// </summary>
        private void ExportStrokesAsSvg(StrokeCollection strokes, string filePath, int width, int height, List<WinImage> fillImages = null,
            double coordScaleX = 1.0, double coordScaleY = 1.0)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\">");

            // 导出油漆桶矢量填充 Path（位于笔画下方）：直接输出 Path.Data 多边形点
            var fillPaths = _drawingManager?.GetFillPaths() ?? new List<System.Windows.Shapes.Path>();
            foreach (var fp in fillPaths)
            {
                try
                {
                    if (fp.Data == null) continue;

                    // Path 在 InkCanvas 中位置（默认 0,0）
                    double left = InkCanvas.GetLeft(fp);
                    double top = InkCanvas.GetTop(fp);
                    if (double.IsNaN(left)) left = 0;
                    if (double.IsNaN(top)) top = 0;
                    left *= coordScaleX;
                    top *= coordScaleY;

                    // 提取填充颜色
                    System.Windows.Media.Color color = Colors.Black;
                    if (fp.Fill is System.Windows.Media.SolidColorBrush fillBrush && fillBrush.Color != null)
                        color = fillBrush.Color;
                    string fillColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}";

                    // 将 Geometry 转为 PathGeometry 提取多边形点
                    var pg = fp.Data as System.Windows.Media.PathGeometry
                             ?? System.Windows.Media.PathGeometry.CreateFromGeometry(fp.Data);
                    if (pg == null) continue;

                    var pathBuilder = new StringBuilder();
                    foreach (var fig in pg.Figures)
                    {
                        var pts = ExtractFigurePoints(fig);
                        if (pts.Count < 3) continue;
                        pathBuilder.Append($"M {(pts[0].X * coordScaleX + left):F2} {(pts[0].Y * coordScaleY + top):F2}");
                        for (int i = 1; i < pts.Count; i++)
                        {
                            pathBuilder.Append($" L {(pts[i].X * coordScaleX + left):F2} {(pts[i].Y * coordScaleY + top):F2}");
                        }
                        pathBuilder.Append(" Z");
                    }

                    if (pathBuilder.Length > 0)
                    {
                        sb.AppendLine($"  <path d=\"{pathBuilder}\" fill=\"{fillColor}\" fill-rule=\"evenodd\" stroke=\"none\" data-sw-fill=\"1\"/>");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning("MainWindow", $"导出矢量填充路径失败: {ex.Message}");
                }
            }

            // 兼容旧位图填充（若仍有残留）
            if (fillImages != null && fillImages.Count > 0)
            {
                foreach (var img in fillImages)
                {
                    try
                    {
                        var bitmap = img.Source as BitmapSource;
                        if (bitmap == null) continue;

                        double left = InkCanvas.GetLeft(img);
                        double top = InkCanvas.GetTop(img);
                        if (double.IsNaN(left)) left = 0;
                        if (double.IsNaN(top)) top = 0;
                        left *= coordScaleX;
                        top *= coordScaleY;

                        var color = GetFillColorFromBitmap(bitmap);
                        var polygons = TraceFillBitmapToPolygons(bitmap, color);
                        if (polygons.Count == 0) continue;

                        string fillColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}";

                        var pathBuilder = new StringBuilder();
                        foreach (var polygon in polygons)
                        {
                            var simplified = SimplifyPolygon(polygon, 1.0);
                            if (simplified.Count < 3) continue;
                            pathBuilder.Append($"M {simplified[0].X * coordScaleX + left:F2} {simplified[0].Y * coordScaleY + top:F2}");
                            for (int i = 1; i < simplified.Count; i++)
                            {
                                pathBuilder.Append($" L {simplified[i].X * coordScaleX + left:F2} {simplified[i].Y * coordScaleY + top:F2}");
                            }
                            pathBuilder.Append(" Z");
                        }

                        if (pathBuilder.Length > 0)
                        {
                            sb.AppendLine($"  <path d=\"{pathBuilder}\" fill=\"{fillColor}\" fill-rule=\"evenodd\" stroke=\"none\" data-sw-fill=\"1\"/>");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning("MainWindow", $"导出填充图片失败: {ex.Message}");
                    }
                }
            }

            foreach (Stroke stroke in strokes)
            {
                var attrs = stroke.DrawingAttributes;
                var points = stroke.StylusPoints;
                if (points == null || points.Count < 2) continue;

                // 使用中心线点构建路径数据（缩放到照片像素坐标空间）
                var pathBuilder = new StringBuilder();
                pathBuilder.Append($"M {points[0].X * coordScaleX:F2} {points[0].Y * coordScaleY:F2}");
                for (int i = 1; i < points.Count; i++)
                {
                    pathBuilder.Append($" L {points[i].X * coordScaleX:F2} {points[i].Y * coordScaleY:F2}");
                }

                var color = attrs.Color;
                string strokeColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
                double opacity = attrs.IsHighlighter ? 0.5 : 1.0;
                double strokeWidth = Math.Max(attrs.Width, attrs.Height);
                if (double.IsNaN(strokeWidth) || double.IsInfinity(strokeWidth) || strokeWidth <= 0.01)
                    strokeWidth = 2.0;
                // 线宽按面积守恒缩放（与 ScaleStrokes 导入侧一致）
                double widthScale = Math.Sqrt(coordScaleX * coordScaleY);
                if (double.IsNaN(widthScale) || double.IsInfinity(widthScale) || widthScale <= 0)
                    widthScale = 1.0;
                strokeWidth *= widthScale;

                sb.AppendLine($"  <path d=\"{pathBuilder}\" fill=\"none\" stroke=\"{strokeColor}\" stroke-opacity=\"{opacity:F2}\" stroke-width=\"{strokeWidth:F2}\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>");
            }

            sb.AppendLine("</svg>");

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        /// <summary>
        /// 从 PathFigure 提取折线点（支持 LineSegment/PolyLineSegment，其他段类型取终点）
        /// </summary>
        private static List<WinPoint> ExtractFigurePoints(System.Windows.Media.PathFigure fig)
        {
            var pts = new List<WinPoint>();
            if (fig == null) return pts;
            pts.Add(fig.StartPoint);
            foreach (var seg in fig.Segments)
            {
                if (seg is System.Windows.Media.LineSegment ls)
                {
                    pts.Add(ls.Point);
                }
                else if (seg is System.Windows.Media.PolyLineSegment pls)
                {
                    foreach (var p in pls.Points) pts.Add(p);
                }
                else
                {
                    // 其他段类型（Bezier/Arc 等）取终点
                    var tp = seg.GetType().GetProperty("Point");
                    if (tp != null && tp.GetValue(seg) is WinPoint ep)
                        pts.Add(ep);
                }
            }
            return pts;
        }

        /// <summary>
        /// 将油漆桶填充位图追踪为矢量多边形（按顺时针绕向的闭合边界）
        /// </summary>
        /// <param name="bitmap">填充位图</param>
        /// <param name="targetColor">若提供，只追踪匹配此颜色的像素（排除笔画像素）</param>
        private static List<List<WinPoint>> TraceFillBitmapToPolygons(BitmapSource bitmap, System.Windows.Media.Color? targetColor = null)
        {
            var polygons = new List<List<WinPoint>>();
            if (bitmap == null) return polygons;

            int w = bitmap.PixelWidth;
            int h = bitmap.PixelHeight;
            if (w <= 0 || h <= 0) return polygons;

            int stride = w * 4;
            var pixels = new byte[stride * h];
            bitmap.CopyPixels(pixels, stride, 0);

            // 若提供了目标色，只匹配该色（容差±2，应对 Pbgra32 预乘误差）；否则匹配所有不透明像素
            byte? tB = targetColor?.B, tG = targetColor?.G, tR = targetColor?.R;

            bool IsFilled(int x, int y)
            {
                if (x < 0 || x >= w || y < 0 || y >= h) return false;
                int idx = (y * w + x) * 4;
                if (pixels[idx + 3] <= 128) return false;
                if (tB.HasValue)
                {
                    return Math.Abs(pixels[idx] - tB.Value) <= 2 &&
                           Math.Abs(pixels[idx + 1] - tG!.Value) <= 2 &&
                           Math.Abs(pixels[idx + 2] - tR!.Value) <= 2;
                }
                return true;
            }

            // 构建有向边界边（顺时针绕向，使填充区在右侧）
            var edgeMap = new Dictionary<(int, int), List<(int, int)>>();
            void AddEdge(int sx, int sy, int ex, int ey)
            {
                var key = (sx, sy);
                if (!edgeMap.TryGetValue(key, out var list))
                {
                    list = new List<(int, int)>();
                    edgeMap[key] = list;
                }
                list.Add((ex, ey));
            }

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (!IsFilled(x, y)) continue;
                    if (!IsFilled(x, y - 1)) AddEdge(x, y, x + 1, y);           // 上边
                    if (!IsFilled(x + 1, y)) AddEdge(x + 1, y, x + 1, y + 1);   // 右边
                    if (!IsFilled(x, y + 1)) AddEdge(x + 1, y + 1, x, y + 1);   // 下边
                    if (!IsFilled(x - 1, y)) AddEdge(x, y + 1, x, y);           // 左边
                }
            }

            // 将边链接为闭合多边形
            while (edgeMap.Count > 0)
            {
                var firstStart = edgeMap.Keys.First();
                var ends = edgeMap[firstStart];
                var firstEnd = ends[0];
                ends.RemoveAt(0);
                if (ends.Count == 0) edgeMap.Remove(firstStart);

                var polygon = new List<WinPoint> { new WinPoint(firstStart.Item1, firstStart.Item2) };
                var current = firstEnd;

                while (current != firstStart)
                {
                    polygon.Add(new WinPoint(current.Item1, current.Item2));
                    if (!edgeMap.TryGetValue(current, out var candidates) || candidates.Count == 0)
                        break;
                    var next = candidates[0];
                    candidates.RemoveAt(0);
                    if (candidates.Count == 0) edgeMap.Remove(current);
                    current = next;
                }

                if (polygon.Count >= 3)
                    polygons.Add(polygon);
            }

            return polygons;
        }

        /// <summary>
        /// Douglas-Peucker 多边形抽稀（闭合多边形按开放折线处理，首尾为锚点）
        /// </summary>
        private static List<WinPoint> SimplifyPolygon(List<WinPoint> points, double tolerance)
        {
            if (points == null || points.Count < 3) return points;

            int n = points.Count;
            var keep = new bool[n];
            keep[0] = keep[n - 1] = true;

            void SimplifyRange(int s, int e)
            {
                if (e <= s + 1) return;
                double maxDist = 0;
                int maxIdx = -1;
                var p1 = points[s];
                var p2 = points[e];
                for (int i = s + 1; i < e; i++)
                {
                    var p = points[i];
                    double dx = p2.X - p1.X;
                    double dy = p2.Y - p1.Y;
                    double len2 = dx * dx + dy * dy;
                    double dist;
                    if (len2 < 1e-12)
                    {
                        dist = Math.Sqrt((p.X - p1.X) * (p.X - p1.X) + (p.Y - p1.Y) * (p.Y - p1.Y));
                    }
                    else
                    {
                        dist = Math.Abs(((p.X - p1.X) * dy - (p.Y - p1.Y) * dx)) / Math.Sqrt(len2);
                    }
                    if (dist > maxDist)
                    {
                        maxDist = dist;
                        maxIdx = i;
                    }
                }
                if (maxIdx >= 0 && maxDist > tolerance)
                {
                    keep[maxIdx] = true;
                    SimplifyRange(s, maxIdx);
                    SimplifyRange(maxIdx, e);
                }
            }

            SimplifyRange(0, n - 1);

            var result = new List<WinPoint>();
            for (int i = 0; i < n; i++)
            {
                if (keep[i]) result.Add(points[i]);
            }
            return result;
        }

        /// <summary>
        /// 从填充位图中采样填充颜色（出现最多的不透明颜色，即填充主色而非笔画色）
        /// </summary>
        private static System.Windows.Media.Color GetFillColorFromBitmap(BitmapSource bitmap)
        {
            try
            {
                if (bitmap == null) return Colors.Black;
                int w = bitmap.PixelWidth;
                int h = bitmap.PixelHeight;
                int stride = w * 4;
                var pixels = new byte[stride * h];
                bitmap.CopyPixels(pixels, stride, 0);

                // 统计每种不透明颜色的出现次数，取最多的（填充区域通常远大于笔画）
                var counts = new Dictionary<(byte, byte, byte), int>();
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    byte a = pixels[i + 3];
                    if (a <= 128) continue;
                    byte b = pixels[i];
                    byte g = pixels[i + 1];
                    byte r = pixels[i + 2];
                    // 反预乘
                    if (a > 0 && a < 255)
                    {
                        b = (byte)Math.Min(255, b * 255 / a);
                        g = (byte)Math.Min(255, g * 255 / a);
                        r = (byte)Math.Min(255, r * 255 / a);
                    }
                    var key = (b, g, r);
                    counts.TryGetValue(key, out int c);
                    counts[key] = c + 1;
                }

                if (counts.Count > 0)
                {
                    var dominant = counts.OrderByDescending(kv => kv.Value).First().Key;
                    return System.Windows.Media.Color.FromRgb(dominant.Item3, dominant.Item2, dominant.Item1);
                }
            }
            catch { }
            return Colors.Black;
        }

        /// <summary>
        /// 将 SVG 填充路径（M/L/Z 多边形）光栅化为位图，用于导入时还原油漆桶填充
        /// </summary>
        private static BitmapSource RasterizeFillPath(string pathData, System.Windows.Media.Color color, int width, int height)
        {
            try
            {
                if (width <= 0) width = 1920;
                if (height <= 0) height = 1080;

                // 用 StreamGeometry 构建多边形几何（支持多子路径 + evenodd 填充规则）
                var geometry = new StreamGeometry();
                geometry.FillRule = FillRule.EvenOdd;
                using (var ctx = geometry.Open())
                {
                    var tokens = System.Text.RegularExpressions.Regex.Matches(pathData, @"[MLZmlz]|[-+]?\d*\.?\d+(?:[eE][-+]?\d+)?")
                        .Cast<System.Text.RegularExpressions.Match>()
                        .Select(m => m.Value)
                        .ToList();

                    var invariant = System.Globalization.CultureInfo.InvariantCulture;
                    double Num(int idx) => idx < tokens.Count && double.TryParse(tokens[idx], System.Globalization.NumberStyles.Float, invariant, out double v) ? v : 0;

                    double curX = 0, curY = 0;
                    double startX = 0, startY = 0;
                    char cmd = 'M';
                    int i = 0;
                    while (i < tokens.Count)
                    {
                        if (tokens[i].Length == 1 && char.IsLetter(tokens[i][0]))
                        {
                            cmd = tokens[i][0];
                            i++;
                        }
                        char c = char.ToUpper(cmd);
                        bool rel = char.IsLower(cmd);
                        switch (c)
                        {
                            case 'M':
                                curX = Num(i); curY = Num(i + 1); i += 2;
                                if (rel) { curX += startX; curY += startY; }
                                startX = curX; startY = curY;
                                // BeginFigure 自动结束上一个 figure；isFilled=true, isClosed=true（闭合多边形）
                                ctx.BeginFigure(new WinPoint(curX, curY), true, true);
                                cmd = char.IsLower(cmd) ? 'l' : 'L';
                                break;
                            case 'L':
                                curX = Num(i); curY = Num(i + 1); i += 2;
                                if (rel) { curX += startX; curY += startY; }
                                ctx.LineTo(new WinPoint(curX, curY), true, true);
                                startX = curX; startY = curY;
                                break;
                            case 'Z':
                                // figure 已通过 BeginFigure(isClosed=true) 标记为闭合，无需额外操作
                                cmd = 'M';
                                break;
                        }
                    }
                }
                geometry.Freeze();

                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawGeometry(new SolidColorBrush(color), null, geometry);
                }

                var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();
                return rtb;
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"光栅化填充路径失败: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// 将 BitmapSource 编码为 base64 PNG 字符串
        /// </summary>
        private static string BitmapSourceToBase64Png(BitmapSource bitmap)
        {
            try
            {
                var encoder = new PngBitmapEncoder();
                var frame = bitmap as BitmapFrame;
                if (frame != null)
                    encoder.Frames.Add(frame);
                else
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));

                using (var ms = new MemoryStream())
                {
                    encoder.Save(ms);
                    return Convert.ToBase64String(ms.ToArray());
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 将笔迹导出为 PNG 图片（透明背景，不包含背景图像）
        /// </summary>
        private void ExportStrokesAsPng(StrokeCollection strokes, string filePath, int width, int height)
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                // 不绘制背景图像 - 保持透明
                foreach (var stroke in strokes)
                {
                    var attrs = stroke.DrawingAttributes;
                    var geometry = stroke.GetGeometry(attrs);
                    var brush = new SolidColorBrush(attrs.Color);
                    if (attrs.IsHighlighter)
                    {
                        brush.Opacity = 0.5;
                    }
                    context.DrawGeometry(brush, null, geometry);
                }
            }

            var renderBitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            renderBitmap.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(renderBitmap));
            using var stream = new FileStream(filePath, FileMode.Create);
            encoder.Save(stream);
        }

        /// <summary>
        /// 关闭触控信息悬浮窗
        /// </summary>
        private void CloseTouchInfo_Click(object sender, RoutedEventArgs e)
        {
            TouchInfoPopup.IsOpen = false;
            Logger.Debug("MainWindow", "关闭触控信息悬浮窗");
        }

        /// <summary>
        /// 切换摄像头
        /// </summary>
        private void SwitchCamera_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing) return;

            Logger.Info("MainWindow", "开始切换摄像头");

            var cameras = _cameraManager.GetAvailableCameras();
            if (cameras.Count == 0)
            {
                Logger.Warning("MainWindow", "未找到可用摄像头");
                MessageBox.Show("未找到可用摄像头。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                _cameraManager.CheckCameraAvailability();
                ShowNoCameraBackground();
                return;
            }

            // 使用WPF窗口替代WinForms
            var dialog = new Window
            {
                Title = "选择摄像头",
                Width = 400,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow
            };

            var stackPanel = new StackPanel { Margin = new Thickness(10) };

            var comboBox = new WinComboBox
            {
                Margin = new Thickness(0, 0, 0, 10),
                ItemsSource = cameras,
                SelectedIndex = _cameraManager.CurrentCameraIndex
            };

            var buttonPanel = new StackPanel
            {
                Orientation = WinOrientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };

            var okButton = new WinButton
            {
                Content = "确定",
                Width = 80,
                Margin = new Thickness(5, 0, 0, 0),
                IsDefault = true
            };

            var cancelButton = new WinButton
            {
                Content = "取消",
                Width = 80,
                Margin = new Thickness(5, 0, 0, 0),
                IsCancel = true
            };

            buttonPanel.Children.Add(cancelButton);
            buttonPanel.Children.Add(okButton);

            stackPanel.Children.Add(comboBox);
            stackPanel.Children.Add(buttonPanel);
            dialog.Content = stackPanel;

            bool? result = null;

            okButton.Click += (s, args) =>
            {
                result = true;
                dialog.Close();
            };

            cancelButton.Click += (s, args) =>
            {
                result = false;
                dialog.Close();
            };

            dialog.ShowDialog();

            if (result == true && comboBox.SelectedIndex >= 0)
            {
                int newCameraIndex = comboBox.SelectedIndex;

                // 重置视频帧记录状态
                _isFirstFrameProcessed = false;
                Logger.ResetVideoFrameLogging();

                // 切换摄像头
                if (_cameraManager.SwitchCamera(newCameraIndex))
                {
                    Logger.Info("MainWindow", $"已切换到摄像头: {cameras[newCameraIndex]}");
                    MessageBox.Show($"已切换到摄像头: {cameras[newCameraIndex]}", "提示", MessageBoxButton.OK, MessageBoxImage.Information);

                    // 切换摄像头后应用该摄像头的配置
                    ApplyCameraConfigOnStartup();
                }
                else
                {
                    Logger.Error("MainWindow", "切换摄像头失败");
                    MessageBox.Show("切换摄像头失败。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    ShowNoCameraBackground();
                }
            }
        }

        /// <summary>
        /// 打开画面调节窗口
        /// </summary>
        private void OpenAdjustVideo_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing) return;

            // 检查摄像头可用性
            if (!_cameraManager.IsCameraAvailable)
            {
                MessageBox.Show("没有可用的摄像头，无法使用画面调节功能。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Logger.Info("MainWindow", "打开画面调节窗口");

            // 创建画面调节窗口
            var wnd = new AdjustVideoWindow(
                _brightness,
                _contrast,
                _rotation,
                _mirrorHorizontal,
                _mirrorVertical
            );
            wnd.Owner = this;
            if (wnd.ShowDialog() == true)
            {
                // 更新画面调节参数
                _brightness = wnd.Brightness;
                _contrast = wnd.Contrast;
                _rotation = wnd.Rotation;
                _mirrorHorizontal = wnd.MirrorH;
                _mirrorVertical = wnd.MirrorV;

                // 应用设置到摄像头管理器
                _cameraManager.SetVideoAdjustments(_brightness, _contrast, _rotation, _mirrorHorizontal, _mirrorVertical);

                // 更新当前摄像头的配置
                UpdateCurrentCameraAdjustments();

                Logger.Info("MainWindow", "画面调节参数已更新");
            }
        }

        /// <summary>
        /// 清除透视校正
        /// </summary>
        private void ClearCorrection_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing) return;

            Logger.Info("MainWindow", "清除透视校正");

            _cameraManager.ClearPerspectiveCorrection();

            // 清除当前摄像头的校正配置
            int cameraIndex = _cameraManager.CurrentCameraIndex;
            if (config.CameraConfigs.ContainsKey(cameraIndex))
            {
                config.CameraConfigs[cameraIndex].ClearCorrection();
            }

            SaveConfig();

            // 刷新当前画面
            if (_cameraManager.IsCameraAvailable)
            {
                var frame = _cameraManager.GetCurrentFrame();
                if (frame != null)
                {
                    using var processed = _cameraManager.ProcessFrame(frame, applyAdjustments: true);
                    var videoImage = (WinImage)FindName("VideoImage");
                    if (videoImage != null)
                    {
                        videoImage.Source = _memoryManager.BitmapToBitmapImage(processed);
                    }
                    frame.Dispose();
                }
            }

            Logger.Info("MainWindow", "透视校正已清除");
            MessageBox.Show("透视校正已清除。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// 打开设置窗口
        /// </summary>
        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing) return;

            Logger.Info("MainWindow", "打开设置窗口");

            var cameras = _cameraManager.GetAvailableCameras();
            var settingsWindow = new SettingsWindow(config, cameras)
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            if (settingsWindow.ShowDialog() == true)
            {
                // 应用窗口设置
                WindowState = config.StartMaximized ? WindowState.Maximized : WindowState.Normal;

                // 保存配置（默认颜色等仅写入config，下次启动时读取生效）
                SaveConfig();

                // 立即应用画板模式等运行时设置
                _drawingManager.ApplyConfig(config);

                // 根据画板模式更新油漆桶按钮可见性
                UpdatePaintBucketButtonVisibility();

                // 重新应用主题
                ApplyTheme();

                // 更新触控信息悬浮窗显示状态（根据开发者模式）
                TouchInfoPopup.IsOpen = config.DeveloperMode;

                // 检查是否需要切换摄像头
                if (_cameraManager.CurrentCameraIndex != config.CameraIndex)
                {
                    if (_cameraManager.SwitchCamera(config.CameraIndex))
                    {
                        Logger.Info("MainWindow", $"已切换到摄像头: {cameras[config.CameraIndex]}");
                        MessageBox.Show($"已切换到摄像头: {cameras[config.CameraIndex]}", "提示", MessageBoxButton.OK, MessageBoxImage.Information);

                        // 切换摄像头后应用该摄像头的配置
                        ApplyCameraConfigOnStartup();
                    }
                    else
                    {
                        Logger.Error("MainWindow", "切换摄像头失败");
                        MessageBox.Show("切换摄像头失败。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        ShowNoCameraBackground();
                    }
                }

                Logger.Info("MainWindow", "设置已应用");
            }
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 启动摄像头（带降级处理）
        /// </summary>
        private void StartCameraWithFallback()
        {
            // 重置视频帧记录状态
            _isFirstFrameProcessed = false;
            Logger.ResetVideoFrameLogging();

            if (!_cameraManager.StartCamera())
            {
                Logger.Warning("MainWindow", "未找到可用摄像头");
                MessageBox.Show("未找到可用摄像头。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                ShowNoCameraBackground();
            }
            else
            {
                Logger.Info("MainWindow", "摄像头启动成功");

                // 摄像头启动成功后应用配置
                ApplyCameraConfigOnStartup();
            }
        }

        /// <summary>
        /// 显示无摄像头背景
        /// </summary>
        private void ShowNoCameraBackground()
        {
            Dispatcher.Invoke(() =>
            {
                var videoImage = (WinImage)FindName("VideoImage");
                var videoArea = (Grid)FindName("VideoArea");
                if (videoImage != null)
                {
                    videoImage.Source = null;
                }
                if (videoArea != null)
                {
                    videoArea.Background = _noCameraBackground;
                }

                var textBlock = new TextBlock
                {
                    Text = LanguageManager.Instance.GetTranslation("NoCameraDetected"),
                    Foreground = WinBrushes.White,
                    FontSize = 16,
                    FontWeight = FontWeights.Bold,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                if (videoArea != null)
                {
                    videoArea.IsHitTestVisible = true;
                    videoArea.Focusable = true;
                }
                Logger.Info("MainWindow", "已切换到无摄像头模式，批注功能可用");
            });
        }

        /// <summary>
        /// 显示拍照提示
        /// </summary>
        private async void ShowPhotoTip()
        {
            if (_isClosing) return;

            Logger.Debug("MainWindow", "显示拍照提示");

            var photoTipPopup = FindName("PhotoTipPopup") as Popup;
            if (photoTipPopup != null)
            {
                photoTipPopup.IsOpen = true;
                await Task.Delay(3000);
                photoTipPopup.IsOpen = false;
            }
        }

        /// <summary>
        /// 更新 TouchSDK 状态显示
        /// </summary>
        private void UpdateTouchSDKStatus()
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    if (TouchCountText != null)
                    {
                        TouchCountText.Text = _touchManager.GetTouchSDKStatusText();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("MainWindow", $"更新TouchSDK状态显示失败: {ex.Message}", ex);
                }
            });
        }

        /// <summary>
        /// 更新 SDK 面积显示
        /// </summary>
        private void UpdateSDKTouchAreaDisplay()
        {
            try
            {
                if (SDKTouchAreaText != null)
                {
                    SDKTouchAreaText.Text = $"SDK面积: {_touchManager.SDKTouchArea:F0} 像素²";
                }
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"更新SDK面积显示失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 加载配置
        /// </summary>
        private void LoadConfig()
        {
            try
            {
                Logger.Debug("MainWindow", "开始加载配置");

                if (!File.Exists(configPath))
                {
                    config = new AppConfig();
                    Logger.Info("MainWindow", "配置文件不存在，使用默认配置");
                    return;
                }

                var json = File.ReadAllText(configPath, Encoding.UTF8);
                var cfg = JsonConvert.DeserializeObject<AppConfig>(json);
                if (cfg == null)
                {
                    config = new AppConfig();
                    Logger.Warning("MainWindow", "配置文件解析失败，使用默认配置");
                    return;
                }

                config = cfg;

                // 确保 CameraConfigs 字典被初始化
                if (config.CameraConfigs == null)
                {
                    config.CameraConfigs = new Dictionary<int, CameraConfig>();
                }

                // 加载语言设置
                if (_languageManager != null)
                {
                    _languageManager.CurrentLanguage = (LanguageType)config.Language;
                }

                // 网络偏好不持久化到 config，软件启动时默认局域网
                // 运行期间切换的值保留在内存中，悬浮窗关闭再打开时不变
                config.NetworkPreference = "LAN";

                Logger.Info("MainWindow", $"配置加载成功，包含 {config.CameraConfigs.Count} 个摄像头的配置");

                // 向后兼容：如果有旧的 CameraCorrections，迁移到新的 CameraConfigs
                if (typeof(AppConfig).GetProperty("CameraCorrections") != null)
                {
                    MigrateOldCorrectionConfig(cfg);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"加载配置失败: {ex.Message}", ex);
                config = new AppConfig();
            }
        }

        /// <summary>
        /// 迁移旧的校正配置到新的格式
        /// </summary>
        private void MigrateOldCorrectionConfig(AppConfig cfg)
        {
            try
            {
                // 使用反射检查是否有旧属性（旧格式使用 CameraCorrections）
                var oldProperty = cfg.GetType().GetProperty("CameraCorrections");
                if (oldProperty != null)
                {
                    var oldValue = oldProperty.GetValue(cfg);
                    if (oldValue is Dictionary<int, object> oldDict && oldDict.Count > 0)
                    {
                        Logger.Info("MainWindow", "检测到旧的校正配置，正在迁移...");

                        foreach (var kvp in oldDict)
                        {
                            try
                            {
                                // 动态解析旧的配置格式
                                var json = JsonConvert.SerializeObject(kvp.Value);
                                var dynamicObj = JsonConvert.DeserializeObject<dynamic>(json);

                                if (dynamicObj != null)
                                {
                                    if (!config.CameraConfigs.ContainsKey(kvp.Key))
                                    {
                                        config.CameraConfigs[kvp.Key] = new CameraConfig
                                        {
                                            CameraIndex = kvp.Key,
                                            CameraName = $"摄像头 {kvp.Key}",
                                            HasCorrection = true
                                        };

                                        // 尝试解析源尺寸
                                        if (dynamicObj.SourceWidth != null)
                                        {
                                            config.CameraConfigs[kvp.Key].SourceWidth = (int)dynamicObj.SourceWidth;
                                            config.CameraConfigs[kvp.Key].SourceHeight = (int)dynamicObj.SourceHeight;
                                        }

                                        if (dynamicObj.OriginalCameraWidth != null)
                                        {
                                            config.CameraConfigs[kvp.Key].OriginalCameraWidth = (int)dynamicObj.OriginalCameraWidth;
                                            config.CameraConfigs[kvp.Key].OriginalCameraHeight = (int)dynamicObj.OriginalCameraHeight;
                                        }

                                        // 尝试解析校正点
                                        if (dynamicObj.CorrectionPoints != null)
                                        {
                                            var pointsList = new List<AForge.IntPoint>();

                                            foreach (var point in dynamicObj.CorrectionPoints)
                                            {
                                                if (point.X != null && point.Y != null)
                                                {
                                                    pointsList.Add(new AForge.IntPoint((int)point.X, (int)point.Y));
                                                }
                                            }

                                            if (pointsList.Count == 4)
                                            {
                                                config.CameraConfigs[kvp.Key].SetCorrectionPoints(pointsList);
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Error("MainWindow", $"迁移摄像头 {kvp.Key} 配置失败: {ex.Message}", ex);
                            }
                        }

                        Logger.Info("MainWindow", $"已尝试迁移 {oldDict.Count} 个摄像头的校正配置");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"迁移旧配置失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 启动时应用摄像头配置（包括校正和调整）
        /// </summary>
        private void ApplyCameraConfigOnStartup()
        {
            try
            {
                if (!_cameraManager.IsCameraAvailable || config == null)
                {
                    Logger.Debug("MainWindow", "摄像头不可用或配置为空，跳过配置应用");
                    return;
                }

                int cameraIndex = _cameraManager.CurrentCameraIndex;

                Logger.Debug("MainWindow", $"尝试为摄像头 {cameraIndex} 应用配置");

                // 检查是否有该摄像头的配置
                if (config.CameraConfigs != null &&
                    config.CameraConfigs.ContainsKey(cameraIndex))
                {
                    var cameraConfig = config.CameraConfigs[cameraIndex];

                    if (cameraConfig != null)
                    {
                        Logger.Info("MainWindow", $"找到摄像头 {cameraIndex} 的配置，正在应用...");

                        // 1. 应用画面调整参数
                        ApplyImageAdjustments(cameraConfig.Adjustments);

                        // 2. 应用透视校正
                        if (cameraConfig.HasCorrection &&
                            cameraConfig.PerspectivePoints != null &&
                            cameraConfig.PerspectivePoints.Count == 4)
                        {
                            ApplyPerspectiveCorrection(cameraConfig);
                        }

                        Logger.Info("MainWindow", $"摄像头 {cameraIndex} 的配置已成功应用");
                    }
                }
                else
                {
                    Logger.Debug("MainWindow", $"摄像头 {cameraIndex} 没有找到配置，使用默认设置");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"应用摄像头配置失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 应用画面调整参数
        /// </summary>
        private void ApplyImageAdjustments(ImageAdjustments adjustments)
        {
            try
            {
                if (adjustments == null) return;

                // 更新全局调整参数
                _brightness = (adjustments.Brightness - 100) / 100.0 * 50; // 转换为-50到50的范围
                _contrast = (adjustments.Contrast - 100) / 100.0 * 50; // 转换为-50到50的范围
                _rotation = adjustments.Orientation;
                _mirrorHorizontal = adjustments.FlipHorizontal;

                // 应用到摄像头管理器
                _cameraManager.SetVideoAdjustments(_brightness, _contrast, _rotation, _mirrorHorizontal, _mirrorVertical);

                Logger.Debug("MainWindow", $"已应用画面调整: 亮度={adjustments.Brightness}, 对比度={adjustments.Contrast}, 旋转={adjustments.Orientation}°, 水平翻转={adjustments.FlipHorizontal}");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"应用画面调整失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 应用透视校正
        /// </summary>
        private void ApplyPerspectiveCorrection(CameraConfig cameraConfig)
        {
            try
            {
                if (cameraConfig == null || !cameraConfig.HasCorrection) return;

                // 获取校正点
                var correctionPoints = cameraConfig.GetCorrectionPoints();
                if (correctionPoints.Count != 4)
                {
                    Logger.Warning("MainWindow", $"校正点数量无效: {correctionPoints.Count}，应为4");
                    return;
                }

                // 创建透视校正过滤器
                var filter = new QuadrilateralTransformation(
                    correctionPoints,
                    cameraConfig.SourceWidth > 0 ? cameraConfig.SourceWidth : 640,
                    cameraConfig.SourceHeight > 0 ? cameraConfig.SourceHeight : 480);

                // 应用到摄像头管理器
                _cameraManager.SetPerspectiveCorrectionFilter(filter);

                Logger.Info("MainWindow", $"透视校正已应用: 源尺寸={cameraConfig.SourceWidth}x{cameraConfig.SourceHeight}");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"应用透视校正失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 更新当前摄像头的调整参数
        /// </summary>
        private void UpdateCurrentCameraAdjustments()
        {
            try
            {
                int cameraIndex = _cameraManager.CurrentCameraIndex;

                // 创建或更新当前摄像头的配置
                if (!config.CameraConfigs.ContainsKey(cameraIndex))
                {
                    config.CameraConfigs[cameraIndex] = new CameraConfig
                    {
                        CameraIndex = cameraIndex,
                        CameraName = _cameraManager.GetCurrentCameraName()
                    };
                }

                // 更新调整参数
                config.CameraConfigs[cameraIndex].Adjustments = new ImageAdjustments
                {
                    Brightness = (int)((_brightness / 50.0 * 100) + 100), // 转换回0-200范围
                    Contrast = (int)((_contrast / 50.0 * 100) + 100),     // 转换回0-200范围
                    Orientation = _rotation,
                    FlipHorizontal = _mirrorHorizontal
                };

                Logger.Debug("MainWindow", $"已更新摄像头 {cameraIndex} 的画面调整参数");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"更新摄像头调整参数失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 保存配置
        /// </summary>
        private void SaveConfig()
        {
            try
            {
                Logger.Debug("MainWindow", "开始保存配置");

                // 创建或更新当前摄像头的配置
                int cameraIndex = _cameraManager?.CurrentCameraIndex ?? 0;

                if (!config.CameraConfigs.ContainsKey(cameraIndex))
                {
                    config.CameraConfigs[cameraIndex] = new CameraConfig
                    {
                        CameraIndex = cameraIndex,
                        CameraName = _cameraManager.GetCurrentCameraName()
                    };
                }

                // 更新当前摄像头的调整参数
                config.CameraConfigs[cameraIndex].Adjustments = new ImageAdjustments
                {
                    Brightness = (int)((_brightness / 50.0 * 100) + 100), // 转换回0-200范围
                    Contrast = (int)((_contrast / 50.0 * 100) + 100),     // 转换回0-200范围
                    Orientation = _rotation,
                    FlipHorizontal = _mirrorHorizontal
                };

                var cfg = new AppConfig
                {
                    CameraIndex = cameraIndex,
                    StartMaximized = config.StartMaximized,
                    AutoStartCamera = config.AutoStartCamera,
                    DefaultPenWidth = _drawingManager.UserPenWidth,
                    DefaultPenColor = config.DefaultPenColor,
                    EnableHardwareAcceleration = config.EnableHardwareAcceleration,
                    EnableFrameProcessing = config.EnableFrameProcessing,
                    FrameRateLimit = config.FrameRateLimit,
                    CameraConfigs = config.CameraConfigs,
                    Theme = config.Theme,
                    Language = (int)(_languageManager?.CurrentLanguage ?? LanguageType.SimplifiedChinese),
                    IsFirstRun = config.IsFirstRun,
                    NetworkPreference = config.NetworkPreference ?? "LAN",
                    HotspotSsidMode = config.HotspotSsidMode,
                    HotspotCustomSsid = config.HotspotCustomSsid ?? "",
                    HotspotPassword = config.HotspotPassword ?? "12345678",
                    EnablePalmEraser = config.EnablePalmEraser,
                    PalmEraserThreshold = config.PalmEraserThreshold,
                    ManualEraserSize = config.ManualEraserSize,
                    EnableCanvasMode = config.EnableCanvasMode
                };

                var json = JsonConvert.SerializeObject(cfg, Formatting.Indented);
                File.WriteAllText(configPath, json, Encoding.UTF8);

                Logger.Info("MainWindow", "配置保存成功");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"保存配置失败: {ex.Message}", ex);
            }
        }

        #endregion

        #region 主题相关方法

        /// <summary>
        /// 应用主题
        /// </summary>
        private void ApplyTheme()
        {
            if (config == null) return;

            try
            {
                // 移除当前主题资源
                if (_currentTheme != null)
                {
                    Resources.MergedDictionaries.Remove(_currentTheme);
                }

                // 加载新主题资源
                string themePath;
                switch (config.Theme)
                {
                    case "Dark":
                        themePath = "Themes/DarkTheme.xaml";
                        break;
                    case "Light":
                    default:
                        themePath = "Themes/LightTheme.xaml";
                        break;
                }

                _currentTheme = new ResourceDictionary();
                _currentTheme.Source = new Uri(themePath, UriKind.Relative);
                Resources.MergedDictionaries.Add(_currentTheme);

                // 同步设置 iNKORE UI 库的应用主题，使使用 ModernWindowStyle 的窗口（如设置窗口）也跟随主题
                ThemeManager.Current.ApplicationTheme = config.Theme == "Dark"
                    ? iNKORE.UI.WPF.Modern.ApplicationTheme.Dark
                    : iNKORE.UI.WPF.Modern.ApplicationTheme.Light;

                // 主题切换后重新生成二维码以匹配新主题颜色
                if (_deviceConnectionManager != null && _deviceConnectionManager.QrCodeImage != null)
                {
                    _deviceConnectionManager.GenerateQrCode();
                    if (ConnectDevicePopup != null && ConnectDevicePopup.IsOpen && _deviceConnectionManager.QrCodeImage != null)
                    {
                        QrCodeImage.Source = _deviceConnectionManager.QrCodeImage;
                    }
                }

                // 应用窗口背景颜色
                this.Background = Resources["WindowBackgroundBrush"] as WinBrush;

                // 应用工具栏背景颜色
                if (BottomToolbar != null)
                {
                    BottomToolbar.Background = Resources["ToolbarBackgroundBrush"] as WinBrush;
                }

                // 更新样式引用
                UpdateDynamicStyles();

                Logger.Info("MainWindow", $"主题已应用: {config.Theme}");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"加载主题时出错: {ex.Message}", ex);
                MessageBox.Show($"加载主题时出错: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 更新动态样式
        /// </summary>
        private void UpdateDynamicStyles()
        {
            try
            {
                // 更新按钮样式引用
                var buttonStyle = Resources["ButtonStyle"] as Style;
                var toggleButtonStyle = Resources["ToggleButtonStyle"] as Style;
                var toolToggleButtonStyle = Resources["ToolToggleButtonStyle"] as Style;
                var photoButtonStyle = Resources["PhotoButtonStyle"] as Style;
                var moreButtonStyle = Resources["MoreButtonStyle"] as Style;

                // 这里可以添加样式更新的具体逻辑
                // 例如，为特定控件重新应用样式
                if (MoveBtn != null && toolToggleButtonStyle != null)
                {
                    MoveBtn.Style = toolToggleButtonStyle;
                }
                if (PenBtn != null && toolToggleButtonStyle != null)
                {
                    PenBtn.Style = toolToggleButtonStyle;
                }
                if (EraserBtn != null && toolToggleButtonStyle != null)
                {
                    EraserBtn.Style = toolToggleButtonStyle;
                }

                Logger.Debug("MainWindow", "动态样式已更新");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"更新动态样式失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 刷新主题（可以从设置窗口回调）
        /// </summary>
        public void RefreshTheme()
        {
            LoadConfig();
            ApplyTheme();
        }

        #endregion

        #region 关闭和清理

        /// <summary>
        /// 退出应用
        /// </summary>
        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing) return;
            _isClosing = true;

            Logger.Info("MainWindow", "用户请求退出 - 执行快速关闭流程");

            try
            {
                // 1. 立刻阻止一切新操作
                _isClosing = true;

                // 2. 快速停止摄像头（在后台线程执行，不阻塞UI）
                Task.Run(() =>
                {
                    try
                    {
                        if (_cameraManager != null)
                        {
                            _cameraManager.OnNewFrameProcessed -= OnCameraFrameReceived; // 解绑事件
                            _cameraManager.PauseCamera(); // 快速暂停
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("MainWindow", $"后台停止摄像头失败: {ex.Message}", ex);
                    }
                });

                // 3. 强制退出校正模式（防止它卡住）
                if (_isPerspectiveCorrectionMode)
                {
                    ForceExitCorrectionMode();
                }

                // 4. 关闭所有弹窗
                CloseAllPopups();

                // 5. 保存配置
                SaveConfig();

                // 6. 立即关闭应用（不等待后台任务）
                System.Windows.Application.Current.Shutdown(0);
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", "快速退出失败", ex);
                try { Environment.Exit(0); } catch { }
            }
        }

        /// <summary>
        /// 关闭所有弹出窗口
        /// </summary>
        private void CloseAllPopups()
        {
            try
            {
                PenSettingsPopup.IsOpen = false;
                MoreMenuPopup.IsOpen = false;
                TouchInfoPopup.IsOpen = false;
                
                var photoPanelBorder = FindName("PhotoPanelBorder") as Border;
                if (photoPanelBorder != null)
                {
                    photoPanelBorder.Visibility = Visibility.Collapsed;
                    UpdatePhotoButtonState(false);
                }
                
                var photoTipPopup = FindName("PhotoTipPopup") as Popup;
                if (photoTipPopup != null)
                {
                    photoTipPopup.IsOpen = false;
                }

                Logger.Debug("MainWindow", "所有弹出窗口已关闭");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"关闭弹出窗口失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 清理静态和托管资源
        /// </summary>
        private void ClearStaticResources()
        {
            try
            {
                Logger.Info("MainWindow", "开始清理静态资源");

                // 清理画笔资源
                if (_drawingManager != null)
                {
                    _drawingManager.Dispose();
                    _drawingManager = null;
                }

                // 清理内存资源
                if (_memoryManager != null)
                {
                    _memoryManager.CleanupAllResources();
                    _memoryManager = null;
                }

                // 清理摄像头资源
                if (_cameraManager != null)
                {
                    _cameraManager.ReleaseCameraResources();
                    _cameraManager = null;
                }

                // 清理触控资源
                if (_touchManager != null)
                {
                    _touchManager.StopTracking();
                    _touchManager = null;
                }

                // 清理照片悬浮窗资源
                if (_photoPopupManager != null)
                {
                    _photoPopupManager.Dispose();
                    _photoPopupManager = null;
                }

                // 清理数据集合
                _photos.Clear();
                _liveStrokes = null;

                // 清理图片资源
                var videoImage = (WinImage)FindName("VideoImage");
                if (videoImage != null && videoImage.Source != null)
                {
                    var source = videoImage.Source as BitmapSource;
                    if (source != null)
                    {
                        videoImage.Source = null;
                        source = null;
                    }
                }

                // 清理画布
                var ink = (InkCanvas)FindName("Ink");
                if (ink != null)
                {
                    ink.Strokes.Clear();
                }

                // 清理校正相关资源
                if (_originalCorrectionFrame != null)
                {
                    _originalCorrectionFrame.Dispose();
                    _originalCorrectionFrame = null;
                }

                // 清理配置
                config = null;

                Logger.Info("MainWindow", "静态资源清理完成");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"清理静态资源失败: {ex.Message}", ex);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                Logger.Info("MainWindow", "开始应用程序关闭流程...");
                _isClosing = true;

                // 强制退出梯形校正模式（如果正在校正）
                if (_isPerspectiveCorrectionMode)
                {
                    Logger.Warning("MainWindow", "检测到校正模式未正确退出，强制退出");
                    ForceExitCorrectionMode();
                }

                // 关闭所有弹出窗口
                CloseAllPopups();

                // 取消所有事件订阅
                UnsubscribeAllEvents();

                // 清理照片悬浮窗管理器
                _photoPopupManager?.Dispose();

                // 记录系统状态摘要
                _logManager?.LogSystemStatus();

                // 保存配置（必须在清理前调用）
                SaveConfig();

                // 清理所有管理器
                _touchManager?.StopTracking();
                _cameraManager?.ReleaseCameraResources();
                _memoryManager?.CleanupAllResources();
                _drawingManager?.Dispose();

                // 停止设备连接监听（关闭悬浮窗时不会停止，需在退出时清理）
                try
                {
                    if (_deviceConnectionManager != null)
                    {
                        _deviceConnectionManager.ConnectionStatusChanged -= OnConnectionStatusChanged;
                        _deviceConnectionManager.ClientConnected -= OnClientConnected;
                        _deviceConnectionManager.PhotoReceived -= OnPhotoReceived;
                        _deviceConnectionManager.ConnectedDeviceCountChanged -= OnConnectedDeviceCountChanged;
                        _deviceConnectionManager.ConnectedDevicesChanged -= OnConnectedDevicesChanged;
                        _deviceConnectionManager.HandshakeCompleted -= OnHandshakeCompleted;
                        if (_deviceConnectionManager.IsListening)
                        {
                            _deviceConnectionManager.StopListening();
                        }
                    }
                }
                catch (Exception exClean)
                {
                    Logger.Error("MainWindow", $"清理设备连接管理器失败: {exClean.Message}", exClean);
                }

                // 清理静态资源
                ClearStaticResources();

                Logger.Info("MainWindow", "应用程序关闭流程完成");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"关闭过程中发生错误: {ex.Message}", ex);
            }
            finally
            {
                // 关闭日志系统
                Logger.Shutdown();
                base.OnClosed(e);
            }
        }

        #endregion

        #region 模式切换和UI事件

        private void SetMode(DrawingManager.ToolMode mode, bool initial = false)
        {
            try
            {
                _drawingManager.SetMode(mode, initial);
                MoveBtn.IsChecked = mode == DrawingManager.ToolMode.Move;
                PenBtn.IsChecked = mode == DrawingManager.ToolMode.Pen;
                EraserBtn.IsChecked = mode == DrawingManager.ToolMode.Eraser;
                PaintBucketBtn.IsChecked = mode == DrawingManager.ToolMode.PaintBucket;
                // 形状功能已移除

                if (mode == DrawingManager.ToolMode.Pen)
                {
                    _panZoomManager.ApplyStrokeScaleCompensation();
                }

                // 禁用橡皮擦覆盖层（如果不是橡皮擦模式）
                if (mode != DrawingManager.ToolMode.Eraser)
                {
                    _drawingManager.DisableEraserOverlay();
                }

                Logger.Debug("MainWindow", $"切换到模式: {mode}");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"设置模式失败: {ex.Message}", ex);
            }
        }

        private void PaintBucketBtn_Click(object sender, RoutedEventArgs e)
        {
            // 如果当前不是油漆桶模式，切换到油漆桶模式
            if (_drawingManager.CurrentMode != DrawingManager.ToolMode.PaintBucket)
            {
                SetMode(DrawingManager.ToolMode.PaintBucket);

                // 关闭画笔设置悬浮窗
                PenSettingsPopup.IsOpen = false;

                Logger.Debug("MainWindow", "切换到油漆桶模式");
            }
            else
            {
                // 如果已经是油漆桶模式，保持选中状态
                PaintBucketBtn.IsChecked = true;
            }
        }

        /// <summary>
        /// 根据画板模式状态更新油漆桶按钮的可见性
        /// 油漆桶工具仅在启用画板模式时显示
        /// </summary>
        private void UpdatePaintBucketButtonVisibility()
        {
            bool canvasModeEnabled = _drawingManager.EnableCanvasMode;

            // 切换油漆桶按钮可见性
            PaintBucketBtn.Visibility = canvasModeEnabled ? Visibility.Visible : Visibility.Collapsed;

            // 若画板模式被关闭但当前正处于油漆桶模式，则切回画笔模式
            if (!canvasModeEnabled && _drawingManager.CurrentMode == DrawingManager.ToolMode.PaintBucket)
            {
                SetMode(DrawingManager.ToolMode.Pen);
            }
        }

        private void MoveBtn_Click(object sender, RoutedEventArgs e)
        {
            // 如果当前不是移动模式，切换到移动模式
            if (_drawingManager.CurrentMode != DrawingManager.ToolMode.Move)
            {
                SetMode(DrawingManager.ToolMode.Move);

                // 关闭画笔设置悬浮窗
                PenSettingsPopup.IsOpen = false;
            }
            else
            {
                // 如果已经是移动模式，保持选中状态（不取消）
                MoveBtn.IsChecked = true;
            }
        }

        private void EraserBtn_Click(object sender, RoutedEventArgs e)
        {
            // 如果当前不是橡皮擦模式，切换到橡皮擦模式
            if (_drawingManager.CurrentMode != DrawingManager.ToolMode.Eraser)
            {
                SetMode(DrawingManager.ToolMode.Eraser);

                // 关闭画笔设置悬浮窗
                PenSettingsPopup.IsOpen = false;

                // 启用橡皮擦覆盖层
                _drawingManager.EnableEraserOverlay();
                _drawingManager.ApplyAdvancedEraserShape();
            }
            else
            {
                // 如果已经是橡皮擦模式，保持选中状态（不取消）
                EraserBtn.IsChecked = true;

                // 显示清屏确认悬浮窗
                ShowClearConfirmPopup();
            }
        }

        private void ShowClearConfirmPopup()
        {
            if (ClearConfirmPopup.IsOpen)
            {
                ClearConfirmPopup.IsOpen = false;
                return;
            }
            ClearConfirmPopup.IsOpen = true;
        }

        private void ClearConfirmPopup_Opened(object sender, EventArgs e)
        {
            _isSliderDragging = false;
            _sliderReachedEnd = false;
            
            // 重置滑块位置
            SliderThumb.Margin = new Thickness(2, 0, 0, 0);
            SliderProgress.Width = 0;
        }

        private void ClearConfirmPopup_Closed(object sender, EventArgs e)
        {
            _isSliderDragging = false;
            _sliderReachedEnd = false;
        }

        public CustomPopupPlacement[] ClearConfirmPopup_PlacementCallback(System.Windows.Size popupSize, System.Windows.Size targetSize, System.Windows.Point offset)
        {
            double x = (targetSize.Width - popupSize.Width) / 2;
            double y = -popupSize.Height - 5;
            
            return new CustomPopupPlacement[] 
            { 
                new CustomPopupPlacement(new System.Windows.Point(x, y), PopupPrimaryAxis.Vertical) 
            };
        }

        private void SliderThumb_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isSliderDragging = true;
            _sliderReachedEnd = false;
            _sliderStartX = e.GetPosition(SliderTrack).X;
            _sliderMaxDistance = SliderTrack.ActualWidth - SliderThumb.ActualWidth - 4;
            SliderThumb.CaptureMouse();
        }

        private void SliderThumb_MouseMove(object sender, WinMouseEventArgs e)
        {
            if (!_isSliderDragging) return;

            double currentX = e.GetPosition(SliderTrack).X;
            double offset = currentX - _sliderStartX;
            
            // 限制范围
            offset = Math.Max(0, Math.Min(offset, _sliderMaxDistance));
            
            // 更新滑块位置
            SliderThumb.Margin = new Thickness(2 + offset, 0, 0, 0);
            SliderProgress.Width = offset + SliderThumb.ActualWidth / 2;

            // 检查是否滑到底（标记状态，松手时才执行）
            _sliderReachedEnd = offset >= _sliderMaxDistance - 5;
        }

        private void SliderThumb_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isSliderDragging) return;
            
            _isSliderDragging = false;
            SliderThumb.ReleaseMouseCapture();
            
            // 如果滑到底了，执行清屏
            if (_sliderReachedEnd)
            {
                ClearConfirmPopup.IsOpen = false;
                ClearInk_Click(null, null);
            }
            else
            {
                // 没有滑到底，弹回起点
                SliderThumb.Margin = new Thickness(2, 0, 0, 0);
                SliderProgress.Width = 0;
            }
        }

        private void ConnectDeviceBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ConnectDevicePopup.IsOpen)
                {
                    ConnectDevicePopup.IsOpen = false;
                    Logger.Debug("MainWindow", "关闭连接设备悬浮窗（监听保持后台运行）");
                }
                else
                {
                    // 确保管理器只创建一次，连接独立于悬浮窗生命周期保活
                    EnsureDeviceConnectionManager();

                    ConnectDevicePopup.IsOpen = true;
                    Logger.Debug("MainWindow", "打开连接设备悬浮窗");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"切换连接设备悬浮窗失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 确保 _deviceConnectionManager 已创建并订阅事件（仅创建一次，复用现有连接）
        /// </summary>
        private void EnsureDeviceConnectionManager()
        {
            if (_deviceConnectionManager != null)
                return;

            _deviceConnectionManager = new Services.DeviceConnectionManager();
            _deviceConnectionManager.ConnectionStatusChanged += OnConnectionStatusChanged;
            _deviceConnectionManager.ClientConnected += OnClientConnected;
            _deviceConnectionManager.PhotoReceived += OnPhotoReceived;
            _deviceConnectionManager.ConnectedDeviceCountChanged += OnConnectedDeviceCountChanged;
            _deviceConnectionManager.ConnectedDevicesChanged += OnConnectedDevicesChanged;
            _deviceConnectionManager.HandshakeCompleted += OnHandshakeCompleted;

            Logger.Info("MainWindow", "DeviceConnectionManager 已创建并订阅事件");
        }

        private readonly Services.HotspotService _hotspotService = new Services.HotspotService();

        /// <summary>
        /// 已连接设备数变化时更新悬浮窗左侧徽章
        /// </summary>
        private void OnConnectedDeviceCountChanged(int count)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (ConnectedDeviceCountText != null)
                    {
                        ConnectedDeviceCountText.Text = count.ToString();
                    }
                    if (ConnectedDeviceCountBadge != null)
                    {
                        ConnectedDeviceCountBadge.Background = count > 0
                            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80))
                            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(33, 150, 243));
                    }
                    if (NetworkPreferenceComboBox != null)
                    {
                        NetworkPreferenceComboBox.IsEnabled = count == 0;
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"更新已连接设备数失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 已连接设备信息列表变化时刷新右侧展开面板
        /// </summary>
        private void OnConnectedDevicesChanged(System.Collections.Generic.List<Services.ConnectedDeviceInfo> devices)
        {
            try
            {
                Dispatcher.Invoke(() => RefreshConnectedDeviceList(devices));
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"刷新已连接设备列表失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 刷新已连接设备列表显示（更新 ItemsControl 与空状态图片/文字）
        /// </summary>
        private void RefreshConnectedDeviceList(System.Collections.Generic.List<Services.ConnectedDeviceInfo> devices)
        {
            if (ConnectedDeviceListItems == null) return;
            var list = devices ?? new System.Collections.Generic.List<Services.ConnectedDeviceInfo>();
            ConnectedDeviceListItems.ItemsSource = list;
            if (ConnectedDeviceEmptyPanel != null)
            {
                ConnectedDeviceEmptyPanel.Visibility = list.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        /// <summary>
        /// 点击已连接设备数徽章：向左展开/收起左侧设备列表
        /// </summary>
        private void ConnectedDeviceCountBadge_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (ConnectedDeviceListPanel == null) return;
                bool willShow = ConnectedDeviceListPanel.Visibility != Visibility.Visible;
                ConnectedDeviceListPanel.Visibility = willShow ? Visibility.Visible : Visibility.Collapsed;
                // 展开时立即用当前快照刷新一次列表，避免显示旧数据
                if (willShow && _deviceConnectionManager != null)
                {
                    RefreshConnectedDeviceList(_deviceConnectionManager.GetConnectedDevices());
                }
                Logger.Debug("MainWindow", willShow ? "展开已连接设备列表" : "收起已连接设备列表");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"切换已连接设备列表展开状态失败: {ex.Message}", ex);
            }
        }

        private void ConnectDevicePopup_Opened(object sender, EventArgs e)
        {
            try
            {
                // 自动开始连接
                StartConnection();
                
                Logger.Debug("MainWindow", "连接设备悬浮窗已打开");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"连接设备悬浮窗打开事件失败: {ex.Message}", ex);
            }
        }

        private void ConnectDevicePopup_Closed(object sender, EventArgs e)
        {
            try
            {
                // 关闭悬浮窗时不再停止监听，保持后台连接保活
                // 仅在应用退出时才会停止监听（见 OnClosed）

                // 重置为主视图，下次打开时显示主视图
                if (ConnectDeviceSettingsView != null)
                {
                    ConnectDeviceSettingsView.Visibility = Visibility.Collapsed;
                }
                if (ConnectDeviceMainView != null)
                {
                    ConnectDeviceMainView.Visibility = Visibility.Visible;
                }
                // 收起右侧已连接设备列表，下次打开时默认收起
                if (ConnectedDeviceListPanel != null)
                {
                    ConnectedDeviceListPanel.Visibility = Visibility.Collapsed;
                }

                Logger.Debug("MainWindow", "连接设备悬浮窗已关闭（监听保持后台运行）");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"连接设备悬浮窗关闭事件失败: {ex.Message}", ex);
            }
        }

        private void StartConnection()
        {
            try
            {
                EnsureDeviceConnectionManager();

                RestoreNetworkPreference();

                if (ConnectedDeviceCountText != null)
                {
                    int currentCount = _deviceConnectionManager.ConnectedDeviceCount;
                    ConnectedDeviceCountText.Text = currentCount.ToString();
                    if (ConnectedDeviceCountBadge != null)
                    {
                        ConnectedDeviceCountBadge.Background = currentCount > 0
                            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80))
                            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(33, 150, 243));
                    }
                    if (NetworkPreferenceComboBox != null)
                    {
                        NetworkPreferenceComboBox.IsEnabled = currentCount == 0;
                    }
                }

                if (_deviceConnectionManager.IsListening)
                {
                    // 热点模式下二维码/IP 已由 RestoreNetworkPreference 设置，不覆盖
                    if (NetworkPreferenceComboBox?.SelectedIndex != 1)
                    {
                        if (_deviceConnectionManager.QrCodeImage != null)
                        {
                            QrCodeImage.Source = _deviceConnectionManager.QrCodeImage;
                        }
                        IpAddressText.Text = $"{_languageManager.GetTranslation("IPAddress")}: {_deviceConnectionManager.GetLocalIPAddress()}:{_deviceConnectionManager.Port}";
                    }

                    // 同步当前真实连接状态到状态文本，避免悬浮窗关闭期间状态变化导致 UI 显示残留旧文本
                    if (ConnectionStatusText != null)
                    {
                        ConnectionStatusText.Text = _deviceConnectionManager.ConnectedDeviceCount > 0
                            ? _languageManager.GetTranslation("HandshakeSuccess")
                            : _languageManager.GetTranslation("WaitingForConnection");
                    }

                    Logger.Debug("MainWindow", "监听已在运行，复用现有连接，仅刷新 UI");
                    return;
                }

                int userPort = _deviceConnectionManager.Port;

                // 热点模式下不生成局域网二维码（由 RestoreNetworkPreference / SwitchToHotspotAsync 负责）
                if (NetworkPreferenceComboBox?.SelectedIndex != 1)
                {
                    _deviceConnectionManager.GenerateQrCode();

                    QrCodeImage.Source = _deviceConnectionManager.QrCodeImage;
                    IpAddressText.Text = $"{_languageManager.GetTranslation("IPAddress")}: {_deviceConnectionManager.GetLocalIPAddress()}:{_deviceConnectionManager.Port}";
                }
                ConnectionStatusText.Text = _languageManager.GetTranslation("WaitingForConnection");

                _ = _deviceConnectionManager.StartListeningAsync();

                Logger.Info("MainWindow", $"开始设备连接，端口: {userPort}");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"开始设备连接失败: {ex.Message}", ex);
                MessageBox.Show($"开始连接失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RefreshPortBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 获取用户输入的端口号
                if (int.TryParse(PortNumberText.Text, out int port))
                {
                    EnsureDeviceConnectionManager();

                    // 切换端口需要先停止当前监听（会断开已连接的设备）
                    if (_deviceConnectionManager.IsListening)
                    {
                        _deviceConnectionManager.StopListening();
                    }
                    _deviceConnectionManager.Port = port;

                    // 重新启动监听
                    StartConnection();

                    Logger.Info("MainWindow", $"刷新端口: {port}");
                }
                else
                {
                    MessageBox.Show("请输入有效的端口号", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"刷新端口失败: {ex.Message}", ex);
                MessageBox.Show($"刷新端口失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 点击设置按钮：切换到设置视图
        /// </summary>
        private void ConnectDeviceSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ConnectDeviceMainView.Visibility = Visibility.Collapsed;
                ConnectDeviceSettingsView.Visibility = Visibility.Visible;

                EnsureDeviceConnectionManager();

                // 同步当前端口号到输入框
                PortNumberText.Text = _deviceConnectionManager.Port.ToString();

                // 加载网卡列表并选中当前网卡
                LoadNetworkAdapters();

                Logger.Debug("MainWindow", "连接设备悬浮窗切换到设置视图");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"切换到设置视图失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 加载网卡列表到下拉框
        /// </summary>
        private void LoadNetworkAdapters()
        {
            try
            {
                // 暂时移除事件，避免填充时触发
                NetworkAdapterComboBox.SelectionChanged -= NetworkAdapterComboBox_SelectionChanged;
                NetworkAdapterComboBox.Items.Clear();

                // 是否显示虚拟网卡：勾选时不过滤虚拟网卡
                bool excludeVirtual = ShowVirtualAdaptersCheckBox.IsChecked != true;
                var adapters = _deviceConnectionManager.GetNetworkAdapters(excludeVirtual);
                int selectedIndex = -1;
                string currentId = _deviceConnectionManager.SelectedAdapterId;

                for (int i = 0; i < adapters.Count; i++)
                {
                    var adapter = adapters[i];
                    var item = new ComboBoxItem
                    {
                        Content = adapter.DisplayName,
                        Tag = adapter.Id
                    };
                    NetworkAdapterComboBox.Items.Add(item);

                    if (!string.IsNullOrEmpty(currentId) && adapter.Id == currentId)
                    {
                        selectedIndex = i;
                    }
                }

                // 若未指定网卡（自动选择），默认选第一项
                if (adapters.Count > 0 && selectedIndex < 0)
                {
                    selectedIndex = 0;
                }
                NetworkAdapterComboBox.SelectedIndex = selectedIndex;

                // 恢复事件
                NetworkAdapterComboBox.SelectionChanged += NetworkAdapterComboBox_SelectionChanged;

                // 恢复事件后，基于当前选中项同步 SelectedAdapterId 并刷新显示
                // （切换"显示虚拟网卡"或首次加载时，选中项可能变化，需确保状态一致）
                if (NetworkAdapterComboBox.SelectedItem is ComboBoxItem selectedItem
                    && selectedItem.Tag is string selectedId
                    && selectedId != _deviceConnectionManager.SelectedAdapterId)
                {
                    _deviceConnectionManager.SelectedAdapterId = selectedId;
                    RefreshConnectionDisplay();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"加载网卡列表失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 刷新二维码与 IP 地址显示（基于当前 SelectedAdapterId）
        /// </summary>
        private void RefreshConnectionDisplay()
        {
            try
            {
                // 如果当前是热点模式，刷新热点信息
                if (NetworkPreferenceComboBox?.SelectedIndex == 1)
                {
                    UpdateHotspotInfoDisplay();
                }
                else
                {
                    _deviceConnectionManager.GenerateQrCode();
                    if (_deviceConnectionManager.QrCodeImage != null)
                    {
                        QrCodeImage.Source = _deviceConnectionManager.QrCodeImage;
                    }
                    IpAddressText.Text = $"{_languageManager.GetTranslation("IPAddress")}: {_deviceConnectionManager.GetLocalIPAddress()}:{_deviceConnectionManager.Port}";
                }
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"刷新连接显示失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 显示虚拟网卡复选框状态变化：重新加载网卡列表
        /// </summary>
        private void ShowVirtualAdaptersCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                // 重新加载网卡列表（保持当前选择）
                LoadNetworkAdapters();
                Logger.Debug("MainWindow", $"显示虚拟网卡: {ShowVirtualAdaptersCheckBox.IsChecked == true}");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"切换显示虚拟网卡失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 单击文字标签：切换复选框勾选状态（文字移出 CheckBox 后保留点击勾选交互）
        /// </summary>
        private void ShowVirtualAdaptersLabel_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                ShowVirtualAdaptersCheckBox.IsChecked = !(ShowVirtualAdaptersCheckBox.IsChecked == true);
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"切换显示虚拟网卡失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 单击提示图标：显示/关闭提示悬浮框
        /// </summary>
        private void VirtualAdapterTipIcon_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (VirtualAdapterTipPopup.IsOpen)
                {
                    // 已打开则关闭
                    VirtualAdapterTipPopup.IsOpen = false;
                }
                else
                {
                    // 打开并订阅全局点击事件，点击外部时关闭
                    VirtualAdapterTipPopup.IsOpen = true;
                    System.Windows.Input.InputManager.Current.PostProcessInput += CloseTipPopupOnOutsideClick;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"切换提示悬浮框失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 全局输入处理：点击提示悬浮框外部时关闭它
        /// </summary>
        private void CloseTipPopupOnOutsideClick(object sender, System.Windows.Input.ProcessInputEventArgs e)
        {
            try
            {
                if (!VirtualAdapterTipPopup.IsOpen)
                {
                    System.Windows.Input.InputManager.Current.PostProcessInput -= CloseTipPopupOnOutsideClick;
                    return;
                }

                // 检测鼠标按下事件
                if (e.StagingItem.Input is System.Windows.Input.MouseButtonEventArgs mbe
                    && mbe.ChangedButton == System.Windows.Input.MouseButton.Left)
                {
                    // 判断点击的元素是否属于 Popup 或图标
                    var directlyOver = System.Windows.Input.Mouse.DirectlyOver as System.Windows.DependencyObject;
                    bool inPopup = IsDescendantOf(directlyOver, VirtualAdapterTipPopup.Child);
                    bool inIcon = IsDescendantOf(directlyOver, VirtualAdapterTipIcon);

                    if (!inPopup && !inIcon)
                    {
                        VirtualAdapterTipPopup.IsOpen = false;
                        System.Windows.Input.InputManager.Current.PostProcessInput -= CloseTipPopupOnOutsideClick;
                    }
                }
            }
            catch
            {
                // 忽略检测异常，避免影响正常输入
            }
        }

        /// <summary>
        /// 判断元素是否是目标的后代（沿可视化树向上查找）
        /// </summary>
        private static bool IsDescendantOf(System.Windows.DependencyObject element, System.Windows.DependencyObject target)
        {
            if (element == null || target == null) return false;
            while (element != null)
            {
                if (element == target) return true;
                element = System.Windows.Media.VisualTreeHelper.GetParent(element)
                          ?? System.Windows.LogicalTreeHelper.GetParent(element);
            }
            return false;
        }

        /// <summary>
        /// 网卡选择变化
        /// </summary>
        private void NetworkAdapterComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            try
            {
                if (NetworkAdapterComboBox.SelectedItem is ComboBoxItem item && item.Tag is string adapterId)
                {
                    _deviceConnectionManager.SelectedAdapterId = adapterId;
                    RefreshConnectionDisplay();
                    Logger.Info("MainWindow", $"切换网卡: {item.Content}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"切换网卡失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 点击返回按钮：切换回主视图
        /// </summary>
        private void ConnectDeviceBackBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ConnectDeviceSettingsView.Visibility = Visibility.Collapsed;
                ConnectDeviceMainView.Visibility = Visibility.Visible;

                // 返回主视图时刷新 IP 与二维码显示，确保切换网卡后的最新状态可见
                RefreshConnectionDisplay();

                Logger.Debug("MainWindow", "连接设备悬浮窗切换回主视图");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"切换回主视图失败: {ex.Message}", ex);
            }
        }

        private void OnConnectionStatusChanged(string status)
        {
            try
            {
                // 使用 BeginInvoke 异步派发到 UI 线程，避免阻塞 DeviceConnectionManager 的网络读取循环
                // （网络循环若被 UI 线程阻塞，可能错过 PING/PONG 导致 30 秒心跳超时误判断连）
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ConnectionStatusText != null)
                    {
                        ConnectionStatusText.Text = status;
                    }
                }));
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"更新连接状态失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 更新语言UI
        /// </summary>
        private void UpdateLanguageUI()
        {
            try
            {
                if (_languageManager == null)
                    return;

                // 连接设备悬浮窗
                ConnectDeviceTitleText.Text = _languageManager.GetTranslation("ConnectDevice");
                RefreshButtonText.Text = _languageManager.GetTranslation("Refresh");


                // 主界面按钮
                MoreButtonText.Text = _languageManager.GetTranslation("More");
                MinimizeButtonText.Text = _languageManager.GetTranslation("Minimize");
                ScanQRButtonText.Text = _languageManager.GetTranslation("ScanQR");
                MoveButtonText.Text = _languageManager.GetTranslation("Move");
                PenButtonText.Text = _languageManager.GetTranslation("Pen");
                EraserButtonText.Text = _languageManager.GetTranslation("Eraser");
                // 形状功能已移除
                UndoButtonText.Text = _languageManager.GetTranslation("Undo");
                RedoButtonText.Text = _languageManager.GetTranslation("Redo");
                ClearButtonText.Text = _languageManager.GetTranslation("Clear");
                CaptureButtonText.Text = _languageManager.GetTranslation("Capture");
                ConnectDeviceButtonText.Text = _languageManager.GetTranslation("ConnectDevice");
                PhotoRecordsTitleText.Text = _languageManager.GetTranslation("PhotoRecords");
                //SaveImageText.Text = _languageManager.GetTranslation("SaveImage");
                PenSettingsTitleText.Text = _languageManager.GetTranslation("PenSettings");

                // 更新照片按钮文字（根据当前展开状态）
                var photoPanel = FindName("PhotoPanelBorder") as Border;
                bool isPhotoPanelOpen = photoPanel != null && photoPanel.Visibility == Visibility.Visible;
                UpdatePhotoButtonState(isPhotoPanelOpen);

                Logger.Debug("MainWindow", "语言UI更新完成");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"更新语言UI失败: {ex.Message}", ex);
            }
        }

        private void OnClientConnected(string message)
        {
            try
            {
                // 使用 BeginInvoke 异步派发，避免阻塞网络读取循环（详见 OnConnectionStatusChanged 注释）
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ConnectionStatusText != null)
                    {
                        ConnectionStatusText.Text = message;
                    }
                }));
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"处理客户端连接失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 握手成功后，将照片栏中所有本地照片（Local）同步到新连接的手机客户端。
        /// 来自手机的照片（FromClient）不再次同步，以避免循环。
        /// </summary>
        private void OnHandshakeCompleted()
        {
            try
            {
                Logger.Info("MainWindow", "握手完成，开始同步本地照片到客户端");
                _ = SyncAllLocalPhotosToClientsAsync();
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"握手后同步照片失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 照片集合变化：本地新增照片自动推送到已连接客户端（来自手机的照片不再回传以避免循环）
        /// </summary>
        private void Photos_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add || e.NewItems == null)
                return;

            foreach (var item in e.NewItems)
            {
                if (item is PhotoWithStrokes photo && photo.Source == PhotoSource.Local)
                {
                    _ = SyncPhotoToClientsAsync(photo);
                }
            }
        }

        /// <summary>
        /// 同步照片栏中所有本地照片到已连接客户端（握手后调用）
        /// </summary>
        private async Task SyncAllLocalPhotosToClientsAsync()
        {
            try
            {
                if (_deviceConnectionManager == null || !_deviceConnectionManager.HasConnectedClients)
                    return;

                // 在 UI 线程获取照片快照（ObservableCollection 非线程安全，OnHandshakeCompleted 可能在网络线程触发）
                List<PhotoWithStrokes> localPhotos = null;
                await Dispatcher.InvokeAsync(() =>
                {
                    localPhotos = _photos.Where(p => p.Source == PhotoSource.Local && p.Image != null).ToList();
                });

                if (localPhotos == null || localPhotos.Count == 0)
                {
                    Logger.Info("MainWindow", "无本地照片需要同步");
                    return;
                }

                Logger.Info("MainWindow", $"开始同步 {localPhotos.Count} 张本地照片到客户端");

                foreach (var photo in localPhotos)
                {
                    await SyncPhotoToClientsAsync(photo);
                }

                Logger.Info("MainWindow", "本地照片同步完成");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"同步所有本地照片失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 将单张照片同步到所有已连接的手机客户端（标注为来自服务端）
        /// </summary>
        private async Task SyncPhotoToClientsAsync(PhotoWithStrokes photo)
        {
            try
            {
                if (_deviceConnectionManager == null || !_deviceConnectionManager.HasConnectedClients)
                    return;
                if (photo?.Image == null) return;

                var bytes = BitmapSourceToBytes(photo.Image);
                if (bytes == null || bytes.Length == 0) return;

                await _deviceConnectionManager.SendPhotoToClientsAsync(bytes, photo.PhotoId);
                Logger.Info("MainWindow", $"已推送照片到客户端，photoId={photo.PhotoId}");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"同步照片到客户端失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 将 BitmapSource 编码为 PNG 字节数组
        /// </summary>
        private byte[] BitmapSourceToBytes(BitmapSource bitmap)
        {
            try
            {
                if (bitmap == null) return null;
                var encoder = new PngBitmapEncoder();
                var frame = BitmapFrame.Create(bitmap);
                encoder.Frames.Add(frame);
                using (var stream = new System.IO.MemoryStream())
                {
                    encoder.Save(stream);
                    return stream.ToArray();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"BitmapSource 转字节数组失败: {ex.Message}", ex);
                return null;
            }
        }

        private void OnPhotoReceived(byte[] photoData)
        {
            Logger.Info("MainWindow", $"OnPhotoReceived 开始，数据大小: {photoData?.Length ?? 0} 字节");
            // 切换到 UI 线程执行，因为后续涉及 UI 操作
            Dispatcher.Invoke(() =>
            {
                ImportPhotoFromBytes(photoData);
            });
        }

        /// <summary>
        /// 从字节数据导入照片（参照文件导入逻辑）
        /// </summary>
        private void ImportPhotoFromBytes(byte[] photoData)
        {
            if (_isClosing || photoData == null || photoData.Length == 0)
                return;

            Logger.Info("MainWindow", $"开始从字节数据导入照片，大小: {photoData.Length} 字节");

            try
            {
                // 1. 保存文件
                string filePath = SaveRemotePhotoToDisk(photoData);
                Logger.Info("MainWindow", $"文件保存路径: {filePath ?? "保存失败"}");

                if (string.IsNullOrEmpty(filePath))
                {
                    Logger.Error("MainWindow", "保存文件失败");
                    return;
                }

                // 2. 创建 BitmapImage - 使用 StreamSource 直接从字节数组创建，更可靠
                var bitmap = ConvertBytesToBitmapImage(photoData);
                if (bitmap == null)
                {
                    Logger.Error("MainWindow", "BitmapImage 创建失败");
                    return;
                }

                Logger.Info("MainWindow", $"BitmapImage 创建成功，尺寸: {bitmap.PixelWidth}x{bitmap.PixelHeight}");

                // 3. 添加到照片栏（标记为来自手机客户端，不再同步回手机以避免循环）
                _photoPopupManager.AddPhoto(bitmap, null, filePath, PhotoSource.FromClient);
                Logger.Info("MainWindow", "照片已添加到照片栏（来源：手机客户端）");

                // 4. 展开照片栏
                var photoPanelBorder = FindName("PhotoPanelBorder") as Border;
                if (photoPanelBorder != null && photoPanelBorder.Visibility != Visibility.Visible)
                {
                    photoPanelBorder.Visibility = Visibility.Visible;
                    UpdatePhotoButtonState(true);
                    Logger.Info("MainWindow", "照片栏已展开");
                }

                // 5. 显示提示
                ShowPhotoTip();
                _memoryManager?.TriggerMemoryCleanup();

                Logger.Info("MainWindow", $"远程照片导入成功: {filePath}");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"从字节数据导入照片失败: {ex.Message}", ex);
            }
        }

        private string SaveRemotePhotoToDisk(byte[] photoData)
        {
            try
            {
                var saveDir = System.IO.Path.Combine(@"D:\EasiCameraPhoto", DateTime.Now.ToString("yyyy-MM-dd"));
                
                if (!System.IO.Directory.Exists(saveDir))
                {
                    System.IO.Directory.CreateDirectory(saveDir);
                }

                var fileName = $"Remote_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                var filePath = System.IO.Path.Combine(saveDir, fileName);

                System.IO.File.WriteAllBytes(filePath, photoData);
                
                Logger.Info("MainWindow", $"远程照片已保存到: {filePath}");
                return filePath;
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"保存远程照片失败: {ex.Message}", ex);
                return null;
            }
        }

        private BitmapImage ConvertBytesToBitmapImage(byte[] imageData)
        {
            try
            {
                using (var stream = new System.IO.MemoryStream(imageData))
                {
                    var bitmapImage = new BitmapImage();
                    bitmapImage.BeginInit();
                    bitmapImage.StreamSource = stream;
                    bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                    bitmapImage.EndInit();
                    bitmapImage.Freeze();
                    return bitmapImage;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"转换字节数组为BitmapImage失败: {ex.Message}", ex);
                return null;
            }
        }

        private void Ink_PreviewMouseLeftButtonDown(object sender, WinMouseButtonEventArgs e)
        {
            var mode = _drawingManager.CurrentMode;
            if (mode == DrawingManager.ToolMode.PaintBucket)
            {
                // 油漆桶模式：在点击位置填充封闭图形
                var position = e.GetPosition(sender as IInputElement);
                _drawingManager.FillClosedShape(position);
                e.Handled = true;
            }
            else if (mode == DrawingManager.ToolMode.Line || mode == DrawingManager.ToolMode.Arrow ||
                mode == DrawingManager.ToolMode.Rectangle || mode == DrawingManager.ToolMode.Ellipse ||
                mode == DrawingManager.ToolMode.Circle || mode == DrawingManager.ToolMode.DashedLine ||
                mode == DrawingManager.ToolMode.DotLine)
            {
                var position = e.GetPosition(sender as IInputElement);
                int shapeMode = mode switch
                {
                    DrawingManager.ToolMode.Line => 1,
                    DrawingManager.ToolMode.Arrow => 2,
                    DrawingManager.ToolMode.Rectangle => 3,
                    DrawingManager.ToolMode.Ellipse => 4,
                    DrawingManager.ToolMode.Circle => 5,
                    DrawingManager.ToolMode.DashedLine => 8,
                    DrawingManager.ToolMode.DotLine => 18,
                    _ => 0
                };
                _drawingManager.StartShapeDrawing(position, shapeMode);
                e.Handled = true;
            }
        }

        private void Ink_PreviewMouseMove(object sender, WinMouseEventArgs e)
        {
            var mode = _drawingManager.CurrentMode;
            if (mode == DrawingManager.ToolMode.Line || mode == DrawingManager.ToolMode.Arrow ||
                mode == DrawingManager.ToolMode.Rectangle || mode == DrawingManager.ToolMode.Ellipse ||
                mode == DrawingManager.ToolMode.Circle || mode == DrawingManager.ToolMode.DashedLine ||
                mode == DrawingManager.ToolMode.DotLine)
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    var position = e.GetPosition(sender as IInputElement);
                    _drawingManager.UpdateShapePreview(position);
                    e.Handled = true;
                }
            }
        }

        private void Ink_PreviewMouseLeftButtonUp(object sender, WinMouseButtonEventArgs e)
        {
            var mode = _drawingManager.CurrentMode;
            if (mode == DrawingManager.ToolMode.Line || mode == DrawingManager.ToolMode.Arrow ||
                mode == DrawingManager.ToolMode.Rectangle || mode == DrawingManager.ToolMode.Ellipse ||
                mode == DrawingManager.ToolMode.Circle || mode == DrawingManager.ToolMode.DashedLine ||
                mode == DrawingManager.ToolMode.DotLine)
            {
                _drawingManager.CommitShape();
                e.Handled = true;
            }
        }

        private void OverlayInk_PreviewMouseLeftButtonDown(object sender, WinMouseButtonEventArgs e)
        {
        }

        private void OverlayInk_PreviewMouseMove(object sender, WinMouseEventArgs e)
        {
        }

        private void OverlayInk_PreviewMouseLeftButtonUp(object sender, WinMouseButtonEventArgs e)
        {
        }

        private void OverlayInk_PreviewStylusDown(object sender, StylusDownEventArgs e)
        {
        }

        private void OverlayInk_PreviewStylusMove(object sender, StylusEventArgs e)
        {
        }

        private void OverlayInk_PreviewStylusUp(object sender, StylusEventArgs e)
        {
        }

        private void PenWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (PenWidthValue != null)
            {
                PenWidthValue.Text = e.NewValue.ToString("0");
                _panZoomManager.SetOriginalPenWidth(e.NewValue);
                _drawingManager.UpdatePenAttributes();
                Logger.Debug("MainWindow", $"笔迹宽度设置为: {e.NewValue:F1}");
            }
        }

        private void ColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string colorName)
            {
                try
                {
                    // 1. 更新UI选中状态
                    SelectColorButton(colorName);

                    // 2. 设置画笔颜色
                    var color = GetColorFromName(colorName);
                    _drawingManager.SetPenColor(color);

                    // 3. 应用笔迹缩放补偿
                    _panZoomManager.ApplyStrokeScaleCompensation();

                    // 4. 记录日志
                    Logger.Debug("MainWindow", $"画笔颜色设置为: {colorName}");
                }
                catch (Exception ex)
                {
                    Logger.Error("MainWindow", $"设置画笔颜色失败: {ex.Message}", ex);
                }
            }
        }

        private void ClosePenSettings_Click(object sender, RoutedEventArgs e)
        {
            PenSettingsPopup.IsOpen = false;
            Logger.Debug("MainWindow", "关闭画笔设置悬浮窗");
        }

        #region 更多颜色悬浮窗

        // HSV 状态：H[0,360), S[0,1], V[0,1]
        private double _mcHue = 0;
        private double _mcSaturation = 1;
        private double _mcValue = 1;
        private bool _mcIsUpdatingHex = false;       // 正在由 SV/Hue 反写 Hex 文本
        private bool _mcIsSvDragging = false;
        private bool _mcIsHueDragging = false;

        /// <summary>
        /// 打开更多颜色悬浮窗
        /// </summary>
        private void MoreColorsBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 用当前画笔颜色初始化
                var current = _drawingManager?.PenColor ?? System.Windows.Media.Colors.Black;
                ColorToHsv(current, out _mcHue, out _mcSaturation, out _mcValue);
                MoreColorsPopup.IsOpen = true;
                Logger.Debug("MainWindow", "打开更多颜色悬浮窗");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"打开更多颜色悬浮窗失败: {ex.Message}", ex);
            }
        }

        private void CloseMoreColors_Click(object sender, RoutedEventArgs e)
        {
            MoreColorsPopup.IsOpen = false;
        }

        private void MoreColorsPopup_Opened(object sender, EventArgs e)
        {
            try
            {
                UpdateSvHueLayer();
                UpdateSvMarker();
                UpdateHueMarker();
                UpdateHexFromHsv();
                UpdateColorPreview();
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"更多颜色悬浮窗打开失败: {ex.Message}", ex);
            }
        }

        private void MoreColorsPopup_Closed(object sender, EventArgs e)
        {
            _mcIsSvDragging = false;
            _mcIsHueDragging = false;
            Logger.Debug("MainWindow", "更多颜色悬浮窗已关闭");
        }

        private void SvSquare_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _mcIsSvDragging = true;
            SvSquare.CaptureMouse();
            UpdateSvFromMouse(e.GetPosition(SvSquare));
        }

        private void SvSquare_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _mcIsSvDragging = false;
            SvSquare.ReleaseMouseCapture();
        }

        private void SvSquare_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_mcIsSvDragging)
            {
                UpdateSvFromMouse(e.GetPosition(SvSquare));
            }
        }

        private void HueBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _mcIsHueDragging = true;
            HueBar.CaptureMouse();
            UpdateHueFromMouse(e.GetPosition(HueBar));
        }

        private void HueBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _mcIsHueDragging = false;
            HueBar.ReleaseMouseCapture();
        }

        private void HueBar_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_mcIsHueDragging)
            {
                UpdateHueFromMouse(e.GetPosition(HueBar));
            }
        }

        /// <summary>
        /// 根据鼠标在 SV 方块上的位置更新饱和度与明度
        /// </summary>
        private void UpdateSvFromMouse(System.Windows.Point pos)
        {
            double w = SvSquare.ActualWidth > 0 ? SvSquare.ActualWidth : SvSquare.Width;
            double h = SvSquare.ActualHeight > 0 ? SvSquare.ActualHeight : SvSquare.Height;
            if (w <= 0 || h <= 0) return;

            double s = Math.Clamp(pos.X / w, 0, 1);
            // 顶部最亮，底部最暗
            double v = Math.Clamp(1 - (pos.Y / h), 0, 1);

            _mcSaturation = s;
            _mcValue = v;

            UpdateSvMarker();
            UpdateHexFromHsv();
            UpdateColorPreview();
        }

        /// <summary>
        /// 根据鼠标在色相条上的位置更新色相
        /// </summary>
        private void UpdateHueFromMouse(System.Windows.Point pos)
        {
            double w = HueBar.ActualWidth > 0 ? HueBar.ActualWidth : HueBar.Width;
            if (w <= 0) return;

            double ratio = Math.Clamp(pos.X / w, 0, 1);
            _mcHue = ratio * 360.0;
            if (_mcHue >= 360.0) _mcHue = 0;

            UpdateSvHueLayer();
            UpdateHueMarker();
            UpdateHexFromHsv();
            UpdateColorPreview();
        }

        /// <summary>
        /// 更新 SV 方块底层纯色（当前色相）
        /// </summary>
        private void UpdateSvHueLayer()
        {
            if (SvHueLayer == null) return;
            var pure = HsvToColor(_mcHue, 1, 1);
            SvHueLayer.Fill = new SolidColorBrush(pure);
        }

        /// <summary>
        /// 更新 SV 选中位置标记
        /// </summary>
        private void UpdateSvMarker()
        {
            if (SvMarker == null || SvSquare == null) return;
            double w = SvSquare.ActualWidth > 0 ? SvSquare.ActualWidth : SvSquare.Width;
            double h = SvSquare.ActualHeight > 0 ? SvSquare.ActualHeight : SvSquare.Height;
            double x = _mcSaturation * w - SvMarker.Width / 2.0;
            double y = (1 - _mcValue) * h - SvMarker.Height / 2.0;
            Canvas.SetLeft(SvMarker, x);
            Canvas.SetTop(SvMarker, y);
        }

        /// <summary>
        /// 更新色相条指示器位置
        /// </summary>
        private void UpdateHueMarker()
        {
            if (HueMarker == null || HueBar == null) return;
            double w = HueBar.ActualWidth > 0 ? HueBar.ActualWidth : HueBar.Width;
            double x = (_mcHue / 360.0) * w - HueMarker.Width / 2.0;
            Canvas.SetLeft(HueMarker, x);
        }

        /// <summary>
        /// 由 HSV 推导 Hex 并写入输入框
        /// </summary>
        private void UpdateHexFromHsv()
        {
            if (HexInput == null) return;
            var c = HsvToColor(_mcHue, _mcSaturation, _mcValue);
            _mcIsUpdatingHex = true;
            try
            {
                HexInput.Text = string.Format(System.Globalization.CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
            }
            finally
            {
                _mcIsUpdatingHex = false;
            }
        }

        /// <summary>
        /// 更新当前颜色预览方块
        /// </summary>
        private void UpdateColorPreview()
        {
            if (MoreColorPreview == null) return;
            var c = HsvToColor(_mcHue, _mcSaturation, _mcValue);
            MoreColorPreview.Background = new SolidColorBrush(c);
        }

        /// <summary>
        /// 16 进制输入框文本变化：尝试解析并反推 SV/Hue
        /// </summary>
        private void HexInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_mcIsUpdatingHex) return;
            if (HexInput == null) return;

            var text = HexInput.Text?.Trim() ?? string.Empty;
            // 兼容带 # 与不带 #
            if (string.IsNullOrEmpty(text)) return;
            if (!text.StartsWith("#"))
            {
                // 仅在用户输入合法字符时尝试补 #
                if (text.Length == 6 || text.Length == 8)
                {
                    text = "#" + text;
                }
                else
                {
                    return;
                }
            }

            try
            {
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(text);
                ColorToHsv(color, out double h, out double s, out double v);
                _mcHue = h;
                _mcSaturation = s;
                _mcValue = v;
                UpdateSvHueLayer();
                UpdateSvMarker();
                UpdateHueMarker();
                UpdateColorPreview();
            }
            catch
            {
                // 输入不完整时忽略
            }
        }

        /// <summary>
        /// 确定应用所选颜色到画笔
        /// </summary>
        private void MoreColorsOk_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var color = HsvToColor(_mcHue, _mcSaturation, _mcValue);
                _drawingManager.SetPenColor(color);
                _panZoomManager.ApplyStrokeScaleCompensation();

                // 隐藏预设色块的对钩（自定义颜色不在预设集合内）
                HideAllCheckIcons();
                _currentPenColor = "Custom";

                Logger.Debug("MainWindow", $"画笔颜色通过更多颜色选取设置为: #{color.R:X2}{color.G:X2}{color.B:X2}");
                MoreColorsPopup.IsOpen = false;
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"应用更多颜色失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// HSV -> Color
        /// </summary>
        private static System.Windows.Media.Color HsvToColor(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            s = Math.Clamp(s, 0, 1);
            v = Math.Clamp(v, 0, 1);

            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;

            double r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }

            return System.Windows.Media.Color.FromRgb(
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255));
        }

        /// <summary>
        /// Color -> HSV
        /// </summary>
        private static void ColorToHsv(System.Windows.Media.Color color, out double h, out double s, out double v)
        {
            double r = color.R / 255.0;
            double g = color.G / 255.0;
            double b = color.B / 255.0;

            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double delta = max - min;

            v = max;
            s = (max <= 0) ? 0 : delta / max;

            if (delta <= 0.000001)
            {
                h = 0; // 灰色，色相未定义，取 0
            }
            else if (max == r)
            {
                h = 60.0 * (((g - b) / delta) % 6);
            }
            else if (max == g)
            {
                h = 60.0 * (((b - r) / delta) + 2);
            }
            else
            {
                h = 60.0 * (((r - g) / delta) + 4);
            }

            if (h < 0) h += 360.0;
        }

        #endregion

        private void ClearInk_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing) return;
            _drawingManager.ClearStrokes();
            Logger.Info("MainWindow", "清除所有笔迹");
        }

        private void UndoInk_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing) return;
            _drawingManager.Undo();
            Logger.Debug("MainWindow", "撤销操作");
        }

        private void RedoInk_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing) return;
            _drawingManager.Redo();
            Logger.Debug("MainWindow", "重做操作");
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            MoreMenuPopup.IsOpen = !MoreMenuPopup.IsOpen;
            Logger.Debug("MainWindow", "切换更多菜单显示状态");
        }

        private void PhotoImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement image && image.DataContext is PhotoWithStrokes photo)
            {
                if (_isSaveSelectionMode)
                {
                    photo.IsSelected = !photo.IsSelected;
                }
                else
                {
                    if (PhotoList.SelectedItem == photo)
                    {
                        PhotoList.SelectedIndex = -1;
                        _photoPopupManager.BackToLive();
                    }
                    else
                    {
                        PhotoList.SelectedItem = photo;
                    }
                }
            }
        }

        private void DeletePhoto_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is PhotoWithStrokes photo)
            {
                // 切换到确认状态：隐藏删除按钮，显示确认/取消按钮
                var parent = VisualTreeHelper.GetParent(button);
                var deleteConfirmPanel = FindVisualChildByName<StackPanel>(parent, "DeleteConfirmPanel");
                if (deleteConfirmPanel != null)
                {
                    button.Visibility = Visibility.Collapsed;
                    deleteConfirmPanel.Visibility = Visibility.Visible;
                }
            }
        }

        private void ConfirmDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is PhotoWithStrokes photo)
            {
                bool wasViewing = _photoPopupManager.CurrentPhoto == photo;

                _photoPopupManager.GetPhotos().Remove(photo);
                _photoPopupManager.UpdatePhotoIndexes();

                if (wasViewing)
                {
                    _photoPopupManager.BackToLive();
                }

                Logger.Info("MainWindow", $"已删除照片 {photo.Index}");
            }
        }

        private void CancelDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                // 恢复删除按钮显示，隐藏确认/取消按钮组
                var parent = VisualTreeHelper.GetParent(button);
                while (parent != null && !(parent is StackPanel panel && panel.Name == "DeleteConfirmPanel"))
                {
                    parent = VisualTreeHelper.GetParent(parent);
                }
                if (parent is StackPanel confirmPanel)
                {
                    confirmPanel.Visibility = Visibility.Collapsed;
                    var grandParent = VisualTreeHelper.GetParent(confirmPanel);
                    var deleteBtn = FindVisualChildByName<Button>(grandParent, "DeletePhotoButton");
                    if (deleteBtn != null)
                    {
                        deleteBtn.Visibility = Visibility.Visible;
                    }
                }
            }
        }

        private T FindVisualChildByName<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            if (parent == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed && typed.Name == name)
                    return typed;
                var result = FindVisualChildByName<T>(child, name);
                if (result != null) return result;
            }
            return null;
        }

        private void CancelSelect_Click(object sender, RoutedEventArgs e)
        {
            ExitSaveSelectionMode();
        }

        private void TogglePhotoPanel_Click(object sender, RoutedEventArgs e)
        {
            var photoPanelBorder = FindName("PhotoPanelBorder") as Border;
            if (photoPanelBorder != null)
            {
                if (photoPanelBorder.Visibility == Visibility.Visible)
                {
                    photoPanelBorder.Visibility = Visibility.Collapsed;
                    if (_isSaveSelectionMode)
                    {
                        ExitSaveSelectionMode();
                    }
                    UpdatePhotoButtonState(false);
                    Logger.Debug("MainWindow", "照片栏收起");
                }
                else
                {
                    photoPanelBorder.Visibility = Visibility.Visible;
                    UpdatePhotoButtonState(true);
                    Logger.Debug("MainWindow", "照片栏展开");
                }
            }
        }

        private void UpdatePhotoButtonState(bool isPanelOpen)
        {
            if (PhotoIconViewbox != null && CloseIconViewbox != null && TogglePhotoButtonText != null)
            {
                if (isPanelOpen)
                {
                    PhotoIconViewbox.Visibility = Visibility.Collapsed;
                    CloseIconViewbox.Visibility = Visibility.Visible;
                    TogglePhotoButtonText.Text = _languageManager?.GetTranslation("Close") ?? "关闭";
                }
                else
                {
                    PhotoIconViewbox.Visibility = Visibility.Visible;
                    CloseIconViewbox.Visibility = Visibility.Collapsed;
                    TogglePhotoButtonText.Text = _languageManager?.GetTranslation("Photos") ?? "照片";
                }
            }
        }

        private void OnPhotoPopupOpened()
        {
        }

        private void OnPhotoPopupClosed()
        {
        }

        private async void NetworkPreferenceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (e.RemovedItems.Count == 0)
                    return;

                var selectedItem = NetworkPreferenceComboBox.SelectedItem as ComboBoxItem;
                if (selectedItem == null)
                    return;

                int selectedIndex = NetworkPreferenceComboBox.SelectedIndex;
                string preference = selectedIndex == 1 ? "Hotspot" : "LAN";

                if (_deviceConnectionManager != null && _deviceConnectionManager.ConnectedDeviceCount > 0)
                {
                    var confirmResult = MessageBox.Show(
                        _languageManager.GetTranslation("SwitchNetworkConfirm"),
                        "",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (confirmResult != MessageBoxResult.Yes)
                    {
                        NetworkPreferenceComboBox.SelectionChanged -= NetworkPreferenceComboBox_SelectionChanged;
                        NetworkPreferenceComboBox.SelectedIndex = preference == "Hotspot" ? 0 : 1;
                        NetworkPreferenceComboBox.SelectionChanged += NetworkPreferenceComboBox_SelectionChanged;
                        return;
                    }
                }

                config.NetworkPreference = preference;

                if (preference == "Hotspot")
                {
                    // 切换期间禁用选择框，防止重复操作
                    NetworkPreferenceComboBox.IsEnabled = false;

                    string ssid = GetCurrentHotspotSsid();
                    string password = GetCurrentHotspotPassword();
                    var result = await _hotspotService.ConfigureAndStartHotspot(ssid, password);

                    if (result.Success)
                    {
                        string ip = _hotspotService.GetHotspotIPAddress();
                        int port = _deviceConnectionManager?.Port ?? 8888;
                        // 热点模式：JSON 格式，头部带 type 字段以标识当前模式
                        var json = $"{{\"type\":\"hotspot\",\"ssid\":\"{ssid}\",\"password\":\"{password}\",\"ip\":\"{ip}\",\"port\":{port}}}";
                        QrCodeImage.Source = GenerateQrCodeFromContent(json);
                        IpAddressText.Text = $"{_languageManager.GetTranslation("IPAddress")}: {ip}:{port}";
                        UpdateHotspotInfoDisplay();
                        NetworkPreferenceComboBox.IsEnabled = true;
                        Logger.Info("MainWindow", "热点模式已启用");
                    }
                    else
                    {
                        string errorMsg = GetHotspotErrorMessage(result.FailureReason);
                        MessageBox.Show($"{errorMsg}\n{_languageManager.GetTranslation("HotspotRevertToLAN")}", "", MessageBoxButton.OK, MessageBoxImage.Warning);

                        NetworkPreferenceComboBox.SelectionChanged -= NetworkPreferenceComboBox_SelectionChanged;
                        NetworkPreferenceComboBox.SelectedIndex = 0;
                        NetworkPreferenceComboBox.SelectionChanged += NetworkPreferenceComboBox_SelectionChanged;

                        config.NetworkPreference = "LAN";
                        RefreshLanQrCode();
                        UpdateHotspotInfoDisplay();
                        NetworkPreferenceComboBox.IsEnabled = true;
                    }
                }
                else
                {
                    RefreshLanQrCode();
                    UpdateHotspotInfoDisplay();
                    Logger.Info("MainWindow", "已切换到局域网模式");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"切换网络偏好失败: {ex.Message}", ex);
                NetworkPreferenceComboBox.IsEnabled = true;
            }
        }

        private string GetHotspotErrorMessage(Services.HotspotFailureReason? reason)
        {
            return reason switch
            {
                Services.HotspotFailureReason.UacDenied => _languageManager.GetTranslation("HotspotNeedsAdmin"),
                Services.HotspotFailureReason.NotSupported => _languageManager.GetTranslation("HotspotNotSupported"),
                Services.HotspotFailureReason.Timeout => _languageManager.GetTranslation("HotspotTimeout"),
                Services.HotspotFailureReason.StartFailed => _languageManager.GetTranslation("HotspotStartFailed"),
                _ => _languageManager.GetTranslation("HotspotConfigFailed")
            };
        }

        private void RefreshLanQrCode()
        {
            try
            {
                EnsureDeviceConnectionManager();
                _deviceConnectionManager.GenerateQrCode();
                QrCodeImage.Source = _deviceConnectionManager.QrCodeImage;
                IpAddressText.Text = $"{_languageManager.GetTranslation("IPAddress")}: {_deviceConnectionManager.GetLocalIPAddress()}:{_deviceConnectionManager.Port}";
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"刷新局域网二维码失败: {ex.Message}", ex);
            }
        }

        private bool _isFirstNetworkPreferenceRestore = true;

        private void RestoreNetworkPreference()
        {
            try
            {
                if (NetworkPreferenceComboBox == null) return;

                // 恢复热点设置 UI（SSID 模式、密码等从 config 加载）
                RestoreHotspotSettings();

                // 读取当前内存中的网络偏好（软件启动时默认 LAN，运行期间切换后保留）
                string pref = config.NetworkPreference ?? "LAN";

                NetworkPreferenceComboBox.SelectionChanged -= NetworkPreferenceComboBox_SelectionChanged;
                NetworkPreferenceComboBox.SelectedIndex = pref == "Hotspot" ? 1 : 0;
                NetworkPreferenceComboBox.SelectionChanged += NetworkPreferenceComboBox_SelectionChanged;

                UpdateHotspotInfoDisplay();

                if (pref == "Hotspot")
                {
                    if (_isFirstNetworkPreferenceRestore)
                    {
                        // 软件启动后首次打开悬浮窗：热点尚未运行，需要启动热点
                        _isFirstNetworkPreferenceRestore = false;
                        _ = SwitchToHotspotAsync();
                    }
                    else
                    {
                        // 悬浮窗关闭再打开：热点已在后台运行，立即刷新二维码（无需等待）
                        RefreshHotspotQrCode();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"恢复网络偏好失败: {ex.Message}", ex);
            }
        }

        private async System.Threading.Tasks.Task SwitchToHotspotAsync()
        {
            string ssid = GetCurrentHotspotSsid();
            string password = GetCurrentHotspotPassword();
            var result = await _hotspotService.ConfigureAndStartHotspot(ssid, password);

            if (result.Success)
            {
                string ip = _hotspotService.GetHotspotIPAddress();
                int port = _deviceConnectionManager?.Port ?? 8888;
                // 热点模式：JSON 格式，头部带 type 字段以标识当前模式
                var json = $"{{\"type\":\"hotspot\",\"ssid\":\"{ssid}\",\"password\":\"{password}\",\"ip\":\"{ip}\",\"port\":{port}}}";
                QrCodeImage.Source = GenerateQrCodeFromContent(json);
                IpAddressText.Text = $"{_languageManager.GetTranslation("IPAddress")}: {ip}:{port}";
                UpdateHotspotInfoDisplay();
                Logger.Info("MainWindow", "启动时恢复热点模式成功");
            }
            else
            {
                string errorMsg = GetHotspotErrorMessage(result.FailureReason);
                Logger.Warning("MainWindow", $"启动时恢复热点模式失败: {errorMsg}");

                NetworkPreferenceComboBox.SelectionChanged -= NetworkPreferenceComboBox_SelectionChanged;
                NetworkPreferenceComboBox.SelectedIndex = 0;
                NetworkPreferenceComboBox.SelectionChanged += NetworkPreferenceComboBox_SelectionChanged;

                config.NetworkPreference = "LAN";
                RefreshLanQrCode();
                UpdateHotspotInfoDisplay();
            }
        }

        // --------- Hotspot support ---------

        /// <summary>
        /// 获取当前配置的热点 SSID
        /// </summary>
        private string GetCurrentHotspotSsid()
        {
            return config.HotspotSsidMode switch
            {
                0 => Environment.MachineName,
                2 => string.IsNullOrEmpty(config.HotspotCustomSsid) ? Services.HotspotConfig.Ssid : config.HotspotCustomSsid,
                _ => Services.HotspotConfig.Ssid
            };
        }

        /// <summary>
        /// 获取当前配置的热点密码
        /// </summary>
        private string GetCurrentHotspotPassword()
        {
            return string.IsNullOrEmpty(config.HotspotPassword) ? Services.HotspotConfig.Password : config.HotspotPassword;
        }

        /// <summary>
        /// 更新热点信息显示面板
        /// </summary>
        private void UpdateHotspotInfoDisplay()
        {
            if (HotspotInfoPanel == null) return;

            bool isHotspot = NetworkPreferenceComboBox?.SelectedIndex == 1;
            HotspotInfoPanel.Visibility = isHotspot ? Visibility.Visible : Visibility.Collapsed;

            if (isHotspot)
            {
                string ssid = GetCurrentHotspotSsid();
                string password = GetCurrentHotspotPassword();
                HotspotSsidDisplay.Text = $"{_languageManager.GetTranslation("HotspotSSID")}: {ssid}";
                HotspotPasswordDisplay.Text = $"{_languageManager.GetTranslation("HotspotPassword")}: {password}";
            }
        }

        /// <summary>
        /// 仅刷新热点二维码（不重启热点），用于悬浮窗重新打开等场景。
        /// 假定热点已经在运行，直接读取当前热点适配器 IP。
        /// </summary>
        private void RefreshHotspotQrCode()
        {
            try
            {
                string ssid = GetCurrentHotspotSsid();
                string password = GetCurrentHotspotPassword();
                string ip = _hotspotService.GetHotspotIPAddress();
                int port = _deviceConnectionManager?.Port ?? 8888;
                // 热点模式：JSON 格式，头部带 type 字段以标识当前模式
                var json = $"{{\"type\":\"hotspot\",\"ssid\":\"{ssid}\",\"password\":\"{password}\",\"ip\":\"{ip}\",\"port\":{port}}}";
                QrCodeImage.Source = GenerateQrCodeFromContent(json);
                IpAddressText.Text = $"{_languageManager.GetTranslation("IPAddress")}: {ip}:{port}";
                Logger.Debug("MainWindow", $"已刷新热点二维码 SSID={ssid} IP={ip}:{port}");
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"刷新热点二维码失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 应用热点设置变更：重新配置系统热点并刷新二维码。
        /// 仅在当前为热点模式且密码合法（≥8位）时执行。
        /// </summary>
        private async System.Threading.Tasks.Task ApplyHotspotSettingsAsync()
        {
            // 仅在热点模式下才需要重配热点
            if (NetworkPreferenceComboBox?.SelectedIndex != 1) return;

            string password = GetCurrentHotspotPassword();
            if (password.Length < 8)
            {
                Logger.Debug("MainWindow", "热点密码不足8位，跳过重新配置热点");
                return;
            }

            string ssid = GetCurrentHotspotSsid();
            Logger.Info("MainWindow", $"实时应用热点设置: SSID={ssid}");

            // 重新配置并启动热点（TetheringManager 会更新 SSID/密码配置）
            var result = await _hotspotService.ConfigureAndStartHotspot(ssid, password);
            if (result.Success)
            {
                RefreshHotspotQrCode();
                UpdateHotspotInfoDisplay();
                Logger.Info("MainWindow", "热点设置已实时更新");
            }
            else
            {
                Logger.Warning("MainWindow", $"实时更新热点设置失败: {result.ErrorMessage}");
            }
        }

        private void HotspotSsidModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (HotspotSsidModeComboBox == null) return;
                if (e.RemovedItems.Count == 0) return;

                int mode = HotspotSsidModeComboBox.SelectedIndex;
                config.HotspotSsidMode = mode;
                SaveConfig();

                // 仅自定义模式显示输入框
                HotspotCustomSsidText.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;

                UpdateHotspotInfoDisplay();
                // 实时更新系统热点配置和二维码
                _ = ApplyHotspotSettingsAsync();
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"热点 SSID 模式切换失败: {ex.Message}", ex);
            }
        }

        private void HotspotCustomSsidText_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                if (HotspotCustomSsidText == null) return;
                config.HotspotCustomSsid = HotspotCustomSsidText.Text;
                SaveConfig();
                UpdateHotspotInfoDisplay();
                // 实时更新系统热点配置和二维码
                _ = ApplyHotspotSettingsAsync();
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"热点自定义 SSID 修改失败: {ex.Message}", ex);
            }
        }

        private void HotspotPasswordText_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                if (HotspotPasswordText == null) return;
                string pwd = HotspotPasswordText.Text;
                if (pwd.Length < 8)
                {
                    HotspotPasswordText.BorderBrush = System.Windows.Media.Brushes.Red;
                    HotspotPasswordText.ToolTip = "密码至少需要8位";
                }
                else
                {
                    HotspotPasswordText.ClearValue(System.Windows.Controls.TextBox.BorderBrushProperty);
                    HotspotPasswordText.ToolTip = null;
                }
                config.HotspotPassword = pwd;
                SaveConfig();
                UpdateHotspotInfoDisplay();
                // 实时更新系统热点配置和二维码（密码不足8位时内部会跳过重配热点）
                _ = ApplyHotspotSettingsAsync();
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"热点密码修改失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 从 config 恢复热点设置 UI 状态
        /// </summary>
        private void RestoreHotspotSettings()
        {
            try
            {
                if (HotspotSsidModeComboBox == null) return;

                HotspotSsidModeComboBox.SelectionChanged -= HotspotSsidModeComboBox_SelectionChanged;
                HotspotSsidModeComboBox.SelectedIndex = config.HotspotSsidMode;
                HotspotSsidModeComboBox.SelectionChanged += HotspotSsidModeComboBox_SelectionChanged;

                HotspotCustomSsidText.Text = config.HotspotCustomSsid ?? "";
                HotspotCustomSsidText.Visibility = config.HotspotSsidMode == 2 ? Visibility.Visible : Visibility.Collapsed;

                HotspotPasswordText.Text = config.HotspotPassword ?? "12345678";
            }
            catch (Exception ex)
            {
                Logger.Error("MainWindow", $"恢复热点设置失败: {ex.Message}", ex);
            }
        }

        private BitmapImage GenerateQrCodeFromContent(string content)
        {
            var generator = new QRCodeGenerator();
            var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
            var qr = new QRCode(data);
            var bitmap = qr.GetGraphic(10);
            var bitmapImage = new BitmapImage();
            using (var stream = new MemoryStream())
            {
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                stream.Position = 0;
                bitmapImage.BeginInit();
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.StreamSource = stream;
                bitmapImage.EndInit();
                bitmapImage.Freeze();
            }
            return bitmapImage;
        }

        private void BackToLive_Click(object sender, RoutedEventArgs e)
        {
            _photoPopupManager.BackToLive();
        }

        #endregion

        #region 公开属性（用于数据绑定）

        /// <summary>
        /// 当前选中的照片（用于数据绑定）
        /// </summary>
        public PhotoWithStrokes CurrentPhoto
        {
            get { return _photoPopupManager?.CurrentPhoto; }
        }

        #endregion
    }
}
