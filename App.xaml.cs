using ShowWriteAir.Services;
using System.Threading;
using System.Windows;

namespace ShowWriteAir
{
    public partial class App : System.Windows.Application
    {
        public static SplashWindow SplashWindow;
        private static Thread _splashThread;
        private MainWindow _mainWindow;
        private OOBEWindow _oobeWindow;

        // 是否首次启动（运行时从 config.json 读取 IsFirstRun）
        private bool _isFirstRun = true;

        /// <summary>
        /// 从 config.json 读取是否首次启动
        /// </summary>
        private bool LoadFirstRunFlag()
        {
            try
            {
                var configPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                if (System.IO.File.Exists(configPath))
                {
                    var json = System.IO.File.ReadAllText(configPath, System.Text.Encoding.UTF8);
                    var config = Newtonsoft.Json.JsonConvert.DeserializeObject<Models.AppConfig>(json);
                    if (config != null)
                    {
                        return config.IsFirstRun;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("App", $"读取首次启动标记失败: {ex.Message}", ex);
            }
            return true;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                Logger.Info("App", "应用程序启动开始");

                LoadLanguageSettings();

                // 读取是否首次启动标记
                _isFirstRun = LoadFirstRunFlag();

                // OOBE 阶段（首次启动）：主程序不运行
                if (_isFirstRun)
                {
                    Logger.Info("App", "首次启动，进入 OOBE 阶段，主程序暂不运行");
                    _oobeWindow = new OOBEWindow();
                    _oobeWindow.Show();
                    Logger.Info("App", "OOBE 窗口已显示");
                    return;
                }

                _splashThread = new Thread(() =>
                {
                    SplashWindow = new SplashWindow();
                    SplashWindow.Show();
                    System.Windows.Threading.Dispatcher.Run();
                });
                _splashThread.SetApartmentState(ApartmentState.STA);
                _splashThread.IsBackground = true;
                _splashThread.Start();

                System.Threading.Thread.Sleep(100);

                // 检测是否通过文件关联打开
                bool isOpenedFromFile = IsOpenedFromFile(e.Args);

                CreateMainWindow(isOpenedFromFile);

                System.Threading.Thread.Sleep(200);

                _mainWindow?.Show();

                // 处理命令行参数（文件关联打开）
                if (e.Args != null && e.Args.Length > 0)
                {
                    ProcessCommandLineArgs(e.Args);
                }

                Logger.Info("App", "应用程序启动完成");
            }
            catch (Exception ex)
            {
                Logger.Error("App", $"启动失败: {ex.Message}", ex);
                System.Windows.MessageBox.Show($"启动失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        /// <summary>
        /// 检测是否通过文件关联打开
        /// </summary>
        /// <param name="args">命令行参数</param>
        /// <returns>是否通过文件关联打开</returns>
        private bool IsOpenedFromFile(string[] args)
        {
            if (args == null || args.Length == 0)
                return false;

            string[] supportedExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

            foreach (var arg in args)
            {
                if (System.IO.File.Exists(arg))
                {
                    string extension = System.IO.Path.GetExtension(arg).ToLower();
                    if (supportedExtensions.Contains(extension))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 处理命令行参数（支持文件关联打开）
        /// </summary>
        /// <param name="args">命令行参数数组</param>
        private void ProcessCommandLineArgs(string[] args)
        {
            if (args == null || args.Length == 0 || _mainWindow == null)
                return;

            try
            {
                Logger.Info("App", $"处理命令行参数: {string.Join(", ", args)}");

                // 支持的图片扩展名
                string[] supportedExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

                // 筛选出图片文件
                var imageFiles = args
                    .Where(arg => System.IO.File.Exists(arg))
                    .Where(arg =>
                    {
                        string extension = System.IO.Path.GetExtension(arg).ToLower();
                        return supportedExtensions.Contains(extension);
                    })
                    .ToArray();

                if (imageFiles.Length > 0)
                {
                    Logger.Info("App", $"检测到 {imageFiles.Length} 个图片文件参数");
                    _mainWindow.ImportPhotosFromFiles(imageFiles);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("App", $"处理命令行参数失败: {ex.Message}", ex);
            }
        }

        public static void CloseSplash()
        {
            if (SplashWindow != null)
            {
                SplashWindow.Dispatcher.Invoke(() =>
                {
                    SplashWindow.CloseSplash();
                });
                SplashWindow = null;
            }
            if (_splashThread != null && _splashThread.IsAlive)
            {
                _splashThread.Join(2000);
                if (_splashThread.IsAlive)
                {
                    _splashThread.Abort();
                }
                _splashThread = null;
            }
        }

        /// <summary>
        /// 加载语言设置
        /// </summary>
        private void LoadLanguageSettings()
        {
            try
            {
                Logger.Debug("App", "加载语言设置");

                var configPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                if (System.IO.File.Exists(configPath))
                {
                    var json = System.IO.File.ReadAllText(configPath, System.Text.Encoding.UTF8);
                    var config = Newtonsoft.Json.JsonConvert.DeserializeObject<Models.AppConfig>(json);
                    if (config != null)
                    {
                        LanguageManager.Instance.CurrentLanguage = (LanguageType)config.Language;
                        Logger.Info("App", $"语言设置已加载: {LanguageManager.Instance.CurrentLanguage}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("App", $"加载语言设置失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 创建主窗口
        /// </summary>
        /// <param name="isOpenedFromFile">是否通过文件关联打开</param>
        private void CreateMainWindow(bool isOpenedFromFile = false)
        {
            try
            {
                Logger.Debug("App", "创建主窗口开始");

                _mainWindow = new MainWindow(false, isOpenedFromFile);

                Logger.Debug("App", "主窗口创建完成");
            }
            catch (Exception ex)
            {
                Logger.Error("App", $"创建主窗口失败: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// 应用程序退出时的处理
        /// </summary>
        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                Logger.Info("App", "应用程序退出");

                CloseSplash();

                if (_oobeWindow != null)
                {
                    _oobeWindow.Close();
                    _oobeWindow = null;
                }

                if (_mainWindow != null)
                {
                    _mainWindow.Close();
                    _mainWindow = null;
                }
            }
            catch { }

            base.OnExit(e);
        }
    }
}