# EMT Client

EMT Studio 的 React + TypeScript 前端。入口文件只负责应用壳层和顶层对话框；业务代码按功能域拆分，避免重新形成单文件应用。

## 目录约定

```text
src/
  features/
    editor/model/           纯编辑文档模型与序列化
    jobs/                   任务时间线与任务中心
    precheck/               预处理运行和审阅
    projects/               项目入口、项目设置与表单模型
    settings/               应用设置
    translation/            翻译、预览、底图编辑与文本布局
    workspace/              项目级工作流协调
  shared/
    lib/                    无业务归属的轻量工具
    types/                  跨功能域 UI 类型
    ui/                     可复用展示组件
    viewport/               画布视口行为
```

## 依赖方向

- `App.tsx` 可以组合 feature 和 shared 模块，但不承载业务编辑逻辑。
- feature 可以依赖 shared、API/contract 基础设施及本功能域的 model。
- shared 不依赖 feature。
- 编辑器的可测试数据变换放在 `features/editor/model`，React 组件不复制这些规则。
- 仅在多个功能域确实共享时才提升到 shared，避免建立巨型公共模块或全局项目状态 Context。

## 工作流约束

- 预处理、选页和翻译阶段通过 `hidden` 保持挂载，不能用条件渲染替代。
- 切换项目、阶段、页面或编辑模式前必须 flush 当前编辑。
- 所有写 API 共用项目 revision 语义；布局和底图编辑继续使用 650 ms 自动保存防抖。
- 页面级异步请求必须在切页/切模式时失效，不能让旧响应覆盖新页面。
- Object URL、文本片段请求序列和未完成渲染需要在切页或卸载时清理。
- 为保持视觉兼容，重构组件时优先保留现有 JSX 层级与 className；全局样式仍由 `App.css` 提供。

## 验证

```powershell
npm run lint
npm run build
```

涉及工作流、画布或设置页面的修改还应在桌面壳层中回归阶段切换、页面切换、预览/底图/文字模式以及任务中心。
