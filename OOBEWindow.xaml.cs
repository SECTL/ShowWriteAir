using ShowWriteAir.Services;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Diagnostics;

namespace ShowWriteAir
{
    /// <summary>
    /// OOBE（开箱体验）窗口 - 初始配置向导
    /// </summary>
    public partial class OOBEWindow : Window
    {
        private readonly VideoService _videoService = new();
        private List<string> _cameras = new();
        private bool _isCameraSwitching = false;
        private bool _isClosing = false;
        private bool _isTransitioning = false;

        // OOBE 期间已选设置（待保存到 config）
        private int _selectedCameraIndex = 0;
        private string _selectedTheme = "Light"; // "Light" 或 "Dark"

        // 配置文件路径
        private static readonly string ConfigPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        // 标题颜色
        private static readonly SolidColorBrush ActiveTitleBrush =
            new(System.Windows.Media.Color.FromRgb(0x1F, 0x1F, 0x1F));
        private static readonly SolidColorBrush InactiveTitleBrush =
            new(System.Windows.Media.Color.FromRgb(0x9A, 0x9A, 0x9A));

        // 主题卡片边框颜色
        private static readonly SolidColorBrush SelectedCardBorder =
            new(System.Windows.Media.Color.FromRgb(0x29, 0x80, 0xB9));
        private static readonly SolidColorBrush UnselectedCardBorder =
            new(System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC));

        // 过渡动画参数
        private static readonly TimeSpan TransitionDuration = TimeSpan.FromSeconds(0.35);
        private const double TransitionOffset = 60;

        public OOBEWindow()
        {
            InitializeComponent();
            Logger.Info("OOBE", "OOBE 窗口已创建（首页）");
        }

        /// <summary>
        /// 启动按钮点击事件：从首页切换到引导页，并触发标题滑入动画
        /// </summary>
        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            Logger.Info("OOBE", "用户点击了启动按钮，进入引导页");

            // 切换页面
            Page1.Visibility = Visibility.Collapsed;
            Page2.Visibility = Visibility.Visible;

            // 启动四个标题从右至左依次滑入的动画
            var slideInStoryboard = (Storyboard)FindResource("TitlesSlideInStoryboard");
            slideInStoryboard.Begin();

            // 标题滑入总时长约 1.1s（最后一个标题 BeginTime 0.6s + Duration 0.5s）
            // 动画完成后淡入显示内容区域
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.1) };
            timer.Tick += (s, args) =>
            {
                timer.Stop();
                var contentFadeIn = (Storyboard)FindResource("ContentFadeInStoryboard");
                contentFadeIn.Begin();
            };
            timer.Start();
        }

        /// <summary>
        /// 同意条款复选框状态变化：控制下一步按钮是否可用
        /// </summary>
        private void AgreeCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            NextButton.IsEnabled = AgreeCheckBox.IsChecked == true;
        }

        /// <summary>
        /// 欢迎页下一步：过渡到基础设置页（向前：当前页上滑出 + 新页从下滑入）
        /// </summary>
        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isTransitioning) return;
            Logger.Info("OOBE", "用户同意服务条款，进入基础设置");

            TransitionToPage(BasicContent, WelcomeContent, forward: true, () =>
            {
                // 迁移标题高亮：欢迎(灰) → 基础(深)
                Title1.Foreground = InactiveTitleBrush;
                Title2.Foreground = ActiveTitleBrush;

                // 动画完成后初始化摄像头列表并启动预览
                InitializeCameraPreview();
            });
        }

        /// <summary>
        /// 基础设置上一步：过渡回欢迎页（向后：当前页向下滑出 + 上一页从上滑入）
        /// </summary>
        private void BasicBackButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isTransitioning) return;
            Logger.Info("OOBE", "用户点击上一步，返回服务条款页");

            // 先停止摄像头预览
            StopCameraPreview();

            TransitionToPage(WelcomeContent, BasicContent, forward: false, () =>
            {
                // 迁移标题高亮：基础(灰) → 欢迎(深)
                Title2.Foreground = InactiveTitleBrush;
                Title1.Foreground = ActiveTitleBrush;
            });
        }

        /// <summary>
        /// 通用页面过渡动画
        /// </summary>
        /// <param name="toPage">即将进入的页面</param>
        /// <param name="fromPage">即将离开的页面</param>
        /// <param name="forward">true=向前（下一页），false=向后（上一页）</param>
        /// <param name="onCompleted">动画完成后回调</param>
        private void TransitionToPage(FrameworkElement toPage, FrameworkElement fromPage, bool forward, Action onCompleted)
        {
            _isTransitioning = true;

            // 获取两个页面的 TranslateTransform
            var fromTransform = (TranslateTransform)fromPage.RenderTransform;
            var toTransform = (TranslateTransform)toPage.RenderTransform;

            // 显示目标页（初始位于滑入起点）
            toPage.Visibility = Visibility.Visible;
            toTransform.Y = forward ? TransitionOffset : -TransitionOffset;
            toPage.Opacity = 0;

            var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };

            // 当前页：向前时向上滑出(Y→-offset)，向后时向下滑出(Y→+offset)
            var fromAnimY = new DoubleAnimation
            {
                From = 0,
                To = forward ? -TransitionOffset : TransitionOffset,
                Duration = TransitionDuration,
                EasingFunction = ease
            };
            var fromAnimOpacity = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = TransitionDuration
            };

            // 目标页：从下方/上方滑入到 Y=0
            var toAnimY = new DoubleAnimation
            {
                From = forward ? TransitionOffset : -TransitionOffset,
                To = 0,
                Duration = TransitionDuration,
                EasingFunction = ease
            };
            var toAnimOpacity = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TransitionDuration
            };

            // 动画完成后清理
            toAnimOpacity.Completed += (s, e) =>
            {
                fromPage.Visibility = Visibility.Collapsed;
                fromTransform.Y = 0;
                fromPage.Opacity = 1;
                _isTransitioning = false;
                onCompleted?.Invoke();
            };

            // 启动动画
            fromTransform.BeginAnimation(TranslateTransform.YProperty, fromAnimY);
            fromPage.BeginAnimation(OpacityProperty, fromAnimOpacity);
            toTransform.BeginAnimation(TranslateTransform.YProperty, toAnimY);
            toPage.BeginAnimation(OpacityProperty, toAnimOpacity);
        }

        /// <summary>
        /// 初始化摄像头列表并启动预览
        /// </summary>
        private void InitializeCameraPreview()
        {
            try
            {
                // 获取可用摄像头列表
                _cameras = _videoService.GetAvailableCameras();
                CameraComboBox.Items.Clear();

                if (_cameras.Count == 0)
                {
                    CameraPlaceholder.Text = "未检测到摄像头";
                    CameraPlaceholder.Visibility = Visibility.Visible;
                    Logger.Warning("OOBE", "未检测到可用摄像头");
                    return;
                }

                // 填充下拉框
                foreach (var camera in _cameras)
                {
                    CameraComboBox.Items.Add(camera);
                }
                CameraComboBox.SelectedIndex = 0;

                // 订阅帧事件并启动摄像头
                _videoService.OnNewFrameProcessed += OnCameraFrameReceived;
                StartCameraPreview(0);
            }
            catch (Exception ex)
            {
                Logger.Error("OOBE", $"初始化摄像头预览失败: {ex.Message}", ex);
                CameraPlaceholder.Text = "摄像头初始化失败";
                CameraPlaceholder.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// 停止摄像头预览
        /// </summary>
        private void StopCameraPreview()
        {
            try
            {
                _videoService.OnNewFrameProcessed -= OnCameraFrameReceived;
                _videoService.Stop();
                CameraPreview.Source = null;
                Logger.Info("OOBE", "摄像头预览已停止");
            }
            catch (Exception ex)
            {
                Logger.Error("OOBE", $"停止摄像头预览失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 启动指定摄像头的预览
        /// </summary>
        private void StartCameraPreview(int cameraIndex)
        {
            if (_isClosing) return;

            try
            {
                CameraPlaceholder.Visibility = Visibility.Collapsed;
                bool success = _videoService.Start(cameraIndex);
                if (!success)
                {
                    CameraPlaceholder.Text = "摄像头启动失败";
                    CameraPlaceholder.Visibility = Visibility.Visible;
                    Logger.Error("OOBE", $"摄像头启动失败，索引: {cameraIndex}");
                }
                else
                {
                    Logger.Info("OOBE", $"摄像头预览已启动，索引: {cameraIndex}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("OOBE", $"启动摄像头预览异常: {ex.Message}", ex);
                CameraPlaceholder.Text = "摄像头启动失败";
                CameraPlaceholder.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// 接收摄像头帧并显示到预览框
        /// </summary>
        private void OnCameraFrameReceived(System.Drawing.Bitmap frame)
        {
            if (_isClosing || frame == null)
            {
                frame?.Dispose();
                return;
            }

            Dispatcher.Invoke(() =>
            {
                if (_isClosing)
                {
                    frame.Dispose();
                    return;
                }

                try
                {
                    // 转换 Bitmap → BitmapImage
                    using var memory = new System.IO.MemoryStream();
                    frame.Save(memory, System.Drawing.Imaging.ImageFormat.Bmp);
                    memory.Position = 0;

                    var bmpImage = new BitmapImage();
                    bmpImage.BeginInit();
                    bmpImage.CacheOption = BitmapCacheOption.OnLoad;
                    bmpImage.StreamSource = memory;
                    bmpImage.EndInit();
                    bmpImage.Freeze();

                    CameraPreview.Source = bmpImage;
                    CameraPlaceholder.Visibility = Visibility.Collapsed;
                }
                catch (Exception ex)
                {
                    Logger.Error("OOBE", $"预览帧处理失败: {ex.Message}", ex);
                }
                finally
                {
                    frame.Dispose();
                }
            });
        }

        /// <summary>
        /// 摄像头下拉框选择变化：切换预览
        /// </summary>
        private void CameraComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isCameraSwitching || CameraComboBox.SelectedIndex < 0)
                return;

            try
            {
                _isCameraSwitching = true;
                int newIndex = CameraComboBox.SelectedIndex;
                Logger.Info("OOBE", $"用户切换摄像头到索引: {newIndex}");

                // 停止当前摄像头并启动新摄像头
                _videoService.Stop();
                StartCameraPreview(newIndex);
            }
            catch (Exception ex)
            {
                Logger.Error("OOBE", $"切换摄像头失败: {ex.Message}", ex);
            }
            finally
            {
                _isCameraSwitching = false;
            }
        }

        /// <summary>
        /// 基础设置下一步：过渡到个性化页（停止摄像头 + 保存默认摄像头）
        /// </summary>
        private void BasicNextButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isTransitioning) return;
            Logger.Info("OOBE", "用户完成基础设置，点击下一步，进入个性化");

            // 记录选择的摄像头索引
            _selectedCameraIndex = CameraComboBox.SelectedIndex >= 0 ? CameraComboBox.SelectedIndex : 0;

            // 保存默认摄像头到 config
            SaveCameraIndexToConfig(_selectedCameraIndex);

            // 停止摄像头预览
            StopCameraPreview();

            TransitionToPage(PersonalizeContent, BasicContent, forward: true, () =>
            {
                // 迁移标题高亮：基础(灰) → 个性化(深)
                Title2.Foreground = InactiveTitleBrush;
                Title3.Foreground = ActiveTitleBrush;
            });
        }

        /// <summary>
        /// 个性化页上一步：过渡回基础设置页（重启摄像头预览）
        /// </summary>
        private void PersonalizeBackButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isTransitioning) return;
            Logger.Info("OOBE", "用户在个性化页点击上一步，返回基础设置");

            TransitionToPage(BasicContent, PersonalizeContent, forward: false, () =>
            {
                // 迁移标题高亮：个性化(灰) → 基础(深)
                Title3.Foreground = InactiveTitleBrush;
                Title2.Foreground = ActiveTitleBrush;

                // 重新启动摄像头预览
                InitializeCameraPreview();
            });
        }

        /// <summary>
        /// 个性化页下一步：保存主题到 config，并过渡到完成页
        /// </summary>
        private void PersonalizeNextButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isTransitioning) return;
            Logger.Info("OOBE", $"用户完成个性化设置，点击下一步，所选主题: {_selectedTheme}");

            // 保存主题到 config
            SaveThemeToConfig(_selectedTheme);

            TransitionToPage(CompleteContent, PersonalizeContent, forward: true, () =>
            {
                // 迁移标题高亮：个性化(灰) → 开始(深)
                Title3.Foreground = InactiveTitleBrush;
                Title4.Foreground = ActiveTitleBrush;
            });
        }

        /// <summary>
        /// 完成页上一步：过渡回个性化页
        /// </summary>
        private void CompleteBackButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isTransitioning) return;
            Logger.Info("OOBE", "用户在完成页点击上一步，返回个性化");

            TransitionToPage(PersonalizeContent, CompleteContent, forward: false, () =>
            {
                // 迁移标题高亮：开始(灰) → 个性化(深)
                Title4.Foreground = InactiveTitleBrush;
                Title3.Foreground = ActiveTitleBrush;
            });
        }

        /// <summary>
        /// 开始使用按钮：写入首次启动标记为完成并重启应用
        /// </summary>
        private void StartUsingButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isTransitioning) return;
            Logger.Info("OOBE", "用户点击开始使用，OOBE 配置完成，写入首次启动标记并重启应用");
            // 将首次启动标记写入 config
            SaveFirstRunFlag(false);
            // 重启应用
            string exePath = Process.GetCurrentProcess().MainModule.FileName;
            Process.Start(exePath);
            // 关闭当前 OOBE 窗口并退出
            this.Close();
        }

        /// <summary>
        /// 浅色主题卡片点击
        /// </summary>
        private void LightThemeCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            SelectTheme("Light");
        }

        /// <summary>
        /// 深色主题卡片点击
        /// </summary>
        private void DarkThemeCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            SelectTheme("Dark");
        }

        /// <summary>
        /// 选择主题并更新卡片视觉状态
        /// </summary>
        private void SelectTheme(string theme)
        {
            _selectedTheme = theme;
            if (theme == "Light")
            {
                LightThemeCard.BorderBrush = SelectedCardBorder;
                DarkThemeCard.BorderBrush = UnselectedCardBorder;
            }
            else
            {
                LightThemeCard.BorderBrush = UnselectedCardBorder;
                DarkThemeCard.BorderBrush = SelectedCardBorder;
            }
            Logger.Info("OOBE", $"用户选择主题: {theme}");
        }

        #region 配置读写

        /// <summary>
        /// 读取现有 config.json（不存在则返回默认 AppConfig）
        /// </summary>
        private static Models.AppConfig LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath, System.Text.Encoding.UTF8);
                    var cfg = Newtonsoft.Json.JsonConvert.DeserializeObject<Models.AppConfig>(json);
                    if (cfg != null) return cfg;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("OOBE", $"读取配置失败: {ex.Message}", ex);
            }
            return new Models.AppConfig();
        }

        /// <summary>
        /// 保存默认摄像头索引到 config（保留其它字段）
        /// </summary>
        private static void SaveCameraIndexToConfig(int cameraIndex)
        {
            try
            {
                var cfg = LoadConfig();
                cfg.CameraIndex = cameraIndex;
                WriteConfig(cfg);
                Logger.Info("OOBE", $"默认摄像头索引已保存: {cameraIndex}");
            }
            catch (Exception ex)
            {
                Logger.Error("OOBE", $"保存摄像头索引失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 保存主题到 config（保留其它字段）
        /// </summary>

        /// <summary>
        /// 保存首次启动标记到 config（保留其它字段）
        /// </summary>
        private static void SaveFirstRunFlag(bool isFirstRun)
        {
            try
            {
                var cfg = LoadConfig();
                cfg.IsFirstRun = isFirstRun;
                WriteConfig(cfg);
                Logger.Info("OOBE", $"首次启动标记已保存: {isFirstRun}");
            }
            catch (Exception ex)
            {
                Logger.Error("OOBE", $"保存首次启动标记失败: {ex.Message}", ex);
            }
        }

        private static void SaveThemeToConfig(string theme)
        {
            try
            {
                var cfg = LoadConfig();
                cfg.Theme = theme;
                WriteConfig(cfg);
                Logger.Info("OOBE", $"主题已保存: {theme}");
            }
            catch (Exception ex)
            {
                Logger.Error("OOBE", $"保存主题失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 写入 config.json
        /// </summary>
        private static void WriteConfig(Models.AppConfig cfg)
        {
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(cfg, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(ConfigPath, json, System.Text.Encoding.UTF8);
        }

        #endregion

        /// <summary>
        /// 窗口关闭时释放摄像头资源
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            _isClosing = true;
            try
            {
                _videoService.OnNewFrameProcessed -= OnCameraFrameReceived;
                _videoService.Dispose();
                Logger.Info("OOBE", "摄像头资源已释放");
            }
            catch (Exception ex)
            {
                Logger.Error("OOBE", $"释放摄像头资源失败: {ex.Message}", ex);
            }
            base.OnClosed(e);
        }
    }
}
