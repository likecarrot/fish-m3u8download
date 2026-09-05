using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using System.Web; // 用于 URL 解码
using System.Collections.Generic;

namespace FishM3u8Downloader
{
    class Program
    {
        // 自定义协议名
        private const string ProtocolName = "fish-m3u8downloader";
        // MediaGo API 端口（你指定的 8899）
        private const int ApiPort = 8899;
        // 状态轮询间隔（毫秒）
        private const int PollInterval = 3000;

        #region API 数据结构
        // 通用 API 响应结构
        class ApiResponse<T>
        {
            public bool success { get; set; }
            public int code { get; set; }
            public string message { get; set; }
            public T data { get; set; }
        }

        // 下载任务结构
        class DownloadTask
        {
            public int id { get; set; }
            public string name { get; set; }
            public string type { get; set; }
            public string url { get; set; }
            public string status { get; set; }
            public string file { get; set; }
            public DateTime createdDate { get; set; }
            public DateTime updatedDate { get; set; }
        }

        // 日志响应结构
        class LogResult
        {
            public int id { get; set; }
            public string log { get; set; }
        }
        #endregion

        static void Main(string[] args)
        {
            try
            {
                // 注册协议
                if (args.Length == 1 && args[0].Equals("--register", StringComparison.OrdinalIgnoreCase))
                {
                    RegisterProtocol();
                    Console.WriteLine($"协议 {ProtocolName} 注册成功！");
                    Console.WriteLine("按任意键退出...");
                    Console.ReadKey();
                    return;
                }

                // 注销协议
                if (args.Length == 1 && args[0].Equals("--unregister", StringComparison.OrdinalIgnoreCase))
                {
                    UnregisterProtocol();
                    Console.WriteLine($"协议 {ProtocolName} 已注销！");
                    Console.WriteLine("按任意键退出...");
                    Console.ReadKey();
                    return;
                }

                // 解析下载参数
                var options = ParseArguments(args);
                if (options == null)
                {
                    PrintUsage();
                    foreach (var ar in args)
                        Console.Write(ar);

                    Console.WriteLine("按任意键退出...");
                    Console.ReadKey();
                    return;
                }

                Console.WriteLine("========== 下载任务接收 ==========");
                Console.WriteLine($"  视频链接: {options.Url}");
                Console.WriteLine($"  服务地址: {options.Host}:{ApiPort}");
                Console.WriteLine($"  文件名称: {options.SaveName}");
                Console.WriteLine($"  数据库: {options.DatabasePath}");
                Console.WriteLine("==================================");

                // 1. 番号去重检查
                if (CheckDuplicate(options.SaveName, options.DatabasePath))
                {
                    Console.WriteLine("⚠️  番号已存在于数据库，跳过下载。");
                    Console.WriteLine("按任意键退出...");
                    Console.ReadKey();
                    return;
                }

                // 2. 提交下载任务
                Console.WriteLine("\n正在提交下载任务...");
                DownloadTask task = SubmitDownloadTask(options);
                if (task == null)
                {
                    Console.WriteLine("× 任务提交失败，请检查服务地址和网络。");
                    Console.WriteLine("按任意键退出...");
                    Console.ReadKey();
                    return;
                }

                Console.WriteLine($"√ 任务提交成功，任务ID：{task.id}");
                Console.WriteLine("开始监控下载状态...\n");

                // 3. 循环轮询任务状态
                while (true)
                {
                    Thread.Sleep(PollInterval);
                    DownloadTask currentTask = GetTaskInfo(options.Host, task.id, options.Auth);

                    if (currentTask == null)
                    {
                        Console.Write("\r查询状态失败，重试中...  ");
                        continue;
                    }

                    // 实时刷新状态显示
                    Console.Write($"\r当前状态: {currentTask.status,-12} ");

                    switch (currentTask.status)
                    {
                        case "success":
                            HandleSuccess(options.SaveName, options.DatabasePath);
                            goto EndLoop;

                        case "failed":
                            HandleFailed(options.Host, task.id, options.Auth);
                            goto EndLoop;

                        case "stopped":
                            Console.WriteLine("\n⏹️  下载已被手动停止");
                            goto EndLoop;

                        // waiting / downloading 继续循环
                        default:
                            break;
                    }
                }
            EndLoop:;

                Console.WriteLine("\n----------------------------------");
                Console.WriteLine("按任意键退出程序...");
                Console.ReadKey();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n× 程序发生异常: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                Console.WriteLine("\n按任意键退出...");
                Console.ReadKey();
            }
        }

        #region 参数解析
        class DownloadOptions
        {
            public string Url { get; set; }
            public string Host { get; set; }
            public string SaveName { get; set; }
            public string DatabasePath { get; set; }
            public string Auth { get; set; }
        }

        /// <summary>
        /// 手动拆分命令行参数字符串，支持双引号包裹的带空格参数
        /// </summary>
        static string[] SplitCommandLine(string cmd)
        {
            var args = new List<string>();
            var current = new System.Text.StringBuilder();
            bool inQuote = false;

            for (int i = 0; i < cmd.Length; i++)
            {
                char c = cmd[i];
                if (c == '"')
                {
                    inQuote = !inQuote;
                    continue;
                }
                if (c == ' ' && !inQuote)
                {
                    if (current.Length > 0)
                    {
                        args.Add(current.ToString());
                        current.Clear();
                    }
                    continue;
                }
                current.Append(c);
            }
            if (current.Length > 0)
                args.Add(current.ToString());

            return args.ToArray();
        }

        static DownloadOptions ParseArguments(string[] args)
        {
            if (args.Length == 0) return null;

            // 1. 拿到原始的第一个参数（整个协议URL）
            string rawArg = args[0];

            // 2. URL 解码，还原 %20 %22 等编码字符
            string decoded = HttpUtility.UrlDecode(rawArg);

            // 3. 去掉协议前缀
            if (!decoded.StartsWith(ProtocolName + ":", StringComparison.OrdinalIgnoreCase))
            {
                // 手动调试模式：直接传参的情况，走原逻辑
                return ParseArgumentsLegacy(args);
            }

            string paramStr = decoded.Substring(ProtocolName.Length + 1).TrimStart('/');

            // 4. 手动拆分参数数组
            string[] realArgs = SplitCommandLine(paramStr);
            if (realArgs.Length == 0) return null;

            var options = new DownloadOptions();
            // 第一个拆分出来的就是 URL
            options.Url = realArgs[0];

            // 5. 解析后续命名参数
            for (int i = 1; i < realArgs.Length; i++)
            {
                if (realArgs[i].Equals("-host", StringComparison.OrdinalIgnoreCase) && i + 1 < realArgs.Length)
                {
                    options.Host = realArgs[++i];
                }
                else if (realArgs[i].Equals("--saveName", StringComparison.OrdinalIgnoreCase) && i + 1 < realArgs.Length)
                {
                    options.SaveName = realArgs[++i];
                }
                else if (realArgs[i].Equals("--Database", StringComparison.OrdinalIgnoreCase) && i + 1 < realArgs.Length)
                {
                    options.DatabasePath = realArgs[++i];
                }
                else if (realArgs[i].Equals("--auth", StringComparison.OrdinalIgnoreCase) && i + 1 < realArgs.Length)
                {
                    options.Auth = realArgs[++i];
                }
            }

            // 校验必填项
            if (string.IsNullOrEmpty(options.Url) || string.IsNullOrEmpty(options.Host) ||
                string.IsNullOrEmpty(options.SaveName) || string.IsNullOrEmpty(options.DatabasePath))
            {
                return null;
            }


            if (options.SaveName.Contains("Jable.TV"))
                options.SaveName = options.SaveName.Substring(0, options.SaveName.IndexOf("Jable.TV") - 3);
            if (options.SaveName.Contains("MissAV"))
                options.SaveName = options.SaveName.Substring(0, options.SaveName.IndexOf("MissAV") - 3);
            if (options.SaveName.Contains("Pornhub.com"))
                options.SaveName = options.SaveName.Substring(0, options.SaveName.IndexOf("Pornhub.com") - 3);



            return options;
        }

        /// <summary>
        /// 兼容旧的手动调试传参方式
        /// </summary>
        static DownloadOptions ParseArgumentsLegacy(string[] args)
        {
            var options = new DownloadOptions();
            options.Url = args[0];

            for (int i = 1; i < args.Length; i++)
            {
                if (args[i].Equals("-host", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.Host = args[++i];
                }
                else if (args[i].Equals("--saveName", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.SaveName = args[++i];
                }
                else if (args[i].Equals("--Database", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.DatabasePath = args[++i];
                }
                else if (args[i].Equals("--auth", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.Auth = args[++i];
                }
            }

            if (string.IsNullOrEmpty(options.Url) || string.IsNullOrEmpty(options.Host) ||
                string.IsNullOrEmpty(options.SaveName) || string.IsNullOrEmpty(options.DatabasePath))
            {
                return null;
            }

            return options;
        }


        static void PrintUsage()
        {
            Console.WriteLine("参数错误，正确用法：");
            Console.WriteLine($"  {ProtocolName}:<视频地址> -host <IP> --saveName <文件名> --Database <数据库txt路径>");
            Console.WriteLine("\n示例：");
            Console.WriteLine($"  {ProtocolName}:https://example.com/video.m3u8 -host 192.168.0.144 --saveName \"STARS-435 测试\" --Database \"D:\\downloads\\zchepai.txt\"");
            Console.WriteLine("\n注册协议: FishM3u8Downloader.exe --register");
            Console.WriteLine("注销协议: FishM3u8Downloader.exe --unregister");
        }
        #endregion

        #region 番号去重与入库
        /// <summary>
        /// 检查文件名中是否包含数据库中已有的番号
        /// </summary>
        static bool CheckDuplicate(string saveName, string dbPath)
        {
            if (!File.Exists(dbPath)) return false;

            string[] lines = File.ReadAllLines(dbPath);
            foreach (string line in lines)
            {
                string num = line.Trim();
                if (string.IsNullOrEmpty(num)) continue;
                if (saveName.IndexOf(num, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Console.WriteLine($"\n匹配到已存在番号: {num}");
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 从文件名中提取番号（默认匹配 字母-数字 格式，如 STARS-435）
        /// </summary>
        static string ExtractNumber(string fileName)
        {
            Match match = Regex.Match(fileName, @"[A-Za-z]+-\d+", RegexOptions.IgnoreCase);
            return match.Success ? match.Value.ToUpper() : null;
        }

        /// <summary>
        /// 下载成功处理：提取番号并追加到数据库
        /// </summary>
        static void HandleSuccess(string saveName, string dbPath)
        {
            Console.WriteLine("\n✅ 下载完成！");
            string number = ExtractNumber(saveName);

            if (string.IsNullOrEmpty(number))
            {
                Console.WriteLine("⚠️  未能从文件名中提取到番号，未写入数据库");
                return;
            }

            // 去重后写入
            bool exists = false;
            if (File.Exists(dbPath))
            {
                string[] existing = File.ReadAllLines(dbPath);
                foreach (string line in existing)
                {
                    if (line.Trim().Equals(number, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
            }

            if (!exists)
            {
                File.AppendAllText(dbPath, number + Environment.NewLine, Encoding.UTF8);
                Console.WriteLine($"📝 番号 {number} 已写入数据库: {dbPath}");
            }
            else
            {
                Console.WriteLine($"ℹ️  番号 {number} 已在数据库中，无需重复写入");
            }
        }
        #endregion

        #region MediaGo API 调用
        static JavaScriptSerializer _serializer = new JavaScriptSerializer();

        /// <summary>
        /// 提交下载任务
        /// </summary>
        static DownloadTask SubmitDownloadTask(DownloadOptions options)
        {
            string apiUrl = $"http://{options.Host}:{ApiPort}/api/downloads";

            // 构造请求体
            var requestBody = new
            {
                tasks = new[]
                {
            new
            {
                type = "m3u8",
                url = options.Url,
                name = options.SaveName
            }
        },
                startDownload = true
            };

            try
            {
                using (WebClient client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.ContentType] = "application/json";
                    client.Encoding = Encoding.UTF8;

                    // 新增：Bearer Token 鉴权
                    if (!string.IsNullOrEmpty(options.Auth))
                    {
                        client.Headers[HttpRequestHeader.Authorization] = $"Bearer {options.Auth}";
                    }

                    string json = _serializer.Serialize(requestBody);
                    string response = client.UploadString(apiUrl, "POST", json);

                    var result = _serializer.Deserialize<ApiResponse<DownloadTask[]>>(response);
                    if (result.success && result.data != null && result.data.Length > 0)
                    {
                        return result.data[0];
                    }
                    return null;
                }
            }
            catch (WebException ex)
            {
                Console.WriteLine($"请求失败: {ex.Message}");
                if (ex.Response != null)
                {
                    using (StreamReader reader = new StreamReader(ex.Response.GetResponseStream()))
                    {
                        Console.WriteLine($"错误详情: {reader.ReadToEnd()}");
                    }
                }
                return null;
            }
        }


        /// <summary>
        /// 查询单个任务详情
        /// </summary>
        static DownloadTask GetTaskInfo(string host, int taskId, string authToken = null)
        {
            string apiUrl = $"http://{host}:{ApiPort}/api/downloads/{taskId}";
            try
            {
                using (WebClient client = new WebClient())
                {
                    client.Encoding = Encoding.UTF8;
                    // 新增：鉴权
                    if (!string.IsNullOrEmpty(authToken))
                    {
                        client.Headers[HttpRequestHeader.Authorization] = $"Bearer {authToken}";
                    }

                    string response = client.DownloadString(apiUrl);
                    var result = _serializer.Deserialize<ApiResponse<DownloadTask>>(response);
                    return result.success ? result.data : null;
                }
            }
            catch
            {
                return null;
            }
        }


        /// <summary>
        /// 下载失败处理：获取并打印详细日志
        /// </summary>
        static void HandleFailed(string host, int taskId, string authToken = null)
        {
            Console.WriteLine("\n❌ 下载失败！");
            Console.WriteLine("===== 详细下载日志 =====");

            string apiUrl = $"http://{host}:{ApiPort}/api/downloads/{taskId}/logs";
            try
            {
                using (WebClient client = new WebClient())
                {
                    client.Encoding = Encoding.UTF8;
                    // 新增：鉴权
                    if (!string.IsNullOrEmpty(authToken))
                    {
                        client.Headers[HttpRequestHeader.Authorization] = $"Bearer {authToken}";
                    }

                    string response = client.DownloadString(apiUrl);
                    var result = _serializer.Deserialize<ApiResponse<LogResult>>(response);
                    if (result.success && result.data != null)
                    {
                        Console.WriteLine(result.data.log);
                    }
                    else
                    {
                        Console.WriteLine(response);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"获取日志失败: {ex.Message}");
            }
            Console.WriteLine("========================");
        }

        #endregion

        #region 自定义协议注册/注销
        static void RegisterProtocol()
        {
            string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;

            using (RegistryKey key = Registry.ClassesRoot.CreateSubKey(ProtocolName))
            {
                key.SetValue("", $"URL:{ProtocolName} Protocol");
                key.SetValue("URL Protocol", "");

                using (RegistryKey shell = key.CreateSubKey("shell"))
                using (RegistryKey open = shell.CreateSubKey("open"))
                using (RegistryKey command = open.CreateSubKey("command"))
                {
                    // %1 接收完整协议URL，%* 传递所有后续参数
                    command.SetValue("", $"\"{exePath}\" \"%1\" %*");
                }
            }
        }

        static void UnregisterProtocol()
        {
            try
            {
                Registry.ClassesRoot.DeleteSubKeyTree(ProtocolName, false);
            }
            catch { }
        }
        #endregion
    }
}
