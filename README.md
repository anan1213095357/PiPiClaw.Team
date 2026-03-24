# 🦐 PiPiClaw.Team - 皮皮虾团队协作中控系统

<div align="center">

![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=for-the-badge&logo=dotnet)
![AOT](https://img.shields.io/badge/AOT-Compiled-8A2BE2?style=for-the-badge)
![License](https://img.shields.io/badge/License-MIT-green?style=for-the-badge)

**一个可视化的多 AI  Agent 协同工作管理平台**

![皮皮虾办公室](https://img.shields.io/badge/🦐-皮皮虾公司-orange?style=for-the-badge)

</div>

---

## 📖 项目简介

**PiPiClaw.Team** 是一个基于 .NET 10.0 开发的轻量级 AI 团队协作中控系统。它提供了一个生动有趣的"办公室"可视化界面，让您可以像管理真实员工一样管理多个 AI Agent 节点。

### ✨ 核心特性

| 特性 | 描述 |
|------|------|
| 🏢 **可视化办公室** | 以卡通工位形式展示每个 AI 员工的工作状态 |
| 📋 **任务派发** | 点击工位即可给指定 AI 员工派发任务 |
| 💬 **流式对话** | 支持实时流式输出，边思考边展示 |
| 📊 **状态监控** | 2 秒轮询刷新，实时显示员工工作状态 |
| 📝 **工作报告** | 任务完成后可查看完整的工作报告 |
| 🧹 **记忆管理** | 支持清空单个或全部员工的上下文记忆 |
| 👥 **员工管理** | 招募新员工、修改信息、开除员工 |
| 🔌 **节点代理** | 自动转发请求到对应的 AI 节点后端 |

---

## 🖼️ 界面预览

```
<img src="./cde9a3e6-c02c-415d-84ef-487ed55accfc.png" alt="PiPiClaw 多模型设置（OpenAI 协议兼容，支持多家 LLM）" width="49%" />

```

---

## 🚀 快速开始

### 环境要求

- .NET 10.0 SDK 或更高版本
- Windows 10/11 操作系统
- 一个或多个 AI Agent 节点（皮皮虾节点）

### 编译运行

```powershell
# 1. 克隆项目
git clone https://github.com/anan1213095357/PiPiClaw.Team.git
cd PiPiClaw.Team

# 2. 发布 AOT 编译版本（可选，获得更小的体积和更快的启动）
dotnet publish -c Release -r win-x64

# 3. 直接运行
dotnet run

# 或以管理员身份运行（如果需要绑定特定端口）
Start-Process powershell -Verb RunAs -ArgumentList "dotnet run"
```

### 访问界面

启动后，在浏览器中打开：
```
http://localhost:4050/
```

---

## 📁 项目结构

```
PiPiClaw.Team/
├── Program.cs              # 主程序入口，包含 HTTP 服务器和前端 HTML
├── PiPiClaw.Team.csproj    # 项目配置文件
├── team_config.json        # 员工通讯录配置文件（运行时生成）
├── img_shrimp_working.png  # 皮皮虾工作图片（嵌入资源）
├── img_empty_desk.png      # 空桌子图片（嵌入资源）
├── README.md               # 项目说明文档
├── Properties/
│   └── launchSettings.json # 启动配置
├── bin/                    # 编译输出目录
└── obj/                    # 临时对象文件
```

---

## 🔧 配置说明

### 员工通讯录 (team_config.json)

系统会自动生成并维护 `team_config.json` 配置文件，格式如下：

```json
{
  "PeerNodes": {
    "海应": {
      "url": "http://localhost:5050",
      "role": "前端工程师"
    },
    "狗蛋": {
      "url": "http://localhost:5051",
      "role": "后端工程师"
    },
    "铁柱": {
      "url": "http://192.168.1.100:5050",
      "role": "测试工程师"
    }
  }
}
```

| 字段 | 类型 | 说明 |
|------|------|------|
| `PeerNodes` | Object | 员工字典，Key 为员工姓名 |
| `url` | String | 该员工对应的 AI 节点服务地址 |
| `role` | String | 员工的岗位/角色描述 |

---

## 🎯 使用指南

### 1. 招募新员工

1. 点击任意**空工位**或**离线工位**
2. 在弹窗中填写：
   - **员工姓名**：如 "海应"
   - **岗位头衔**：如 "前端工程师"
   - **节点 URL**：如 "http://localhost:5050"
3. 点击"办理入职"

### 2. 派发任务

1. 点击**在岗员工**的工位
2. 在任务输入框中输入具体需求
3. 点击"开始干活"
4. 观察气泡状态变化：
   - 🔵 **思考中**：员工正在处理任务
   - 🟠 **已完成**：任务完成，点击查看报告

### 3. 查看工作报告

- 点击**橙色气泡**或**📄 最新报告**按钮
- 查看 Markdown 格式的工作总结

### 4. 员工管理

| 操作 | 说明 |
|------|------|
| 📝 修改信息 | 更改员工姓名、角色或节点地址 |
| 🧹 清空记忆 | 清除该员工的上下文对话历史 |
| 🔥 开除员工 | 从通讯录中移除，释放工位 |

### 5. 全局操作

- **🧹 一键清空所有员工记忆**：批量清除所有在职员工的上下文

---

## 🌐 API 接口

中控系统提供以下 HTTP API：

| 接口 | 方法 | 说明 |
|------|------|------|
| `/` | GET | 返回前端 HTML 页面 |
| `/api/config` | GET | 获取员工通讯录配置 |
| `/api/config` | POST | 更新员工通讯录配置 |
| `/api/chat` | POST | 派发任务（流式响应） |
| `/api/status` | GET | 查询员工工作状态 |
| `/api/history` | GET | 获取员工对话历史/报告 |
| `/api/clear` | POST | 清空单个员工记忆 |
| `/api/clearall` | POST | 清空所有员工记忆 |

---

## 🏗️ 技术架构

```
┌─────────────────────────────────────────────────────────────┐
│                      浏览器 (用户界面)                        │
│                   http://localhost:4050                     │
└─────────────────────────┬───────────────────────────────────┘
                          │ HTTP 请求
                          ▼
┌─────────────────────────────────────────────────────────────┐
│              PiPiClaw.Team 中控服务器                         │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐ │
│  │  静态文件服务 │  │  配置管理    │  │   请求代理转发      │ │
│  │  (HTML/CSS) │  │ (team_config)│  │  (HTTP Client)     │ │
│  └─────────────┘  └─────────────┘  └─────────────────────┘ │
└─────────────────────────┬───────────────────────────────────┘
                          │ 转发请求
                          ▼
┌─────────────────────────────────────────────────────────────┐
│                    AI Agent 节点集群                          │
│  ┌───────────┐  ┌───────────┐  ┌───────────┐  ┌──────────┐ │
│  │  海应节点  │  │  狗蛋节点  │  │  铁柱节点  │  │  ...    │ │
│  │ :5050     │  │ :5051     │  │ :5052     │  │          │ │
│  └───────────┘  └───────────┘  └───────────┘  └──────────┘ │
└─────────────────────────────────────────────────────────────┘
```

---

## 🔒 安全说明

- 中控服务默认监听 `localhost:4050`，仅本地访问
- 如需局域网访问，请修改 `Program.cs` 中的监听地址
- 跨电脑访问时请确保防火墙允许对应端口
- 建议在生产环境中添加身份验证机制

---

## 📄 许可证

本项目采用 **MIT License** 开源协议

---

## 🤝 贡献指南

欢迎提交 Issue 和 Pull Request！

1. Fork 本项目
2. 创建特性分支 (`git checkout -b feature/AmazingFeature`)
3. 提交更改 (`git commit -m 'Add some AmazingFeature'`)
4. 推送到分支 (`git push origin feature/AmazingFeature`)
5. 开启 Pull Request

---

## 📬 联系方式

- **GitHub**: [anan1213095357](https://github.com/anan1213095357)
- **项目仓库**: [PiPiClaw.Team](https://github.com/anan1213095357/PiPiClaw.Team)

---

<div align="center">

**🦐 皮皮虾公司 · 让 AI 协作更有趣！**

*Made with ❤️ using .NET 10.0*

</div>
