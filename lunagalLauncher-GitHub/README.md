# lunagalLauncher

lunagalLauncher：用于管理本地 llama-server、启动翻译器以及 Galgame 辅助应用，并包含鼠标映射等鼠标增强能力。  
manages your local llama-server, launches translation tools and Galgame helper apps, and adds mouse enhancements such as mouse remapping.

---

一个基于 WinUI 3 的 llama-server 服务管理工具

---

## 📋 项目状态

**当前版本**: 开发中  
**框架**: .NET 10.0 + WinUI 3  
**平台**: Windows 10/11 (x64)  
**最后更新**: 2026-02-05

---

## 🎯 当前任务

**待解决问题**: 下拉栏（Flyout）的位置和宽度不正确

- **宽度**: 固定 500px，应该与输入框一致
- **位置**: 显示在小按钮下方，应该显示在整个输入框下方

**预计解决时间**: 1-2 小时

---

## 🚀 快速开始

### 编译并运行
```powershell
cd c:\Users\fasdg\Desktop\c
.\quick_build.bat
```

### 仅编译
```powershell
"D:\Microsoft Visual Studio\Community\MSBuild\Current\Bin\MSBuild.exe" lunagalLauncher.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x64
```

### 仅运行
```powershell
Start-Process "bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\lunagalLauncher.exe"
```

---

## 📚 交接文档

**如果您是新接手这个项目，请从这里开始：**

### 🌟 推荐阅读顺序

1. **`交接文档索引.md`** ← 从这里开始！
   - 这是您的导航地图，会指引您找到所需的所有信息

2. **`快速参考卡片.md`**
   - 最关键的信息和命令，随时查阅

3. **`项目交接总览.md`**
   - 项目全貌和快速指引

4. **`项目交接文档.md`**
   - 完整的项目信息和背景

5. **`技术细节补充.md`**
   - 深度技术分析和调试技巧

### 📖 文档说明

| 文档 | 用途 | 阅读时间 |
|------|------|---------|
| 交接文档索引.md | 导航和快速查找 | 1分钟 |
| 快速参考卡片.md | 最常用的信息 | 5分钟 |
| 项目交接总览.md | 项目概览 | 10分钟 |
| 项目交接文档.md | 详细信息 | 30分钟 |
| 技术细节补充.md | 深度分析 | 20分钟 |

---

## 🏗️ 项目结构

```
lunagalLauncher/
├── Views/                      # 页面视图
│   ├── LlamaServicePage.xaml      # llama 服务管理页面
│   └── LlamaServicePage.xaml.cs
│
├── Utils/                      # 工具类
│   ├── FlyoutPressBehavior.cs     # Flyout 按压动画 ⭐ 需要修改
│   ├── CheckBoxDragSelectBehavior.cs
│   └── AnimationHelper.cs
│
├── Core/                       # 核心功能
│   ├── LlamaServiceManager.cs     # 服务管理
│   ├── GpuDetector.cs             # GPU 检测
│   └── ModelScanner.cs            # 模型扫描
│
├── 交接文档/                   # 项目交接文档
│   ├── 交接文档索引.md            # 从这里开始 ⭐
│   ├── 快速参考卡片.md
│   ├── 项目交接总览.md
│   ├── 项目交接文档.md
│   └── 技术细节补充.md
│
└── 构建脚本/
    ├── quick_build.bat            # 快速编译并运行
    └── cleanup_net8.bat           # 清理旧版本
```

---

## ✨ 核心功能

- ✅ llama-server 服务管理（启动、停止、重启）
- ✅ GPU 硬件检测和配置
- ✅ 模型文件管理和扫描
- ✅ 配置预设保存/加载
- ✅ 自定义命令行参数
- ✅ 复选框拖拽多选
- ⚠️ 下拉栏位置优化（待完成）

---

## 🔧 技术栈

- **框架**: .NET 10.0
- **UI**: WinUI 3
- **语言**: C# 12 + XAML
- **日志**: Serilog
- **构建**: MSBuild

---

## 📝 开发说明

### 环境要求
- Windows 10/11 (x64)
- Visual Studio 2022 Community
- .NET 10.0 SDK
- Windows SDK 10.0.19041.0

### 编译配置
- Configuration: Debug
- Platform: x64
- Target Framework: net10.0-windows10.0.19041.0

---

## 🐛 已知问题

1. **Flyout 位置不正确** ⭐ 当前任务
   - 优先级: 高
   - 影响: 用户体验
   - 状态: 待解决
   - 详情: 查看交接文档

---

## 📞 获取帮助

### 遇到问题？

1. **不知道从哪里开始**
   → 阅读 `交接文档索引.md`

2. **需要快速参考**
   → 查看 `快速参考卡片.md`

3. **编译失败**
   → 查看 `技术细节补充.md` 第六章

4. **需要深入了解代码**
   → 阅读 `技术细节补充.md` 第一章

---

## 🎯 下一步

### 如果您是新接手的开发者：

1. ✅ 阅读 `交接文档索引.md`（1分钟）
2. ✅ 阅读 `快速参考卡片.md`（5分钟）
3. ✅ 运行程序，观察问题（5分钟）
4. ✅ 修改 `Utils/FlyoutPressBehavior.cs`（30-60分钟）
5. ✅ 测试验证（10分钟）

### 推荐的解决方案

修改 `FlyoutPressBehavior.cs` 的 `ProcessClickAsync()` 方法，使用 `FlyoutShowOptions` 指定 Border 作为锚点。

详细步骤请查看 `快速参考卡片.md` 或 `技术细节补充.md`。

---

## 📄 许可证

[GPL-3.0](LICENSE)

---

## 👥 贡献者

（待添加）

---

## 📅 更新日志

### 2026-02-05
- ✅ 完成 .NET 8.0 → .NET 10.0 升级
- ✅ 实现 Flyout 按压动画
- ✅ 实现复选框拖拽多选
- ✅ 创建完整的项目交接文档
- ⚠️ 待解决：Flyout 位置问题

---

**开始您的开发之旅，请打开 `交接文档索引.md`！** 🚀
