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
[JsonSerializable(typeof(List<JsonElement>))]
[JsonSerializable(typeof(ProjectBoard))]
[JsonSerializable(typeof(ProjectTask))]
[JsonSerializable(typeof(List<ProjectTask>))]

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
    public string? Resume { get; set; }
    public int ModelIndex { get; set; } = 0;
}
public class ChatRequest
{
    public string? message { get; set; }
    public int modelIndex { get; set; }

    public string? sop { get; set; }

    public string? caller { get; set; }
    public string? taskId { get; set; }
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
    [JsonPropertyName("Resume")] public string? Resume { get; set; }
    [JsonPropertyName("ModelIndex")] public int ModelIndex { get; set; } = 0;
}
public class ProjectTask
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("assignee")] public string Assignee { get; set; } = ""; // 派给哪个皮皮虾
    [JsonPropertyName("status")] public string Status { get; set; } = "todo"; // todo, doing, done
    [JsonPropertyName("update_time")] public string UpdateTime { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    [JsonPropertyName("result")] public string Result { get; set; } = "";
}

public class ProjectBoard
{
    [JsonPropertyName("project_name")] public string? ProjectName { get; set; }
    [JsonPropertyName("tasks")] public List<ProjectTask> Tasks { get; set; } = [];
}
public class AppConfig
{
    public string? CompanyProfile { get; set; }

    public string CompanyName { get; set; } = "未命名皮皮虾公司";
    public bool HasLicense { get; set; } = false;
    public string MasterNodeUrl { get; set; } = "http://127.0.0.1:5050";
    public Dictionary<string, NodeInfo> PeerNodes { get; set; } = new();

    public string? CompanySOP { get; set; }

    public List<ProjectBoard> Projects { get; set; } = new();
}

class Program
{
    // 全局静态配置
    private static AppConfig _config = new AppConfig();
    private static string _configPath = "team_config.json";
    private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

#if DEBUG
    const string BossMarketUrl = "http://ddns.work:8888";
#else
    const string BossMarketUrl = "http://ddns.work:8888";
#endif

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
            using var postRes = await _httpClient.SendAsync(postReq);

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
                string targetUrl = string.IsNullOrEmpty(_config.MasterNodeUrl) ? "http://127.0.0.1:5050" : _config.MasterNodeUrl;
                string username = req.Headers["X-Username"] ?? "未知员工";
                username = Uri.UnescapeDataString(username);

                res.ContentType = "text/plain; charset=utf-8";
                res.SendChunked = true; // 开启分块传输，实现流式输出

                using var writer = new StreamWriter(res.OutputStream, new UTF8Encoding(false)) { AutoFlush = true };

                using var reader = new StreamReader(req.InputStream);
                string body = await reader.ReadToEndAsync();

                // 3. 将请求转发给真正的目标节点 (代码1)
                try
                {
                    var proxyReq = new HttpRequestMessage(HttpMethod.Post, targetUrl.TrimEnd('/') + "/api/chat");
                    proxyReq.Headers.Add("X-Username", Uri.EscapeDataString(username));
                    string currentTeamUrl = $"http://{req.Url?.Host}:{req.Url?.Port}";
                    proxyReq.Headers.Add("X-Team-Url", Uri.EscapeDataString(currentTeamUrl));

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
            else if ((path == "/api/clear" || path == "/api/cancel") && req.HttpMethod == "POST")
            {
                string username = Uri.UnescapeDataString(req.Headers["X-Username"] ?? "");
                string targetUrl = "";
                if (username.ToLower() == "ceo")
                {
                    targetUrl = _config.PeerNodes.Values.FirstOrDefault(n => !string.IsNullOrEmpty(n.Url))?.Url ?? (_config.MasterNodeUrl ?? "http://127.0.0.1:5050");
                }
                else if (_config.PeerNodes.TryGetValue(username, out var nodeInfo) && !string.IsNullOrEmpty(nodeInfo.Url))
                {
                    targetUrl = nodeInfo.Url;
                }
                if (string.IsNullOrEmpty(targetUrl))
                {
                    res.StatusCode = 404;
                    return;
                }
                try
                {
                    using var proxyReq = new HttpRequestMessage(HttpMethod.Post, targetUrl.TrimEnd('/') + path);
                    proxyReq.Headers.Add("X-Username", Uri.EscapeDataString(username));
                    using var proxyRes = await _httpClient.SendAsync(proxyReq);
                    proxyRes.EnsureSuccessStatusCode();
                    res.ContentType = "application/json; charset=utf-8";
                    string statusResp = path == "/api/cancel" ? "{\"status\":\"cancelled\"}" : "{\"status\":\"cleared\"}";
                    await res.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(statusResp));
                }
                catch (Exception ex)
                {
                    res.StatusCode = 500;
                    Console.WriteLine($"[{path} 异常] {ex.Message}");
                }
            }
            else if (path == "/api/clearall" && req.HttpMethod == "POST")
            {
                int successCount = 0;
                var tasks = new List<Task>();

                // 【新增】：找一个可用的节点，顺手把 "ceo" 的记忆也彻底擦除
                string hrAgentUrl = _config.PeerNodes.Values.FirstOrDefault(n => !string.IsNullOrEmpty(n.Url))?.Url ?? "http://127.0.0.1:5050";
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        using var proxyReq = new HttpRequestMessage(HttpMethod.Post, hrAgentUrl.TrimEnd('/') + "/api/clear");
                        proxyReq.Headers.Add("X-Username", Uri.EscapeDataString("ceo"));
                        using var proxyRes = await _httpClient.SendAsync(proxyReq);
                    }
                    catch { /* 忽略异常 */ }
                }));

                // 原有的遍历员工清理逻辑保持不变
                foreach (var kvp in _config.PeerNodes)
                {
                    if (string.IsNullOrEmpty(kvp.Value.Url)) continue;

                    tasks.Add(Task.Run(async () =>
                    {
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

                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        using var proxyReq = new HttpRequestMessage(HttpMethod.Post, callAgentUrl.TrimEnd('/') + "/api/clear");
                        proxyReq.Headers.Add("X-Username", Uri.EscapeDataString("ceo"));
                        using var proxyRes = await _httpClient.SendAsync(proxyReq);
                    }
                    catch { /* 忽略 */ }
                }));

                foreach (var kvp in _config.PeerNodes)
                {
                    if (string.IsNullOrEmpty(kvp.Value.Url)) continue;
                    tasks.Add(Task.Run(async () =>
                    {
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
1. 编排一个包含 1 - 21 个核心员工的团队，生成他们的名字和岗位头衔，以及详细的个人简历（包含专业背景、工作经验、性格特点等）不需要ceo，因为用户就是 ceo。
2. 注意：所有生成员工的 Url 必须全部统一填为 ""{targetUrl}""。
3. 调用你的本地工具，读取并修改你自己的 `appsettings.json`，把这些新员工信息补充到你的 `PeerNodes` 字典中，PeerNodes 字典格式如下。
""PeerNodes"": {{
    ""陈智远"": {{
      ""Name"": ""陈智远"",
      ""Url"": ""{targetUrl}"",
      ""Role"": ""产品经理"",
      ""Resume"": ""清华大学 MBA，深耕互联网产品领域 15 年。具备极强的商业敏锐度与全局战略思维，拥有多款亿级用户量“爆款”产品的从 0 到 1 及规模化增长经验。擅长在复杂业务环境下整合跨职能资源，以数据驱动决策，实现产品价值与商业利润的双重突破。"",
      ""Description"": ""负责产品全生命周期的战略规划与路线图制定，深度洞察市场趋势以识别商业机会。凭借丰富的行业经验，精准平衡用户体验与商业效益，通过高效的资源整合驱动产品持续创新与市场准入。""
    }}
}}

4. 彻底修改完你自己的配置后，请在最终回复中，**只输出**一个合法的 JSON 数组，供中控台同步使用。绝不要有任何多余的废话和 Markdown 标记。
5. 编写一段详细的【公司简介与对接指南】（包含对接流程、谁负责什么业务、如何协作，使用 Markdown 排版）。
4和5的要求格式严格如下：
{{
    ""Profile"": ""这里填写你生成的 Markdown 格式的公司简介与对接指南（注意：JSON 字符串中的换行必须转义为 \\n，确保整个 JSON 格式合法）"",
    ""Employees"": [
        {{ ""name"": ""员工姓名"", ""Role"": ""岗位头衔"", ""Description"": ""负责的具体能力与工作任务说明"",""Resume"": ""个人详细信息，简历介绍。"", ""Url"": ""{targetUrl}"" }}
    ]
}}
注意：json字段不能省略必须严谨
";



                var chatReq = new ChatRequest { message = prompt, modelIndex = 0 };
                using var agentReq = new HttpRequestMessage(HttpMethod.Post, callAgentUrl.TrimEnd('/') + "/api/chat");
                agentReq.Headers.Add("X-Username", Uri.EscapeDataString("ceo"));
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
                                        Resume = t.Resume,
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


            // [Team后端] 获取轻量级人才列表
            else if (path == "/api/boss/list" && req.HttpMethod == "GET")
            {
                try
                {
                    using var proxyReq = new HttpRequestMessage(HttpMethod.Get, BossMarketUrl + "/api/list");
                    using var proxyRes = await _httpClient.SendAsync(proxyReq);
                    proxyRes.EnsureSuccessStatusCode();
                    var responseBytes = await proxyRes.Content.ReadAsByteArrayAsync();
                    res.ContentType = "application/json; charset=utf-8";
                    res.ContentLength64 = responseBytes.Length;
                    await res.OutputStream.WriteAsync(responseBytes);
                }
                catch { res.StatusCode = 500; }
            }

            // [新增] 去 BOSS 服务器注册公司营业执照
            else if (path == "/api/boss/register" && req.HttpMethod == "POST")
            {
                using var reader = new StreamReader(req.InputStream);
                var bodyJson = JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement;
                string companyName = bodyJson.GetProperty("companyName").GetString() ?? "";

                if (string.IsNullOrWhiteSpace(companyName)) { res.StatusCode = 400; return; }

                try
                {
                    var regReq = new HttpRequestMessage(HttpMethod.Post, BossMarketUrl + "/api/register");
                    regReq.Content = new StringContent($"{{\"companyName\":\"{companyName}\"}}", Encoding.UTF8, "application/json");
                    var regRes = await _httpClient.SendAsync(regReq);

                    _config.CompanyName = companyName;

                    if (regRes.IsSuccessStatusCode)
                    {
                        _config.HasLicense = true; // 注册成功，拿到执照
                        File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, AppJsonContext.Default.AppConfig), Encoding.UTF8);
                        res.ContentType = "application/json; charset=utf-8";
                        await res.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"status\":\"ok\"}"));
                    }
                    else throw new Exception("BOSS 服务器拒绝注册");
                }
                catch
                {
                    // 注册失败，但本地依然修改公司名，只是执照状态为 false
                    _config.HasLicense = false;
                    File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, AppJsonContext.Default.AppConfig), Encoding.UTF8);
                    res.StatusCode = 500;
                }
            }

            // [Team后端] 上传员工到 BOSS (二进制流无缝透传)
            else if (path == "/api/boss/upload" && req.HttpMethod == "POST")
            {
                string username = Uri.UnescapeDataString(req.Headers["X-Username"] ?? "");
                // [修改]：不再从 Header 拿虚假的 currentUsername，而是直接从本地配置取真实的公司名
                string teamId = string.IsNullOrWhiteSpace(_config.CompanyName) ? "无执照公司" : _config.CompanyName;

                if (_config.PeerNodes.TryGetValue(username, out var nodeInfo) && !string.IsNullOrEmpty(nodeInfo.Url))
                {
                    try
                    {
                        var exportUrl = nodeInfo.Url.TrimEnd('/') + "/api/export";
                        using var exportReq = new HttpRequestMessage(HttpMethod.Get, exportUrl);
                        exportReq.Headers.Add("X-Username", Uri.EscapeDataString(username));

                        using var exportRes = await _httpClient.SendAsync(exportReq, HttpCompletionOption.ResponseHeadersRead);
                        exportRes.EnsureSuccessStatusCode();

                        var uploadReq = new HttpRequestMessage(HttpMethod.Post, BossMarketUrl + "/api/upload");
                        uploadReq.Headers.Add("X-Agent-Profile", exportRes.Headers.GetValues("X-Agent-Profile").First());
                        uploadReq.Headers.Add("X-Team-Id", Uri.EscapeDataString(teamId)); // 附带公司名上传
                        uploadReq.Content = new StreamContent(await exportRes.Content.ReadAsStreamAsync());
                        uploadReq.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");

                        using var uploadRes = await _httpClient.SendAsync(uploadReq);
                        uploadRes.EnsureSuccessStatusCode();

                        res.StatusCode = 200;
                    }
                    catch (Exception ex) { Console.WriteLine(ex.Message); res.StatusCode = 500; }
                }
            }

            // [Team后端] 从 BOSS 招募员工
            else if (path == "/api/boss/hire" && req.HttpMethod == "POST")
            {
                using var reader = new StreamReader(req.InputStream);
                var bodyJson = JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement;

                string targetId = bodyJson.GetProperty("id").GetString() ?? ""; // 核心：靠文件 Hash 拉取
                string targetName = bodyJson.GetProperty("name").GetString() ?? "";
                string targetNodeUrl = bodyJson.GetProperty("nodeUrl").GetString() ?? "";

                try
                {
                    // 1. 去 BOSS 服务器根据 ID 下载完整的 ZIP 流
                    var dlReq = new HttpRequestMessage(HttpMethod.Get, $"{BossMarketUrl}/api/download?id={Uri.EscapeDataString(targetId)}");
                    using var dlRes = await _httpClient.SendAsync(dlReq, HttpCompletionOption.ResponseHeadersRead);
                    dlRes.EnsureSuccessStatusCode();

                    // 2. 推给目标底层节点的 /api/import
                    var importReq = new HttpRequestMessage(HttpMethod.Post, targetNodeUrl.TrimEnd('/') + "/api/import");
                    importReq.Headers.Add("X-Username", Uri.EscapeDataString(targetName));
                    importReq.Content = new StreamContent(await dlRes.Content.ReadAsStreamAsync());
                    importReq.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");

                    using var importRes = await _httpClient.SendAsync(importReq);
                    importRes.EnsureSuccessStatusCode();

                    // 3. 在中控团队注册该员工
                    var listRes = await _httpClient.GetStringAsync(BossMarketUrl + "/api/list");
                    var allAgents = JsonSerializer.Deserialize(listRes, AppJsonContext.Default.ListJsonElement);
                    var agent = allAgents.FirstOrDefault(a => a.GetProperty("Id").GetString() == targetId);

                    _config.PeerNodes[targetName] = new NodeInfo
                    {
                        Name = targetName,
                        Url = targetNodeUrl,
                        Role = agent.GetProperty("Role").GetString() ?? "新员工",
                        Description = agent.GetProperty("Description").GetString() ?? "",
                        Resume = agent.GetProperty("Resume").GetString() ?? "",
                        ModelIndex = agent.GetProperty("ModelIndex").GetInt32()
                    };
                    File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, AppJsonContext.Default.AppConfig), Encoding.UTF8);
                    await SyncPeerNodesToMasterAsync();

                    res.StatusCode = 200;
                }
                catch { res.StatusCode = 500; }
            }
            else if (path == "/api/boss/delete" && req.HttpMethod == "POST")
            {
                using var reader = new StreamReader(req.InputStream);
                var bodyJson = JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement;
                string targetId = bodyJson.GetProperty("id").GetString() ?? "";

                // 获取本地公司的名字，作为鉴权凭证
                string teamId = string.IsNullOrWhiteSpace(_config.CompanyName) ? "无执照公司" : _config.CompanyName;

                try
                {
                    var delReq = new HttpRequestMessage(HttpMethod.Post, BossMarketUrl + "/api/delete");
                    delReq.Headers.Add("X-Team-Id", Uri.EscapeDataString(teamId)); // 带上公司大印
                    delReq.Content = new StringContent($"{{\"id\":\"{targetId}\"}}", Encoding.UTF8, "application/json");

                    using var delRes = await _httpClient.SendAsync(delReq);

                    if (delRes.IsSuccessStatusCode)
                    {
                        res.StatusCode = 200;
                        await res.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"status\":\"ok\"}"));
                    }
                    else if (delRes.StatusCode == HttpStatusCode.Forbidden)
                    {
                        res.StatusCode = 403; // 无权限
                    }
                    else
                    {
                        res.StatusCode = 500;
                    }
                }
                catch { res.StatusCode = 500; }
            }
            else if (path == "/api/board" && req.HttpMethod == "GET")
            {
                res.ContentType = "application/json; charset=utf-8";
                // 👉 直接返回列表
                var boardData = _config.Projects ?? new List<ProjectBoard>();
                await res.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(boardData, AppJsonContext.Default.ListProjectBoard)));
            }
            else if (path == "/api/board" && req.HttpMethod == "POST")
            {
                using var reader = new StreamReader(req.InputStream);
                string body = await reader.ReadToEndAsync();
                try
                {
                    var newBoard = JsonSerializer.Deserialize(body, typeof(ProjectBoard), AppJsonContext.Default) as ProjectBoard;
                    if (newBoard != null)
                    {
                        _config.Projects ??= new List<ProjectBoard>();
                        bool isDispatchNeeded = false; // 标记是否需要触发后端全局派发
                        var tasksToDispatch = new List<ProjectTask>();

                        // 情况A：只更新单个任务状态 (普通员工调用)
                        if (newBoard.Tasks != null && newBoard.Tasks.Count == 1 && string.IsNullOrEmpty(newBoard.ProjectName))
                        {
                            var updateTask = newBoard.Tasks[0];
                            var existingTask = _config.Projects.SelectMany(p => p.Tasks).FirstOrDefault(t => t.Id == updateTask.Id);
                            if (existingTask != null)
                            {
                                existingTask.Status = updateTask.Status;
                                existingTask.UpdateTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                                if (!string.IsNullOrEmpty(updateTask.Result)) existingTask.Result = updateTask.Result;
                            }
                        }
                        // 情况B：新增拆解任务 (CEO调用)
                        else if (!string.IsNullOrEmpty(newBoard.ProjectName))
                        {
                            isDispatchNeeded = true; // 老板立项，激活派发引擎！
                            var existingProject = _config.Projects.FirstOrDefault(p => p.ProjectName == newBoard.ProjectName);
                            if (existingProject == null)
                            {
                                _config.Projects.Add(newBoard);
                                tasksToDispatch.AddRange(newBoard.Tasks);
                            }
                            else
                            {
                                if (newBoard.Tasks != null)
                                {
                                    foreach (var t in newBoard.Tasks)
                                    {
                                        if (string.IsNullOrEmpty(t.Id)) t.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
                                        t.UpdateTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                                        if (string.IsNullOrWhiteSpace(t.Assignee)) t.Assignee = "待认领";

                                        var existingT = existingProject.Tasks.FirstOrDefault(x => x.Title == t.Title || x.Id == t.Id);
                                        if (existingT == null)
                                        {
                                            existingProject.Tasks.Add(t);
                                            tasksToDispatch.Add(t);
                                        }
                                        else existingT.Status = t.Status;
                                    }
                                }
                            }
                        }
                        File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, typeof(AppConfig), AppJsonContext.Default), Encoding.UTF8);
                        if (isDispatchNeeded && tasksToDispatch.Count > 0)
                        {
                            string teamUrl = $"http://{req.Url?.Host}:{req.Url?.Port}";
                            string companySop = _config.CompanySOP ?? "";
                            string projectName = newBoard.ProjectName;
                            _ = Task.Run(async () =>
                            {
                                string accumulatedContext = "";
                                foreach (var t in tasksToDispatch)
                                {
                                    if (string.IsNullOrWhiteSpace(t.Assignee) || t.Assignee == "待认领" || t.Assignee.ToLower() == "ceo") continue;

                                    if (_config.PeerNodes.TryGetValue(t.Assignee, out var nodeInfo) && !string.IsNullOrEmpty(nodeInfo.Url))
                                    {
                                        try
                                        {
                                            string targetUrl = nodeInfo.Url.TrimEnd('/') + "/api/agent_task";
                                            string execPrompt = $"【系统最高指令】\n所属项目：{projectName}\n你的任务目标：{t.Title}\n";
                                            if (!string.IsNullOrWhiteSpace(accumulatedContext))
                                            {
                                                execPrompt += $"\n【前置任务交付的上下文参考】（请基于以下结果继续推进你的工作）：\n{accumulatedContext}\n";
                                            }
                                            execPrompt += "\n请立刻开始执行此任务。完成后请直接用自然语言输出结果，底层会自动为你更新项目看板。";
                                            var proxyReq = new HttpRequestMessage(HttpMethod.Post, targetUrl);
                                            proxyReq.Headers.Add("X-Username", Uri.EscapeDataString(t.Assignee));
                                            proxyReq.Headers.Add("X-Team-Url", Uri.EscapeDataString(teamUrl));
                                            var reqBody = new ChatRequest
                                            {
                                                message = execPrompt,
                                                modelIndex = nodeInfo.ModelIndex,
                                                caller = "ceo",
                                                taskId = t.Id,
                                                sop = companySop
                                            };
                                            proxyReq.Content = new StringContent(JsonSerializer.Serialize(reqBody, typeof(ChatRequest), AppJsonContext.Default), Encoding.UTF8, "application/json");
                                            using var proxyRes = await _httpClient.SendAsync(proxyReq);
                                            proxyRes.EnsureSuccessStatusCode();
                                            string taskResult = await proxyRes.Content.ReadAsStringAsync();
                                            accumulatedContext += $"\n--- 同事 [{t.Assignee}] 完成了前置任务 [{t.Title}]，交付成果如下 ---\n{taskResult}\n";
                                            await Task.Delay(3000);
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.WriteLine($"[中控后台派发异常] 派发给 {t.Assignee} 失败: {ex.Message}");
                                            accumulatedContext += $"\n--- 警告：同事 [{t.Assignee}] 的前置任务 [{t.Title}] 执行异常 ---\n系统反馈：{ex.Message}\n";
                                        }
                                    }
                                }
                            });
                        }
                    }
                    res.StatusCode = 200;
                    await res.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"status\":\"ok\"}"));
                }
                catch { res.StatusCode = 500; }
            }
            else if (path == "/api/board" && req.HttpMethod == "DELETE")
            {
                // 👉 一键清空所有项目
                _config.Projects = [];
                File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, typeof(AppConfig), AppJsonContext.Default), Encoding.UTF8);
                res.ContentType = "application/json; charset=utf-8";
                await res.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"status\":\"ok\"}"));
            }
            else if (path == "/api/agent_task" && req.HttpMethod == "POST")
            {
                string username = Uri.UnescapeDataString(req.Headers["X-Username"] ?? "");
                string targetUrl = string.IsNullOrEmpty(_config.MasterNodeUrl) ? "http://127.0.0.1:5050" : _config.MasterNodeUrl;

                // 寻址对应的底层节点
                if (_config.PeerNodes.TryGetValue(username, out var nodeInfo) && !string.IsNullOrEmpty(nodeInfo.Url))
                {
                    targetUrl = nodeInfo.Url;
                }

                using var reader = new StreamReader(req.InputStream);
                string body = await reader.ReadToEndAsync();

                try
                {
                    var proxyReq = new HttpRequestMessage(HttpMethod.Post, targetUrl.TrimEnd('/') + "/api/agent_task");
                    proxyReq.Headers.Add("X-Username", Uri.EscapeDataString(username));
                    string currentTeamUrl = $"http://{req.Url?.Host}:{req.Url?.Port}";
                    proxyReq.Headers.Add("X-Team-Url", Uri.EscapeDataString(currentTeamUrl));

                    proxyReq.Content = new StringContent(body, Encoding.UTF8, "application/json");

                    using var proxyRes = await _httpClient.SendAsync(proxyReq);
                    proxyRes.EnsureSuccessStatusCode();

                    // agent_task 返回的是纯文本的执行结果
                    string proxyResStr = await proxyRes.Content.ReadAsStringAsync();
                    res.ContentType = "text/plain; charset=utf-8";
                    await res.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(proxyResStr));
                }
                catch (Exception ex)
                {
                    res.StatusCode = 500;
                    Console.WriteLine($"[代理 AgentTask 异常] {ex.Message}");
                }
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
    padding: 10px 18px;
    border: none;
    border-radius: 8px;
    cursor: pointer;
    font-weight: bold;
    font-size: 0.95rem;
    color: #fff;
    display: inline-flex;
    align-items: center;
    gap: 6px;
    transition: all 0.3s ease; 
}

.top-btn:hover {
    transform: translateY(-3px);
    filter: brightness(1.1);
}
.top-btn:active {
    transform: translateY(0);
    filter: brightness(0.9);
}

.btn-create   { background: linear-gradient(135deg, #4a3f35, #2c251f); box-shadow: 0 4px 10px rgba(44, 37, 31, 0.3); }
.btn-deploy   { background: linear-gradient(135deg, #16a085, #27ae60); box-shadow: 0 4px 10px rgba(39, 174, 96, 0.3); }
.btn-board    { background: linear-gradient(135deg, #2980b9, #8e44ad); box-shadow: 0 4px 10px rgba(41, 128, 185, 0.3); }
.btn-sop      { background: linear-gradient(135deg, #34495e, #2c3e50); box-shadow: 0 4px 10px rgba(44, 62, 80, 0.3); }

.btn-boss     { background: linear-gradient(135deg, #27ae60, #2ecc71); box-shadow: 0 4px 10px rgba(39, 174, 96, 0.3); }
.btn-clear    { background: linear-gradient(135deg, #e67e22, #d35400); box-shadow: 0 4px 10px rgba(230, 126, 34, 0.3); }
.btn-bankrupt { background: linear-gradient(135deg, #c0392b, #8e44ad); box-shadow: 0 4px 10px rgba(192, 57, 43, 0.3); }

.btn-group-wrapper {
    display: flex;
    flex-direction: column;
    gap: 12px; 
    margin-top: auto; 
    padding-top: 15px;
}
.btn-row {
    display: flex;
    gap: 10px;
    flex-wrap: wrap;
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


        #reportModal {
            display: none; position: fixed; inset: 0; background: rgba(0,0,0,0.6);
            z-index: 10050; /* 👈 将层级提高，盖过人才市场的 10000 */
            align-items: center; justify-content: center; backdrop-filter: blur(5px);
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


/* --- 💼 BOSS 直聘：高级人才市场 UI --- */
#bossTalentList::-webkit-scrollbar {
    width: 6px;
}
#bossTalentList::-webkit-scrollbar-thumb {
    background: #cbd5e1;
    border-radius: 3px;
}
#bossTalentList::-webkit-scrollbar-track {
    background: transparent;
}

.talent-card {
    display: flex;
    align-items: center;
    gap: 15px;
    padding: 15px;
    border-radius: 12px;
    background: #ffffff;
    border: 1px solid #e2e8f0;
    transition: all 0.3s ease;
    margin-bottom: 10px;
    box-shadow: 0 2px 8px rgba(0,0,0,0.02);
}
.talent-card:hover {
    transform: translateY(-2px);
    box-shadow: 0 8px 20px rgba(0,0,0,0.08);
    border-color: #cbd5e1;
}
.talent-avatar {
    flex-shrink: 0;
    width: 55px;
    height: 55px;
    border-radius: 50%;
    border: 2px solid #f8fafc;
    box-shadow: 0 4px 10px rgba(0,0,0,0.06);
    overflow: hidden;
    background: #f1f5f9;
    display: flex;
    align-items: center;
    justify-content: center;
}
.talent-avatar img {
    width: 80%; /* 调整皮皮虾图片的显示比例 */
    height: auto;
}
.talent-info {
    flex-grow: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 6px;
}
.talent-header {
    display: flex;
    align-items: center;
    gap: 8px;
}
.talent-name {
    font-size: 1.1em;
    font-weight: bold;
    color: #1e293b;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}
.talent-role {
    font-size: 0.75em;
    background: linear-gradient(135deg, #3b82f6, #2563eb);
    color: white;
    padding: 3px 8px;
    border-radius: 12px;
    white-space: nowrap;
    box-shadow: 0 2px 5px rgba(59, 130, 246, 0.2);
}
.talent-desc {
    font-size: 0.85em;
    color: #64748b;
    line-height: 1.5;
    display: -webkit-box;
    -webkit-line-clamp: 2; /* 最多显示2行，超出省略号 */
    -webkit-box-orient: vertical;
    overflow: hidden;
}
.talent-action {
    flex-shrink: 0;
}
.btn-hire {
    padding: 8px 16px;
    background: linear-gradient(135deg, #10b981, #059669);
    color: white;
    border: none;
    border-radius: 8px;
    cursor: pointer;
    font-weight: bold;
    box-shadow: 0 4px 10px rgba(16, 185, 129, 0.2);
    transition: all 0.2s;
}
.btn-hire:hover {
    transform: translateY(-2px);
    box-shadow: 0 6px 15px rgba(16, 185, 129, 0.35);
}

/* --- 替换后的项目列表样式 --- */
.project-list-container { 
    min-height: 0; 
    overflow-y: auto; 
    margin-top: 15px; 
    padding-right: 5px; 
}
.project-list-container::-webkit-scrollbar { width: 6px; }
.project-list-container::-webkit-scrollbar-thumb { background: #cbd5e1; border-radius: 3px; }
.project-group { margin-bottom: 10px;background: #fff; border-radius: 10px; border: 1px solid #e2e8f0; overflow: hidden; box-shadow: 0 2px 8px rgba(0,0,0,0.02); transition: all 0.2s ease; }
.project-group:hover { box-shadow: 0 4px 15px rgba(0,0,0,0.06); }
.project-header { display: flex; justify-content: space-between; align-items: center; padding: 15px 20px; background: #f8fafc; cursor: pointer; transition: background 0.2s; border-bottom: 1px solid transparent; user-select: none; }
.project-header:hover { background: #f1f5f9; }
.project-header.expanded { border-bottom-color: #e2e8f0; }
.project-title { 
    font-weight: bold; 
    font-size: 1.05em; 
    color: #1e293b; 
    display: flex; 
    align-items: center; 
    gap: 8px; 
    flex: 1; /* 核心修改：自动霸占所有左侧剩余空间 */
    min-width: 0; /* 防止超长文本撑破布局 */
    overflow: hidden; 
    white-space: nowrap; 
    text-overflow: ellipsis; 
}
.project-progress-container { 
    flex: 0 0 180px;
    margin: 0 20px; 
    display: flex; 
    flex-direction: column; 
    gap: 4px; 
}
.progress-bar-bg { height: 6px; background: #e2e8f0; border-radius: 3px; overflow: hidden; width: 100%; }
.progress-bar-fill { height: 100%; background: linear-gradient(90deg, #3b82f6, #10b981); transition: width 0.4s ease; }
.project-meta { 
    font-size: 0.85em; 
    color: #64748b; 
    font-weight: bold; 
    flex: 0 0 60px; 
    text-align: right; 
}
.task-list { padding: 5px 20px 15px 20px; background: #fff; }
.task-row { display: flex; justify-content: space-between; align-items: center; padding: 12px 0; border-bottom: 1px dashed #f1f5f9; transition: background 0.2s; }
.task-row:last-child { border-bottom: none; }
.task-row:hover { background: #fafafa; }
.task-title { font-size: 0.9em; color: #334155; flex: 1; padding-right: 15px; font-weight: 500; }
.task-badges { display: flex; gap: 8px; align-items: center; font-size: 0.75em; flex-shrink: 0; }
.badge { padding: 4px 8px; border-radius: 6px; font-weight: bold; }
.badge.todo { background: #f1f5f9; color: #64748b; border: 1px solid #e2e8f0; }
.badge.doing { background: #eff6ff; color: #3b82f6; border: 1px solid #bfdbfe; }
.badge.done { background: #f0fdf4; color: #16a34a; border: 1px solid #bbf7d0; }
.badge.assignee { background: #f8fafc; border: 1px solid #e2e8f0; color: #475569; display: flex; align-items: center; gap: 4px; }

    </style>

</head>

<body style="margin:0; padding:0; width:100vw; height:100vh; background-color:#ab9980; ">
    
    <div class="header-container">


<div class="header-left" style="justify-content: flex-start; padding-bottom: 5px;">
    <div style="display: flex; flex-direction: column; align-items: flex-start; cursor: pointer; margin-bottom: 12px; transition: transform 0.2s;" onclick="openLicenseModal()" title="点击修改公司并申请执照" onmouseover="this.style.transform='translateX(5px)'" onmouseout="this.style.transform='none'">
        <h1 style="margin: 0; display: flex; align-items: center; gap: 10px; font-size: 2.2rem;">
            🏢 <span id="companyNameDisplay" style="background: linear-gradient(120deg, var(--pipi-cyan), var(--pipi-magenta));">未命名公司</span>
        </h1>
        <span id="licenseTag" style="margin-top: 6px; font-size: 0.75rem; padding: 4px 8px; border-radius: 6px; background: #e74c3c; color: white; font-weight: bold; letter-spacing: 1px; box-shadow: 0 4px 10px rgba(0,0,0,0.1);">未获取营业执照</span>
    </div>
    <p>欢迎回来！这是您的团队。点击空位可以单招。</p>

    <div class="btn-group-wrapper">
        <div class="btn-row">
            <button onclick="openCreateCompanyModal()" class="top-btn btn-create">🚀 一键开设公司</button>
            <button onclick="openCeoModal()" class="top-btn btn-deploy">🎯 部署战略/项目</button>
            <button onclick="openBoardModal()" class="top-btn btn-board">📊 项目看板</button>
            <button onclick="openSopModal()" class="top-btn btn-sop">📜 公司制度/SOP</button>
        </div>
        <div class="btn-row">
            <button onclick="openBossMarket()" class="top-btn btn-boss">💼 BOSS 直聘</button>
            <button onclick="clearAllMemory()" class="top-btn btn-clear">🧹 一键清空记忆</button>
            <button onclick="bankruptcy()" class="top-btn btn-bankrupt">💥 一键破产</button>
        </div>
    </div>
</div>

<div class="guide-right" style="background: rgba(255,255,255,0.85);">
    <div id="onboardingGuide" style="color: #333; font-size: 0.95rem; line-height: 1.5;">
        <h3 style="margin-top:0; color:#2c3e50;">🏢 公司简介与工作指南</h3>
        <p style="color:#666;">HR还未配置公司简介... 点击左侧「一键开设公司」生成团队及指南。</p>
    </div>
</div>
    </div>

    <div class="desk-grid" id="mainDeskGrid">
        
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
            <textarea id="recruitResume" placeholder="个人简历/详细介绍 (如: 拥有10年爬虫经验，精通Python，性格严谨...)"
                style="padding:10px; border:1px solid #ddd; border-radius:8px; outline:none; font-size:12px; height:60px; resize:none; font-family:inherit;"></textarea>
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
            



<div style="display:flex; gap:10px; margin-top:12px;">
    <button onclick="confirmTask()"
        style="flex:2; padding:12px; background:linear-gradient(135deg, #4a90e2, #357abd); color:#fff; border:none; border-radius:8px; cursor:pointer; font-weight:bold; font-size:15px; box-shadow: 0 4px 10px rgba(74,144,226,0.3);">
        🚀 开始干活
    </button>
    <button onclick="cancelEmployeeTask()"
        style="flex:1; padding:12px; background:linear-gradient(135deg, #e74c3c, #c0392b); color:#fff; border:none; border-radius:8px; cursor:pointer; font-weight:bold; font-size:14px; box-shadow: 0 4px 10px rgba(231,76,60,0.3);">
        🛑 中止任务
    </button>
</div>

<div style="display:grid; grid-template-columns: 1fr 1fr; gap:8px; margin-top:12px;">
    <button onclick="editEmployee()"
        style="padding:10px; background:#f8f9fa; color:#2c3e50; border:1px solid #dee2e6; border-radius:8px; cursor:pointer; font-weight:bold;">
        📝 修改信息
    </button>
    <button onclick="clearEmployeeMemory()"
        style="padding:10px; background:#fff3cd; color:#856404; border:1px solid #ffeeba; border-radius:8px; cursor:pointer; font-weight:bold;">
        🧹 清空记忆
    </button>
    <button onclick="uploadToBoss()"
        style="padding:10px; background:#f3e5f5; color:#8e44ad; border:1px solid #e1bee7; border-radius:8px; cursor:pointer; font-weight:bold;">
        ⬆️ 上传至直聘
    </button>
    <button onclick="fireEmployee()"
        style="padding:10px; background:#fcf0f0; color:#c0392b; border:1px solid #fadbd8; border-radius:8px; cursor:pointer; font-weight:bold;">
        💥 开除员工
    </button>
</div>

<button onclick="closeModal('taskModal')"
    style="width:100%; margin-top:6px; padding:8px; background:transparent; color:#95a5a6; border:none; cursor:pointer; font-weight:bold;">
    取消关闭
</button>




        </div>
    </div>

<div id="bossModal" style="display:none; position:fixed; inset:0; background:rgba(0,0,0,0.6); z-index:10000; align-items:center; justify-content:center; backdrop-filter:blur(5px);">
    <div style="background:#f8fafc; padding:25px; border-radius:15px; height:80vh;width:80vw; display:flex; flex-direction:column; box-shadow:0 10px 30px rgba(0,0,0,0.2);">
        <div style="display:flex; justify-content:space-between; align-items:center; border-bottom: 1px solid #e2e8f0; padding-bottom: 15px; margin-bottom: 10px;">
            <h3 style="margin:0; color:#059669; display: flex; align-items: center; gap: 8px;">💼 人才市场</h3>
            <button onclick="closeModal('bossModal')" style="background:none; border:none; font-size:1.4em; cursor:pointer; color:#94a3b8; transition: color 0.2s;" onmouseover="this.style.color='#ef4444'" onmouseout="this.style.color='#94a3b8'">✖</button>
        </div>

        <input type="text" id="bossSearchInput" placeholder="🔍 搜索姓名、职位、能力..." oninput="filterBossMarket()" style="margin-bottom:12px; padding:10px; border:1px solid #cbd5e1; border-radius:8px; outline:none; font-size:14px; width:100%; box-sizing:border-box;">

        <div id="bossTalentList" style="flex:1; overflow-y:auto; display:flex; flex-direction:column; padding-right:8px;">
        </div>
    </div>
</div>

<div id="sopModal" style="display:none; position:fixed; inset:0; background:rgba(0,0,0,0.6); z-index:10000; align-items:center; justify-content:center; backdrop-filter:blur(3px);">
    <div style="background:#fff; padding:25px; border-radius:15px; width:450px; display:flex; flex-direction:column; gap:12px; box-shadow:0 10px 30px rgba(0,0,0,0.2);">
        <h3 style="margin:0 0 5px 0; text-align:center; color:#333;">📜 公司制度与工作流 (SOP)</h3>
        <p style="font-size:12px; color:#666; margin:0;">在此制定的规则将作为最高准则，在每次派发任务时强制注入给每一位员工的潜意识中。告诉他们谁该干什么，不该干什么。</p>

        <textarea id="sopInput" placeholder="例如：&#10;1. 我们的目标是写小说并发布。&#10;2. CEO只负责拆解大纲，不要自己写。&#10;3. 作家必须根据大纲写出完整的章节，不要只给方案。&#10;4. 必须有产出结果！禁止员工之间互相踢皮球！"
            style="padding:12px; border:1px solid #ddd; border-radius:8px; outline:none; height:180px; resize:none; font-family:inherit; font-size:13px; line-height:1.5;"></textarea>

        <div style="display:flex; gap:10px; margin-top:10px;">
            <button onclick="saveSopConfig()" style="flex:1; padding:10px; background:#2c3e50; color:#fff; border:none; border-radius:8px; cursor:pointer; font-weight:bold;">颁布制度</button>
            <button onclick="closeModal('sopModal')" style="flex:1; padding:10px; background:#eee; color:#666; border:none; border-radius:8px; cursor:pointer;">取消</button>
        </div>
    </div>
</div>


<div id="boardModal" style="display:none; position:fixed; inset:0; background:rgba(0,0,0,0.6); z-index:10050; align-items:center; justify-content:center; backdrop-filter:blur(5px);">
    <div style="background:#f8fafc; padding:25px; border-radius:15px; height:85vh; width:85vw; display:flex; flex-direction:column; box-shadow:0 10px 30px rgba(0,0,0,0.2);">
        <div style="display:flex; justify-content:space-between; align-items:center; border-bottom: 1px solid #e2e8f0; padding-bottom: 15px;">
            <h3 style="margin:0; color:#1e293b; display: flex; align-items: center; gap: 8px;">
                📊 项目看板: <span id="boardProjectName" style="color:#3b82f6;">正在同步进度...</span>
            </h3>
            <div style="display:flex; gap: 10px;">
                <button onclick="deleteProject()" style="padding:6px 12px; background:#e74c3c; color:white; border:none; border-radius:6px; cursor:pointer; font-weight:bold;">🗑️ 清空</button>
                <button onclick="closeModal('boardModal')" style="background:none; border:none; font-size:1.4em; cursor:pointer; color:#94a3b8;">✖</button>
            </div>
        </div>

        <div id="boardProjectList" class="project-list-container">
            </div>
    </div>
</div>


<div id="ceoModal" style="display:none; position:fixed; inset:0; background:rgba(0,0,0,0.6); z-index:10000; align-items:center; justify-content:center; backdrop-filter:blur(3px);">
    <div style="background:#fff; padding:25px; border-radius:15px; width:450px; display:flex; flex-direction:column; gap:12px; box-shadow:0 10px 30px rgba(0,0,0,0.2);">
        <div style="display:flex; justify-content:space-between; align-items:center;">
            <h3 style="margin:0; color:#2c3e50; font-size: 1.2rem;">👑 CEO 战略部署中心</h3>
            <button onclick="closeModal('ceoModal')" style="border:none; background:none; font-size:20px; cursor:pointer; color:#666;">✖</button>
        </div>
        <p style="font-size:12px; color:#666; margin:0;">请在此下达全局战略或新项目需求。提交后将自动交由 PM (ceo) 拆解，并更新至项目看板，全员自动跟进！</p>

        <textarea id="ceoTaskInput" placeholder="老板，请描述需求... (如: 写一个贪吃蛇游戏并部署到本地)"
            style="padding:12px; border:1px solid #ddd; border-radius:8px; outline:none; height:120px; resize:none; font-family:inherit; font-size:14px; background: #f8fafc;"></textarea>

        <div style="display:flex; gap:10px; margin-top:10px;">
            <button onclick="dispatchCeoTask()" style="flex:1; padding:12px; background:linear-gradient(135deg, #16a085, #27ae60); color:#fff; border:none; border-radius:8px; font-weight:bold; cursor:pointer; box-shadow:0 4px 10px rgba(39,174,96,0.3); transition: transform 0.2s;">
                🚀 一键发布战略并更新看板
            </button>
        </div>
    </div>
</div>

<div id="strategyEditModal" style="display:none; position:fixed; inset:0; background:rgba(0,0,0,0.6); z-index:10050; align-items:center; justify-content:center; backdrop-filter:blur(3px);">
    <div style="background:#f8fafc; padding:25px; border-radius:15px; width:550px; max-height: 80vh; display:flex; flex-direction:column; gap:15px; box-shadow:0 10px 30px rgba(0,0,0,0.2);">
        <div style="display:flex; justify-content:space-between; align-items:center; border-bottom: 1px solid #e2e8f0; padding-bottom: 10px;">
            <h3 style="margin:0; color:#2c3e50; font-size: 1.2rem;">🛠️ 战略二次编排</h3>
            <button onclick="closeModal('strategyEditModal')" style="border:none; background:none; font-size:20px; cursor:pointer; color:#666;">✖</button>
        </div>

        <div style="display: flex; align-items: center; gap: 10px;">
            <strong style="color: #475569; width: 80px;">项目名称:</strong>
            <input type="text" id="editProjectName" style="flex:1; padding:10px; border:1px solid #cbd5e1; border-radius:8px; outline:none; font-weight:bold; color:#1e293b;">
        </div>

        <div style="flex:1; overflow-y:auto; padding-right: 5px; background: #fff; border: 1px solid #e2e8f0; border-radius: 8px; padding: 10px;" id="editTaskListContainer">
            </div>

        <div style="display:flex; gap:10px; margin-top:5px;">
            <button onclick="addStrategyTask()" style="padding:10px 15px; background:#f1f5f9; color:#475569; border:1px solid #cbd5e1; border-radius:8px; font-weight:bold; cursor:pointer; transition: 0.2s;">
                ➕ 新增一项任务
            </button>
            <div style="flex: 1;"></div>
            <button onclick="confirmAndDispatchStrategy()" style="padding:10px 20px; background:linear-gradient(135deg, #16a085, #27ae60); color:#fff; border:none; border-radius:8px; font-weight:bold; cursor:pointer; box-shadow:0 4px 10px rgba(39,174,96,0.3);">
                🚀 确认发布并执行
            </button>
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


function openCeoModal() {
    document.getElementById('ceoTaskInput').value = ''; // 打开时清空上次内容
    document.getElementById('ceoModal').style.display = 'flex';
}

async function dispatchCeoTask() {
    const taskContent = document.getElementById('ceoTaskInput').value.trim();
    if (!taskContent) return alert("老板，战略内容不能为空！你得先画大饼啊！");
    closeModal('ceoModal');
    const companySop = window.teamConfig?.CompanySOP || "";
    let pmName = "ceo";

    // 1. 获取所有除 ceo 以外的员工
    const allNodes = Object.entries(window.teamConfig?.PeerNodes || {}).filter(([name, info]) => {
        return name && name !== 'undefined' && name.toLowerCase() !== 'ceo';
    });

    if (allNodes.length === 0) {
        return alert("公司除了老板没别人了，先去招人吧！");
    }

    // ================= 【核心修改：从项目看板排期过滤忙碌员工】 =================
    document.getElementById('loadingOverlay').style.display = 'flex';
    document.querySelector('.hr-loading-text').innerText = `正在确认员工档期...`;
    document.querySelector('.hr-loading-subtext').innerText = `正在查阅所有历史项目的任务排期表...`;

    let busyEmployees = new Set();
    try {
        // 直接向中控拉取最新的全局项目看板
        const boardRes = await fetch('/api/board');
        if (boardRes.ok) {
            const projects = await boardRes.json();
            projects.forEach(p => {
                if (p.tasks && p.tasks.length > 0) {
                    p.tasks.forEach(t => {
                        // 只要状态不是 done (即 todo 或 doing)，这个人就被上个项目占用了
                        const status = (t.status || t.Status || 'todo').toLowerCase();
                        if (status !== 'done') {
                            const assignee = t.assignee || t.Assignee;
                            if (assignee) busyEmployees.add(assignee);
                        }
                    });
                }
            });
        }
    } catch (e) {
        console.warn("拉取看板排期失败，跳过排期检测", e);
    }

    // 过滤出真正闲置的员工（名字不在 busyEmployees 集合中的）
    const availableNodes = allNodes.filter(([name, info]) => !busyEmployees.has(name));

    if (availableNodes.length === 0) {
        document.getElementById('loadingOverlay').style.display = 'none';
        return alert("老板，项目看板上大家都有没做完的任务（处于 todo 或 doing 状态）！\n为了防止并发冲突，请等他们清空手头任务，或者再去直聘招点新人吧。");
    }

    // 2. 仅使用【排期空闲员工】生成能力边界清单
    const teamInfo = availableNodes.map(([name, info]) => 
        `- 姓名: ${name}, 岗位: ${info.Role || info.role || '未知'}, 职责: ${info.Description || info.description || '暂无'}`
    ).join('\n');
    // ================= 【核心修改结束】 =================


const planPrompt = `【全局任务调度与执行编排引擎】
接收到原始指令：
${taskContent}

当前可用执行单元及其能力边界如下：
${teamInfo}

请作为“绝对务实、结果导向”的智能调度中枢（Dispatcher），动态感知指令的真实业务规模，并直接输出纯 JSON 格式的终态执行任务。

【核心调度逻辑】（极其重要，必须严格遵循）
1. **动态规模感知与自适应拆解（去领域化）**：
   - 【单点/线性动作】：当指令目标单一、步骤明确、无复杂前置依赖时（闭环成本极低），**保持极度克制**。直接将其转化为 1 个原子动作，匹配给唯一最合适的执行单元。绝对禁止主观扩充流程或臆想“前期调研”。
   - 【系统/复合工程】：当指令具有宏观性、包含多模块或需要跨能力域协同协作时，**必须进行降维解构**。跳过所有“需求分析”、“架构规划”、“开会统筹”等务虚环节，直接将其平行拆解为【各子领域第一阶段的实质性落地动作】，并按能力图谱分别派发。
2. **绝对泛化与结果约束**：无论指令属于数字系统、物理控制、文本处理还是逻辑运算，调度中枢只关心“实质性产出”。所有拆解出的任务，必须是可以立即动手的终端执行动作，拒绝一切中间态文档。
3. **动作定义标准**：任务标题 (title) 必须是简练的动宾结构，直接指明操作对象与最终结果。

【严格输出规范】
禁止使用任何工具！禁止包含任何解释性文字！禁止使用 Markdown 的 \`\`\`json 标签！直接且仅输出以下 JSON 字符串：
{
  "project_name": "提炼精准的指令归纳名称",
  "tasks": [
    {
      "title": "具体要执行的实质性落地动作",
      "assignee": "分配的执行单元姓名（必须从输入名单中精确提取）"
    }
  ]
}`;


    document.getElementById('loadingOverlay').style.display = 'flex';
    document.querySelector('.hr-loading-text').innerText = `规划项目计划并生成看板...`;
    document.querySelector('.hr-loading-subtext').innerText = `ceo开始思考中...`;
    try {
        await fetch('/api/clear', {
            method: 'POST',
            headers: { 'X-Username': encodeURIComponent(pmName) }
        }).catch(e => console.warn("静默清理ceo上下文失败", e));


        // 获取计划
        const res = await fetch('/api/chat', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'X-Username': encodeURIComponent(pmName) },
            body: JSON.stringify({
                message: planPrompt,
                modelIndex: window.teamConfig?.PeerNodes?.[pmName]?.ModelIndex || 0,
                sop: companySop
            })
        });

        // 拼装流式返回的数据
        const reader = res.body.getReader();
        const decoder = new TextDecoder();
        let fullText = '';
        let buffer = '';
        while (true) {
            const { value, done } = await reader.read();
            if (done) break;
            buffer += decoder.decode(value, { stream: true });
            const parts = buffer.split('|||END|||');
            buffer = parts.pop();
            for (const part of parts) {
                if(!part.trim()) continue;
                try {
                    const msg = JSON.parse(part);
                    // 抓取模型输出的文本
                    if (msg.type === 'final' || msg.type === 'tool_result') fullText += msg.content;
                } catch(e){}
            }
        }

        // 3. 粗暴且安全的 JSON 清洗提取
        let jsonStr = fullText.replace(/```json/gi, '').replace(/```/g, '').trim();
        const startIdx = jsonStr.indexOf('{');
        const endIdx = jsonStr.lastIndexOf('}');
        if (startIdx >= 0 && endIdx > startIdx) {
            jsonStr = jsonStr.substring(startIdx, endIdx + 1);
        }

        const boardData = JSON.parse(jsonStr);


        if (boardData.tasks && Array.isArray(boardData.tasks)) {
            boardData.tasks.forEach((t, index) => {
                if (!t.id) {
                    t.id = Math.random().toString(36).substring(2, 10);
                }
                if (!t.status) {
                    t.status = "todo";
                }
            });
        }

        // ================= 【核心修改：拦截直接执行，进入二次编排弹窗】 =================
        window.tempStrategyBoardData = boardData; // 存入全局变量供弹窗二次编辑
        document.getElementById('loadingOverlay').style.display = 'none'; // 先关掉加载动画
        openStrategyEditModal(); // 打开二次编排弹窗

    } catch (e) {
        document.getElementById('loadingOverlay').style.display = 'none';
        alert("❌ 项目生成失败，可能是 AI 脑子抽风没按格式返回 JSON，请重试或检查后台日志。\n" + e.message);
    }
}
// ================= 二次编排相关逻辑 =================

function openStrategyEditModal() {
    const data = window.tempStrategyBoardData;
    document.getElementById('editProjectName').value = data.project_name || "未命名新项目";
    renderStrategyTasks();
    document.getElementById('strategyEditModal').style.display = 'flex';
}

function renderStrategyTasks() {
    const container = document.getElementById('editTaskListContainer');
    container.innerHTML = '';
    const tasks = window.tempStrategyBoardData.tasks || [];

    // 动态拉取当前所有在职员工名单（不含ceo）
    const employees = Object.keys(window.teamConfig?.PeerNodes || {}).filter(n => n !== 'ceo');

    tasks.forEach((t, i) => {
        // 构建员工下拉框
        let optionsHtml = employees.map(emp => 
            `<option value="${emp}" ${t.assignee === emp ? 'selected' : ''}>👤 ${emp}</option>`
        ).join('');

        container.innerHTML += `
            <div style="display:flex; gap:10px; margin-bottom:12px; align-items:center; background:#f8fafc; padding:10px; border-radius:8px; border:1px solid #e2e8f0;">
                <div style="display:flex; flex-direction:column; flex:1; gap:6px;">
                    <input type="text" value="${escapeHtml(t.title)}" onchange="window.tempStrategyBoardData.tasks[${i}].title = this.value" style="padding:8px; border:1px solid #cbd5e1; border-radius:6px; outline:none; font-size:14px;" placeholder="任务具体内容">
                    <div style="display:flex; align-items:center; gap:8px;">
                        <span style="font-size:12px; color:#64748b;">负责人:</span>
                        <select onchange="window.tempStrategyBoardData.tasks[${i}].assignee = this.value" style="padding:6px; border:1px solid #cbd5e1; border-radius:6px; font-size:13px; outline:none; cursor:pointer;">
                            <option value="">❓ 待认领</option>
                            ${optionsHtml}
                        </select>
                    </div>
                </div>
                <button onclick="deleteStrategyTask(${i})" style="padding:10px; background:#fef2f2; color:#ef4444; border:1px solid #fecaca; border-radius:8px; cursor:pointer; font-size:16px;" title="删除此任务">🗑️</button>
            </div>
        `;
    });

    if (tasks.length === 0) {
        container.innerHTML = `<div style="text-align:center; padding:20px; color:#94a3b8;">当前项目还没有任何任务，请手动新增。</div>`;
    }
}

function addStrategyTask() {
    if (!window.tempStrategyBoardData.tasks) window.tempStrategyBoardData.tasks = [];
    window.tempStrategyBoardData.tasks.push({
        id: Math.random().toString(36).substring(2, 10),
        title: "",
        assignee: "",
        status: "todo"
    });
    renderStrategyTasks(); // 刷新列表
}

function deleteStrategyTask(idx) {
    window.tempStrategyBoardData.tasks.splice(idx, 1);
    renderStrategyTasks(); // 刷新列表
}

async function confirmAndDispatchStrategy() {
    const boardData = window.tempStrategyBoardData;
    boardData.project_name = document.getElementById('editProjectName').value.trim() || "未命名新项目";

    // 过滤掉没填内容的空任务
    boardData.tasks = (boardData.tasks || []).filter(t => t.title.trim() !== "");

    if (boardData.tasks.length === 0) {
        return alert("⚠️ 必须至少保留一个非空的任务才能发布！");
    }

    closeModal('strategyEditModal');
    document.getElementById('loadingOverlay').style.display = 'flex';
    document.querySelector('.hr-loading-text').innerText = '正在立项并将任务托管给中控后台...';

    try {
        // 👇 你只需要把 JSON 丢给后端一次！剩下的全部交由 C# 接管！
        await fetch('/api/board', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(boardData)
        });

        await refreshBoard(); // 刷新网页端的看板显示

        // 搞定！现在就算你立刻按下 F5 刷新网页，或者拔掉电脑电源，后端的 C# 也会忠实地把任务发完。
        alert(`✅ 项目【${boardData.project_name}】已成功立项并托管给中控！\n中控后台正在向各位员工并发推送指令，您现在可以安全地刷新页面或关闭浏览器了。`);
        document.getElementById('ceoTaskInput').value = '';

    } catch (e) {
        alert("❌ 立项失败，请检查网络：" + e.message);
    } finally {
        document.getElementById('loadingOverlay').style.display = 'none';
    }
}

// 动态计算并适配网格宽度 (手机端受 CSS 控制永远两列)
function adjustGridColumns() {
    const grid = document.getElementById('mainDeskGrid');
    const totalDesks = grid.children.length;

    if (window.innerWidth >= 768) {
        if (totalDesks > 21) {
            // 超出 21 个，按比例缩小卡片的最小宽度，让屏幕全摆下
            const newMinWidth = Math.max(80, Math.floor(180 * (21 / totalDesks)));
            grid.style.gridTemplateColumns = `repeat(auto-fill, minmax(${newMinWidth}px, 1fr))`;
        } else {
            // 少于 21 个，保持原本的宽度
            grid.style.gridTemplateColumns = `repeat(auto-fill, minmax(180px, 1fr))`;
        }
    } else {
        // 手机端清空内联样式，回归 CSS 的 repeat(2, 1fr)
        grid.style.gridTemplateColumns = ''; 
    }
}
window.addEventListener('resize', adjustGridColumns);

// 动态创建一个工位 DOM
function createDeskElement() {
    const desk = document.createElement('div');
    desk.className = 'company-card empty-desk';
    desk.innerHTML = `
        <div class="desk-img-container">
            <img src="img_shrimp_working.png" alt="皮皮虾办公" class="shrimp-desk-img">
            <img src="img_empty_desk.png" alt="空桌子" class="empty-desk-img">
            <img src="penzai2.png" class="desk-penzai">
            <img src="penzai1.png" class="desk-penzai-2">
        </div>
    `;
    desk.style.cursor = 'pointer';

    desk.addEventListener('click', () => {
        if (desk.classList.contains('empty-desk') || desk.classList.contains('away')) {
            currentTargetDesk = desk;
            document.getElementById('recruitName').value = '';
            document.getElementById('recruitRole').value = '';
            document.getElementById('recruitUrl').value = '';
            document.getElementById('recruitModal').style.display = 'flex';
        } else if (desk.classList.contains('at-work')) {
            currentTargetDesk = desk;
            currentTargetName = desk.querySelector('.id-card-name').innerText;
            document.getElementById('taskModalTitle').innerText = `管理员工：${currentTargetName}`;
            document.getElementById('taskInput').value = '';
            document.getElementById('taskModal').style.display = 'flex';
        }
    });
    document.getElementById('mainDeskGrid').appendChild(desk);
    adjustGridColumns();
    return desk;
}


function openSopModal() {
    // 弹窗打开时，回显当前保存的 SOP
    document.getElementById('sopInput').value = window.teamConfig.CompanySOP || '';
    document.getElementById('sopModal').style.display = 'flex';
}

async function saveSopConfig() {
    const newSop = document.getElementById('sopInput').value.trim();
    if (!window.teamConfig) return;

    window.teamConfig.CompanySOP = newSop;

    try {
        const res = await fetch('/api/config', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(window.teamConfig)
        });
        if (res.ok) {
            alert("✅ 公司制度已全网同步颁布！下一次安排任务时将生效。");
            closeModal('sopModal');
        } else {
            alert("❌ 同步失败，请检查网络。");
        }
    } catch (e) {
        console.error(e);
        alert("❌ 网络请求异常。");
    }
}

async function openBoardModal() {
    document.getElementById('boardModal').style.display = 'flex';
    await refreshBoard();
}

async function refreshBoard() {
    try {
        const res = await fetch('/api/board');
        if (!res.ok) return;
        const projects = await res.json(); 

        const listContainer = document.getElementById('boardProjectList');
        listContainer.innerHTML = '';

        if (projects && projects.length > 0) {
            document.getElementById('boardProjectName').innerText = `共 ${projects.length} 个并行项目`;

            projects.forEach((p, index) => {
                const projName = escapeHtml(p.project_name || p.ProjectName);
                let tasksHtml = '';
                let totalTasks = 0;
                let doneTasks = 0;

                // 构建任务列表
                if (p.tasks && p.tasks.length > 0) {
                    totalTasks = p.tasks.length;
                    p.tasks.forEach(t => {
                        const statusClass = (t.status || t.Status || 'todo').toLowerCase();
                        if (statusClass === 'done') doneTasks++;

                        let statusText = '待办 (TODO)';
                        if (statusClass === 'doing') statusText = '进行中 (DOING)';
                        if (statusClass === 'done') statusText = '已完成 (DONE)';

                        // 格式化时间去秒
                        const updateTime = (t.update_time || t.UpdateTime || '').substring(5, 16);
                        let resultHtml = '';
                        const taskResult = t.result || t.Result;
                        if (taskResult) {
                            resultHtml = `
                            <div style="margin-top: 8px; padding: 10px 14px; background: rgba(16, 185, 129, 0.05); border-left: 3px solid #10b981; border-radius: 6px; font-size: 0.85em; color: #475569; line-height: 1.5;">
                                <strong style="color: #059669; display: block; margin-bottom: 4px;">🎯 交付总结：</strong>
                                <span style="white-space: pre-wrap; font-family: inherit;">${escapeHtml(taskResult)}</span>
                            </div>`;
                        }

                        tasksHtml += `
                        <div class="task-row" style="display:flex; flex-direction:column; align-items:stretch; padding: 12px 0;">
                            <div style="display:flex; justify-content:space-between; align-items:center;">
                                <div class="task-title">▪ ${escapeHtml(t.title || t.Title)}</div>
                                <div class="task-badges">
                                    <span class="badge assignee">👤 ${escapeHtml(t.assignee || t.Assignee)}</span>
                                    <span class="badge ${statusClass}">${statusText}</span>
                                    <span style="color:#94a3b8; width: 85px; text-align: right;">${updateTime}</span>
                                </div>
                            </div>
                            ${resultHtml}
                        </div>`;
                    });
                } else {
                    tasksHtml = '<div style="text-align:center; color:#94a3b8; padding:20px 0;">该项目下暂无拆解的子任务。</div>';
                }

                // 计算进度百分比
                let progress = totalTasks === 0 ? 0 : Math.round((doneTasks / totalTasks) * 100);

                // 默认展开第一个项目，其余折叠
                const isExpanded = index === 0 ? 'expanded' : '';
                const displayStyle = index === 0 ? 'style="display:block;"' : 'style="display:none;"';
                const toggleIcon = index === 0 ? '▼' : '▶';

                // 组装整个项目的 HTML
                const projHtml = `
                <div class="project-group">
                    <div class="project-header ${isExpanded}" onclick="toggleProject(this)">
                        <div class="project-title">
                            <span class="toggle-icon" style="color:#94a3b8; font-size:0.8em; width:15px; display:inline-block;">${toggleIcon}</span>
                            📁 ${projName}
                        </div>
                        <div class="project-progress-container">
                            <div style="font-size:0.75em; color:#64748b; margin-bottom:2px; display:flex; justify-content:space-between;">
                                <span>整体进度</span>
                                <span>${doneTasks} / ${totalTasks} 任务</span>
                            </div>
                            <div class="progress-bar-bg">
                                <div class="progress-bar-fill" style="width: ${progress}%;"></div>
                            </div>
                        </div>
                        <div class="project-meta">
                            ${progress}%
                        </div>
                    </div>
                    <div class="task-list" ${displayStyle}>
                        ${tasksHtml}
                    </div>
                </div>`;

                listContainer.innerHTML += projHtml;
            });
        } else {
            document.getElementById('boardProjectName').innerText = '当前暂无推进中的大项目';
            listContainer.innerHTML = '<div style="text-align:center; color:#94a3b8; padding:50px; background:#fff; border-radius:10px; border:1px dashed #cbd5e1;">AI CEO 尚未建立任何项目或拆解任务</div>';
        }
    } catch (e) { console.error("看板拉取失败", e); }
}

// 新增：折叠/展开动画控制函数
function toggleProject(headerEl) {
    const taskList = headerEl.nextElementSibling;
    const icon = headerEl.querySelector('.toggle-icon');
    const isExpanded = headerEl.classList.contains('expanded');

    if (isExpanded) {
        headerEl.classList.remove('expanded');
        taskList.style.display = 'none';
        icon.innerText = '▶';
    } else {
        headerEl.classList.add('expanded');
        taskList.style.display = 'block';
        icon.innerText = '▼';
    }
}

async function deleteProject() {
    if(!confirm("⚠️ 确定要一键清空当前所有项目进度和任务吗？这通常用于项目验收结项后。")) return;
    try {
        const res = await fetch('/api/board', { method: 'DELETE' });
        if(res.ok) {
            alert('✅ 项目已归档结项！');
            await refreshBoard();
        }
    } catch(e) { alert('操作异常！'); }
}



function ensureOneEmptyDesk() {
    const grid = document.getElementById('mainDeskGrid');
    const emptyDesks = Array.from(grid.querySelectorAll('.company-card.empty-desk'));

    if (emptyDesks.length === 0) {
        createDeskElement();
    } else if (emptyDesks.length > 1) {
        // 如果空桌子多了(比如刚开除了员工)，就清理掉，保持只留一个
        for (let i = 1; i < emptyDesks.length; i++) emptyDesks[i].remove();
        adjustGridColumns();
    }
}

        window.addEventListener('load', async () => {
            const guideDiv = document.getElementById('onboardingGuide');


            try {
            const res = await fetch('/api/config');
            if (!res.ok) return;
            const cfg = await res.json();
            window.teamConfig = cfg;

            document.getElementById('companyNameDisplay').innerText = cfg.CompanyName || "未命名皮皮虾公司";
            const licenseTag = document.getElementById('licenseTag');
            if (cfg.HasLicense) {
                licenseTag.innerText = "✅ 已获取营业执照";
                licenseTag.style.background = "#2ecc71"; // 绿色
            } else {
                licenseTag.innerText = "⚠️ 未获取营业执照";
                licenseTag.style.background = "#e74c3c"; // 红色
            }


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
                const grid = document.getElementById('mainDeskGrid');
                grid.innerHTML = ''; // 清除可能残留的旧工位

                let index = 0;
                for (const [name, info] of Object.entries(cfg.PeerNodes)) {
                    if (name === 'ceo') continue;
                    const desk = createDeskElement(); // 动态生成工位
                    const role = info.Role || info.role || '资深员工';
                    renderEmployeeUI(desk, name, role);
                    index++;
                }
                console.log(`[初始化] 已成功加载 ${index} 位员工。`);

                ensureOneEmptyDesk(); // 自动补齐 1 个空位用来招募新员工

                // 遍历所有在岗员工的 DOM 拉取记录
                const allActiveDesks = document.querySelectorAll('.company-card.at-work');
                allActiveDesks.forEach(desk => {
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
                    });
                }
            } catch (e) {
                console.error("加载配置失败:", e);
            }
        });

function escapeHtml(s) {
    if (s == null) return '';
    return String(s)
        .replaceAll('&', '&amp;')
        .replaceAll('<', '&lt;')
        .replaceAll('>', '&gt;')
        .replaceAll('"', '&quot;')
        .replaceAll("'", "&#39;");
}


async function cancelEmployeeTask() {
    if (!currentTargetDesk || !currentTargetName) return;

    // 二次确认防误触
    if (!confirm(`老板，确定要强制中止【${currentTargetName}】当前正在执行的任务吗？`)) {
        return;
    }

    try {
        const res = await fetch('/api/cancel', {
            method: 'POST',
            headers: {
                'X-Username': encodeURIComponent(currentTargetName)
            }
        });

        if (res.ok) {
            alert(`✅ 已向【${currentTargetName}】下达任务中止指令！`);
            closeModal('taskModal');

            // 手动把工位上的状态气泡切成中止状态
            const bubble = currentTargetDesk.querySelector('.chat-bubble');
            if (bubble) {
                bubble.classList.remove('thinking');
                bubble.innerText = '⚠️ 任务已强行中止';
            }
        } else {
            alert(`❌ 中止失败，该员工的底层节点可能离线或未响应。`);
        }
    } catch (e) {
        console.error('中止任务请求异常:', e);
        alert(`❌ 网络请求异常，请检查控制台。`);
    }
}


// 4. 从直聘大厅下架员工
async function deleteFromBoss(agentId, agentName) {
    if (!confirm(`⚠️ 危险操作：\n确定要从人才市场下架【${agentName}】吗？\n下架后云端的简历和压缩包将被永久删除，不可恢复！`)) return;

    try {
        const res = await fetch('/api/boss/delete', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ id: agentId })
        });

        if (res.ok) {
            alert(`✅ 已成功将【${agentName}】从市场下架！`);
            openBossMarket(); // 刷新列表，员工消失
        } else if (res.status === 403) {
            alert(`❌ 下架失败：权限不足！您只能下架属于自己【${document.getElementById('companyNameDisplay').innerText}】的员工。`);
        } else {
            alert(`❌ 下架失败，BOSS 服务器异常。`);
        }
    } catch (e) {
        alert("网络异常: " + e.message);
    }
}
// 【新增】：在人才市场查看员工简历
function viewResume(id) {
    if (!window.bossTalentCache) return;

    // 通过 ID 找到对应的员工数据
    const talent = window.bossTalentCache.find(t => t.Id === id);
    if (talent) {
        const talentName = talent.Name || talent.name || '未知员工';

        // 偷个懒，直接复用现成的工作报告弹窗 (reportModal)
        document.getElementById('reportTitle').innerText = `📄 【${talentName}】的个人简历`;

        let content = talent.Resume || talent.resume || '这个人很神秘，没有留下任何简历信息...';

        // 支持 Markdown 渲染，让简历看起来更高大上
        if (typeof marked !== 'undefined') {
            document.getElementById('reportMarkdown').innerHTML = marked.parse(content);
        } else {
            document.getElementById('reportMarkdown').innerHTML = `<pre style="white-space:pre-wrap; font-family:inherit;">${content}</pre>`;
        }

        // 显示弹窗
        document.getElementById('reportModal').style.display = 'flex';
    }
}
// 1. 负责渲染列表的函数（完全用你原来的 HTML 结构）
function renderBossTalentHtml(list) {
    const listContainer = document.getElementById('bossTalentList');
    if (!list || list.length === 0) {
        listContainer.innerHTML = '<div style="text-align:center; color:#64748b; padding: 40px; background: #fff; border-radius: 10px; border: 1px dashed #cbd5e1;">当前市场没有正在求职的员工数据包。</div>';
        return;
    }

    let html = '';
    const currentCompanyName = document.getElementById('companyNameDisplay').innerText; 
    list.forEach(t => {
        const talentName = t.Name || t.name || '未知员工';
        const avatar = getAvatarByName(talentName);
        const isMyTeam = t.TeamId === currentCompanyName;

        const deleteBtnHtml = isMyTeam ? 
            `<button class="btn-hire" style="background: linear-gradient(135deg, #e74c3c, #c0392b); box-shadow: 0 4px 10px rgba(231, 76, 60, 0.2);" onclick="deleteFromBoss('${t.Id}', '${escapeHtml(talentName)}')">🗑️ 下架</button>` 
            : '';

        html += `
        <div class="talent-card">
            <div class="talent-avatar">
                <img src="${avatar}" onerror="this.src='img_shrimp_working.png'">
            </div>
            <div class="talent-info">
                <div class="talent-header">
                    <span class="talent-name" title="${escapeHtml(talentName)}">${escapeHtml(talentName)}</span>
                    <span class="talent-role" style="background:#8e44ad; font-size: 0.7em;">[${escapeHtml(t.TeamId)}]</span>
                    <span class="talent-role">${escapeHtml(t.Role)}</span>
                </div>
                <div class="talent-desc" title="${escapeHtml(t.Description)}">${escapeHtml(t.Description)}</div>
            </div>
            <div class="talent-action" style="display:flex; gap:8px;">
                <button class="btn-hire" style="background: linear-gradient(135deg, #f39c12, #d35400); box-shadow: 0 4px 10px rgba(243, 156, 18, 0.2);" onclick="viewResume('${t.Id}')">📄 简历</button>
                <button class="btn-hire" onclick="hireFromBoss('${escapeHtml(talentName)}')">📥 录用</button>
                ${deleteBtnHtml}
            </div>
        </div>`;
    });
    listContainer.innerHTML = html;
}

// 2. 搜索过滤逻辑
function filterBossMarket() {
    if (!window.bossTalentCache) return;
    const keyword = document.getElementById('bossSearchInput').value.trim().toLowerCase();

    if (!keyword) {
        renderBossTalentHtml(window.bossTalentCache);
        return;
    }

    const filteredList = window.bossTalentCache.filter(t => {
        const name = (t.Name || t.name || '').toLowerCase();
        const role = (t.Role || '').toLowerCase();
        const desc = (t.Description || '').toLowerCase();
        const resume = (t.Resume || '').toLowerCase();
        return name.includes(keyword) || role.includes(keyword) || desc.includes(keyword) || resume.includes(keyword);
    });

    renderBossTalentHtml(filteredList);
}

// 1. 负责渲染列表的函数
function renderBossTalentHtml(list) {
    const listContainer = document.getElementById('bossTalentList');
    if (!list || list.length === 0) {
        listContainer.innerHTML = '<div style="text-align:center; color:#64748b; padding: 40px; background: #fff; border-radius: 10px; border: 1px dashed #cbd5e1;">当前市场没有正在求职的员工数据包。</div>';
        return;
    }

    let html = '';
    const currentCompanyName = document.getElementById('companyNameDisplay').innerText; 
    list.forEach(t => {
        const talentName = t.Name || t.name || '未知员工';
        const avatar = getAvatarByName(talentName);
        const isMyTeam = t.TeamId === currentCompanyName;

        const deleteBtnHtml = isMyTeam ? 
            `<button class="btn-hire" style="background: linear-gradient(135deg, #e74c3c, #c0392b); box-shadow: 0 4px 10px rgba(231, 76, 60, 0.2);" onclick="deleteFromBoss('${t.Id}', '${escapeHtml(talentName)}')">🗑️ 下架</button>` 
            : '';

        html += `
        <div class="talent-card">
            <div class="talent-avatar">
                <img src="${avatar}" onerror="this.src='img_shrimp_working.png'">
            </div>
            <div class="talent-info">
                <div class="talent-header">
                    <span class="talent-name" title="${escapeHtml(talentName)}">${escapeHtml(talentName)}</span>
                    <span class="talent-role" style="background:#8e44ad; font-size: 0.7em;">[${escapeHtml(t.TeamId)}]</span>
                    <span class="talent-role">${escapeHtml(t.Role)}</span>
                </div>
                <div class="talent-desc" title="${escapeHtml(t.Description)}">${escapeHtml(t.Description)}</div>
            </div>
            <div class="talent-action" style="display:flex; gap:8px;">
                <button class="btn-hire" style="background: linear-gradient(135deg, #f39c12, #d35400); box-shadow: 0 4px 10px rgba(243, 156, 18, 0.2);" onclick="viewResume('${t.Id}')">📄 简历</button>
                <button class="btn-hire" onclick="hireFromBoss('${escapeHtml(talentName)}')">📥 录用</button>
                ${deleteBtnHtml}
            </div>
        </div>`;
    });
    listContainer.innerHTML = html;
}

// 2. 搜索过滤逻辑
function filterBossMarket() {
    if (!window.bossTalentCache) return;
    const keyword = document.getElementById('bossSearchInput').value.trim().toLowerCase();

    if (!keyword) {
        renderBossTalentHtml(window.bossTalentCache);
        return;
    }

    const filteredList = window.bossTalentCache.filter(t => {
        const name = (t.Name || t.name || '').toLowerCase();
        const role = (t.Role || '').toLowerCase();
        const desc = (t.Description || '').toLowerCase();
        const resume = (t.Resume || '').toLowerCase();
        return name.includes(keyword) || role.includes(keyword) || desc.includes(keyword) || resume.includes(keyword);
    });

    renderBossTalentHtml(filteredList);
}

// 3. 原始的打开市场逻辑
async function openBossMarket() {
    document.getElementById('bossModal').style.display = 'flex';

    const searchInput = document.getElementById('bossSearchInput');
    if (searchInput) searchInput.value = '';

    const listContainer = document.getElementById('bossTalentList');

    listContainer.innerHTML = `
        <div style="text-align:center; color:#94a3b8; padding: 40px;">
            <div class="hr-spinner" style="margin: 0 auto 15px; width: 40px; height: 40px;"></div>
            正在连接云端人才库...
        </div>`;

    try {
        const res = await fetch('/api/boss/list');
        if (!res.ok) throw new Error('网络错误');
        const list = await res.json();

        window.bossTalentCache = list; 
        renderBossTalentHtml(list);
    } catch (e) {
        listContainer.innerHTML = '<div style="color:#ef4444; text-align:center; padding: 40px; background: #fff; border-radius: 10px;">❌ 无法连接到BOSS服务器，请检查后端路由。</div>';
    }
}



// 2. 从直聘大厅录用员工
async function hireFromBoss(agentName) {
    const nodeUrl = prompt(`请分配物理工位：\n要把【${agentName}】的数据包解压部署到哪个底层节点URL？`, "http://127.0.0.1:5050");
    if (!nodeUrl) return;

    if (!confirm(`确定录用【${agentName}】？\n系统将拉取其完整的 ZIP 数据（含记忆、技能库、历史）并部署至 ${nodeUrl}。`)) return;

    document.getElementById('loadingOverlay').style.display = 'flex';
    document.querySelector('.hr-loading-text').innerText = 'HR正在疯狂下载数据包并办理入职...';

    try {
        const res = await fetch('/api/boss/hire', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ id: agentId, name: agentName, nodeUrl: nodeUrl })
        });
        if (res.ok) {
            alert(`🎉 搞定！【${agentName}】已空降工位，技能记忆全部恢复。`);
            location.reload();
        } else {
            alert("❌ 录用失败，请检查目标节点是否存活或跨域配置。");
        }
    } catch (e) {
        alert("网络异常: " + e.message);
    } finally {
        document.getElementById('loadingOverlay').style.display = 'none';
        document.querySelector('.hr-loading-text').innerText = 'HR 正在拼命招人中...';
    }
}

async function uploadToBoss() {
    if (!currentTargetName) return;
    if (!confirm(`确定要把【${currentTargetName}】打包上传吗？\n系统将自动生成 ZIP，并推送到云端交易大厅。`)) return;

    closeModal('taskModal');
    document.getElementById('loadingOverlay').style.display = 'flex';
    document.querySelector('.hr-loading-text').innerText = '正在将员工打包并上传至云端...';

    try {
        // Header 里已经不需要传 TeamID 了，后端会自动去读取 _config.CompanyName
        const res = await fetch('/api/boss/upload', {
            method: 'POST',
            headers: { 'X-Username': encodeURIComponent(currentTargetName) }
        });
        if (res.ok) {
            alert(`✅ 【${currentTargetName}】的数据包已成功挂至人才市场！`);
        } else {
            alert("❌ 上传失败，请检查是否获取了营业执照，或后端是否连通了 BOSS 服务器。");
        }
    } catch (e) {
        alert("网络异常: " + e.message);
    } finally {
        document.getElementById('loadingOverlay').style.display = 'none';
    }
}

// [新增] 点击铭牌修改公司名的逻辑
async function openLicenseModal() {
    const currentName = document.getElementById('companyNameDisplay').innerText;
    const newName = prompt("【公司工商注册系统】\n请输入您的公司名称：\n(系统将自动尝试向 BOSS 直聘大厅申请营业执照)", currentName);

    if (!newName || newName === currentName) return;

    try {
        const res = await fetch('/api/boss/register', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ companyName: newName })
        });
        if (res.ok) {
            alert(`🎉 恭喜！【${newName}】已成功在 BOSS 交易大厅注册并获取营业执照！可以正常向市场输送人才了。`);
        } else {
            alert(`⚠️ 【${newName}】已在本地修改，但连接 BOSS 大厅注册失败。\n当前状态：未获取营业执照 (团队仅限本地运作)`);
        }
        // 刷新页面重新拉取最新的 config 渲染铭牌
        location.reload(); 
    } catch(e) {
        alert("网络请求异常！");
    }
}





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
                    document.getElementById('recruitResume').value = nodeInfo.Resume || nodeInfo.resume || '';
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
            const resume = document.getElementById('recruitResume').value.trim() || '';
            const url = document.getElementById('recruitUrl').value.trim();

            const modelIdx = parseInt(document.getElementById('recruitModelIndex').value) || 0;

            if (!name) return alert("员工姓名不能为空！");
            renderEmployeeUI(currentTargetDesk, name, role);
            ensureOneEmptyDesk(); // 填坑完毕后，自动在末尾再加一个空桌子

            try {
                let res = await fetch('/api/config');
                let cfg = await res.json();
                if (!cfg.PeerNodes) cfg.PeerNodes = {};

                if (isEditing && originalEditName !== name && cfg.PeerNodes[originalEditName]) {
                    delete cfg.PeerNodes[originalEditName];
                }
                cfg.PeerNodes[name] = { Url: url, Role: role, Description: desc,Resume: resume, ModelIndex: modelIdx }; 
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
                    <img src="penzai2.png" class="desk-penzai">
                    <img src="penzai1.png" class="desk-penzai-2">
                </div>
            `;

            ensureOneEmptyDesk(); // 自动清理多余的空桌子并重新排版
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

    // 1. 原本的拼接逻辑不需要了，还原即可
    taskContent = `用户需求：${taskContent}`;
    closeModal('taskModal');

    const mIdx = window.teamConfig?.PeerNodes?.[currentTargetName]?.ModelIndex || 0;
    const bubble = currentTargetDesk.querySelector('.chat-bubble');
    if (bubble) { /* ... 保持不变 ... */ }

    // 2. 拿到中控存的公司制度
    const companySop = window.teamConfig?.CompanySOP || "";

    try {
        fetch('/api/chat', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'X-Username': encodeURIComponent(currentTargetName)
            },
            // 👇 3. 核心修改：在 JSON 里多传一个 sop 字段
            body: JSON.stringify({ 
                message: taskContent, 
                modelIndex: mIdx,
                sop: companySop  // <--- 新增这个字段
            })
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
