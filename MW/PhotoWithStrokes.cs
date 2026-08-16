using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ShowWrite
{
    /// <summary>
    /// 照片来源类型
    /// </summary>
    public enum PhotoSource
    {
        /// <summary>
        /// 本地拍照或本地导入（本机产生，可同步到手机客户端）
        /// </summary>
        Local,

        /// <summary>
        /// 来自手机客户端（PHOTO_BIN/SWEC_PHOTO_PUSH source=client 接收，不再同步回手机以避免循环）
        /// </summary>
        FromClient,

        /// <summary>
        /// 来自服务端（预留：手机端收到的 PC 推送图片，PC 端不会出现此来源）
        /// </summary>
        FromServer
    }

    /// <summary>
    /// 支持笔迹的照片包装类
    /// </summary>
    public class PhotoWithStrokes : INotifyPropertyChanged
    {
        public ShowWrite.Models.CapturedImage CapturedImage { get; set; }
        public StrokeCollection Strokes { get; set; }

        public BitmapSource Image { get; set; }
        public BitmapSource Thumbnail { get; set; }
        public string Timestamp { get; set; }
        public int Index { get; set; }
        public string FilePath { get; set; }

        /// <summary>
        /// 照片唯一标识（UUID），用于设备间同步时标号
        /// </summary>
        public string PhotoId { get; set; }

        private PhotoSource _source = PhotoSource.Local;
        /// <summary>
        /// 照片来源：本地 / 来自客户端 / 来自服务端
        /// </summary>
        public PhotoSource Source
        {
            get => _source;
            set
            {
                if (_source != value)
                {
                    _source = value;
                    OnPropertyChanged(nameof(Source));
                }
            }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public PhotoWithStrokes(ShowWrite.Models.CapturedImage capturedImage)
        {
            CapturedImage = capturedImage;
            Strokes = new StrokeCollection();
            Timestamp = DateTime.Now.ToString("MM-dd HH:mm:ss");
            PhotoId = Guid.NewGuid().ToString("N");

            // 设置图像
            Image = capturedImage.Image;

            // 创建缩略图
            Thumbnail = CreateThumbnail(capturedImage.Image, 120, 90);
        }

        /// <summary>
        /// 创建缩略图
        /// </summary>
        private BitmapSource CreateThumbnail(BitmapSource source, int width, int height)
        {
            try
            {
                // 增加空值检查
                if (source == null)
                {
                    Logger.Warning("PhotoWithStrokes", "无法创建缩略图：source 为 null");
                    // 返回一个默认的空白图像
                    return CreateDefaultThumbnail(width, height);
                }

                var scaleX = (double)width / source.PixelWidth;
                var scaleY = (double)height / source.PixelHeight;
                var scale = Math.Min(scaleX, scaleY);
                var scaledWidth = (int)(source.PixelWidth * scale);
                var scaledHeight = (int)(source.PixelHeight * scale);
                var thumbnail = new TransformedBitmap(source,
                    new ScaleTransform(scale, scale));
                var result = new CroppedBitmap(thumbnail,
                    new Int32Rect((thumbnail.PixelWidth - scaledWidth) / 2,
                                 (thumbnail.PixelHeight - scaledHeight) / 2,
                                 scaledWidth, scaledHeight));
                result.Freeze();
                return result;
            }
            catch (Exception ex)
            {
                Logger.Error("PhotoWithStrokes", $"创建缩略图失败: {ex.Message}", ex);
                return CreateDefaultThumbnail(width, height);
            }
        }

        private BitmapSource CreateDefaultThumbnail(int width, int height)
        {
            // 创建一个默认的占位图
            var renderTarget = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            DrawingVisual drawingVisual = new DrawingVisual();

            using (DrawingContext drawingContext = drawingVisual.RenderOpen())
            {
                drawingContext.DrawRectangle(System.Windows.Media.Brushes.DarkGray, null, new Rect(0, 0, width, height));
                drawingContext.DrawText(
                    new FormattedText("无图像",
                        CultureInfo.CurrentCulture,
                        System.Windows.FlowDirection.LeftToRight,
                        new Typeface("Arial"),
                        12,
                        System.Windows.Media.Brushes.White,
                        VisualTreeHelper.GetDpi(System.Windows.Application.Current.MainWindow).PixelsPerDip),
                    new System.Windows.Point(5, height / 2 - 6));
            }

            renderTarget.Render(drawingVisual);
            renderTarget.Freeze();
            return renderTarget;
        }
    }
}