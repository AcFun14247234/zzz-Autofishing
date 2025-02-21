using System;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Dnn;
using Emgu.CV.Structure;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace WpfApp2
{
    public partial class MainWindow : Window
    {
        // ==== 原有代码 ====
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        // ==== 新增代码 ====
        private Dictionary<string, bool> _keyStateTracker = new Dictionary<string, bool>();

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        private const byte VK_A = 0x41;
        private const byte VK_D = 0x44;

        private bool _isRunning = false;
        private IntPtr _gameWindowHandle;
        private RECT _gameWindowRect;

        private const int CaptureX = 0; // 从窗口左上角开始截图
        private const int CaptureY = 0; // 从窗口左上角开始截图
        private const int CaptureWidth = 1920; // 调整为 1920
        private const int CaptureHeight = 1080; // 调整为 1080

        private Dictionary<string, Action> _imageDictionary;

        // ONNX 模型路径
        private const string OnnxModelPath = "model.onnx";

        // 类别标签
        private readonly string[] _classLabels = { "A", "D" };

        // DNN 模型
        private Net _net;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public MainWindow()
        {
            try
            {
                // 初始化 Emgu.CV
                CvInvoke.Init();
                SetProcessDPIAware();
                InitializeComponent();

                // 加载 ONNX 模型
                _net = DnnInvoke.ReadNetFromONNX(OnnxModelPath);
                if (_net.Empty)
                {
                    Log("无法加载 ONNX 模型！");
                    return;
                }

                // 获取安装目录下的 Resources 文件夹路径
                string resourcePath = Path.Combine(
                    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), // 安装目录
                    "Resources" // 资源文件夹
                );

                // 确保 Resources 文件夹存在
                if (!Directory.Exists(resourcePath))
                {
                    Directory.CreateDirectory(resourcePath);
                    Log($"创建资源文件夹：{resourcePath}");
                }

                // 初始化图像字典
                _imageDictionary = new Dictionary<string, Action>
                {
                    { "A", () => HandleContinuousKey(VK_A, true, _keyStateTracker) },
                    { "D", () => HandleContinuousKey(VK_D, true, _keyStateTracker) }
                };
            }
            catch (Exception ex)
            {
                MessageBox.Show($"初始化失败: {ex.Message}\n请确认已安装VC++运行库", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
            }
        }

        private void StartStopButton_Click(object sender, RoutedEventArgs e)
        {
            _isRunning = !_isRunning;
            StartStopButton.Content = _isRunning ? "停止" : "开始";
            StatusTextBlock.Text = _isRunning ? "状态：识别中..." : "状态：已停止";

            if (_isRunning)
            {
                try
                {
                    _gameWindowHandle = WaitForGameWindow(
                        "绝区零",
                        "UnityWndClass"
                    );

                    if (_gameWindowHandle == IntPtr.Zero)
                    {
                        Log("未找到游戏窗口！");
                        _isRunning = false;
                        StartStopButton.Content = "开始";
                        StatusTextBlock.Text = "状态：未找到游戏窗口";
                        return;
                    }

                    if (!GetWindowRect(_gameWindowHandle, out _gameWindowRect))
                    {
                        Log("无法获取窗口尺寸！");
                        _isRunning = false;
                        StartStopButton.Content = "开始";
                        StatusTextBlock.Text = "状态：窗口尺寸获取失败";
                        return;
                    }

                    Log($"找到游戏窗口，区域：{_gameWindowRect.Left}, {_gameWindowRect.Top}, {_gameWindowRect.Right}, {_gameWindowRect.Bottom}");
                    Task.Run(() => RunImageRecognition());
                }
                catch (Exception ex)
                {
                    Log($"窗口查找失败：{ex.Message}");
                    _isRunning = false;
                    StartStopButton.Content = "开始";
                    StatusTextBlock.Text = "状态：窗口查找失败";
                }
            }
        }

        private IntPtr WaitForGameWindow(string windowTitle, string className = null, int timeout = 10000)
        {
            IntPtr windowHandle = IntPtr.Zero;
            Stopwatch stopwatch = Stopwatch.StartNew();

            while (windowHandle == IntPtr.Zero && stopwatch.ElapsedMilliseconds < timeout)
            {
                windowHandle = FindWindow(className, windowTitle);

                if (windowHandle == IntPtr.Zero)
                {
                    windowHandle = FindWindowByExactTitle(windowTitle);
                }

                if (windowHandle != IntPtr.Zero && !VerifyWindowValid(windowHandle))
                {
                    windowHandle = IntPtr.Zero;
                }

                if (windowHandle == IntPtr.Zero)
                {
                    Task.Delay(500).Wait();
                }
            }
            return windowHandle;
        }

        private IntPtr FindWindowByExactTitle(string windowTitle)
        {
            IntPtr result = IntPtr.Zero;
            EnumWindows(delegate (IntPtr hWnd, IntPtr param)
            {
                StringBuilder sb = new StringBuilder(256);
                GetWindowText(hWnd, sb, 256);
                if (sb.ToString() == windowTitle)
                {
                    result = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        private bool VerifyWindowValid(IntPtr hWnd)
        {
            if (!IsWindowVisible(hWnd)) return false;

            GetWindowRect(hWnd, out RECT rect);
            return rect.Right - rect.Left > 100 && rect.Bottom - rect.Top > 100;
        }

        private void RunImageRecognition()
        {
            while (_isRunning)
            {
                try
                {
                    using (var screenshot = CaptureGameWindow())
                    {
                        // 使用 AI 模型识别图像
                        var detectedClass = PerformImageRecognition(screenshot);
                        if (!string.IsNullOrEmpty(detectedClass) && _imageDictionary.ContainsKey(detectedClass))
                        {
                            _imageDictionary[detectedClass].Invoke();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"图像识别出错：{ex.Message}");
                }

                Task.Delay(100).Wait();
            }
        }

        private string PerformImageRecognition(Bitmap screenshot)
        {
            try
            {
                // 将 Bitmap 转换为 Mat
                Mat screenshotMat = new Mat();
                using (var memoryStream = new MemoryStream())
                {
                    screenshot.Save(memoryStream, System.Drawing.Imaging.ImageFormat.Png);
                    byte[] imageBytes = memoryStream.ToArray();
                    CvInvoke.Imdecode(imageBytes, ImreadModes.Color, screenshotMat);
                }

                // 预处理图像
                // 预处理图像
                Mat blob = DnnInvoke.BlobFromImage(screenshotMat, 1.0 / 255, new System.Drawing.Size(224, 224), new MCvScalar(0, 0, 0), true, false);

                // 设置模型输入
                _net.SetInput(blob);

                // 运行推理
                Mat output = _net.Forward();

                // 解析输出
                float[] scores = output.GetData(true) as float[];
                int maxIndex = scores.ToList().IndexOf(scores.Max());
                string detectedClass = _classLabels[maxIndex];

                Log($"检测到类别：{detectedClass}");
                return detectedClass;
            }
            catch (Exception ex)
            {
                Log($"图像处理失败：{ex.Message}");
                Log($"异常详细信息：{ex.ToString()}");
                return null;
            }
        }

        private void Log(string message)
        {
            if (Dispatcher.CheckAccess())
            {
                LogTextBox.AppendText($"{DateTime.Now:HH:mm:ss}: {message}\n");
                LogTextBox.ScrollToEnd();
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => Log(message)));
            }
        }

        private Bitmap CaptureGameWindow()
        {
            var screenshot = new Bitmap(CaptureWidth, CaptureHeight);
            using (var graphics = Graphics.FromImage(screenshot))
            {
                graphics.CopyFromScreen(
                    _gameWindowRect.Left + CaptureX,
                    _gameWindowRect.Top + CaptureY,
                    0,
                    0,
                    new System.Drawing.Size(CaptureWidth, CaptureHeight),
                    CopyPixelOperation.SourceCopy
                );
            }
            return screenshot;
        }

        private void HandleContinuousKey(byte key, bool isLongPress, Dictionary<string, bool> keyStateTracker)
        {
            string keyId = key.ToString();

            if (!keyStateTracker.ContainsKey(keyId))
            {
                keyStateTracker[keyId] = false;
            }

            if (isLongPress)
            {
                if (!keyStateTracker[keyId])
                {
                    keybd_event(key, 0, 0x0000, UIntPtr.Zero); // 按下按键
                    keyStateTracker[keyId] = true;
                    Log($"长按 {(char)key} 键，状态：按下");
                }
            }
            else
            {
                RapidPress(key);
            }
        }

        private async void RapidPress(byte key)
        {
            string keyId = key.ToString();
            _keyStateTracker[keyId] = true;
            for (int i = 0; i < 5; i++)
            {
                keybd_event(key, 0, 0x0000, UIntPtr.Zero); // 按下按键
                await Task.Delay(10);
                keybd_event(key, 0, 0x0002, UIntPtr.Zero); // 释放按键
                await Task.Delay(200);
                Log($"快速连点 {(char)key} 键，状态：按下并释放");
            }
            _keyStateTracker[keyId] = false;
            Log($"快速连点 {(char)key} 键，状态：释放");
        }
    }
}