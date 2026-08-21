using ShowWriteAir.Models;
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

namespace ShowWriteAir
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
        public double UserPenWidth { get; set; } = 2.0;
        public Color PenColor => _inkCanvas.DefaultDrawingAttributes.Color;

        // 触摸点跟踪
        private readonly Dictionary<int, WinPoint> _touchPoints = new Dictionary<int, WinPoint>();
        private double _lastTouchDistance = -1;
        private WinPoint _lastTouchCenter;

        // 橡皮擦设置
        private double _manualEraserSize = 20.0;
        private bool _enableCanvasMode = false;
        public bool EnableCanvasMode
        {
            get => _enableCanvasMode;
            set
            {
                _enableCanvasMode = value;
                if (_enableCanvasMode) _isEraserCircleShape = true;
                UpdateEraserStyle();
            }
        }

        // 高级橡皮擦系统
        private bool _isUsingGeometryEraser = false;
        private IncrementalStrokeHitTester _hitTester = null;
        private double _eraserWidth = 64;
        private bool _isEraserCircleShape = false;
        private bool _isUsingStrokesEraser = false;
        private double _canvasEraserWidth = 0;
        private double _canvasEraserHeight = 0;
        private Matrix _scaleMatrix = new Matrix();
        private System.Windows.Controls.Canvas _eraserOverlayCanvas;
        private WinImage _eraserFeedback;
        private TranslateTransform _eraserFeedbackTranslateTransform;
        private static readonly Guid _isLockGuid = new Guid("12345678-1234-1234-1234-123456789ABC");
        private const string _fillImageTag = "PaintBucketFill";

        // 绘制系统增强
        private bool _isLongPressSelected = false;
        private bool _isMouseDown = false;
        private bool _isTouchDown = false;
        private WinPoint _iniP = new WinPoint(0, 0);
        private DateTime _lastUpdateTime = DateTime.MinValue;
        private const int _updateThrottleMs = 16;
        private Stroke _lastTempStroke;
        private StrokeCollection _lastTempStrokeCollection = new StrokeCollection();

        // 多点触控系统增强
        private bool _isInMultiTouchMode = false;
        private List<int> _dec = new List<int>();
        private WinPoint _centerPoint = new WinPoint(0, 0);
        private InkCanvasEditingMode _lastInkCanvasEditingMode = InkCanvasEditingMode.Ink;
        private const double _multiTouchDelayMs = 100;
        private bool _isPanning = false;
        private WinPoint _lastMousePos;

        // 形状绘制功能
        private int _drawingShapeMode = 0;
        private bool _isDrawingShape = false;
        private WinPoint _shapeStartPoint;
        private Stroke _tempStroke;
        private StrokeCollection _tempStrokeCollection;

        // 手写笔预览功能
        private readonly Dictionary<int, StrokeVisual> _strokeVisualList = new Dictionary<int, StrokeVisual>();
        private readonly Dictionary<int, VisualCanvas> _visualCanvasList = new Dictionary<int, VisualCanvas>();
        private readonly Dictionary<int, InkCanvasEditingMode> _touchDownPointsList = new Dictionary<int, InkCanvasEditingMode>();

        // UI 元素保护机制
        private List<UIElement> _preservedElements;

        public List<UIElement> PreserveNonStrokeElements()
        {
            var preservedElements = new List<UIElement>();
            try
            {
                for (int i = _inkCanvas.Children.Count - 1; i >= 0; i--)
                {
                    var child = _inkCanvas.Children[i];
                    if (child is WinImage || child is MediaElement ||
                        (child is Border border && border.Name != "EraserOverlayCanvas"))
                    {
                        var clonedElement = CloneUIElement(child);
                        if (clonedElement != null) preservedElements.Add(clonedElement);
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"保存非笔画元素失败: {ex.Message}"); }
            return preservedElements;
        }

        private UIElement CloneUIElement(UIElement originalElement)
        {
            try
            {
                if (originalElement is WinImage originalImage)
                {
                    var clonedImage = new WinImage();
                    if (originalImage.Source is BitmapSource bitmapSource) clonedImage.Source = bitmapSource;
                    clonedImage.Width = originalImage.Width;
                    clonedImage.Height = originalImage.Height;
                    clonedImage.Stretch = originalImage.Stretch;
                    clonedImage.StretchDirection = originalImage.StretchDirection;
                    clonedImage.Name = originalImage.Name;
                    clonedImage.IsHitTestVisible = originalImage.IsHitTestVisible;
                    clonedImage.Focusable = originalImage.Focusable;
                    clonedImage.Cursor = originalImage.Cursor;
                    clonedImage.IsManipulationEnabled = originalImage.IsManipulationEnabled;
                    InkCanvas.SetLeft(clonedImage, InkCanvas.GetLeft(originalImage));
                    InkCanvas.SetTop(clonedImage, InkCanvas.GetTop(originalImage));
                    if (originalImage.RenderTransform != null) clonedImage.RenderTransform = originalImage.RenderTransform.Clone();
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
                    InkCanvas.SetLeft(clonedBorder, InkCanvas.GetLeft(originalBorder));
                    InkCanvas.SetTop(clonedBorder, InkCanvas.GetTop(originalBorder));
                    return clonedBorder;
                }
            }
            catch (Exception ex) { Console.WriteLine($"克隆 UI 元素失败: {ex.Message}"); }
            return null;
        }

        public void RestoreNonStrokeElements(List<UIElement> preservedElements)
        {
            if (preservedElements == null) return;
            try { foreach (var element in preservedElements) _inkCanvas.Children.Add(element); }
            catch (Exception ex) { Console.WriteLine($"恢复非笔画元素失败: {ex.Message}"); }
        }

        public void ClearCanvasPreserveElements()
        {
            try
            {
                _preservedElements = PreserveNonStrokeElements();
                _inkCanvas.Children.Clear();
                RestoreNonStrokeElements(_preservedElements);
            }
            catch (Exception ex) { Console.WriteLine($"清除画布失败: {ex.Message}"); }
        }

        // 触摸事件处理改进
        private bool _isMultiTouchMode = false;
        private bool _isSingleFingerDragMode = false;
        private readonly List<int> _touchDeviceIds = new List<int>();
        private DateTime _lastTouchDownTime = DateTime.MinValue;
        private const double MultiTouchDelayMs = 100;

        public bool IsMultiTouchMode => _isMultiTouchMode;
        public bool IsSingleFingerDragMode => _isSingleFingerDragMode;
        public void ToggleSingleFingerDragMode() { _isSingleFingerDragMode = !_isSingleFingerDragMode; }
        public void CancelSingleFingerDragMode() { if (_isSingleFingerDragMode) _isSingleFingerDragMode = false; }

        public void HandleTouchDown(int touchId, WinPoint position)
        {
            _touchDeviceIds.Add(touchId);
            _lastTouchDownTime = DateTime.Now;
            _isMultiTouchMode = _touchDeviceIds.Count > 1;
            _touchPoints[touchId] = position;
            if (_isDrawingShape) StartShapeDrawing(position, _drawingShapeMode);
        }

        public void HandleTouchMove(int touchId, WinPoint position)
        {
            if (!_touchPoints.ContainsKey(touchId)) return;
            _touchPoints[touchId] = position;
            if (_isDrawingShape && _touchPoints.Count == 1) UpdateShapePreview(position);
            if (_touchPoints.Count >= 2) HandleMultiTouchMove();
        }

        public void HandleTouchUp(int touchId)
        {
            if (!_touchDeviceIds.Contains(touchId)) return;
            _touchDeviceIds.Remove(touchId);
            _touchPoints.Remove(touchId);
            if (_isDrawingShape && _touchDeviceIds.Count == 0) CommitShape();
            if (_touchDeviceIds.Count <= 1) _isMultiTouchMode = false;
        }

        private void HandleMultiTouchMove()
        {
            if (_touchPoints.Count < 2) return;
            var points = _touchPoints.Values.ToList();
            var p1 = points[0]; var p2 = points[1];
            var center = new WinPoint((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2);
            var distance = Math.Sqrt(Math.Pow(p2.X - p1.X, 2) + Math.Pow(p2.Y - p1.Y, 2));
            if (_lastTouchDistance < 0) { _lastTouchDistance = distance; _lastTouchCenter = center; return; }
            var scale = distance / _lastTouchDistance;
            var deltaX = center.X - _lastTouchCenter.X;
            var deltaY = center.Y - _lastTouchCenter.Y;
            _lastTouchDistance = distance; _lastTouchCenter = center;
        }

        public void ResetMultiTouchState()
        {
            _isMultiTouchMode = false; _isSingleFingerDragMode = false;
            _touchDeviceIds.Clear(); _touchPoints.Clear();
            _lastTouchDistance = -1; _lastTouchCenter = new WinPoint(0, 0);
        }

        // TouchSDK 相关
        private bool _touchSDKInitialized = false;
        private double _sdkTouchArea = 0;
        private bool _isPalmEraserActive = false;
        private ToolMode _lastModeBeforePalmEraser = ToolMode.Pen;
        private double _palmEraserThreshold = 5000.0;
        private double _currentTouchArea = 0.0;
        private bool _enablePalmEraser = true;
        private bool _inSetMode = false;
        private bool _isEnabled = true;

        public double PalmEraserThreshold { get => _palmEraserThreshold; set { _palmEraserThreshold = Math.Max(1000, value); } }
        public bool EnablePalmEraser { get => _enablePalmEraser; set => _enablePalmEraser = value; }
        public bool IsPalmEraserActive => _isPalmEraserActive;

        private delegate void FuncTouchPointData(IntPtr pDevInfo, IntPtr pdata, int maxpointnum, int nValidPointNum, IntPtr pObj);
        private delegate void FuncHotplugDevInfo(IntPtr devInfo, byte attached, IntPtr callbackobject);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DeviceInfo { public int deviceID; public int vendorID; public int productID; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string deviceName; public int maxTouchPoints; public int resolutionX; public int resolutionY; }
        [StructLayout(LayoutKind.Sequential)]
        public struct TouchPointData { public int x; public int y; public int width; public int height; public int pressure; public byte touchState; public byte touchID; public byte area; public byte reserved; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetDllDirectory(string lpPathName);
        [DllImport("TouchSDKDll.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int InitTouch([In, Out] DeviceInfo[] pDevInfos, int nMaxDevInfoNum, FuncTouchPointData funcTouchPointData, FuncHotplugDevInfo funcHotplugDevInfo, IntPtr pObj);
        [DllImport("TouchSDKDll.dll", CallingConvention = CallingConvention.Cdecl)] private static extern bool EnableTouch(DeviceInfo DevInfo, int nTimeout = 20);
        [DllImport("TouchSDKDll.dll", CallingConvention = CallingConvention.Cdecl)] private static extern bool EnableRawData(DeviceInfo DevInfo, int nTimeout = 20);
        [DllImport("TouchSDKDll.dll", CallingConvention = CallingConvention.Cdecl)] private static extern bool EnableTouchWidthData(DeviceInfo DevInfo, int nTimeout = 20);
        [DllImport("TouchSDKDll.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int GetTouchDeviceCount();
        [DllImport("TouchSDKDll.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void ExitTouch();

        public double SDKTouchArea { get => _sdkTouchArea; private set { if (_sdkTouchArea != value) { _sdkTouchArea = value; OnSDKTouchAreaChanged?.Invoke(value); } } }
        public event Action<double> OnSDKTouchAreaChanged;
        public event Action<bool> OnPalmEraserStateChanged;

        public DrawingManager(InkCanvas inkCanvas, FrameworkElement videoArea, Window mainWindow)
        {
            _inkCanvas = inkCanvas; _videoArea = videoArea; _mainWindow = mainWindow;
            InitializeEventHandlers(); CurrentMode = ToolMode.Move;
            _inkCanvas.EditingMode = InkCanvasEditingMode.None; _mainWindow.Cursor = Cursors.Hand;
            InitializeTouchSDK();
        }

        public void SetOverlayInkCanvas(InkCanvas overlayInkCanvas, ScaleTransform zoomTransform, TranslateTransform panTransform)
        {
            _overlayInkCanvas = overlayInkCanvas; _zoomTransform = zoomTransform; _panTransform = panTransform;
            if (_overlayInkCanvas != null)
            {
                _overlayInkCanvas.StrokeCollected += OverlayInk_StrokeCollected;
                _overlayInkCanvas.DefaultDrawingAttributes = _inkCanvas.DefaultDrawingAttributes.Clone();
            }
        }

        public void SetUseOverlayInkCanvas(bool use)
        {
            _useOverlayInkCanvas = use;
            if (_overlayInkCanvas != null) _overlayInkCanvas.Visibility = use && CurrentMode == ToolMode.Pen ? Visibility.Visible : Visibility.Collapsed;
        }

        public bool IsOverlayInkCanvasActive => _useOverlayInkCanvas && _overlayInkCanvas != null && CurrentMode == ToolMode.Pen;
        public void SetEnabled(bool enabled) { _isEnabled = enabled; }
        private bool CheckEnabled() { return _isEnabled; }

        private void InitializeEventHandlers()
        {
            _inkCanvas.StrokeCollected += Ink_StrokeCollected;
            _inkCanvas.PreviewMouseLeftButtonDown += Ink_PreviewMouseDown;
            _inkCanvas.PreviewMouseLeftButtonUp += Ink_PreviewMouseUp;
            _inkCanvas.PreviewStylusDown += Ink_PreviewStylusDown;
            _inkCanvas.PreviewStylusUp += Ink_PreviewStylusUp;
            _inkCanvas.Strokes.StrokesChanged += Ink_StrokesChanged;
        }

        public bool InitializeTouchSDK()
        {
            try
            {
                string arch = IntPtr.Size == 8 ? "x64" : "x86";
                string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, arch, "TouchSDKDll.dll");
                if (!File.Exists(dllPath)) return false;
                string archDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, arch);
                if (!SetDllDirectory(archDirectory)) return false;
                int deviceCount = GetTouchDeviceCount();
                if (deviceCount <= 0) return false;
                var deviceInfos = new DeviceInfo[10];
                int initResult = InitTouch(deviceInfos, deviceInfos.Length, OnTouchPointData, OnHotplugEvent, IntPtr.Zero);
                if (initResult != 0) return false;
                if (deviceCount > 0)
                {
                    var firstDevice = deviceInfos[0];
                    if (!EnableTouch(firstDevice)) return false;
                    if (!EnableRawData(firstDevice)) return false;
                    if (!EnableTouchWidthData(firstDevice)) return false;
                }
                _touchSDKInitialized = true;
                return true;
            }
            catch { return false; }
        }

        private void OnTouchPointData(IntPtr pDevInfo, IntPtr pdata, int maxpointnum, int nValidPointNum, IntPtr pObj)
        {
            try
            {
                if (nValidPointNum <= 0) { SDKTouchArea = 0; return; }
                double totalArea = 0;
                for (int i = 0; i < nValidPointNum; i++)
                {
                    int offset = i * Marshal.SizeOf<TouchPointData>();
                    IntPtr pointPtr = IntPtr.Add(pdata, offset);
                    var point = Marshal.PtrToStructure<TouchPointData>(pointPtr);
                    totalArea += point.width * point.height;
                }
                SDKTouchArea = totalArea;
            }
            catch { SDKTouchArea = 0; }
        }

        private void OnHotplugEvent(IntPtr devInfo, byte attached, IntPtr callbackobject) { }
        private void CleanupTouchSDK() { if (_touchSDKInitialized) { try { ExitTouch(); _touchSDKInitialized = false; } catch { } } }
        public bool IsTouchSDKInitialized => _touchSDKInitialized;

        public void ApplyConfig(AppConfig config)
        {
            var penColor = (Color)System.Windows.Media.ColorConverter.ConvertFromString(config.DefaultPenColor ?? "#FF000000");
            _inkCanvas.DefaultDrawingAttributes.Color = penColor;
            UserPenWidth = config.DefaultPenWidth;
            _inkCanvas.EraserShape = new RectangleStylusShape(_manualEraserSize, _manualEraserSize);
            if (config.EnablePalmEraser) { EnablePalmEraser = true; PalmEraserThreshold = config.PalmEraserThreshold; }
            else EnablePalmEraser = false;
            EnableCanvasMode = config.EnableCanvasMode;
            UpdatePenAttributes();
        }

        public void SetPenColor(Color color)
        {
            _inkCanvas.DefaultDrawingAttributes.Color = color;
            if (_overlayInkCanvas != null) _overlayInkCanvas.DefaultDrawingAttributes.Color = color;
        }

        private void StartEdit() { if (_isEditing) return; _currentEdit = new EditAction(); _isEditing = true; }
        private void EndEdit()
        {
            if (!_isEditing || _currentEdit == null) return;
            if (_currentEdit.AddedStrokes.Count > 0 || _currentEdit.RemovedStrokes.Count > 0)
            {
                _editHistory.Push(_currentEdit); _redoHistory.Clear();
            }
            _currentEdit = null; _isEditing = false;
        }

        private void Ink_PreviewMouseDown(object sender, MouseButtonEventArgs e) => StartEdit();
        private void Ink_PreviewMouseUp(object sender, MouseButtonEventArgs e) { if (CurrentMode != ToolMode.Pen) EndEdit(); }
        private void Ink_PreviewStylusDown(object sender, StylusDownEventArgs e) => StartEdit();
        private void Ink_PreviewStylusUp(object sender, StylusEventArgs e) { if (CurrentMode != ToolMode.Pen) EndEdit(); }

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
            catch { }
        }

        private Stroke TransformStrokeFromOverlayToMain(Stroke overlayStroke)
        {
            if (_zoomTransform == null || _panTransform == null) return null;
            try
            {
                var stylusPoints = overlayStroke.StylusPoints;
                var transformedPoints = new StylusPointCollection();
                double zoom = _zoomTransform.ScaleX;
                double panX = _panTransform.X; double panY = _panTransform.Y;
                foreach (var point in stylusPoints)
                {
                    double canvasX = (point.X - panX) / zoom;
                    double canvasY = (point.Y - panY) / zoom;
                    transformedPoints.Add(new StylusPoint(canvasX, canvasY, point.PressureFactor));
                }
                var newStroke = new Stroke(transformedPoints);
                double adjustedWidth = Math.Max(0.5, UserPenWidth / zoom);
                newStroke.DrawingAttributes = new DrawingAttributes
                {
                    Color = overlayStroke.DrawingAttributes.Color,
                    Width = adjustedWidth,
                    Height = adjustedWidth,
                    StylusTip = overlayStroke.DrawingAttributes.StylusTip,
                    IsHighlighter = overlayStroke.DrawingAttributes.IsHighlighter
                };
                return newStroke;
            }
            catch { return null; }
        }

        public WinPoint ScreenToCanvasPoint(WinPoint screenPoint)
        {
            if (_zoomTransform == null || _panTransform == null) return screenPoint;
            double zoom = _zoomTransform.ScaleX; double panX = _panTransform.X; double panY = _panTransform.Y;
            return new WinPoint((screenPoint.X - panX) / zoom, (screenPoint.Y - panY) / zoom);
        }

        public WinPoint CanvasToScreenPoint(WinPoint canvasPoint)
        {
            if (_zoomTransform == null || _panTransform == null) return canvasPoint;
            double zoom = _zoomTransform.ScaleX; double panX = _panTransform.X; double panY = _panTransform.Y;
            return new WinPoint(canvasPoint.X * zoom + panX, canvasPoint.Y * zoom + panY);
        }

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
                if (CurrentMode == mode && !initial) return;
                if (_isPalmEraserActive && mode != ToolMode.Eraser) ForceDeactivatePalmEraser();
                CurrentMode = mode;
                if (_overlayInkCanvas != null)
                {
                    _overlayInkCanvas.Visibility = (_useOverlayInkCanvas && mode == ToolMode.Pen) ? Visibility.Visible : Visibility.Collapsed;
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
                    }
                }
                switch (mode)
                {
                    case ToolMode.Move: _inkCanvas.EditingMode = InkCanvasEditingMode.None; _mainWindow.Cursor = Cursors.Hand; _drawingShapeMode = 0; break;
                    case ToolMode.Pen: _inkCanvas.EditingMode = _useOverlayInkCanvas && _overlayInkCanvas != null ? InkCanvasEditingMode.None : InkCanvasEditingMode.Ink; _mainWindow.Cursor = Cursors.Arrow; _drawingShapeMode = 0; break;
                    case ToolMode.Eraser: _inkCanvas.EditingMode = InkCanvasEditingMode.EraseByPoint; _mainWindow.Cursor = Cursors.Arrow; _drawingShapeMode = 0; if (!_isPalmEraserActive) _inkCanvas.EraserShape = new RectangleStylusShape(_manualEraserSize, _manualEraserSize); break;
                    case ToolMode.Line: _inkCanvas.EditingMode = InkCanvasEditingMode.None; _mainWindow.Cursor = Cursors.Cross; _drawingShapeMode = 1; break;
                    case ToolMode.Arrow: _inkCanvas.EditingMode = InkCanvasEditingMode.None; _mainWindow.Cursor = Cursors.Cross; _drawingShapeMode = 2; break;
                    case ToolMode.Rectangle: _inkCanvas.EditingMode = InkCanvasEditingMode.None; _mainWindow.Cursor = Cursors.Cross; _drawingShapeMode = 3; break;
                    case ToolMode.Ellipse: _inkCanvas.EditingMode = InkCanvasEditingMode.None; _mainWindow.Cursor = Cursors.Cross; _drawingShapeMode = 4; break;
                    case ToolMode.Circle: _inkCanvas.EditingMode = InkCanvasEditingMode.None; _mainWindow.Cursor = Cursors.Cross; _drawingShapeMode = 5; break;
                    case ToolMode.DashedLine: _inkCanvas.EditingMode = InkCanvasEditingMode.None; _mainWindow.Cursor = Cursors.Cross; _drawingShapeMode = 8; break;
                    case ToolMode.DotLine: _inkCanvas.EditingMode = InkCanvasEditingMode.None; _mainWindow.Cursor = Cursors.Cross; _drawingShapeMode = 18; break;
                    case ToolMode.PaintBucket: _inkCanvas.EditingMode = InkCanvasEditingMode.None; _mainWindow.Cursor = Cursors.Cross; _drawingShapeMode = 0; break;
                }
            }
            finally { _inSetMode = false; }
        }

        public void OpenPenSettings()
        {
            double currentEraserWidth = _manualEraserSize;
            if (_inkCanvas.EraserShape is RectangleStylusShape rectShape) currentEraserWidth = rectShape.Width;
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
            double compensatedWidth = Math.Max(1.0, Math.Min(50.0, UserPenWidth / Math.Max(CurrentZoom, 0.01)));
            _inkCanvas.DefaultDrawingAttributes.Width = compensatedWidth;
            _inkCanvas.DefaultDrawingAttributes.Height = compensatedWidth;
            if (_overlayInkCanvas != null)
            {
                _overlayInkCanvas.DefaultDrawingAttributes.Width = UserPenWidth;
                _overlayInkCanvas.DefaultDrawingAttributes.Height = UserPenWidth;
                if (CurrentMode == ToolMode.Pen && _useOverlayInkCanvas) _overlayInkCanvas.DefaultDrawingAttributes.Color = _inkCanvas.DefaultDrawingAttributes.Color;
            }
        }

        public void ClearStrokes() { _inkCanvas.Strokes.Clear(); ClearFillImages(); _editHistory.Clear(); _redoHistory.Clear(); }

        public void ClearFillImages()
        {
            try
            {
                var toRemove = new List<UIElement>();
                foreach (var child in _inkCanvas.Children)
                {
                    if (child is System.Windows.Shapes.Path p && p.Tag is string tag1 && tag1 == _fillImageTag) toRemove.Add(p);
                    else if (child is WinImage img && img.Tag is string tag2 && tag2 == _fillImageTag) toRemove.Add(img);
                }
                foreach (var el in toRemove) _inkCanvas.Children.Remove(el);
            }
            catch { }
        }

        public List<WinImage> GetFillImages()
        {
            var result = new List<WinImage>();
            try { foreach (var child in _inkCanvas.Children) if (child is WinImage img && img.Tag is string tag && tag == _fillImageTag) result.Add(img); } catch { }
            return result;
        }

        /// <summary>
        /// 矢量填充快照项：包含已冻结的 PathGeometry 与填充色，便于在 PhotoWithStrokes 上持久化。
        /// </summary>
        public sealed class FillPathRecord
        {
            public System.Windows.Media.PathGeometry Geometry;
            public System.Windows.Media.Color Color;
        }

        /// <summary>
        /// 位图填充快照项：包含位图与 InkCanvas DIP 坐标/尺寸，用于持久化与跨尺寸还原。
        /// </summary>
        public sealed class FillImageRecord
        {
            public BitmapSource Bitmap;
            public double X, Y, Width, Height;
        }

        /// <summary>
        /// 对当前 InkCanvas 上所有矢量填充（Tag=PaintBucketFill 的 Path）做一份可序列化快照。
        /// 返回的 PathGeometry 均为冻结副本，可安全跨线程/跨照片持有。
        /// </summary>
        public List<FillPathRecord> GetFillPathsSnapshot()
        {
            var result = new List<FillPathRecord>();
            try
            {
                foreach (var child in _inkCanvas.Children)
                {
                    if (child is System.Windows.Shapes.Path p && p.Tag is string tag && tag == _fillImageTag)
                    {
                        var geo = p.Data as System.Windows.Media.PathGeometry
                                  ?? System.Windows.Media.PathGeometry.CreateFromGeometry(p.Data);
                        if (geo == null)
                        {
                            Logger.Warning("DrawingManager", $"GetFillPathsSnapshot: 跳过 Path，无法转 PathGeometry (Data 类型: {p.Data?.GetType().Name})");
                            continue;
                        }
                        // 填充 Path 的几何已内嵌坐标变换（AddFillPath/AddFillPathGeometry 在生成时已 flatten），
                        // 此处仅需克隆并冻结即可作为快照；Path 本身无 RenderTransform。
                        var clone = geo.Clone();
                        if (clone.CanFreeze) clone.Freeze();
                        var color = System.Windows.Media.Colors.Black;
                        if (p.Fill is System.Windows.Media.SolidColorBrush b) color = b.Color;
                        result.Add(new FillPathRecord { Geometry = clone, Color = color });
                    }
                }
                Logger.Info("DrawingManager", $"GetFillPathsSnapshot: 捕获 {result.Count} 条矢量填充");
            }
            catch (Exception ex) { Logger.Error("DrawingManager", $"GetFillPathsSnapshot 失败: {ex.Message}", ex); }
            return result;
        }

        /// <summary>
        /// 对当前 InkCanvas 上所有位图填充（Tag=PaintBucketFill 的 Image）做一份快照。
        /// </summary>
        public List<FillImageRecord> GetFillImagesSnapshot()
        {
            var result = new List<FillImageRecord>();
            try
            {
                foreach (var child in _inkCanvas.Children)
                {
                    if (child is WinImage img && img.Tag is string tag && tag == _fillImageTag)
                    {
                        var bmp = img.Source as BitmapSource;
                        if (bmp == null) continue;
                        if (!bmp.IsFrozen && bmp.CanFreeze) bmp.Freeze();
                        result.Add(new FillImageRecord
                        {
                            Bitmap = bmp,
                            X = InkCanvas.GetLeft(img),
                            Y = InkCanvas.GetTop(img),
                            Width = img.Width,
                            Height = img.Height
                        });
                    }
                }
            }
            catch { }
            return result;
        }

        /// <summary>
        /// 按给定 PathGeometry 与填充色新增矢量填充，可选按 scaleX/scaleY 应用变换。
        /// 与 <see cref="AddFillPath(string, Color, double, double)"/> 区别：直接接收几何对象，避免字符串往返解析。
        /// </summary>
        public void AddFillPathGeometry(System.Windows.Media.PathGeometry geometry, System.Windows.Media.Color color, double scaleX = 1.0, double scaleY = 1.0)
        {
            try
            {
                if (geometry == null)
                {
                    Logger.Warning("DrawingManager", "AddFillPathGeometry: geometry 为 null，跳过");
                    return;
                }
                System.Windows.Media.Geometry final;
                if (Math.Abs(scaleX - 1.0) > 1e-6 || Math.Abs(scaleY - 1.0) > 1e-6)
                {
                    var srcPg = geometry as System.Windows.Media.PathGeometry
                                ?? System.Windows.Media.PathGeometry.CreateFromGeometry(geometry) as System.Windows.Media.PathGeometry;
                    if (srcPg == null)
                    {
                        Logger.Warning("DrawingManager", "AddFillPathGeometry: 无法转 PathGeometry，跳过");
                        return;
                    }
                    var matrix = new System.Windows.Media.Matrix(); matrix.Scale(scaleX, scaleY);
                    var transformed = new System.Windows.Media.PathGeometry(srcPg.Figures, srcPg.FillRule, new System.Windows.Media.MatrixTransform(matrix));
                    final = transformed.GetFlattenedPathGeometry();
                    Logger.Info("DrawingManager", $"AddFillPathGeometry: 缩放还原填充 scale={scaleX:F3}x{scaleY:F3}, 颜色=#{color.R:X2}{color.G:X2}{color.B:X2}");
                }
                else
                {
                    final = geometry;
                    Logger.Info("DrawingManager", $"AddFillPathGeometry: 原样还原填充 (无缩放), 颜色=#{color.R:X2}{color.G:X2}{color.B:X2}");
                }
                var path = new System.Windows.Shapes.Path { Data = final, Fill = new System.Windows.Media.SolidColorBrush(color), Stroke = null, IsHitTestVisible = false, Tag = _fillImageTag };
                InkCanvas.SetLeft(path, 0); InkCanvas.SetTop(path, 0);
                _inkCanvas.Children.Insert(0, path);
            }
            catch (Exception ex) { Logger.Error("DrawingManager", $"AddFillPathGeometry 失败: {ex.Message}", ex); }
        }

        public void AddStrokes(StrokeCollection strokes) { if (strokes == null || strokes.Count == 0) return; try { _inkCanvas.Strokes.Add(strokes); } catch { } }

        public void AddFillImage(BitmapSource bitmap, double x, double y, double width, double height)
        {
            try
            {
                if (bitmap == null) return;
                if (!bitmap.IsFrozen && bitmap.CanFreeze) bitmap.Freeze();
                if (width <= 0) width = bitmap.PixelWidth; if (height <= 0) height = bitmap.PixelHeight;
                var image = new WinImage { Source = bitmap, Width = width, Height = height, IsHitTestVisible = false, Tag = _fillImageTag };
                InkCanvas.SetLeft(image, x); InkCanvas.SetTop(image, y);
                _inkCanvas.Children.Insert(0, image);
            }
            catch { }
        }

        private void EraseFillImagesAt(WinPoint canvasPt, double canvasEraserWidth, double canvasEraserHeight, bool isCircle)
        {
            try
            {
                double radiusX = canvasEraserWidth / 2.0; double radiusY = canvasEraserHeight / 2.0;
                foreach (var child in _inkCanvas.Children.OfType<System.Windows.Shapes.Path>().ToArray())
                {
                    if (!(child.Tag is string tag) || tag != _fillImageTag) continue;
                    if (child.Data == null) continue;
                    double left = InkCanvas.GetLeft(child); double top = InkCanvas.GetTop(child);
                    if (double.IsNaN(left)) left = 0; if (double.IsNaN(top)) top = 0;
                    double ex = canvasPt.X - radiusX - left; double ey = canvasPt.Y - radiusY - top;
                    Geometry eraseGeometry = isCircle ? new EllipseGeometry(new Rect(ex, ey, canvasEraserWidth, canvasEraserHeight)) : new RectangleGeometry(new Rect(ex, ey, canvasEraserWidth, canvasEraserHeight));
                    PathGeometry current = child.Data as PathGeometry ?? PathGeometry.CreateFromGeometry(child.Data);
                    if (current == null) continue;
                    if (!current.Bounds.IntersectsWith(eraseGeometry.Bounds)) continue;
                    var combined = Geometry.Combine(current, eraseGeometry, GeometryCombineMode.Exclude, null);
                    if (combined == null) continue;
                    child.Data = combined.GetFlattenedPathGeometry();
                }
                foreach (var child in _inkCanvas.Children.OfType<WinImage>().ToArray())
                {
                    if (!(child.Tag is string imgTag) || imgTag != _fillImageTag) continue;
                    if (!(child.Source is WriteableBitmap wb)) continue;
                    double left = InkCanvas.GetLeft(child); double top = InkCanvas.GetTop(child);
                    int imgW = wb.PixelWidth; int imgH = wb.PixelHeight;
                    int x0 = (int)Math.Floor(canvasPt.X - radiusX - left); int y0 = (int)Math.Floor(canvasPt.Y - radiusY - top);
                    int x1 = (int)Math.Ceiling(canvasPt.X + radiusX - left); int y1 = (int)Math.Ceiling(canvasPt.Y + radiusY - top);
                    if (x0 < 0) x0 = 0; if (y0 < 0) y0 = 0; if (x1 > imgW) x1 = imgW; if (y1 > imgH) y1 = imgH;
                    if (x0 >= x1 || y0 >= y1) continue;
                    int stride = imgW * 4; byte[] pixels = new byte[stride * imgH];
                    wb.CopyPixels(pixels, stride, 0);
                    double cx = canvasPt.X - left; double cy = canvasPt.Y - top;
                    bool changed = false;
                    if (isCircle)
                    {
                        for (int y = y0; y < y1; y++) { double dy = y - cy; for (int x = x0; x < x1; x++) { double dx = x - cx; if ((dx * dx) / (radiusX * radiusX) + (dy * dy) / (radiusY * radiusY) <= 1.0) { int idx = (y * imgW + x) * 4; if (pixels[idx + 3] != 0) { pixels[idx] = 0; pixels[idx + 1] = 0; pixels[idx + 2] = 0; pixels[idx + 3] = 0; changed = true; } } } }
                    }
                    else
                    {
                        for (int y = y0; y < y1; y++) { for (int x = x0; x < x1; x++) { int idx = (y * imgW + x) * 4; if (pixels[idx + 3] != 0) { pixels[idx] = 0; pixels[idx + 1] = 0; pixels[idx + 2] = 0; pixels[idx + 3] = 0; changed = true; } } }
                    }
                    if (changed) wb.WritePixels(new Int32Rect(0, 0, imgW, imgH), pixels, stride, 0);
                }
            }
            catch { }
        }

        public void Undo()
        {
            if (_editHistory.Count == 0) return;
            var lastAction = _editHistory.Pop();
            var redoAction = new EditAction();
            foreach (var stroke in lastAction.AddedStrokes) { if (_inkCanvas.Strokes.Contains(stroke)) { _inkCanvas.Strokes.Remove(stroke); redoAction.RemovedStrokes.Add(stroke); } }
            foreach (var stroke in lastAction.RemovedStrokes) { if (!_inkCanvas.Strokes.Contains(stroke)) { _inkCanvas.Strokes.Add(stroke); redoAction.AddedStrokes.Add(stroke); } }
            _redoHistory.Push(redoAction);
        }

        public void Redo()
        {
            if (_redoHistory.Count == 0) return;
            var redoAction = _redoHistory.Pop();
            foreach (var stroke in redoAction.RemovedStrokes) { if (!_inkCanvas.Strokes.Contains(stroke)) _inkCanvas.Strokes.Add(stroke); }
            foreach (var stroke in redoAction.AddedStrokes) { if (_inkCanvas.Strokes.Contains(stroke)) _inkCanvas.Strokes.Remove(stroke); }
            _editHistory.Push(redoAction);
        }

        public bool CanRedo => _redoHistory.Count > 0;

        public void SwitchToPhotoStrokes(StrokeCollection strokes)
        {
            // 旧路径：未提供 origin 尺寸和 fill 数据，等价于不做坐标缩放、不还原填充
            // （兼容旧数据/实时模式笔迹）
            SwitchToPhotoStrokes(strokes, 0, 0, null, null);
        }

        /// <summary>
        /// 切换到指定笔迹集合，并按"原始 InkCanvas 尺寸 → 当前 InkCanvas 尺寸"进行坐标缩放。
        /// 用于照片查看路径：解决笔迹在创建时与回看时 InkCanvas 尺寸不一致导致的错位问题。
        /// 当 <paramref name="originInkWidth"/> 或 <paramref name="originInkHeight"/> 为 0（旧照片数据）时，
        /// 保持原行为（不缩放），向后兼容。
        /// 同时按相同 <paramref name="scaleX"/>/<paramref name="scaleY"/> 还原矢量/位图填充。
        /// </summary>
        /// <param name="strokes">要加载的笔迹集合</param>
        /// <param name="originInkWidth">笔迹创建时 InkCanvas 的实际宽度（DIP）</param>
        /// <param name="originInkHeight">笔迹创建时 InkCanvas 的实际高度（DIP）</param>
        /// <param name="fillPaths">关联的矢量填充快照（可选；null 表示无填充需要还原）</param>
        /// <param name="fillImages">关联的位图填充快照（可选）</param>
        public void SwitchToPhotoStrokes(StrokeCollection strokes, double originInkWidth, double originInkHeight,
            List<FillPathRecord> fillPaths = null, List<FillImageRecord> fillImages = null)
        {
            StrokeCollection toLoad = strokes;
            double scaleX = 1.0, scaleY = 1.0;

            if (strokes != null && strokes.Count > 0
                && originInkWidth > 0 && originInkHeight > 0)
            {
                double curW = _inkCanvas.ActualWidth;
                double curH = _inkCanvas.ActualHeight;
                // 仅当当前 InkCanvas 尺寸有效且与 origin 尺寸有显著差异时才缩放
                if (curW > 0 && curH > 0
                    && (Math.Abs(originInkWidth - curW) > 0.5 || Math.Abs(originInkHeight - curH) > 0.5))
                {
                    scaleX = curW / originInkWidth;
                    scaleY = curH / originInkHeight;
                    toLoad = ScaleStrokes(strokes, scaleX, scaleY);
                    Logger.Info("DrawingManager",
                        $"笔迹按 InkCanvas 尺寸变化缩放: origin={originInkWidth}x{originInkHeight}, current={curW}x{curH}, scale={scaleX:F3}x{scaleY:F3}");
                }
            }
            // 即使 strokes 为空（如纯填充照片），也要在 origin 尺寸有效时计算 scale，用于填充还原
            else if ((strokes == null || strokes.Count == 0)
                     && originInkWidth > 0 && originInkHeight > 0)
            {
                double curW = _inkCanvas.ActualWidth;
                double curH = _inkCanvas.ActualHeight;
                if (curW > 0 && curH > 0
                    && (Math.Abs(originInkWidth - curW) > 0.5 || Math.Abs(originInkHeight - curH) > 0.5))
                {
                    scaleX = curW / originInkWidth;
                    scaleY = curH / originInkHeight;
                }
            }

            _inkCanvas.Strokes.StrokesChanged -= Ink_StrokesChanged;
            _inkCanvas.Strokes = toLoad;
            _inkCanvas.Strokes.StrokesChanged += Ink_StrokesChanged;
            ClearFillImages(); _editHistory.Clear(); _redoHistory.Clear();

            // 还原填充：按与 strokes 相同的 scaleX/scaleY 缩放，保证与照片/笔迹对齐
            Logger.Info("DrawingManager", $"SwitchToPhotoStrokes 还原填充: fillPaths={fillPaths?.Count ?? 0}, fillImages={fillImages?.Count ?? 0}, scaleX={scaleX:F3}, scaleY={scaleY:F3}");
            if (fillPaths != null && fillPaths.Count > 0)
            {
                foreach (var fp in fillPaths)
                    AddFillPathGeometry(fp.Geometry, fp.Color, scaleX, scaleY);
            }
            if (fillImages != null && fillImages.Count > 0)
            {
                foreach (var fi in fillImages)
                {
                    if (fi.Bitmap == null) continue;
                    AddFillImage(fi.Bitmap,
                        fi.X * scaleX, fi.Y * scaleY,
                        fi.Width * scaleX, fi.Height * scaleY);
                }
            }
        }

        /// <summary>
        /// 获取 InkCanvas 当前的实际尺寸（DIP）。用于在保存笔迹时记录坐标空间参考。
        /// </summary>
        public System.Windows.Size GetInkCanvasSize()
        {
            return new System.Windows.Size(_inkCanvas.ActualWidth, _inkCanvas.ActualHeight);
        }

        /// <summary>
        /// 按比例缩放笔迹坐标及线宽（用于将笔迹从一个坐标系映射到另一个坐标系）。
        /// 线宽按面积守恒缩放，保持视觉比例。
        /// </summary>
        private static StrokeCollection ScaleStrokes(StrokeCollection strokes, double scaleX, double scaleY)
        {
            var result = new StrokeCollection();
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

        public void HandleMouseWheel(MouseWheelEventArgs e) { if (!CheckEnabled()) return; }
        public void HandleManipulationDelta(ManipulationDeltaEventArgs e) { if (!CheckEnabled()) return; }

        public void HandleMouseDown(MouseButtonEventArgs e)
        {
            if (!CheckEnabled()) return;
            if (CurrentMode == ToolMode.Move) { var p = e.GetPosition(_mainWindow); _lastMousePos = new WinPoint(p.X, p.Y); _isPanning = true; _mainWindow.Cursor = Cursors.Hand; }
        }

        public void HandleMouseMove(System.Windows.Input.MouseEventArgs e)
        {
            if (!CheckEnabled()) return;
            if (_isPanning && CurrentMode == ToolMode.Move && e.LeftButton == MouseButtonState.Pressed) { var pos = e.GetPosition(_mainWindow); _lastMousePos = new WinPoint(pos.X, pos.Y); }
        }

        public void HandleMouseUp(MouseButtonEventArgs e) { if (!CheckEnabled()) return; _isPanning = false; _mainWindow.Cursor = Cursors.Arrow; }

        public void HandleTouchDown(TouchEventArgs e)
        {
            if (!CheckEnabled()) return;
            var touchPoint = e.GetTouchPoint(_videoArea);
            _touchPoints[e.TouchDevice.Id] = touchPoint.Position;
            UpdateTouchCenterAndDistance();
            if (CurrentMode >= ToolMode.Line && CurrentMode <= ToolMode.DotLine && CurrentMode != ToolMode.PaintBucket)
            {
                if (_touchPoints.Count == 1) StartShapeDrawing(touchPoint.Position, _drawingShapeMode);
            }
            if (CurrentMode == ToolMode.Pen && _enablePalmEraser) HandleTouchDownForPalmEraser(e);
        }

        public void HandleTouchMove(TouchEventArgs e)
        {
            if (!CheckEnabled()) return;
            if (_touchPoints.ContainsKey(e.TouchDevice.Id))
            {
                var touchPoint = e.GetTouchPoint(_videoArea);
                _touchPoints[e.TouchDevice.Id] = touchPoint.Position;
                UpdateTouchCenterAndDistance();
                if (_isDrawingShape && _touchPoints.Count == 1) UpdateShapePreview(touchPoint.Position);
                if (_isPalmEraserActive) HandleTouchMoveForPalmEraser(e);
                if (CurrentMode == ToolMode.Move && _touchPoints.Count >= 2) HandleMultiTouchGesture();
            }
        }

        public void HandleTouchUp(TouchEventArgs e)
        {
            if (!CheckEnabled()) return;
            if (_isDrawingShape && _touchPoints.Count == 0) CommitShape();
            if (_touchPoints.ContainsKey(e.TouchDevice.Id)) _touchPoints.Remove(e.TouchDevice.Id);
            if (_isPalmEraserActive) HandleTouchUpForPalmEraser(e);
            UpdateTouchCenterAndDistance();
            if (_touchPoints.Count < 2) _lastTouchDistance = -1;
        }

        private void UpdateTouchCenterAndDistance()
        {
            if (_touchPoints.Count == 0) { _lastTouchCenter = new WinPoint(0, 0); _lastTouchDistance = -1; return; }
            double centerX = 0, centerY = 0;
            foreach (var point in _touchPoints.Values) { centerX += point.X; centerY += point.Y; }
            centerX /= _touchPoints.Count; centerY /= _touchPoints.Count;
            _lastTouchCenter = new WinPoint(centerX, centerY);
            if (_touchPoints.Count == 2) { var points = _touchPoints.Values.ToArray(); double dx = points[1].X - points[0].X; double dy = points[1].Y - points[0].Y; _lastTouchDistance = Math.Sqrt(dx * dx + dy * dy); }
            else _lastTouchDistance = -1;
        }

        private void HandleMultiTouchGesture()
        {
            if (_touchPoints.Count < 2 || _lastTouchDistance <= 0) return;
            var points = _touchPoints.Values.ToArray();
            double dx = points[1].X - points[0].X; double dy = points[1].Y - points[0].Y;
            _lastTouchDistance = Math.Sqrt(dx * dx + dy * dy);
        }

        private void HandleTouchDownForPalmEraser(TouchEventArgs e)
        {
            try
            {
                if (CurrentMode != ToolMode.Pen) return;
                var touchPoint = e.GetTouchPoint(_videoArea);
                var bounds = touchPoint.Bounds;
                _currentTouchArea = bounds.Width * bounds.Height;
                if (_currentTouchArea > _palmEraserThreshold) ActivatePalmEraser(touchPoint);
            }
            catch { }
        }

        private void HandleTouchMoveForPalmEraser(TouchEventArgs e)
        {
            try
            {
                if (!_isPalmEraserActive) return;
                var touchPoint = e.GetTouchPoint(_videoArea);
                var bounds = touchPoint.Bounds;
                _currentTouchArea = bounds.Width * bounds.Height;
                if (_currentTouchArea > _palmEraserThreshold) UpdateEraserSizeBasedOnTouchArea(_currentTouchArea);
                else DeactivatePalmEraser();
            }
            catch { }
        }

        private void HandleTouchUpForPalmEraser(TouchEventArgs e) { try { if (_isPalmEraserActive) DeactivatePalmEraser(); } catch { } }

        private void ActivatePalmEraser(TouchPoint touchPoint)
        {
            if (_isPalmEraserActive) return;
            _lastModeBeforePalmEraser = CurrentMode;
            CurrentMode = ToolMode.Eraser;
            _inkCanvas.EditingMode = InkCanvasEditingMode.EraseByPoint;
            _mainWindow.Cursor = Cursors.Arrow;
            UpdateEraserSizeBasedOnTouchArea(_currentTouchArea);
            _isPalmEraserActive = true;
            OnPalmEraserStateChanged?.Invoke(true);
        }

        private void DeactivatePalmEraser()
        {
            if (!_isPalmEraserActive) return;
            CurrentMode = _lastModeBeforePalmEraser;
            switch (_lastModeBeforePalmEraser)
            {
                case ToolMode.Move: _inkCanvas.EditingMode = InkCanvasEditingMode.None; _mainWindow.Cursor = Cursors.Hand; break;
                case ToolMode.Pen: _inkCanvas.EditingMode = InkCanvasEditingMode.Ink; _mainWindow.Cursor = Cursors.Arrow; break;
                case ToolMode.Eraser: _inkCanvas.EditingMode = InkCanvasEditingMode.EraseByPoint; _mainWindow.Cursor = Cursors.Arrow; _inkCanvas.EraserShape = new RectangleStylusShape(_manualEraserSize, _manualEraserSize); break;
            }
            _isPalmEraserActive = false;
            OnPalmEraserStateChanged?.Invoke(false);
        }

        private void UpdateEraserSizeBasedOnTouchArea(double touchArea)
        {
            try { double newEraserSize = Math.Max(10, Math.Sqrt(touchArea) * 0.05); _inkCanvas.EraserShape = new RectangleStylusShape(newEraserSize, newEraserSize); } catch { }
        }

        public void SetPalmEraserThreshold(double threshold) { PalmEraserThreshold = threshold; }
        public void ForceDeactivatePalmEraser() { DeactivatePalmEraser(); }
        public bool HasStrokes => _inkCanvas.Strokes.Count > 0;
        public StrokeCollection GetStrokes() { return new StrokeCollection(_inkCanvas.Strokes); }

        private Stroke CreateLineStroke(WinPoint start, WinPoint end)
        {
            var points = new List<WinPoint> { start, end };
            var stylusPoints = new StylusPointCollection(points);
            var attributes = _inkCanvas.DefaultDrawingAttributes.Clone();
            attributes.Width = UserPenWidth; attributes.Height = UserPenWidth;
            return new Stroke(stylusPoints) { DrawingAttributes = attributes };
        }

        private Stroke CreateArrowStroke(WinPoint start, WinPoint end)
        {
            double arrowWidth = 15, arrowHeight = 10;
            var theta = Math.Atan2(start.Y - end.Y, start.X - end.X);
            var sin = Math.Sin(theta); var cos = Math.Cos(theta);
            var points = new List<WinPoint> { start, end, new WinPoint(end.X + (arrowWidth * cos - arrowHeight * sin), end.Y + (arrowWidth * sin + arrowHeight * cos)), end, new WinPoint(end.X + (arrowWidth * cos + arrowHeight * sin), end.Y - (arrowHeight * cos - arrowWidth * sin)) };
            var stylusPoints = new StylusPointCollection(points);
            var attributes = _inkCanvas.DefaultDrawingAttributes.Clone();
            attributes.Width = UserPenWidth; attributes.Height = UserPenWidth;
            return new Stroke(stylusPoints) { DrawingAttributes = attributes };
        }

        private Stroke CreateRectangleStroke(WinPoint start, WinPoint end)
        {
            var points = new List<WinPoint> { new WinPoint(start.X, start.Y), new WinPoint(start.X, end.Y), new WinPoint(end.X, end.Y), new WinPoint(end.X, start.Y), new WinPoint(start.X, start.Y) };
            var stylusPoints = new StylusPointCollection(points);
            var attributes = _inkCanvas.DefaultDrawingAttributes.Clone();
            attributes.Width = UserPenWidth; attributes.Height = UserPenWidth;
            return new Stroke(stylusPoints) { DrawingAttributes = attributes };
        }

        private Stroke CreateEllipseStroke(WinPoint start, WinPoint end)
        {
            var points = GenerateEllipseGeometry(start, end);
            var stylusPoints = new StylusPointCollection(points);
            var attributes = _inkCanvas.DefaultDrawingAttributes.Clone();
            attributes.Width = UserPenWidth; attributes.Height = UserPenWidth;
            return new Stroke(stylusPoints) { DrawingAttributes = attributes };
        }

        private Stroke CreateCircleStroke(WinPoint center, WinPoint edge)
        {
            double radius = GetDistance(center, edge);
            var topLeft = new WinPoint(center.X - radius, center.Y - radius);
            var bottomRight = new WinPoint(center.X + radius, center.Y + radius);
            var points = GenerateEllipseGeometry(topLeft, bottomRight);
            var stylusPoints = new StylusPointCollection(points);
            var attributes = _inkCanvas.DefaultDrawingAttributes.Clone();
            attributes.Width = UserPenWidth; attributes.Height = UserPenWidth;
            return new Stroke(stylusPoints) { DrawingAttributes = attributes };
        }

        private StrokeCollection CreateDashedLineStrokeCollection(WinPoint start, WinPoint end)
        {
            var strokes = new StrokeCollection();
            double dashLength = 10; double gapLength = 5; double totalLength = GetDistance(start, end);
            double dx = (end.X - start.X) / totalLength; double dy = (end.Y - start.Y) / totalLength;
            double currentLength = 0;
            while (currentLength < totalLength)
            {
                double dashEnd = Math.Min(currentLength + dashLength, totalLength);
                var dashStart = new WinPoint(start.X + dx * currentLength, start.Y + dy * currentLength);
                var dashEndPoint = new WinPoint(start.X + dx * dashEnd, start.Y + dy * dashEnd);
                var points = new List<WinPoint> { dashStart, dashEndPoint };
                var stylusPoints = new StylusPointCollection(points);
                var attributes = _inkCanvas.DefaultDrawingAttributes.Clone();
                attributes.Width = UserPenWidth; attributes.Height = UserPenWidth;
                strokes.Add(new Stroke(stylusPoints) { DrawingAttributes = attributes });
                currentLength = dashEnd + gapLength;
            }
            return strokes;
        }

        private StrokeCollection CreateDotLineStrokeCollection(WinPoint start, WinPoint end)
        {
            var strokes = new StrokeCollection();
            double dotSpacing = 15; double totalLength = GetDistance(start, end);
            double dx = (end.X - start.X) / totalLength; double dy = (end.Y - start.Y) / totalLength;
            double currentLength = 0;
            while (currentLength < totalLength)
            {
                var dotPoint = new WinPoint(start.X + dx * currentLength, start.Y + dy * currentLength);
                var points = new List<WinPoint> { dotPoint, dotPoint };
                var stylusPoints = new StylusPointCollection(points);
                var attributes = _inkCanvas.DefaultDrawingAttributes.Clone();
                attributes.Width = UserPenWidth; attributes.Height = UserPenWidth;
                strokes.Add(new Stroke(stylusPoints) { DrawingAttributes = attributes });
                currentLength += dotSpacing;
            }
            return strokes;
        }

        private List<WinPoint> GenerateEllipseGeometry(WinPoint topLeft, WinPoint bottomRight)
        {
            var points = new List<WinPoint>();
            double centerX = (topLeft.X + bottomRight.X) / 2; double centerY = (topLeft.Y + bottomRight.Y) / 2;
            double radiusX = Math.Abs(bottomRight.X - topLeft.X) / 2; double radiusY = Math.Abs(bottomRight.Y - topLeft.Y) / 2;
            int segments = 60;
            for (int i = 0; i <= segments; i++) { double angle = 2 * Math.PI * i / segments; points.Add(new WinPoint(centerX + radiusX * Math.Cos(angle), centerY + radiusY * Math.Sin(angle))); }
            return points;
        }

        private double GetDistance(WinPoint p1, WinPoint p2) { return Math.Sqrt(Math.Pow(p2.X - p1.X, 2) + Math.Pow(p2.Y - p1.Y, 2)); }

        public void StartShapeDrawing(WinPoint startPoint, int shapeMode) { _drawingShapeMode = shapeMode; _isDrawingShape = true; _shapeStartPoint = startPoint; _tempStroke = null; _tempStrokeCollection = null; }

        public void UpdateShapePreview(WinPoint endPoint)
        {
            if (!_isDrawingShape) return;
            try
            {
                if (_tempStroke != null) { _inkCanvas.Strokes.Remove(_tempStroke); _tempStroke = null; }
                if (_tempStrokeCollection != null) { _inkCanvas.Strokes.Remove(_tempStrokeCollection); _tempStrokeCollection = null; }
                switch (_drawingShapeMode)
                {
                    case 1: _tempStroke = CreateLineStroke(_shapeStartPoint, endPoint); _inkCanvas.Strokes.Add(_tempStroke); break;
                    case 2: _tempStroke = CreateArrowStroke(_shapeStartPoint, endPoint); _inkCanvas.Strokes.Add(_tempStroke); break;
                    case 3: _tempStroke = CreateRectangleStroke(_shapeStartPoint, endPoint); _inkCanvas.Strokes.Add(_tempStroke); break;
                    case 4: _tempStroke = CreateEllipseStroke(_shapeStartPoint, endPoint); _inkCanvas.Strokes.Add(_tempStroke); break;
                    case 5: _tempStroke = CreateCircleStroke(_shapeStartPoint, endPoint); _inkCanvas.Strokes.Add(_tempStroke); break;
                    case 8: _tempStrokeCollection = CreateDashedLineStrokeCollection(_shapeStartPoint, endPoint); _inkCanvas.Strokes.Add(_tempStrokeCollection); break;
                    case 18: _tempStrokeCollection = CreateDotLineStrokeCollection(_shapeStartPoint, endPoint); _inkCanvas.Strokes.Add(_tempStrokeCollection); break;
                }
            }
            catch { }
        }

        public void CommitShape()
        {
            if (!_isDrawingShape) return;
            try
            {
                StartEdit();
                if (_tempStroke != null)
                {
                    _inkCanvas.Strokes.Remove(_tempStroke);
                    var finalStroke = _tempStroke.Clone();
                    finalStroke.DrawingAttributes.Width = UserPenWidth; finalStroke.DrawingAttributes.Height = UserPenWidth;
                    _inkCanvas.Strokes.Add(finalStroke); _currentEdit?.AddedStrokes.Add(finalStroke); _tempStroke = null;
                }
                if (_tempStrokeCollection != null)
                {
                    _inkCanvas.Strokes.Remove(_tempStrokeCollection);
                    var finalStrokes = new StrokeCollection(_tempStrokeCollection);
                    _inkCanvas.Strokes.Add(finalStrokes);
                    foreach (var stroke in finalStrokes) { stroke.DrawingAttributes.Width = UserPenWidth; stroke.DrawingAttributes.Height = UserPenWidth; _currentEdit?.AddedStrokes.Add(stroke); }
                    _tempStrokeCollection = null;
                }
                EndEdit();
            }
            catch { }
            finally { _isDrawingShape = false; _drawingShapeMode = 0; }
        }

        public void CancelShapeDrawing()
        {
            if (!_isDrawingShape) return;
            try
            {
                if (_tempStroke != null) { _inkCanvas.Strokes.Remove(_tempStroke); _tempStroke = null; }
                if (_tempStrokeCollection != null) { _inkCanvas.Strokes.Remove(_tempStrokeCollection); _tempStrokeCollection = null; }
            }
            catch { }
            finally { _isDrawingShape = false; _drawingShapeMode = 0; }
        }

        public bool IsDrawingShape => _isDrawingShape;

        // =========================
        // 油漆桶工具 (填充封闭图形) - 【已实现：填充被笔触覆盖但不溢出】
        // =========================
        public bool FillClosedShape(WinPoint canvasPoint)
        {
            try
            {
                double canvasWidth = _inkCanvas.ActualWidth;
                double canvasHeight = _inkCanvas.ActualHeight;
                if (canvasWidth <= 0 || canvasHeight <= 0) return false;
                if (_inkCanvas.Strokes.Count == 0) return false;

                // 4x 分辨率，亚像素精度
                const int scale = 4;
                int width = (int)canvasWidth * scale;
                int height = (int)canvasHeight * scale;
                int clickX = (int)Math.Round(canvasPoint.X * scale);
                int clickY = (int)Math.Round(canvasPoint.Y * scale);

                if (clickX < 0 || clickX >= width || clickY < 0 || clickY >= height) return false;

                // 1. 渲染笔画到位图
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0, 0, width, height));
                    foreach (var stroke in _inkCanvas.Strokes) stroke.Draw(dc);
                }
                var rtb = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                rtb.Render(dv);

                int stride = width * 4;
                var pixels = new byte[stride * height];
                rtb.CopyPixels(pixels, stride, 0);

                // 2. 低阈值二值化（阈值30）：
                //    笔画的完整视觉范围（包括抗锯齿）都视为"墙壁"，
                //    确保填充绝对不会溢出到笔画外部。
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    if (pixels[i + 3] > 30)
                    {
                        pixels[i] = 0; pixels[i + 1] = 0; pixels[i + 2] = 0; pixels[i + 3] = 255;
                    }
                    else
                    {
                        pixels[i + 3] = 0;
                    }
                }

                // 3. 泛洪填充（目标色 = 纯透明）
                int clickIdx = (clickY * width + clickX) * 4;
                if (pixels[clickIdx + 3] > 128) return false;

                Color fillColor = _inkCanvas.DefaultDrawingAttributes.Color;
                byte fillB = fillColor.B, fillG = fillColor.G, fillR = fillColor.R;

                ScanlineFloodFill(pixels, width, height, clickX, clickY,
                    0, 0, 0, 0, fillB, fillG, fillR, 255);

                // 4. 【核心】膨胀填充区域，让填充延伸到笔画内部
                //    膨胀量 = 最细笔画宽度 × 40%（在4x分辨率下）
                //    由于 Path 在笔画下方，膨胀进入笔画区域的部分被笔画自然覆盖
                //    膨胀量 < 笔画半宽（50%），所以绝对不会溢出到笔画另一侧
                double minStrokeWidth = _inkCanvas.Strokes.Min(s =>
                    Math.Min(s.DrawingAttributes.Width, s.DrawingAttributes.Height));
                int expandRadius = Math.Max(3, (int)Math.Round(minStrokeWidth * scale * 0.4));
                ExpandFillRegion(pixels, width, height, fillColor, expandRadius);

                // 5. 追踪边界并简化
                var rawPolygons = TraceFillPolygons(pixels, width, height, fillColor);
                if (rawPolygons.Count == 0) return false;

                var simplified = new List<List<WinPoint>>();
                foreach (var poly in rawPolygons)
                {
                    var dipPoly = new List<WinPoint>(poly.Count);
                    for (int i = 0; i < poly.Count; i++)
                        dipPoly.Add(new WinPoint((poly[i].X + 0.5) / scale, (poly[i].Y + 0.5) / scale));
                    var s = SimplifyPolygon(dipPoly, 0.5);
                    if (s.Count >= 3) simplified.Add(s);
                }
                if (simplified.Count == 0) return false;

                var geometry = BuildFillGeometry(simplified);

                // 6. 创建矢量 Path（Insert(0) 确保在笔画下方）
                //    膨胀进入笔画区域的部分被笔画自然覆盖，视觉上完美契合
                var path = new System.Windows.Shapes.Path
                {
                    Data = geometry,
                    Fill = new SolidColorBrush(fillColor),
                    Stroke = null,
                    IsHitTestVisible = false,
                    Tag = _fillImageTag
                };
                InkCanvas.SetLeft(path, 0);
                InkCanvas.SetTop(path, 0);
                _inkCanvas.Children.Insert(0, path);

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"填充封闭图形失败: {ex.Message}");
                return false;
            }
        }

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
                int idx = (y * width + x) * 4;
                while (y >= 0 && pixels[idx] == targetB && pixels[idx + 1] == targetG && pixels[idx + 2] == targetR && pixels[idx + 3] == targetA) { y--; idx -= width * 4; }
                y++; idx += width * 4;
                bool spanLeft = false; bool spanRight = false;
                while (y < height && pixels[idx] == targetB && pixels[idx + 1] == targetG && pixels[idx + 2] == targetR && pixels[idx + 3] == targetA)
                {
                    pixels[idx] = fillB; pixels[idx + 1] = fillG; pixels[idx + 2] = fillR; pixels[idx + 3] = fillA;
                    if (x > 0)
                    {
                        int leftIdx = idx - 4;
                        if (pixels[leftIdx] == targetB && pixels[leftIdx + 1] == targetG && pixels[leftIdx + 2] == targetR && pixels[leftIdx + 3] == targetA)
                        { if (!spanLeft) { stack.Push(y * width + (x - 1)); spanLeft = true; } }
                        else spanLeft = false;
                    }
                    if (x < width - 1)
                    {
                        int rightIdx = idx + 4;
                        if (pixels[rightIdx] == targetB && pixels[rightIdx + 1] == targetG && pixels[rightIdx + 2] == targetR && pixels[rightIdx + 3] == targetA)
                        { if (!spanRight) { stack.Push(y * width + (x + 1)); spanRight = true; } }
                        else spanRight = false;
                    }
                    y++; idx += width * 4;
                }
            }
        }

        /// <summary>
        /// 膨胀填充区域：从填充边界向外扩展 radius 像素（BFS 8连通）。
        /// 不跳过笔画像素，直接覆盖。因为填充 Path 在笔画下方，
        /// 覆盖笔画像素的部分会被笔画自然遮挡，
        /// 从而实现"填充被笔触覆盖一部分但不溢出"的效果。
        /// </summary>
        private static void ExpandFillRegion(byte[] pixels, int w, int h, Color fillColor, int radius)
        {
            if (pixels == null || w <= 0 || h <= 0 || radius <= 0) return;
            byte fB = fillColor.B, fG = fillColor.G, fR = fillColor.R;

            bool IsFill(int idx) =>
                pixels[idx + 3] > 128 &&
                Math.Abs(pixels[idx] - fB) <= 2 &&
                Math.Abs(pixels[idx + 1] - fG) <= 2 &&
                Math.Abs(pixels[idx + 2] - fR) <= 2;

            // 第一步：找到所有填充像素的边界邻居（非填充像素），作为 BFS 起点
            var queue = new Queue<(int x, int y, int d)>();
            var visited = new bool[w * h];
            int[] dx4 = { 1, -1, 0, 0 };
            int[] dy4 = { 0, 0, 1, -1 };

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = (y * w + x) * 4;
                    if (!IsFill(idx)) continue;
                    for (int k = 0; k < 4; k++)
                    {
                        int nx = x + dx4[k], ny = y + dy4[k];
                        if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                        int np = ny * w + nx;
                        if (!visited[np] && !IsFill(np * 4))
                        {
                            visited[np] = true;
                            queue.Enqueue((nx, ny, 1));
                        }
                    }
                }
            }

            // 第二步：BFS 向外扩展 radius 层，直接覆盖所有像素（包括笔画）
            int[] dx8 = { 1, -1, 0, 0, 1, 1, -1, -1 };
            int[] dy8 = { 0, 0, 1, -1, 1, -1, 1, -1 };

            while (queue.Count > 0)
            {
                var (cx, cy, d) = queue.Dequeue();
                if (d > radius) continue;

                // 覆盖为填充色（不区分笔画/背景，因为 Path 在笔画下方会被遮挡）
                int cidx = (cy * w + cx) * 4;
                pixels[cidx] = fB;
                pixels[cidx + 1] = fG;
                pixels[cidx + 2] = fR;
                pixels[cidx + 3] = 255;

                if (d < radius)
                {
                    for (int k = 0; k < 8; k++)
                    {
                        int nx = cx + dx8[k], ny = cy + dy8[k];
                        if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                        int np = ny * w + nx;
                        if (!visited[np])
                        {
                            visited[np] = true;
                            queue.Enqueue((nx, ny, d + 1));
                        }
                    }
                }
            }
        }

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

        private VisualCanvas GetVisualCanvas(int id) { return _visualCanvasList.TryGetValue(id, out var visualCanvas) ? visualCanvas : null; }
        private InkCanvasEditingMode GetTouchDownPointsList(int id) { return _touchDownPointsList.TryGetValue(id, out var mode) ? mode : _inkCanvas.EditingMode; }

        private void CleanupStrokePreview(int id)
        {
            try
            {
                if (_visualCanvasList.TryGetValue(id, out var visualCanvas)) { if (_inkCanvas.Children.Contains(visualCanvas)) _inkCanvas.Children.Remove(visualCanvas); _visualCanvasList.Remove(id); }
                _strokeVisualList.Remove(id); _touchDownPointsList.Remove(id);
            }
            catch { }
        }

        private void CleanupAllStrokePreviews()
        {
            try
            {
                foreach (var canvas in _visualCanvasList.Values.ToList()) { if (_inkCanvas.Children.Contains(canvas)) _inkCanvas.Children.Remove(canvas); }
                _strokeVisualList.Clear(); _visualCanvasList.Clear(); _touchDownPointsList.Clear();
            }
            catch { }
        }

        public void InitializeEraserOverlay(System.Windows.Controls.Canvas eraserOverlayCanvas)
        {
            _eraserOverlayCanvas = eraserOverlayCanvas;
            _eraserFeedback = _mainWindow.FindName("EraserFeedback") as System.Windows.Controls.Image;
            if (_eraserFeedback != null) _eraserFeedbackTranslateTransform = _eraserFeedback.RenderTransform as TranslateTransform;
            eraserOverlayCanvas.StylusDown += (o, args) => { args.Handled = true; if (args.StylusDevice.TabletDevice.Type == TabletDeviceType.Stylus) eraserOverlayCanvas.CaptureStylus(); EraserOverlay_PointerDown(o); };
            eraserOverlayCanvas.StylusUp += (o, args) => { args.Handled = true; if (args.StylusDevice.TabletDevice.Type == TabletDeviceType.Stylus) eraserOverlayCanvas.ReleaseStylusCapture(); EraserOverlay_PointerUp(o); };
            eraserOverlayCanvas.StylusMove += (o, args) => { args.Handled = true; EraserOverlay_PointerMove(o, args.GetPosition(_inkCanvas)); };
            eraserOverlayCanvas.MouseDown += (o, args) => { eraserOverlayCanvas.CaptureMouse(); EraserOverlay_PointerDown(o); };
            eraserOverlayCanvas.MouseUp += (o, args) => { eraserOverlayCanvas.ReleaseMouseCapture(); EraserOverlay_PointerUp(o); };
            eraserOverlayCanvas.MouseMove += (o, args) => { EraserOverlay_PointerMove(o, args.GetPosition(_inkCanvas)); };
            UpdateEraserStyle();
        }

        private void UpdateEraserStyle()
        {
            if (_eraserFeedback == null) return;
            string resourceKey = _isEraserCircleShape ? "EllipseEraserImageSource" : "RectangleEraserImageSource";
            var imageSource = _mainWindow.TryFindResource(resourceKey) as DrawingImage;
            if (imageSource != null) _eraserFeedback.Source = imageSource;
        }

        private void EraserOverlay_PointerDown(object sender)
        {
            if (_isUsingGeometryEraser) return;
            _isUsingGeometryEraser = true;
            var _h = _eraserWidth * 56 / 38;
            double zoom = CurrentZoom > 0.01 ? CurrentZoom : 1.0;
            double canvasEraserWidth = _eraserWidth / zoom;
            double canvasH = _h / zoom;
            _canvasEraserWidth = canvasEraserWidth;
            _canvasEraserHeight = _isEraserCircleShape ? canvasEraserWidth : canvasH;
            StylusShape eraserShape = _isEraserCircleShape ? new EllipseStylusShape(canvasEraserWidth, canvasEraserWidth) : new RectangleStylusShape(canvasEraserWidth, canvasH);
            _hitTester = _inkCanvas.Strokes.GetIncrementalStrokeHitTester(eraserShape);
            _hitTester.StrokeHit += EraserGeometry_StrokeHit;
            var scaleX = _eraserWidth / 38; var scaleY = _h / 56;
            _scaleMatrix = new Matrix(); _scaleMatrix.ScaleAt(scaleX, scaleY, 0, 0);
            if (_eraserFeedback != null)
            {
                _eraserFeedback.Width = Math.Max(_eraserWidth, 10);
                _eraserFeedback.Height = _isEraserCircleShape ? _eraserFeedback.Width : _h;
                _eraserFeedback.Measure(new System.Windows.Size(Double.PositiveInfinity, Double.PositiveInfinity));
                _eraserFeedback.Visibility = Visibility.Collapsed;
            }
        }

        private void EraserOverlay_PointerUp(object sender)
        {
            if (!_isUsingGeometryEraser) return;
            _isUsingGeometryEraser = false;
            ((UIElement)sender).ReleaseMouseCapture();
            if (_eraserFeedback != null) _eraserFeedback.Visibility = Visibility.Collapsed;
            if (_hitTester != null) { _hitTester.EndHitTesting(); _hitTester = null; }
            EndEdit();
        }

        private void EraserOverlay_PointerMove(object sender, WinPoint pt)
        {
            if (!_isUsingGeometryEraser) return;
            if (_isUsingStrokesEraser)
            {
                var _filtered = _inkCanvas.Strokes.HitTest(pt).Where(stroke => !stroke.ContainsPropertyData(_isLockGuid));
                var filtered = _filtered as Stroke[] ?? _filtered.ToArray();
                if (!filtered.Any()) return;
                _inkCanvas.Strokes.Remove(new StrokeCollection(filtered));
                EraseFillImagesAt(pt, _canvasEraserWidth, _canvasEraserHeight, _isEraserCircleShape);
            }
            else
            {
                if (_eraserFeedback != null && _eraserFeedback.Visibility == Visibility.Collapsed) _eraserFeedback.Visibility = Visibility.Visible;
                if (_eraserFeedbackTranslateTransform != null)
                {
                    WinPoint screenPt = CanvasToScreenPoint(pt);
                    _eraserFeedbackTranslateTransform.X = screenPt.X - _eraserFeedback.ActualWidth / 2;
                    _eraserFeedbackTranslateTransform.Y = screenPt.Y - _eraserFeedback.ActualHeight / 2;
                }
                EraseFillImagesAt(pt, _canvasEraserWidth, _canvasEraserHeight, _isEraserCircleShape);
                if (_hitTester != null) _hitTester.AddPoint(pt);
            }
        }

        private void EraserGeometry_StrokeHit(object sender, StrokeHitEventArgs args)
        {
            StrokeCollection eraseResult = args.GetPointEraseResults();
            StrokeCollection strokesToReplace = new StrokeCollection { args.HitStroke };
            var filtered_2replace = strokesToReplace.Where(stroke => !stroke.ContainsPropertyData(_isLockGuid));
            var filtered2Replace = filtered_2replace as Stroke[] ?? filtered_2replace.ToArray();
            if (!filtered2Replace.Any()) return;
            var filtered_result = eraseResult.Where(stroke => !stroke.ContainsPropertyData(_isLockGuid));
            var filteredResult = filtered_result as Stroke[] ?? filtered_result.ToArray();
            if (filteredResult.Any()) _inkCanvas.Strokes.Replace(new StrokeCollection(filtered2Replace), new StrokeCollection(filteredResult));
            else _inkCanvas.Strokes.Remove(new StrokeCollection(filtered2Replace));
        }

        public void EnableEraserOverlay() { if (_eraserOverlayCanvas != null) { _eraserOverlayCanvas.IsHitTestVisible = true; _eraserOverlayCanvas.Visibility = Visibility.Visible; } }
        public void DisableEraserOverlay()
        {
            if (_eraserOverlayCanvas != null) { _eraserOverlayCanvas.IsHitTestVisible = false; _eraserOverlayCanvas.Visibility = Visibility.Collapsed; }
            if (_isUsingGeometryEraser) { _isUsingGeometryEraser = false; if (_hitTester != null) { _hitTester.EndHitTesting(); _hitTester = null; } }
            if (_eraserFeedback != null) _eraserFeedback.Visibility = Visibility.Collapsed;
        }

        public void UpdateEraserSize(int sizeLevel)
        {
            double k = 1.0;
            switch (sizeLevel) { case 0: k = _isEraserCircleShape ? 0.5 : 0.7; break; case 1: k = _isEraserCircleShape ? 0.8 : 0.9; break; case 2: k = 1.0; break; case 3: k = _isEraserCircleShape ? 1.25 : 1.2; break; case 4: k = _isEraserCircleShape ? 1.5 : 1.3; break; }
            _eraserWidth = _isEraserCircleShape ? k * 90 : k * 90 * 0.6;
            UpdateEraserStyle();
        }

        public void ToggleEraserShape() { _isEraserCircleShape = !_isEraserCircleShape; UpdateEraserStyle(); }
        public void ToggleEraserMode() { _isUsingStrokesEraser = !_isUsingStrokesEraser; }

        public void ApplyAdvancedEraserShape()
        {
            try
            {
                UpdateEraserSize(2);
                StylusShape eraserShape = _isEraserCircleShape ? new EllipseStylusShape(_eraserWidth, _eraserWidth) : new RectangleStylusShape(_eraserWidth, _eraserWidth * 56 / 38);
                _inkCanvas.EraserShape = eraserShape;
            }
            catch { }
        }

        private void UpdateTempStrokeSafely(Stroke newStroke)
        {
            var now = DateTime.Now;
            if ((now - _lastUpdateTime).TotalMilliseconds < _updateThrottleMs) return;
            _lastUpdateTime = now;
            try
            {
                _inkCanvas.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        _inkCanvas.Strokes.Add(newStroke);
                        if (_lastTempStroke != null && _inkCanvas.Strokes.Contains(_lastTempStroke)) _inkCanvas.Strokes.Remove(_lastTempStroke);
                        _lastTempStroke = newStroke;
                    }
                    catch { if (_lastTempStroke != null && _inkCanvas.Strokes.Contains(_lastTempStroke)) try { _inkCanvas.Strokes.Remove(_lastTempStroke); } catch { } _lastTempStroke = newStroke; try { _inkCanvas.Strokes.Add(newStroke); } catch { } }
                }), DispatcherPriority.Render);
            }
            catch { }
        }

        private void UpdateTempStrokeCollectionSafely(StrokeCollection newStrokeCollection)
        {
            var now = DateTime.Now;
            if ((now - _lastUpdateTime).TotalMilliseconds < _updateThrottleMs) return;
            _lastUpdateTime = now;
            try
            {
                _inkCanvas.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        _inkCanvas.Strokes.Add(newStrokeCollection);
                        if (_lastTempStrokeCollection != null && _lastTempStrokeCollection.Count > 0) { foreach (var stroke in _lastTempStrokeCollection) { if (_inkCanvas.Strokes.Contains(stroke)) _inkCanvas.Strokes.Remove(stroke); } }
                        _lastTempStrokeCollection = newStrokeCollection;
                    }
                    catch { if (_lastTempStrokeCollection != null && _lastTempStrokeCollection.Count > 0) { foreach (var stroke in _lastTempStrokeCollection) { try { _inkCanvas.Strokes.Remove(stroke); } catch { } } } _lastTempStrokeCollection = newStrokeCollection; try { _inkCanvas.Strokes.Add(newStrokeCollection); } catch { } }
                }), DispatcherPriority.Render);
            }
            catch { }
        }

        private List<WinPoint> GenerateEllipseGeometry(WinPoint st, WinPoint ed, bool isDrawTop = true, bool isDrawBottom = true)
        {
            var a = 0.5 * (ed.X - st.X); var b = 0.5 * (ed.Y - st.Y);
            var pointList = new List<WinPoint>();
            if (isDrawTop && isDrawBottom) { for (double r = 0; r <= 2 * Math.PI; r = r + 0.01) pointList.Add(new WinPoint(0.5 * (st.X + ed.X) + a * Math.Cos(r), 0.5 * (st.Y + ed.Y) + b * Math.Sin(r))); }
            else
            {
                if (isDrawBottom) for (double r = 0; r <= Math.PI; r = r + 0.01) pointList.Add(new WinPoint(0.5 * (st.X + ed.X) + a * Math.Cos(r), 0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
                if (isDrawTop) for (var r = Math.PI; r <= 2 * Math.PI; r = r + 0.01) pointList.Add(new WinPoint(0.5 * (st.X + ed.X) + a * Math.Cos(r), 0.5 * (st.Y + ed.Y) + b * Math.Sin(r)));
            }
            return pointList;
        }

        private StrokeCollection GenerateDashedLineEllipseStrokeCollection(WinPoint st, WinPoint ed, bool isDrawTop = true, bool isDrawBottom = true)
        {
            var a = 0.5 * (ed.X - st.X); var b = 0.5 * (ed.Y - st.Y);
            var step = 0.05; var pointList = new List<WinPoint>(); StylusPointCollection point; Stroke stroke;
            var strokes = new StrokeCollection();
            if (isDrawBottom) for (var i = 0.0; i < 1.0; i += step * 1.66) { pointList = new List<WinPoint>(); for (var r = Math.PI * i; r <= Math.PI * (i + step); r = r + 0.01) pointList.Add(new WinPoint(0.5 * (st.X + ed.X) + a * Math.Cos(r), 0.5 * (st.Y + ed.Y) + b * Math.Sin(r))); point = new StylusPointCollection(pointList); stroke = new Stroke(point) { DrawingAttributes = _inkCanvas.DefaultDrawingAttributes.Clone() }; strokes.Add(stroke.Clone()); }
            if (isDrawTop) for (var i = 1.0; i < 2.0; i += step * 1.66) { pointList = new List<WinPoint>(); for (var r = Math.PI * i; r <= Math.PI * (i + step); r = r + 0.01) pointList.Add(new WinPoint(0.5 * (st.X + ed.X) + a * Math.Cos(r), 0.5 * (st.Y + ed.Y) + b * Math.Sin(r))); point = new StylusPointCollection(pointList); stroke = new Stroke(point) { DrawingAttributes = _inkCanvas.DefaultDrawingAttributes.Clone() }; strokes.Add(stroke.Clone()); }
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
            if (_overlayInkCanvas != null) _overlayInkCanvas.StrokeCollected -= OverlayInk_StrokeCollected;
            _editHistory.Clear(); _redoHistory.Clear(); _touchPoints.Clear(); _touchDeviceIds.Clear();
            CleanupAllStrokePreviews(); ResetMultiTouchState(); CleanupTouchSDK();
        }

        // =========================
        // 矢量填充辅助（油漆桶填充运行时矢量化）
        // =========================
        private static List<List<WinPoint>> TraceFillPolygons(byte[] pixels, int w, int h, Color? targetColor = null)
        {
            var polygons = new List<List<WinPoint>>();
            if (pixels == null || w <= 0 || h <= 0) return polygons;
            byte? tB = targetColor?.B, tG = targetColor?.G, tR = targetColor?.R;
            bool IsFilled(int x, int y)
            {
                if (x < 0 || x >= w || y < 0 || y >= h) return false;
                int idx = (y * w + x) * 4;
                if (pixels[idx + 3] <= 128) return false;
                if (tB.HasValue) return Math.Abs(pixels[idx] - tB.Value) <= 2 && Math.Abs(pixels[idx + 1] - tG!.Value) <= 2 && Math.Abs(pixels[idx + 2] - tR!.Value) <= 2;
                return true;
            }
            var edgeMap = new Dictionary<(int, int), List<(int, int)>>();
            void AddEdge(int sx, int sy, int ex, int ey) { var key = (sx, sy); if (!edgeMap.TryGetValue(key, out var list)) { list = new List<(int, int)>(); edgeMap[key] = list; } list.Add((ex, ey)); }
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (!IsFilled(x, y)) continue;
                    if (!IsFilled(x, y - 1)) AddEdge(x, y, x + 1, y);
                    if (!IsFilled(x + 1, y)) AddEdge(x + 1, y, x + 1, y + 1);
                    if (!IsFilled(x, y + 1)) AddEdge(x + 1, y + 1, x, y + 1);
                    if (!IsFilled(x - 1, y)) AddEdge(x, y + 1, x, y);
                }
            }
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
                    if (!edgeMap.TryGetValue(current, out var candidates) || candidates.Count == 0) break;
                    var next = candidates[0];
                    candidates.RemoveAt(0);
                    if (candidates.Count == 0) edgeMap.Remove(current);
                    current = next;
                }
                if (polygon.Count >= 3) polygons.Add(polygon);
            }
            return polygons;
        }

        private static List<WinPoint> SimplifyPolygon(List<WinPoint> points, double tolerance)
        {
            if (points == null || points.Count < 3) return points;
            int n = points.Count;
            var keep = new bool[n];
            keep[0] = keep[n - 1] = true;
            void SimplifyRange(int s, int e)
            {
                if (e <= s + 1) return;
                double maxDist = 0; int maxIdx = -1;
                var p1 = points[s]; var p2 = points[e];
                for (int i = s + 1; i < e; i++)
                {
                    var p = points[i];
                    double dx = p2.X - p1.X; double dy = p2.Y - p1.Y;
                    double len2 = dx * dx + dy * dy; double dist;
                    if (len2 < 1e-12) dist = Math.Sqrt((p.X - p1.X) * (p.X - p1.X) + (p.Y - p1.Y) * (p.Y - p1.Y));
                    else dist = Math.Abs(((p.X - p1.X) * dy - (p.Y - p1.Y) * dx)) / Math.Sqrt(len2);
                    if (dist > maxDist) { maxDist = dist; maxIdx = i; }
                }
                if (maxIdx >= 0 && maxDist > tolerance) { keep[maxIdx] = true; SimplifyRange(s, maxIdx); SimplifyRange(maxIdx, e); }
            }
            SimplifyRange(0, n - 1);
            var result = new List<WinPoint>();
            for (int i = 0; i < n; i++) if (keep[i]) result.Add(points[i]);
            return result;
        }

        /// <summary>
        /// 从多边形列表构建闭合 StreamGeometry。
        /// 使用 Uniform Cubic B-Spline 转三次贝塞尔曲线，
        /// 生成极其平滑的轮廓，完美贴合 Stroke 的渲染质感。
        /// </summary>
        private static StreamGeometry BuildFillGeometry(List<List<WinPoint>> polygons)
        {
            var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
            using (var ctx = geometry.Open())
            {
                foreach (var poly in polygons)
                {
                    int n = poly.Count;
                    if (n < 3) continue;

                    // 计算 B-Spline 闭合曲线的真实起点
                    var pPrevStart = poly[n - 1];
                    var p0Start = poly[0];
                    var p1Start = poly[1];
                    var startPoint = new WinPoint(
                        (pPrevStart.X + 4 * p0Start.X + p1Start.X) / 6.0,
                        (pPrevStart.Y + 4 * p0Start.Y + p1Start.Y) / 6.0);

                    ctx.BeginFigure(startPoint, true, true);

                    // 遍历生成每一段 B-Spline → Bezier 曲线
                    for (int i = 0; i < n; i++)
                    {
                        var pPrev = poly[(i - 1 + n) % n];
                        var pCurr = poly[i];
                        var pNext = poly[(i + 1) % n];
                        var pNext2 = poly[(i + 2) % n];

                        var c1 = new WinPoint(
                            (2 * pCurr.X + pNext.X) / 3.0,
                            (2 * pCurr.Y + pNext.Y) / 3.0);
                        var c2 = new WinPoint(
                            (pCurr.X + 2 * pNext.X) / 3.0,
                            (pCurr.Y + 2 * pNext.Y) / 3.0);
                        var endPoint = new WinPoint(
                            (pCurr.X + 4 * pNext.X + pNext2.X) / 6.0,
                            (pCurr.Y + 4 * pNext.Y + pNext2.Y) / 6.0);

                        ctx.BezierTo(c1, c2, endPoint, true, true);
                    }
                }
            }
            geometry.Freeze();
            return geometry;
        }

        public List<System.Windows.Shapes.Path> GetFillPaths()
        {
            var result = new List<System.Windows.Shapes.Path>();
            try { foreach (var child in _inkCanvas.Children) if (child is System.Windows.Shapes.Path p && p.Tag is string tag && tag == _fillImageTag) result.Add(p); } catch { }
            return result;
        }

        public void AddFillPath(string pathData, System.Windows.Media.Color color, double scaleX = 1.0, double scaleY = 1.0)
        {
            try
            {
                if (string.IsNullOrEmpty(pathData)) return;
                var geometry = System.Windows.Media.Geometry.Parse(pathData);
                if (geometry == null) return;
                System.Windows.Media.Geometry final;
                if (Math.Abs(scaleX - 1.0) > 1e-6 || Math.Abs(scaleY - 1.0) > 1e-6)
                {
                    var srcPg = geometry as System.Windows.Media.PathGeometry ?? System.Windows.Media.PathGeometry.CreateFromGeometry(geometry);
                    if (srcPg == null) return;
                    var matrix = new System.Windows.Media.Matrix(); matrix.Scale(scaleX, scaleY);
                    var transformed = new System.Windows.Media.PathGeometry(srcPg.Figures, srcPg.FillRule, new System.Windows.Media.MatrixTransform(matrix));
                    final = transformed.GetFlattenedPathGeometry();
                }
                else final = geometry;
                var path = new System.Windows.Shapes.Path { Data = final, Fill = new SolidColorBrush(color), Stroke = null, IsHitTestVisible = false, Tag = _fillImageTag };
                InkCanvas.SetLeft(path, 0); InkCanvas.SetTop(path, 0);
                _inkCanvas.Children.Insert(0, path);
            }
            catch { }
        }
    }
}