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
[JsonSerializable(typeof(Dictionary<string, NodeInfo>))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(CompanySetupResult))]
internal partial class AppJsonContext : JsonSerializerContext { }
public class CreateCompanyReq
{
    public string? Description { get; set; }
    public string? MasterNodeUrl { get; set; }
}
public class CompanySetupResult
{
    public string? Profile { get; set; }
    public List<NodeInfoTemplate>? Employees { get; set; }
}
public class NodeInfoTemplate
{
    public string? name { get; set; }
    public string? Role { get; set; }
    public string? Url { get; set; }
    public string? Description { get; set; }
    public int ModelIndex { get; set; } = 0;
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
    [JsonPropertyName("ModelIndex")] public int ModelIndex { get; set; } = 0;
}
// 2. 修改配置类
public class AppConfig
{
    public string? CompanyProfile { get; set; }

    public string MasterNodeUrl { get; set; } = "http://127.0.0.1:5050";
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
    // 【新增】强行覆盖到底层的同步方法
    private static async Task SyncPeerNodesToMasterAsync()
    {
        string targetUrl = string.IsNullOrEmpty(_config.MasterNodeUrl) ? "http://127.0.0.1:5050" : _config.MasterNodeUrl;
        try
        {
            // 1. 获取底层节点的完整配置
            var getReq = new HttpRequestMessage(HttpMethod.Get, targetUrl.TrimEnd('/') + "/api/config");
            using var getRes = await _httpClient.SendAsync(getReq);
            if (!getRes.IsSuccessStatusCode) return;

            var masterCfgStr = await getRes.Content.ReadAsStringAsync();
            var masterCfgDict = JsonSerializer.Deserialize(masterCfgStr, AppJsonContext.Default.DictionaryStringJsonElement) ?? new();

            // 2. 将我们的员工名单强行塞入底层的 PeerNodes 节点中
            masterCfgDict["PeerNodes"] = JsonSerializer.SerializeToElement(_config.PeerNodes, AppJsonContext.Default.DictionaryStringNodeInfo);

            // 3. 写回底层节点
            var postReq = new HttpRequestMessage(HttpMethod.Post, targetUrl.TrimEnd('/') + "/api/config");
            postReq.Content = new StringContent(JsonSerializer.Serialize(masterCfgDict, AppJsonContext.Default.DictionaryStringJsonElement), Encoding.UTF8, "application/json");
            await _httpClient.SendAsync(postReq);

            Console.WriteLine($"[强绑定] 已将最新通讯录 ({_config.PeerNodes.Count} 人) 实时同步到底层节点 {targetUrl}！");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[强绑定失败] 底层节点离线或配置异常: {ex.Message}");
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
                // 每次打开 Team 网页，都动态从底层拉取员工名单，确保与底层绝对一致
                string targetUrl = string.IsNullOrEmpty(_config.MasterNodeUrl) ? "http://127.0.0.1:5050" : _config.MasterNodeUrl;
                try
                {
                    var proxyReq = new HttpRequestMessage(HttpMethod.Get, targetUrl.TrimEnd('/') + "/api/config");
                    using var proxyRes = await _httpClient.SendAsync(proxyReq);
                    if (proxyRes.IsSuccessStatusCode)
                    {
                        var jsonStr = await proxyRes.Content.ReadAsStringAsync();
                        var masterCfg = JsonSerializer.Deserialize(jsonStr, AppJsonContext.Default.DictionaryStringJsonElement);
                        if (masterCfg != null && masterCfg.TryGetValue("PeerNodes", out var peerNodesEl))
                        {
                            var freshNodes = JsonSerializer.Deserialize(peerNodesEl.GetRawText(), AppJsonContext.Default.DictionaryStringNodeInfo);
                            if (freshNodes != null) _config.PeerNodes = freshNodes;
                        }
                    }
                }
                catch { /* 如果底层挂了，安全容错使用 Team 内存上次留存的记录 */ }

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
                    _config = newConfig; // 更新 Team 本地内存
                    File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, AppJsonContext.Default.AppConfig), Encoding.UTF8);

                    // 【核心联动】：在 Team 页面无论你招人/修改/开除，立刻回推写入 PiPiClaw 底层
                    await SyncPeerNodesToMasterAsync();
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
            else if ((path == "/api/status" || path == "/api/history" || path == "/api/tasks") && req.HttpMethod == "GET")
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
1. 编排一个包含 1 - 21 个核心员工的团队，生成他们的名字和岗位头衔。
2. 注意：所有生成员工的 Url 必须全部统一填为 ""{targetUrl}""。
3. 调用你的本地工具，读取并修改你自己的 `appsettings.json`，把这些新员工信息补充到你的 `PeerNodes` 字典中，PeerNodes 字典格式如下。
""PeerNodes"": {{
    ""陈智远"": {{
      ""Name"": ""陈智远"",
      ""Url"": ""{targetUrl}"",
      ""Role"": ""首席执行官 CEO"",
      ""Description"": ""负责公司整体战略规划、业务发展方向决策、重大合作伙伴关系建立、投融资事务管理，统筹公司各部门协同运作，对董事会负责""
    }}
}}

4. 彻底修改完你自己的配置后，请在最终回复中，**只输出**一个合法的 JSON 数组，供中控台同步使用。绝不要有任何多余的废话和 Markdown 标记。
5. 编写一段详细的【公司简介与对接指南】（包含对接流程、谁负责什么业务、如何协作，使用 Markdown 排版）。
4和5的要求格式严格如下：
{{
    ""Profile"": ""这里填写你生成的 Markdown 格式的公司简介与对接指南（注意：JSON 字符串中的换行必须转义为 \\n，确保整个 JSON 格式合法）"",
    ""Employees"": [
        {{ ""name"": ""员工姓名"", ""Role"": ""岗位头衔"", ""Description"": ""负责的具体能力与工作任务说明"", ""Url"": ""{targetUrl}"" }}
    ]
}}
注意：json字段不能省略必须严谨
";



                var chatReq = new ChatRequest { message = prompt, modelIndex = 0 };
                using var agentReq = new HttpRequestMessage(HttpMethod.Post, callAgentUrl.TrimEnd('/') + "/api/chat");
                agentReq.Headers.Add("X-Username", Uri.EscapeDataString("Team中控"));
                agentReq.Content = new StringContent(JsonSerializer.Serialize(chatReq, typeof(ChatRequest), AppJsonContext.Default), Encoding.UTF8, "application/json");

                try
                {
                    using var agentRes = await _httpClient.SendAsync(agentReq);
                    agentRes.EnsureSuccessStatusCode();

                    var agentResStr = await agentRes.Content.ReadAsStringAsync();
                    string finalJson = "{}";

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

                    // 👉 【核心修改】：现在截取大括号 {} 而不是中括号 []
                    finalJson = finalJson.Replace("```json", "").Replace("```", "").Trim();
                    int startIndex = finalJson.IndexOf('{');
                    int endIndex = finalJson.LastIndexOf('}');
                    if (startIndex >= 0 && endIndex > startIndex)
                    {
                        finalJson = finalJson.Substring(startIndex, endIndex - startIndex + 1);
                    }

                    var setupResult = JsonSerializer.Deserialize(finalJson, typeof(CompanySetupResult), AppJsonContext.Default) as CompanySetupResult;

                    if (setupResult != null)
                    {
                        // 【核心修改 1】：把大模型写好的公司简介塞进全局配置
                        _config.CompanyProfile = setupResult.Profile;

                        if (setupResult.Employees != null && setupResult.Employees.Count > 0)
                        {
                            foreach (var t in setupResult.Employees)
                            {
                                if (!string.IsNullOrEmpty(t.name))
                                {
                                    _config.PeerNodes[t.name] = new NodeInfo
                                    {
                                        Name = t.name,
                                        Url = t.Url,
                                        Role = t.Role,
                                        Description = t.Description,
                                        ModelIndex = t.ModelIndex
                                    };
                                }
                            }
                        }
                        File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, typeof(AppConfig), AppJsonContext.Default), Encoding.UTF8);
                        await SyncPeerNodesToMasterAsync();
                    }

                    // 提取简介（防止为空），并作为返回值带回给前端
                    string safeProfile = JsonSerializer.Serialize(setupResult?.Profile ?? "HR太懒，没有留下任何对接指南...", AppJsonContext.Default.String);

                    res.ContentType = "application/json; charset=utf-8";
                    await res.OutputStream.WriteAsync(Encoding.UTF8.GetBytes($"{{\"status\":\"ok\", \"profile\": {safeProfile}}}"));
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
                _config.CompanyProfile = null;
                File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, typeof(AppConfig), AppJsonContext.Default), Encoding.UTF8);
                await SyncPeerNodesToMasterAsync();
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

        .task-badge {
            display: none;
            position: absolute;
            bottom: 7%;
            left: 8px;
            z-index: 20;
            background: #f3e5f5;
            color: #8e44ad;
            border: 1px solid #e1bee7;
            padding: 3px 8px;
            border-radius: 12px;
            font-size: 10px;
            font-weight: bold;
            cursor: pointer;
            transition: all 0.2s ease;
            pointer-events: auto;
            box-shadow: 0 2px 5px rgba(0,0,0,0.15);
        }
        .task-badge:hover {
            background: #e1bee7;
            transform: scale(1.05);
        }


        /* 顶部左右分栏容器 */
        .header-container {
            width: 100vw;
            box-sizing: border-box;
            display: flex;
            flex-direction: column;
            gap: 20px;
            margin-bottom: 30px;
            padding: 20px 30px;
            background: rgba(255, 255, 255, 0.35);
            backdrop-filter: blur(10px);
            -webkit-backdrop-filter: blur(10px);
            box-shadow: 0 4px 15px rgba(0, 0, 0, 0.05);
            border-bottom: 1px solid rgba(255, 255, 255, 0.6);
            color: #4a3f35;
        }
        @media (min-width: 900px) {
            .header-container {
                flex-direction: row;
                align-items: stretch;
            }
        }
        .header-left {
            flex: 0 0 auto;
            min-width: 350px;
            display: flex;
            flex-direction: column;
            justify-content: center;
        }
        .header-left h1 {
            margin: 0 0 8px 0;
            font-size: 2.2rem;
            text-shadow: 1px 1px 2px rgba(255,255,255,0.8);
            letter-spacing: 2px;
        }
        .header-left p {
            margin: 0;
            font-size: 1.05rem;
            color: #5c4e40;
            font-weight: 600;
        }

        /* 右侧的对接指南看板 */
        .guide-right {
            flex: 1;
            background: rgba(255, 255, 255, 0.6);
            border-radius: 12px;
            padding: 15px 20px;
            max-height: 180px; /* 限制高度出滚动条，不把下面工位挤没 */
            overflow-y: auto;
            border: 1px solid rgba(255, 255, 255, 0.9);
            box-shadow: inset 0 2px 6px rgba(0,0,0,0.05);
            text-align: left;
        }
        .guide-right::-webkit-scrollbar { width: 6px; }
        .guide-right::-webkit-scrollbar-thumb { background: rgba(0,0,0,0.2); border-radius: 3px; }
        .guide-right h1, .guide-right h2, .guide-right h3 { margin-top: 0; margin-bottom: 8px; font-size: 1.2rem; color: #333;}
        .guide-right p { margin-top: 0; margin-bottom: 10px; font-size: 0.95rem; color: #444; line-height: 1.5; }
        .guide-right ul, .guide-right ol { margin-top: 0; padding-left: 20px; font-size: 0.95rem; color: #444;}

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
            top: 4%;
            left: 50%;
            transform: translateX(-50%);
            background: rgba(255, 255, 255, 0.95);
            border: 1px solid rgba(0, 0, 0, 0.05);
            border-radius: 14px;
            padding: 8px 14px;
            font-size: 8px;
            font-weight: 800;
            color: #475569;
            max-width: 90%;
            z-index: 100;
            box-shadow: 0 6px 20px rgba(0, 0, 0, 0.08);
            animation: bubbleFloat 3s ease-in-out infinite;
            transition: all 0.3s ease;

            /* 【修改部分】：允许多行文本并保留省略号 */
            white-space: nowrap; 
            display: -webkit-box;
            -webkit-line-clamp: 4; /* 最多显示 4 行 */
            -webkit-box-orient: vertical;
            text-align: left;
            line-height: 1.4;
            overflow: hidden;
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
            bottom: 28%;
            right: 24%;
            width: 25%;
            height: 15%;
            display: flex;
            flex-direction: column;
            justify-content: center;
            align-items: flex-end;
            z-index: 10;
        }

        .id-card-name {
            width: 100%;
            font-size: clamp(10px, 4vw, 10px);
            font-weight: bold;
            color: #000000;
            margin: 4px 0px 2px 0px;
            line-height: 1.2;
            white-space: nowrap;
            text-align: right;
        }

        .id-card-role {
            font-size: clamp(9px, 3.2vw, 10px);
            color: #555555;
            margin: 0;
            line-height: 1.2;
            text-align: left;
            white-space: nowrap;
            padding-left: 100%;
            animation: marqueeScroll 6s linear infinite;
        }
        .role-scroll-wrapper {
            width: 100%;
            overflow: hidden;
            white-space: nowrap;
            box-sizing: border-box;
            container-type: inline-size;
        }
        @keyframes marqueeScroll {
            0% {
                transform: translateX(0); /* 从右侧外边开始 */
            }
            100% {
                transform: translateX(-100%); /* 一直向左移动，直到自身完全移出左侧 */
            }
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
            display: block !important;
            position: absolute;
            bottom: 7%;
            right: 8px;
            z-index: 20;
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

        /* 默认样式（优先适配手机端：极致贴边显示） */
        .report-content {
            background: #fff; 
            width: 96%;           /* 手机端宽度占比 96%，左右留白极小 */
            height: 96vh;         /* 手机端高度占比 96vh，上下贴边 */
            max-width: 1200px;    /* 放宽最大限制，允许电脑端变大 */
            border-radius: 12px; 
            display: flex; 
            flex-direction: column; 
            overflow: hidden;
            box-shadow: 0 10px 30px rgba(0,0,0,0.3);
        }

        /* 电脑/平板端样式（屏幕宽度大于 768px 时生效） */
        @media (min-width: 768px) {
            .id-card-area {
                bottom: 28%; /* 电脑端视觉微调 */
            }
            .id-card-name {
                font-size: clamp(8px, 1.2vw, 8px);
            }
            .id-card-role {
                font-size: clamp(6px, 1vw, 8px);
            }
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



/* --- 新增：HR 招募中的加载动画遮罩 --- */
.hr-loading-container {
    display: none; /* 默认隐藏 */
    position: fixed;
    inset: 0;
    background: rgba(255, 255, 255, 0.85);
    z-index: 100000; /* 层级设到最高 */
    align-items: center;
    justify-content: center;
    flex-direction: column;
    backdrop-filter: blur(8px);
    -webkit-backdrop-filter: blur(8px);
}
.hr-spinner {
    width: 60px;
    height: 60px;
    border: 6px solid #e2e8f0;
    border-top: 6px solid #3498db;
    border-radius: 50%;
    animation: spin 1s linear infinite;
    margin-bottom: 20px;
    box-shadow: 0 4px 15px rgba(52, 152, 219, 0.2);
}
.hr-loading-text {
    font-size: 1.5rem;
    font-weight: bold;
    color: #2c3e50;
    margin-bottom: 12px;
    animation: pulsate 1.5s infinite ease-in-out;
}
.hr-loading-subtext {
    font-size: 1rem;
    color: #7f8c8d;
    text-align: center;
    line-height: 1.6;
}
@keyframes spin {
    0% { transform: rotate(0deg); }
    100% { transform: rotate(360deg); }
}
@keyframes pulsate {
    0%, 100% { opacity: 1; }
    50% { opacity: 0.6; }
}
    </style>
</head>

<body style="margin:0; padding:0; width:100vw; height:100vh; background-color:#ab9980; ">
    
    <div class="header-container">
        <div class="header-left">
            <h1>🏢 皮皮虾公司办公室</h1>
            <p>欢迎回来！这是您的团队。点击空位可以单招。</p>
            <div style="display: flex; gap: 10px; margin-top: 15px; flex-wrap: wrap;">
                <button onclick="clearAllMemory()" class="top-btn top-btn-clear">🧹 一键清空记忆</button>
                <button onclick="openCreateCompanyModal()" class="top-btn top-btn-create">🚀 一键开设公司</button>
                <button onclick="bankruptcy()" class="top-btn" style="background: linear-gradient(135deg, #c0392b, #8e44ad); color:#fff;">💥 一键破产</button>
            </div>
        </div>

        <div class="guide-right" id="onboardingGuide">
            <div style="color: #888; font-style: italic; text-align: center; margin-top: 30px;">
                暂无公司对接指南。<br>请点击左侧【🚀 一键开设公司】让 HR 自动生成业务蓝图。
            </div>
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
            <input type="text" id="recruitUrl" placeholder="节点URL (如: http://127.0.0.1:5050)"
                style="padding:10px; border:1px solid #ddd; border-radius:8px; outline:none; font-size:12px;"
                onblur="fetchNodeModels()"> <details style="margin-top: 4px; font-size: 13px; color: #666; cursor: pointer;">
                <summary style="outline: none; font-weight: bold;">⚙️ 高级设置 (单独设置模型)</summary>
                <div style="margin-top: 8px;">
                    <label style="display:block; margin-bottom: 4px; font-size:11px;">读取该节点支持的模型并选择：</label>
                    <select id="recruitModelIndex" style="width:100%; padding:8px; border:1px solid #ddd; border-radius:8px; outline:none;">
                        <option value="0">默认模型 (0)</option>
                    </select>
                    <div id="modelFetchStatus" style="font-size:11px; color:#999; margin-top:4px;">填入上方 URL 后自动连接节点获取...</div>
                </div>
            </details>

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
<div id="loadingOverlay" class="hr-loading-container">
    <div class="hr-spinner"></div>
    <div class="hr-loading-text">HR 正在拼命招人中...</div>
    <div class="hr-loading-subtext">
        这可能需要一些时间来配置公司网络与岗位。<br>
        我们正在仔细为您组建梦之队，请耐心等待 ☕
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

        window.addEventListener('load', async () => {
            const guideDiv = document.getElementById('onboardingGuide');


            try {
            const res = await fetch('/api/config');
            if (!res.ok) return;
            const cfg = await res.json();
            window.teamConfig = cfg;
            if (cfg.CompanyProfile && guideDiv) {
                // 【核心修复】：把大模型生成的字面量 "\n" 强制转换成真实的网页换行符
                const realMd = cfg.CompanyProfile.replace(/\\n/g, '\n');

                if (typeof marked !== 'undefined') {
                    // 兼容新老版本的 marked 解析调用
                    guideDiv.innerHTML = marked.parse ? marked.parse(realMd) : marked(realMd);
                } else {
                    guideDiv.innerHTML = `<pre style="white-space:pre-wrap; font-family:inherit;">${realMd}</pre>`;
                }
            }
                if (cfg.PeerNodes) {
                    const desks = document.querySelectorAll('.company-card');
                    let index = 0;

                    // 1. 遍历保存的通讯录，依次填入工位
                    for (const [name, info] of Object.entries(cfg.PeerNodes)) {
                        if (index < desks.length) {
                            const role = info.Role || info.role || '资深员工';
                            renderEmployeeUI(desks[index], name, role);
                            index++;
                        }
                    }
                    console.log(`[初始化] 已成功加载 ${index} 位员工。`);

                    for (let i = 0; i < index; i++) {
                        const desk = desks[i];
                        const empName = desk.querySelector('.id-card-name').innerText;
                        const bubble = desk.querySelector('.chat-bubble');

                        // 静默拉取该员工的历史记录
                        fetch('/api/history', {
                            headers: { 'X-Username': encodeURIComponent(empName) }
                        })
                        .then(r => r.ok ? r.json() : null)
                        .then(history => {

                            if (history && history.length > 0) {
                                reportContent = "";
                                history.forEach(m => {
                                    const role = (m.role || m.Role || "").toLowerCase();
                                    const content = m.content || m.Content;

                                    if (role === 'user' && content) {
                                        // 老板派发的任务
                                        reportContent += `### 🎯 任务指令\n> ${content}\n\n`;
                                    } else if (role === 'assistant' && content) {
                                        // 员工执行的报告
                                        reportContent += `### 📝 汇报结果\n${content}\n\n---\n\n`;
                                    }
                                });

                                if (reportContent === "") {
                                    reportContent = "该员工当前只有底层调用记录，暂无最终文本报告。";
                                }
                            }

                        }).catch(e => {
                            console.warn(`[状态恢复] 无法拉取 ${empName} 的历史记录`, e);
                        });
                    }
                }
            } catch (e) {
                console.error("加载配置失败:", e);
            }
        });




        // 👇 新增：去目标节点拉取它的 appsettings.json 里的 Models 列表
        async function fetchNodeModels(targetIndex = null) {
            const url = document.getElementById('recruitUrl').value.trim();
            const select = document.getElementById('recruitModelIndex');
            const status = document.getElementById('modelFetchStatus');

            if (!url) return;
            status.innerText = "🔄 正在跨网络连接节点拉取配置...";
            select.innerHTML = '<option value="0">默认模型 (0)</option>'; // 先重置保底

            try {
                const cleanUrl = url.replace(/\/$/, '');
                const res = await fetch(`${cleanUrl}/api/config`); // 对方底层代码开了 CORS，这里能直接通
                if (res.ok) {
                    const cfg = await res.json();
                    if (cfg.Models && cfg.Models.length > 0) {
                        select.innerHTML = '';
                        cfg.Models.forEach((m, idx) => {
                            const opt = document.createElement('option');
                            opt.value = idx;
                            opt.text = m.Model ? `[${idx}] ${m.Model}` : `[${idx}] 未命名配置`;
                            select.appendChild(opt);
                        });
                        // 如果是修改员工信息时回显，把之前存的序号选中
                        if (targetIndex !== null && targetIndex < cfg.Models.length) {
                            select.value = targetIndex;
                        }
                        status.innerText = "✅ 获取成功！请按需为该员工分模型。";
                        return;
                    }
                }
                status.innerText = "⚠️ 节点响应正常，但未返回模型列表。";
            } catch (e) {
                status.innerText = "❌ 无法连接节点，请检查 URL 是否正确或节点是否已启动。";
            }
        }










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

            // 1. 关闭输入表单弹窗
            closeModal('createCompanyModal');
            // 2. 显示全屏高大上的加载动画
            document.getElementById('loadingOverlay').style.display = 'flex';

            try {
                const res = await fetch('/api/create_company', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ description: desc, masterNodeUrl: masterUrl })
                });

                if (res.ok) {
                    const data = await res.json();
                    alert("🎉 招募完毕，公司对接指南已生成！");
                    location.reload(); // 成功后刷新页面渲染工位
                } else {
                    alert("❌ 失败！请检查节点是否存活或后台日志。");
                    document.getElementById('loadingOverlay').style.display = 'none';
                }
            } catch (e) {
                console.error(e);
                alert("❌ 网络请求异常。");
                document.getElementById('loadingOverlay').style.display = 'none';
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

                    const savedIndex = nodeInfo.ModelIndex !== undefined ? nodeInfo.ModelIndex : (nodeInfo.modelIndex || 0);
                    document.getElementById('recruitModelIndex').value = savedIndex;
                    fetchNodeModels(savedIndex);

                    document.getElementById('recruitModal').querySelector('h3').innerText = '📝 修改员工信息';
                    document.getElementById('recruitModal').style.display = 'flex';
                }
            });
        }




        // 新增：根据姓名生成固定图片的哈希算法
        function getAvatarByName(name) {
            if (!name) return 'img_shrimp_working.png';

            // 候选图片池 (总共 9 张)
            const images = [
                '1.png', '2.png', '3.png', '4.png', 
                '5.png', '6.png', '7.png', '8.png', 
                'img_shrimp_working.png'
            ];

            // 计算字符串的 Hash 值
            let hash = 0;
            for (let i = 0; i < name.length; i++) {
                hash = name.charCodeAt(i) + ((hash << 5) - hash);
            }

            // 取绝对值并对图片数组长度取余，算出具体的索引
            const index = Math.abs(hash) % images.length;
            return images[index];
        }

        // 替换：修改渲染工位的方法，调用我们写好的算法
        function renderEmployeeUI(deskElement, name, role) {
            deskElement.className = 'company-card at-work';

            // 生成唯一 deskId 绑定在 DOM 上，用于关联报告
            const deskId = 'desk_' + Math.random().toString(36).substr(2, 9);
            deskElement.dataset.deskId = deskId;

            // 根据名字获取对应的图片名称
            const avatarImg = getAvatarByName(name);

            // 【核心修复1】：把 report-badge 移到 desk-img-container 内部
            // 【核心修复2】：为 role 添加 role-scroll-wrapper 容器
            // 【新增修改】：将写死的 img_shrimp_working.png 替换为动态获取的 avatarImg
            deskElement.innerHTML = `
                <div class="chat-bubble" onclick="openReportFromBubble(event, this)">摸鱼中...</div>
                <div class="desk-img-container">
                    <img src="${avatarImg}" alt="皮皮虾办公" class="shrimp-desk-img">
                    <img src="img_empty_desk.png" alt="空桌子" class="empty-desk-img">
                    <img src="penzai2.png" class="desk-penzai">
                    <img src="penzai1.png" class="desk-penzai-2">
                    <div class="report-badge" style="display: block !important;" onclick="openReportById(event, '${deskId}', '${name}')">📄 工作日志</div>
                    <div class="task-badge" onclick="openNodeTasks(event, '${name}')">⏰ 任务(0)</div>
                </div>
                <div class="id-card-area" style="pointer-events: none;"> 
                    <p class="id-card-name">${name}</p>

                    <div class="role-scroll-wrapper">
                        <p class="id-card-role">${role}</p>
                    </div>

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

                            // 【修改部分】：直接应用从底层组装好的三行文本
                            const actionText = data.currentAction || '🤔 正在分析决策中...';
                            bubble.innerText = actionText;
                            bubble.title = actionText; // 悬停可以看全文本

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

                            } 
                            // 如果本来就在摸鱼，更新离线或摸鱼文案
                            else if (!bubble.classList.contains('done')) {
                                bubble.innerText = data.currentAction || '摸鱼中...';
                            }
                        }
                    }

                fetch('/api/tasks', { headers: { 'X-Username': encodeURIComponent(name) } })
                    .then(r => r.ok ? r.json() : [])
                    .then(tasks => {
                        const pending = (tasks || []).filter(t => t.status === 'pending');
                        const taskBadge = desk.querySelector('.task-badge');
                        if (taskBadge) {
                            if (pending.length > 0) {
                                taskBadge.style.display = 'block';
                                taskBadge.innerText = `⏰ 任务(${pending.length})`;
                            } else {
                                taskBadge.style.display = 'none';
                            }
                        }
                    }).catch(() => {});


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
                        let fullLog = "";
                        history.forEach(m => {
                            const role = (m.role || m.Role || "").toLowerCase();
                            const content = m.content || m.Content;

                            const timeHtml = m.timestamp || m.Timestamp ? `<span style="font-size:12px; color:#999; font-weight:normal; margin-left:8px;">(${m.timestamp || m.Timestamp})</span>` : "";
                            if (role === 'user' && content) {
                                fullLog += `### 🎯 任务指令${timeHtml}\n> ${content}\n\n`;
                            } else if (role === 'assistant' && content) {
                                fullLog += `### 📝 汇报结果${timeHtml}\n${content}\n\n<br><hr style="border: none; border-top: 2px dashed #ccc; margin: 30px 0;"><br>\n\n`;
                            }
                        });

                        if (fullLog !== "") {
                            reportContent = fullLog;
                        } else {
                            reportContent = "该员工当前只有系统底层调用记录，暂无最终文本报告。";
                        }
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

            const modelIdx = parseInt(document.getElementById('recruitModelIndex').value) || 0;

            if (!name) return alert("员工姓名不能为空！");
            renderEmployeeUI(currentTargetDesk, name, role);

            try {
                let res = await fetch('/api/config');
                let cfg = await res.json();
                if (!cfg.PeerNodes) cfg.PeerNodes = {};

                if (isEditing && originalEditName !== name && cfg.PeerNodes[originalEditName]) {
                    delete cfg.PeerNodes[originalEditName];
                }
                cfg.PeerNodes[name] = { Url: url, Role: role, Description: desc, ModelIndex: modelIdx }; 
                window.teamConfig = cfg; 

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

            const mIdx = window.teamConfig?.PeerNodes?.[currentTargetName]?.ModelIndex || 0;
            const bubble = currentTargetDesk.querySelector('.chat-bubble');
            if (bubble) {
                bubble.classList.remove('done');
                bubble.classList.add('thinking');
                bubble.innerText = '愿上帝与我们同在！！！';
                currentTargetDesk.dataset.taskStartTime = Date.now();
            }

            try {
                // 发送任务后，无需 await 阻塞等待长连接流
                fetch('/api/chat', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        'X-Username': encodeURIComponent(currentTargetName)
                    },
                    body: JSON.stringify({ message: taskContent, modelIndex: mIdx })
                }).catch(e => console.error('长连接断开或异常 (前端轮询接管中):', e));

            } catch (e) {
                console.error('任务派发异常:', e);
            }
        }

        async function openNodeTasks(event, empName) {
            event.stopPropagation(); // 阻止触发工位点击
            try {
                const res = await fetch('/api/tasks', { headers: { 'X-Username': encodeURIComponent(empName) } });
                if (res.ok) {
                    const tasks = await res.json();
                    const pending = (tasks || []).filter(t => t.status === 'pending');
                    if (pending.length === 0) {
                        alert(`【${empName}】当前没有挂起的任务。`);
                        return;
                    }
                    // 拼接任务详情供展示
                    let taskList = pending.map(t => {
                        const time = new Date(t.execute_at).toLocaleString('zh-CN', { month:'2-digit', day:'2-digit', hour:'2-digit', minute:'2-digit'});
                        const loop = t.interval_minutes > 0 ? ` (每${t.interval_minutes}分钟)` : '';
                        return `[${time}]${loop} - ${t.user_intent}`;
                    }).join('\n\n');

                    alert(`【${empName}】当前的挂起任务：\n\n${taskList}`);
                } else {
                    alert(`无法获取【${empName}】的任务状态。`);
                }
            } catch (e) {
                alert(`请求异常，无法连接到节点。`);
            }
        }
    </script>
</body>

</html>
""";
}
