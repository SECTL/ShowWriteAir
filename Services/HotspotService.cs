using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ShowWrite.Services
{
    public enum HotspotFailureReason
    {
        UacDenied,
        PermissionDenied,
        NotSupported,
        Timeout,
        ConfigFailed,
        StartFailed,
        Unknown
    }

    public class HotspotResult
    {
        public bool Success { get; set; }
        public HotspotFailureReason? FailureReason { get; set; }
        public string ErrorMessage { get; set; }

        public static HotspotResult Ok() => new HotspotResult { Success = true };
        public static HotspotResult Fail(HotspotFailureReason reason, string message = "") =>
            new HotspotResult { Success = false, FailureReason = reason, ErrorMessage = message };
    }

    public static class HotspotConfig
    {
        public const string Ssid = "ShowWriteHotspot";
        public const string Password = "12345678";
        public const string DefaultHotspotIP = "192.168.137.1";
        public const int ProcessTimeoutSeconds = 30;
    }

    public class HotspotService
    {
        /// <summary>
        /// 当前使用的热点实现方式
        /// </summary>
        public string CurrentBackend { get; private set; } = "Unknown";

        /// <summary>
        /// 配置并启动热点。优先通过 PowerShell 调用 WinRT TetheringManager (现代 API)，
        /// 失败或旧系统回退到 netsh hostednetwork。
        /// </summary>
        public async Task<HotspotResult> ConfigureAndStartHotspot(string ssid, string password)
        {
            try
            {
                Logger.Info("HotspotService", $"开始配置热点 SSID={ssid}");

                // 优先尝试现代 API (TetheringManager via PowerShell)
                Logger.Info("HotspotService", "尝试使用 TetheringManager (现代 API via PowerShell)");
                var modernResult = await StartTetheringViaPowerShellAsync(ssid, password);
                if (modernResult.Success)
                {
                    CurrentBackend = "TetheringManager";
                    return modernResult;
                }

                Logger.Warning("HotspotService", $"TetheringManager 失败: {modernResult.ErrorMessage}，尝试回退到 netsh");

                // 回退到 netsh hostednetwork
                var netshResult = await StartNetshHotspotAsync(ssid, password);
                if (netshResult.Success)
                {
                    CurrentBackend = "netsh";
                }
                return netshResult;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Logger.Warning("HotspotService", "UAC 提权被用户拒绝");
                return HotspotResult.Fail(HotspotFailureReason.UacDenied, ex.Message);
            }
            catch (TimeoutException)
            {
                Logger.Warning("HotspotService", "热点配置超时");
                return HotspotResult.Fail(HotspotFailureReason.Timeout);
            }
            catch (Win32Exception ex)
            {
                Logger.Error("HotspotService", $"权限不足: {ex.Message}", ex);
                return HotspotResult.Fail(HotspotFailureReason.PermissionDenied, ex.Message);
            }
            catch (Exception ex)
            {
                Logger.Error("HotspotService", $"热点配置未知异常: {ex.Message}", ex);
                return HotspotResult.Fail(HotspotFailureReason.Unknown, ex.Message);
            }
        }

        public async Task<HotspotResult> StopHotspot()
        {
            try
            {
                Logger.Info("HotspotService", "开始停止热点");

                // 优先用 PowerShell 停止 TetheringManager
                if (CurrentBackend == "TetheringManager" || CurrentBackend == "Unknown")
                {
                    try
                    {
                        var stopResult = await StopTetheringViaPowerShellAsync();
                        if (stopResult.Success)
                        {
                            Logger.Info("HotspotService", "热点已停止 (TetheringManager)");
                            return stopResult;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning("HotspotService", $"TetheringManager 停止失败: {ex.Message}，尝试 netsh");
                    }
                }

                var result = await RunElevatedNetshAsync("wlan stop hostednetwork");
                if (result.exitCode != 0)
                {
                    Logger.Error("HotspotService", $"停止热点失败: {result.output}");
                    return HotspotResult.Fail(HotspotFailureReason.StartFailed, result.output);
                }
                Logger.Info("HotspotService", "热点已停止 (netsh)");
                return HotspotResult.Ok();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return HotspotResult.Fail(HotspotFailureReason.UacDenied, ex.Message);
            }
            catch (TimeoutException)
            {
                return HotspotResult.Fail(HotspotFailureReason.Timeout);
            }
            catch (Exception ex)
            {
                Logger.Error("HotspotService", $"停止热点异常: {ex.Message}", ex);
                return HotspotResult.Fail(HotspotFailureReason.Unknown, ex.Message);
            }
        }

        public string GetHotspotIPAddress()
        {
            try
            {
                var adapters = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                                 ni.NetworkInterfaceType != NetworkInterfaceType.Loopback);

                foreach (var adapter in adapters)
                {
                    // 兼容两种后端的热点适配器
                    if (adapter.Name.Contains("ShowWriteHotspot") ||
                        adapter.Description.Contains("Microsoft Hosted Network Virtual Adapter") ||
                        adapter.Description.Contains("Hosted Network") ||
                        adapter.Description.IndexOf("Microsoft Wi-Fi Direct Virtual Adapter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        adapter.Name.IndexOf("Local Area", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var ipProps = adapter.GetIPProperties();
                        foreach (var addr in ipProps.UnicastAddresses)
                        {
                            if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                Logger.Info("HotspotService", $"热点适配器 IP: {addr.Address}");
                                return addr.Address.ToString();
                            }
                        }
                    }
                }

                Logger.Warning("HotspotService", "未找到热点适配器，返回默认 IP");
                return HotspotConfig.DefaultHotspotIP;
            }
            catch (Exception ex)
            {
                Logger.Error("HotspotService", $"获取热点 IP 失败: {ex.Message}", ex);
                return HotspotConfig.DefaultHotspotIP;
            }
        }

        public bool IsHostedNetworkSupported()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = "wlan show drivers",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(psi);
                if (process == null) return false;
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                return output.Contains("是") && output.Contains("托管网络") ||
                       output.Contains("Yes") && output.Contains("Hosted network");
            }
            catch (Exception ex)
            {
                Logger.Error("HotspotService", $"检测托管网络支持失败: {ex.Message}", ex);
                return false;
            }
        }

        #region 现代 API: TetheringManager (via PowerShell)

        /// <summary>
        /// PowerShell 脚本：通过 WinRT TetheringManager 配置并启动热点
        /// 不直接 await StartTetheringAsync 返回值（PowerShell WinRT 投影存在 COM 类型转换问题），
        /// 而是调用后轮询 TetheringOperationalState 判断是否成功启动。
        /// </summary>
        private const string StartTetheringScript = @"
param([string]$Ssid = 'ShowWriteHotspot', [string]$Password = '12345678')
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Runtime.WindowsRuntime

function Await-Action($op) {
    $method = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and -not $_.IsGenericMethod -and
        $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction'
    } | Select-Object -First 1
    $task = $method.Invoke($null, @($op))
    $task.Wait(-1) | Out-Null
}

try {
    $t = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType=WindowsRuntime]

    $profile = [Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType=WindowsRuntime]::GetInternetConnectionProfile()
    if ($null -eq $profile) {
        Write-Output 'FAIL:NoInternetConnection'
        return
    }

    $manager = $t::CreateFromConnectionProfile($profile)

    # 获取当前配置并修改（避免直接 New-Object 加载 TetheringAccessPointConfiguration 失败）
    $config = $manager.GetCurrentAccessPointConfiguration()
    $config.Ssid = $Ssid
    $config.Passphrase = $Password
    Await-Action ($manager.ConfigureAccessPointAsync($config))

    # 调用 StartTetheringAsync，不 await 返回值
    $op = $manager.StartTetheringAsync()

    # 轮询 TetheringOperationalState 直到 On (1) 或超时
    $maxWait = 15
    $elapsed = 0
    $state = $manager.TetheringOperationalState
    while ($state -ne 1 -and $elapsed -lt $maxWait) {
        Start-Sleep -Milliseconds 500
        $elapsed += 0.5
        $state = $manager.TetheringOperationalState
    }

    # TetheringOperationalState: 0=Unknown, 1=On, 2=Off
    if ($state -eq 1) {
        Write-Output 'SUCCESS'
    } else {
        Write-Output ('FAIL:State=' + $state)
    }
}
catch {
    Write-Output ('ERROR:' + $_.Exception.Message)
}
";

        /// <summary>
        /// PowerShell 脚本：通过 WinRT TetheringManager 停止热点
        /// </summary>
        private const string StopTetheringScript = @"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Runtime.WindowsRuntime

function Await-Action($op) {
    $method = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and -not $_.IsGenericMethod -and
        $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction'
    } | Select-Object -First 1
    $task = $method.Invoke($null, @($op))
    $task.Wait(-1) | Out-Null
}

try {
    $t = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType=WindowsRuntime]

    $profile = [Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType=WindowsRuntime]::GetInternetConnectionProfile()
    if ($null -eq $profile) {
        Write-Output 'SUCCESS'
        return
    }

    $manager = $t::CreateFromConnectionProfile($profile)
    $op = $manager.StopTetheringAsync()

    # 轮询 TetheringOperationalState 直到 Off (2) 或超时
    $maxWait = 10
    $elapsed = 0
    $state = $manager.TetheringOperationalState
    while ($state -eq 1 -and $elapsed -lt $maxWait) {
        Start-Sleep -Milliseconds 500
        $elapsed += 0.5
        $state = $manager.TetheringOperationalState
    }

    Write-Output 'SUCCESS'
}
catch {
    Write-Output ('ERROR:' + $_.Exception.Message)
}
";

        private async Task<HotspotResult> StartTetheringViaPowerShellAsync(string ssid, string password)
        {
            var tempScript = Path.Combine(Path.GetTempPath(), $"showwrite_hotspot_start_{Guid.NewGuid():N}.ps1");
            try
            {
                await File.WriteAllTextAsync(tempScript, StartTetheringScript, Encoding.UTF8);

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempScript}\" -Ssid \"{ssid}\" -Password \"{password}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                Logger.Debug("HotspotService", "通过 PowerShell 启动 TetheringManager 热点");

                using var process = Process.Start(psi);
                if (process == null)
                {
                    return HotspotResult.Fail(HotspotFailureReason.Unknown, "无法启动 PowerShell");
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(HotspotConfig.ProcessTimeoutSeconds));
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();

                // 等待进程退出
                while (!process.HasExited)
                {
                    if (cts.Token.IsCancellationRequested)
                    {
                        try { process.Kill(); } catch { }
                        return HotspotResult.Fail(HotspotFailureReason.Timeout, "PowerShell 执行超时");
                    }
                    await Task.Delay(200, cts.Token);
                }

                string output = (await outputTask).Trim();
                string error = (await errorTask).Trim();

                Logger.Debug("HotspotService", $"PowerShell 输出: {output}");
                if (!string.IsNullOrEmpty(error))
                    Logger.Warning("HotspotService", $"PowerShell 错误流: {error}");

                if (output.StartsWith("SUCCESS"))
                {
                    Logger.Info("HotspotService", "TetheringManager 热点已启动");
                    return HotspotResult.Ok();
                }
                else if (output.StartsWith("FAIL:"))
                {
                    string msg = output.Substring(5);
                    Logger.Error("HotspotService", $"TetheringManager 启动失败: {msg}");
                    return HotspotResult.Fail(HotspotFailureReason.StartFailed, msg);
                }
                else if (output.StartsWith("ERROR:"))
                {
                    string msg = output.Substring(6);
                    Logger.Error("HotspotService", $"TetheringManager 异常: {msg}");
                    return HotspotResult.Fail(HotspotFailureReason.StartFailed, msg);
                }
                else
                {
                    Logger.Error("HotspotService", $"PowerShell 未知输出: {output} {error}");
                    return HotspotResult.Fail(HotspotFailureReason.Unknown, output + " " + error);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("HotspotService", $"PowerShell 执行异常: {ex.Message}", ex);
                return HotspotResult.Fail(HotspotFailureReason.Unknown, ex.Message);
            }
            finally
            {
                try { if (File.Exists(tempScript)) File.Delete(tempScript); } catch { }
            }
        }

        private async Task<HotspotResult> StopTetheringViaPowerShellAsync()
        {
            var tempScript = Path.Combine(Path.GetTempPath(), $"showwrite_hotspot_stop_{Guid.NewGuid():N}.ps1");
            try
            {
                await File.WriteAllTextAsync(tempScript, StopTetheringScript, Encoding.UTF8);

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempScript}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                Logger.Debug("HotspotService", "通过 PowerShell 停止 TetheringManager 热点");

                using var process = Process.Start(psi);
                if (process == null)
                {
                    return HotspotResult.Fail(HotspotFailureReason.Unknown, "无法启动 PowerShell");
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(HotspotConfig.ProcessTimeoutSeconds));
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();

                while (!process.HasExited)
                {
                    if (cts.Token.IsCancellationRequested)
                    {
                        try { process.Kill(); } catch { }
                        return HotspotResult.Fail(HotspotFailureReason.Timeout, "PowerShell 执行超时");
                    }
                    await Task.Delay(200, cts.Token);
                }

                string output = (await outputTask).Trim();
                string error = (await errorTask).Trim();

                Logger.Debug("HotspotService", $"PowerShell 输出: {output}");

                if (output.StartsWith("SUCCESS"))
                {
                    return HotspotResult.Ok();
                }
                else
                {
                    Logger.Warning("HotspotService", $"停止 TetheringManager 失败: {output} {error}");
                    return HotspotResult.Fail(HotspotFailureReason.StartFailed, output + " " + error);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("HotspotService", $"PowerShell 停止异常: {ex.Message}", ex);
                return HotspotResult.Fail(HotspotFailureReason.Unknown, ex.Message);
            }
            finally
            {
                try { if (File.Exists(tempScript)) File.Delete(tempScript); } catch { }
            }
        }

        #endregion

        #region 旧 API: netsh hostednetwork

        private async Task<HotspotResult> StartNetshHotspotAsync(string ssid, string password)
        {
            Logger.Info("HotspotService", "使用 netsh hostednetwork 配置热点");

            var configResult = await RunElevatedNetshAsync($"wlan set hostednetwork mode=allow ssid={ssid} key={password}");
            if (configResult.exitCode != 0)
            {
                if (configResult.output.Contains("不支持") || configResult.output.Contains("not supported") || configResult.output.Contains("hosted network"))
                {
                    Logger.Warning("HotspotService", "系统不支持托管网络");
                    return HotspotResult.Fail(HotspotFailureReason.NotSupported, configResult.output);
                }
                Logger.Error("HotspotService", $"热点配置失败: {configResult.output}");
                return HotspotResult.Fail(HotspotFailureReason.ConfigFailed, configResult.output);
            }

            Logger.Info("HotspotService", "热点配置成功，开始启动");

            var startResult = await RunElevatedNetshAsync("wlan start hostednetwork");
            if (startResult.exitCode != 0)
            {
                Logger.Error("HotspotService", $"热点启动失败: {startResult.output}");
                return HotspotResult.Fail(HotspotFailureReason.StartFailed, startResult.output);
            }

            Logger.Info("HotspotService", "热点已成功启动 (netsh)");
            return HotspotResult.Ok();
        }

        private async Task<(int exitCode, string output)> RunElevatedNetshAsync(string arguments)
        {
            var tempFile = Path.Combine(Path.GetTempPath(), $"showwrite_netsh_{Guid.NewGuid():N}.txt");
            var sanitizedArgs = arguments.Replace(HotspotConfig.Password, "***");
            Logger.Debug("HotspotService", $"执行提权 netsh 命令: {sanitizedArgs}");

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c netsh {arguments} > \"{tempFile}\" 2>&1",
                    Verb = "runas",
                    UseShellExecute = true,
                    CreateNoWindow = true
                };

                Process process;
                try
                {
                    process = Process.Start(psi);
                }
                catch (Win32Exception ex)
                {
                    Logger.Warning("HotspotService", $"UAC 提权失败: {ex.Message}");
                    throw;
                }

                if (process == null)
                {
                    return (1, "Failed to start elevated process");
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(HotspotConfig.ProcessTimeoutSeconds));
                var tcs = new TaskCompletionSource<bool>();

                void OnExited(object s, EventArgs e)
                {
                    process.Exited -= OnExited;
                    tcs.TrySetResult(true);
                }

                process.EnableRaisingEvents = true;
                process.Exited += OnExited;

                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, cts.Token));

                if (completedTask != tcs.Task)
                {
                    try { process.Kill(); } catch { }
                    process.Exited -= OnExited;
                    throw new TimeoutException($"netsh 命令超时 ({HotspotConfig.ProcessTimeoutSeconds}s)");
                }

                var exitCode = process.ExitCode;
                string output = "";

                if (File.Exists(tempFile))
                {
                    try
                    {
                        output = await File.ReadAllTextAsync(tempFile);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning("HotspotService", $"读取临时文件失败: {ex.Message}");
                    }
                }

                var sanitizedOutput = output.Replace(HotspotConfig.Password, "***");
                Logger.Debug("HotspotService", $"netsh 命令完成 exitCode={exitCode} output={sanitizedOutput}");

                return (exitCode, output);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempFile))
                        File.Delete(tempFile);
                }
                catch { }
            }
        }

        #endregion
    }
}
