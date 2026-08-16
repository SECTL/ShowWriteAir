using ShowWrite.Models;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using StylusPointCollection = System.Windows.Input.StylusPointCollection;
using Stroke = System.Windows.Ink.Stroke;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using Path = System.IO.Path;
using WinPoint = System.Windows.Point;
using WinImage = System.Windows.Controls.Image;
using DrawingImage = System.Windows.Media.DrawingImage;

namespace ShowWrite
{
    public class DrawingManager : IDisposable
    {
        public enum ToolMode { None, Move, Pen, Eraser, Line, Arrow, Rectangle, Ellipse, Circle, DashedLine, DotLine, PaintBucket }

        private readonly InkCanvas _inkCanvas;
        private readonly FrameworkElement _videoArea;
        private readonly Window _mainWindow;

        public ToolMode CurrentMode { get; private set; } = ToolMode.None;

        private InkCanvas _overlayInkCanvas;
        private ScaleTransform _zoomTransform;
        private TranslateTransform _panTransform;
        private bool _useOverlayInkCanvas = true;

        // 编辑历史
        private readonly Stack<EditAction> _editHistory = new Stack<EditAction>();
        private readonly Stack<EditAction> _redoHistory = new Stack<EditAction>();
        private EditAction? _currentEdit = null;
        private bool _isEditing = false;

        private class EditAction
        {
            public List<Stroke> AddedStrokes { get; } = new();
            public List<Stroke> RemovedStrokes { get; } = new();
        }

        // 缩放比例 & 用户笔宽
        public double CurrentZoom { get; set; } = 1.0;
        public double UserPenWidth { get; set; } = 2.0; // 改为 set 可访问
        public Color PenColor => _inkCanvas.DefaultDrawingAttributes.Color;

        // 触摸点跟踪
        private readonly Dictionary<int, WinPoint> _touchPoints = new Dictionary<int, WinPoint>();
        private double _lastTouchDistance = -1;
        private WinPoint _lastTouchCenter;

        // 橡皮擦设置
        private double _manualEraserSize = 20.0;

        // 画板模式：启用后橡皮擦为正圆形，且大小不受画板缩放影响
        private bool _enableCanvasMode = false;
        public bool EnableCanvasMode
        {
            get => _enableCanvasMode;
            set
            {
                _enableCanvasMode = value;
                // 画板模式启用时强制使用圆形橡皮擦
                if (_enableCanvasMode)
                {
                    _isEraserCircleShape = true;
                }
                UpdateEraserStyle();
            }
        }

        // =========================
        // 高级橡皮擦系统 (从 DrawingAndErasingLogic.cs 移植)
        // =========================
        private bool _isUsingGeometryEraser = false;
        private IncrementalStrokeHitTester _hitTester = null;
        private double _eraserWidth = 64;
        private bool _isEraserCircleShape = false;
        private bool _isUsingStrokesEraser = false;
        // 当前橡皮擦在画布坐标系下的尺寸（用于擦除填充图片）
        private double _canvasEraserWidth = 0;
        private double _canvasEraserHeight = 0;
        private Matrix _scaleMatrix = new Matrix();
        private System.Windows.Controls.Canvas _eraserOverlayCanvas;
        private WinImage _eraserFeedback;
        private TranslateTransform _eraserFeedbackTranslateTransform;
        private static readonly Guid _isLockGuid = new Guid("12345678-1234-1234-1234-123456789ABC");
        // 油漆桶填充图片在 InkCanvas.Children 中的标识
        private const string _fillImageTag = "PaintBucketFill";

        // =========================
        // 绘制系统增强 (从 DrawingAndErasingLogic.cs 移植)
        // =========================
        private bool _isLongPressSelected = false;
        private bool _isMouseDown = false;
        private bool _isTouchDown = false;
        private WinPoint _iniP = new WinPoint(0, 0);
        private DateTime _lastUpdateTime = DateTime.MinValue;
        private const int _updateThrottleMs = 16;
        private Stroke _lastTempStroke;
        private StrokeCollection _lastTempStrokeCollection = new StrokeCollection();

        // =========================
        // 多点触控系统增强 (从 DrawingAndErasingLogic.cs 移植)
        // =========================
        private bool _isInMultiTouchMode = false;
        private List<int> _dec = new List<int>();
        private WinPoint _centerPoint = new WinPoint(0, 0);
        private InkCanvasEditingMode _lastInkCanvasEditingMode = InkCanvasEditingMode.Ink;
        private const double _multiTouchDelayMs = 100;

        // 鼠标平移状态
        private bool _isPanning = false;
        private WinPoint _lastMousePos;

        // =========================
        // 形状绘制功能 (从 MW_ShapeDrawing.cs 移植)
        // =========================
        private int _drawingShapeMode = 0; // 0=无, 1=直线, 2=箭头, 3=矩形, 4=椭圆, 5=圆, 8=虚线, 18=点线
        private bool _isDrawingShape = false;
        private WinPoint _shapeStartPoint;
        private Stroke _tempStroke;
        private StrokeCollection _tempStrokeCollection;

        // =========================
        // 手写笔预览功能 (从 MW_TouchEvents.cs 移植)
        // =========================
        private readonly Dictionary<int, StrokeVisual> _strokeVisualList = new Dictionary<int, StrokeVisual>();
        private readonly Dictionary<int, VisualCanvas> _visualCanvasList = new Dictionary<int, VisualCanvas>();
        private readonly Dictionary<int, InkCanvasEditingMode> _touchDownPointsList = new Dictionary<int, InkCanvasEditingMode>();

        // =========================
        // UI 元素保护机制
        // =========================
        private List<UIElement> _preservedElements;

        /// <summary>
        /// 保存画布上的非笔画元素（如图片、媒体元素等）
        /// </summary>
        public List<UIElement> PreserveNonStrokeElements()
        {
            var preservedElements = new List<UIElement>();

            try
            {
                // 遍历 inkCanvas 的所有子元素
                for (int i = _inkCanvas.Children.Count - 1; i >= 0; i--)
                {
                    var child = _inkCanvas.Children[i];

                    // 保存图片、媒体元素等非笔画相关的 UI 元素
                    if (child is WinImage || child is MediaElement ||
                        (child is Border border && border.Name != "EraserOverlayCanvas"))
                    {
                        // 创建元素的深拷贝，避免直接引用导致的问题
                        var clonedElement = CloneUIElement(child);
                        if (clonedElement != null)
                        {
                            preservedElements.Add(clonedElement);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"保存非笔画元素失败: {ex.Message}");
            }

            return preservedElements;
        }

        /// <summary>
        /// 克隆 UI 元素，创建深拷贝
        /// </summary>
        private UIElement CloneUIElement(UIElement originalElement)
        {
            try
            {
                if (originalElement is WinImage originalImage)
                {
                    var clonedImage = new WinImage();

                    // 复制图片源
                    if (originalImage.Source is BitmapSource bitmapSource)
                    {
                        clonedImage.Source = bitmapSource;
                    }

                    // 复制属性
                    clonedImage.Width = originalImage.Width;
                    clonedImage.Height = originalImage.Height;
                    clonedImage.Stretch = originalImage.Stretch;
                    clonedImage.StretchDirection = originalImage.StretchDirection;
                    clonedImage.Name = originalImage.Name;
                    clonedImage.IsHitTestVisible = originalImage.IsHitTestVisible;
                    clonedImage.Focusable = originalImage.Focusable;
                    clonedImage.Cursor = originalImage.Cursor;
                    clonedImage.IsManipulationEnabled = originalImage.IsManipulationEnabled;

                    // 复制位置
                    InkCanvas.SetLeft(clonedImage, InkCanvas.GetLeft(originalImage));
                    InkCanvas.SetTop(clonedImage, InkCanvas.GetTop(originalImage));

                    // 复制变换
                    if (originalImage.RenderTransform != null)
                    {
                        clonedImage.RenderTransform = originalImage.RenderTransform.Clone();
                    }

                    return clonedImage;
                }
                else if (originalElement is MediaElement originalMedia)
                {
                    var clonedMedia = new MediaElement
                    {
                        Source = originalMedia.Source,
                        Width = originalMedia.Width,
                        Height = originalMedia.Height,
                        Name = originalMedia.Name,
                        IsHitTestVisible = originalMedia.IsHitTestVisible,
                        Focusable = originalMedia.Focusable,
                        RenderTransform = originalMedia.RenderTransform?.Clone()
                    };

                    // 复制位置
                    InkCanvas.SetLeft(clonedMedia, InkCanvas.GetLeft(originalMedia));
                    InkCanvas.SetTop(clonedMedia, InkCanvas.GetTop(originalMedia));

                    return clonedMedia;
                }
                else if (originalElement is Border originalBorder)
                {
                    var clonedBorder = new Border
                    {
                        Width = originalBorder.Width,
                        Height = originalBorder.Height,
                        Name = originalBorder.Name,
                        IsHitTestVisible = originalBorder.IsHitTestVisible,
                        Focusable = originalBorder.Focusable,
                        Background = originalBorder.Background,
                        BorderBrush = originalBorder.BorderBrush,
                        BorderThickness = originalBorder.BorderThickness,
                        CornerRadius = originalBorder.CornerRadius,
                        RenderTransform = originalBorder.RenderTransform?.Clone()
                    };

                    // 复制位置
                    InkCanvas.SetLeft(clonedBorder, InkCanvas.GetLeft(originalBorder));
                    InkCanvas.SetTop(clonedBorder, InkCanvas.GetTop(originalBorder));

                    return clonedBorder;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"克隆 UI 元素失败: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// 恢复之前保存的非笔画元素到画布
        /// </summary>
        public void RestoreNonStrokeElements(List<UIElement> preservedElements)
        {
            if (preservedElements == null) return;

            try
            {
                foreach (var element in preservedElements)
                {
                    // 由于现在使用的是克隆的元素，不需要检查 Parent 属性
                    _inkCanvas.Children.Add(element);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"恢复非笔画元素失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 清除画布但保留非笔画元素
        /// </summary>
        public void ClearCanvasPreserveElements()
        {
            try
            {
                // 保存非笔画元素
                _preservedElements = PreserveNonStrokeElements();

                // 清除所有子元素
                _inkCanvas.Children.Clear();

                // 恢复非笔画元素
                RestoreNonStrokeElements(_preservedElements);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"清除画布失败: {ex.Message}");
            }
        }

        // =========================
        // 触摸事件处理改进
        // =========================
        private bool _isMultiTouchMode = false;
        private bool _isSingleFingerDragMode = false;
        private readonly List<int> _touchDeviceIds = new List<int>();
        private DateTime _lastTouchDownTime = DateTime.MinValue;
        private const double MultiTouchDelayMs = 100;

        /// <summary>
        /// 检查是否为多点触控
        /// </summary>
        public bool IsMultiTouchMode => _isMultiTouchMode;

        /// <summary>
        /// 检查是否为单指拖拽模式
        /// </summary>
        public bool IsSingleFingerDragMode => _isSingleFingerDragMode;

        /// <summary>
        /// 切换单指拖拽模式
        /// </summary>
        public void ToggleSingleFingerDragMode()
        {
            _isSingleFingerDragMode = !_isSingleFingerDragMode;
            Console.WriteLine($"单指拖拽模式: {_isSingleFingerDragMode}");
        }

        /// <summary>
        /// 取消单指拖拽模式
        /// </summary>
        public void CancelSingleFingerDragMode()
        {
            if (_isSingleFingerDragMode)
            {
                _isSingleFingerDragMode = false;
                Console.WriteLine("已取消单指拖拽模式");
            }
        }

        /// <summary>
        /// 处理触摸按下事件
        /// </summary>
        public void HandleTouchDown(int touchId, WinPoint position)
        {
            _touchDeviceIds.Add(touchId);
            _lastTouchDownTime = DateTime.Now;

            // 检测多点触控
            if (_touchDeviceIds.Count > 1)
            {
                _isMultiTouchMode = true;
                Console.WriteLine("进入多点触控模式");
            }
            else
            {
                _isMultiTouchMode = false;
            }

            // 记录触摸点
            _touchPoints[touchId] = position;

            // 如果正在绘制形状，开始形状绘制
            if (_isDrawingShape)
            {
                StartShapeDrawing(position, _drawingShapeMode);
            }
        }

        /// <summary>
        /// 处理触摸移动事件
        /// </summary>
        public void HandleTouchMove(int touchId, WinPoint position)
        {
            if (!_touchPoints.ContainsKey(touchId)) return;

            // 更新触摸点位置
            _touchPoints[touchId] = position;

            // 如果正在绘制形状，更新预览
            if (_isDrawingShape && _touchPoints.Count == 1)
            {
                UpdateShapePreview(position);
            }

            // 多点触控处理
            if (_touchPoints.Count >= 2)
            {
                HandleMultiTouchMove();
            }
        }

        /// <summary>
        /// 处理触摸抬起事件
        /// </summary>
        public void HandleTouchUp(int touchId)
        {
            if (!_touchDeviceIds.Contains(touchId)) return;

            _touchDeviceIds.Remove(touchId);
            _touchPoints.Remove(touchId);

            // 如果正在绘制形状，提交形状
            if (_isDrawingShape && _touchDeviceIds.Count == 0)
            {
                CommitShape();
            }

            // 检查是否退出多点触控模式
            if (_touchDeviceIds.Count <= 1)
            {
                _isMultiTouchMode = false;
                Console.WriteLine("退出多点触控模式");
            }
        }

        /// <summary>
        /// 处理多点触控移动
        /// </summary>
        private void HandleMultiTouchMove()
        {
            if (_touchPoints.Count < 2) return;

            var points = _touchPoints.Values.ToList();
            var p1 = points[0];
            var p2 = points[1];

            // 计算触摸中心
            var center = new WinPoint((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2);

            // 计算触摸距离
            var distance = Math.Sqrt(Math.Pow(p2.X - p1.X, 2) + Math.Pow(p2.Y - p1.Y, 2));

            // 如果是第一次多点触控，记录初始状态
            if (_lastTouchDistance < 0)
            {
                _lastTouchDistance = distance;
                _lastTouchCenter = center;
                return;
            }

            // 计算距离变化（缩放）
            var scale = distance / _lastTouchDistance;

            // 计算中心变化（平移）
            var deltaX = center.X - _lastTouchCenter.X;
            var deltaY = center.Y - _lastTouchCenter.Y;

            // 更新状态
            _lastTouchDistance = distance;
            _lastTouchCenter = center;

            // 触发缩放和平移事件（需要在 MainWindow 中处理）
            // 这里可以添加事件通知机制
            Console.WriteLine($"多点触控: 缩放={scale:F2}, 平移=({deltaX:F2}, {deltaY:F2})");
        }

        /// <summary>
        /// 重置多点触控状态
        /// </summary>
        public void ResetMultiTouchState()
        {
            _isMultiTouchMode = false;
            _isSingleFingerDragMode = false;
            _touchDeviceIds.Clear();
            _touchPoints.Clear();
            _lastTouchDistance = -1;
            _lastTouchCenter = new WinPoint(0, 0);
        }

        // =========================
        // TouchSDK 相关字段
        // =========================
        private bool _touchSDKInitialized = false;
        private double _sdkTouchArea = 0;

        // =========================
        // 手掌擦手势功能 (从 MW_TouchEvents.cs 移植)
        // =========================
        private bool _isPalmEraserActive = false;
        private ToolMode _lastModeBeforePalmEraser = ToolMode.Pen;
        private double _palmEraserThreshold = 5000.0; // 大幅提高默认阈值，从1000提高到5000，显著降低灵敏度
        private double _currentTouchArea = 0.0;
        private bool _enablePalmEraser = true;

        // 递归保护
        private bool _inSetMode = false;

        // 添加：管理器启用状态
        private bool _isEnabled = true;

        // 手掌擦配置属性
        public double PalmEraserThreshold
        {
            get => _palmEraserThreshold;
            set
            {
                _palmEraserThreshold = Math.Max(1000, value); // 提高最小阈值从1到1000，避免设置过小的阈值
                Console.WriteLine($"设置手掌擦阈值: {_palmEraserThreshold}");
            }
        }

        public bool EnablePalmEraser
        {
            get => _enablePalmEraser;
            set => _enablePalmEraser = value;
        }

        public bool IsPalmEraserActive => _isPalmEraserActive;

        // TouchSDK 回调委托
        private delegate void FuncTouchPointData(IntPtr pDevInfo, IntPtr pdata, int maxpointnum, int nValidPointNum, IntPtr pObj);
        private delegate void FuncHotplugDevInfo(IntPtr devInfo, byte attached, IntPtr callbackobject);

        // 设备信息结构体
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DeviceInfo
        {
            public int deviceID;
            public int vendorID;
            public int productID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string deviceName;
            public int maxTouchPoints;
            public int resolutionX;
            public int resolutionY;
        }

        // 触摸点数据结构体
        [StructLayout(LayoutKind.Sequential)]
        public struct TouchPointData
        {
            public int x;           // X 坐标
            public int y;           // Y 坐标
            public int width;       // 触摸宽度
            public int height;      // 触摸高度
            public int pressure;    // 压力值
            public byte touchState; // 触摸状态
            public byte touchID;    // 触摸点ID
            public byte area;       // 触摸面积
            public byte reserved;   // 保留字段
        }

        // TouchSDK DLL 导入
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        [DllImport("TouchSDKDll.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int InitTouch(
            [In, Out] DeviceInfo[] pDevInfos,
            int nMaxDevInfoNum,
            FuncTouchPointData funcTouchPointData,
            FuncHotplugDevInfo funcHotplugDevInfo,
            IntPtr pObj);

        [DllImport("TouchSDKDll.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool EnableTouch(DeviceInfo DevInfo, int nTimeout = 20);

        [DllImport("TouchSDKDll.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool EnableRawData(DeviceInfo DevInfo, int nTimeout = 20);

        [DllImport("TouchSDKDll.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool EnableTouchWidthData(DeviceInfo DevInfo, int nTimeout = 20);

        [DllImport("TouchSDKDll.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int GetTouchDeviceCount();

        [DllImport("TouchSDKDll.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void ExitTouch();

        // SDK 面积属性
        public double SDKTouchArea
        {
            get => _sdkTouchArea;
            private set
            {
                if (_sdkTouchArea != value)
                {
                    _sdkTouchArea = value;
                    OnSDKTouchAreaChanged?.Invoke(value);
                }
            }
        }

        // SDK 面积变化事件
        public event Action<double> OnSDKTouchAreaChanged;

        // 手掌擦状态变化事件
        public event Action<bool> OnPalmEraserStateChanged;

        public DrawingManager(InkCanvas inkCanvas, FrameworkElement videoArea, Window mainWindow)
        {
            _inkCanvas = inkCanvas;
            _videoArea = videoArea;
            _mainWindow = mainWindow;

            InitializeEventHandlers();

            CurrentMode = ToolMode.Move;
            _inkCanvas.EditingMode = InkCanvasEditingMode.None;
            _mainWindow.Cursor = Cursors.Hand;

            InitializeTouchSDK();
        }

        public void SetOverlayInkCanvas(InkCanvas overlayInkCanvas, ScaleTransform zoomTransform, TranslateTransform panTransform)
        {
            _overlayInkCanvas = overlayInkCanvas;
            _zoomTransform = zoomTransform;
            _panTransform = panTransform;

            if (_overlayInkCanvas != null)
            {
                _overlayInkCanvas.StrokeCollected += OverlayInk_StrokeCollected;
                _overlayInkCanvas.DefaultDrawingAttributes = _inkCanvas.DefaultDrawingAttributes.Clone();
                Console.WriteLine("OverlayInkCanvas 已初始化");
            }
        }

        public void SetUseOverlayInkCanvas(bool use)
        {
            _useOverlayInkCanvas = use;
            if (_overlayInkCanvas != null)
            {
                _overlayInkCanvas.Visibility = use && CurrentMode == ToolMode.Pen ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public bool IsOverlayInkCanvasActive => _useOverlayInkCanvas && _overlayInkCanvas != null && CurrentMode == ToolMode.Pen;

        /// <summary>
        /// 设置管理器是否启用
        /// </summary>
        public void SetEnabled(bool enabled)
        {
            _isEnabled = enabled;
        }

        /// <summary>
        /// 检查管理器是否启用
        /// </summary>
        private bool CheckEnabled()
        {
            return _isEnabled;
        }

        private void InitializeEventHandlers()
        {
            // 捕捉画笔/橡皮事件
            _inkCanvas.StrokeCollected += Ink_StrokeCollected;
            _inkCanvas.PreviewMouseLeftButtonDown += Ink_PreviewMouseDown;
            _inkCanvas.PreviewMouseLeftButtonUp += Ink_PreviewMouseUp;
            _inkCanvas.PreviewStylusDown += Ink_PreviewStylusDown;
            _inkCanvas.PreviewStylusUp += Ink_PreviewStylusUp;
            _inkCanvas.Strokes.StrokesChanged += Ink_StrokesChanged;
        }

        // =========================
        // TouchSDK 初始化和管理
        // =========================

        /// <summary>
        /// 初始化 TouchSDK
        /// </summary>
        public bool InitializeTouchSDK()
        {
            try
            {
                Console.WriteLine("开始初始化 TouchSDK...");

                // 直接根据架构设置 DLL 路径
                string arch = IntPtr.Size == 8 ? "x64" : "x86";
                string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, arch, "TouchSDKDll.dll");

                Console.WriteLine($"直接查找 DLL 路径: {dllPath}");

                // 检查 DLL 文件是否存在
                if (!File.Exists(dllPath))
                {
                    Console.WriteLine($"错误: 未找到 TouchSDKDll.dll 在路径: {dllPath}");
                    Console.WriteLine("请确保以下文件存在:");
                    Console.WriteLine($"- {Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "x64", "TouchSDKDll.dll")}");
                    Console.WriteLine($"- {Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "x86", "TouchSDKDll.dll")}");
                    return false;
                }

                Console.WriteLine($"找到 TouchSDK DLL 文件: {dllPath}");

                // 设置 DLL 目录到架构特定的文件夹
                string archDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, arch);
                if (!SetDllDirectory(archDirectory))
                {
                    int errorCode = Marshal.GetLastWin32Error();
                    Console.WriteLine($"设置 DLL 目录失败: {archDirectory}, 错误代码: {errorCode}");
                    return false;
                }
                Console.WriteLine($"已设置 DLL 目录: {archDirectory}");

                // 检查设备数量
                Console.WriteLine("正在获取 TouchSDK 设备数量...");
                int deviceCount = GetTouchDeviceCount();
                Console.WriteLine($"检测到 {deviceCount} 个 TouchSDK 设备");

                if (deviceCount <= 0)
                {
                    Console.WriteLine("未检测到 TouchSDK 兼容设备，TouchSDK 将不可用");
                    Console.WriteLine("可能的原因:");
                    Console.WriteLine("1. 没有连接 TouchSDK 兼容设备");
                    Console.WriteLine("2. 设备驱动未正确安装");
                    Console.WriteLine("3. 设备权限不足");
                    return false;
                }

                // 初始化设备信息数组
                var deviceInfos = new DeviceInfo[10];

                Console.WriteLine("正在初始化 TouchSDK...");
                int initResult = InitTouch(
                    deviceInfos,
                    deviceInfos.Length,
                    OnTouchPointData,
                    OnHotplugEvent,
                    IntPtr.Zero);

                Console.WriteLine($"InitTouch 返回代码: {initResult}");

                if (initResult != 0)
                {
                    Console.WriteLine($"TouchSDK 初始化失败，错误码: {initResult}");
                    return false;
                }

                // 启用第一个设备的功能
                if (deviceCount > 0)
                {
                    var firstDevice = deviceInfos[0];
                    Console.WriteLine($"正在启用设备: {firstDevice.deviceName} (ID: {firstDevice.deviceID})");

                    if (!EnableTouch(firstDevice))
                    {
                        Console.WriteLine("启用触摸功能失败");
                        return false;
                    }
                    Console.WriteLine("触摸功能已启用");

                    if (!EnableRawData(firstDevice))
                    {
                        Console.WriteLine("启用原始数据失败");
                        return false;
                    }
                    Console.WriteLine("原始数据功能已启用");

                    if (!EnableTouchWidthData(firstDevice))
                    {
                        Console.WriteLine("启用触摸面积数据失败");
                        return false;
                    }
                    Console.WriteLine("触摸面积数据功能已启用");
                }

                _touchSDKInitialized = true;
                Console.WriteLine("TouchSDK 初始化成功 ✓");
                return true;
            }
            catch (DllNotFoundException dllEx)
            {
                Console.WriteLine($"DLL 未找到异常: {dllEx.Message}");
                Console.WriteLine($"这通常意味着:");
                Console.WriteLine($"1. TouchSDKDll.dll 文件不存在");
                Console.WriteLine($"2. DLL 文件损坏");
                Console.WriteLine($"3. 架构不匹配 (32位/64位)");
                return false;
            }
            catch (BadImageFormatException badImageEx)
            {
                Console.WriteLine($"DLL 格式异常: {badImageEx.Message}");
                Console.WriteLine($"架构不匹配: 当前应用程序是 {(IntPtr.Size == 8 ? "64位" : "32位")}");
                Console.WriteLine($"请确保使用正确的架构版本:");
                Console.WriteLine($"- 64位应用程序使用 x64/TouchSDKDll.dll");
                Console.WriteLine($"- 32位应用程序使用 x86/TouchSDKDll.dll");
                return false;
            }
            catch (EntryPointNotFoundException entryEx)
            {
                Console.WriteLine($"入口点未找到异常: {entryEx.Message}");
                Console.WriteLine($"DLL 中的函数签名可能已更改或不兼容");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"TouchSDK 初始化异常: {ex.Message}");
                Console.WriteLine($"异常类型: {ex.GetType()}");
                Console.WriteLine($"堆栈跟踪: {ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// 触摸点数据回调
        /// </summary>
        private void OnTouchPointData(IntPtr pDevInfo, IntPtr pdata, int maxpointnum, int nValidPointNum, IntPtr pObj)
        {
            try
            {
                if (nValidPointNum <= 0)
                {
                    SDKTouchArea = 0;
                    return;
                }

                double totalArea = 0;

                for (int i = 0; i < nValidPointNum; i++)
                {
                    int offset = i * Marshal.SizeOf<TouchPointData>();
                    IntPtr pointPtr = IntPtr.Add(pdata, offset);

                    var point = Marshal.PtrToStructure<TouchPointData>(pointPtr);

                    // 计算单个触摸点的面积（使用 width * height）
                    double pointArea = point.width * point.height;
                    totalArea += pointArea;

                    // 调试输出（可选）
                    // Console.WriteLine($"TouchSDK 点 {i}: X={point.x}, Y={point.y}, Width={point.width}, Height={point.height}, Area={pointArea}");
                }

                // 更新 SDK 面积（通过属性触发事件）
                SDKTouchArea = totalArea;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"处理 TouchSDK 数据异常: {ex.Message}");
                SDKTouchArea = 0;
            }
        }

        /// <summary>
        /// 热插拔事件回调
        /// </summary>
        private void OnHotplugEvent(IntPtr devInfo, byte attached, IntPtr callbackobject)
        {
            string status = attached != 0 ? "连接" : "断开";
            Console.WriteLine($"TouchSDK 设备{status}");
        }

        /// <summary>
        /// 清理 TouchSDK 资源
        /// </summary>
        private void CleanupTouchSDK()
        {
            if (_touchSDKInitialized)
            {
                try
                {
                    ExitTouch();
                    _touchSDKInitialized = false;
                    Console.WriteLine("TouchSDK 已清理");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"清理 TouchSDK 异常: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 获取 TouchSDK 初始化状态
        /// </summary>
        public bool IsTouchSDKInitialized => _touchSDKInitialized;

        // =========================
        // 配置和应用设置
        // =========================
        public void ApplyConfig(AppConfig config)
        {
            var penColor = (Color)System.Windows.Media.ColorConverter.ConvertFromString(config.DefaultPenColor ?? "#FF000000");
            _inkCanvas.DefaultDrawingAttributes.Color = penColor;
            UserPenWidth = config.DefaultPenWidth;

            // 设置初始橡皮擦形状
            _inkCanvas.EraserShape = new RectangleStylusShape(_manualEraserSize, _manualEraserSize);

            // 应用手掌擦配置
            if (config.EnablePalmEraser)
            {
                EnablePalmEraser = true;
                PalmEraserThreshold = config.PalmEraserThreshold;
            }
            else
            {
                EnablePalmEraser = false;
            }

            // 应用画板模式配置
            EnableCanvasMode = config.EnableCanvasMode;

            UpdatePenAttributes();
        }

        public void SetPenColor(Color color)
        {
            _inkCanvas.DefaultDrawingAttributes.Color = color;
            if (_overlayInkCanvas != null)
            {
                _overlayInkCanvas.DefaultDrawingAttributes.Color = color;
            }
        }

        // =========================
        // 编辑操作管理
        // =========================
        private void StartEdit()
        {
            if (_isEditing) return;
            _currentEdit = new EditAction();
            _isEditing = true;
        }

        private void EndEdit()
        {
            if (!_isEditing || _currentEdit == null) return;
            if (_currentEdit.AddedStrokes.Count > 0 || _currentEdit.RemovedStrokes.Count > 0)
            {
                _editHistory.Push(_currentEdit);
                // 新操作后清空重做历史
                _redoHistory.Clear();
            }
            _currentEdit = null;
            _isEditing = false;
        }

        private void Ink_PreviewMouseDown(object sender, MouseButtonEventArgs e) => StartEdit();
        private void Ink_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (CurrentMode != ToolMode.Pen)
                EndEdit();
        }

        private void Ink_PreviewStylusDown(object sender, StylusDownEventArgs e) => StartEdit();
        private void Ink_PreviewStylusUp(object sender, StylusEventArgs e)
        {
            if (CurrentMode != ToolMode.Pen)
                EndEdit();
        }

        private void Ink_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
        {
            if (CurrentMode != ToolMode.Pen) return;
            
            e.Stroke.DrawingAttributes.Width = UserPenWidth;
            e.Stroke.DrawingAttributes.Height = UserPenWidth;
            
            if (!_isEditing || _currentEdit == null) StartEdit();
            _currentEdit!.AddedStrokes.Add(e.Stroke);
            EndEdit();
        }

        private void OverlayInk_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
        {
            if (CurrentMode != ToolMode.Pen || _overlayInkCanvas == null) return;

            try
            {
                var overlayStroke = e.Stroke;
                var transformedStroke = TransformStrokeFromOverlayToMain(overlayStroke);
                
                if (transformedStroke != null)
                {
                    _inkCanvas.Strokes.Add(transformedStroke);
                    
                    if (!_isEditing || _currentEdit == null) StartEdit();
                    _currentEdit?.AddedStrokes.Add(transformedStroke);
                    EndEdit();
                }

                _overlayInkCanvas.Strokes.Remove(overlayStroke);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"OverlayInk_StrokeCollected 错误: {ex.Message}");
            }
        }

        private Stroke TransformStrokeFromOverlayToMain(Stroke overlayStroke)
        {
            if (_zoomTransform == null || _panTransform == null) return null;

            try
            {
                var stylusPoints = overlayStroke.StylusPoints;
                var transformedPoints = new StylusPointCollection();

                double zoom = _zoomTransform.ScaleX;
                double panX = _panTransform.X;
                double panY = _panTransform.Y;

                foreach (var point in stylusPoints)
                {
                    double screenX = point.X;
                    double screenY = point.Y;

                    double canvasX = (screenX - panX) / zoom;
                    double canvasY = (screenY - panY) / zoom;

                    transformedPoints.Add(new StylusPoint(canvasX, canvasY, point.PressureFactor));
                }

                var newStroke = new Stroke(transformedPoints);
                double adjustedWidth = UserPenWidth / zoom;
                adjustedWidth = Math.Max(0.5, adjustedWidth);
                
                newStroke.DrawingAttributes = new DrawingAttributes
                {
                    Color = overlayStroke.DrawingAttributes.Color,
                    Width = adjustedWidth,
                    Height = adjustedWidth,
                    StylusTip = overlayStroke.DrawingAttributes.StylusTip,
                    IsHighlighter = overlayStroke.DrawingAttributes.IsHighlighter
                };
                
                Console.WriteLine($"TransformStroke: UserPenWidth={UserPenWidth}, zoom={zoom}, adjustedWidth={adjustedWidth}");
                
                return newStroke;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"TransformStrokeFromOverlayToMain 错误: {ex.Message}");
                return null;
            }
        }

        public WinPoint ScreenToCanvasPoint(WinPoint screenPoint)
        {
            if (_zoomTransform == null || _panTransform == null)
                return screenPoint;

            double zoom = _zoomTransform.ScaleX;
            double panX = _panTransform.X;
            double panY = _panTransform.Y;

            double canvasX = (screenPoint.X - panX) / zoom;
            double canvasY = (screenPoint.Y - panY) / zoom;

            return new WinPoint(canvasX, canvasY);
        }

        public WinPoint CanvasToScreenPoint(WinPoint canvasPoint)
        {
            if (_zoomTransform == null || _panTransform == null)
                return canvasPoint;

            double zoom = _zoomTransform.ScaleX;
            double panX = _panTransform.X;
            double panY = _panTransform.Y;

            double screenX = canvasPoint.X * zoom + panX;
            double screenY = canvasPoint.Y * zoom + panY;

            return new WinPoint(screenX, screenY);
        }

        // 橡皮：StrokesChanged 会持续触发，等 MouseUp 再 EndEdit()
        private void Ink_StrokesChanged(object? sender, StrokeCollectionChangedEventArgs e)
        {
            if (CurrentMode == ToolMode.Pen) return;
            if (!_isEditing || _currentEdit == null) return;
            foreach (var s in e.Added) _currentEdit.AddedStrokes.Add(s);
            foreach (var s in e.Removed) _currentEdit.RemovedStrokes.Add(s);
        }

        public void SetMode(ToolMode mode, bool initial = false)
        {
            if (_inSetMode) return;
            _inSetMode = true;

            try
            {
                if (CurrentMode == mode && !initial)
                    return;

                if (_isPalmEraserActive && mode != ToolMode.Eraser)
                {
                    ForceDeactivatePalmEraser();
                }

                CurrentMode = mode;

                if (_overlayInkCanvas != null)
                {
                    _overlayInkCanvas.Visibility = (_useOverlayInkCanvas && mode == ToolMode.Pen) 
                        ? Visibility.Visible 
                        : Visibility.Collapsed;
                    
                    if (mode == ToolMode.Pen && _useOverlayInkCanvas)
                    {
                        _overlayInkCanvas.DefaultDrawingAttributes = new DrawingAttributes
                        {
                            Color = _inkCanvas.DefaultDrawingAttributes.Color,
                            Width = UserPenWidth,
                            Height = UserPenWidth,
                            StylusTip = _inkCanvas.DefaultDrawingAttributes.StylusTip,
                            IsHighlighter = _inkCanvas.DefaultDrawingAttributes.IsHighlighter
                        };
                        _overlayInkCanvas.EditingMode = InkCanvasEditingMode.Ink;
                        Console.WriteLine($"SetMode Pen: UserPenWidth={UserPenWidth}, Overlay Width={_overlayInkCanvas.DefaultDrawingAttributes.Width}");
                    }
                }

                switch (mode)
                {
                    case ToolMode.Move:
                        _inkCanvas.EditingMode = InkCanvasEditingMode.None;
                        _mainWindow.Cursor = Cursors.Hand;
                        _drawingShapeMode = 0;
                        break;
                    case ToolMode.Pen:
                        if (_useOverlayInkCanvas && _overlayInkCanvas != null)
                        {
                            _inkCanvas.EditingMode = InkCanvasEditingMode.None;
                        }
                        else
                        {
                            _inkCanvas.EditingMode = InkCanvasEditingMode.Ink;
                        }
                        _mainWindow.Cursor = Cursors.Arrow;
                        _drawingShapeMode = 0;
                        break;
                    case ToolMode.Eraser:
                        _inkCanvas.EditingMode = InkCanvasEditingMode.EraseByPoint;
                        _mainWindow.Cursor = Cursors.Arrow;
                        _drawingShapeMode = 0;
                        if (!_isPalmEraserActive)
                        {
                            _inkCanvas.EraserShape = new RectangleStylusShape(_manualEraserSize, _manualEraserSize);
                        }
                        break;
                    case ToolMode.Line:
                        _inkCanvas.EditingMode = InkCanvasEditingMode.None;
                        _mainWindow.Cursor = Cursors.Cross;
                        _drawingShapeMode = 1;
                        break;
                    case ToolMode.Arrow:
                        _inkCanvas.EditingMode = InkCanvasEditingMode.None;
                        _mainWindow.Cursor = Cursors.Cross;
                        _drawingShapeMode = 2;
                        break;
                    case ToolMode.Rectangle:
                        _inkCanvas.EditingMode = InkCanvasEditingMode.None;
                        _mainWindow.Cursor = Cursors.Cross;
                        _drawingShapeMode = 3;
                        break;
                    case ToolMode.Ellipse:
                        _inkCanvas.EditingMode = InkCanvasEditingMode.None;
                        _mainWindow.Cursor = Cursors.Cross;
                        _drawingShapeMode = 4;
                        break;
                    case ToolMode.Circle:
                        _inkCanvas.EditingMode = InkCanvasEditingMode.None;
                        _mainWindow.Cursor = Cursors.Cross;
                        _drawingShapeMode = 5;
                        break;
                    case ToolMode.DashedLine:
                        _inkCanvas.EditingMode = InkCanvasEditingMode.None;
                        _mainWindow.Cursor = Cursors.Cross;
                        _drawingShapeMode = 8;
                        break;
                    case ToolMode.DotLine:
                        _inkCanvas.EditingMode = InkCanvasEditingMode.None;
                        _mainWindow.Cursor = Cursors.Cross;
                        _drawingShapeMode = 18;
                        break;
                    case ToolMode.PaintBucket:
                        _inkCanvas.EditingMode = InkCanvasEditingMode.None;
                        _mainWindow.Cursor = Cursors.Cross;
                        _drawingShapeMode = 0;
                        break;
                }
            }
            finally
            {
                _inSetMode = false;
            }
        }

        public void OpenPenSettings()
        {
            double currentEraserWidth = _manualEraserSize;
            if (_inkCanvas.EraserShape is RectangleStylusShape rectShape)
            {
                currentEraserWidth = rectShape.Width;
            }

            var dlg = new PenSettingsWindow(_inkCanvas.DefaultDrawingAttributes.Color, UserPenWidth, currentEraserWidth);
            if (dlg.ShowDialog() == true)
            {
                _inkCanvas.DefaultDrawingAttributes.Color = dlg.SelectedColor;
                UserPenWidth = dlg.SelectedPenWidth;
                _manualEraserSize = dlg.SelectedEraserWidth;
                _inkCanvas.EraserShape = new RectangleStylusShape(_manualEraserSize, _manualEraserSize);
                UpdatePenAttributes();
            }
        }

        public void UpdatePenAttributes()
        {
            double compensatedWidth = UserPenWidth / Math.Max(CurrentZoom, 0.01);
            compensatedWidth = Math.Max(1.0, Math.Min(50.0, compensatedWidth));
            
            _inkCanvas.DefaultDrawingAttributes.Width = compensatedWidth;
            _inkCanvas.DefaultDrawingAttributes.Height = compensatedWidth;

            if (_overlayInkCanvas != null)
            {
                _overlayInkCanvas.DefaultDrawingAttributes.Width = UserPenWidth;
                _overlayInkCanvas.DefaultDrawingAttributes.Height = UserPenWidth;
                
                if (CurrentMode == ToolMode.Pen && _useOverlayInkCanvas)
                {
                    _overlayInkCanvas.DefaultDrawingAttributes.Color = _inkCanvas.DefaultDrawingAttributes.Color;
                }
                
                Console.WriteLine($"UpdatePenAttributes: UserPenWidth={UserPenWidth}, Overlay Width={_overlayInkCanvas.DefaultDrawingAttributes.Width}");
            }
        }

        // =========================
        // 绘制操作
        // =========================
        public void ClearStrokes()
        {
            _inkCanvas.Strokes.Clear();
            ClearFillImages();
            _editHistory.Clear();
            _redoHistory.Clear();
        }

        /// <summary>
        /// 清除所有油漆桶填充图片
        /// </summary>
        public void ClearFillImages()
        {
            try
            {
                var toRemove = new List<UIElement>();
                foreach (var child in _inkCanvas.Children)
                {
                    if (child is WinImage img && img.Tag is string tag && tag == _fillImageTag)
                    {
                        toRemove.Add(img);
                    }
                }
                foreach (var img in toRemove)
                {
                    _inkCanvas.Children.Remove(img);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"清除填充图片失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取当前画布上所有油漆桶填充图片（用于导出）
        /// </summary>
        public List<WinImage> GetFillImages()
        {
            var result = new List<WinImage>();
            try
            {
                foreach (var child in _inkCanvas.Children)
                {
                    if (child is WinImage img && img.Tag is string tag && tag == _fillImageTag)
                    {
                        result.Add(img);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"获取填充图片失败: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// 在橡皮擦经过的位置擦除填充图片（按橡皮擦形状将对应区域设为透明）
        /// </summary>
        /// <param name="canvasPt">画布坐标的橡皮擦中心点</param>
        /// <param name="canvasEraserWidth">画布坐标下橡皮擦宽度</param>
        /// <param name="canvasEraserHeight">画布坐标下橡皮擦高度（圆形模式下等于宽度）</param>
        /// <param name="isCircle">是否为圆形橡皮擦</param>
        private void EraseFillImagesAt(WinPoint canvasPt, double canvasEraserWidth, double canvasEraserHeight, bool isCircle)
        {
            try
            {
                double radiusX = canvasEraserWidth / 2.0;
                double radiusY = canvasEraserHeight / 2.0;

                foreach (var child in _inkCanvas.Children.OfType<WinImage>().ToArray())
                {
                    if (!(child.Tag is string tag) || tag != _fillImageTag) continue;
                    if (!(child.Source is WriteableBitmap wb)) continue;

                    double left = InkCanvas.GetLeft(child);
                    double top = InkCanvas.GetTop(child);
                    int imgW = wb.PixelWidth;
                    int imgH = wb.PixelHeight;

                    // 橡皮擦在图片像素坐标系下的范围
                    int x0 = (int)Math.Floor(canvasPt.X - radiusX - left);
                    int y0 = (int)Math.Floor(canvasPt.Y - radiusY - top);
                    int x1 = (int)Math.Ceiling(canvasPt.X + radiusX - left);
                    int y1 = (int)Math.Ceiling(canvasPt.Y + radiusY - top);

                    // 裁剪到图片范围
                    if (x0 < 0) x0 = 0;
                    if (y0 < 0) y0 = 0;
                    if (x1 > imgW) x1 = imgW;
                    if (y1 > imgH) y1 = imgH;
                    if (x0 >= x1 || y0 >= y1) continue;

                    // 快速判断是否完全在图片范围外（即橡皮擦区域与图片不相交）
                    // 上面裁剪后若 x0>=x1 或 y0>=y1 已 continue

                    int stride = imgW * 4;
                    byte[] pixels = new byte[stride * imgH];
                    wb.CopyPixels(pixels, stride, 0);

                    double cx = canvasPt.X - left;
                    double cy = canvasPt.Y - top;
                    bool changed = false;

                    if (isCircle)
                    {
                        // 圆形橡皮擦：(x-cx)^2/radiusX^2 + (y-cy)^2/radiusY^2 <= 1 时擦除
                        for (int y = y0; y < y1; y++)
                        {
                            double dy = y - cy;
                            for (int x = x0; x < x1; x++)
                            {
                                double dx = x - cx;
                                if ((dx * dx) / (radiusX * radiusX) + (dy * dy) / (radiusY * radiusY) <= 1.0)
                                {
                                    int idx = (y * imgW + x) * 4;
                                    if (pixels[idx + 3] != 0)
                                    {
                                        pixels[idx] = 0;
                                        pixels[idx + 1] = 0;
                                        pixels[idx + 2] = 0;
                                        pixels[idx + 3] = 0;
                                        changed = true;
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        // 矩形橡皮擦：直接擦除矩形区域
                        for (int y = y0; y < y1; y++)
                        {
                            for (int x = x0; x < x1; x++)
                            {
                                int idx = (y * imgW + x) * 4;
                                if (pixels[idx + 3] != 0)
                                {
                                    pixels[idx] = 0;
                                    pixels[idx + 1] = 0;
                                    pixels[idx + 2] = 0;
                                    pixels[idx + 3] = 0;
                                    changed = true;
                                }
                            }
                        }
                    }

                    if (changed)
                    {
                        wb.WritePixels(new Int32Rect(0, 0, imgW, imgH), pixels, stride, 0);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"擦除填充图片失败: {ex.Message}");
            }
        }

        public void Undo()
        {
            if (_editHistory.Count == 0) return;
            var lastAction = _editHistory.Pop();

            // 将撤销的操作加入重做历史
            var redoAction = new EditAction();
            foreach (var stroke in lastAction.AddedStrokes)
            {
                if (_inkCanvas.Strokes.Contains(stroke))
                {
                    _inkCanvas.Strokes.Remove(stroke);
                    redoAction.RemovedStrokes.Add(stroke);
                }
            }
            foreach (var stroke in lastAction.RemovedStrokes)
            {
                if (!_inkCanvas.Strokes.Contains(stroke))
                {
                    _inkCanvas.Strokes.Add(stroke);
                    redoAction.AddedStrokes.Add(stroke);
                }
            }

            _redoHistory.Push(redoAction);
        }

        public void Redo()
        {
            if (_redoHistory.Count == 0) return;
            var redoAction = _redoHistory.Pop();

            // 执行重做操作（与撤销相反）
            foreach (var stroke in redoAction.RemovedStrokes)
            {
                if (!_inkCanvas.Strokes.Contains(stroke))
                    _inkCanvas.Strokes.Add(stroke);
            }
            foreach (var stroke in redoAction.AddedStrokes)
            {
                if (_inkCanvas.Strokes.Contains(stroke))
                    _inkCanvas.Strokes.Remove(stroke);
            }

            // 将重做的操作重新加入撤销历史
            _editHistory.Push(redoAction);
        }

        public bool CanRedo => _redoHistory.Count > 0;

        public void SwitchToPhotoStrokes(StrokeCollection strokes)
        {
            _inkCanvas.Strokes.StrokesChanged -= Ink_StrokesChanged;
            _inkCanvas.Strokes = strokes;
            _inkCanvas.Strokes.StrokesChanged += Ink_StrokesChanged;
            // 切换图像/直播模式时清除所有填充图片
            ClearFillImages();
            _editHistory.Clear();
            _redoHistory.Clear();
        }

        // =========================
        // 缩放/平移功能 - 修复空引用异常
        // =========================
        public void HandleMouseWheel(MouseWheelEventArgs e)
        {
            if (!CheckEnabled()) return;

            // 现在缩放功能由 MainWindow 统一处理
            // 这里只处理非移动模式下的鼠标滚轮事件
            if (CurrentMode != ToolMode.Move)
            {
                // 可以在这里添加其他模式的滚轮处理逻辑
                // 例如：调整笔迹大小等
            }
        }

        public void HandleManipulationDelta(ManipulationDeltaEventArgs e)
        {
            if (!CheckEnabled()) return;

            // 现在手势操作由 MainWindow 统一处理
            // 这里只处理非移动模式下的手势
            if (CurrentMode != ToolMode.Move)
            {
                // 可以在这里添加其他模式的手势处理逻辑
            }
        }

        // =========================
        // 鼠标事件处理
        // =========================
        public void HandleMouseDown(MouseButtonEventArgs e)
        {
            if (!CheckEnabled()) return;

            // 只在移动模式下启用平移
            if (CurrentMode == ToolMode.Move)
            {
                var p = e.GetPosition(_mainWindow);
                _lastMousePos = new WinPoint(p.X, p.Y);
                _isPanning = true;
                _mainWindow.Cursor = Cursors.Hand;
            }
        }

        public void HandleMouseMove(System.Windows.Input.MouseEventArgs e)
        {
            if (!CheckEnabled()) return;

            // 只在移动模式下处理平移
            if (_isPanning && CurrentMode == ToolMode.Move && e.LeftButton == MouseButtonState.Pressed)
            {
                var pos = e.GetPosition(_mainWindow);
                // 平移功能现在由 MainWindow 统一处理
                // 这里只更新鼠标位置
                _lastMousePos = new WinPoint(pos.X, pos.Y);
            }
        }

        public void HandleMouseUp(MouseButtonEventArgs e)
        {
            if (!CheckEnabled()) return;

            _isPanning = false;
            _mainWindow.Cursor = Cursors.Arrow;
        }

        // =========================
        // 触摸事件处理
        // =========================
        public void HandleTouchDown(TouchEventArgs e)
        {
            if (!CheckEnabled()) return;

            // 记录触摸点
            var touchPoint = e.GetTouchPoint(_videoArea);
            _touchPoints[e.TouchDevice.Id] = touchPoint.Position;

            // 更新触摸中心点和距离
            UpdateTouchCenterAndDistance();

            // 处理形状绘制
            if (CurrentMode == ToolMode.Line || CurrentMode == ToolMode.Arrow ||
                CurrentMode == ToolMode.Rectangle || CurrentMode == ToolMode.Ellipse ||
                CurrentMode == ToolMode.Circle || CurrentMode == ToolMode.DashedLine ||
                CurrentMode == ToolMode.DotLine)
            {
                if (_touchPoints.Count == 1)
                {
                    StartShapeDrawing(touchPoint.Position, _drawingShapeMode);
                }
            }

            // 检测手掌擦（只在画笔模式下且启用手掌擦功能）
            if (CurrentMode == ToolMode.Pen && _enablePalmEraser)
            {
                HandleTouchDownForPalmEraser(e);
            }
        }

        public void HandleTouchMove(TouchEventArgs e)
        {
            if (!CheckEnabled()) return;

            // 更新触摸点位置
            if (_touchPoints.ContainsKey(e.TouchDevice.Id))
            {
                var touchPoint = e.GetTouchPoint(_videoArea);
                _touchPoints[e.TouchDevice.Id] = touchPoint.Position;

                // 更新触摸中心点和距离
                UpdateTouchCenterAndDistance();

                // 更新形状预览
                if (_isDrawingShape && _touchPoints.Count == 1)
                {
                    UpdateShapePreview(touchPoint.Position);
                }

                // 更新手掌擦（如果激活）
                if (_isPalmEraserActive)
                {
                    HandleTouchMoveForPalmEraser(e);
                }

                // 只在移动模式下处理手势
                if (CurrentMode == ToolMode.Move && _touchPoints.Count >= 2)
                {
                    HandleMultiTouchGesture();
                }
            }
        }

        public void HandleTouchUp(TouchEventArgs e)
        {
            if (!CheckEnabled()) return;

            // 提交形状绘制
            if (_isDrawingShape && _touchPoints.Count == 0)
            {
                CommitShape();
            }

            // 移除触摸点
            if (_touchPoints.ContainsKey(e.TouchDevice.Id))
            {
                _touchPoints.Remove(e.TouchDevice.Id);
            }

            // 停用手掌擦（如果激活）
            if (_isPalmEraserActive)
            {
                HandleTouchUpForPalmEraser(e);
            }

            // 更新触摸中心点和距离
            UpdateTouchCenterAndDistance();

            // 重置最后触摸距离
            if (_touchPoints.Count < 2)
            {
                _lastTouchDistance = -1;
            }
        }

        // 更新触摸中心点和距离
        private void UpdateTouchCenterAndDistance()
        {
            if (_touchPoints.Count == 0)
            {
                _lastTouchCenter = new WinPoint(0, 0);
                _lastTouchDistance = -1;
                return;
            }

            // 计算中心点
            double centerX = 0, centerY = 0;
            foreach (var point in _touchPoints.Values)
            {
                centerX += point.X;
                centerY += point.Y;
            }
            centerX /= _touchPoints.Count;
            centerY /= _touchPoints.Count;
            _lastTouchCenter = new WinPoint(centerX, centerY);

            // 计算两点之间的距离（如果是双指）
            if (_touchPoints.Count == 2)
            {
                var points = _touchPoints.Values.ToArray();
                double dx = points[1].X - points[0].X;
                double dy = points[1].Y - points[0].Y;
                _lastTouchDistance = Math.Sqrt(dx * dx + dy * dy);
            }
            else
            {
                _lastTouchDistance = -1;
            }
        }

        // 处理多指手势（缩放和平移）
        private void HandleMultiTouchGesture()
        {
            if (_touchPoints.Count < 2 || _lastTouchDistance <= 0)
                return;

            // 计算当前两点之间的距离
            var points = _touchPoints.Values.ToArray();
            double dx = points[1].X - points[0].X;
            double dy = points[1].Y - points[0].Y;
            double currentDistance = Math.Sqrt(dx * dx + dy * dy);

            // 计算缩放比例
            if (_lastTouchDistance > 0)
            {
                double scaleFactor = currentDistance / _lastTouchDistance;
                // 现在缩放功能由 MainWindow 统一处理
                // 这里只更新触摸距离
            }

            // 更新最后触摸距离
            _lastTouchDistance = currentDistance;
        }

        // =========================
        // 手掌擦手势功能实现
        // =========================

        /// <summary>
        /// 处理触摸按下事件，检测手掌擦
        /// </summary>
        private void HandleTouchDownForPalmEraser(TouchEventArgs e)
        {
            try
            {
                // 只在画笔模式下检测手掌擦
                if (CurrentMode != ToolMode.Pen) return;

                var touchPoint = e.GetTouchPoint(_videoArea);
                var bounds = touchPoint.Bounds;

                // 计算触摸面积
                _currentTouchArea = bounds.Width * bounds.Height;

                // 添加更详细的调试信息，帮助调整阈值
                Console.WriteLine($"触摸面积: {_currentTouchArea:F0}, 阈值: {_palmEraserThreshold:F0}, 需要达到: {(_palmEraserThreshold / _currentTouchArea):P0} 才能触发");

                // 如果触摸面积超过阈值，激活手掌擦
                if (_currentTouchArea > _palmEraserThreshold)
                {
                    ActivatePalmEraser(touchPoint);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"手掌擦触摸按下处理错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理触摸移动事件，更新手掌擦
        /// </summary>
        private void HandleTouchMoveForPalmEraser(TouchEventArgs e)
        {
            try
            {
                if (!_isPalmEraserActive) return;

                var touchPoint = e.GetTouchPoint(_videoArea);
                var bounds = touchPoint.Bounds;

                // 更新触摸面积
                _currentTouchArea = bounds.Width * bounds.Height;

                // 动态调整橡皮擦大小基于触摸面积
                if (_currentTouchArea > _palmEraserThreshold)
                {
                    UpdateEraserSizeBasedOnTouchArea(_currentTouchArea);
                }
                else
                {
                    // 如果面积下降到阈值以下，停用手掌擦
                    DeactivatePalmEraser();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"手掌擦触摸移动处理错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理触摸抬起事件，停用手掌擦
        /// </summary>
        private void HandleTouchUpForPalmEraser(TouchEventArgs e)
        {
            try
            {
                if (_isPalmEraserActive)
                {
                    DeactivatePalmEraser();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"手掌擦触摸抬起处理错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 激活手掌擦
        /// </summary>
        private void ActivatePalmEraser(TouchPoint touchPoint)
        {
            if (_isPalmEraserActive) return;

            Console.WriteLine("激活手掌擦");

            // 保存当前模式
            _lastModeBeforePalmEraser = CurrentMode;

            // 切换到橡皮擦模式（使用直接设置避免递归）
            CurrentMode = ToolMode.Eraser;
            _inkCanvas.EditingMode = InkCanvasEditingMode.EraseByPoint;
            _mainWindow.Cursor = Cursors.Arrow;

            // 根据触摸面积调整橡皮擦大小
            UpdateEraserSizeBasedOnTouchArea(_currentTouchArea);

            _isPalmEraserActive = true;

            // 触发手掌擦激活事件
            OnPalmEraserStateChanged?.Invoke(true);
        }

        /// <summary>
        /// 停用手掌擦
        /// </summary>
        private void DeactivatePalmEraser()
        {
            if (!_isPalmEraserActive) return;

            Console.WriteLine("停用手掌擦");

            // 恢复之前的模式（使用直接设置避免递归）
            CurrentMode = _lastModeBeforePalmEraser;

            switch (_lastModeBeforePalmEraser)
            {
                case ToolMode.Move:
                    _inkCanvas.EditingMode = InkCanvasEditingMode.None;
                    _mainWindow.Cursor = Cursors.Hand;
                    break;
                case ToolMode.Pen:
                    _inkCanvas.EditingMode = InkCanvasEditingMode.Ink;
                    _mainWindow.Cursor = Cursors.Arrow;
                    break;
                case ToolMode.Eraser:
                    _inkCanvas.EditingMode = InkCanvasEditingMode.EraseByPoint;
                    _mainWindow.Cursor = Cursors.Arrow;
                    _inkCanvas.EraserShape = new RectangleStylusShape(_manualEraserSize, _manualEraserSize);
                    break;
            }

            _isPalmEraserActive = false;

            // 触发手掌擦状态变化事件
            OnPalmEraserStateChanged?.Invoke(false);
        }

        /// <summary>
        /// 基于触摸面积更新橡皮擦大小（无上限）
        /// </summary>
        private void UpdateEraserSizeBasedOnTouchArea(double touchArea)
        {
            try
            {
                // 根据触摸面积计算橡皮擦大小
                // 使用更保守的缩放因子，从0.1降低到0.05，让橡皮擦大小增长更慢
                double baseSize = Math.Sqrt(touchArea) * 0.05;

                // 只设置最小值10，不设置最大值上限
                double newEraserSize = Math.Max(10, baseSize);

                _inkCanvas.EraserShape = new RectangleStylusShape(newEraserSize, newEraserSize);

                Console.WriteLine($"更新橡皮擦大小: {newEraserSize:F1} (基于触摸面积: {touchArea:F0})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"更新橡皮擦大小错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 设置手掌擦阈值
        /// </summary>
        public void SetPalmEraserThreshold(double threshold)
        {
            PalmEraserThreshold = threshold;
        }

        /// <summary>
        /// 强制停用手掌擦（用于外部调用）
        /// </summary>
        public void ForceDeactivatePalmEraser()
        {
            DeactivatePalmEraser();
        }

        public bool HasStrokes => _inkCanvas.Strokes.Count > 0;

        public StrokeCollection GetStrokes()
        {
            return new StrokeCollection(_inkCanvas.Strokes);
        }

        // =========================
        // 形状绘制辅助方法
        // =========================

        /// <summary>
        /// 生成直线笔迹
        /// </summary>
        private Stroke CreateLineStroke(WinPoint start, WinPoint end)
        {
            var points = new List<WinPoint> { start, end };
            var stylusPoints = new StylusPointCollection(points);
            var attributes = _inkCanvas.DefaultDrawingAttributes.Clone();
            attributes.Width = UserPenWidth;
            attributes.Height = UserPenWidth;
            return new Stroke(stylusPoints)
            {
                DrawingAttributes = attributes
            };
        }

        /// <summary>
        /// 生成箭头笔迹
        /// </summary>
        private Stroke CreateArrowStroke(WinPoint start, WinPoint end)
        {
            double arrowWidth = 15, arrowHeight = 10;
            var theta = Math.Atan2(start.Y - end.Y, start.X - end.X);
            var sin = Math.Sin(theta);
            var cos = Math.Cos(theta);

            var points = new List<WinPoint>
            {
                start,
                end,
                new WinPoint(end.X + (arrowWidth * cos - arrowHeight * sin), end.Y + (arrowWidth * sin + arrowHeight * cos)),
                end,
                new WinPoint(end.X + (arrowWidth * cos + arrowHeight * sin), end.Y - (arrowHeight * cos - arrowWidth * sin))
            };

            var stylusPoints = new StylusPointCollection(points);
            var attributes = _inkCanvas.DefaultDrawingAttributes.Clone();
            attributes.Width = UserPenWidth;
            attributes.Height = UserPenWidth;
            return new Stroke(stylusPoints)
            {
                DrawingAttributes = attributes
            };
        }

        /// <summary>
        /// 生成矩形笔迹
        /// </summary>
        private Stroke CreateRectangleStroke(WinPoint start, WinPoint end)
        {
            var points = new List<WinPoint>
            {
                new WinPoint(start.X, start.Y),
                new WinPoint(start.X, end.Y),
                new WinPoint(end.X, end.Y),
                new WinPoint(end.X, start.Y),
                new WinPoint(start.X, start.Y)
            };

            var stylusPoints = new StylusPointCollection(points);
            var attributes = _inkCanvas.DefaultDrawingAttributes.Clone();
            attributes.Width = UserPenWidth;
            attributes.Height = UserPenWidth;
            return new Stroke(stylusPoints)
            {
                DrawingAttributes = attributes
            };
        }

        /// <summary>
        /// 生成椭圆笔迹
        /// </summary>
        private Stroke CreateEllipseStroke(WinPoint start, WinPoint end)
        {
            var points = GenerateEllipseGeometry(start, end);
            var stylusPoints = new StylusPointCollection(points);
            var attributes = _inkCanvas.DefaultDrawingAttributes.Clone();
            attributes.Width = UserPenWidth;
            attributes.Height = UserPenWidth;
            return new Stroke(stylusPoints)
            {
                DrawingAttributes = attributes
            };
        }

        /// <summary>
        /// 生成圆形笔迹
        /// </summary>
        private Stroke CreateCircleStroke(WinPoint center, WinPoint edge)
        {
            double radius = GetDistance(center, edge);
            var topLeft = new WinPoint(center.X - radius, center.Y - radius);
            var bottomRight = new WinPoint(center.X + radius, center.Y + radius);
            var points = GenerateEllipseGeometry(topLeft, bottomRight);
            var stylusPoints = new StylusPointCollection(points);
            var attributes = _inkCanvas.DefaultDrawingAttributes.Clone();
            attributes.Width = UserPenWidth;
            attributes.Height = UserPenWidth;
            return new Stroke(stylusPoints)
            {
                DrawingAttributes = attributes
            };
        }

        /// <summary>
        /// 生成虚线笔迹集合
        /// </summary>
        private StrokeCollection CreateDashedLineStrokeCollection(WinPoint start, WinPoint end)
        {
            var strokes = new StrokeCollection();
            double dashLength = 10;
            double gapLength = 5;
            double totalLength = GetDistance(start, end);
            double dx = (end.X - start.X) / totalLength;
            double dy = (end.Y - start.Y) / totalLength;

            double currentLength = 0;
            while (currentLength < totalLength)
            {
                double dashEnd = Math.Min(currentLength + dashLength, totalLength);
                var dashStart = new WinPoint(start.X + dx * currentLength, start.Y + dy * currentLength);
                var dashEndPoint = new WinPoint(start.X + dx * dashEnd, start.Y + dy * dashEnd);

                var points = new List<WinPoint> { dashStart, dashEndPoint };
                var stylusPoints = new StylusPointCollection(points);
                var attributes = _inkCanvas.DefaultDrawingAttributes.Clone();
                attributes.Width = UserPenWidth;
                attributes.Height = UserPenWidth;
                var stroke = new Stroke(stylusPoints)
                {
                    DrawingAttributes = attributes
                };
                strokes.Add(stroke);

                currentLength = dashEnd + gapLength;
            }

            return strokes;
        }

        /// <summary>
        /// 生成点线笔迹集合
        /// </summary>
        private StrokeCollection CreateDotLineStrokeCollection(WinPoint start, WinPoint end)
        {
            var strokes = new StrokeCollection();
            double dotSpacing = 15;
            double totalLength = GetDistance(start, end);
            double dx = (end.X - start.X) / totalLength;
            double dy = (end.Y - start.Y) / totalLength;

            double currentLength = 0;
            while (currentLength < totalLength)
            {
                var dotPoint = new WinPoint(start.X + dx * currentLength, start.Y + dy * currentLength);
                var points = new List<WinPoint> { dotPoint, dotPoint };
                var stylusPoints = new StylusPointCollection(points);
                var attributes = _inkCanvas.DefaultDrawingAttributes.Clone();
                attributes.Width = UserPenWidth;
                attributes.Height = UserPenWidth;
                var stroke = new Stroke(stylusPoints)
                {
                    DrawingAttributes = attributes
                };
                strokes.Add(stroke);

                currentLength += dotSpacing;
            }

            return strokes;
        }

        /// <summary>
        /// 生成椭圆几何点集合
        /// </summary>
        private List<WinPoint> GenerateEllipseGeometry(WinPoint topLeft, WinPoint bottomRight)
        {
            var points = new List<WinPoint>();
            double centerX = (topLeft.X + bottomRight.X) / 2;
            double centerY = (topLeft.Y + bottomRight.Y) / 2;
            double radiusX = Math.Abs(bottomRight.X - topLeft.X) / 2;
            double radiusY = Math.Abs(bottomRight.Y - topLeft.Y) / 2;

            int segments = 60;
            for (int i = 0; i <= segments; i++)
            {
                double angle = 2 * Math.PI * i / segments;
                double x = centerX + radiusX * Math.Cos(angle);
                double y = centerY + radiusY * Math.Sin(angle);
                points.Add(new WinPoint(x, y));
            }

            return points;
        }

        /// <summary>
        /// 计算两点之间的距离
        /// </summary>
        private double GetDistance(WinPoint p1, WinPoint p2)
        {
            return Math.Sqrt(Math.Pow(p2.X - p1.X, 2) + Math.Pow(p2.Y - p1.Y, 2));
        }

        // =========================
        // 实时预览功能
        // =========================

        /// <summary>
        /// 开始绘制形状
        /// </summary>
        public void StartShapeDrawing(WinPoint startPoint, int shapeMode)
        {
            _drawingShapeMode = shapeMode;
            _isDrawingShape = true;
            _shapeStartPoint = startPoint;
            _tempStroke = null;
            _tempStrokeCollection = null;
        }

        /// <summary>
        /// 更新形状预览
        /// </summary>
        public void UpdateShapePreview(WinPoint endPoint)
        {
            if (!_isDrawingShape) return;

            try
            {
                // 移除旧的预览
                if (_tempStroke != null)
                {
                    _inkCanvas.Strokes.Remove(_tempStroke);
                    _tempStroke = null;
                }
                if (_tempStrokeCollection != null)
                {
                    _inkCanvas.Strokes.Remove(_tempStrokeCollection);
                    _tempStrokeCollection = null;
                }

                // 根据形状模式创建新的预览
                switch (_drawingShapeMode)
                {
                    case 1: // 直线
                        _tempStroke = CreateLineStroke(_shapeStartPoint, endPoint);
                        _inkCanvas.Strokes.Add(_tempStroke);
                        break;
                    case 2: // 箭头
                        _tempStroke = CreateArrowStroke(_shapeStartPoint, endPoint);
                        _inkCanvas.Strokes.Add(_tempStroke);
                        break;
                    case 3: // 矩形
                        _tempStroke = CreateRectangleStroke(_shapeStartPoint, endPoint);
                        _inkCanvas.Strokes.Add(_tempStroke);
                        break;
                    case 4: // 椭圆
                        _tempStroke = CreateEllipseStroke(_shapeStartPoint, endPoint);
                        _inkCanvas.Strokes.Add(_tempStroke);
                        break;
                    case 5: // 圆
                        _tempStroke = CreateCircleStroke(_shapeStartPoint, endPoint);
                        _inkCanvas.Strokes.Add(_tempStroke);
                        break;
                    case 8: // 虚线
                        _tempStrokeCollection = CreateDashedLineStrokeCollection(_shapeStartPoint, endPoint);
                        _inkCanvas.Strokes.Add(_tempStrokeCollection);
                        break;
                    case 18: // 点线
                        _tempStrokeCollection = CreateDotLineStrokeCollection(_shapeStartPoint, endPoint);
                        _inkCanvas.Strokes.Add(_tempStrokeCollection);
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"更新形状预览错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 提交形状绘制
        /// </summary>
        public void CommitShape()
        {
            if (!_isDrawingShape) return;

            try
            {
                StartEdit();

                // 移除临时预览
                if (_tempStroke != null)
                {
                    _inkCanvas.Strokes.Remove(_tempStroke);
                    var finalStroke = _tempStroke.Clone();
                    // 确保最终笔画使用原始宽度（不受缩放影响）
                    finalStroke.DrawingAttributes.Width = UserPenWidth;
                    finalStroke.DrawingAttributes.Height = UserPenWidth;
                    _inkCanvas.Strokes.Add(finalStroke);
                    _currentEdit?.AddedStrokes.Add(finalStroke);
                    _tempStroke = null;
                }
                if (_tempStrokeCollection != null)
                {
                    _inkCanvas.Strokes.Remove(_tempStrokeCollection);
                    var finalStrokes = new StrokeCollection(_tempStrokeCollection);
                    _inkCanvas.Strokes.Add(finalStrokes);
                    foreach (var stroke in finalStrokes)
                    {
                        // 确保最终笔画使用原始宽度（不受缩放影响）
                        stroke.DrawingAttributes.Width = UserPenWidth;
                        stroke.DrawingAttributes.Height = UserPenWidth;
                        _currentEdit?.AddedStrokes.Add(stroke);
                    }
                    _tempStrokeCollection = null;
                }

                EndEdit();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"提交形状错误: {ex.Message}");
            }
            finally
            {
                _isDrawingShape = false;
                _drawingShapeMode = 0;
            }
        }

        /// <summary>
        /// 取消形状绘制
        /// </summary>
        public void CancelShapeDrawing()
        {
            if (!_isDrawingShape) return;

            try
            {
                // 移除临时预览
                if (_tempStroke != null)
                {
                    _inkCanvas.Strokes.Remove(_tempStroke);
                    _tempStroke = null;
                }
                if (_tempStrokeCollection != null)
                {
                    _inkCanvas.Strokes.Remove(_tempStrokeCollection);
                    _tempStrokeCollection = null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"取消形状绘制错误: {ex.Message}");
            }
            finally
            {
                _isDrawingShape = false;
                _drawingShapeMode = 0;
            }
        }

        /// <summary>
        /// 检查是否正在绘制形状
        /// </summary>
        public bool IsDrawingShape => _isDrawingShape;

        // =========================
        // 油漆桶工具 (填充封闭图形)
        // =========================

        /// <summary>
        /// 在指定点执行油漆桶填充（基于扫描线泛洪填充算法）
        /// </summary>
        /// <param name="canvasPoint">画布坐标的点击点</param>
        /// <returns>是否执行了填充</returns>
        public bool FillClosedShape(WinPoint canvasPoint)
        {
            try
            {
                double canvasWidth = _inkCanvas.ActualWidth;
                double canvasHeight = _inkCanvas.ActualHeight;

                if (canvasWidth <= 0 || canvasHeight <= 0) return false;
                if (_inkCanvas.Strokes.Count == 0) return false;

                int width = (int)canvasWidth;
                int height = (int)canvasHeight;

                int clickX = (int)Math.Round(canvasPoint.X);
                int clickY = (int)Math.Round(canvasPoint.Y);

                if (clickX < 0 || clickX >= width || clickY < 0 || clickY >= height)
                {
                    Console.WriteLine($"填充点超出画布范围: ({clickX}, {clickY}), 画布: {width}x{height}");
                    return false;
                }

                // 1. 将所有笔画渲染到位图
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0, 0, width, height));
                    foreach (var stroke in _inkCanvas.Strokes)
                    {
                        stroke.Draw(dc);
                    }
                }

                var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);

                // 2. 复制像素数据
                int stride = width * 4;
                var pixels = new byte[stride * height];
                rtb.CopyPixels(pixels, stride, 0);

                // 3. 获取点击点的颜色（目标颜色）
                int clickIdx = (clickY * width + clickX) * 4;
                byte targetB = pixels[clickIdx];
                byte targetG = pixels[clickIdx + 1];
                byte targetR = pixels[clickIdx + 2];
                byte targetA = pixels[clickIdx + 3];

                // 如果点击点位于笔画上（不透明），则不填充
                if (targetA > 128)
                {
                    Console.WriteLine("点击点位于笔画上，无法填充");
                    return false;
                }

                // 4. 获取填充颜色（当前画笔颜色）
                Color fillColor = _inkCanvas.DefaultDrawingAttributes.Color;
                byte fillB = fillColor.B;
                byte fillG = fillColor.G;
                byte fillR = fillColor.R;
                byte fillA = 255;

                // 如果目标颜色与填充颜色相同，则不填充
                if (targetB == fillB && targetG == fillG && targetR == fillR && targetA == fillA)
                {
                    Console.WriteLine("目标颜色与填充颜色相同");
                    return false;
                }

                // 5. 执行扫描线泛洪填充
                ScanlineFloodFill(pixels, width, height, clickX, clickY,
                    targetB, targetG, targetR, targetA,
                    fillB, fillG, fillR, fillA);

                // 6. 创建填充后的位图
                var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
                bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);

                // 7. 添加到 InkCanvas 作为子元素（位于笔画下方）
                var image = new WinImage
                {
                    Source = bitmap,
                    Width = width,
                    Height = height,
                    IsHitTestVisible = false,
                    Tag = _fillImageTag // 标识为油漆桶填充图片，便于清除/擦除
                };

                InkCanvas.SetLeft(image, 0);
                InkCanvas.SetTop(image, 0);

                // 插入到子元素列表的最前面（位于笔画下方，但在其他填充图片上方）
                _inkCanvas.Children.Insert(0, image);

                Console.WriteLine($"填充完成: 点击点=({clickX}, {clickY}), 填充颜色=({fillR}, {fillG}, {fillB})");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"填充封闭图形失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 扫描线泛洪填充算法（比 BFS 队列方式更快，避免大量内存分配）
        /// </summary>
        private void ScanlineFloodFill(byte[] pixels, int width, int height,
            int startX, int startY,
            byte targetB, byte targetG, byte targetR, byte targetA,
            byte fillB, byte fillG, byte fillR, byte fillA)
        {
            var stack = new Stack<int>();
            stack.Push(startY * width + startX);

            while (stack.Count > 0)
            {
                int idx1D = stack.Pop();
                int y = idx1D / width;
                int x = idx1D - y * width;

                // 向上找到该列中第一个需要填充的像素
                int idx = (y * width + x) * 4;
                while (y >= 0 &&
                       pixels[idx] == targetB &&
                       pixels[idx + 1] == targetG &&
                       pixels[idx + 2] == targetR &&
                       pixels[idx + 3] == targetA)
                {
                    y--;
                    idx -= width * 4;
                }
                y++;
                idx += width * 4;

                bool spanLeft = false;
                bool spanRight = false;

                // 向下扫描并填充
                while (y < height &&
                       pixels[idx] == targetB &&
                       pixels[idx + 1] == targetG &&
                       pixels[idx + 2] == targetR &&
                       pixels[idx + 3] == targetA)
                {
                    // 填充当前像素
                    pixels[idx] = fillB;
                    pixels[idx + 1] = fillG;
                    pixels[idx + 2] = fillR;
                    pixels[idx + 3] = fillA;

                    // 检查左邻居
                    if (x > 0)
                    {
                        int leftIdx = idx - 4;
                        if (pixels[leftIdx] == targetB &&
                            pixels[leftIdx + 1] == targetG &&
                            pixels[leftIdx + 2] == targetR &&
                            pixels[leftIdx + 3] == targetA)
                        {
                            if (!spanLeft)
                            {
                                stack.Push(y * width + (x - 1));
                                spanLeft = true;
                            }
                        }
                        else
                        {
                            spanLeft = false;
                        }
                    }

                    // 检查右邻居
                    if (x < width - 1)
                    {
                        int rightIdx = idx + 4;
                        if (pixels[rightIdx] == targetB &&
                            pixels[rightIdx + 1] == targetG &&
                            pixels[rightIdx + 2] == targetR &&
                            pixels[rightIdx + 3] == targetA)
                        {
                            if (!spanRight)
                            {
                                stack.Push(y * width + (x + 1));
                                spanRight = true;
                            }
                        }
                        else
                        {
                            spanRight = false;
                        }
                    }

                    y++;
                    idx += width * 4;
                }
            }
        }

        // =========================
        // 手写笔预览功能
        // =========================

        /// <summary>
        /// 获取手写笔预览对象
        /// </summary>
        private StrokeVisual GetStrokeVisual(int id)
        {
            if (_strokeVisualList.TryGetValue(id, out var visual)) return visual;

            var strokeVisual = new StrokeVisual(_inkCanvas.DefaultDrawingAttributes.Clone());
            _strokeVisualList[id] = strokeVisual;

            var visualCanvas = new VisualCanvas();
            strokeVisual.SetVisualCanvas(visualCanvas);
            _visualCanvasList[id] = visualCanvas;
            _inkCanvas.Children.Add(visualCanvas);

            return strokeVisual;
        }

        /// <summary>
        /// 获取视觉画布
        /// </summary>
        private VisualCanvas GetVisualCanvas(int id)
        {
            return _visualCanvasList.TryGetValue(id, out var visualCanvas) ? visualCanvas : null;
        }

        /// <summary>
        /// 获取触摸点状态
        /// </summary>
        private InkCanvasEditingMode GetTouchDownPointsList(int id)
        {
            return _touchDownPointsList.TryGetValue(id, out var mode) ? mode : _inkCanvas.EditingMode;
        }

        /// <summary>
        /// 清理手写笔预览
        /// </summary>
        private void CleanupStrokePreview(int id)
        {
            try
            {
                if (_visualCanvasList.TryGetValue(id, out var visualCanvas))
                {
                    if (_inkCanvas.Children.Contains(visualCanvas))
                    {
                        _inkCanvas.Children.Remove(visualCanvas);
                    }
                    _visualCanvasList.Remove(id);
                }
                _strokeVisualList.Remove(id);
                _touchDownPointsList.Remove(id);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"清理手写笔预览错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 清理所有手写笔预览
        /// </summary>
        private void CleanupAllStrokePreviews()
        {
            try
            {
                foreach (var canvas in _visualCanvasList.Values.ToList())
                {
                    if (_inkCanvas.Children.Contains(canvas))
                    {
                        _inkCanvas.Children.Remove(canvas);
                    }
                }
                _strokeVisualList.Clear();
                _visualCanvasList.Clear();
                _touchDownPointsList.Clear();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"清理所有手写笔预览错误: {ex.Message}");
            }
        }

        // =========================
        // 高级橡皮擦系统实现 (从 DrawingAndErasingLogic.cs 移植)
        // =========================

        /// <summary>
        /// 初始化橡皮擦覆盖层
        /// </summary>
        public void InitializeEraserOverlay(System.Windows.Controls.Canvas eraserOverlayCanvas)
        {
            _eraserOverlayCanvas = eraserOverlayCanvas;

            _eraserFeedback = _mainWindow.FindName("EraserFeedback") as System.Windows.Controls.Image;
            if (_eraserFeedback != null)
            {
                _eraserFeedbackTranslateTransform = _eraserFeedback.RenderTransform as TranslateTransform;
            }

            eraserOverlayCanvas.StylusDown += (o, args) =>
            {
                args.Handled = true;
                if (args.StylusDevice.TabletDevice.Type == TabletDeviceType.Stylus) eraserOverlayCanvas.CaptureStylus();
                EraserOverlay_PointerDown(o);
            };
            eraserOverlayCanvas.StylusUp += (o, args) =>
            {
                args.Handled = true;
                if (args.StylusDevice.TabletDevice.Type == TabletDeviceType.Stylus) eraserOverlayCanvas.ReleaseStylusCapture();
                EraserOverlay_PointerUp(o);
            };
            eraserOverlayCanvas.StylusMove += (o, args) =>
            {
                args.Handled = true;
                EraserOverlay_PointerMove(o, args.GetPosition(_inkCanvas));
            };
            eraserOverlayCanvas.MouseDown += (o, args) =>
            {
                eraserOverlayCanvas.CaptureMouse();
                EraserOverlay_PointerDown(o);
            };
            eraserOverlayCanvas.MouseUp += (o, args) =>
            {
                eraserOverlayCanvas.ReleaseMouseCapture();
                EraserOverlay_PointerUp(o);
            };
            eraserOverlayCanvas.MouseMove += (o, args) =>
            {
                EraserOverlay_PointerMove(o, args.GetPosition(_inkCanvas));
            };

            UpdateEraserStyle();
        }

        /// <summary>
        /// 更新橡皮擦样式
        /// </summary>
        private void UpdateEraserStyle()
        {
            if (_eraserFeedback == null) return;

            string resourceKey = _isEraserCircleShape ? "EllipseEraserImageSource" : "RectangleEraserImageSource";
            var imageSource = _mainWindow.TryFindResource(resourceKey) as DrawingImage;

            if (imageSource != null)
            {
                _eraserFeedback.Source = imageSource;
            }
        }

        /// <summary>
        /// 橡皮擦覆盖层按下
        /// </summary>
        private void EraserOverlay_PointerDown(object sender)
        {
            if (_isUsingGeometryEraser) return;

            _isUsingGeometryEraser = true;

            // 橡皮擦显示尺寸（屏幕坐标，与画板缩放无关）
            var _h = _eraserWidth * 56 / 38;

            // 命中测试使用画布坐标：屏幕尺寸 / 缩放比例
            double zoom = CurrentZoom > 0.01 ? CurrentZoom : 1.0;
            double canvasEraserWidth = _eraserWidth / zoom;
            double canvasH = _h / zoom;

            // 保存当前橡皮擦在画布坐标下的尺寸，供擦除填充图片使用
            _canvasEraserWidth = canvasEraserWidth;
            _canvasEraserHeight = _isEraserCircleShape ? canvasEraserWidth : canvasH;

            StylusShape eraserShape;
            if (_isEraserCircleShape)
            {
                eraserShape = new EllipseStylusShape(canvasEraserWidth, canvasEraserWidth);
            }
            else
            {
                eraserShape = new RectangleStylusShape(canvasEraserWidth, canvasH);
            }

            _hitTester = _inkCanvas.Strokes.GetIncrementalStrokeHitTester(eraserShape);
            _hitTester.StrokeHit += EraserGeometry_StrokeHit;

            var scaleX = _eraserWidth / 38;
            var scaleY = _h / 56;
            _scaleMatrix = new Matrix();
            _scaleMatrix.ScaleAt(scaleX, scaleY, 0, 0);

            if (_eraserFeedback != null)
            {
                // 反馈层使用屏幕坐标尺寸（不随画板缩放变化）
                _eraserFeedback.Width = Math.Max(_eraserWidth, 10);
                _eraserFeedback.Height = _isEraserCircleShape ? _eraserFeedback.Width : _h;
                _eraserFeedback.Measure(new System.Windows.Size(Double.PositiveInfinity, Double.PositiveInfinity));
                _eraserFeedback.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// 橡皮擦覆盖层抬起
        /// </summary>
        private void EraserOverlay_PointerUp(object sender)
        {
            if (!_isUsingGeometryEraser) return;

            _isUsingGeometryEraser = false;

            ((UIElement)sender).ReleaseMouseCapture();

            if (_eraserFeedback != null)
            {
                _eraserFeedback.Visibility = Visibility.Collapsed;
            }

            if (_hitTester != null)
            {
                _hitTester.EndHitTesting();
                _hitTester = null;
            }

            EndEdit();
        }

        /// <summary>
        /// 橡皮擦覆盖层移动
        /// </summary>
        private void EraserOverlay_PointerMove(object sender, WinPoint pt)
        {
            if (!_isUsingGeometryEraser) return;

            if (_isUsingStrokesEraser)
            {
                var _filtered = _inkCanvas.Strokes.HitTest(pt).Where(stroke => !stroke.ContainsPropertyData(_isLockGuid));
                var filtered = _filtered as Stroke[] ?? _filtered.ToArray();
                if (!filtered.Any()) return;
                _inkCanvas.Strokes.Remove(new StrokeCollection(filtered));
                // 同时擦除填充图片
                EraseFillImagesAt(pt, _canvasEraserWidth, _canvasEraserHeight, _isEraserCircleShape);
            }
            else
            {
                if (_eraserFeedback != null && _eraserFeedback.Visibility == Visibility.Collapsed)
                {
                    _eraserFeedback.Visibility = Visibility.Visible;
                }

                if (_eraserFeedbackTranslateTransform != null)
                {
                    // 反馈层位于屏幕坐标系（EraserOverlayCanvas 已移出 VideoArea），
                    // 需要把画布坐标 pt 转换为屏幕坐标
                    WinPoint screenPt = CanvasToScreenPoint(pt);
                    _eraserFeedbackTranslateTransform.X = screenPt.X - _eraserFeedback.ActualWidth / 2;
                    _eraserFeedbackTranslateTransform.Y = screenPt.Y - _eraserFeedback.ActualHeight / 2;
                }

                // 擦除填充图片
                EraseFillImagesAt(pt, _canvasEraserWidth, _canvasEraserHeight, _isEraserCircleShape);

                if (_hitTester != null)
                {
                    // 命中测试使用画布坐标
                    _hitTester.AddPoint(pt);
                }
            }
        }

        /// <summary>
        /// 橡皮擦几何命中
        /// </summary>
        private void EraserGeometry_StrokeHit(object sender, StrokeHitEventArgs args)
        {
            StrokeCollection eraseResult = args.GetPointEraseResults();
            StrokeCollection strokesToReplace = new StrokeCollection { args.HitStroke };

            var filtered_2replace = strokesToReplace.Where(stroke => !stroke.ContainsPropertyData(_isLockGuid));
            var filtered2Replace = filtered_2replace as Stroke[] ?? filtered_2replace.ToArray();
            if (!filtered2Replace.Any()) return;

            var filtered_result = eraseResult.Where(stroke => !stroke.ContainsPropertyData(_isLockGuid));
            var filteredResult = filtered_result as Stroke[] ?? filtered_result.ToArray();

            if (filteredResult.Any())
            {
                _inkCanvas.Strokes.Replace(new StrokeCollection(filtered2Replace), new StrokeCollection(filteredResult));
            }
            else
            {
                _inkCanvas.Strokes.Remove(new StrokeCollection(filtered2Replace));
            }
        }

        /// <summary>
        /// 启用橡皮擦覆盖层
        /// </summary>
        public void EnableEraserOverlay()
        {
            if (_eraserOverlayCanvas != null)
            {
                _eraserOverlayCanvas.IsHitTestVisible = true;
                _eraserOverlayCanvas.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// 禁用橡皮擦覆盖层
        /// </summary>
        public void DisableEraserOverlay()
        {
            if (_eraserOverlayCanvas != null)
            {
                _eraserOverlayCanvas.IsHitTestVisible = false;
                _eraserOverlayCanvas.Visibility = Visibility.Collapsed;
            }

            if (_isUsingGeometryEraser)
            {
                _isUsingGeometryEraser = false;
                if (_hitTester != null)
                {
                    _hitTester.EndHitTesting();
                    _hitTester = null;
                }
            }

            if (_eraserFeedback != null)
            {
                _eraserFeedback.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// 更新橡皮擦大小
        /// </summary>
        public void UpdateEraserSize(int sizeLevel)
        {
            double k = 1.0;

            switch (sizeLevel)
            {
                case 0: k = _isEraserCircleShape ? 0.5 : 0.7; break;
                case 1: k = _isEraserCircleShape ? 0.8 : 0.9; break;
                case 2: k = 1.0; break;
                case 3: k = _isEraserCircleShape ? 1.25 : 1.2; break;
                case 4: k = _isEraserCircleShape ? 1.5 : 1.3; break;
            }

            if (_isEraserCircleShape)
            {
                _eraserWidth = k * 90;
            }
            else
            {
                _eraserWidth = k * 90 * 0.6;
            }

            UpdateEraserStyle();
        }

        /// <summary>
        /// 切换橡皮擦形状
        /// </summary>
        public void ToggleEraserShape()
        {
            _isEraserCircleShape = !_isEraserCircleShape;
            UpdateEraserStyle();
        }

        /// <summary>
        /// 切换橡皮擦模式
        /// </summary>
        public void ToggleEraserMode()
        {
            _isUsingStrokesEraser = !_isUsingStrokesEraser;
        }

        /// <summary>
        /// 应用高级橡皮擦形状
        /// </summary>
        public void ApplyAdvancedEraserShape()
        {
            try
            {
                UpdateEraserSize(2);

                StylusShape eraserShape;
                if (_isEraserCircleShape)
                {
                    eraserShape = new EllipseStylusShape(_eraserWidth, _eraserWidth);
                }
                else
                {
                    var height = _eraserWidth * 56 / 38;
                    eraserShape = new RectangleStylusShape(_eraserWidth, height);
                }

                _inkCanvas.EraserShape = eraserShape;

                Console.WriteLine($"Eraser: Applied shape - Size: {_eraserWidth}, Circle: {_isEraserCircleShape}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Eraser: Error applying shape - {ex.Message}");
            }
        }

        // =========================
        // 绘制系统增强实现 (从 DrawingAndErasingLogic.cs 移植)
        // =========================

        /// <summary>
        /// 安全更新临时笔画
        /// </summary>
        private void UpdateTempStrokeSafely(Stroke newStroke)
        {
            var now = DateTime.Now;
            if ((now - _lastUpdateTime).TotalMilliseconds < _updateThrottleMs)
            {
                return;
            }
            _lastUpdateTime = now;

            try
            {
                _inkCanvas.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        _inkCanvas.Strokes.Add(newStroke);

                        if (_lastTempStroke != null && _inkCanvas.Strokes.Contains(_lastTempStroke))
                        {
                            _inkCanvas.Strokes.Remove(_lastTempStroke);
                        }

                        _lastTempStroke = newStroke;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"UpdateTempStrokeSafely 失败: {ex.Message}");
                        if (_lastTempStroke != null && _inkCanvas.Strokes.Contains(_lastTempStroke))
                        {
                            try { _inkCanvas.Strokes.Remove(_lastTempStroke); } catch { }
                        }
                        _lastTempStroke = newStroke;
                        try { _inkCanvas.Strokes.Add(newStroke); } catch { }
                    }
                }), DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UpdateTempStrokeSafely Dispatcher 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 安全更新临时笔画集合
        /// </summary>
        private void UpdateTempStrokeCollectionSafely(StrokeCollection newStrokeCollection)
        {
            var now = DateTime.Now;
            if ((now - _lastUpdateTime).TotalMilliseconds < _updateThrottleMs)
            {
                return;
            }
            _lastUpdateTime = now;

            try
            {
                _inkCanvas.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        _inkCanvas.Strokes.Add(newStrokeCollection);

                        if (_lastTempStrokeCollection != null && _lastTempStrokeCollection.Count > 0)
                        {
                            foreach (var stroke in _lastTempStrokeCollection)
                            {
                                if (_inkCanvas.Strokes.Contains(stroke))
                                {
                                    _inkCanvas.Strokes.Remove(stroke);
                                }
                            }
                        }

                        _lastTempStrokeCollection = newStrokeCollection;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"UpdateTempStrokeCollectionSafely 失败: {ex.Message}");
                        if (_lastTempStrokeCollection != null && _lastTempStrokeCollection.Count > 0)
                        {
                            foreach (var stroke in _lastTempStrokeCollection)
                            {
                                try { _inkCanvas.Strokes.Remove(stroke); } catch { }
                            }
                        }
                        _lastTempStrokeCollection = newStrokeCollection;
                        try { _inkCanvas.Strokes.Add(newStrokeCollection); } catch { }
                    }
                }), DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UpdateTempStrokeCollectionSafely Dispatcher 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 生成椭圆几何
        /// </summary>
        private List<WinPoint> GenerateEllipseGeometry(WinPoint st, WinPoint ed, bool isDrawTop = true, bool isDrawBottom = true)
        {
            var a = 0.5 * (ed.X - st.X);
            var b = 0.5 * (ed.Y - st.Y);
            var pointList = new List<WinPoint>();
            if (isDrawTop && isDrawBottom)
            {
                for (double r = 0; r <= 2 * Math.PI; r = r + 0.01)
                    pointList.Add(new WinPoint(0.5 * (st.X + ed.X) + a * Math.Cos(r),
                        0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
            }
            else
            {
                if (isDrawBottom)
                    for (double r = 0; r <= Math.PI; r = r + 0.01)
                        pointList.Add(new WinPoint(0.5 * (st.X + ed.X) + a * Math.Cos(r),
                            0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
                if (isDrawTop)
                    for (var r = Math.PI; r <= 2 * Math.PI; r = r + 0.01)
                        pointList.Add(new WinPoint(0.5 * (st.X + ed.X) + a * Math.Cos(r),
                            0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
            }

            return pointList;
        }

        /// <summary>
        /// 生成虚线椭圆笔画集合
        /// </summary>
        private StrokeCollection GenerateDashedLineEllipseStrokeCollection(WinPoint st, WinPoint ed, bool isDrawTop = true, bool isDrawBottom = true)
        {
            var a = 0.5 * (ed.X - st.X);
            var b = 0.5 * (ed.Y - st.Y);
            var step = 0.05;
            var pointList = new List<WinPoint>();
            StylusPointCollection point;
            Stroke stroke;
            var strokes = new StrokeCollection();
            if (isDrawBottom)
                for (var i = 0.0; i < 1.0; i += step * 1.66)
                {
                    pointList = new List<WinPoint>();
                    for (var r = Math.PI * i; r <= Math.PI * (i + step); r = r + 0.01)
                        pointList.Add(new WinPoint(0.5 * (st.X + ed.X) + a * Math.Cos(r),
                            0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = _inkCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                }

            if (isDrawTop)
                for (var i = 1.0; i < 2.0; i += step * 1.66)
                {
                    pointList = new List<WinPoint>();
                    for (var r = Math.PI * i; r <= Math.PI * (i + step); r = r + 0.01)
                        pointList.Add(new WinPoint(0.5 * (st.X + ed.X) + a * Math.Cos(r),
                            0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
                    point = new StylusPointCollection(pointList);
                    stroke = new Stroke(point)
                    {
                        DrawingAttributes = _inkCanvas.DefaultDrawingAttributes.Clone()
                    };
                    strokes.Add(stroke.Clone());
                }

            return strokes;
        }

        public void Dispose()
        {
            _inkCanvas.StrokeCollected -= Ink_StrokeCollected;
            _inkCanvas.PreviewMouseLeftButtonDown -= Ink_PreviewMouseDown;
            _inkCanvas.PreviewMouseLeftButtonUp -= Ink_PreviewMouseUp;
            _inkCanvas.PreviewStylusDown -= Ink_PreviewStylusDown;
            _inkCanvas.PreviewStylusUp -= Ink_PreviewStylusUp;
            _inkCanvas.Strokes.StrokesChanged -= Ink_StrokesChanged;

            if (_overlayInkCanvas != null)
            {
                _overlayInkCanvas.StrokeCollected -= OverlayInk_StrokeCollected;
            }

            _editHistory.Clear();
            _redoHistory.Clear();
            _touchPoints.Clear();
            _touchDeviceIds.Clear();

            CleanupAllStrokePreviews();
            ResetMultiTouchState();
            CleanupTouchSDK();
        }
    }
}