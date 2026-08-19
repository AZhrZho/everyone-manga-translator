# EMT Studio

[English](README.md) | [简体中文](README.zh-CN.md)

<p align="center">
  <img src="assets/branding/emt-logo/svg/emt-logo-dark-transparent.svg" alt="EMT Studio" width="160">
</p>

**先读懂整部作品，再开始翻译。**

EMT Studio 是一款面向漫画翻译与嵌字的桌面工作台。它不只是把每个气泡中的对白逐句替换成另一种语言，而是可以先理解整本漫画中的人物、关系、称呼、术语、场景和剧情线索，再将这些全书上下文贯穿到后续翻译与审阅中。

从导入图片文件夹或 PDF，到清除原文、生成译文、智能排版、逐页审阅和导出成品，EMT Studio 将整个流程保存在一个可随时继续编辑的本地项目中。它适合希望借助 AI 提高效率，同时仍然掌握最终结果的翻译者、汉化团队和漫画创作者。

<p align="center">
  <img src="assets/screenshots/emt-studio-overview.png" alt="EMT Studio 翻译、嵌字与 Story Bible 工作界面" width="100%">
</p>

> [!WARNING]
> EMT Studio 仍在积极开发中。请备份原始素材和项目数据，并且只处理、传播你有权使用的内容。

> [!IMPORTANT]
> EMT Studio 目前主要由使用简体中文的开发者进行开发和测试。以其他语言作为目标语言的翻译与嵌字工作流尚未获得同等程度的验证，实际效果可能因语言而异，现阶段暂无法作出质量保证。欢迎其他语言的使用者提供反馈或参与改进。

## 功能亮点

### 先理解整本漫画，再开始翻译

在专家模式中，AI 会在正式逐页翻译前预检整部作品，将页面类型、章节与场景关系、角色、称呼、术语和剧情线索整理成可审阅的 **Story Bible（全书设定集）**，并为每一页生成对应提示。翻译每一页时都会参考这些共享上下文，从而减少人名、关键术语、语气和角色口吻前后不一致的问题。

### 让翻译与嵌字排版一起思考

多模态模型会同时参考原始漫画页面、检测到的文字区域、全书上下文以及可用字体样张，在生成译文时一并考虑字体、字号、字重、颜色、文字方向、字距、换行和文本框布局。这种联合处理方式可以让译文更自然地适应画面和气泡空间，而不是在翻译完成后再勉强塞入原有区域。

### 每一个阶段都可审阅、可修改

AI 生成的结果只是工作起点，而不是不可更改的最终答案。你可以决定哪些页面参与翻译，审阅和修订 Story Bible，修改译文，移动文本框，更换字体，修复清字底图，并在调整过程中保留不同的页面版本。

## 使用流程

1. 将图片文件夹或 PDF 导入为本地项目。
2. 为整本漫画选择**快速模式**或**专家模式**。
3. 使用专家模式时，先执行 AI 预检并审阅生成的全书上下文。
4. 逐页翻译，由多模态模型同时规划译文与嵌字排版。
5. 使用 CTD 或 CRAFT 在本地检测文字并完成清字。
6. 人工审阅译文、画面、字体和布局，并进行最终调整。
7. 将完成的漫画导出为图片、ZIP 压缩包或 PDF。

## 快速模式与专家模式

- **快速模式**直接逐页完成检测、翻译、清字和嵌字，适合短篇、试译，或更重视处理速度而不需要整书分析的任务。
- **专家模式**会先进行整书预检并生成可审阅的 Story Bible，再开始逐页翻译，更适合篇幅较长、重视角色口吻、称呼和术语一致性的作品。

## 项目状态

EMT Studio 仍在积极开发中，当前仓库主要面向开发者和早期测试者。核心项目流程已经具备，但功能和文件格式仍可能随着应用迭代而变化。

模型权重、API 凭据、有版权的漫画页面、生成的运行记录和测试产物均不会纳入源代码管理。部分真实模型冒烟测试为可选测试，需要开发者自行提供输入。

## 环境要求

- [.NET SDK 10.0.302 或更高版本](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Node.js 22 或更高版本](https://nodejs.org/)
- npm
- 用于 AI 预处理和翻译的模型服务 API 密钥
- 源码开发需要与所选文字检测引擎兼容的本地模型；Windows Release 构建会自动下载并校验模型

目前开发以 Windows 为主。.NET 类库已包含跨平台路径处理，但 Electron 桌面行为和原生依赖仍需在 Linux、macOS 上进行更多验证。

## 快速开始

克隆仓库后，恢复 .NET 与 JavaScript 依赖：

```powershell
dotnet restore .\MangaTranslator.slnx
npm ci --prefix .\apps\emt-client
npm --prefix .\apps\emt-desktop run bootstrap
```

启动 Electron 开发环境：

```powershell
npm --prefix .\apps\emt-desktop run dev
```

桌面宿主会在 `http://127.0.0.1:5198` 启动本地 API，并在 `http://127.0.0.1:5173` 启动 Vite 客户端。

也可以分别运行两个服务：

```powershell
# 终端 1
dotnet run --project .\apps\MangaTranslator.Api\MangaTranslator.Api.csproj --urls http://127.0.0.1:5198

# 终端 2
npm --prefix .\apps\emt-client run dev -- --host 127.0.0.1
```

## 配置

在应用中打开**设置**，添加服务提供方、接口地址、API 密钥与模型，然后为各工作流阶段指定模型。API 密钥通过 ASP.NET Core Data Protection 保存在本地应用数据目录中，禁止提交到仓库。

如果应用启动失败，反馈问题时请一并附上日志。Windows 下日志位于 `%APPDATA%\EMT Studio\logs\desktop.log` 和 `%APPDATA%\EMT Studio\logs\api.log`。

检测模型权重不会提交到本仓库。本地构建的 Windows 安装包会包含 Release 构建期间下载并通过校验的模型。使用源码开发时，请将模型安装到应用的本地模型目录，或设置以下环境变量：

```powershell
$env:MANGA_TRANSLATOR_CRAFT_MODEL = 'C:\path\to\craft.onnx'
$env:MANGA_TRANSLATOR_CTD_MODEL = 'C:\path\to\comictextdetector.pt.onnx'
```

模型接口、第三方归属和可选冒烟测试配置请参阅 [CRAFT 文档](packages/MangaTranslator.LocalDetection.Craft/README.md)与 [CTD 文档](packages/MangaTranslator.LocalDetection.Ctd/README.md)。

## Windows Release 构建

目前只生成 Windows x64 安装包。在 PowerShell 7 中运行：

```powershell
pwsh .\eng\Build-WindowsRelease.ps1
```

脚本会执行完整的干净发布流程：

1. 按锁文件恢复 npm 依赖和 .NET 包。
2. 运行 .NET、前端测试以及前端 lint。
3. 构建 React 客户端，并发布自包含的 `win-x64` API。
4. 从固定版本的模型制品仓库下载 CRAFT 与 CTD，同时校验文件长度和 SHA-256。
5. 使用 Electron Builder 生成 NSIS 安装包。

安装包输出到 `artifacts/windows/`。中间文件和模型权重只会出现在已忽略的 `build/`、`artifacts/` 目录中，禁止提交。经过审计的下载地址和校验值保存在 [`eng/release-models.json`](eng/release-models.json)。如需只下载并校验模型而不打包，可运行：

```powershell
pwsh .\eng\Download-ReleaseModels.ps1
```

CRAFT 官方仓库发布的是 PyTorch 权重，并未提供 EMT Studio 所需的 ONNX 图。因此清单会同时记录官方上游仓库，并单独固定兼容的 ONNX 转换制品仓库；CTD 则直接从其上游项目 Release 下载。

本地迭代时可使用 `-SkipTests`，使用 `-ForceModelDownload` 可忽略已有的已校验模型并重新下载。默认生成未签名安装包；如有 Windows 代码签名证书，可设置 Electron Builder 标准的 `CSC_LINK` 与 `CSC_KEY_PASSWORD` 环境变量。

## 仓库结构

```text
apps/      可运行的 ASP.NET Core、React 与 Electron 应用
packages/  可复用的预处理、翻译、检测、渲染与存储类库
tools/     字体管理与预处理命令行工具
tests/     确定性单元测试与可选的通用冒烟测试
eng/       带校验的 Release 构建与模型下载脚本
assets/    项目自有品牌资源
```

运行时数据不属于源代码基线。`.gitignore` 已排除本地漫画、模型权重、托管字体、生成的运行记录、构建输出和测试结果。请勿为了加入版权素材或敏感信息而削弱这些规则。

安装包内置 SIL OFL 1.1 授权的快乐体，作为简体中文默认字体。Windows
系统若已安装幼圆 `SIMYOU.TTF`，应用首次启动时会将其复制到用户自己的
字体库中；安装包不会重新分发幼圆。若系统没有幼圆，仍可使用快乐体，应用
会正常启动。

## 验证

提交变更前请运行 .NET 测试与前端检查：

```powershell
dotnet test .\MangaTranslator.slnx
npm --prefix .\apps\emt-client test
npm --prefix .\apps\emt-client run lint
npm --prefix .\apps\emt-client run build
```

调用外部服务或加载真实模型的测试，只有在提供文档所述环境变量时才会启用。

## 参与贡献

EMT Studio 仍在不断成长。仓库正式公开后，无论贡献大小，我们都诚挚欢迎。你可以报告能够稳定复现的问题、完善文档或界面翻译、测试更多目标语言、提出工作流改进建议，也可以直接提交功能修复或新特性。如果准备进行较大的改动，建议先创建 Issue 交流实现方向，避免重复工作。

为了让项目保持安全且便于审阅，请确保测试结果稳定、可重复，并只使用合成素材或允许再分发的测试夹具。请勿提交 API 凭据、模型权重或有版权的漫画页面。报告问题时，如果能够附上运行环境、操作流程和复现步骤，也会帮助大家更快地定位原因。

## 许可证

EMT Studio 是采用 [GNU Affero General Public License version 3](LICENSE) 发布的自由软件，SPDX 标识为 `AGPL-3.0-only`。你可以在该许可证条款下使用、研究、修改和再分发本软件。如果修改 EMT Studio 后通过网络向用户提供交互服务，AGPL 还要求按照许可证规定向这些用户提供对应源代码。以上仅为简要说明，实际权利和义务以 `LICENSE` 正文为准。

`MangaTranslator.LocalDetection.Ctd` 组件衍生自 `manga-image-translator`，并继续标记为 `GPL-3.0-only`。GPLv3 与 AGPLv3 包含针对组合程序的兼容条款，但分发者仍须保留适用的声明并遵守两份许可证的相关要求。发布前请阅读该组件的 [NOTICE](packages/MangaTranslator.LocalDetection.Ctd/NOTICE.md)。其他第三方声明保留在各自组件目录中。
