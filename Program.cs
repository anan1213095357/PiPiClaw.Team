using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace PiPiClaw.Team;

// AOT 必须使用 Source Generator 处理 JSON
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(ChatRequest))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(ChatResponse))]
[JsonSerializable(typeof(NodeInfo))]
[JsonSerializable(typeof(CreateCompanyReq))]
[JsonSerializable(typeof(NodeInfoTemplate))]
[JsonSerializable(typeof(List<NodeInfoTemplate>))]
internal partial class AppJsonContext : JsonSerializerContext { }
public class CreateCompanyReq
{
    public string? Description { get; set; }
    public string? MasterNodeUrl { get; set; }
}
public class NodeInfoTemplate
{
    public string? name { get; set; }
    public string? Role { get; set; }
    public string? Url { get; set; }
    public string? Description { get; set; }
}
public class ChatRequest
{
    public string? message { get; set; }
    public int modelIndex { get; set; }
}

public class ChatResponse
{
    public string? type { get; set; }
    public string? content { get; set; }
}
public class NodeInfo
{
    [JsonPropertyName("Name")] public string? Name { get; set; }
    [JsonPropertyName("Url")] public string? Url { get; set; }
    [JsonPropertyName("Role")] public string? Role { get; set; }
    [JsonPropertyName("Description")] public string? Description { get; set; }
}
// 2. 修改配置类
public class AppConfig
{
    // Key 为姓名，Value 改为 NodeInfo 对象
    public Dictionary<string, NodeInfo> PeerNodes { get; set; } = new();
}

class Program
{
    // 全局静态配置
    private static AppConfig _config = new AppConfig();
    private static string _configPath = "team_config.json";
    private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    static async Task Main(string[] args)
    {
        string url = "http://+:4050/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(url);

        try
        {
            if (File.Exists(_configPath))
            {
                try
                {
                    var json = File.ReadAllText(_configPath, Encoding.UTF8);
                    var loadedConfig = JsonSerializer.Deserialize<AppConfig>(json, AppJsonContext.Default.AppConfig);
                    if (loadedConfig != null) _config = loadedConfig;
                }
                catch { /* 忽略解析错误 */ }
            }
            listener.Start();
            Console.WriteLine($"[系统] 皮皮虾中控已启动，监听地址: {url}");
        }
        catch (HttpListenerException ex)
        {
            Console.WriteLine($"启动失败，请尝试使用管理员权限运行。错误: {ex.Message}");
            return;
        }

        while (true)
        {
            var context = await listener.GetContextAsync();
            _ = Task.Run(() => HandleRequestAsync(context)); // 异步处理不阻塞主线程
        }
    }

    private static async Task HandleRequestAsync(HttpListenerContext context)
    {
        var req = context.Request;
        var res = context.Response;

        try
        {
            string path = req.Url?.AbsolutePath.ToLower() ?? "/";

            // 1. 处理静态前端页面
            if (path == "/" || path == "/index.html")
            {
                res.ContentType = "text/html; charset=utf-8";
                byte[] buffer = Encoding.UTF8.GetBytes(HtmlContent);
                res.ContentLength64 = buffer.Length;
                await res.OutputStream.WriteAsync(buffer);
            }
            else if (path.EndsWith(".png") || path.EndsWith(".jpeg") || path.EndsWith(".jpg"))
            {
                string resourceName = $"PiPiClaw.Team.{path.Substring(1)}";
                using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    res.ContentType = path.EndsWith(".png") ? "image/png" : "image/jpeg";
                    res.ContentLength64 = stream.Length;
                    await stream.CopyToAsync(res.OutputStream);
                }
                else
                { 
                
                }
            }
            // 3. 配置文件接口 GET /api/config
            else if (path == "/api/config" && req.HttpMethod == "GET")
            {
                res.ContentType = "application/json; charset=utf-8";
                var json = JsonSerializer.Serialize(_config, AppJsonContext.Default.AppConfig);
                byte[] buffer = Encoding.UTF8.GetBytes(json);
                res.ContentLength64 = buffer.Length;
                await res.OutputStream.WriteAsync(buffer);
            }
            // 4. 配置文件接口 POST /api/config
            else if (path == "/api/config" && req.HttpMethod == "POST")
            {
                using var reader = new StreamReader(req.InputStream);
                string body = await reader.ReadToEndAsync();
                var newConfig = JsonSerializer.Deserialize<AppConfig>(body, AppJsonContext.Default.AppConfig);
                if (newConfig != null)
                {
                    _config = newConfig; // 更新内存

                    // 【核心修复】：将前端传来的最新配置写进本地文件！
                    File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, AppJsonContext.Default.AppConfig), Encoding.UTF8);
                    Console.WriteLine($"[配置中心] 已更新并持久化员工通讯录。当前在职人数: {_config.PeerNodes.Count}");
                }
                res.StatusCode = 200;
            }
            // 5. 聊天流式接口 POST /api/chat (已联动真正的节点端)
            else if (path == "/api/chat" && req.HttpMethod == "POST")
            {
                string username = req.Headers["X-Username"] ?? "未知员工";
                username = Uri.UnescapeDataString(username);

                res.ContentType = "text/plain; charset=utf-8";
                res.SendChunked = true; // 开启分块传输，实现流式输出

                using var writer = new StreamWriter(res.OutputStream, new UTF8Encoding(false)) { AutoFlush = true };

                // 1. 查找通讯录中该员工对应的真实节点 URL
                if (!_config.PeerNodes.TryGetValue(username, out var nodeInfo) || string.IsNullOrEmpty(nodeInfo.Url))
                {
                    var errMsg = JsonSerializer.Serialize(new ChatResponse { type = "final", content = $"[调度失败] 未找到【{username}】的节点地址。" }, AppJsonContext.Default.ChatResponse);
                    await writer.WriteAsync(errMsg + "|||END|||");
                    return;
                }
                string targetUrl = nodeInfo.Url;
                // 2. 读取前端发来的请求体 (包含 message 和 modelIndex)
                using var reader = new StreamReader(req.InputStream);
                string body = await reader.ReadToEndAsync();

                // 3. 将请求转发给真正的目标节点 (代码1)
                try
                {
                    var proxyReq = new HttpRequestMessage(HttpMethod.Post, targetUrl.TrimEnd('/') + "/api/chat");

                    // 这里为了避免中文导致 Header 报错，进行一下 URL 编码，把真实的员工名传过去
                    proxyReq.Headers.Add("X-Username", Uri.EscapeDataString(username));
                    proxyReq.Content = new StringContent(body, Encoding.UTF8, "application/json");

                    // 关键：使用全局 _httpClient 和 ResponseHeadersRead
                    using var proxyRes = await _httpClient.SendAsync(proxyReq, HttpCompletionOption.ResponseHeadersRead);
                    proxyRes.EnsureSuccessStatusCode(); // 如果对方返回 500 或 404，直接抛出异常进入 catch

                    using var proxyStream = await proxyRes.Content.ReadAsStreamAsync();
                    await proxyStream.CopyToAsync(res.OutputStream);
                }
                catch (HttpRequestException httpEx)
                {
                    // 【核心修复】：将网络底层的真实错误、以及它试图请求的完整 URL 打印回前端气泡
                    string errDetail = $"[通信拦截] 无法连接到节点！\n试图请求的地址: {targetUrl}/api/chat\n底层报错: {httpEx.Message}\n\n排查建议：\n1. 检查对应的皮皮虾节点是否已启动？\n2. 检查填写的 IP 和端口是否完全一致？\n3. 如果跨电脑访问，请检查防火墙。";

                    var errMsg = JsonSerializer.Serialize(new ChatResponse { type = "final", content = errDetail }, AppJsonContext.Default.ChatResponse);
                    await writer.WriteAsync(errMsg + "|||END|||");
                }
                catch (Exception ex)
                {
                    var errMsg = JsonSerializer.Serialize(new ChatResponse { type = "final", content = $"[未知异常] 代理层发生错误: {ex.Message}" }, AppJsonContext.Default.ChatResponse);
                    await writer.WriteAsync(errMsg + "|||END|||");
                }
            }
            // 6. 状态轮询转发 /api/status 与 历史记录转发 /api/history
            else if ((path == "/api/status" || path == "/api/history") && req.HttpMethod == "GET")
            {
                string username = Uri.UnescapeDataString(req.Headers["X-Username"] ?? "");
                if (!_config.PeerNodes.TryGetValue(username, out var nodeInfo) || string.IsNullOrEmpty(nodeInfo.Url))
                {
                    res.StatusCode = 404;
                    return;
                }

                try
                {
                    using var proxyReq = new HttpRequestMessage(HttpMethod.Get, nodeInfo.Url.TrimEnd('/') + path);
                    proxyReq.Headers.Add("X-Username", Uri.EscapeDataString(username));
                    using var proxyRes = await _httpClient.SendAsync(proxyReq);
                    proxyRes.EnsureSuccessStatusCode();

                    using var proxyStream = await proxyRes.Content.ReadAsStreamAsync();
                    res.ContentType = "application/json; charset=utf-8";
                    await proxyStream.CopyToAsync(res.OutputStream);
                }
                catch
                {
                    // 如果节点挂了，友好的返回一个离线状态
                    if (path == "/api/status")
                    {
                        var offlineResp = "{\"isWorking\":false, \"currentAction\":\"离线/失联\"}";
                        res.ContentType = "application/json; charset=utf-8";
                        await res.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(offlineResp));
                    }
                    else
                    {
                        res.StatusCode = 500;
                    }
                }
            }
            else if (path == "/api/clear" && req.HttpMethod == "POST")
            {
                string username = Uri.UnescapeDataString(req.Headers["X-Username"] ?? "");
                if (!_config.PeerNodes.TryGetValue(username, out var nodeInfo) || string.IsNullOrEmpty(nodeInfo.Url))
                {
                    res.StatusCode = 404;
                    return;
                }
                try
                {
                    using var proxyReq = new HttpRequestMessage(HttpMethod.Post, nodeInfo.Url.TrimEnd('/') + path);
                    proxyReq.Headers.Add("X-Username", Uri.EscapeDataString(username));
                    using var proxyRes = await _httpClient.SendAsync(proxyReq);
                    proxyRes.EnsureSuccessStatusCode();

                    res.ContentType = "application/json; charset=utf-8";
                    await res.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"status\":\"cleared\"}"));
                }
                catch (Exception ex)
                {
                    res.StatusCode = 500;
                    Console.WriteLine($"[清理异常] {ex.Message}");
                }
            }
            else if (path == "/api/clearall" && req.HttpMethod == "POST")
            {
                int successCount = 0;
                var tasks = new List<Task>();

                // 【新增】：找一个可用的节点，顺手把 "Team中控" 的记忆也彻底擦除
                string hrAgentUrl = _config.PeerNodes.Values.FirstOrDefault(n => !string.IsNullOrEmpty(n.Url))?.Url ?? "http://127.0.0.1:5050";
                tasks.Add(Task.Run(async () => {
                    try
                    {
                        using var proxyReq = new HttpRequestMessage(HttpMethod.Post, hrAgentUrl.TrimEnd('/') + "/api/clear");
                        proxyReq.Headers.Add("X-Username", Uri.EscapeDataString("Team中控"));
                        await _httpClient.SendAsync(proxyReq);
                    }
                    catch { /* 忽略异常 */ }
                }));

                // 原有的遍历员工清理逻辑保持不变
                foreach (var kvp in _config.PeerNodes)
                {
                    if (string.IsNullOrEmpty(kvp.Value.Url)) continue;

                    tasks.Add(Task.Run(async () => {
                        try
                        {
                            using var proxyReq = new HttpRequestMessage(HttpMethod.Post, kvp.Value.Url.TrimEnd('/') + "/api/clear");
                            proxyReq.Headers.Add("X-Username", Uri.EscapeDataString(kvp.Key));
                            using var proxyRes = await _httpClient.SendAsync(proxyReq);
                            if (proxyRes.IsSuccessStatusCode) Interlocked.Increment(ref successCount);
                        }
                        catch { /* 节点离线则忽略 */ }
                    }));
                }

                await Task.WhenAll(tasks);

                res.ContentType = "application/json; charset=utf-8";
                await res.OutputStream.WriteAsync(Encoding.UTF8.GetBytes($"{{\"status\":\"ok\", \"cleared\":{successCount}}}"));
            }
            else if (path == "/api/create_company" && req.HttpMethod == "POST")
            {
                string callAgentUrl = _config.PeerNodes.Values.FirstOrDefault(n => !string.IsNullOrEmpty(n.Url))?.Url ?? "http://127.0.0.1:5050";

                int successCount = 0;
                var tasks = new List<Task>();

                tasks.Add(Task.Run(async () => {
                    try
                    {
                        using var proxyReq = new HttpRequestMessage(HttpMethod.Post, callAgentUrl.TrimEnd('/') + "/api/clear");
                        proxyReq.Headers.Add("X-Username", Uri.EscapeDataString("Team中控"));
                        await _httpClient.SendAsync(proxyReq);
                    }
                    catch { /* 忽略 */ }
                }));

                foreach (var kvp in _config.PeerNodes)
                {
                    if (string.IsNullOrEmpty(kvp.Value.Url)) continue;
                    tasks.Add(Task.Run(async () => {
                        try
                        {
                            using var proxyReq = new HttpRequestMessage(HttpMethod.Post, kvp.Value.Url.TrimEnd('/') + "/api/clear");
                            proxyReq.Headers.Add("X-Username", Uri.EscapeDataString(kvp.Key));
                            using var proxyRes = await _httpClient.SendAsync(proxyReq);
                            if (proxyRes.IsSuccessStatusCode) Interlocked.Increment(ref successCount);
                        }
                        catch { /* 节点离线则忽略 */ }
                    }));
                }

                await Task.WhenAll(tasks);

                // 解析前端参数
                using var reader = new StreamReader(req.InputStream);
                string body = await reader.ReadToEndAsync();
                var reqData = JsonSerializer.Deserialize(body, typeof(CreateCompanyReq), AppJsonContext.Default) as CreateCompanyReq;

                if (reqData == null || string.IsNullOrEmpty(reqData.Description) || string.IsNullOrEmpty(reqData.MasterNodeUrl))
                {
                    res.StatusCode = 400;
                    return;
                }

                string targetUrl = reqData.MasterNodeUrl.Trim();


                // 👉 2. 把它塞给大模型，命令它用这个地址去改本地配置，最后再吐出带这个地址的 JSON
                var prompt = $@"老板下达了开设新公司的指令，业务描述：【{reqData.Description}】。
请你作为一个高级HR兼架构师，完成以下连串任务：
1. 编排一个包含不多于 21 个核心员工的团队，生成他们的名字和岗位头衔。
2. 注意：所有生成员工的 Url 必须全部统一填为 ""{targetUrl}""。
3. 调用你的本地工具，读取并修改你自己的 `appsettings.json`，把这些新员工信息补充到你的 `PeerNodes` 字典中。
4. 彻底修改完你自己的配置后，请在最终回复中，**只输出**一个合法的 JSON 数组，供中控台同步使用。绝不要有任何多余的废话和 Markdown 标记。
格式严格如下：
[
  {{ ""name"": ""员工姓名"", ""Role"": ""岗位头衔"", ""Description"": ""负责的具体能力与工作任务说明"", ""Url"": ""{targetUrl}"" }}
]
注意：json字段不能省略必须严谨
";



                // 构造给皮皮虾的请求，使用符合 AOT 的 ChatRequest 强类型
                var chatReq = new ChatRequest { message = prompt, modelIndex = 0 };
                using var agentReq = new HttpRequestMessage(HttpMethod.Post, callAgentUrl.TrimEnd('/') + "/api/chat");
                agentReq.Headers.Add("X-Username", Uri.EscapeDataString("Team中控"));

                // 严格使用 AOT 序列化
                agentReq.Content = new StringContent(JsonSerializer.Serialize(chatReq, typeof(ChatRequest), AppJsonContext.Default), Encoding.UTF8, "application/json");

                try
                {
                    using var agentRes = await _httpClient.SendAsync(agentReq);
                    agentRes.EnsureSuccessStatusCode();

                    var agentResStr = await agentRes.Content.ReadAsStringAsync();
                    string finalJson = "[]";

                    var parts = agentResStr.Split(new[] { "|||END|||" }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        try
                        {
                            var pushMsg = JsonSerializer.Deserialize(part, typeof(ChatResponse), AppJsonContext.Default) as ChatResponse;
                            if (pushMsg != null && pushMsg.type == "final" && !string.IsNullOrEmpty(pushMsg.content))
                            {
                                finalJson = pushMsg.content;
                            }
                        }
                        catch { }
                    }

                    // 暴力清洗大模型可能带上的 markdown 标记
                    finalJson = finalJson.Replace("```json", "").Replace("```", "").Trim();
                    int startIndex = finalJson.IndexOf('[');
                    int endIndex = finalJson.LastIndexOf(']');
                    if (startIndex >= 0 && endIndex > startIndex)
                    {
                        finalJson = finalJson.Substring(startIndex, endIndex - startIndex + 1);
                    }

                    // 👉 3. Team 中控解析大模型吐回来的终极 JSON，同步配置
                    var templates = JsonSerializer.Deserialize(finalJson, typeof(List<NodeInfoTemplate>), AppJsonContext.Default) as List<NodeInfoTemplate>;

                    if (templates != null && templates.Count > 0)
                    {
                        foreach (var t in templates)
                        {
                            if (!string.IsNullOrEmpty(t.name))
                            {
                                // 直接用大模型分配好的 url 和 role 同步进 Team
                                _config.PeerNodes[t.name] = new NodeInfo { Name = t.name, Url = t.Url, Role = t.Role, Description = t.Description };
                            }
                        }
                        // Team 把自己的 team_config.json 写盘
                        File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, typeof(AppConfig), AppJsonContext.Default), Encoding.UTF8);
                    }

                    res.ContentType = "application/json; charset=utf-8";
                    await res.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"status\":\"ok\"}"));
                }
                catch (Exception ex)
                {
                    res.StatusCode = 500;
                    Console.WriteLine($"[指派招人异常] {ex.Message}");
                }
            }
            else if (path == "/api/bankruptcy" && req.HttpMethod == "POST")
            {
                _config.PeerNodes.Clear(); 
                File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, typeof(AppConfig), AppJsonContext.Default), Encoding.UTF8);
                res.ContentType = "application/json; charset=utf-8";
                await res.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"status\":\"ok\"}"));
            }
            else
            {
                res.StatusCode = 404;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] 请求处理异常: {ex.Message}");
            res.StatusCode = 500;
        }
        finally
        {
            res.Close();
        }
    }

    // 完整且未作任何修改的前端 HTML 代码
    private const string HtmlContent = """
<!DOCTYPE html>
<html lang="zh-CN">

<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>皮皮虾公司办公室</title>
    <script src="https://cdn.jsdelivr.net/npm/marked/marked.min.js"></script>
    <style>
        body {
            font-family: 'Arial Rounded MT Bold', 'Helvetica Rounded', sans-serif;
            background-color: #ffffff;
            margin: 0;
            padding: 10px;
            display: flex;
            flex-direction: column;
            align-items: center;
        }

        .header {
            width: 100vw;
            box-sizing: border-box;
            text-align: center;
            margin-bottom: 30px;
            padding: 20px;
            background: rgba(255, 255, 255, 0.35); /* 半透明玻璃质感 */
            backdrop-filter: blur(10px);
            -webkit-backdrop-filter: blur(10px);
            box-shadow: 0 4px 15px rgba(0, 0, 0, 0.05);
            border-bottom: 1px solid rgba(255, 255, 255, 0.6);
            color: #4a3f35; /* 配合背景的深咖啡色 */
        }
        .header h1 {
            margin: 0 0 8px 0;
            font-size: 2.2rem;
            text-shadow: 1px 1px 2px rgba(255,255,255,0.8);
            letter-spacing: 2px;
        }
        .header p {
            margin: 0;
            font-size: 1.05rem;
            color: #5c4e40;
            font-weight: 600;
        }

        /* --- 新增: 顶部按钮专属样式 --- */
        .top-btn {
            padding: 10px 20px;
            border: none;
            border-radius: 8px;
            cursor: pointer;
            font-weight: bold;
            font-size: 0.95rem;
            transition: all 0.3s ease;
            box-shadow: 0 4px 10px rgba(0,0,0,0.1);
            display: inline-flex;
            align-items: center;
            gap: 6px;
        }
        .top-btn-clear {
            background: linear-gradient(135deg, #e67e22, #d35400); /* 暖橙渐变 */
            color: #fff;
        }
        .top-btn-clear:hover {
            transform: translateY(-2px);
            box-shadow: 0 6px 15px rgba(211, 84, 0, 0.3);
        }
        .top-btn-create {
            background: linear-gradient(135deg, #4a3f35, #2c251f); /* 沉稳深咖渐变 */
            color: #fff;
        }
        .top-btn-create:hover {
            transform: translateY(-2px);
            box-shadow: 0 6px 15px rgba(44, 37, 31, 0.35);
        }

        .desk-grid {
            display: grid;
            grid-template-columns: repeat(2, 1fr);
            gap: 10px;
            width: 100%;
            max-width: 1400px;
        }

        @media (min-width: 768px) {
            .desk-grid {
                grid-template-columns: repeat(auto-fill, minmax(180px, 1fr));
                gap: 20px;
            }
        }

        .company-card {
            position: relative;
            cursor: default;
            width: 100%;
            aspect-ratio: 4 / 5;
            border-radius: 10px;
        }

        .chat-bubble {
            position: absolute;
            top: 4%; /* 往下挪一点，使其自然悬浮在顶部 */
            left: 50%;
            transform: translateX(-50%);
            background: rgba(255, 255, 255, 0.95);
            border: 1px solid rgba(0, 0, 0, 0.05);
            border-radius: 14px;
            padding: 8px 14px;
            font-size: 12px;
            font-weight: 600;
            color: #475569;
            max-width: 90%;
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
            z-index: 100;
            box-shadow: 0 6px 20px rgba(0, 0, 0, 0.08);
            animation: bubbleFloat 3s ease-in-out infinite;
            transition: all 0.3s ease;
        }

        .chat-bubble::after {
            content: '';
            position: absolute;
            bottom: -6px;
            left: 50%;
            transform: translateX(-50%);
            border-width: 6px 6px 0;
            border-style: solid;
            border-color: rgba(255, 255, 255, 0.95) transparent transparent;
            transition: border-color 0.3s ease;
        }

        /* 工作中/思考中的现代科技蓝 */
        .chat-bubble.thinking {
            background: linear-gradient(135deg, #f0f4ff, #e6edfa);
            color: #3b82f6; 
            border: 1px solid #dbeafe;
            box-shadow: 0 6px 16px rgba(59, 130, 246, 0.15);
            animation: bubblePulse 1s infinite alternate;
        }
        .chat-bubble.thinking::after {
            border-color: #e6edfa transparent transparent;
        }

        /* 任务完成后的现代暖橙 */
        .chat-bubble.done {
            background: linear-gradient(135deg, #fdfbf7, #fff8f1);
            color: #f59e0b;
            border: 1px solid #fef3c7;
            box-shadow: 0 6px 16px rgba(245, 158, 11, 0.15);
            cursor: pointer;
        }
        .chat-bubble.done::after {
            border-color: #fff8f1 transparent transparent;
        }

        @keyframes bubbleFloat {
            0%, 100% { transform: translate(-50%, 0); }
            50% { transform: translate(-50%, -5px); }
        }
        @keyframes bubblePulse {
            0% { transform: translate(-50%, 0) scale(1); box-shadow: 0 0 10px rgba(59,130,246,0.1); }
            100% { transform: translate(-50%, -4px) scale(1.05); box-shadow: 0 6px 20px rgba(59,130,246,0.3); }
        }

        /* ----------------------- */

        .desk-img-container {
            width: 100%;
            height: 100%;
            position: absolute;
            top: 0;
            left: 0;
            display: flex;
            justify-content: center;
            align-items: center;
            border-radius: 10px;
        }

        .shrimp-desk-img,
        .empty-desk-img {
            display: block;
            width: 90%;
            height: auto;
            position: absolute;
            top: 50%;
            left: 50%;
            transform: translate(-50%, -50%);
        }

        .id-card-area {
            position: absolute;
            bottom: 30%;
            right: 25%;
            width: 20%;
            height: 10%;
            display: flex;
            flex-direction: column;
            justify-content: center;
            align-items: flex-end;
            z-index: 10;
        }

        .id-card-name {
            font-size: clamp(8px, 2vw, 12px);
            font-weight: bold;
            color: #000000;
            margin: 0;
            line-height: 1.2;
            white-space: nowrap;
            text-align: right;
        }

        .id-card-role {
            font-size: clamp(6px, 1.5vw, 9px);
            color: #555555;
            margin: 0;
            line-height: 1.2;
            text-align: left;
            white-space: nowrap;
        }

        .company-card.at-work .shrimp-desk-img {
            display: block;
        }

        .company-card.at-work .empty-desk-img {
            display: none;
        }

        .company-card.away .shrimp-desk-img {
            display: none;
        }

        .company-card.away .empty-desk-img {
            display: block;
        }

        .company-card.empty-desk .shrimp-desk-img {
            display: none;
        }

        .company-card.empty-desk .empty-desk-img {
            display: block;
        }

        .away .id-card-area,
        .empty-desk .id-card-area,
        .away .chat-bubble,
        .empty-desk .chat-bubble {
            display: none;
        }
        @keyframes bubbleFloat {
            0%, 100% { transform: translate(-50%, 0); }
            50% { transform: translate(-50%, -5px); }
        }

        .chat-bubble.thinking {
            background: #e0f7fa;
            color: #006064;
            box-shadow: 0 4px 15px rgba(0, 172, 193, 0.2);
            animation: bubblePulse 1s infinite alternate;
        }
        .chat-bubble.thinking::after {
            border-color: #e0f7fa transparent transparent; /* 尾巴颜色同步 */
        }
        @keyframes bubblePulse {
            0% { transform: translate(-50%, 0) scale(1); box-shadow: 0 0 5px rgba(0,172,193,0.2); }
            100% { transform: translate(-50%, -4px) scale(1.05); box-shadow: 0 0 15px rgba(0,172,193,0.5); }
        }

        .chat-bubble.done {
            background: #fff3e0;
            color: #e65100;
            cursor: pointer;
            box-shadow: 0 4px 15px rgba(255, 152, 0, 0.2);
            display: flex;
            align-items: center;
            gap: 6px; 
        }
        .chat-bubble.done::after {
            border-color: #fff3e0 transparent transparent; /* 尾巴颜色同步 */
        }
        .chat-bubble.done:hover {
            background: #ffe0b2;
            transform: translate(-50%, -2px) scale(1.05);
        }
        .chat-bubble.done::before {
            content: '';
            display: block;
            width: 8px;
            height: 8px;
            background: #f44336;
            border-radius: 50%;
            box-shadow: 0 0 4px rgba(244,67,54,0.6);
            animation: blink 1s infinite;
            flex-shrink: 0;
        }
        @keyframes blink { 50% { opacity: 0.4; } }

        .report-badge {
            display: none; /* 初始隐藏，有报告后显示 */
            position: absolute;
            bottom: 8px; /* 绝对定位：距离底部8px */
            left: 8px;   /* 绝对定位：距离左侧8px */
            z-index: 20; /* 确保显示在图片上方 */
            background: #e3f2fd;
            color: #1976d2;
            border: 1px solid #bbdefb;
            padding: 3px 8px;
            border-radius: 12px;
            font-size: 10px;
            font-weight: bold;
            cursor: pointer;
            transition: all 0.2s ease;
            pointer-events: auto; /* 允许点击 */
            box-shadow: 0 2px 5px rgba(0,0,0,0.15);
        }
        .report-badge:hover {
            background: #bbdefb;
            transform: scale(1.05);
        }


        /* --- 新增：记事本报告模态框样式 --- */
        #reportModal {
            display: none; position: fixed; inset: 0; background: rgba(0,0,0,0.6);
            z-index: 10000; align-items: center; justify-content: center; backdrop-filter: blur(5px);
        }
        .report-content {
            background: #fff; width: 90%; max-width: 800px; height: 80vh;
            border-radius: 12px; display: flex; flex-direction: column; overflow: hidden;
            box-shadow: 0 10px 30px rgba(0,0,0,0.3);
        }
        .report-header {
            padding: 15px 20px; background: #f8f9fa; border-bottom: 1px solid #eee;
            display: flex; justify-content: space-between; align-items: center;
        }
        .report-body {
            padding: 20px; overflow-y: auto; flex: 1; font-family: sans-serif; line-height: 1.6; color: #333;
        }
        .report-body pre { background: #282c34; color: #abb2bf; padding: 12px; border-radius: 6px; overflow-x: auto; }
        .report-body code { font-family: Consolas, monospace; }
        .report-body table { border-collapse: collapse; width: 100%; margin-bottom: 1em; }
        .report-body th, .report-body td { border: 1px solid #ddd; padding: 8px; }
        .report-body th { background-color: #f2f2f2; }

        .desk-penzai {
            position: absolute;
            bottom: 0%;
            left: -5%;  
            width: 28%;
            z-index: 12;
            pointer-events: none; 
            filter: drop-shadow(2px 2px 4px rgba(0,0,0,0.15));
            transition: transform 0.3s ease;
        }
        .company-card:hover .desk-penzai {
            transform: scale(1.05) rotate(-2deg);
        }

        .desk-penzai-2 {
            position: absolute;
            top: 20%;
            right: 0%;
            width: 28%;
            z-index: 12;
            pointer-events: none; 
            filter: drop-shadow(2px 2px 4px rgba(0,0,0,0.15));
            transition: transform 0.3s ease;
        }
        .company-card:hover .desk-penzai-2 {
            transform: scale(1.05) rotate(-2deg);
        }
    </style>
</head>

<body style="margin:0; padding:0; width:100vw; height:100vh; background-color:#ab9980; ">
    
    <div class="header">
        <h1>🏢 皮皮虾公司办公室</h1>
        <p>欢迎回来！这是您的团队。点击空位可以招募新🦐入职。</p>
        <div style="display: flex; justify-content: center; gap: 15px; margin-top: 15px;">
            <button onclick="clearAllMemory()" class="top-btn top-btn-clear">🧹 一键清空所有员工记忆</button>
            <button onclick="openCreateCompanyModal()" class="top-btn top-btn-create">🚀 一键开设公司</button>
            <button onclick="bankruptcy()" class="top-btn" style="background: linear-gradient(135deg, #c0392b, #8e44ad); color:#fff;">💥 一键破产</button>
        </div>
    </div>

    <div class="desk-grid">
        <div class="company-card empty-desk">
            <div class="desk-img-container">
                <img src="img_shrimp_working.png" alt="皮皮虾办公" class="shrimp-desk-img">
                <img src="img_empty_desk.png" alt="空桌子" class="empty-desk-img">
                <img src="penzai2.png" class="desk-penzai">
                <img src="penzai1.png" class="desk-penzai-2">
            </div>
        </div>
        <div class="company-card empty-desk">
            <div class="desk-img-container">
                <img src="img_shrimp_working.png" alt="皮皮虾办公" class="shrimp-desk-img">
                <img src="img_empty_desk.png" alt="空桌子" class="empty-desk-img">
                <img src="penzai2.png" class="desk-penzai">
                <img src="penzai1.png" class="desk-penzai-2">
            </div>
        </div>
        <div class="company-card empty-desk">
            <div class="desk-img-container">
                <img src="img_shrimp_working.png" alt="皮皮虾办公" class="shrimp-desk-img">
                <img src="img_empty_desk.png" alt="空桌子" class="empty-desk-img">
                <img src="penzai2.png" class="desk-penzai">
                <img src="penzai1.png" class="desk-penzai-2">
            </div>
        </div>
        <div class="company-card empty-desk">
            <div class="desk-img-container">
                <img src="img_shrimp_working.png" alt="皮皮虾办公" class="shrimp-desk-img">
                <img src="img_empty_desk.png" alt="空桌子" class="empty-desk-img">
                <img src="penzai2.png" class="desk-penzai">
                <img src="penzai1.png" class="desk-penzai-2">
            </div>
        </div>
        <div class="company-card empty-desk">
            <div class="desk-img-container">
                <img src="img_shrimp_working.png" alt="皮皮虾办公" class="shrimp-desk-img">
                <img src="img_empty_desk.png" alt="空桌子" class="empty-desk-img">
                <img src="penzai2.png" class="desk-penzai">
                <img src="penzai1.png" class="desk-penzai-2">
            </div>
        </div>
        <div class="company-card empty-desk">
            <div class="desk-img-container">
                <img src="img_shrimp_working.png" alt="皮皮虾办公" class="shrimp-desk-img">
                <img src="img_empty_desk.png" alt="空桌子" class="empty-desk-img">
                <img src="penzai2.png" class="desk-penzai">
                <img src="penzai1.png" class="desk-penzai-2">
            </div>
        </div>
        <div class="company-card empty-desk">
            <div class="desk-img-container">
                <img src="img_shrimp_working.png" alt="皮皮虾办公" class="shrimp-desk-img">
                <img src="img_empty_desk.png" alt="空桌子" class="empty-desk-img">
                <img src="penzai2.png" class="desk-penzai">
                <img src="penzai1.png" class="desk-penzai-2">
            </div>
        </div>
        <div class="company-card empty-desk">
            <div class="desk-img-container">
                <img src="img_shrimp_working.png" alt="皮皮虾办公" class="shrimp-desk-img">
                <img src="img_empty_desk.png" alt="空桌子" class="empty-desk-img">
                <img src="penzai2.png" class="desk-penzai">
                <img src="penzai1.png" class="desk-penzai-2">
            </div>
        </div>
        <div class="company-card empty-desk">
            <div class="desk-img-container">
                <img src="img_shrimp_working.png" alt="皮皮虾办公" class="shrimp-desk-img">
                <img src="img_empty_desk.png" alt="空桌子" class="empty-desk-img">
                <img src="penzai2.png" class="desk-penzai">
                <img src="penzai1.png" class="desk-penzai-2">
            </div>
        </div>
        <div class="company-card empty-desk">
            <div class="desk-img-container">
                <img src="img_shrimp_working.png" alt="皮皮虾办公" class="shrimp-desk-img">
                <img src="img_empty_desk.png" alt="空桌子" class="empty-desk-img">
                <img src="penzai2.png" class="desk-penzai">
                <img src="penzai1.png" class="desk-penzai-2">
            </div>
        </div>
        <div class="company-card empty-desk">
            <div class="desk-img-container">
                <img src="img_shrimp_working.png" alt="皮皮虾办公" class="shrimp-desk-img">
                <img src="img_empty_desk.png" alt="空桌子" class="empty-desk-img">
                <img src="penzai2.png" class="desk-penzai">
                <img src="penzai1.png" class="desk-penzai-2">
            </div>
        </div>
        <div class="company-card empty-desk">
            <div class="desk-img-container">
                <img src="img_shrimp_working.png" alt="皮皮虾办公" class="shrimp-desk-img">
                <img src="img_empty_desk.png" alt="空桌子" class="empty-desk-img">
                <img src="penzai2.png" class="desk-penzai">
                <img src="penzai1.png" class="desk-penzai-2">
            </div>
        </div>
        <div class="company-card empty-desk">
            <div class="desk-img-container">
                <img src="img_shrimp_working.png" alt="皮皮虾办公" class="shrimp-desk-img">
                <img src="img_empty_desk.png" alt="空桌子" class="empty-desk-img">
                <img src="penzai2.png" class="desk-penzai">
                <img src="penzai1.png" class="desk-penzai-2">
            </div>
        </div>
        <div class="company-card empty-desk">
            <div class="desk-img-container">
                <img src="img_shrimp_working.png" alt="皮皮虾办公" class="shrimp-desk-img">
                <img src="img_empty_desk.png" alt="空桌子" class="empty-desk-img">
                <img src="penzai2.png" class="desk-penzai">
                <img src="penzai1.png" class="desk-penzai-2">
            </div>
        </div>
        <div class="company-card empty-desk">
            <div class="desk-img-container">
                <img src="img_shrimp_working.png" alt="皮皮虾办公" class="shrimp-desk-img">
                <img src="img_empty_desk.png" alt="空桌子" class="empty-desk-img">
                <img src="penzai2.png" class="desk-penzai">
                <img src="penzai1.png" class="desk-penzai-2">
            </div>
        </div>
        <div class="company-card empty-desk">
            <div class="desk-img-container">
                <img src="img_shrimp_working.png" alt="皮皮虾办公" class="shrimp-desk-img">
                <img src="img_empty_desk.png" alt="空桌子" class="empty-desk-img">
                <img src="penzai2.png" class="desk-penzai">
                <img src="penzai1.png" class="desk-penzai-2">
            </div>
        </div>
        <div class="company-card empty-desk">
            <div class="desk-img-container">
                <img src="img_shrimp_working.png" alt="皮皮虾办公" class="shrimp-desk-img">
                <img src="img_empty_desk.png" alt="空桌子" class="empty-desk-img">
                <img src="penzai2.png" class="desk-penzai">
                <img src="penzai1.png" class="desk-penzai-2">
            </div>
        </div>
        <div class="company-card empty-desk">
            <div class="desk-img-container">
                <img src="img_shrimp_working.png" alt="皮皮虾办公" class="shrimp-desk-img">
                <img src="img_empty_desk.png" alt="空桌子" class="empty-desk-img">
                <img src="penzai2.png" class="desk-penzai">
                <img src="penzai1.png" class="desk-penzai-2">
            </div>
        </div>
        <div class="company-card empty-desk">
            <div class="desk-img-container">
                <img src="img_shrimp_working.png" alt="皮皮虾办公" class="shrimp-desk-img">
                <img src="img_empty_desk.png" alt="空桌子" class="empty-desk-img">
                <img src="penzai2.png" class="desk-penzai">
                <img src="penzai1.png" class="desk-penzai-2">
            </div>
        </div>
        <div class="company-card empty-desk">
            <div class="desk-img-container">
                <img src="img_shrimp_working.png" alt="皮皮虾办公" class="shrimp-desk-img">
                <img src="img_empty_desk.png" alt="空桌子" class="empty-desk-img">
                <img src="penzai2.png" class="desk-penzai">
                <img src="penzai1.png" class="desk-penzai-2">
            </div>
        </div>
        <div class="company-card empty-desk">
            <div class="desk-img-container">
                <img src="img_shrimp_working.png" alt="皮皮虾办公" class="shrimp-desk-img">
                <img src="img_empty_desk.png" alt="空桌子" class="empty-desk-img">
                <img src="penzai2.png" class="desk-penzai">
                <img src="penzai1.png" class="desk-penzai-2">
            </div>
        </div>



    </div>
    
    <div id="recruitModal"
        style="display:none; position:fixed; inset:0; background:rgba(0,0,0,0.6); z-index:9999; align-items:center; justify-content:center; backdrop-filter:blur(3px);">
        <div
            style="background:#fff; padding:25px; border-radius:15px; width:300px; display:flex; flex-direction:column; gap:12px; box-shadow:0 10px 30px rgba(0,0,0,0.2);">
            <h3 style="margin:0 0 5px 0; text-align:center; color:#333;">🤝 招募新员工</h3>
            <input type="text" id="recruitName" placeholder="员工姓名 (如: 铁柱)"
                style="padding:10px; border:1px solid #ddd; border-radius:8px; outline:none; font-weight:bold;">
            <input type="text" id="recruitRole" placeholder="岗位头衔 (如: 爬虫工程师)"
                style="padding:10px; border:1px solid #ddd; border-radius:8px; outline:none;">
            <input type="text" id="recruitDesc" placeholder="能力说明 (如: 负责网络数据抓取)"
                style="padding:10px; border:1px solid #ddd; border-radius:8px; outline:none; font-size:12px;">
            <input type="text" id="recruitUrl" placeholder="节点URL (如: http://192.168.1.10:5050)"
                style="padding:10px; border:1px solid #ddd; border-radius:8px; outline:none; font-size:12px;">
            <div style="display:flex; gap:10px; margin-top:10px;">
                <button onclick="confirmRecruit()"
                    style="flex:1; padding:10px; background:#333; color:#fff; border:none; border-radius:8px; cursor:pointer; font-weight:bold;">办理入职</button>
                <button onclick="closeModal('recruitModal')"
                    style="flex:1; padding:10px; background:#eee; color:#666; border:none; border-radius:8px; cursor:pointer;">取消</button>
            </div>
        </div>
    </div>
<div id="createCompanyModal"
        style="display:none; position:fixed; inset:0; background:rgba(0,0,0,0.6); z-index:10000; align-items:center; justify-content:center; backdrop-filter:blur(3px);">
        <div style="background:#fff; padding:25px; border-radius:15px; width:340px; display:flex; flex-direction:column; gap:12px; box-shadow:0 10px 30px rgba(0,0,0,0.2);">
            <h3 style="margin:0 0 5px 0; text-align:center; color:#333;">🏢 一键开设公司</h3>
            
            <textarea id="companyDesc" placeholder="请输入公司愿景或业务描述..."
                style="padding:10px; border:1px solid #ddd; border-radius:8px; outline:none; height:80px; resize:none; font-family:inherit;"></textarea>

            <input type="text" id="masterNodeUrl" placeholder="要分配的统一地址 (默认: http://127.0.0.1:5050)"
                style="padding:10px; border:1px solid #ddd; border-radius:8px; outline:none; font-size:13px; font-family:monospace;">
            
            <div style="display:flex; gap:10px; margin-top:10px;">
                <button id="btnConfirmCreate" onclick="confirmCreateCompany()"
                    style="flex:1; padding:10px; background:#2980b9; color:#fff; border:none; border-radius:8px; cursor:pointer; font-weight:bold;">开始入职</button>
                <button onclick="closeModal('createCompanyModal')"
                    style="flex:1; padding:10px; background:#eee; color:#666; border:none; border-radius:8px; cursor:pointer;">取消</button>
            </div>
        </div>
    </div>
    <div id="reportModal" onclick="if(event.target===this) closeModal('reportModal')">
        <div class="report-content">
            <div class="report-header">
                <h3 id="reportTitle" style="margin:0; color:#333;">📝 工作报告</h3>
                <button onclick="closeModal('reportModal')" style="border:none; background:none; font-size:20px; cursor:pointer; color:#666;">✖</button>
            </div>
            <div class="report-body" id="reportMarkdown"></div>
        </div>
    </div>
    <div id="taskModal"
        style="display:none; position:fixed; inset:0; background:rgba(0,0,0,0.6); z-index:9999; align-items:center; justify-content:center; backdrop-filter:blur(3px);">
        <div
            style="background:#fff; padding:25px; border-radius:15px; width:340px; display:flex; flex-direction:column; gap:12px; box-shadow:0 10px 30px rgba(0,0,0,0.2);">
            <h3 id="taskModalTitle" style="margin:0 0 5px 0; text-align:center; color:#333;">给员工派发任务</h3>
            <textarea id="taskInput" placeholder="请输入具体的需求指令..."
                style="padding:10px; border:1px solid #ddd; border-radius:8px; height:90px; resize:none; outline:none; font-family:inherit;"></textarea>
            
            <div style="display:flex; gap:8px; margin-top:10px;">
                <button onclick="confirmTask()"
                    style="flex:2; padding:10px; background:#4a90e2; color:#fff; border:none; border-radius:8px; cursor:pointer; font-weight:bold;">开始干活</button>
                <button onclick="clearEmployeeMemory()"
                    style="flex:1.5; padding:10px; background:#f39c12; color:#fff; border:none; border-radius:8px; cursor:pointer; font-weight:bold;">清空记忆</button>
            </div>
            <div style="display:flex; gap:8px; margin-top:4px;">
                <button onclick="editEmployee()"
                        style="flex:1; padding:10px; background:#2ecc71; color:#fff; border:none; border-radius:8px; cursor:pointer; font-weight:bold;">修改信息</button>
                <button onclick="fireEmployee()"
                    style="flex:1; padding:10px; background:#e74c3c; color:#fff; border:none; border-radius:8px; cursor:pointer; font-weight:bold;">开除员工</button>
                <button onclick="closeModal('taskModal')"
                    style="flex:1; padding:10px; background:#eee; color:#666; border:none; border-radius:8px; cursor:pointer;">取消关闭</button>
            </div>
        </div>
    </div>

    <script>
        let currentTargetDesk = null;
        let currentTargetName = '';
        const deskReports = {};
        // 统一关闭弹窗
        function closeModal(id) {
            document.getElementById(id).style.display = 'none';
        }

        // 初始化：为所有工位绑定点击事件
        document.querySelectorAll('.company-card').forEach(desk => {
            desk.style.cursor = 'pointer'; // 增加点击手势反馈
            desk.addEventListener('click', () => {
                // 如果是空工位或离线状态 -> 招人
                if (desk.classList.contains('empty-desk') || desk.classList.contains('away')) {
                    currentTargetDesk = desk;
                    document.getElementById('recruitName').value = '';
                    document.getElementById('recruitRole').value = '';
                    document.getElementById('recruitUrl').value = '';
                    document.getElementById('recruitModal').style.display = 'flex';
                }
                // 如果是在岗状态 -> 派发任务 或 开除
                else if (desk.classList.contains('at-work')) {
                    currentTargetDesk = desk;
                    currentTargetName = desk.querySelector('.id-card-name').innerText;
                    document.getElementById('taskModalTitle').innerText = `管理员工：${currentTargetName}`;
                    document.getElementById('taskInput').value = '';
                    document.getElementById('taskModal').style.display = 'flex';
                }
            });
        });

        // 页面加载完成后自动执行
        window.addEventListener('load', async () => {
            try {
                const res = await fetch('/api/config');
                if (!res.ok) return;
                const cfg = await res.json();

                if (cfg.PeerNodes) {
                    const desks = document.querySelectorAll('.company-card');
                    let index = 0;

                    // 遍历保存的通讯录，依次填入工位
                    for (const [name, info] of Object.entries(cfg.PeerNodes)) {
                        if (index < desks.length) {
                            const role = info.Role || info.role || '资深员工';
                            renderEmployeeUI(desks[index], name, role);
                            index++;
                        }
                    }
                    console.log(`[初始化] 已成功加载 ${index} 位员工。`);
                }
            } catch (e) {
                console.error("加载配置失败:", e);
            }
        });

        async function clearAllMemory() {
            if (!confirm("老板，确定要一键清空【所有在职员工】的上下文记忆吗？")) return;
            try {
                const res = await fetch('/api/clearall', { method: 'POST' });
                if (res.ok) {
                    const data = await res.json();
                    alert(`✅ 成功擦除了 ${data.cleared} 名皮皮虾的记忆！`);
                }
            } catch (e) {
                alert("❌ 请求异常，请检查控制台。");
            }
        }

        let isEditing = false;
        let originalEditName = "";

        function openCreateCompanyModal() {
            document.getElementById('companyDesc').value = '';
            document.getElementById('masterNodeUrl').value = localStorage.getItem('temp_master_url') || 'http://127.0.0.1:5050';
            document.getElementById('createCompanyModal').style.display = 'flex';
        }

        async function confirmCreateCompany() {
            const desc = document.getElementById('companyDesc').value.trim();
            const masterUrl = document.getElementById('masterNodeUrl').value.trim() || 'http://127.0.0.1:5050';

            if (!desc) return alert("请先描述业务！");
            
            localStorage.setItem('temp_master_url', masterUrl);

            const btn = document.getElementById('btnConfirmCreate');
            const originalText = btn.innerText;
            btn.innerText = "安排入职中...";
            btn.disabled = true;

            try {
                const res = await fetch('/api/create_company', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    // 把业务描述和这个唯一的地址传给后端
                    body: JSON.stringify({ description: desc, masterNodeUrl: masterUrl })
                });

                if (res.ok) {
                    alert("🎉 招募完毕，员工已入职！");
                    location.reload(); 
                } else {
                    alert("❌ 失败！请检查节点是否存活。");
                }
            } catch (e) {
                console.error(e);
                alert("❌ 网络请求异常。");
            } finally {
                btn.innerText = originalText;
                btn.disabled = false;
            }
        }

        function editEmployee() {
            closeModal('taskModal');
            isEditing = true;
            originalEditName = currentTargetName;

            fetch('/api/config').then(r => r.json()).then(cfg => {
                const nodeInfo = cfg.PeerNodes[currentTargetName];
                if(nodeInfo) {
                    document.getElementById('recruitName').value = currentTargetName;
                    // 🌟 兼容大小写读取属性
                    document.getElementById('recruitRole').value = nodeInfo.Role || nodeInfo.role || '';
                    document.getElementById('recruitDesc').value = nodeInfo.Description || nodeInfo.description || '';
                    document.getElementById('recruitUrl').value = nodeInfo.Url || nodeInfo.url || '';

                    document.getElementById('recruitModal').querySelector('h3').innerText = '📝 修改员工信息';
                    document.getElementById('recruitModal').style.display = 'flex';
                }
            });
        }




        // 封装：将指定的工位 DOM 元素渲染为在职员工状态
        function renderEmployeeUI(deskElement, name, role) {
            deskElement.className = 'company-card at-work';

            // 生成唯一 deskId 绑定在 DOM 上，用于关联报告
            const deskId = 'desk_' + Math.random().toString(36).substr(2, 9);
            deskElement.dataset.deskId = deskId;

            // 【核心修复1】：把 report-badge 移到 desk-img-container 内部
            deskElement.innerHTML = `
                <div class="chat-bubble" onclick="openReportFromBubble(event, this)">摸鱼中...</div>
                <div class="desk-img-container">
                    <img src="img_shrimp_working.png" alt="皮皮虾办公" class="shrimp-desk-img">
                    <img src="img_empty_desk.png" alt="空桌子" class="empty-desk-img">
                    <img src="penzai2.png" class="desk-penzai">
                    <img src="penzai1.png" class="desk-penzai-2">
                    <div class="report-badge" onclick="openReportById(event, '${deskId}', '${name}')">📄 最新报告</div>
                </div>
                <div class="id-card-area" style="pointer-events: none;"> 
                    <p class="id-card-name">${name}</p>
                    <p class="id-card-role">${role}</p>
                </div>
            `;
        }

        // 气泡专属的查看事件（看完会消除红点）
        function openReportFromBubble(event, bubbleElem) {
            event.stopPropagation(); // 阻止点击工位
            if (!bubbleElem.classList.contains('done')) return; 

            const deskCard = bubbleElem.closest('.company-card');
            const deskId = deskCard.dataset.deskId;
            const empName = deskCard.querySelector('.id-card-name').innerText;

            showReportModal(deskId, empName);

            // 查看后恢复气泡的摸鱼状态（红点消失）
            bubbleElem.classList.remove('done');
            bubbleElem.innerText = '继续摸鱼...';
        }

        // 常驻按钮专属的查看事件（无限次重复打开）
        function openReportById(event, deskId, empName) {
            event.stopPropagation(); // 阻止触发重新分配任务
            showReportModal(deskId, empName);
        }

        setInterval(async () => {
            const desks = document.querySelectorAll('.company-card.at-work');
            for (const desk of desks) {
                const name = desk.querySelector('.id-card-name').innerText;
                const bubble = desk.querySelector('.chat-bubble');
                if (!name || !bubble) continue;

                try {
                    const res = await fetch('/api/status', {
                        method: 'GET',
                        headers: { 'X-Username': encodeURIComponent(name) }
                    });

                        if (res.ok) {
                        const data = await res.json();
                        if (data.isWorking) {
                            bubble.classList.remove('done');
                            bubble.classList.add('thinking');

                            const actionText = data.currentAction || '正在思考决策中...';
                            // 加 Emoji 图标
                            bubble.innerText = actionText.includes('思考') ? actionText : '⚙️ ' + actionText;
                            // 【新增这一行】：给 DOM 加上 title 属性，截断的参数可以通过鼠标悬停看见全文
                            bubble.title = actionText; 

                            const reportBadge = desk.querySelector('.report-badge');
                            if (reportBadge) reportBadge.style.display = 'none';
                        } else {
                            const startTime = parseInt(desk.dataset.taskStartTime || '0');
                            if (Date.now() - startTime < 3000) {
                                continue; // 跳过当前这个工位，检查下一个
                            }
                            // 空闲状态：如果之前是在 working，说明刚干完活
                            if (bubble.classList.contains('thinking')) {
                                bubble.classList.remove('thinking');
                                bubble.classList.add('done');
                                bubble.innerText = '老板，我干完活了！(点我查看)';

                                const reportBadge = desk.querySelector('.report-badge');
                                if (reportBadge) reportBadge.style.display = 'block';
                            } 
                            // 如果本来就在摸鱼，更新离线或摸鱼文案
                            else if (!bubble.classList.contains('done')) {
                                bubble.innerText = data.currentAction || '摸鱼中...';
                            }
                        }
                    }
                } catch (e) {
                    // 节点不通
                    bubble.classList.remove('thinking', 'done');
                    bubble.innerText = '❌ 离线/失联';
                }
            }
        }, 2000);


        // 统一的报告渲染与弹窗逻辑
        async function showReportModal(deskId, empName) {
            document.getElementById('reportTitle').innerText = `📝 【${empName}】的工作报告`;
            document.getElementById('reportMarkdown').innerHTML = `<div style="text-align:center; padding: 20px;">正在向工位拉取最新日志...</div>`;
            document.getElementById('reportModal').style.display = 'flex';

            try {
                const res = await fetch('/api/history', {
                    headers: { 'X-Username': encodeURIComponent(empName) }
                });

                if (res.ok) {
                    const history = await res.json();
                    let reportContent = "该员工当前没有任何工作记录。";

                    if (history && history.length > 0) {
                        // 找到最后一条 AI 的回复当做总结报告
                        const lastMsg = history.slice().reverse().find(m => m.role === 'assistant' && m.content);
                        if (lastMsg) reportContent = lastMsg.content;
                    }

                    if (typeof marked !== 'undefined') {
                        document.getElementById('reportMarkdown').innerHTML = marked.parse(reportContent);
                    } else {
                        document.getElementById('reportMarkdown').innerHTML = `<pre style="white-space:pre-wrap;">${reportContent}</pre>`;
                    }
                } else {
                    document.getElementById('reportMarkdown').innerHTML = `拉取失败：节点无响应`;
                }
            } catch (e) {
                document.getElementById('reportMarkdown').innerHTML = `拉取异常：无法连接到底层节点`;
            }
        }

        async function confirmRecruit() {
            const name = document.getElementById('recruitName').value.trim();
            const role = document.getElementById('recruitRole').value.trim() || '新晋员工';
            const desc = document.getElementById('recruitDesc').value.trim() || '暂无说明';
            const url = document.getElementById('recruitUrl').value.trim();

            if (!name) return alert("员工姓名不能为空！");
            renderEmployeeUI(currentTargetDesk, name, role);

            try {
                let res = await fetch('/api/config');
                let cfg = await res.json();
                if (!cfg.PeerNodes) cfg.PeerNodes = {};

                if (isEditing && originalEditName !== name && cfg.PeerNodes[originalEditName]) {
                    delete cfg.PeerNodes[originalEditName];
                }

                // 重点：大写属性同步给后端
                cfg.PeerNodes[name] = { Url: url, Role: role, Description: desc }; 

                await fetch('/api/config', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(cfg)
                });
            } catch (e) {
                console.error("同步失败:", e);
            }

            isEditing = false;
            document.getElementById('recruitModal').querySelector('h3').innerText = '🤝 招募新员工';
            closeModal('recruitModal');
        }
        async function bankruptcy() {
            if (!confirm("老板，真的要【一键破产】吗？这会解散所有员工、清空公司，且无法恢复！")) return;
            try {
                const res = await fetch('/api/bankruptcy', { method: 'POST' });
                if (res.ok) {
                    alert("💥 公司已破产，所有员工已被遣散归乡！");
                    location.reload(); 
                }
            } catch (e) {
                alert("❌ 操作失败。");
            }
        }
        // 【新增逻辑】：确认开除员工流程
        async function fireEmployee() {
            if (!currentTargetDesk || !currentTargetName) return;

            // 增加二次确认防误触
            if (!confirm(`老板，确认要开除【${currentTargetName}】吗？工位腾出后可以招募新人。`)) {
                return;
            }

            // 1. 同步到后端删除该节点配置 (与招人逻辑呼应)
            try {
                let res = await fetch('/api/config');
                if (res.ok) {
                    let cfg = await res.json();
                    if (cfg.PeerNodes && cfg.PeerNodes[currentTargetName]) {
                        delete cfg.PeerNodes[currentTargetName]; // 从通讯录中抹除
                        await fetch('/api/config', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify(cfg)
                        });
                        console.log(`[配置同步] 员工 ${currentTargetName} 已从中控系统移除。`);
                    }
                }
            } catch (e) {
                console.warn("同步删除配置失败，但前端 UI 会继续执行开除操作。", e);
            }

            // 2. 将卡片状态重置为空工位状态
            currentTargetDesk.className = 'company-card empty-desk';
            currentTargetDesk.innerHTML = `
                <div class="desk-img-container">
                    <img src="img_shrimp_working.png" alt="皮皮虾办公" class="shrimp-desk-img">
                    <img src="img_empty_desk.png" alt="空桌子" class="empty-desk-img">
                </div>
            `;

            closeModal('taskModal');
        }
        async function clearEmployeeMemory() {
            if (!currentTargetDesk || !currentTargetName) return;

            // 二次确认，防止误触
            if (!confirm(`老板，确定要彻底清空【${currentTargetName}】的上下文记忆吗？\n清空后 ta 将变成一张白纸。`)) {
                return;
            }

            try {
                const res = await fetch('/api/clear', {
                    method: 'POST',
                    headers: {
                        'X-Username': encodeURIComponent(currentTargetName)
                    }
                });

                if (res.ok) {
                    alert(`✅ 已成功擦除【${currentTargetName}】的记忆！可以安排新任务了。`);
                    closeModal('taskModal');
                } else {
                    alert(`❌ 清除失败，节点可能不在线或未响应。`);
                }
            } catch (e) {
                console.error('清理记忆请求异常:', e);
                alert(`❌ 网络请求异常，请检查控制台。`);
            }
        }
        async function confirmTask() {
            let taskContent = document.getElementById('taskInput').value.trim();
            if (!taskContent) return alert("任务内容不能为空！");

            taskContent += '注意：你是一个团队公司，这些任务需要你们公司团队人员协作配合完成。';

            closeModal('taskModal');

            // 提前预设 UI 状态，剩下的变化交给刚刚写的 setInterval 去接管
            const bubble = currentTargetDesk.querySelector('.chat-bubble');
            if (bubble) {
                bubble.classList.remove('done');
                bubble.classList.add('thinking');
                bubble.innerText = '收到！启动中...';

                // 👇 【新增这一行】：打上时间戳，给网络请求留出缓冲期
                currentTargetDesk.dataset.taskStartTime = Date.now();

                const reportBadge = currentTargetDesk.querySelector('.report-badge');
                if (reportBadge) reportBadge.style.display = 'none';
            }

            try {
                // 发送任务后，无需 await 阻塞等待长连接流
                fetch('/api/chat', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        'X-Username': encodeURIComponent(currentTargetName)
                    },
                    body: JSON.stringify({ message: taskContent, modelIndex: 0 })
                }).catch(e => console.error('长连接断开或异常 (前端轮询接管中):', e));

            } catch (e) {
                console.error('任务派发异常:', e);
            }
        }
    </script>
</body>

</html>
""";
}
